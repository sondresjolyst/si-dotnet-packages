using Microsoft.EntityFrameworkCore;

namespace SjolystInnovation.AppKit.Vipps;

/// <summary>A webhook event that was handled, kept so a redelivery of it is skipped.</summary>
public class VippsProcessedWebhookEvent
{
    /// <summary>The event key. Test events start with <c>test:</c>.</summary>
    public required string Id { get; set; }
    /// <summary>The name of the slot that received the event.</summary>
    public required string Source { get; set; }
    /// <summary>When the event was handled, in UTC.</summary>
    public DateTime ProcessedAt { get; set; }
}

/// <summary>A DbContext that holds processed webhook events. The app's DbContext implements it.</summary>
public interface IVippsWebhookDbContext
{
    /// <summary>The processed webhook events.</summary>
    DbSet<VippsProcessedWebhookEvent> ProcessedWebhookEvents { get; }
}

/// <summary>A stored webhook registration: its id at Vipps and its protected secret.</summary>
/// <param name="Id">The webhook id at Vipps.</param>
/// <param name="ProtectedSecret">The secret, as <see cref="IVippsWebhookSecretProtector.Protect"/> returned it.</param>
public sealed record VippsWebhookRegistration(string Id, string ProtectedSecret);

/// <summary>
/// Stores one registration per slot and environment. Register your own as a scoped service to keep
/// registrations in your own columns. Otherwise the registrations go to the table that
/// <see cref="VippsWebhookModelBuilderExtensions.AddAppKitVippsWebhookRegistrations"/> maps.
/// </summary>
public interface IVippsWebhookRegistrationStore
{
    /// <summary>Gets the registration of a slot in an environment.</summary>
    /// <param name="slot">The slot name.</param>
    /// <param name="isTest">Whether to get the test registration.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The registration, or null when there is none.</returns>
    Task<VippsWebhookRegistration?> GetAsync(string slot, bool isTest, CancellationToken cancellationToken);

    /// <summary>
    /// Sets or clears the registration of a slot in an environment. A store over the app's DbContext only
    /// changes the tracked entities. The registration service saves the DbContext afterwards.
    /// </summary>
    /// <param name="slot">The slot name.</param>
    /// <param name="isTest">Whether to set the test registration.</param>
    /// <param name="registration">The registration, or null to clear it.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    Task SetAsync(string slot, bool isTest, VippsWebhookRegistration? registration, CancellationToken cancellationToken);

    /// <summary>
    /// Runs after the registration service committed new registrations, for example to clear a cache of
    /// the secrets. Clearing it earlier could let a read in between cache the old secrets.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the work.</param>
    Task SavedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>A row of the default registration table.</summary>
public class VippsWebhookRegistrationEntry
{
    /// <summary>The slot name.</summary>
    public required string Slot { get; set; }
    /// <summary>Whether the registration is in the test environment.</summary>
    public bool IsTest { get; set; }
    /// <summary>The webhook id at Vipps.</summary>
    public required string WebhookId { get; set; }
    /// <summary>The protected secret.</summary>
    public required string ProtectedSecret { get; set; }
}

/// <summary>Adds the webhook tables to an EF model.</summary>
public static class VippsWebhookModelBuilderExtensions
{
    /// <summary>
    /// Maps <see cref="VippsProcessedWebhookEvent"/> to <paramref name="tableName"/>, keyed by an id of up to
    /// 200 characters, with a source of up to 50 characters, an index on <c>ProcessedAt</c> for the cleanup,
    /// and an index on <c>Source</c> and <c>Id</c>.
    /// </summary>
    /// <param name="modelBuilder">The model builder.</param>
    /// <param name="tableName">The table name. Defaults to <c>ProcessedWebhookEvents</c>.</param>
    /// <returns>The same model builder.</returns>
    public static ModelBuilder AddAppKitVippsProcessedWebhookEvents(this ModelBuilder modelBuilder, string tableName = "ProcessedWebhookEvents")
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<VippsProcessedWebhookEvent>(e =>
        {
            e.ToTable(tableName);
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).HasMaxLength(200);
            e.Property(p => p.Source).IsRequired().HasMaxLength(VippsWebhookSlot.MaxNameLength);
            e.HasIndex(p => p.ProcessedAt);
            // Redundant next to the key, but kept so an app that moves here with this index needs no migration.
            e.HasIndex(p => new { p.Source, p.Id });
        });
        return modelBuilder;
    }

    /// <summary>
    /// Maps <see cref="VippsWebhookRegistrationEntry"/> to <paramref name="tableName"/>, with one row per slot
    /// and environment. Only the default <see cref="IVippsWebhookRegistrationStore"/> uses it.
    /// </summary>
    /// <param name="modelBuilder">The model builder.</param>
    /// <param name="tableName">The table name. Defaults to <c>VippsWebhookRegistrations</c>.</param>
    /// <returns>The same model builder.</returns>
    public static ModelBuilder AddAppKitVippsWebhookRegistrations(this ModelBuilder modelBuilder, string tableName = "VippsWebhookRegistrations")
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<VippsWebhookRegistrationEntry>(e =>
        {
            e.ToTable(tableName);
            e.HasKey(r => new { r.Slot, r.IsTest });
            e.Property(r => r.Slot).HasMaxLength(VippsWebhookSlot.MaxNameLength);
            e.Property(r => r.WebhookId).IsRequired().HasMaxLength(200);
            e.Property(r => r.ProtectedSecret).IsRequired();
        });
        return modelBuilder;
    }
}

/// <summary>Keeps registrations in the table of <see cref="VippsWebhookRegistrationEntry"/> on the app's DbContext.</summary>
internal sealed class EfVippsWebhookRegistrationStore<TContext>(TContext db) : IVippsWebhookRegistrationStore
    where TContext : DbContext
{
    public async Task<VippsWebhookRegistration?> GetAsync(string slot, bool isTest, CancellationToken cancellationToken)
    {
        var entry = await db.Set<VippsWebhookRegistrationEntry>().FindAsync([slot, isTest], cancellationToken);
        return entry is null ? null : new VippsWebhookRegistration(entry.WebhookId, entry.ProtectedSecret);
    }

    public async Task SetAsync(string slot, bool isTest, VippsWebhookRegistration? registration, CancellationToken cancellationToken)
    {
        var set = db.Set<VippsWebhookRegistrationEntry>();
        var entry = await set.FindAsync([slot, isTest], cancellationToken);
        if (registration is null)
        {
            if (entry is not null) set.Remove(entry);
            return;
        }
        if (entry is null)
        {
            set.Add(new VippsWebhookRegistrationEntry { Slot = slot, IsTest = isTest, WebhookId = registration.Id, ProtectedSecret = registration.ProtectedSecret });
            return;
        }
        entry.WebhookId = registration.Id;
        entry.ProtectedSecret = registration.ProtectedSecret;
    }
}
