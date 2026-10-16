using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SjolystInnovation.AppKit.Vipps;

/// <summary>
/// Makes sure each slot is registered in each configured Vipps environment, once the app has started.
/// Test and live are separate at Vipps, so each has its own registration and secret. A stored
/// registration that Vipps no longer lists, for example after two weeks of failed deliveries, or whose
/// secret can no longer be read, is replaced.
/// </summary>
internal sealed partial class VippsWebhookRegistrationService<TContext>(
    IServiceScopeFactory scopes, IOptions<VippsWebhookOptions> webhookOptions, IOptions<VippsOptions> vippsOptions,
    ILogger<VippsWebhookRegistrationService<TContext>> logger) : BackgroundService
    where TContext : DbContext
{
    private const string NpgsqlProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!webhookOptions.Value.RegisterOnStart) return;
        // Leave startup first, so a slow Vipps never holds the app back.
        await Task.Yield();
        try
        {
            await RegisterAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Failed(logger, ex);
        }
    }

    internal async Task RegisterAsync(CancellationToken cancellationToken)
    {
        var options = webhookOptions.Value;
        var vipps = vippsOptions.Value;
        if (options.Slots.Count == 0) return;
        if (string.IsNullOrWhiteSpace(options.PublicBaseUrl))
        {
            Log.NoBaseUrl(logger);
            return;
        }

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var client = scope.ServiceProvider.GetRequiredService<IVippsClient>();
        var store = scope.ServiceProvider.GetRequiredService<IVippsWebhookRegistrationStore>();
        var protector = scope.ServiceProvider.GetRequiredService<IVippsWebhookSecretProtector>();

        // Instances starting together register one at a time. Otherwise one could delete the registration
        // another just stored. Raw SQL because EF Core has no advisory lock API.
        var useLock = options.AdvisoryLockKey is not null && db.Database.ProviderName == NpgsqlProvider;
        await using var transaction = useLock ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        if (useLock)
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({options.AdvisoryLockKey!.Value})", cancellationToken);

        var registered = new Dictionary<bool, IReadOnlyList<VippsWebhook>>();
        foreach (var isTest in new[] { false, true })
        {
            if (!vipps.For(isTest).IsConfigured) continue;
            try
            {
                registered[isTest] = await client.ListWebhooksAsync(isTest, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.ListFailed(logger, ex, Environment(isTest));
            }
        }

        var stale = new List<(string Id, bool IsTest, string Slot)>();
        var changed = await MoveToTheirEnvironmentAsync(options, store, registered, stale, cancellationToken);
        var baseUrl = options.PublicBaseUrl.TrimEnd('/');
        foreach (var isTest in new[] { false, true })
        {
            if (!registered.TryGetValue(isTest, out var existing)) continue;
            var environment = Environment(isTest);
            foreach (var slot in options.Slots)
            {
                var stored = await store.GetAsync(slot.Name, isTest, cancellationToken);
                var listed = stored is not null && existing.Any(h => h.Id == stored.Id);
                if (listed && protector.TryUnprotect(stored!.ProtectedSecret, out _)) continue;
                if (listed)
                    Log.UnreadableSecret(logger, slot.Name, environment, stored!.Id);

                // A live slot may hold a test registration. Without the test listing that cannot be ruled
                // out, so the slot is left as it is.
                if (!listed && !isTest && stored is not null && vipps.Test.IsConfigured && !registered.ContainsKey(true))
                {
                    Log.LeftInPlace(logger, slot.Name, stored.Id);
                    continue;
                }

                var url = baseUrl + slot.Path;
                try
                {
                    var created = await client.RegisterWebhookAsync(url, [.. slot.EventsToRegister], isTest, cancellationToken);
                    await store.SetAsync(slot.Name, isTest, new VippsWebhookRegistration(created.Id, protector.Protect(created.Secret)), cancellationToken);
                    changed = true;
                    Log.Registered(logger, slot.Name, environment, created.Id);

                    // Vipps hands out a secret only when registering, so other registrations for this URL
                    // cannot be verified. They are deleted once the new registration is stored.
                    stale.AddRange(existing.Where(h => SameUrl(h.Url, url)).Select(h => (h.Id, isTest, slot.Name)));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.RegisterFailed(logger, ex, slot.Name, environment);
                }
            }
        }

        if (changed)
            await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        // Runs after the commit, so a read in between cannot cache the old secrets.
        if (changed)
        {
            scope.ServiceProvider.GetService<VippsWebhookSecretCache>()?.Clear();
            await store.SavedAsync(cancellationToken);
        }

        foreach (var (id, isTest, slot) in stale)
        {
            var environment = Environment(isTest);
            try
            {
                await client.DeleteWebhookAsync(id, isTest, cancellationToken);
                Log.Deleted(logger, slot, environment, id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.DeleteFailed(logger, ex, slot, environment, id);
            }
        }
    }

    private static string Environment(bool isTest) => isTest ? "test" : "live";

    private static bool SameUrl(string a, string b) =>
        string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A live slot holds a test registration when the test environment lists its id. It moves to the empty
    /// test slot. When the test slot already has its own registration, the one in the live slot can no
    /// longer be verified, so it joins <paramref name="stale"/> to be deleted.
    /// </summary>
    private static async Task<bool> MoveToTheirEnvironmentAsync(VippsWebhookOptions options, IVippsWebhookRegistrationStore store,
        Dictionary<bool, IReadOnlyList<VippsWebhook>> registered, List<(string Id, bool IsTest, string Slot)> stale, CancellationToken cancellationToken)
    {
        if (!registered.TryGetValue(true, out var test)) return false;
        var moved = false;
        foreach (var slot in options.Slots)
        {
            var live = await store.GetAsync(slot.Name, false, cancellationToken);
            if (live is null || !test.Any(h => h.Id == live.Id)) continue;
            var testSlot = await store.GetAsync(slot.Name, true, cancellationToken);
            if (testSlot is not null)
            {
                if (testSlot.Id != live.Id)
                    stale.Add((live.Id, true, slot.Name));
                continue;
            }
            await store.SetAsync(slot.Name, true, live, cancellationToken);
            await store.SetAsync(slot.Name, false, null, cancellationToken);
            moved = true;
        }
        return moved;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Vipps webhooks are not registered, because no public base URL is set")]
        public static partial void NoBaseUrl(ILogger logger);

        [LoggerMessage(Level = LogLevel.Error, Message = "Could not list Vipps webhooks in the {Environment} environment")]
        public static partial void ListFailed(ILogger logger, Exception exception, string environment);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Vipps {Slot} webhook {WebhookId} in the {Environment} environment has a secret that cannot be read, registering a new one")]
        public static partial void UnreadableSecret(ILogger logger, string slot, string environment, string webhookId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Vipps {Slot} webhook {WebhookId} is not listed in live, left in place until the test environment can be listed")]
        public static partial void LeftInPlace(ILogger logger, string slot, string webhookId);

        [LoggerMessage(Level = LogLevel.Information, Message = "Registered Vipps {Slot} webhook in the {Environment} environment: {WebhookId}")]
        public static partial void Registered(ILogger logger, string slot, string environment, string webhookId);

        [LoggerMessage(Level = LogLevel.Error, Message = "Failed to register Vipps {Slot} webhook in the {Environment} environment")]
        public static partial void RegisterFailed(ILogger logger, Exception exception, string slot, string environment);

        [LoggerMessage(Level = LogLevel.Information, Message = "Deleted unverifiable Vipps {Slot} webhook in the {Environment} environment: {WebhookId}")]
        public static partial void Deleted(ILogger logger, string slot, string environment, string webhookId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to delete unverifiable Vipps {Slot} webhook in the {Environment} environment: {WebhookId}")]
        public static partial void DeleteFailed(ILogger logger, Exception exception, string slot, string environment, string webhookId);

        [LoggerMessage(Level = LogLevel.Error, Message = "Vipps webhook registration failed")]
        public static partial void Failed(ILogger logger, Exception exception);
    }
}
