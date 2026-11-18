using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SjolystInnovation.AppKit.Auth;

internal sealed record VerifyEmailRequest(string? Code);

/// <summary>What a request for a new verification code may do right now.</summary>
internal enum CodeDue
{
    /// <summary>A new code may be issued.</summary>
    Yes,

    /// <summary>The last code was issued less than the cooldown ago.</summary>
    Cooldown,

    /// <summary>Five wrong codes were tried and the current code has not expired yet.</summary>
    Locked,
}

/// <summary>The rules that keep verification emails from flooding a mailbox and codes from being guessed without limit. Register and resend share them.</summary>
internal static class VerificationThrottle
{
    /// <summary>
    /// Decides whether a new code may be issued. The time of the last issue is the expiry minus the code lifetime,
    /// which is exact because every code lives for <see cref="EmailVerificationCodes.Lifetime"/>. After too many wrong
    /// attempts no new code is issued until the current one has expired, so a guesser gets five tries per lifetime.
    /// </summary>
    public static CodeDue CodeIsDue(IEmailVerificationUser user, DateTime now, TimeSpan cooldown)
    {
        if (user.EmailVerificationCodeExpiration is not { } expires)
            return CodeDue.Yes;
        if (user.EmailVerificationAttempts >= VerificationCodes.MaxAttempts && expires > now)
            return CodeDue.Locked;
        return now - (expires - EmailVerificationCodes.Lifetime) >= cooldown ? CodeDue.Yes : CodeDue.Cooldown;
    }

    /// <summary>
    /// Decides whether the owner of an existing account may be told that someone tried to sign up, and notes the time when
    /// so. The time is kept in memory, so on several instances each may send one notice per cooldown.
    /// </summary>
    public static bool NoticeIsDue(IMemoryCache cache, string userId, DateTime now, TimeSpan cooldown)
    {
        if (cooldown <= TimeSpan.Zero)
            return true;
        var key = $"SjolystInnovation.AppKit.Auth:already-registered-notice:{userId}";
        if (cache.TryGetValue(key, out DateTime last) && now - last < cooldown)
            return false;
        cache.Set(key, now, cooldown);
        return true;
    }
}

internal sealed class VerificationHandlers<TUser, TContext>(ILogger logger)
    where TUser : IdentityUser, IEmailVerificationUser
    where TContext : DbContext, IAccountConsentDbContext
{
    private static T Get<T>(HttpContext http) where T : notnull => http.RequestServices.GetRequiredService<T>();

    // The account the bearer token belongs to, or null when it is gone or not active.
    private static async Task<TUser?> CurrentAsync(HttpContext http)
    {
        var id = http.User.GetUserId();
        if (id is null)
            return null;
        var user = await Get<UserManager<TUser>>(http).FindByIdAsync(id);
        return user is null or IAppKitAccount { IsDeleted: true } ? null : user;
    }

    public async Task<IResult> VerifyAsync(VerifyEmailRequest request, HttpContext http)
    {
        var users = Get<UserManager<TUser>>(http);
        var cancellationToken = http.RequestAborted;
        var code = request.Code?.Trim();
        if (string.IsNullOrEmpty(code))
        {
            var errors = new AuthFieldErrors();
            errors.Required("Code");
            return errors.ToResult();
        }
        if (await CurrentAsync(http) is not { } user)
            return TypedResults.Unauthorized();

        switch (EmailVerificationCodes.Check(user, code, Get<TimeProvider>(http)))
        {
            case CodeCheck.AlreadyVerified:
                return AuthResults.Error(AuthErrorCodes.AlreadyVerified, "The email address is already verified.", StatusCodes.Status409Conflict);
            case CodeCheck.TooManyAttempts:
                return AuthResults.Error(AuthErrorCodes.TooManyAttempts, "Too many attempts. Wait until the code has expired, then request a new one.", StatusCodes.Status429TooManyRequests);
            case CodeCheck.Invalid:
                await users.UpdateAsync(user);
                return AuthResults.Error(AuthErrorCodes.InvalidCode, "Invalid or expired verification code.", StatusCodes.Status400BadRequest);
        }

        var db = Get<TContext>(http);
        await using (var tx = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            EmailVerificationCodes.Complete(user);
            // Rotating the stamp also saves the confirmed user. It ends the access tokens of an opted-in app.
            var rotated = await users.UpdateSecurityStampAsync(user);
            if (!rotated.Succeeded)
            {
                await tx.RollbackAsync(cancellationToken);
                // Another request confirmed the same account first.
                if (rotated.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.ConcurrencyFailure)))
                    return AuthResults.Error(AuthErrorCodes.AlreadyVerified, "The email address is already verified.", StatusCodes.Status409Conflict);
                throw new InvalidOperationException("Verifying the email failed: " + string.Join(" ", rotated.Errors.Select(e => e.Description)));
            }
            await Get<IRefreshTokenService>(http).RevokeAllForUserAsync(user.Id, cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }

        // Drop the user from the bearer check cache, so the earlier access tokens stop working at once on this instance.
        http.RequestServices.GetService<IMemoryCache>()?.Remove(IdentityAndBearerExtensions.UserCheckKey(user.Id));
        AuthEndpointsExtensions.Log.EmailVerified(logger, user.Id);
        foreach (var handler in http.RequestServices.GetServices<IEmailVerifiedHandler<TUser>>())
            await handler.OnEmailVerifiedAsync(user, cancellationToken);
        return TypedResults.Ok(new MessageResponse("Your email address is verified. Sign in again to continue."));
    }

    public async Task<IResult> ResendAsync(HttpContext http)
    {
        if (await CurrentAsync(http) is not { } user)
            return TypedResults.Unauthorized();
        if (user.EmailConfirmed)
            return AuthResults.Error(AuthErrorCodes.AlreadyVerified, "The email address is already verified.", StatusCodes.Status409Conflict);

        var time = Get<TimeProvider>(http);
        var due = VerificationThrottle.CodeIsDue(user, time.GetUtcNow().UtcDateTime, Get<IOptions<AppKitRegistrationOptions<TUser>>>(http).Value.ResendCooldown);
        if (due == CodeDue.Locked)
            return AuthResults.Error(AuthErrorCodes.TooManyAttempts, "Too many attempts. Wait until the code has expired, then request a new one.", StatusCodes.Status429TooManyRequests);
        if (due == CodeDue.Cooldown)
            return AuthResults.Error(AuthErrorCodes.TooManyRequests, "A code was sent a moment ago. Wait a little before asking for another.", StatusCodes.Status429TooManyRequests);

        var code = EmailVerificationCodes.Issue(user, time);
        var updated = await Get<UserManager<TUser>>(http).UpdateAsync(user);
        if (!updated.Succeeded)
            throw new InvalidOperationException("Saving the new verification code failed: " + string.Join(" ", updated.Errors.Select(e => e.Description)));
        await Get<IRegistrationEmails<TUser>>(http).SendVerificationAsync(user, code, null, http.RequestAborted);
        AuthEndpointsExtensions.Log.VerificationResent(logger, user.Id);
        return TypedResults.Ok(new MessageResponse("A new verification code is on its way to your email address."));
    }
}
