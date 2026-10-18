using System.Text.Json.Serialization;

namespace SjolystInnovation.AppKit.Vipps;

/// <summary>How often a recurring agreement charges.</summary>
public enum VippsInterval
{
    /// <summary>Charges every month.</summary>
    Month,
    /// <summary>Charges every year.</summary>
    Year,
}

/// <summary>
/// A request for a recurring agreement with variable pricing. The user approves a ceiling, and each
/// charge may be at most that ceiling. The name and description are cut to the lengths Vipps accepts.
/// </summary>
public sealed record VippsCreateAgreementRequest
{
    /// <summary>The product name the user sees.</summary>
    public required string ProductName { get; init; }
    /// <summary>The product description the user sees. Optional.</summary>
    public string? ProductDescription { get; init; }
    /// <summary>The ceiling the user approves, between 1 øre and NOK 20 000.</summary>
    public required int SuggestedMaxAmountInOre { get; init; }
    /// <summary>The first charge in øre, captured when the user approves.</summary>
    public required int InitialChargeInOre { get; init; }
    /// <summary>The description of the first charge.</summary>
    public required string InitialChargeDescription { get; init; }
    /// <summary>The unit of the charge interval.</summary>
    public required VippsInterval Interval { get; init; }
    /// <summary>How many interval units pass between charges. Defaults to 1.</summary>
    public int IntervalCount { get; init; } = 1;
    /// <summary>The page Vipps sends the user to after they approve or reject.</summary>
    public required string MerchantRedirectUrl { get; init; }
    /// <summary>The page with the agreement terms.</summary>
    public required string MerchantAgreementUrl { get; init; }
    /// <summary>The user's phone number, to prefill in Vipps.</summary>
    public required string PhoneNumber { get; init; }
    /// <summary>The profile data to ask for. Defaults to <c>name address email phoneNumber</c>.</summary>
    public string Scope { get; init; } = "name address email phoneNumber";
}

/// <summary>The response to a new agreement.</summary>
public sealed record VippsCreateAgreementResponse
{
    /// <summary>The Vipps agreement id.</summary>
    public string AgreementId { get; init; } = string.Empty;
    /// <summary>The URL where the user approves the agreement.</summary>
    public string VippsConfirmationUrl { get; init; } = string.Empty;
    /// <summary>Whether the agreement was created in the test environment.</summary>
    public bool IsTest { get; init; }
}

/// <summary>A recurring agreement as Vipps reports it.</summary>
public sealed record VippsAgreement
{
    /// <summary>The Vipps agreement id.</summary>
    public string Id { get; init; } = string.Empty;
    /// <summary>The agreement status, for example <c>ACTIVE</c>.</summary>
    public string Status { get; init; } = string.Empty;
    /// <summary>The product name.</summary>
    public string ProductName { get; init; } = string.Empty;
    /// <summary>When the agreement started. A time without an offset counts as UTC.</summary>
    [JsonConverter(typeof(VippsUtcDateTimeOffsetConverter))]
    public DateTimeOffset? Start { get; init; }
    /// <summary>When the agreement stopped. A time without an offset counts as UTC.</summary>
    [JsonConverter(typeof(VippsUtcDateTimeOffsetConverter))]
    public DateTimeOffset? Stop { get; init; }
    /// <summary>The user's profile id, once they have approved.</summary>
    public string? Sub { get; init; }
}

/// <summary>A request for a recurring charge. A due date sooner than Vipps allows moves to the earliest date it accepts.</summary>
public sealed record VippsCreateChargeRequest
{
    /// <summary>The amount in øre.</summary>
    public required int AmountInOre { get; init; }
    /// <summary>The date the charge falls due. Vipps receives it as <c>yyyy-MM-dd</c>.</summary>
    public required DateOnly Due { get; init; }
    /// <summary>The description the user sees.</summary>
    public required string Description { get; init; }
    /// <summary>The order id, which becomes the charge id. At most 50 characters are sent.</summary>
    public required string OrderId { get; init; }
    /// <summary>How many days Vipps retries a failed charge. Defaults to 5.</summary>
    public int RetryDays { get; init; } = 5;
}

/// <summary>The response to a new charge.</summary>
public sealed record VippsCreateChargeResponse
{
    /// <summary>The Vipps charge id.</summary>
    public string ChargeId { get; init; } = string.Empty;
}

/// <summary>One line of a payment receipt. The VAT is split out of the line total.</summary>
public sealed record VippsReceiptLine
{
    /// <summary>The line id.</summary>
    public required string Id { get; init; }
    /// <summary>The line name.</summary>
    public required string Name { get; init; }
    /// <summary>The price of one unit in øre, including VAT.</summary>
    public required int UnitPriceInOre { get; init; }
    /// <summary>The number of units.</summary>
    public required int Quantity { get; init; }
    /// <summary>The VAT rate in basis points, for example 2500 for 25 %.</summary>
    public int TaxRateBasisPoints { get; init; }
}

/// <summary>A request for an ePayment that is reserved now and captured later, with a receipt.</summary>
public sealed record VippsCreatePaymentRequest
{
    /// <summary>The merchant's own reference, unique per payment.</summary>
    public required string Reference { get; init; }
    /// <summary>The amount in øre.</summary>
    public required int AmountInOre { get; init; }
    /// <summary>The user's phone number, to prefill in Vipps.</summary>
    public required string PhoneNumber { get; init; }
    /// <summary>The page Vipps sends the user to afterwards.</summary>
    public required string ReturnUrl { get; init; }
    /// <summary>The payment description the user sees.</summary>
    public required string Description { get; init; }
    /// <summary>The receipt lines.</summary>
    public required IReadOnlyList<VippsReceiptLine> ReceiptLines { get; init; }
    /// <summary>The profile data to ask for. Defaults to <c>name address email phoneNumber</c>. Set it to <c>null</c> or empty to send no profile, so the user sees no consent screen.</summary>
    public string? Scope { get; init; } = "name address email phoneNumber";
}

/// <summary>The response to a new payment.</summary>
public sealed record VippsCreatePaymentResponse
{
    /// <summary>The URL to send the user to.</summary>
    public string RedirectUrl { get; init; } = string.Empty;
    /// <summary>The merchant's reference for the payment.</summary>
    public string Reference { get; init; } = string.Empty;
    /// <summary>Whether the payment was created in the test environment.</summary>
    public bool IsTest { get; init; }
}

/// <summary>An ePayment as Vipps reports it.</summary>
public sealed record VippsPayment
{
    /// <summary>The merchant's reference for the payment.</summary>
    public string Reference { get; init; } = string.Empty;
    /// <summary>The payment state, for example <c>AUTHORIZED</c>.</summary>
    public string State { get; init; } = string.Empty;
    /// <summary>The user's profile id, when they shared a profile.</summary>
    public string? ProfileSub { get; init; }
    /// <summary>The captured amount in øre.</summary>
    public int CapturedAmountInOre { get; init; }
    /// <summary>The refunded amount in øre.</summary>
    public int RefundedAmountInOre { get; init; }
    /// <summary>The canceled amount in øre.</summary>
    public int CancelledAmountInOre { get; init; }
    /// <summary>The last moment a capture of the reservation is guaranteed. A time without an offset counts as UTC. Null when Vipps does not send it.</summary>
    [JsonConverter(typeof(VippsUtcDateTimeOffsetConverter))]
    public DateTimeOffset? CaptureGuaranteedUntil { get; init; }
}

/// <summary>An address from a Vipps profile.</summary>
public sealed record VippsAddress
{
    /// <summary>The street address.</summary>
    public string? StreetAddress { get; init; }
    /// <summary>The postal code.</summary>
    public string? PostalCode { get; init; }
    /// <summary>The place name, such as the town. Vipps calls it the region.</summary>
    public string? Region { get; init; }
    /// <summary>The country code.</summary>
    public string? Country { get; init; }
    /// <summary>The whole address as Vipps formatted it.</summary>
    public string? Formatted { get; init; }
}

/// <summary>The profile data a user shared.</summary>
public sealed record VippsUserInfo
{
    /// <summary>The user's name.</summary>
    public string? Name { get; init; }
    /// <summary>The user's email address.</summary>
    public string? Email { get; init; }
    /// <summary>The user's phone number.</summary>
    public string? PhoneNumber { get; init; }
    /// <summary>The user's address.</summary>
    public VippsAddress? Address { get; init; }
}

/// <summary>A registered webhook.</summary>
public sealed record VippsWebhook
{
    /// <summary>The webhook id.</summary>
    public string Id { get; init; } = string.Empty;
    /// <summary>The URL Vipps calls.</summary>
    public string Url { get; init; } = string.Empty;
}

/// <summary>A newly registered webhook, with the secret that signs its requests.</summary>
/// <param name="Id">The webhook id at Vipps.</param>
/// <param name="Secret">The secret that signs the webhook's requests. Vipps returns it only once.</param>
public sealed record VippsRegisteredWebhook(string Id, string Secret);
