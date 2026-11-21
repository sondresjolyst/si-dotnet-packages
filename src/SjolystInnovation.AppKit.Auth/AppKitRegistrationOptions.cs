namespace SjolystInnovation.AppKit.Auth;

/// <summary>The details of a person who signs up, passed to the new-user factory.</summary>
/// <param name="Email">The email address the person gave.</param>
/// <param name="UserName">The user name the person chose, or null when the app does not ask for one.</param>
/// <param name="FirstName">The first name, or an empty string when the app does not ask for names.</param>
/// <param name="LastName">The last name, or an empty string when the app does not ask for names.</param>
/// <param name="Locale">The language the person uses, or null when the client sent none.</param>
public sealed record AppKitRegistration(string Email, string? UserName, string FirstName, string LastName, string? Locale);

/// <summary>The options for the opt-in public sign-up, set through <see cref="SelfRegistrationServiceCollectionExtensions.AddAppKitSelfRegistration{TUser, TContext}"/>.</summary>
public sealed class AppKitRegistrationOptions<TUser>
{
    /// <summary>A function that creates a user from an <see cref="AppKitRegistration"/>. Required.</summary>
    public Func<AppKitRegistration, TUser>? NewUser { get; set; }

    /// <summary>The role to give a new user, or null for none.</summary>
    public string? Role { get; set; }

    /// <summary>The age a person confirms to be at least. Defaults to 16.</summary>
    public int MinimumAge { get; set; } = 16;

    /// <summary>The document name of the terms in the consent rows. Defaults to <c>terms</c>.</summary>
    public string TermsDocument { get; set; } = "terms";

    /// <summary>Whether the sign-up requires a first and a last name. Defaults to false.</summary>
    public bool RequireNames { get; set; }

    /// <summary>Whether the sign-up accepts a user name. Defaults to false.</summary>
    public bool AllowUserName { get; set; }

    /// <summary>Whether to store a shortened IP address with each consent. Defaults to false.</summary>
    public bool RecordTruncatedIp { get; set; }

    /// <summary>How long a person waits before a new verification code may be sent. Defaults to 1 minute.</summary>
    public TimeSpan ResendCooldown { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The most emails per address and UTC day that anyone who types the address can cause: the notice to an existing
    /// account and the verification email that register sends for a new account. A <c>+tag</c> in the local part is
    /// ignored when addresses are compared, so it counts per mailbox. Over the cap nothing is sent and the answers do not change.
    /// No mailbox gets more than this and <see cref="MaxOwnerEmailsPerAddressPerDay"/> together from the sign-up in a day, however many
    /// accounts point at it. Defaults to 5.
    /// </summary>
    public int MaxEmailsPerAddressPerDay { get; set; } = 5;

    /// <summary>
    /// The most emails per account and UTC day that the owner causes: <c>resend-verification</c> and the removal
    /// reminders. An account is its exact normalised address. It has its own count, so people who are not signed in cannot use it up. A resend over the cap answers
    /// 429 <c>tooManyRequests</c>. Defaults to 5.
    /// </summary>
    public int MaxOwnerEmailsPerAddressPerDay { get; set; } = 5;

    /// <summary>How long an account may stay unverified before it is removed. Defaults to 30 days.</summary>
    public TimeSpan UnverifiedLifetime { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Whether a hosted service removes unverified accounts and sends the reminders, when the app starts and once a day
    /// after that. Set it to false to run <see cref="IUnverifiedAccountCleanup"/> from the app's own scheduler instead.
    /// Defaults to true.
    /// </summary>
    public bool RunCleanupInBackground { get; set; } = true;

    /// <summary>
    /// How long before removal each reminder goes out. Every offset must be positive and shorter than
    /// <see cref="UnverifiedLifetime"/>. Defaults to 14 days and 3 days. An empty list sends none.
    /// </summary>
    public IReadOnlyList<TimeSpan> RemindBefore { get; set; } = [TimeSpan.FromDays(14), TimeSpan.FromDays(3)];
}
