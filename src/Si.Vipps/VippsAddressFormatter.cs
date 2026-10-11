namespace Si.Vipps;

/// <summary>A Vipps address on one line, for an invoice or an order.</summary>
public static class VippsAddressFormatter
{
    public const int DefaultMaxLength = 500;

    /// <summary>
    /// The address Vipps formatted, or the parts joined with commas when it sent none. Null when
    /// there is nothing to show. Cut to <paramref name="maxLength"/> characters.
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
