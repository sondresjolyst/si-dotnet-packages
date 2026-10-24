namespace SjolystInnovation.AppKit.Consent;

/// <summary>
/// One cookie consent choice as the site's consent banner sent it, with the time the server received it.
/// Every request adds a row, so the rows for one <see cref="ConsentId"/> are the choices sent for that
/// browser. The package never changes a row. It deletes a row only when the row is older than
/// <see cref="AppKitConsentOptions.KeepFor"/>.
/// </summary>
/// <remarks>
/// <para>
/// The log helps document consent. It records what was sent and when. It does not prove who made the
/// choice, because the endpoint is open and any client can send to it.
/// </para>
/// <para>
/// The log is pseudonymous. A row holds no IP address and no user agent. It is tied to an account only
/// through <see cref="UserId"/>, which is set when the request was signed in.
/// </para>
/// </remarks>
public class ConsentRecord
{
    /// <summary>The row id.</summary>
    public long Id { get; init; }

    /// <summary>
    /// The random id the browser made for itself and keeps in its stored consent record. It links the
    /// choices sent for one browser and says nothing else about the visitor.
    /// </summary>
    public Guid ConsentId { get; init; }

    /// <summary>Whether the choice sent allowed marketing cookies.</summary>
    public bool Marketing { get; init; }

    /// <summary>The version of the cookie notice the choice answered, as the app names it, for example <c>2026-10-24</c>.</summary>
    public required string NoticeVersion { get; init; }

    /// <summary>The version of the browser's stored consent record.</summary>
    public int SchemaVersion { get; init; }

    /// <summary>When the server received the choice, in UTC. The package sets it. A time sent by the client is never used.</summary>
    public DateTime DecidedAt { get; init; }

    /// <summary>
    /// The id of the signed-in user who sent the choice, from the name identifier claim. Null when the
    /// request was not signed in, or when the id is longer than 450 characters.
    /// </summary>
    public string? UserId { get; init; }

    /// <summary>Where the choice was sent from, one of <see cref="ConsentSources"/>.</summary>
    public required string Source { get; init; }
}

/// <summary>The places a consent choice can be sent from.</summary>
public static class ConsentSources
{
    /// <summary>The consent banner, shown to a visitor who has not chosen yet.</summary>
    public const string Banner = "banner";

    /// <summary>The cookie settings, opened again after a choice.</summary>
    public const string Settings = "settings";

    /// <summary>Every allowed source.</summary>
    public static IReadOnlyList<string> All { get; } = [Banner, Settings];
}
