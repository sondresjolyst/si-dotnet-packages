using System.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace SjolystInnovation.AppKit.Auth;

/// <summary>Removes an app's own data for a user inside the account deletion transaction.</summary>
public interface IAccountDataEraser<in TUser>
{
    Task EraseAsync(TUser user, CancellationToken cancellationToken);
}

/// <summary>Runs once an account deletion has committed, such as to disconnect the user's live connections.</summary>
public interface IAccountDeletedHandler<in TUser>
{
    Task OnDeletedAsync(TUser user, CancellationToken cancellationToken);
}

/// <summary>Deletes accounts. The user row stays so records that point at it, such as invoices, keep it.</summary>
public interface IAccountDeleter<in TUser>
{
    /// <summary>
    /// In one transaction: runs every <see cref="IAccountDataEraser{TUser}"/>, scrubs the user's personal
    /// data, locks the account for good, rotates its security stamp and deletes its refresh tokens. Then
    /// runs every <see cref="IAccountDeletedHandler{TUser}"/>. Throws <see cref="LastAdminException"/>
    /// for the last active admin, and rolls everything back when a step fails.
    /// </summary>
    Task DeleteAsync(TUser user, CancellationToken cancellationToken = default);
}

/// <summary>The account is the last active admin, so deleting it would leave nobody to administer the app.</summary>
public sealed class LastAdminException() : InvalidOperationException("The last admin account cannot be deleted. Give someone else the admin role first.");

/// <summary>Another change to the accounts collided with this deletion, which was rolled back. Trying again may succeed.</summary>
public sealed class AccountChangeConflictException(Exception inner)
    : InvalidOperationException("Another change to the accounts happened at the same time. Try again.", inner);

public sealed class AppKitAccountOptions
{
    /// <summary>The role that must keep an active holder. <c>Admin</c> by default.</summary>
    public string AdminRole { get; set; } = "Admin";
}

public static class AccountServiceCollectionExtensions
{
    /// <summary>Registers <see cref="IAccountDeleter{TUser}"/>. Register erasers and handlers as scoped services.</summary>
    public static IServiceCollection AddAppKitAccounts<TUser, TContext>(this IServiceCollection services, Action<AppKitAccountOptions>? configure = null)
        where TUser : IdentityUser, IAppKitAccount
        where TContext : DbContext, IAuthDbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<AppKitAccountOptions>().Configure(o => configure?.Invoke(o));
        services.AddMemoryCache();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<IAccountDeleter<TUser>, AccountDeleter<TUser, TContext>>();
        return services;
    }
}

internal sealed partial class AccountDeleter<TUser, TContext>(
    TContext db,
    UserManager<TUser> users,
    IEnumerable<IAccountDataEraser<TUser>> erasers,
    IEnumerable<IAccountDeletedHandler<TUser>> handlers,
    Microsoft.Extensions.Options.IOptions<AppKitAccountOptions> options,
    IMemoryCache cache,
    TimeProvider time,
    ILogger<AccountDeleter<TUser, TContext>> logger) : IAccountDeleter<TUser>
    where TUser : IdentityUser, IAppKitAccount
    where TContext : DbContext, IAuthDbContext
{
    public async Task DeleteAsync(TUser user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        var adminRole = options.Value.AdminRole;
        try
        {
            await DeleteInTransactionAsync(user, adminRole, cancellationToken);
        }
        catch (Exception e) when (IsSerializationFailure(e))
        {
            throw new AccountChangeConflictException(e);
        }

        // The bearer check forgets the user, so a token still in use stops at once on this instance.
        cache.Remove(IdentityAndBearerExtensions.UserCheckKey(user.Id));
        Log.Deleted(logger, user.Id);
        foreach (var handler in handlers)
            await handler.OnDeletedAsync(user, cancellationToken);
    }

    // SQLSTATE 40001 is a serialization failure, the database's answer when two transactions collide.
    internal static bool IsSerializationFailure(Exception? e)
    {
        for (; e is not null; e = e.InnerException)
        {
            if (e is System.Data.Common.DbException { SqlState: "40001" })
                return true;
        }
        return false;
    }

    private async Task DeleteInTransactionAsync(TUser user, string adminRole, CancellationToken cancellationToken)
    {
        // Serializable, with the admin check inside, so two admins deleting each other at once cannot
        // both pass the check. The database aborts one of them instead.
        await using (var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken))
        {
            if (await users.IsInRoleAsync(user, adminRole)
                && (await users.GetUsersInRoleAsync(adminRole)).Count(a => !a.IsDeleted) <= 1)
                throw new LastAdminException();

            foreach (var eraser in erasers)
                await eraser.EraseAsync(user, cancellationToken);

            Scrub(user, time.GetUtcNow().UtcDateTime);
            // RemoveRange rather than a bulk delete, so it works on every EF provider, in-memory included.
            db.RefreshTokens.RemoveRange(await db.RefreshTokens.Where(t => t.UserId == user.Id).ToListAsync(cancellationToken));
            await db.SaveChangesAsync(cancellationToken);

            // Rotating the stamp also saves the scrubbed user through Identity's validation.
            var result = await users.UpdateSecurityStampAsync(user);
            if (!result.Succeeded)
                throw new InvalidOperationException("Deleting the account failed: " + string.Join(" ", result.Errors.Select(e => e.Description)));
            await tx.CommitAsync(cancellationToken);
        }
    }

    internal static void Scrub(TUser user, DateTime now)
    {
        user.FirstName = "Deleted";
        user.LastName = "User";
        // A placeholder rather than null, since Identity requires a unique email. The .invalid domain never routes.
        // Identity fills in the normalized forms when it saves the user.
        user.Email = $"deleted-{user.Id}@deleted.invalid";
        user.UserName = $"deleted-{user.Id}";
        user.EmailConfirmed = false;
        user.PhoneNumber = null;
        user.PhoneNumberConfirmed = false;
        user.TwoFactorEnabled = false;
        user.PasswordHash = null;
        user.LockoutEnabled = true;
        user.LockoutEnd = DateTimeOffset.MaxValue;
        user.AccessFailedCount = 0;
        if (user is IPasswordResetUser reset)
        {
            reset.PasswordResetCodeHash = null;
            reset.PasswordResetCodeExpiration = null;
            reset.PasswordResetAttempts = 0;
        }
        if (user is IEmailVerificationUser verification)
        {
            verification.EmailVerificationCodeHash = null;
            verification.EmailVerificationCodeExpiration = null;
            verification.EmailVerificationAttempts = 0;
        }
        user.IsDeleted = true;
        user.DeletedAt = now;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Account {UserId} deleted.")]
        public static partial void Deleted(ILogger logger, string userId);
    }
}
