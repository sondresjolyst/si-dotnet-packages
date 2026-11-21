using System.Linq.Expressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SjolystInnovation.AppKit.Auth;

/// <summary>The user field that records the last removal reminder, so each reminder goes out once.</summary>
public interface IRemovalReminderUser
{
    /// <summary>When the last removal reminder was sent, in UTC, or null if none was sent.</summary>
    DateTime? RemovalReminderSentAt { get; set; }
}

/// <summary>What one run of the unverified account cleanup did.</summary>
/// <param name="Removed">How many accounts were removed.</param>
/// <param name="Reminded">How many removal reminders were sent.</param>
/// <param name="Failed">How many accounts failed and were left as they were.</param>
/// <param name="Skipped">How many accounts were left alone because another host removed or changed them at the same moment.</param>
public sealed record UnverifiedCleanupResult(int Removed, int Reminded, int Failed, int Skipped = 0);

/// <summary>Removes accounts that came through the sign-up and never verified their email, and reminds their owners first.</summary>
public interface IUnverifiedAccountCleanup
{
    /// <summary>
    /// Runs the cleanup once. It removes every active account that holds a consent row with the source <c>register</c> and whose email is still unconfirmed when it is older than
    /// <see cref="AppKitRegistrationOptions{TUser}.UnverifiedLifetime"/>, each in its own transaction, after every
    /// <see cref="IAccountDataEraser{TUser}"/> has run. The user is deleted for real, so its refresh tokens and consents
    /// go with it and the email is free again. An account with a live verification code is kept until the code has
    /// expired, but never more than one day past its removal time. Then it sends the reminders of
    /// <see cref="AppKitRegistrationOptions{TUser}.RemindBefore"/> that are due, once each, when the user type
    /// implements <see cref="IRemovalReminderUser"/>. A reminder carries a fresh code, counts toward
    /// <see cref="AppKitRegistrationOptions{TUser}.MaxOwnerEmailsPerAddressPerDay"/> and waits for a later run when the address is over it. An account that is locked after
    /// too many wrong codes is reminded in a later run. One failing account is logged and does not stop the run. An account that
    /// another host removed or changed at the same moment is counted as skipped and logged at debug level, not as a failure.
    /// The run also removes the sign-up email counts from before yesterday.
    /// </summary>
    Task<UnverifiedCleanupResult> RunOnceAsync(CancellationToken cancellationToken = default);
}

internal enum CleanupOutcome
{
    Removed,
    Reminded,
    Skipped,

    // Another host removed or changed the account at the same moment.
    Lost,
}

internal sealed partial class UnverifiedAccountCleanup<TUser, TContext>(
    IServiceProvider services, IOptions<AppKitRegistrationOptions<TUser>> options, TimeProvider time, ILogger<UnverifiedAccountCleanup<TUser, TContext>> logger)
    : IUnverifiedAccountCleanup
    where TUser : IdentityUser, IAppKitAccount, IEmailVerificationUser
    where TContext : DbContext, IAccountConsentDbContext
{
    // An owner who asks for codes keeps the account for at most this long after its removal time.
    internal static readonly TimeSpan Grace = TimeSpan.FromDays(1);

    // The consent source that the sign-up writes. Only an account with such a row came through the sign-up.
    private const string SignUpSource = "register";

    private readonly bool _canRemind = CheckReminders(options.Value, logger);

    private static bool CheckReminders(AppKitRegistrationOptions<TUser> settings, ILogger logger)
    {
        if (settings.RemindBefore.Count == 0)
            return false;
        if (typeof(IRemovalReminderUser).IsAssignableFrom(typeof(TUser)))
            return true;
        Log.RemindersUnavailable(logger, typeof(TUser).Name);
        return false;
    }

    public async Task<UnverifiedCleanupResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        var now = time.GetUtcNow().UtcDateTime;
        int removed = 0, reminded = 0, failed = 0, skipped = 0;

        var removalCutoff = now - settings.UnverifiedLifetime;
        foreach (var id in await IdsAsync(u => u.CreatedAt < removalCutoff, cancellationToken))
        {
            try
            {
                switch (await RemoveAsync(id, cancellationToken))
                {
                    case CleanupOutcome.Removed:
                        removed++;
                        break;
                    case CleanupOutcome.Lost:
                        skipped++;
                        Log.LostRace(logger, id);
                        break;
                }
            }
            catch (DbUpdateConcurrencyException)
            {
                skipped++;
                Log.LostRace(logger, id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                Log.RemoveFailed(logger, ex, id);
            }
        }

        if (_canRemind)
        {
            var until = removalCutoff + settings.RemindBefore.Max();
            foreach (var id in await IdsAsync(u => u.CreatedAt >= removalCutoff && u.CreatedAt <= until, cancellationToken))
            {
                try
                {
                    switch (await RemindAsync(id, cancellationToken))
                    {
                        case CleanupOutcome.Reminded:
                            reminded++;
                            break;
                        case CleanupOutcome.Lost:
                            skipped++;
                            Log.LostRace(logger, id);
                            break;
                    }
                }
                catch (DbUpdateConcurrencyException)
                {
                    skipped++;
                    Log.LostRace(logger, id);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    Log.ReminderFailed(logger, ex, id);
                }
            }
        }

        using (var scope = services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<SignUpMailLimiter<TContext>>().RemoveOldAsync(cancellationToken);

        if (removed + reminded + failed + skipped > 0)
            Log.Ran(logger, removed, reminded, failed, skipped);
        return new UnverifiedCleanupResult(removed, reminded, failed, skipped);
    }

    // The ids of the active accounts with an unconfirmed email that match the filter. Each account is loaded again later.
    private async Task<List<string>> IdsAsync(Expression<Func<TUser, bool>> filter, CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        return await db.Set<TUser>().AsNoTracking().Where(u => !u.EmailConfirmed && !u.IsDeleted && db.AccountConsents.Any(c => c.UserId == u.Id && c.Source == SignUpSource)).Where(filter).Select(u => u.Id).ToListAsync(cancellationToken);
    }

    // Loads the account again inside its own transaction. It is skipped when it is now confirmed or deleted, when it is not
    // old enough after all, or when its owner holds a live code and the grace period has not passed. Otherwise every eraser
    // runs and the user is deleted for real.
    internal async Task<CleanupOutcome> RemoveAsync(string userId, CancellationToken cancellationToken)
    {
        var lifetime = options.Value.UnverifiedLifetime;
        var now = time.GetUtcNow().UtcDateTime;
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<TUser>>();

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var found = await users.FindByIdAsync(userId);
        // The id came from a list made a moment ago, so an account that is gone now was removed by another host.
        if (found is null)
            return CleanupOutcome.Lost;
        if (found is not { EmailConfirmed: false, IsDeleted: false } user || user.CreatedAt >= now - lifetime
            || !await CameThroughSignUpAsync(db, userId, cancellationToken))
            return CleanupOutcome.Skipped;
        if (user.EmailVerificationCodeExpiration > now && user.CreatedAt >= now - lifetime - Grace)
            return CleanupOutcome.Skipped;

        foreach (var eraser in scope.ServiceProvider.GetServices<IAccountDataEraser<TUser>>())
            await eraser.EraseAsync(user, cancellationToken);
        var deleted = await users.DeleteAsync(user);
        if (deleted.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.ConcurrencyFailure)))
            return CleanupOutcome.Lost;
        if (!deleted.Succeeded)
            throw new InvalidOperationException("Removing the unverified account failed: " + string.Join(" ", deleted.Errors.Select(e => e.Code)));
        await tx.CommitAsync(cancellationToken);

        // Drop the user from the bearer check cache, so a token still in use stops working at once on this instance.
        scope.ServiceProvider.GetService<IMemoryCache>()?.Remove(IdentityAndBearerExtensions.UserCheckKey(userId));
        Log.Removed(logger, userId);
        return CleanupOutcome.Removed;
    }

    // Sends the reminder that is due, if there is one. The latest due offset counts, so a run after a long stop sends one
    // reminder and not one per missed offset. The fresh code is saved first, then the email is reserved and sent, and the reminder
    // is only marked as sent after the email went out, so a failed send is tried again in the next run.
    internal async Task<CleanupOutcome> RemindAsync(string userId, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = time.GetUtcNow().UtcDateTime;
        using var scope = services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<TUser>>();

        if (await users.FindByIdAsync(userId) is not { EmailConfirmed: false, IsDeleted: false } user || user is not IRemovalReminderUser reminder
            || !await CameThroughSignUpAsync(scope.ServiceProvider.GetRequiredService<TContext>(), userId, cancellationToken))
            return CleanupOutcome.Skipped;
        var removalAt = user.CreatedAt + settings.UnverifiedLifetime;
        if (removalAt <= now)
            return CleanupOutcome.Skipped;
        var due = settings.RemindBefore.Select(before => removalAt - before).Where(at => at <= now).DefaultIfEmpty(DateTime.MinValue).Max();
        if (due == DateTime.MinValue || reminder.RemovalReminderSentAt >= due)
            return CleanupOutcome.Skipped;
        if (VerificationThrottle.CodeIsDue(user, now, settings.ResendCooldown) != CodeDue.Yes)
            return CleanupOutcome.Skipped;

        // The fresh code is saved first. An account that another host changed at the same moment is left to a later run and nothing is spent.
        var code = EmailVerificationCodes.Issue(user, time);
        var saved = await users.UpdateAsync(user);
        if (!saved.Succeeded)
        {
            if (saved.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.ConcurrencyFailure)))
                return CleanupOutcome.Lost;
            throw new InvalidOperationException("Saving the reminder code failed: " + string.Join(" ", saved.Errors.Select(e => e.Code)));
        }

        // Then the email is reserved. Over the cap of the address no email goes out, and the reminder is tried again in a later run.
        if (!await scope.ServiceProvider.GetRequiredService<SignUpMailLimiter<TContext>>().TryReserveAsync(user.Email!, user.NormalizedEmail, SignUpMailKinds.Reminder, settings.MaxEmailsPerAddressPerDay, settings.MaxOwnerEmailsPerAddressPerDay, TimeSpan.Zero, cancellationToken))
            return CleanupOutcome.Skipped;
        var locale = await scope.ServiceProvider.GetRequiredService<AccountConsentService<TContext>>().LatestLocaleAsync(userId, cancellationToken);
        await scope.ServiceProvider.GetRequiredService<IRegistrationEmails<TUser>>().SendRemovalReminderAsync(user, code, removalAt, locale, cancellationToken);

        reminder.RemovalReminderSentAt = now;
        var marked = await users.UpdateAsync(user);
        if (!marked.Succeeded)
            throw new InvalidOperationException("Saving the reminder time failed: " + string.Join(" ", marked.Errors.Select(e => e.Code)));
        Log.Reminded(logger, userId);
        return CleanupOutcome.Reminded;
    }

    private static Task<bool> CameThroughSignUpAsync(TContext db, string userId, CancellationToken cancellationToken) =>
        db.AccountConsents.AsNoTracking().AnyAsync(c => c.UserId == userId && c.Source == SignUpSource, cancellationToken);

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Removed unverified account {UserId}.")]
        public static partial void Removed(ILogger logger, string userId);

        [LoggerMessage(Level = LogLevel.Information, Message = "Sent a removal reminder to unverified account {UserId}.")]
        public static partial void Reminded(ILogger logger, string userId);

        [LoggerMessage(Level = LogLevel.Error, Message = "Removing unverified account {UserId} failed.")]
        public static partial void RemoveFailed(ILogger logger, Exception exception, string userId);

        [LoggerMessage(Level = LogLevel.Error, Message = "Sending the removal reminder to unverified account {UserId} failed.")]
        public static partial void ReminderFailed(ILogger logger, Exception exception, string userId);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Skipped unverified account {UserId}, because another host removed or changed it at the same moment.")]
        public static partial void LostRace(ILogger logger, string userId);

        [LoggerMessage(Level = LogLevel.Information, Message = "Unverified account cleanup removed {Removed}, reminded {Reminded}, failed {Failed} and skipped {Skipped}.")]
        public static partial void Ran(ILogger logger, int removed, int reminded, int failed, int skipped);

        [LoggerMessage(Level = LogLevel.Warning, Message = "RemindBefore is set, but {UserType} does not implement IRemovalReminderUser, so no removal reminders are sent. Add the interface and a column for it to send them.")]
        public static partial void RemindersUnavailable(ILogger logger, string userType);
    }
}

// Runs the cleanup when the app starts and once a day after that, unless the app switched the hosted run off.
internal sealed partial class UnverifiedAccountCleanupService<TUser>(
    IUnverifiedAccountCleanup cleanup, IOptions<AppKitRegistrationOptions<TUser>> options, TimeProvider time, ILogger<UnverifiedAccountCleanupService<TUser>> logger)
    : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RunCleanupInBackground)
            return;
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                await cleanup.RunOnceAsync(stoppingToken);
            }
            // Log a failed run and try again on the next tick.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Failed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Error, Message = "The unverified account cleanup failed.")]
        public static partial void Failed(ILogger logger, Exception exception);
    }
}
