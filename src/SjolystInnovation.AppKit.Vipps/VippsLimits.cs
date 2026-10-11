namespace SjolystInnovation.AppKit.Vipps;

/// <summary>Limits the Vipps APIs set on request fields.</summary>
public static class VippsLimits
{
    /// <summary>The largest recurring agreement amount, NOK 20 000, in øre.</summary>
    public const int MaxAgreementAmountInOre = 2_000_000;
    /// <summary>The longest recurring <c>productName</c>.</summary>
    public const int ProductNameLength = 45;
    /// <summary>The longest recurring <c>productDescription</c>.</summary>
    public const int ProductDescriptionLength = 100;
    /// <summary>The longest recurring charge <c>orderId</c>.</summary>
    public const int ChargeOrderIdLength = 50;
    /// <summary>The longest <c>Vipps-System-Name</c> and <c>Vipps-System-Version</c>.</summary>
    public const int SystemHeaderLength = 30;
    /// <summary>The earliest a recurring charge may fall due, counted in days from today.</summary>
    public const int MinChargeDueDays = 2;

    /// <summary>The value cut to at most <paramref name="max"/> characters, never between the halves of a surrogate pair.</summary>
    public static string Truncate(string value, int max)
    {
        if (value.Length <= max) return value;
        var cut = char.IsHighSurrogate(value[max - 1]) ? max - 1 : max;
        return value[..cut];
    }

    /// <summary>A system header value: build metadata after a <c>+</c>, such as a commit hash, left out, and at most 30 characters.</summary>
    public static string SystemHeader(string value)
    {
        var plus = value.IndexOf('+');
        if (plus >= 0) value = value[..plus];
        return value.Length <= SystemHeaderLength ? value : value[..SystemHeaderLength];
    }
}
