using System.Buffers;
using System.Net.Mail;

namespace SjolystInnovation.AppKit.Email;

/// <summary>Checks for an address that a user typed, before the app uses it as a recipient or a reply-to.</summary>
public static class EmailAddress
{
    // List separators and header characters, plus the quoted local parts, backslash escapes and domain
    // literals that let one mailbox be spelled many ways.
    private static readonly SearchValues<char> Forbidden = SearchValues.Create([',', ';', '<', '>', '\r', '\n', '\0', '"', '\\', '[', ']']);

    /// <summary>
    /// True when <paramref name="value"/> is one plain address and nothing else, so it cannot add recipients
    /// or a display name to the emails it is used in. Surrounding spaces are allowed. A list separator, a display
    /// name, angle brackets, quotes, a backslash, a domain literal, a line break, a NUL character, or a trailing dot makes it false.
    /// Plus addressing is fine.
    /// </summary>
    /// <param name="value">The address as typed. May be null.</param>
    public static bool IsSingle(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || value!.IndexOfAny(Forbidden) >= 0 || trimmed.EndsWith('.')) return false;
        try
        {
            return new MailAddress(trimmed).Address == trimmed;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
