using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace SjolystInnovation.AppKit.Email;

/// <summary>The limits for <see cref="EmailReceiptGate"/>.</summary>
public sealed class EmailReceiptGateOptions
{
    /// <summary>How long a mailbox waits between receipts. Defaults to 10 minutes. Must be above zero.</summary>
    public TimeSpan AddressWindow { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>The period that <see cref="GlobalLimit"/> counts over. Defaults to one hour. Must be above zero.</summary>
    public TimeSpan GlobalWindow { get; set; } = TimeSpan.FromHours(1);

    /// <summary>The most receipts across all mailboxes in <see cref="GlobalWindow"/>. Defaults to 30. Must be above zero.</summary>
    public int GlobalLimit { get; set; } = 30;
}

/// <summary>
/// Decides whether an app may send a receipt to an address that a user typed. One receipt per mailbox in
/// <see cref="EmailReceiptGateOptions.AddressWindow"/>, where spellings of the same mailbox count as one, and at
/// most <see cref="EmailReceiptGateOptions.GlobalLimit"/> receipts in <see cref="EmailReceiptGateOptions.GlobalWindow"/>
/// across all addresses. The counts live in memory, so each app instance keeps its own.
/// </summary>
public sealed class EmailReceiptGate
{
    private static readonly string[] GmailDomains = ["gmail.com", "googlemail.com"];

    private readonly TimeProvider _time;
    private readonly TimeSpan _addressWindow;
    private readonly TimeSpan _globalWindow;
    private readonly int _globalLimit;
    private readonly object _lock = new();
    private readonly Dictionary<string, DateTimeOffset> _lastByMailbox = [];
    private readonly Queue<DateTimeOffset> _sent = new();

    /// <summary>Creates a gate with the limits in <paramref name="options"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">When a window or the limit is not above zero.</exception>
    public EmailReceiptGate(IOptions<EmailReceiptGateOptions> options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        var value = options.Value;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value.AddressWindow, TimeSpan.Zero, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value.GlobalWindow, TimeSpan.Zero, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value.GlobalLimit, 0, nameof(options));
        _time = time;
        _addressWindow = value.AddressWindow;
        _globalWindow = value.GlobalWindow;
        _globalLimit = value.GlobalLimit;
    }

    /// <summary>True when a receipt to <paramref name="address"/> may go out now. The send is counted at once.</summary>
    /// <exception cref="ArgumentNullException">When the address is null.</exception>
    public bool TryAcquire(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var now = _time.GetUtcNow();
        var mailbox = Mailbox(address);
        lock (_lock)
        {
            while (_sent.Count > 0 && now - _sent.Peek() >= _globalWindow) _sent.Dequeue();
            foreach (var stale in _lastByMailbox.Where(p => now - p.Value >= _addressWindow).Select(p => p.Key).ToList())
                _lastByMailbox.Remove(stale);

            if (_lastByMailbox.ContainsKey(mailbox) || _sent.Count >= _globalLimit) return false;

            _lastByMailbox[mailbox] = now;
            _sent.Enqueue(now);
            return true;
        }
    }

    /// <summary>
    /// The mailbox an address delivers to, in lower case and without a +tag. Gmail ignores dots in the local part
    /// and treats googlemail.com as gmail.com, so those are folded too.
    /// </summary>
    public static string Mailbox(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var trimmed = address.Trim().ToLowerInvariant();
        var at = trimmed.LastIndexOf('@');
        if (at <= 0) return trimmed;

        var local = trimmed[..at];
        var domain = trimmed[(at + 1)..];
        var plus = local.IndexOf('+', StringComparison.Ordinal);
        if (plus > 0) local = local[..plus];
        if (GmailDomains.Contains(domain))
        {
            local = local.Replace(".", "", StringComparison.Ordinal);
            domain = "gmail.com";
        }
        return $"{local}@{domain}";
    }
}

/// <summary>Registers the receipt gate.</summary>
public static class EmailReceiptGateServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="EmailReceiptGate"/> as a singleton, so every request shares its counts, with
    /// <see cref="EmailReceiptGateOptions"/> from <paramref name="configure"/>. It also registers
    /// <see cref="TimeProvider.System"/> unless the app has its own.
    /// </summary>
    public static IServiceCollection AddAppKitEmailReceiptGate(this IServiceCollection services, Action<EmailReceiptGateOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = services.AddOptions<EmailReceiptGateOptions>();
        if (configure is not null) options.Configure(configure);
        return AddGate(services);
    }

    /// <summary>
    /// Registers <see cref="EmailReceiptGate"/> as a singleton, with <see cref="EmailReceiptGateOptions"/> bound
    /// from <paramref name="section"/>. Settings the section leaves out keep their defaults.
    /// </summary>
    public static IServiceCollection AddAppKitEmailReceiptGate(this IServiceCollection services, IConfigurationSection section)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(section);
        services.AddOptions<EmailReceiptGateOptions>().Bind(section);
        return AddGate(services);
    }

    private static IServiceCollection AddGate(IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<EmailReceiptGate>();
        return services;
    }
}
