using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SjolystInnovation.AppKit.Auth;

/// <summary>Sends the emails the auth endpoints need. The app implements it with its own templates.</summary>
public interface IAuthEmails<in TUser>
{
    /// <summary>Sends the password reset code to the user's email address.</summary>
    Task SendPasswordResetAsync(TUser user, string code, CancellationToken cancellationToken);
}

internal sealed record LoginRequest(string? Email, string? Password);
internal sealed record RefreshTokenRequest(string? Token, string? RefreshToken);
internal sealed record LogoutRequest(string? RefreshToken);
internal sealed record PasswordResetRequest(string? Email);
internal sealed record ResetPasswordRequest(string? Email, string? Code, string? NewPassword);
internal sealed record TokenResponse(string Token, string RefreshToken);
internal sealed record MessageResponse(string Message);

/// <summary>The messages the auth endpoints answer with. English by default.</summary>
public sealed class AuthMessages
{
    public string InvalidCredentials { get; set; } = "Invalid email or password.";
    public string LockedOut { get; set; } = "Too many failed attempts. This login is temporarily locked, try again later.";
    public string InvalidToken { get; set; } = "Invalid token";
    public string UserNotFound { get; set; } = "User not found";
    public string InvalidRefreshToken { get; set; } = "Invalid or expired refresh token";
    public string ResetRequested { get; set; } = "If this email is registered, a reset code has been sent.";
    public string InvalidResetCode { get; set; } = "Invalid or expired reset code.";
    public string TooManyResetAttempts { get; set; } = "Too many attempts. Request a new reset code.";
    public string PasswordReset { get; set; } = "Password reset successfully.";
    public string Required { get; set; } = "Required.";
    public string InvalidEmail { get; set; } = "Not a valid email address.";
}

/// <summary>Settings for <see cref="AuthEndpointsExtensions.MapAppKitAuthEndpoints{TUser, TContext}"/>.</summary>
public sealed class AppKitAuthEndpointOptions<TUser>
{
    /// <summary>
    /// Whether a user may sign in, refresh and reset a password, such as <c>u =&gt; !u.IsDeleted</c>. A user
    /// it refuses is treated like an unknown one. Everyone is active when it is not set.
    /// </summary>
    public Func<TUser, bool>? IsActive { get; set; }

    public AuthMessages Messages { get; } = new();
}

public static partial class AuthEndpointsExtensions
{
    /// <summary>
    /// Maps <c>login</c>, <c>refresh-token</c>, <c>logout</c>, <c>request-password-reset</c> and
    /// <c>reset-password</c> under <paramref name="prefix"/>, all anonymous and all POST. They need
    /// Identity with a <see cref="SignInManager{TUser}"/>, <see cref="ServiceCollectionExtensions.AddAppKitAuth{TContext}"/>
    /// and an <see cref="IAuthEmails{TUser}"/>. Errors answer with a <c>message</c>, and a missing or
    /// malformed field with a validation problem.
    /// </summary>
    public static RouteGroupBuilder MapAppKitAuthEndpoints<TUser, TContext>(this IEndpointRouteBuilder endpoints,
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
        group.MapPost("logout", (LogoutRequest request, HttpContext http) => handlers.LogoutAsync(request, http));
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
    private AuthMessages M => options.Messages;

    private bool Active(TUser? user) => user is not null && (options.IsActive is null || options.IsActive(user));

    private static T Get<T>(HttpContext http) where T : notnull => http.RequestServices.GetRequiredService<T>();

    private static JsonHttpResult<MessageResponse> Message(string message, int status) => TypedResults.Json(new MessageResponse(message), statusCode: status);

    private ValidationProblem? Validate(params (string Field, string? Value, bool IsEmail)[] fields)
    {
        var errors = new Dictionary<string, string[]>();
        foreach (var (field, value, isEmail) in fields)
        {
            if (string.IsNullOrWhiteSpace(value))
                errors[field] = [M.Required];
            else if (isEmail && !LooksLikeEmail(value))
                errors[field] = [M.InvalidEmail];
        }
        return errors.Count == 0 ? null : TypedResults.ValidationProblem(errors);
    }

    // The same rule FluentValidation's EmailAddress applies: one @ with something on both sides.
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
            return Message(M.InvalidCredentials, StatusCodes.Status401Unauthorized);

        var result = await signIn.PasswordSignInAsync(user!.UserName ?? string.Empty, request.Password!, isPersistent: false, lockoutOnFailure: true);
        if (!result.Succeeded)
        {
            if (result.IsLockedOut)
            {
                AuthEndpointsExtensions.Log.LockedOut(logger, user.Id);
                return Message(M.LockedOut, StatusCodes.Status401Unauthorized);
            }
            AuthEndpointsExtensions.Log.SignInFailed(logger, user.Id);
            return Message(M.InvalidCredentials, StatusCodes.Status401Unauthorized);
        }

        var token = jwt.Build(user, await users.GetRolesAsync(user));
        var refreshToken = await refreshTokens.IssueAsync(user.Id, http.RequestAborted);
        await db.SaveChangesAsync(http.RequestAborted);
        return TypedResults.Ok(new TokenResponse(token, refreshToken));
    }

    public async Task<IResult> RefreshAsync(RefreshTokenRequest request, HttpContext http)
    {
        var (users, jwt, refreshTokens) = (Get<UserManager<TUser>>(http), Get<IJwtTokenService>(http), Get<IRefreshTokenService>(http));
        if (Validate(("Token", request.Token, false), ("RefreshToken", request.RefreshToken, false)) is { } invalid)
            return invalid;

        var userId = jwt.ReadUserIdFromExpired(request.Token!);
        if (userId is null)
            return Message(M.InvalidToken, StatusCodes.Status400BadRequest);

        var user = await users.FindByIdAsync(userId);
        if (!Active(user))
            return Message(M.UserNotFound, StatusCodes.Status401Unauthorized);

        var rotation = await refreshTokens.RotateAsync(user!.Id, request.RefreshToken!, http.RequestAborted);
        if (rotation.Status != RotationStatus.Rotated)
        {
            AuthEndpointsExtensions.Log.RefreshRefused(logger, user.Id, rotation.Status);
            return Message(M.InvalidRefreshToken, StatusCodes.Status401Unauthorized);
        }

        return TypedResults.Ok(new TokenResponse(jwt.Build(user, await users.GetRolesAsync(user)), rotation.RefreshToken!));
    }

    public async Task<IResult> LogoutAsync(LogoutRequest request, HttpContext http)
    {
        var refreshTokens = Get<IRefreshTokenService>(http);
        if (Validate(("RefreshToken", request.RefreshToken, false)) is { } invalid)
            return invalid;

        // The answer is the same whether or not the token was live, so it reveals nothing.
        await refreshTokens.RevokeAsync(request.RefreshToken!, http.RequestAborted);
        return TypedResults.NoContent();
    }

    public async Task<IResult> RequestResetAsync(PasswordResetRequest request, HttpContext http)
    {
        var (users, emails) = (Get<UserManager<TUser>>(http), Get<IAuthEmails<TUser>>(http));
        if (Validate(("Email", request.Email, true)) is { } invalid)
            return invalid;

        var user = await users.FindByEmailAsync(request.Email!);
        if (Active(user))
        {
            var code = PasswordResetCodes.Issue(user!);
            await users.UpdateAsync(user!);
            await emails.SendPasswordResetAsync(user!, code, http.RequestAborted);
            AuthEndpointsExtensions.Log.ResetRequested(logger, user!.Id);
        }

        // The same answer for an unknown address, so the endpoint does not reveal who has an account.
        return TypedResults.Ok(new MessageResponse(M.ResetRequested));
    }

    public async Task<IResult> ResetAsync(ResetPasswordRequest request, HttpContext http)
    {
        var (users, refreshTokens) = (Get<UserManager<TUser>>(http), Get<IRefreshTokenService>(http));
        if (Validate(("Email", request.Email, true), ("Code", request.Code, false), ("NewPassword", request.NewPassword, false)) is { } invalid)
            return invalid;

        var user = await users.FindByEmailAsync(request.Email!);
        if (!Active(user))
            return Message(M.InvalidResetCode, StatusCodes.Status400BadRequest);

        switch (PasswordResetCodes.Check(user!, request.Code!))
        {
            case CodeCheck.TooManyAttempts:
                return Message(M.TooManyResetAttempts, StatusCodes.Status429TooManyRequests);
            case CodeCheck.Invalid:
                await users.UpdateAsync(user!);
                return Message(M.InvalidResetCode, StatusCodes.Status400BadRequest);
        }

        var token = await users.GeneratePasswordResetTokenAsync(user!);
        var result = await users.ResetPasswordAsync(user!, token, request.NewPassword!);
        if (!result.Succeeded)
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["NewPassword"] = result.Errors.Select(e => e.Description).ToArray() });

        PasswordResetCodes.Complete(user!);
        await users.UpdateAsync(user!);
        // A new password ends every session, so one that was taken over cannot carry on.
        var sessions = await refreshTokens.RevokeAllForUserAsync(user!.Id, http.RequestAborted);
        AuthEndpointsExtensions.Log.PasswordReset(logger, user.Id, sessions);
        return TypedResults.Ok(new MessageResponse(M.PasswordReset));
    }
}
