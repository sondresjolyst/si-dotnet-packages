namespace SjolystInnovation.AppKit.Auth;

/// <summary>The details of a person who signs up, passed to the new-user factory.</summary>
/// <param name="Email">The email address the person gave.</param>
/// <param name="UserName">The user name the person chose, trimmed, or null when the app does not take one or the person sent none.</param>
/// <param name="FirstName">The first name, or an empty string when the app does not ask for names.</param>
/// <param name="LastName">The last name, or an empty string when the app does not ask for names.</param>
/// <param name="Locale">The language the person uses, or null when the client sent none.</param>
public sealed record AppKitRegistration(string Email, string? UserName, string FirstName, string LastName, string? Locale);

/// <summary>The options for the opt-in public sign-up, set through <see cref="SelfRegistrationServiceCollectionExtensions.AddAppKitSelfRegistration{TUser, TContext}"/>.</summary>
public sealed class AppKitRegistrationOptions<TUser>
{
    /// <summary>
    /// A function that creates a user from an <see cref="AppKitRegistration"/>. Required. The sign-up replaces the user name that it
    /// sets with the one the person chose when <see cref="AllowUserName"/> or <see cref="RequireUserName"/> is on and one was sent,
    /// and otherwise with a generated one.
    /// </summary>
    public Func<AppKitRegistration, TUser>? NewUser { get; set; }

    /// <summary>The role to give a new user, or null for none.</summary>
    public string? Role { get; set; }

    /// <summary>The age a person confirms to be at least. Defaults to 16.</summary>
    public int MinimumAge { get; set; } = 16;

    /// <summary>
    /// The document name of the terms in the consent rows. It is 1 to 32 ASCII letters, digits, hyphens or underscores. Defaults to <c>terms</c>.
    /// </summary>
    public string TermsDocument { get; set; } = "terms";

    /// <summary>
    /// The further documents a person accepts at sign-up next to the terms, such as <c>privacy</c>. Each one is sent in the
    /// <c>documents</c> field of the sign-up with the version the person saw, is checked against its own current version, and gets
    /// its own consent row. Each one can also be accepted again through <c>accept-terms</c> with its name in <c>document</c>.
    /// A name is 1 to 32 ASCII letters, digits, hyphens or underscores, so it is never blank and has no spaces. It is not repeated,
    /// and is neither <see cref="TermsDocument"/> nor <c>age</c>. Defaults to none.
    /// </summary>
    public IReadOnlyList<string> AdditionalDocuments { get; set; } = [];

    // A document name fits the consent column and makes an unambiguous field key such as Documents.privacy.
    internal static bool IsDocumentName(string? name) =>
        name is { Length: > 0 and <= 32 } && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    // The documents that accept-terms takes by name and that a requirement may name: the terms, then the further documents.
    internal bool IsAcceptedDocument(string document) =>
        string.Equals(document, TermsDocument, StringComparison.Ordinal) || AdditionalDocuments.Contains(document, StringComparer.Ordinal);

    /// <summary>Whether the sign-up requires a first and a last name. Defaults to false.</summary>
    public bool RequireNames { get; set; }

    /// <summary>
    /// Whether the sign-up accepts a user name. Without one the account gets a generated one. Defaults to false.
    /// </summary>
    public bool AllowUserName { get; set; }

    /// <summary>
    /// Whether every sign-up must send a user name. A missing or blank one is a <c>required</c> field error on <c>UserName</c>.
    /// It also makes the sign-up accept a user name, so <see cref="AllowUserName"/> need not be set. Defaults to false.
    /// </summary>
    public bool RequireUserName { get; set; }

    /// <summary>
    /// The fewest characters of a user name, counted after trimming. A shorter one is a <c>tooShort</c> field error with the
    /// minimum in <c>limits</c>. It applies when <see cref="AllowUserName"/> or <see cref="RequireUserName"/> is on. Must be at
    /// least 1 and at most <see cref="MaxUserNameLength"/>. Defaults to 1.
    /// </summary>
    public int MinUserNameLength { get; set; } = 1;

    /// <summary>
    /// The most characters of a user name, counted after trimming. A longer one is a <c>tooLong</c> field error with the maximum
    /// in <c>limits</c>. It applies when <see cref="AllowUserName"/> or <see cref="RequireUserName"/> is on. Defaults to 50, or to
    /// the narrower of the <c>UserName</c> and <c>NormalizedUserName</c> columns in the EF model when that is under 50. A value the
    /// app sets is kept as it is, and the app fails to start when it is longer than either column, 256 in the Identity default.
    /// The check reads the length that <c>HasMaxLength</c> sets in the model, not a <c>HasColumnType</c> override, so an app that
    /// narrows a column through its column type must also set <c>HasMaxLength</c> to the same length.
    /// </summary>
    public int MaxUserNameLength
    {
        get => _maxUserNameLength ?? DefaultMaxUserNameLength;
        set => _maxUserNameLength = value;
    }

    private int? _maxUserNameLength;

    internal const int DefaultMaxUserNameLength = 50;

    // The maximum the sign-up applies, given the narrowest user name column of the model. A maximum the app set is never lowered,
    // and the start fails instead when it does not fit.
    internal int EffectiveMaxUserNameLength(int? column) =>
        _maxUserNameLength is null && column is { } width ? Math.Min(DefaultMaxUserNameLength, width) : MaxUserNameLength;

    internal bool MaxUserNameLengthIsSet => _maxUserNameLength is not null;

    // A user name is read from the request when the app allows or requires one.
    internal bool TakesUserName => AllowUserName || RequireUserName;

    /// <summary>Whether to store a shortened IP address with each consent. Defaults to false.</summary>
    public bool RecordTruncatedIp { get; set; }

    /// <summary>How long a person waits before a new verification code may be sent. Defaults to 1 minute.</summary>
    public TimeSpan ResendCooldown { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The most emails per address and UTC day that anyone who types the address can cause: the notice to an existing
    /// account and the verification email that register sends for a new account. Addresses are compared by mailbox, as a best effort. A <c>+tag</c>, a trailing dot on the domain, an
    /// internationalised domain written in Unicode or ASCII, <c>googlemail.com</c> and, for Gmail, dots in the local part do not
    /// make a new mailbox. No other provider rule applies. Over the cap nothing is sent and the answers do not change.
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

    /// <summary>
    /// The most sign-ups per network and UTC day. The network is the caller's address cut to its first three parts for IPv4 and
    /// its first 48 bits for IPv6, and kept only as a hash. Every register request that passes the checks counts, whether or not
    /// its email has an account. Over the cap register answers 429 <c>tooManyRequests</c> and does nothing, so no account is made
    /// and no email is sent. The count comes before the lookup by email, so the answer never depends on the email. An app behind a proxy must set up forwarded headers, as the rate limiter needs, or every caller
    /// shares the address of the proxy. Shared networks, such as mobile carriers, can reach the cap, so an app may raise it.
    /// Defaults to 20.
    /// </summary>
    public int MaxSignUpsPerNetworkPerDay { get; set; } = 20;

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
