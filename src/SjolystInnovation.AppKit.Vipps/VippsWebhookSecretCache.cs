using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SjolystInnovation.AppKit.Vipps;

/// <summary>
/// Keeps each slot's readable secrets in memory for <see cref="VippsWebhookOptions.SecretCacheDuration"/>, so
/// the endpoint does not read the store for every request. The registration service clears it when it
/// stores new registrations.
/// </summary>
internal sealed partial class VippsWebhookSecretCache(IOptions<VippsWebhookOptions> options, TimeProvider time, ILogger<VippsWebhookSecretCache> logger)
{
    /// <summary>The readable secrets of a slot. A missing or unreadable secret is null.</summary>
    internal sealed record Secrets(string? Live, string? Test, DateTimeOffset Expires);

    private readonly ConcurrentDictionary<string, Secrets> _slots = new(StringComparer.Ordinal);

    public async Task<Secrets> GetAsync(string slot, IVippsWebhookRegistrationStore store, IVippsWebhookSecretProtector protector, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        if (_slots.TryGetValue(slot, out var cached) && cached.Expires > now) return cached;

        var live = await ReadAsync(slot, false, store, protector, cancellationToken);
        var test = await ReadAsync(slot, true, store, protector, cancellationToken);
        var secrets = new Secrets(live, test, now + options.Value.SecretCacheDuration);
        _slots[slot] = secrets;
        return secrets;
    }

    public void Clear() => _slots.Clear();

    /// <summary>
    /// Reads a stored secret. A secret that cannot be read counts as missing, so the registration service
    /// replaces it instead of every delivery failing.
    /// </summary>
    private async Task<string?> ReadAsync(string slot, bool isTest, IVippsWebhookRegistrationStore store, IVippsWebhookSecretProtector protector, CancellationToken cancellationToken)
    {
        var registration = await store.GetAsync(slot, isTest, cancellationToken);
        if (registration is null || string.IsNullOrEmpty(registration.ProtectedSecret)) return null;
        if (protector.TryUnprotect(registration.ProtectedSecret, out var secret)) return secret;
        Log.UnreadableSecret(logger, slot, isTest ? "test" : "live");
        return null;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Error, Message = "Vipps {Slot} webhook: the stored {Environment} secret cannot be read, the registration is replaced at the next start")]
        public static partial void UnreadableSecret(ILogger logger, string slot, string environment);
    }
}
