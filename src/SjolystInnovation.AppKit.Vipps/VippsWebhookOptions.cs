namespace SjolystInnovation.AppKit.Vipps;

/// <summary>The Vipps API whose events a webhook receives.</summary>
public enum VippsWebhookKind
{
    /// <summary>ePayment events, handled by <see cref="IVippsPaymentWebhookHandler"/>.</summary>
    Payment,
    /// <summary>Recurring agreement and charge events, handled by <see cref="IVippsRecurringWebhookHandler"/>.</summary>
    Recurring,
}

/// <summary>
/// One webhook of the app, such as a shop's ePayment webhook. It has a registration in each configured
/// environment, and each registration has its own secret.
/// </summary>
public sealed class VippsWebhookSlot
{
    /// <summary>The longest slot name, because the name is stored as the source of each processed event.</summary>
    public const int MaxNameLength = 50;

    /// <summary>The slot name, for example <c>shop</c>. It is stored with each processed event and each registration.</summary>
    public required string Name { get; init; }
    /// <summary>The API whose events the slot receives.</summary>
    public required VippsWebhookKind Kind { get; init; }
    /// <summary>The path of the endpoint, for example <c>/api/shop/webhook</c>.</summary>
    public required string Path { get; init; }
    /// <summary>
    /// The events to register. Defaults to <see cref="VippsEvents.Payments"/> or <see cref="VippsEvents.Recurring"/>,
    /// after <see cref="Kind"/>.
    /// </summary>
    public IReadOnlyList<string>? Events { get; init; }

    internal IReadOnlyList<string> EventsToRegister => Events ?? (Kind == VippsWebhookKind.Payment ? VippsEvents.Payments : VippsEvents.Recurring);
}

/// <summary>The options for the webhook endpoints, their registration, and the cleanup of processed events.</summary>
public sealed class VippsWebhookOptions
{
    /// <summary>The webhooks of the app.</summary>
    public IList<VippsWebhookSlot> Slots { get; } = [];

    /// <summary>
    /// The public root URL of the API, for example <c>https://api.example.com</c>. Each slot registers
    /// this URL followed by its path. Without it, nothing is registered.
    /// </summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    /// <summary>Whether a hosted service registers the webhooks after the app starts. Defaults to true.</summary>
    public bool RegisterOnStart { get; set; } = true;

    /// <summary>
    /// The PostgreSQL advisory lock that instances starting together take, so they register one at a
    /// time. Without it, or on another database, registration takes no lock. Defaults to null.
    /// </summary>
    public long? AdvisoryLockKey { get; set; }

    /// <summary>
    /// How long a processed event is kept to stop a redelivery. It must exceed
    /// <see cref="VippsWebhookSignature.MaxAge"/>, or a replay signed earlier could pass. Defaults to 30 days.
    /// </summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Whether a Recurring event without a merchant serial number is accepted. The Vipps Webhooks events
    /// page lists <c>msn</c> among the properties a Recurring payload "may include", so it can be missing.
    /// A Recurring event whose <c>msn</c> is present and differs is always rejected, and an ePayment event
    /// always needs a matching <c>msn</c>, because Vipps documents it as required there. Set this to false
    /// to reject a Recurring event without one as well. Defaults to true.
    /// </summary>
    public bool AllowMissingRecurringMsn { get; set; } = true;

    /// <summary>
    /// The largest body the endpoint reads. A larger request gets 413 before its signature is checked.
    /// Defaults to 64 KB.
    /// </summary>
    public int MaxBodyBytes { get; set; } = 64 * 1024;

    /// <summary>
    /// How long the endpoint keeps a slot's secrets in memory, so a stream of unsigned requests does not
    /// read the database each time. The registration service clears them when it stores new
    /// registrations. Defaults to 30 seconds.
    /// </summary>
    public TimeSpan SecretCacheDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The Data Protection purpose that protects stored secrets. Changing it makes stored secrets
    /// unreadable, which leads to new registrations. Defaults to <c>SjolystInnovation.AppKit.Vipps.webhook-secret.v1</c>.
    /// </summary>
    public string SecretPurpose { get; set; } = "SjolystInnovation.AppKit.Vipps.webhook-secret.v1";

    /// <summary>
    /// The prefix that marks a protected secret. A stored value without it is read as plain text.
    /// Defaults to <c>enc:v1:</c>.
    /// </summary>
    public string SecretPrefix { get; set; } = "enc:v1:";

    /// <summary>Adds a slot.</summary>
    /// <param name="name">The slot name, for example <c>shop</c>.</param>
    /// <param name="kind">The API whose events the slot receives.</param>
    /// <param name="path">The path of the endpoint, for example <c>/api/shop/webhook</c>.</param>
    /// <returns>The same options, for chaining.</returns>
    public VippsWebhookOptions AddSlot(string name, VippsWebhookKind kind, string path)
    {
        Slots.Add(new VippsWebhookSlot { Name = name, Kind = kind, Path = path });
        return this;
    }

    internal VippsWebhookSlot Slot(string name) =>
        Slots.FirstOrDefault(s => s.Name == name)
        ?? throw new InvalidOperationException($"No Vipps webhook slot is named '{name}'. Add it in AddAppKitVippsWebhooks.");

    internal string? Validate()
    {
        if (Retention <= VippsWebhookSignature.MaxAge)
            return $"The processed webhook retention must exceed the signature max age of {VippsWebhookSignature.MaxAge.TotalDays} days.";
        foreach (var slot in Slots)
        {
            if (string.IsNullOrWhiteSpace(slot.Name) || slot.Name.Length > VippsWebhookSlot.MaxNameLength)
                return $"A Vipps webhook slot name must have 1 to {VippsWebhookSlot.MaxNameLength} characters.";
            if (!slot.Path.StartsWith('/'))
                return $"The path of Vipps webhook slot '{slot.Name}' must start with '/'.";
        }
        if (Slots.Select(s => s.Name).Distinct(StringComparer.Ordinal).Count() != Slots.Count)
            return "Each Vipps webhook slot needs its own name.";
        // Routing ignores case and a trailing slash, so two paths that differ only there are one endpoint.
        if (Slots.Select(s => s.Path.TrimEnd('/')).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Slots.Count)
            return "Each Vipps webhook slot needs its own path.";
        if (MaxBodyBytes <= 0)
            return "The Vipps webhook body limit must be positive.";
        if (SecretCacheDuration < TimeSpan.Zero)
            return "The Vipps webhook secret cache duration cannot be negative.";
        return null;
    }
}
