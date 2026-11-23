using Microsoft.AspNetCore.Http;

namespace SjolystInnovation.AppKit.Auth;

/// <summary>Tells the sign-up which version of a document is current, for example the terms or a further document such as <c>privacy</c>.</summary>
public interface IConsentVersionSource
{
    /// <summary>
    /// Returns the current version of <paramref name="document"/>, as a person sees it in <paramref name="locale"/>,
    /// or null when the app has no such document. The sign-up and the accept-terms endpoint ask with the <c>locale</c> of the request, or with none when it has none.
    /// The list of documents to accept again asks with the locale of the user's latest acceptance of the document, or with none when there is
    /// none. Marking a requirement asks with no locale, because a requirement does not belong to one. The sign-up and the accept-terms endpoint accept exactly the version given here, and any other version answers 409 <c>termsChanged</c>.
    /// With no source, or no version here, the sign-up and the accept-terms endpoint store <see cref="AccountConsent.Unversioned"/> and ignore the version the client sent.
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

/// <summary>
/// Sends the emails of the public sign-up. Each method gets a <c>locale</c>, such as <c>nb</c> or <c>en-GB</c>, or null. For a new
/// sign-up it is the locale of the request. For an existing account it is the locale of the latest consent row of the user.
/// </summary>
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
    /// Requires everyone to accept <paramref name="document"/> again. The requirement is marked now. A user is pending from
    /// <paramref name="effectiveFrom"/> unless the user's latest acceptance of the document, of any version, was made at or after
    /// the mark, so a user may accept earlier. The requirement records the version that <see cref="IConsentVersionSource"/> serves at
    /// this moment, or <see cref="AccountConsent.Unversioned"/> when it serves none, and never compares it. A person accepts what the
    /// source serves, so publish the new text first and then call this. A person who accepted between the two is asked once more,
    /// which is safe. Calling it again with the same document and effective time changes nothing. The call clears the requirement
    /// cache of this instance only. After a mark that takes effect at once, a pending user may still pass on another instance for up
    /// to 10 seconds of requirement cache plus <see cref="AppKitJwtBearerOptions.UserCheckCacheDuration"/> of user check cache, which
    /// is 70 seconds by default. Acceptance and mark times are compared, so instances must share a clock within the usual server tolerance.
    /// </summary>
    /// <param name="document">The document, such as <c>terms</c>. It must be the terms document or one of the further documents of the sign-up options.</param>
    /// <param name="effectiveFrom">From when the requirement binds, in UTC.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="ArgumentException">When <paramref name="document"/> is neither the terms document nor a further document,
    /// because no one could accept it and every user would stay pending.</exception>
    Task RequireAcceptanceAsync(string document, DateTime effectiveFrom, CancellationToken cancellationToken = default);
}
