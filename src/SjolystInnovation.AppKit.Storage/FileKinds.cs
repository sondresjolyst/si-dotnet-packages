namespace SjolystInnovation.AppKit.Storage;

/// <summary>
/// The kinds of file a store takes. A store recognizes each kind by the file's first bytes, never by the
/// content type the client sends.
/// </summary>
[Flags]
public enum AppKitFileKinds
{
    /// <summary>No kind. A store with no allowed kinds refuses to start.</summary>
    None = 0,
    /// <summary>A PNG image, stored as <c>.png</c>.</summary>
    Png = 1,
    /// <summary>A JPEG image, stored as <c>.jpg</c>.</summary>
    Jpeg = 2,
    /// <summary>A WebP image, stored as <c>.webp</c>.</summary>
    WebP = 4,
    /// <summary>A GIF image, stored as <c>.gif</c>.</summary>
    Gif = 8,
    /// <summary>An AVIF image, stored as <c>.avif</c>.</summary>
    Avif = 16,
    /// <summary>A PDF document, stored as <c>.pdf</c>.</summary>
    Pdf = 32,
    /// <summary>An ICO icon, stored as <c>.ico</c>.</summary>
    Ico = 64,
    /// <summary>The photo formats: PNG, JPEG, WebP, GIF, and AVIF.</summary>
    Images = Png | Jpeg | WebP | Gif | Avif,
}

/// <summary>Detects a file's kind from its first bytes.</summary>
internal static class FileSignature
{
    /// <summary>The number of leading bytes that <see cref="Detect"/> needs to see.</summary>
    public const int HeaderLength = 64;

    private static readonly (AppKitFileKinds Kind, string Name, string ContentType, string Extension)[] Kinds =
    [
        (AppKitFileKinds.Png, "PNG", "image/png", ".png"),
        (AppKitFileKinds.Jpeg, "JPEG", "image/jpeg", ".jpg"),
        (AppKitFileKinds.WebP, "WebP", "image/webp", ".webp"),
        (AppKitFileKinds.Gif, "GIF", "image/gif", ".gif"),
        (AppKitFileKinds.Avif, "AVIF", "image/avif", ".avif"),
        (AppKitFileKinds.Pdf, "PDF", "application/pdf", ".pdf"),
        (AppKitFileKinds.Ico, "ICO", "image/x-icon", ".ico"),
    ];

    /// <summary>Finds the kind that <paramref name="header"/> starts with. Returns <see cref="AppKitFileKinds.None"/> for anything else.</summary>
    public static AppKitFileKinds Detect(ReadOnlySpan<byte> header) =>
        header.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]) ? AppKitFileKinds.Png
        : header.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]) ? AppKitFileKinds.Jpeg
        : header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8) ? AppKitFileKinds.WebP
        : header.StartsWith("GIF87a"u8) || header.StartsWith("GIF89a"u8) ? AppKitFileKinds.Gif
        : IsAvif(header) ? AppKitFileKinds.Avif
        : header.StartsWith("%PDF-"u8) ? AppKitFileKinds.Pdf
        : header.Length >= 6 && header.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0x01, 0x00]) && (header[4] | header[5]) != 0 ? AppKitFileKinds.Ico
        : AppKitFileKinds.None;

    /// <summary>
    /// Checks for an ISO media <c>ftyp</c> box whose major brand or one of whose compatible brands is
    /// <c>avif</c> or <c>avis</c>.
    /// </summary>
    private static bool IsAvif(ReadOnlySpan<byte> header)
    {
        if (header.Length < 12 || !header[4..8].SequenceEqual("ftyp"u8))
            return false;
        var boxLength = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
        var end = Math.Min(boxLength, header.Length);
        for (var i = 8; i + 4 <= end; i += 4)
        {
            // Offset 12 holds the minor version, not a brand.
            if (i == 12)
                continue;
            var brand = header.Slice(i, 4);
            if (brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8))
                return true;
        }
        return false;
    }

    public static string ContentType(AppKitFileKinds kind) => Find(kind).ContentType;

    public static string Extension(AppKitFileKinds kind) => Find(kind).Extension;

    /// <summary>Lists the allowed kinds for a refusal message, for example <c>PNG, JPEG, or GIF</c>.</summary>
    public static string Describe(AppKitFileKinds allowed)
    {
        var names = Kinds.Where(k => allowed.HasFlag(k.Kind)).Select(k => k.Name).ToList();
        return names.Count switch
        {
            0 => "no",
            1 => names[0],
            2 => $"{names[0]} or {names[1]}",
            _ => $"{string.Join(", ", names[..^1])}, or {names[^1]}",
        };
    }

    private static (AppKitFileKinds Kind, string Name, string ContentType, string Extension) Find(AppKitFileKinds kind) =>
        Kinds.Single(k => k.Kind == kind);
}
