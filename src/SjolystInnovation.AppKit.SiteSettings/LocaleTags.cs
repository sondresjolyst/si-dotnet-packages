namespace SjolystInnovation.AppKit.SiteSettings;

/// <summary>Matches a requested language tag to one of an app's supported locales.</summary>
internal static class LocaleTags
{
    /// <summary>
    /// Finds the supported locale a tag asks for. It tries an exact match first, then the tag's primary
    /// language, with <c>nb</c> and <c>nn</c> read as <c>no</c>. Anything else, or no tag, gets
    /// <paramref name="fallback"/>. The result is in lower case.
    /// </summary>
    public static string Match(IReadOnlyList<string> locales, string fallback, string? locale)
    {
        var tag = locale?.Trim() ?? "";
        var primary = tag.Split('-')[0];
        if (primary.Equals("nb", StringComparison.OrdinalIgnoreCase) || primary.Equals("nn", StringComparison.OrdinalIgnoreCase))
            primary = "no";
        return (locales.FirstOrDefault(l => l.Equals(tag, StringComparison.OrdinalIgnoreCase))
            ?? locales.FirstOrDefault(l => l.Equals(primary, StringComparison.OrdinalIgnoreCase))
            ?? fallback).ToLowerInvariant();
    }
}
