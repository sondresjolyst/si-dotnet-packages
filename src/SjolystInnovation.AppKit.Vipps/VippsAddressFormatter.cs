namespace SjolystInnovation.AppKit.Vipps;

/// <summary>Formats a Vipps address on one line, for an invoice or an order.</summary>
public static class VippsAddressFormatter
{
    /// <summary>The default maximum length of the formatted address.</summary>
    public const int DefaultMaxLength = 500;

    /// <summary>
    /// Returns the address Vipps formatted, or the parts joined with commas when Vipps sent none.
    /// Returns null when there is nothing to show. Cuts the result to <paramref name="maxLength"/> characters.
    /// </summary>
    public static string? Format(VippsAddress? address, int maxLength = DefaultMaxLength)
    {
        if (address == null) return null;
        var formatted = !string.IsNullOrEmpty(address.Formatted)
            ? address.Formatted
            : string.Join(", ", new[] { address.StreetAddress, address.PostalCode, address.Region, address.Country }.Where(s => !string.IsNullOrEmpty(s)));
        if (string.IsNullOrEmpty(formatted)) return null;
        return formatted.Length > maxLength ? formatted[..maxLength] : formatted;
    }
}
