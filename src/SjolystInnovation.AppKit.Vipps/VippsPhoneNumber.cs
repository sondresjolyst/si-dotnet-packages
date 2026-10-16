namespace SjolystInnovation.AppKit.Vipps;

/// <summary>Checks Norwegian phone numbers in the form Vipps asks for.</summary>
public static class VippsPhoneNumber
{
    /// <summary>
    /// Turns a Norwegian mobile number into the digits Vipps expects, for example <c>4791234567</c>. Spaces,
    /// dashes, brackets, and a leading <c>+</c> are dropped, and an eight-digit number gets the <c>47</c>
    /// country code.
    /// </summary>
    /// <param name="raw">The number as the user typed it.</param>
    /// <param name="msisdn">The normalized number, or an empty string when the check fails.</param>
    /// <returns>True when the number is a Norwegian number of eight digits after the country code.</returns>
    public static bool TryNormalize(string? raw, out string msisdn)
    {
        msisdn = string.Empty;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var digits = new string(raw.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length == 8)
            digits = "47" + digits;
        if (digits.Length != 10 || !digits.StartsWith("47", StringComparison.Ordinal))
            return false;

        msisdn = digits;
        return true;
    }
}
