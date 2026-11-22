using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SjolystInnovation.AppKit.Auth;

/// <summary>
/// Sends the emails the auth endpoints need. Implement it with your own templates, or register
/// <see cref="AuthEmailServiceCollectionExtensions.AddAppKitAuthEmails{TUser}"/> for the shared layout.
/// </summary>
public interface IAuthEmails<in TUser>
{
    /// <summary>Sends the password reset code to the user's email address.</summary>
    Task SendPasswordResetAsync(TUser user, string code, CancellationToken cancellationToken);

    /// <summary>Sends an invited user the code they choose their first password with.</summary>
    Task SendInviteAsync(TUser user, string code, CancellationToken cancellationToken);
}

internal sealed record LoginRequest(string? Email, string? Password);
internal sealed record RefreshTokenRequest(string? Token, string? RefreshToken);
internal sealed record LogoutRequest(string? RefreshToken);
internal sealed record PasswordResetRequest(string? Email);
internal sealed record ResetPasswordRequest(string? Email, string? Code, string? NewPassword);
internal sealed record TokenResponse(string Token, string RefreshToken);
// The answer for an app that opted in to sign-up. An app that did not keeps the body of TokenResponse as it was.
internal sealed record SignUpTokenResponse(string Token, string RefreshToken, bool EmailVerified, IReadOnlyList<PendingConsent> PendingConsents);
internal sealed record MessageResponse(string Message);

/// <summary>The options for <see cref="AuthEndpointsExtensions.MapAppKitAuth{TUser, TContext}"/>.</summary>
public sealed class AppKitAuthEndpointOptions<TUser>
{
    /// <summary>
    /// A check for whether a user may sign in, refresh tokens, and reset their password, for example
    /// <c>u =&gt; !u.IsDeleted</c>. The endpoints treat a user it refuses like an unknown one. If it is not
    /// set, every user is active.
    /// </summary>
    public Func<TUser, bool>? IsActive { get; set; }
}

/// <summary>Maps the auth endpoints.</summary>
public static partial class AuthEndpointsExtensions
{
    /// <summary>
    /// Maps <c>login</c>, <c>refresh-token</c>, <c>logout</c>, <c>request-password-reset</c>, and
    /// <c>reset-password</c> under <paramref name="prefix"/>. All are anonymous POST endpoints. When the app called
    /// <see cref="SelfRegistrationServiceCollectionExtensions.AddAppKitSelfRegistration{TUser, TContext}"/>, the answers of
    /// <c>login</c> and <c>refresh-token</c> also hold <c>emailVerified</c> and <c>pendingConsents</c>, a list of
    /// <c>document</c> and <c>version</c>. Other apps get <c>token</c> and <c>refreshToken</c> only. They need
    /// Identity with a <see cref="SignInManager{TUser}"/>, <see cref="AuthServiceCollectionExtensions.AddAppKitAuth{TContext}"/>,
    /// and an <see cref="IAuthEmails{TUser}"/>. An error returns a <c>message</c> and a <c>code</c>. A
    /// missing or malformed field, or a password Identity rejects, returns a validation problem with a
    /// code for each field error. A reset whose result cannot be saved answers 409 <c>tryAgain</c>. When the app called <see cref="SelfRegistrationServiceCollectionExtensions.AddAppKitSelfRegistration{TUser, TContext}"/>, a right reset code on an account with an unconfirmed email also confirms the email and runs the <see cref="IEmailVerifiedHandler{TUser}"/> handlers, and the access tokens issued before the reset stop working at once. <see cref="AuthErrorCodes"/> lists the codes.
    /// </summary>
    public static RouteGroupBuilder MapAppKitAuth<TUser, TContext>(this IEndpointRouteBuilder endpoints,
        string prefix = "/api/auth", Action<AppKitAuthEndpointOptions<TUser>>? configure = null)
        where TUser : IdentityUser, IPasswordResetUser
        where TContext : DbContext, IAuthDbContext
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var options = new AppKitAuthEndpointOptions<TUser>();
        configure?.Invoke(options);
        var logger = endpoints.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("SjolystInnovation.AppKit.Auth.Endpoints");
        var handlers = new AuthHandlers<TUser, TContext>(options, logger);

        var group = endpoints.MapGroup(prefix).AllowAnonymous();
        group.MapPost("login", (LoginRequest request, HttpContext http) => handlers.LoginAsync(request, http));
        group.MapPost("refresh-token", (RefreshTokenRequest request, HttpContext http) => handlers.RefreshAsync(request, http));
        group.MapPost("logout", (LogoutRequest request, HttpContext http) => AuthHandlers<TUser, TContext>.LogoutAsync(request, http));
        group.MapPost("request-password-reset", (PasswordResetRequest request, HttpContext http) => handlers.RequestResetAsync(request, http));
        group.MapPost("reset-password", (ResetPasswordRequest request, HttpContext http) => handlers.ResetAsync(request, http));
        return group;
    }

    internal static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Sign-in failed for user {UserId}.")]
        public static partial void SignInFailed(ILogger logger, string userId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Sign-in locked for user {UserId}.")]
        public static partial void LockedOut(ILogger logger, string userId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Refresh refused for user {UserId}: {Status}.")]
        public static partial void RefreshRefused(ILogger logger, string userId, RotationStatus status);

        [LoggerMessage(Level = LogLevel.Information, Message = "Password reset code sent to user {UserId}.")]
        public static partial void ResetRequested(ILogger logger, string userId);

        [LoggerMessage(Level = LogLevel.Information, Message = "Password reset for user {UserId}, which signed out {Sessions} session(s).")]
        public static partial void PasswordReset(ILogger logger, string userId, int sessions);
    }
}

internal sealed class AuthHandlers<TUser, TContext>(AppKitAuthEndpointOptions<TUser> options, ILogger logger)
    where TUser : IdentityUser, IPasswordResetUser
    where TContext : DbContext, IAuthDbContext
{
    private bool Active(TUser? user) => user is not null && (options.IsActive is null || options.IsActive(user));

    private static T Get<T>(HttpContext http) where T : notnull => http.RequestServices.GetRequiredService<T>();

    private const string InvalidCredentials = "Invalid email or password.";
    private const string InvalidResetCode = "Invalid or expired reset code.";

    private static JsonHttpResult<AuthError> Error(string code, string message, int status) => AuthResults.Error(code, message, status);

    private static ValidationProblem? Validate(params (string Field, string? Value, bool IsEmail)[] fields)
    {
        var errors = new AuthFieldErrors();
        foreach (var (field, value, isEmail) in fields)
        {
            if (string.IsNullOrWhiteSpace(value))
                errors.Required(field);
            else if (isEmail && !LooksLikeEmail(value))
                errors.Add(field, AuthErrorCodes.InvalidEmail, AuthFieldErrors.InvalidEmailMessage);
        }
        return errors.Any ? errors.ToResult() : null;
    }

    // The same rule as FluentValidation's EmailAddress: one @ with something on both sides.
    private static bool LooksLikeEmail(string value)
    {
        var at = value.IndexOf('@', StringComparison.Ordinal);
        return at > 0 && at < value.Length - 1 && at == value.LastIndexOf('@');
    }

    public async Task<IResult> LoginAsync(LoginRequest request, HttpContext http)
    {
        var (users, signIn, jwt, refreshTokens, db) = (Get<UserManager<TUser>>(http), Get<SignInManager<TUser>>(http),
            Get<IJwtTokenService>(http), Get<IRefreshTokenService>(http), Get<TContext>(http));
        if (Validate(("Email", request.Email, true), ("Password", request.Password, false)) is { } invalid)
            return invalid;

        var user = await users.FindByEmailAsync(request.Email!);
        if (!Active(user))
            return Error(AuthErrorCodes.InvalidCredentials, InvalidCredentials, StatusCodes.Status401Unauthorized);

        var result = await signIn.PasswordSignInAsync(user!.UserName ?? string.Empty, request.Password!, isPersistent: false, lockoutOnFailure: true);
        if (!result.Succeeded)
        {
            if (result.IsLockedOut)
            {
                AuthEndpointsExtensions.Log.LockedOut(logger, user.Id);
                return Error(AuthErrorCodes.LockedOut, "Too many failed attempts. This login is temporarily locked, try again later.", StatusCodes.Status401Unauthorized);
            }
            AuthEndpointsExtensions.Log.SignInFailed(logger, user.Id);
            return Error(AuthErrorCodes.InvalidCredentials, InvalidCredentials, StatusCodes.Status401Unauthorized);
        }

        var token = jwt.Build(user, await users.GetRolesAsync(user));
        var refreshToken = await refreshTokens.IssueAsync(user.Id, http.RequestAborted);
        await db.SaveChangesAsync(http.RequestAborted);
        return await TokensAsync(http, user, token, refreshToken);
    }

    // An app that opted in to sign-up also tells the client whether the email is confirmed and which documents to accept again.
    private static async Task<IResult> TokensAsync(HttpContext http, TUser user, string token, string refreshToken)
    {
        if (http.RequestServices.GetService<IPendingConsentReader>() is not { } reader)
            return TypedResults.Ok(new TokenResponse(token, refreshToken));
        return TypedResults.Ok(new SignUpTokenResponse(token, refreshToken, user.EmailConfirmed, await reader.GetPendingAsync(user.Id, http.RequestAborted)));
    }

    public async Task<IResult> RefreshAsync(RefreshTokenRequest request, HttpContext http)
    {
        var (users, jwt, refreshTokens) = (Get<UserManager<TUser>>(http), Get<IJwtTokenService>(http), Get<IRefreshTokenService>(http));
        if (Validate(("Token", request.Token, false), ("RefreshToken", request.RefreshToken, false)) is { } invalid)
            return invalid;

        var userId = jwt.ReadUserIdFromExpired(request.Token!);
        if (userId is null)
            return Error(AuthErrorCodes.InvalidToken, "Invalid token", StatusCodes.Status400BadRequest);

        var user = await users.FindByIdAsync(userId);
        if (!Active(user))
            return Error(AuthErrorCodes.UserNotFound, "User not found", StatusCodes.Status401Unauthorized);

        var rotation = await refreshTokens.RotateAsync(user!.Id, request.RefreshToken!, http.RequestAborted);
        if (rotation.Status != RotationStatus.Rotated)
        {
            AuthEndpointsExtensions.Log.RefreshRefused(logger, user.Id, rotation.Status);
            return Error(AuthErrorCodes.InvalidRefreshToken, "Invalid or expired refresh token", StatusCodes.Status401Unauthorized);
        }

        return await TokensAsync(http, user, jwt.Build(user, await users.GetRolesAsync(user)), rotation.RefreshToken!);
    }

    public static async Task<IResult> LogoutAsync(LogoutRequest request, HttpContext http)
    {
        var refreshTokens = Get<IRefreshTokenService>(http);
        if (Validate(("RefreshToken", request.RefreshToken, false)) is { } invalid)
            return invalid;

        // The response is the same whether or not the token was live, so it reveals nothing.
        await refreshTokens.RevokeAsync(request.RefreshToken!, http.RequestAborted);
        return TypedResults.NoContent();
    }

    public async Task<IResult> RequestResetAsync(PasswordResetRequest request, HttpContext http)
    {
        var (users, emails, time) = (Get<UserManager<TUser>>(http), Get<IAuthEmails<TUser>>(http), Get<TimeProvider>(http));
        if (Validate(("Email", request.Email, true)) is { } invalid)
            return invalid;

        var user = await users.FindByEmailAsync(request.Email!);
        if (Active(user) && PasswordResetCodes.CanIssue(user!, Get<IOptions<AppKitAuthOptions>>(http).Value.ResetCodeCooldown, time))
        {
            var code = PasswordResetCodes.Issue(user!, time);
            await users.UpdateAsync(user!);
            await emails.SendPasswordResetAsync(user!, code, http.RequestAborted);
            AuthEndpointsExtensions.Log.ResetRequested(logger, user!.Id);
        }

        // An unknown address, or one that gets no new code yet, gets the same response, so the endpoint does
        // not reveal who has an account.
        return TypedResults.Ok(new MessageResponse("If this email is registered, a reset code has been sent."));
    }

    public async Task<IResult> ResetAsync(ResetPasswordRequest request, HttpContext http)
    {
        var (users, refreshTokens) = (Get<UserManager<TUser>>(http), Get<IRefreshTokenService>(http));
        if (Validate(("Email", request.Email, true), ("Code", request.Code, false), ("NewPassword", request.NewPassword, false)) is { } invalid)
            return invalid;

        var user = await users.FindByEmailAsync(request.Email!);
        if (!Active(user))
            return Error(AuthErrorCodes.InvalidResetCode, InvalidResetCode, StatusCodes.Status400BadRequest);

        switch (PasswordResetCodes.Check(user!, request.Code!))
        {
            case CodeCheck.TooManyAttempts:
                return Error(AuthErrorCodes.TooManyResetAttempts, "Too many attempts. Request a new reset code.", StatusCodes.Status429TooManyRequests);
            case CodeCheck.Invalid:
                // Only a request whose attempt was stored learns that the code was wrong.
                if (!(await users.UpdateAsync(user!)).Succeeded)
                    return AuthResults.TryAgain();
                return Error(AuthErrorCodes.InvalidResetCode, InvalidResetCode, StatusCodes.Status400BadRequest);
        }

        var token = await users.GeneratePasswordResetTokenAsync(user!);
        var result = await users.ResetPasswordAsync(user!, token, request.NewPassword!);
        if (result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.ConcurrencyFailure)))
            return AuthResults.TryAgain();
        if (!result.Succeeded)
            return new AuthFieldErrors().Password("NewPassword", result.Errors, Get<IOptions<IdentityOptions>>(http).Value.Password.RequiredLength).ToResult();

        PasswordResetCodes.Complete(user!);
        var optedIn = http.RequestServices.GetService<SelfRegistrationRegistered>() is not null;
        // An app that opted in to sign-up also takes the reset code as proof of the mailbox, so the reset confirms the email.
        var confirmed = optedIn && ConfirmEmail(user!);
        var completed = await users.UpdateAsync(user!);
        // A new password ends every session, so a session that someone took over cannot continue.
        var sessions = await refreshTokens.RevokeAllForUserAsync(user!.Id, http.RequestAborted);
        AuthEndpointsExtensions.Log.PasswordReset(logger, user.Id, sessions);
        if (optedIn)
        {
            // Resetting the password rotated the security stamp. Drop the user from the bearer check cache first, also when
            // the last save failed, so the access tokens issued before the reset stop working at once on this instance.
            http.RequestServices.GetService<IMemoryCache>()?.Remove(IdentityAndBearerExtensions.UserCheckKey(user.Id));
        }
        if (!completed.Succeeded)
            return AuthResults.TryAgain();

        if (confirmed)
        {
            AuthEndpointsExtensions.Log.EmailVerified(logger, user.Id);
            foreach (var handler in http.RequestServices.GetServices<IEmailVerifiedHandler<TUser>>())
                await handler.OnEmailVerifiedAsync(user, http.RequestAborted);
        }
        return TypedResults.Ok(new MessageResponse("Password reset successfully."));
    }

    // Confirms an unconfirmed email and clears its verification code. Returns whether it changed anything.
    private static bool ConfirmEmail(TUser user)
    {
        if (user.EmailConfirmed)
            return false;
        user.EmailConfirmed = true;
        if (user is IEmailVerificationUser verification)
        {
            verification.EmailVerificationCodeHash = null;
            verification.EmailVerificationCodeExpiration = null;
            verification.EmailVerificationAttempts = 0;
        }
        return true;
    }
}
