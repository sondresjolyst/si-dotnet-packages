namespace SjolystInnovation.AppKit.Vipps;

/// <summary>The webhook event names, for registering a webhook and telling events apart.</summary>
public static class VippsEvents
{
    /// <summary>An ePayment was created.</summary>
    public const string PaymentCreated = "epayments.payment.created.v1";
    /// <summary>The user authorized an ePayment.</summary>
    public const string PaymentAuthorized = "epayments.payment.authorized.v1";
    /// <summary>An ePayment was captured.</summary>
    public const string PaymentCaptured = "epayments.payment.captured.v1";
    /// <summary>An ePayment was refunded.</summary>
    public const string PaymentRefunded = "epayments.payment.refunded.v1";
    /// <summary>An ePayment was canceled.</summary>
    public const string PaymentCancelled = "epayments.payment.cancelled.v1";
    /// <summary>The user aborted an ePayment.</summary>
    public const string PaymentAborted = "epayments.payment.aborted.v1";
    /// <summary>An ePayment expired before the user acted.</summary>
    public const string PaymentExpired = "epayments.payment.expired.v1";
    /// <summary>An ePayment was terminated.</summary>
    public const string PaymentTerminated = "epayments.payment.terminated.v1";

    /// <summary>The user approved a recurring agreement.</summary>
    public const string AgreementActivated = "recurring.agreement-activated.v1";
    /// <summary>A recurring agreement was stopped.</summary>
    public const string AgreementStopped = "recurring.agreement-stopped.v1";
    /// <summary>A recurring agreement expired before the user approved it.</summary>
    public const string AgreementExpired = "recurring.agreement-expired.v1";
    /// <summary>The user rejected a recurring agreement.</summary>
    public const string AgreementRejected = "recurring.agreement-rejected.v1";
    /// <summary>A recurring charge was captured.</summary>
    public const string ChargeCaptured = "recurring.charge-captured.v1";
    /// <summary>A recurring charge failed.</summary>
    public const string ChargeFailed = "recurring.charge-failed.v1";
    /// <summary>Vipps could not create a recurring charge.</summary>
    public const string ChargeCreationFailed = "recurring.charge-creation-failed.v1";

    /// <summary>The ePayment events a shop needs. That is every ePayment event except created.</summary>
    public static readonly IReadOnlyList<string> Payments =
        [PaymentAuthorized, PaymentCaptured, PaymentRefunded, PaymentTerminated, PaymentAborted, PaymentExpired, PaymentCancelled];

    /// <summary>The Recurring events a subscription needs, for agreements and charges.</summary>
    public static readonly IReadOnlyList<string> Recurring =
        [AgreementActivated, AgreementStopped, AgreementExpired, AgreementRejected, ChargeCaptured, ChargeFailed, ChargeCreationFailed];
}
