namespace SjolystInnovation.AppKit.Vipps;

/// <summary>How often a recurring agreement charges.</summary>
public enum VippsInterval
{
    Month,
    Year,
}

/// <summary>
/// A recurring agreement with variable pricing: the user approves a ceiling, and each charge may be
/// at most that ceiling. The name and description are cut to the lengths Vipps accepts.
/// </summary>
public sealed record VippsCreateAgreementRequest
{
    public required string ProductName { get; init; }
    public string? ProductDescription { get; init; }
    /// <summary>The ceiling the user approves, between 1 øre and NOK 20 000.</summary>
    public required int SuggestedMaxAmountInOre { get; init; }
    /// <summary>The first charge, captured when the user approves.</summary>
    public required int InitialChargeInOre { get; init; }
    public required string InitialChargeDescription { get; init; }
    public required VippsInterval Interval { get; init; }
    public int IntervalCount { get; init; } = 1;
    /// <summary>Where Vipps sends the user after approving or rejecting.</summary>
    public required string MerchantRedirectUrl { get; init; }
    /// <summary>The page with the agreement terms.</summary>
    public required string MerchantAgreementUrl { get; init; }
    public required string PhoneNumber { get; init; }
    /// <summary>The profile data asked for, such as <c>name address email phoneNumber</c>.</summary>
    public string Scope { get; init; } = "name address email phoneNumber";
}

public sealed record VippsCreateAgreementResponse
{
    public string AgreementId { get; init; } = string.Empty;
    public string VippsConfirmationUrl { get; init; } = string.Empty;
    /// <summary>Whether it was created in the test environment.</summary>
    public bool IsTest { get; init; }
}

public sealed record VippsAgreement
{
    public string Id { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string ProductName { get; init; } = string.Empty;
    public DateTime? Start { get; init; }
    public DateTime? Stop { get; init; }
    /// <summary>The user's profile id, once they have approved.</summary>
    public string? Sub { get; init; }
}

/// <summary>A recurring charge. A due date sooner than Vipps allows is moved to the earliest it accepts.</summary>
public sealed record VippsCreateChargeRequest
{
    public required int AmountInOre { get; init; }
    public required DateTime Due { get; init; }
    public required string Description { get; init; }
    /// <summary>Becomes the charge id. At most 50 characters are sent.</summary>
    public required string OrderId { get; init; }
    /// <summary>How many days Vipps retries a failed charge.</summary>
    public int RetryDays { get; init; } = 5;
}

public sealed record VippsCreateChargeResponse
{
    public string ChargeId { get; init; } = string.Empty;
}

/// <summary>One line of a payment receipt. The VAT is split out of the line total.</summary>
public sealed record VippsReceiptLine
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>The price of one unit, VAT included.</summary>
    public required int UnitPriceInOre { get; init; }
    public required int Quantity { get; init; }
    /// <summary>The VAT rate in basis points, such as 2500 for 25 %.</summary>
    public int TaxRateBasisPoints { get; init; }
}

/// <summary>An ePayment, reserved and captured later, with a receipt.</summary>
public sealed record VippsCreatePaymentRequest
{
    /// <summary>The merchant's own reference, unique per payment.</summary>
    public required string Reference { get; init; }
    public required int AmountInOre { get; init; }
    public required string PhoneNumber { get; init; }
    /// <summary>Where Vipps sends the user afterwards.</summary>
    public required string ReturnUrl { get; init; }
    public required string Description { get; init; }
    public required IReadOnlyList<VippsReceiptLine> ReceiptLines { get; init; }
    public string Scope { get; init; } = "name address email phoneNumber";
}

public sealed record VippsCreatePaymentResponse
{
    public string RedirectUrl { get; init; } = string.Empty;
    public string Reference { get; init; } = string.Empty;
    public bool IsTest { get; init; }
}

public sealed record VippsPayment
{
    public string Reference { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string? ProfileSub { get; init; }
    public int CapturedAmountInOre { get; init; }
    public int RefundedAmountInOre { get; init; }
    public int CancelledAmountInOre { get; init; }
}

public sealed record VippsAddress
{
    public string? StreetAddress { get; init; }
    public string? PostalCode { get; init; }
    public string? Region { get; init; }
    public string? Country { get; init; }
    public string? Formatted { get; init; }
}

public sealed record VippsUserInfo
{
    public string? Name { get; init; }
    public string? Email { get; init; }
    public string? PhoneNumber { get; init; }
    public VippsAddress? Address { get; init; }
}

public sealed record VippsWebhook
{
    public string Id { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
}

public sealed record VippsRegisteredWebhook(string Id, string Secret);
