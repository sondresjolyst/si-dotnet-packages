using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SjolystInnovation.AppKit.Email;

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
    NetworkCapReached,
}

public static partial class AuthEndpointsExtensions
{
    /// <summary>
    /// Maps <c>POST register</c> under <paramref name="prefix"/>, the anonymous sign-up. The body holds
    /// <c>email</c>, <c>password</c>, optional <c>userName</c>, <c>firstName</c>, <c>lastName</c> and <c>locale</c>,
    /// and <c>ageConfirmed</c>, <c>termsAccepted</c> and <c>termsVersion</c>. Every check that needs no account runs
    /// first, in a fixed order, so no refusal depends on whether the email has an account. A field error is a
    /// validation problem, and a terms version that is not the one that <see cref="IConsentVersionSource"/> serves answers 409 with the code
    /// <c>termsChanged</c>. A new email creates an unverified user, its consent rows and a verification code in one
    /// transaction, then sends the code. An existing email changes nothing. An active account, verified or not, is
    /// told that someone tried, at most once per <see cref="AppKitRegistrationOptions{TUser}.ResendCooldown"/>. No address gets more than <see cref="AppKitRegistrationOptions{TUser}.MaxEmailsPerAddressPerDay"/> of these emails a day, and over the cap nothing is sent and the answer is the same. A resend has its own cap, <see cref="AppKitRegistrationOptions{TUser}.MaxOwnerEmailsPerAddressPerDay"/>, and over it answers 429 <c>tooManyRequests</c>.
    /// The app's <see cref="ISignUpGuard"/>, when registered, runs after the field checks and before any lookup, and a no answers 400 <c>signUpRefused</c>.
    /// No network gets more than <see cref="AppKitRegistrationOptions{TUser}.MaxSignUpsPerNetworkPerDay"/> sign-ups a day, and over the cap the answer is the normal one and nothing happens.
    /// Every one of these cases answers 200 with the same body. An account that does not choose a user name gets a
    /// generated one, a new GUID in its <c>N</c> form, never the email. Sign-in is by email, so the user name is not shown or typed.
    /// A user name that contains <c>@</c> is refused, because a chosen one could otherwise look like another person's email.
    /// A chosen user name also goes through the allowed characters and the duplicate check of Identity before any lookup by email,
    /// so a bad name is <c>invalidUserName</c> and a taken one is <c>userNameTaken</c>, the same for a new and an existing email.
    /// It also maps <c>POST verify-email</c> with the body <c>code</c> and <c>POST resend-verification</c> with no body.
    /// Both need a signed-in user, the account that is being verified. A right code confirms the email, ends every
    /// session of the user, and answers 200. A check whose result cannot be saved answers 409 <c>tryAgain</c>. A wrong or expired code answers 400 <c>invalidCode</c>, a verified
    /// account 409 <c>alreadyVerified</c>, and five wrong codes 429 <c>tooManyAttempts</c>. A resend answers 200,
    /// or 429 <c>tooManyRequests</c> inside the cooldown, or 429 <c>tooManyAttempts</c> until the locked code has
    /// expired. It also maps <c>POST accept-terms</c> with the body <c>termsVersion</c> and optional <c>locale</c>, for a signed-in
    /// user whether the email is verified or not. A blank version is a field error. The version must be the one that
    /// <see cref="IConsentVersionSource"/> serves, and another version answers 409 <c>termsChanged</c>. With no source, or no version
    /// from it, any version is accepted and <see cref="AccountConsent.Unversioned"/> is recorded. It records the acceptance with
    /// the source <c>reaccept</c> and answers 204. It adds no row when the user already holds that version from at or after the newest
    /// mark of a requirement, so a double click adds one row, and it adds one after a newer mark. It needs Identity, <see cref="AuthServiceCollectionExtensions.AddAppKitAuth{TContext}"/>, an
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
        // An account that did not choose a user name gets one that cannot clash or fail validation. The email is never used
        // as one, because Identity would then judge the email as a user name only when the account is created.
        if (registration.UserName is null && (string.IsNullOrEmpty(probe.UserName) || string.Equals(probe.UserName, email, StringComparison.OrdinalIgnoreCase)))
            probe.UserName = Guid.NewGuid().ToString("N");
        if (options.AllowUserName && registration.UserName is not null && !errors.Has("UserName"))
            await CheckUserNameAsync(errors, users, probe);
        if (!string.IsNullOrWhiteSpace(password) && password.Length <= MaxPassword)
            await CheckPasswordAsync(errors, users, probe, password, Get<IOptions<IdentityOptions>>(http).Value.Password.RequiredLength);
        if (!request.AgeConfirmed)
            errors.Add("AgeConfirmed", AuthErrorCodes.AgeNotConfirmed, $"You must confirm that you are at least {options.MinimumAge} years old.");
        if (!request.TermsAccepted)
            errors.Add("TermsAccepted", AuthErrorCodes.TermsNotAccepted, "You must accept the terms.");
        if (errors.Any)
            return errors.ToResult();

        // The app's guard, such as a captcha or an app attestation check, runs before any lookup by email.
        if (http.RequestServices.GetService<ISignUpGuard>() is { } guard && !await guard.MaySignUpAsync(http, email))
            return AuthResults.Error(AuthErrorCodes.SignUpRefused, "The sign-up could not be checked. Try again.", StatusCodes.Status400BadRequest);

        // The source decides. With no version from it, the client's version is not stored and the unversioned marker is stored
        // instead, so only a version the server knows is ever stored.
        var current = await Get<AccountConsentService<TContext>>(http).CurrentVersionAsync(options.TermsDocument, cancellationToken);
        if (current is not null && !string.Equals(current, termsVersion, StringComparison.Ordinal))
            return AuthResults.Error(AuthErrorCodes.TermsChanged, "The terms have changed. Read the current terms and accept them again.", StatusCodes.Status409Conflict);

        // Every request that got this far counts for its network, whether or not the email has an account. Over the cap the
        // answer is the normal one and nothing happens. A caller whose address is unknown is not counted.
        if (IpShortening.Shorten(http.Connection.RemoteIpAddress) is { } network
            && !await Get<SignUpMailLimiter<TContext>>(http).TryCountNetworkAsync(SignUpMailAddress.NetworkHash(network), options.MaxSignUpsPerNetworkPerDay, cancellationToken))
        {
            // The other paths hash the password, so this one does too and takes about as long.
            _ = users.PasswordHasher.HashPassword(probe, password);
            AuthEndpointsExtensions.Log.RegistrationHandled(logger, null, RegistrationOutcome.NetworkCapReached);
            return Ok();
        }

        var emails = Get<IRegistrationEmails<TUser>>(http);
        var db = Get<TContext>(http);
        var existing = await users.FindByEmailAsync(email);
        if (existing is null)
        {
            string? code;
            try
            {
                var created = await CreateAsync(http, options, users, db, probe, password, registration, current ?? AccountConsent.Unversioned);
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
                if (await Get<SignUpMailLimiter<TContext>>(http).TryReserveAsync(email, probe.NormalizedEmail, SignUpMailKinds.Verification, options.MaxEmailsPerAddressPerDay, options.MaxOwnerEmailsPerAddressPerDay, TimeSpan.Zero, cancellationToken))
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

    // Identity's own user name checks, run for every request before any lookup by email, so the answer never depends on
    // whether the email has an account. Only a name the person chose is checked, never the email that stands in for one.
    private static async Task CheckUserNameAsync(AuthFieldErrors errors, UserManager<TUser> users, TUser probe)
    {
        var name = probe.UserName;
        if (string.IsNullOrEmpty(name))
            return;
        var allowed = users.Options.User.AllowedUserNameCharacters;
        if (!string.IsNullOrEmpty(allowed) && name.Any(c => !allowed.Contains(c, StringComparison.Ordinal)))
            errors.Add("UserName", AuthErrorCodes.InvalidUserName, users.ErrorDescriber.InvalidUserName(name).Description);
        else if (await users.FindByNameAsync(name) is not null)
            errors.Add("UserName", AuthErrorCodes.UserNameTaken, users.ErrorDescriber.DuplicateUserName(name).Description);
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
        if (!await Get<SignUpMailLimiter<TContext>>(http).TryReserveAsync(probe.Email!, existing.NormalizedEmail, SignUpMailKinds.Notice, options.MaxEmailsPerAddressPerDay, options.MaxOwnerEmailsPerAddressPerDay, options.ResendCooldown, cancellationToken))
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

// The default sender. The verification email, the notice to the owner of an existing account and the removal reminder
// are rendered in the layout of the other auth emails. The verification email and the reminder greet with no name,
// because the name of an unverified account was typed by whoever signed up. The notice carries the name of the account
// it goes to.
internal sealed class RegistrationEmails<TUser>(IEmailSender sender, IEmailBrand brands, IOptions<AppKitAuthEmailOptions> options, TimeProvider time)
    : IRegistrationEmails<TUser>
    where TUser : IdentityUser
{
    public async Task SendVerificationAsync(TUser user, string code, string? locale, CancellationToken cancellationToken)
    {
        if (!AppKitAuthEmailOptions.IsPageUrl(options.Value.VerifyEmailUrl))
            throw new InvalidOperationException("AppKitAuthEmailOptions.VerifyEmailUrl must be an absolute http or https address to send the verification email.");
        var expires = (user as IEmailVerificationUser)?.EmailVerificationCodeExpiration
            ?? throw new InvalidOperationException("The user has no verification code. Issue one with EmailVerificationCodes.Issue first.");
        var brand = await brands.GetBrandAsync(cancellationToken);
        await sender.SendAsync(AuthEmailRenderer.Render(AuthEmailKind.Verify, options.Value, brand, null, user.Email!, code,
            expires - time.GetUtcNow().UtcDateTime, options.Value.VerifyEmailUrl), cancellationToken);
    }

    // The language of the emails comes from the email options, like the other auth emails, so the locale is not used.
    public async Task SendAlreadyRegisteredAsync(TUser user, string? locale, CancellationToken cancellationToken)
    {
        var brand = await brands.GetBrandAsync(cancellationToken);
        await sender.SendAsync(RegistrationEmailRenderer.AlreadyRegistered(options.Value, brand, FirstName(user), user.Email!), cancellationToken);
    }

    public async Task SendRemovalReminderAsync(TUser user, string code, DateTime removalAt, string? locale, CancellationToken cancellationToken)
    {
        var brand = await brands.GetBrandAsync(cancellationToken);
        await sender.SendAsync(RegistrationEmailRenderer.Reminder(options.Value, brand, null, user.Email!, code, removalAt), cancellationToken);
    }

    private static string? FirstName(TUser user) => user is IAppKitAccount account ? account.FirstName : null;
}
