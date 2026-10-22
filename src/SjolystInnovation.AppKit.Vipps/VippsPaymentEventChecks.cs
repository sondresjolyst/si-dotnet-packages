namespace SjolystInnovation.AppKit.Vipps;

/// <summary>Why <see cref="VippsPaymentEventChecks.Check"/> refused an ePayment event.</summary>
public enum VippsPaymentEventProblem
{
    /// <summary>The event passed every check.</summary>
    None,
    /// <summary>The event was signed in a different environment from the one the sales unit was created in.</summary>
    WrongEnvironment,
    /// <summary>The operation behind the event did not succeed.</summary>
    NotSuccessful,
    /// <summary>The event's amount does not fit the sales unit's total.</summary>
    AmountMismatch,
}

/// <summary>
/// The checks an <see cref="IVippsPaymentWebhookHandler"/> makes on an ePayment event before it applies the event to
/// a sales unit, such as an order. They take the sales unit's environment and total as plain values.
/// </summary>
public static class VippsPaymentEventChecks
{
    /// <summary>
    /// Whether the amount fits the total. <c>AUTHORIZED</c> reserves the whole total, so its amount must equal it.
    /// <c>CAPTURED</c> and <c>REFUNDED</c> can each be part of it, so their amount must be above zero and at most the
    /// total. Any other event fits whatever its amount.
    /// </summary>
    /// <param name="name">The event name, one of <see cref="VippsPaymentEventNames"/>.</param>
    /// <param name="amountInOre">The event's amount in øre, or null when it has none.</param>
    /// <param name="totalInOre">The sales unit's total in øre.</param>
    /// <returns>Whether the amount fits.</returns>
    public static bool AmountFits(string name, int? amountInOre, int totalInOre) => name switch
    {
        VippsPaymentEventNames.Authorized => amountInOre == totalInOre,
        VippsPaymentEventNames.Captured or VippsPaymentEventNames.Refunded => amountInOre > 0 && amountInOre <= totalInOre,
        _ => true,
    };

    /// <summary>Whether the event's amount fits the total, as <see cref="AmountFits(string, int?, int)"/> decides.</summary>
    /// <param name="paymentEvent">The event.</param>
    /// <param name="totalInOre">The sales unit's total in øre.</param>
    /// <returns>Whether the amount fits.</returns>
    public static bool AmountFits(VippsPaymentEvent paymentEvent, int totalInOre)
    {
        ArgumentNullException.ThrowIfNull(paymentEvent);
        return AmountFits(paymentEvent.Name, paymentEvent.Amount?.Value, totalInOre);
    }

    /// <summary>
    /// Whether the environment that signed the event is the one the sales unit belongs to. A test event for a live
    /// order, or the other way round, must not change it.
    /// </summary>
    /// <typeparam name="TEvent">The event type.</typeparam>
    /// <param name="delivery">The delivery.</param>
    /// <param name="isTest">Whether the sales unit was created in the test environment.</param>
    /// <returns>Whether the environments match.</returns>
    public static bool EnvironmentMatches<TEvent>(VippsWebhookDelivery<TEvent> delivery, bool isTest)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        return delivery.IsTest == isTest;
    }

    /// <summary>
    /// Runs the checks in order: the environment, then the event's success, then its amount. Returns the first
    /// problem, or <see cref="VippsPaymentEventProblem.None"/>. A handler usually answers a problem with
    /// <see cref="VippsWebhookOutcome.Rejected"/> and logs it.
    /// </summary>
    /// <param name="delivery">The delivery.</param>
    /// <param name="isTest">Whether the sales unit was created in the test environment.</param>
    /// <param name="totalInOre">The sales unit's total in øre.</param>
    /// <returns>The first problem found.</returns>
    public static VippsPaymentEventProblem Check(VippsWebhookDelivery<VippsPaymentEvent> delivery, bool isTest, int totalInOre)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        if (!EnvironmentMatches(delivery, isTest)) return VippsPaymentEventProblem.WrongEnvironment;
        if (!delivery.Event.Success) return VippsPaymentEventProblem.NotSuccessful;
        if (!AmountFits(delivery.Event, totalInOre)) return VippsPaymentEventProblem.AmountMismatch;
        return VippsPaymentEventProblem.None;
    }
}
