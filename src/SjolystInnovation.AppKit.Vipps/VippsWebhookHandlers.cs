using System.Text.Json.Serialization;

namespace SjolystInnovation.AppKit.Vipps;

/// <summary>An amount in a webhook event.</summary>
public sealed record VippsWebhookAmount
{
    /// <summary>The amount in øre.</summary>
    public int Value { get; init; }
    /// <summary>The currency, for example <c>NOK</c>.</summary>
    public string Currency { get; init; } = string.Empty;
}

/// <summary>An ePayment webhook event.</summary>
public sealed record VippsPaymentEvent
{
    /// <summary>The merchant serial number of the sales unit.</summary>
    public string? Msn { get; init; }
    /// <summary>The merchant's reference for the payment.</summary>
    public string Reference { get; init; } = string.Empty;
    /// <summary>The reference of this payment operation. Each operation has its own.</summary>
    public string PspReference { get; init; } = string.Empty;
    /// <summary>The event name, for example <c>AUTHORIZED</c> or <c>CAPTURED</c>.</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>The amount of the operation.</summary>
    public VippsWebhookAmount? Amount { get; init; }
    /// <summary>When the operation happened.</summary>
    public DateTimeOffset? Timestamp { get; init; }
    /// <summary>The idempotency key of the request behind the operation. Some events leave it out.</summary>
    public string? IdempotencyKey { get; init; }
    /// <summary>Whether the operation behind the event succeeded.</summary>
    public bool Success { get; init; }
    /// <summary>The user's profile id, on an <c>AUTHORIZED</c> event when the payment asked for a profile.</summary>
    public string? Sub { get; init; }
}

/// <summary>A Recurring agreement or charge webhook event.</summary>
public sealed record VippsRecurringEvent
{
    /// <summary>The merchant serial number. Vipps lists it among the properties a Recurring payload may include.</summary>
    public string? Msn { get; init; }
    /// <summary>The agreement id.</summary>
    public string AgreementId { get; init; } = string.Empty;
    /// <summary>An event id. Vipps does not document one for Recurring events, so it is usually empty.</summary>
    public string? EventId { get; init; }
    /// <summary>The event name, for example <see cref="VippsEvents.AgreementActivated"/>.</summary>
    public string EventType { get; init; } = string.Empty;
    /// <summary>When the change happened. A time without an offset counts as UTC.</summary>
    [JsonConverter(typeof(VippsUtcDateTimeOffsetConverter))]
    public DateTimeOffset? Occurred { get; init; }
    /// <summary>Who stopped the agreement, on <see cref="VippsEvents.AgreementStopped"/>.</summary>
    public string? Actor { get; init; }
    /// <summary>The charge id, on charge events.</summary>
    public string? ChargeId { get; init; }
    /// <summary>The charge amount in øre, on charge events.</summary>
    public int? Amount { get; init; }
    /// <summary>The captured part of the charge in øre, on charge events.</summary>
    public int? AmountCaptured { get; init; }
    /// <summary>Why a charge failed, for example <c>charge_amount_too_high</c>.</summary>
    public string? FailureReason { get; init; }
    /// <summary>The id of the payment operation on a charge.</summary>
    public string? TransactionId { get; init; }
}

/// <summary>A signed event that passed the checks, with what the endpoint learned about it.</summary>
/// <typeparam name="TEvent">The event type.</typeparam>
public sealed record VippsWebhookDelivery<TEvent>
{
    /// <summary>The event.</summary>
    public required TEvent Event { get; init; }
    /// <summary>Whether the test registration's secret signed the event.</summary>
    public required bool IsTest { get; init; }
    /// <summary>The key the event is marked processed under. Test events start with <c>test:</c>.</summary>
    public required string EventKey { get; init; }
    /// <summary>The name of the slot that received the event.</summary>
    public required string Slot { get; init; }
}

/// <summary>What a handler did with an event.</summary>
public enum VippsWebhookOutcomeKind
{
    /// <summary>The handler applied the event. The endpoint saves its changes with the processed marker.</summary>
    Applied,
    /// <summary>The handler refused the event. The endpoint drops its changes, saves the processed marker, and answers 200.</summary>
    Rejected,
    /// <summary>The handler could not handle the event yet. The endpoint saves nothing and answers 500, so Vipps sends it again.</summary>
    Retry,
}

/// <summary>The outcome a handler returns for an event.</summary>
public sealed class VippsWebhookOutcome
{
    private VippsWebhookOutcome(VippsWebhookOutcomeKind kind, Func<CancellationToken, Task>? afterCommit)
    {
        Kind = kind;
        AfterCommit = afterCommit;
    }

    /// <summary>What the handler did.</summary>
    public VippsWebhookOutcomeKind Kind { get; }

    /// <summary>The side effects to run once the changes are saved, such as an email. Only an applied event has them.</summary>
    public Func<CancellationToken, Task>? AfterCommit { get; }

    /// <summary>
    /// A rejected event. Rejecting answers 200, because Vipps retries any 4xx or 5xx for seven days and holds
    /// back the payment's later events until one succeeds. Log the reason where someone notices it.
    /// </summary>
    public static VippsWebhookOutcome Rejected { get; } = new(VippsWebhookOutcomeKind.Rejected, null);

    /// <summary>An event to receive again later.</summary>
    public static VippsWebhookOutcome Retry { get; } = new(VippsWebhookOutcomeKind.Retry, null);

    /// <summary>Creates an applied outcome.</summary>
    /// <param name="afterCommit">
    /// The side effects to run once the changes are saved. A failure there is logged and does not change the answer.
    /// It does not run when a concurrent delivery of the same event saved first.
    /// </param>
    /// <returns>The outcome.</returns>
    public static VippsWebhookOutcome Applied(Func<CancellationToken, Task>? afterCommit = null) => new(VippsWebhookOutcomeKind.Applied, afterCommit);
}

/// <summary>
/// A handler for ePayment events, registered as a scoped service. It changes the app's entities on the
/// app's DbContext without saving. The endpoint saves them together with the processed marker.
/// </summary>
public interface IVippsPaymentWebhookHandler
{
    /// <summary>Handles an event that passed the signature, duplicate, and merchant checks.</summary>
    /// <param name="delivery">The event and its environment.</param>
    /// <param name="cancellationToken">A token that is canceled when the request is aborted.</param>
    /// <returns>The outcome.</returns>
    Task<VippsWebhookOutcome> HandleAsync(VippsWebhookDelivery<VippsPaymentEvent> delivery, CancellationToken cancellationToken);
}

/// <summary>
/// A handler for Recurring events, registered as a scoped service. It changes the app's entities on the
/// app's DbContext without saving. The endpoint saves them together with the processed marker.
/// </summary>
public interface IVippsRecurringWebhookHandler
{
    /// <summary>Handles an event that passed the signature, duplicate, and merchant checks.</summary>
    /// <param name="delivery">The event and its environment.</param>
    /// <param name="cancellationToken">A token that is canceled when the request is aborted.</param>
    /// <returns>The outcome.</returns>
    Task<VippsWebhookOutcome> HandleAsync(VippsWebhookDelivery<VippsRecurringEvent> delivery, CancellationToken cancellationToken);
}
