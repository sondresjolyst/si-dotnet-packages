using Microsoft.AspNetCore.Http;

namespace SjolystInnovation.AppKit.Auth;

/// <summary>Tells the sign-up which version of a document is current, for example the terms.</summary>
public interface IConsentVersionSource
{
    /// <summary>
    /// Returns the current version of <paramref name="document"/>, as a person sees it in <paramref name="locale"/>,
    /// or null when the app has no such document. The sign-up and the accept-terms endpoint ask without a locale, because a requirement does not belong to one. With no version here and no requirement in effect, the sign-up stores <see cref="AccountConsent.Unversioned"/> and ignores the version the client sent.
    /// </summary>
    Task<string?> GetCurrentVersionAsync(string document, string? locale, CancellationToken cancellationToken);
}

/// <summary>Runs inside the sign-up transaction, for example to add the app's own rows for a new user.</summary>
public interface IRegistrationHandler<in TUser>
{
    /// <summary>Handles the new user before the transaction commits. An exception rolls the whole sign-up back.</summary>
    Task OnRegisteringAsync(TUser user, AppKitRegistration registration, CancellationToken cancellationToken);
}

/// <summary>Runs after a user has verified the email address.</summary>
public interface IEmailVerifiedHandler<in TUser>
{
    /// <summary>Handles the verified user after the verification is saved.</summary>
    Task OnEmailVerifiedAsync(TUser user, CancellationToken cancellationToken);
}

/// <summary>Sends the emails of the public sign-up.</summary>
public interface IRegistrationEmails<in TUser>
{
    /// <summary>Sends the verification code to a person who signed up or asked for a new code.</summary>
    Task SendVerificationAsync(TUser user, string code, string? locale, CancellationToken cancellationToken);

    /// <summary>Tells the owner of an existing account that someone tried to sign up with the address.</summary>
    Task SendAlreadyRegisteredAsync(TUser user, string? locale, CancellationToken cancellationToken);

    /// <summary>Warns the owner of an unverified account that it will be removed, and gives a fresh code.</summary>
    Task SendRemovalReminderAsync(TUser user, string code, DateTime removalAt, string? locale, CancellationToken cancellationToken);
}

/// <summary>
/// Lets the app check a request before a sign-up goes on, for example a captcha or an app attestation token. Register it in
/// the service collection. Without one, every sign-up goes on.
/// </summary>
public interface ISignUpGuard
{
    /// <summary>
    /// Returns true when the sign-up may go on, or false to refuse it with 400 and the code <see cref="AuthErrorCodes.SignUpRefused"/>.
    /// It runs after the field checks and before any lookup of the email, so its answer never depends on whether the email has an account.
    /// </summary>
    /// <param name="http">The request, where a token can be read from a header or the body.</param>
    /// <param name="email">The email address of the sign-up, trimmed.</param>
    Task<bool> MaySignUpAsync(HttpContext http, string email);
}

/// <summary>Marks documents that users must accept again. The app calls it when a document changes.</summary>
public interface IConsentRequirements
{
    /// <summary>
    /// Requires everyone to accept <paramref name="version"/> of <paramref name="document"/>. A user is pending
    /// from <paramref name="effectiveFrom"/> until the user accepts, and may accept earlier.
    /// </summary>
    Task RequireAcceptanceAsync(string document, string version, DateTime effectiveFrom, CancellationToken cancellationToken = default);
}
