using System.Globalization;

namespace SjolystInnovation.AppKit.Vipps;

/// <summary>
/// Turns an ePayment read with <see cref="IVippsClient.GetPaymentAsync"/> into the events it implies. Vipps asks
/// merchants to poll as a fallback for lost webhooks, and these events let an app feed a polled payment through
/// the same <see cref="IVippsPaymentWebhookHandler"/> as its webhooks.
/// </summary>
public static class VippsPaymentPolling
{
    /// <summary>
    /// The events a payment implies, in the order Vipps makes them. <c>ABORTED</c>, <c>EXPIRED</c>, and
    /// <c>TERMINATED</c> are states of their own and imply their event. A payment that is still <c>AUTHORIZED</c>
    /// carries its captures, refunds, and cancellation as amounts. A capture implies <c>CAPTURED</c> with the
    /// captured total, followed by <c>REFUNDED</c> with the refunded total when there is one. Without a capture, a
    /// canceled amount implies <c>CANCELLED</c>, and otherwise the payment implies <c>AUTHORIZED</c> with the
    /// authorized amount. <c>CREATED</c> and any other state imply nothing. The amount of a polled <c>AUTHORIZED</c>
    /// comes from <see cref="VippsPayment.AuthorizedAmountInOre"/>, which the client reads from <c>aggregate.authorizedAmount</c>.
    /// </summary>
    /// <param name="payment">The payment as Vipps showed it.</param>
    /// <param name="now">The time of the poll, which becomes each event's timestamp.</param>
    /// <returns>The events, each marked as a success. They have no merchant serial number and no PSP reference.</returns>
    public static IReadOnlyList<VippsPaymentEvent> EventsOf(VippsPayment payment, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(payment);

        VippsPaymentEvent Event(string name, int? amountInOre) => new()
        {
            Reference = payment.Reference,
            Name = name,
            Amount = amountInOre is { } value ? new VippsWebhookAmount { Value = value, Currency = "NOK" } : null,
            Timestamp = now,
            Success = true,
            Sub = name == VippsPaymentEventNames.Authorized ? payment.ProfileSub : null,
            CaptureGuaranteedUntil = name == VippsPaymentEventNames.Authorized ? payment.CaptureGuaranteedUntil : null,
        };

        switch (payment.State)
        {
            case VippsPaymentEventNames.Aborted:
            case VippsPaymentEventNames.Expired:
            case VippsPaymentEventNames.Terminated:
                return [Event(payment.State, null)];
            case VippsPaymentEventNames.Authorized:
                if (payment.CapturedAmountInOre > 0)
                {
                    List<VippsPaymentEvent> events = [Event(VippsPaymentEventNames.Captured, payment.CapturedAmountInOre)];
                    if (payment.RefundedAmountInOre > 0) events.Add(Event(VippsPaymentEventNames.Refunded, payment.RefundedAmountInOre));
                    return events;
                }
                if (payment.CancelledAmountInOre > 0) return [Event(VippsPaymentEventNames.Cancelled, payment.CancelledAmountInOre)];
                return [Event(VippsPaymentEventNames.Authorized, payment.AuthorizedAmountInOre)];
            default:
                return [];
        }
    }

    /// <summary>
    /// The events of <see cref="EventsOf"/> as deliveries for an <see cref="IVippsPaymentWebhookHandler"/>. Each has
    /// the key <c>poll:{reference}:{name}:{amount}</c>, with <c>test:</c> in front in the test environment, so a
    /// handler can log it or store it the way it stores a webhook's key.
    /// </summary>
    /// <param name="payment">The payment as Vipps showed it.</param>
    /// <param name="isTest">Whether the payment was read from the test environment.</param>
    /// <param name="slot">The name of the webhook slot whose handler takes the deliveries.</param>
    /// <param name="now">The time of the poll.</param>
    /// <returns>The deliveries.</returns>
    public static IReadOnlyList<VippsWebhookDelivery<VippsPaymentEvent>> DeliveriesOf(VippsPayment payment, bool isTest, string slot, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(slot);
        return EventsOf(payment, now).Select(e =>
        {
            var key = string.Create(CultureInfo.InvariantCulture, $"poll:{e.Reference}:{e.Name}:{e.Amount?.Value}");
            return new VippsWebhookDelivery<VippsPaymentEvent>
            {
                Event = e,
                IsTest = isTest,
                EventKey = isTest ? $"test:{key}" : key,
                Slot = slot,
            };
        }).ToList();
    }
}
