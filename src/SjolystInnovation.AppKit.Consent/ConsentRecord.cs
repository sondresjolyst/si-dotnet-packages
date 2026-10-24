namespace SjolystInnovation.AppKit.Consent;

/// <summary>
/// One cookie consent choice, as a visitor made it. Every choice adds a row, so the rows for one
/// <see cref="ConsentId"/> are the history of that browser's choices. The package never changes a row.
/// It deletes a row only when the row is older than <see cref="AppKitConsentOptions.KeepFor"/>.
/// </summary>
/// <remarks>
/// The log is pseudonymous. A row holds no IP address and no user agent. It is tied to a person only
/// through <see cref="UserId"/>, which is set when the visitor was signed in.
/// </remarks>
public class ConsentRecord
{
    /// <summary>The row id.</summary>
    public long Id { get; set; }

    /// <summary>
    /// The random id the browser made for itself and keeps in its stored consent record. It links the
    /// choices of one browser and says nothing else about the visitor.
    /// </summary>
    public Guid ConsentId { get; set; }

    /// <summary>Whether the visitor allowed marketing cookies.</summary>
    public bool Marketing { get; set; }

    /// <summary>The version of the cookie notice the visitor answered, as the app names it, for example <c>2026-10-24</c>.</summary>
    public required string NoticeVersion { get; set; }

    /// <summary>The version of the browser's stored consent record.</summary>
    public int SchemaVersion { get; set; }

    /// <summary>When the server received the choice, in UTC. The package sets it. A time sent by the browser is never used.</summary>
    public DateTime DecidedAt { get; set; }

    /// <summary>The id of the signed-in user who made the choice, from the name identifier claim. Null for a visitor who was not signed in.</summary>
    public string? UserId { get; set; }

    /// <summary>Where the choice was made, one of <see cref="ConsentSources"/>.</summary>
    public required string Source { get; set; }
}

/// <summary>The places a consent choice can be made.</summary>
public static class ConsentSources
{
    /// <summary>The consent banner, shown to a visitor who has not chosen yet.</summary>
    public const string Banner = "banner";

    /// <summary>The cookie settings, opened again after a choice.</summary>
    public const string Settings = "settings";

    /// <summary>Every allowed source.</summary>
    public static IReadOnlyList<string> All { get; } = [Banner, Settings];
}
