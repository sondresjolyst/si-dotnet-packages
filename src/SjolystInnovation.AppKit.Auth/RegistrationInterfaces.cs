namespace SjolystInnovation.AppKit.Auth;

/// <summary>Tells the sign-up which version of a document is current, for example the terms.</summary>
public interface IConsentVersionSource
{
    /// <summary>
    /// Returns the current version of <paramref name="document"/>, as a person sees it in <paramref name="locale"/>,
    /// or null when the app has no such document. The sign-up then accepts any version and stores it as sent.
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

    /// <summary>Tells the owner of a verified account that someone tried to sign up with the address.</summary>
    Task SendAlreadyRegisteredAsync(TUser user, string? locale, CancellationToken cancellationToken);

    /// <summary>Warns the owner of an unverified account that it will be removed, and gives a fresh code.</summary>
    Task SendRemovalReminderAsync(TUser user, string code, DateTime removalAt, string? locale, CancellationToken cancellationToken);
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
