namespace SjolystInnovation.AppKit.Vipps;

/// <summary>The webhook event names, for registering a webhook and telling events apart.</summary>
public static class VippsEvents
{
    public const string PaymentCreated = "epayments.payment.created.v1";
    public const string PaymentAuthorized = "epayments.payment.authorized.v1";
    public const string PaymentCaptured = "epayments.payment.captured.v1";
    public const string PaymentRefunded = "epayments.payment.refunded.v1";
    public const string PaymentCancelled = "epayments.payment.cancelled.v1";
    public const string PaymentAborted = "epayments.payment.aborted.v1";
    public const string PaymentExpired = "epayments.payment.expired.v1";
    public const string PaymentTerminated = "epayments.payment.terminated.v1";

    public const string AgreementActivated = "recurring.agreement-activated.v1";
    public const string AgreementStopped = "recurring.agreement-stopped.v1";
    public const string AgreementExpired = "recurring.agreement-expired.v1";
    public const string AgreementRejected = "recurring.agreement-rejected.v1";
    public const string ChargeCaptured = "recurring.charge-captured.v1";
    public const string ChargeFailed = "recurring.charge-failed.v1";
    public const string ChargeCreationFailed = "recurring.charge-creation-failed.v1";

    /// <summary>The ePayment events a shop needs, from authorized to terminated.</summary>
    public static readonly IReadOnlyList<string> Payments =
        [PaymentAuthorized, PaymentCaptured, PaymentRefunded, PaymentTerminated, PaymentAborted, PaymentExpired, PaymentCancelled];

    /// <summary>The Recurring events a subscription needs, for agreements and charges.</summary>
    public static readonly IReadOnlyList<string> Recurring =
        [AgreementActivated, AgreementStopped, AgreementExpired, AgreementRejected, ChargeCaptured, ChargeFailed, ChargeCreationFailed];
}
