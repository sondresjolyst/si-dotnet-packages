using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SjolystInnovation.AppKit.Auth;

internal sealed record RegisterRequest(
    string? Email, string? Password, string? UserName, string? FirstName, string? LastName,
    bool AgeConfirmed, bool TermsAccepted, string? TermsVersion, string? Locale);

/// <summary>What a sign-up did, for the log. A new and an existing email log the same event with a different outcome.</summary>
internal enum RegistrationOutcome
{
    Created,
    OwnerNotified,
    NoticeHeldBack,
    Ignored,
}

public static partial class AuthEndpointsExtensions
{
    /// <summary>
    /// Maps <c>POST register</c> under <paramref name="prefix"/>, the anonymous sign-up. The body holds
    /// <c>email</c>, <c>password</c>, optional <c>userName</c>, <c>firstName</c>, <c>lastName</c> and <c>locale</c>,
    /// and <c>ageConfirmed</c>, <c>termsAccepted</c> and <c>termsVersion</c>. Every check that needs no account runs
    /// first, in a fixed order, so no refusal depends on whether the email has an account. A field error is a
    /// validation problem, and a terms version that is not the current one answers 409 with the code
    /// <c>termsChanged</c>. A new email creates an unverified user, its consent rows and a verification code in one
    /// transaction, then sends the code. An existing email changes nothing. An active account, verified or not, is
    /// told that someone tried, at most once per <see cref="AppKitRegistrationOptions{TUser}.ResendCooldown"/>.
    /// Every one of these cases answers 200 with the same body. A user name that contains <c>@</c> is refused,
    /// because the user name defaults to the email and a chosen one could otherwise take another person's email.
    /// It also maps <c>POST verify-email</c> with the body <c>code</c> and <c>POST resend-verification</c> with no body.
    /// Both need a signed-in user, the account that is being verified. A right code confirms the email, ends every
    /// session of the user, and answers 200. A check whose result cannot be saved answers 409 <c>tryAgain</c>. A wrong or expired code answers 400 <c>invalidCode</c>, a verified
    /// account 409 <c>alreadyVerified</c>, and five wrong codes 429 <c>tooManyAttempts</c>. A resend answers 200,
    /// or 429 <c>tooManyRequests</c> inside the cooldown, or 429 <c>tooManyAttempts</c> until the locked code has
    /// expired. It also maps <c>POST accept-terms</c> with the body <c>termsVersion</c> and optional <c>locale</c>, for a signed-in
    /// user whether the email is verified or not. A blank version is a field error, and a version that is not the current
    /// one answers 409 <c>termsChanged</c>. It records the acceptance with the source <c>reaccept</c>, answers 204, and
    /// answers 204 again for a version the user already accepted. It needs Identity, <see cref="AuthServiceCollectionExtensions.AddAppKitAuth{TContext}"/>, an
    /// <see cref="IRegistrationEmails{TUser}"/> and
    /// <see cref="SelfRegistrationServiceCollectionExtensions.AddAppKitSelfRegistration{TUser, TContext}"/>.
    /// An account counts as inactive when <typeparamref name="TUser"/> implements <see cref="IAppKitAccount"/> and
    /// <see cref="IAppKitAccount.IsDeleted"/> is true.
    /// </summary>
    /// <exception cref="InvalidOperationException">When the app did not call
    /// <see cref="SelfRegistrationServiceCollectionExtensions.AddAppKitSelfRegistration{TUser, TContext}"/>.</exception>
    public static RouteGroupBuilder MapAppKitRegistration<TUser, TContext>(this IEndpointRouteBuilder endpoints, string prefix = "/api/auth")
        where TUser : IdentityUser, IEmailVerificationUser
        where TContext : DbContext, IAccountConsentDbContext
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (endpoints.ServiceProvider.GetService<SelfRegistrationRegistered>() is null)
            throw new InvalidOperationException(
                $"{nameof(MapAppKitRegistration)} needs the sign-up options. Call AddAppKitSelfRegistration<TUser, TContext>() first.");
        var logger = endpoints.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("SjolystInnovation.AppKit.Auth.Registration");
        var handlers = new RegistrationHandlers<TUser, TContext>(logger);

        var group = endpoints.MapGroup(prefix).AllowAnonymous();
        group.MapPost("register", (RegisterRequest request, HttpContext http) => handlers.RegisterAsync(request, http));

        // Not in the anonymous group, because an AllowAnonymous on a group would win over RequireAuthorization.
        var verification = new VerificationHandlers<TUser, TContext>(logger);
        var signedIn = endpoints.MapGroup(prefix).RequireAuthorization();
        signedIn.MapPost("verify-email", (VerifyEmailRequest request, HttpContext http) => verification.VerifyAsync(request, http));
        signedIn.MapPost("resend-verification", (HttpContext http, CancellationToken _) => verification.ResendAsync(http));
        signedIn.MapPost("accept-terms", (AcceptTermsRequest request, HttpContext http) => ConsentHandlers<TUser, TContext>.AcceptTermsAsync(request, http));
        return group;
    }

    internal static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Sign-up handled for user {UserId}: {Outcome}.")]
        public static partial void RegistrationHandled(ILogger logger, string? userId, RegistrationOutcome outcome);

        [LoggerMessage(Level = LogLevel.Information, Message = "Email verified for user {UserId}.")]
        public static partial void EmailVerified(ILogger logger, string userId);

        [LoggerMessage(Level = LogLevel.Information, Message = "Verification code sent again to user {UserId}.")]
        public static partial void VerificationResent(ILogger logger, string userId);
    }
}

internal static class IpShortening
{
    /// <summary>Keeps the first three parts of an IPv4 address and the first 48 bits of an IPv6 address, and zeroes the rest.</summary>
    public static string? Shorten(IPAddress? address)
    {
        if (address is null)
            return null;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        var keep = bytes.Length == 4 ? 3 : 6;
        Array.Clear(bytes, keep, bytes.Length - keep);
        return new IPAddress(bytes).ToString();
    }
}

internal sealed class RegistrationHandlers<TUser, TContext>(ILogger logger)
    where TUser : IdentityUser, IEmailVerificationUser
    where TContext : DbContext, IAccountConsentDbContext
{
    private const int MaxEmail = 200;
    private const int MaxPassword = 128;
    private const int MaxName = 50;
    private const int MaxTermsVersion = 64;
    private const int MaxLocale = 16;
    private const string Accepted = "If the sign-up can go ahead, a verification code is on its way to the email address.";

    private static T Get<T>(HttpContext http) where T : notnull => http.RequestServices.GetRequiredService<T>();

    private static Microsoft.AspNetCore.Http.HttpResults.Ok<MessageResponse> Ok() => TypedResults.Ok(new MessageResponse(Accepted));

    public async Task<IResult> RegisterAsync(RegisterRequest request, HttpContext http)
    {
        var options = Get<IOptions<AppKitRegistrationOptions<TUser>>>(http).Value;
        var users = Get<UserManager<TUser>>(http);
        var cancellationToken = http.RequestAborted;

        var email = request.Email?.Trim() ?? string.Empty;
        var password = request.Password ?? string.Empty;
        var termsVersion = request.TermsVersion?.Trim() ?? string.Empty;
        var locale = string.IsNullOrWhiteSpace(request.Locale) ? null : request.Locale.Trim();
        var registration = new AppKitRegistration(
            email,
            options.AllowUserName && !string.IsNullOrWhiteSpace(request.UserName) ? request.UserName.Trim() : null,
            options.RequireNames ? request.FirstName?.Trim() ?? string.Empty : string.Empty,
            options.RequireNames ? request.LastName?.Trim() ?? string.Empty : string.Empty,
            locale);

        // Everything that needs no account, in one fixed order: fields, password, age, terms, terms version.
        var errors = new AuthFieldErrors();
        CheckFields(errors, options, request, email, termsVersion, locale, registration);
        var probe = options.NewUser!(registration);
        probe.Email = email;
        probe.UserName ??= email;
        if (!string.IsNullOrWhiteSpace(password) && password.Length <= MaxPassword)
            await CheckPasswordAsync(errors, users, probe, password, Get<IOptions<IdentityOptions>>(http).Value.Password.RequiredLength);
        if (!request.AgeConfirmed)
            errors.Add("AgeConfirmed", AuthErrorCodes.AgeNotConfirmed, $"You must confirm that you are at least {options.MinimumAge} years old.");
        if (!request.TermsAccepted)
            errors.Add("TermsAccepted", AuthErrorCodes.TermsNotAccepted, "You must accept the terms.");
        if (errors.Any)
            return errors.ToResult();

        var source = http.RequestServices.GetService<IConsentVersionSource>();
        var current = source is null ? null : await source.GetCurrentVersionAsync(options.TermsDocument, locale, cancellationToken);
        if (current is not null && !string.Equals(current, termsVersion, StringComparison.Ordinal))
            return AuthResults.Error(AuthErrorCodes.TermsChanged, "The terms have changed. Read the current terms and accept them again.", StatusCodes.Status409Conflict);

        var emails = Get<IRegistrationEmails<TUser>>(http);
        var db = Get<TContext>(http);
        var existing = await users.FindByEmailAsync(email);
        if (existing is null)
        {
            string? code;
            try
            {
                var created = await CreateAsync(http, options, users, db, probe, password, registration, termsVersion);
                if (created.Problem is { } problem)
                    return problem;
                code = created.Code;
            }
            catch (DbUpdateException)
            {
                // Another sign-up may have taken the email first. Anything else is a real failure.
                db.ChangeTracker.Clear();
                if (await users.FindByEmailAsync(email) is null)
                    throw;
                code = null;
            }
            if (code is not null)
            {
                await emails.SendVerificationAsync(probe, code, locale, cancellationToken);
                AuthEndpointsExtensions.Log.RegistrationHandled(logger, probe.Id, RegistrationOutcome.Created);
                return Ok();
            }
            db.ChangeTracker.Clear();
            existing = await users.FindByEmailAsync(email);
        }

        await HandleExistingAsync(http, options, users, emails, existing, probe, password, locale);
        return Ok();
    }

    private static void CheckFields(AuthFieldErrors errors, AppKitRegistrationOptions<TUser> options, RegisterRequest request,
        string email, string termsVersion, string? locale, AppKitRegistration registration)
    {
        if (email.Length == 0)
            errors.Required("Email");
        else if (!LooksLikeEmail(email) || email.Length > MaxEmail)
            errors.Add("Email", AuthErrorCodes.InvalidEmail, AuthFieldErrors.InvalidEmailMessage);
        if (string.IsNullOrWhiteSpace(request.Password))
            errors.Required("Password");
        else if (request.Password.Length > MaxPassword)
            errors.Add("Password", AuthErrorCodes.TooLong, $"At most {MaxPassword} characters.", MaxPassword);
        if (options.RequireNames)
        {
            errors.Name("FirstName", registration.FirstName, MaxName);
            errors.Name("LastName", registration.LastName, MaxName);
        }
        if (options.AllowUserName && registration.UserName is { Length: > MaxName })
            errors.Add("UserName", AuthErrorCodes.TooLong, $"At most {MaxName} characters.", MaxName);
        else if (options.AllowUserName && registration.UserName is { } userName && userName.Contains('@', StringComparison.Ordinal))
            errors.Add("UserName", AuthErrorCodes.InvalidUserName, "The user name cannot contain @.");
        errors.Name("TermsVersion", termsVersion, MaxTermsVersion);
        if (locale is { Length: > MaxLocale })
            errors.Add("Locale", AuthErrorCodes.TooLong, $"At most {MaxLocale} characters.", MaxLocale);
    }

    // Runs the password validators against a user that is never saved, so the answer cannot depend on an account.
    private static async Task CheckPasswordAsync(AuthFieldErrors errors, UserManager<TUser> users, TUser probe, string password, int requiredLength)
    {
        var failures = new List<IdentityError>();
        foreach (var validator in users.PasswordValidators)
        {
            var result = await validator.ValidateAsync(users, probe, password);
            if (!result.Succeeded)
                failures.AddRange(result.Errors);
        }
        errors.Password("Password", failures, requiredLength);
    }

    // One transaction for the user, its role, the app's rows, the consents and the code. The result holds the code
    // when the account was made, or no code when the email already has one. A problem is a refusal to return as it is.
    private static async Task<(string? Code, IResult? Problem)> CreateAsync(HttpContext http, AppKitRegistrationOptions<TUser> options,
        UserManager<TUser> users, TContext db, TUser user, string password, AppKitRegistration registration, string termsVersion)
    {
        var cancellationToken = http.RequestAborted;
        var time = Get<TimeProvider>(http);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        user.EmailConfirmed = false;
        var created = await users.CreateAsync(user, password);
        if (!created.Succeeded)
        {
            if (created.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.DuplicateEmail)))
                return (null, null);
            return (null, CreationProblem(created.Errors));
        }

        if (options.Role is { } role)
        {
            var added = await users.AddToRoleAsync(user, role);
            if (!added.Succeeded)
                throw new InvalidOperationException($"Giving the new user the role {role} failed: " + string.Join(" ", added.Errors.Select(e => e.Description)));
        }
        foreach (var handler in http.RequestServices.GetServices<IRegistrationHandler<TUser>>())
            await handler.OnRegisteringAsync(user, registration, cancellationToken);

        var now = time.GetUtcNow().UtcDateTime;
        var ip = options.RecordTruncatedIp ? IpShortening.Shorten(http.Connection.RemoteIpAddress) : null;
        db.AccountConsents.Add(NewConsent(user.Id, options.TermsDocument, termsVersion, registration.Locale, now, ip));
        db.AccountConsents.Add(NewConsent(user.Id, "age", options.MinimumAge.ToString(CultureInfo.InvariantCulture), registration.Locale, now, ip));

        var code = EmailVerificationCodes.Issue(user, time);
        var updated = await users.UpdateAsync(user);
        if (!updated.Succeeded)
            throw new InvalidOperationException("Saving the new user failed: " + string.Join(" ", updated.Errors.Select(e => e.Description)));
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return (code, null);
    }

    private static AccountConsent NewConsent(string userId, string document, string version, string? locale, DateTime now, string? ip) =>
        new() { UserId = userId, Document = document, Version = version, Locale = locale, AcceptedAt = now, Source = "register", Ip = ip };

    // Identity refused a new user for a reason other than a taken email, such as a user name with bad characters.
    private static Microsoft.AspNetCore.Http.HttpResults.ValidationProblem CreationProblem(IEnumerable<IdentityError> identityErrors)
    {
        var errors = new AuthFieldErrors();
        foreach (var error in identityErrors)
        {
            switch (error.Code)
            {
                case nameof(IdentityErrorDescriber.DuplicateUserName):
                    errors.Add("UserName", AuthErrorCodes.UserNameTaken, error.Description);
                    break;
                case nameof(IdentityErrorDescriber.InvalidUserName):
                    errors.Add("UserName", AuthErrorCodes.InvalidUserName, error.Description);
                    break;
                case nameof(IdentityErrorDescriber.InvalidEmail):
                    errors.Add("Email", AuthErrorCodes.InvalidEmail, error.Description);
                    break;
                default:
                    throw new InvalidOperationException("Creating the user failed: " + error.Description);
            }
        }
        return errors.ToResult();
    }

    // The email has an account. Nothing is created or changed. An active account hears that someone tried, at most once per cooldown.
    private async Task HandleExistingAsync(HttpContext http, AppKitRegistrationOptions<TUser> options, UserManager<TUser> users, IRegistrationEmails<TUser> emails,
        TUser? existing, TUser probe, string password, string? locale)
    {
        var cancellationToken = http.RequestAborted;
        // The new-email path hashes the password, so this path hashes one too and the two take about as long.
        _ = users.PasswordHasher.HashPassword(probe, password);

        if (existing is null or IAppKitAccount { IsDeleted: true })
        {
            AuthEndpointsExtensions.Log.RegistrationHandled(logger, existing?.Id, RegistrationOutcome.Ignored);
            return;
        }
        if (!VerificationThrottle.NoticeIsDue(Get<IMemoryCache>(http), existing.Id, Get<TimeProvider>(http).GetUtcNow().UtcDateTime, options.ResendCooldown))
        {
            AuthEndpointsExtensions.Log.RegistrationHandled(logger, existing.Id, RegistrationOutcome.NoticeHeldBack);
            return;
        }
        await emails.SendAlreadyRegisteredAsync(existing, locale, cancellationToken);
        AuthEndpointsExtensions.Log.RegistrationHandled(logger, existing.Id, RegistrationOutcome.OwnerNotified);
    }

    // The same rule as the other endpoints: one @ with something on both sides.
    private static bool LooksLikeEmail(string value)
    {
        var at = value.IndexOf('@', StringComparison.Ordinal);
        return at > 0 && at < value.Length - 1 && at == value.LastIndexOf('@');
    }
}

/// <summary>Holds whether the missing-sender warning has been logged, so it is logged once for the app.</summary>
internal sealed class RegistrationEmailsWarning
{
    public int Logged;
}

// The default sender. The verification email uses the shared layout through IVerificationEmails. The notice to the
// owner of a verified account and the removal reminder have no layout kind yet, so they are skipped with one warning.
internal sealed class RegistrationEmails<TUser>(IVerificationEmails<TUser> verification, RegistrationEmailsWarning warning, ILogger<RegistrationEmails<TUser>> logger)
    : IRegistrationEmails<TUser>
{
    public Task SendVerificationAsync(TUser user, string code, string? locale, CancellationToken cancellationToken) =>
        verification.SendEmailVerificationAsync(user, code, cancellationToken);

    public Task SendAlreadyRegisteredAsync(TUser user, string? locale, CancellationToken cancellationToken) => Skip();

    public Task SendRemovalReminderAsync(TUser user, string code, DateTime removalAt, string? locale, CancellationToken cancellationToken) => Skip();

    private Task Skip()
    {
        if (Interlocked.Exchange(ref warning.Logged, 1) == 0)
            RegistrationEmailsLog.NoSender(logger);
        return Task.CompletedTask;
    }
}

internal static partial class RegistrationEmailsLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "No IRegistrationEmails is registered, so the already registered notice and the removal reminder are not sent. Register your own to send them.")]
    public static partial void NoSender(ILogger logger);
}
