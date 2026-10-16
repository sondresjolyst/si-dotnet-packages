using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace SjolystInnovation.AppKit.Vipps.Tests;

public class VippsWebhookRegistrationServiceTests
{
    private const string ShopUrl = "https://api.example.invalid/api/shop/webhook";
    private const string SubscriptionUrl = "https://api.example.invalid/api/subscriptions/webhook";

    /// <summary>Answers the webhook calls like Vipps and keeps every call it got.</summary>
    private sealed class FakeClient : IVippsClient
    {
        private int _seq;
        public Dictionary<bool, List<VippsWebhook>> Listed { get; } = [];
        public HashSet<bool> ListFails { get; } = [];
        public HashSet<string> RegisterFails { get; } = [];
        public HashSet<string> DeleteFails { get; } = [];
        public List<string> Calls { get; } = [];
        public List<(string Url, IReadOnlyCollection<string> Events, bool IsTest)> Registered { get; } = [];
        public List<(string Id, bool IsTest)> Deleted { get; } = [];

        public Task<IReadOnlyList<VippsWebhook>> ListWebhooksAsync(bool isTest, CancellationToken cancellationToken = default)
        {
            Calls.Add($"list {isTest}");
            if (ListFails.Contains(isTest)) throw new HttpRequestException("down");
            return Task.FromResult<IReadOnlyList<VippsWebhook>>(Listed.TryGetValue(isTest, out var l) ? l : []);
        }

        public Task<VippsRegisteredWebhook> RegisterWebhookAsync(string url, IReadOnlyCollection<string> events, bool isTest, CancellationToken cancellationToken = default)
        {
            Calls.Add("register");
            if (RegisterFails.Contains(url)) throw new HttpRequestException("down");
            Registered.Add((url, events, isTest));
            _seq++;
            return Task.FromResult(new VippsRegisteredWebhook($"new-{(isTest ? "test" : "live")}-{_seq}", $"secret-{_seq}"));
        }

        public Task DeleteWebhookAsync(string webhookId, bool isTest, CancellationToken cancellationToken = default)
        {
            Calls.Add("delete");
            if (DeleteFails.Contains(webhookId)) throw new HttpRequestException("down");
            Deleted.Add((webhookId, isTest));
            return Task.CompletedTask;
        }

        public Task<bool> IsTestAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<VippsCreateAgreementResponse> CreateAgreementAsync(VippsCreateAgreementRequest request, string idempotencyKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<VippsAgreement> GetAgreementAsync(string agreementId, bool? isTest = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StopAgreementAsync(string agreementId, string idempotencyKey, bool? isTest = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateAgreementMaxAmountAsync(string agreementId, int maxAmountInOre, string idempotencyKey, bool? isTest = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<VippsCreateChargeResponse> CreateChargeAsync(string agreementId, VippsCreateChargeRequest request, string idempotencyKey, bool? isTest = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RefundChargeAsync(string agreementId, string chargeId, int amountInOre, string description, string idempotencyKey, bool? isTest = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<VippsCreatePaymentResponse> CreatePaymentAsync(VippsCreatePaymentRequest request, string idempotencyKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<VippsPayment> GetPaymentAsync(string reference, bool? isTest = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<VippsPayment> CapturePaymentAsync(string reference, int amountInOre, string idempotencyKey, bool? isTest = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CancelPaymentAsync(string reference, string idempotencyKey, bool? isTest = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<VippsPayment> RefundPaymentAsync(string reference, int amountInOre, string idempotencyKey, bool? isTest = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<VippsUserInfo?> GetUserInfoAsync(string profileSub, bool? isTest = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>A store like an app's adapter over its own settings columns.</summary>
    private sealed class MemoryStore : IVippsWebhookRegistrationStore
    {
        public Dictionary<(string Slot, bool IsTest), VippsWebhookRegistration> Rows { get; } = [];
        public int Saved;

        public Task<VippsWebhookRegistration?> GetAsync(string slot, bool isTest, CancellationToken cancellationToken) =>
            Task.FromResult(Rows.TryGetValue((slot, isTest), out var r) ? r : null);

        public Task SetAsync(string slot, bool isTest, VippsWebhookRegistration? registration, CancellationToken cancellationToken)
        {
            if (registration is null) Rows.Remove((slot, isTest));
            else Rows[(slot, isTest)] = registration;
            return Task.CompletedTask;
        }

        public Task SavedAsync(CancellationToken cancellationToken)
        {
            Saved++;
            return Task.CompletedTask;
        }

        public string? Id(string slot, bool isTest) => Rows.TryGetValue((slot, isTest), out var r) ? r.Id : null;
        public string? Secret(string slot, bool isTest) => Rows.TryGetValue((slot, isTest), out var r) ? r.ProtectedSecret : null;
    }

    /// <summary>Protects by prefixing, and cannot read a value that starts with <c>broken:</c>.</summary>
    private sealed class PrefixProtector : IVippsWebhookSecretProtector
    {
        public string Protect(string plaintext) => $"protected:{plaintext}";
        public string Unprotect(string stored) => stored.StartsWith("broken:", StringComparison.Ordinal) ? throw new System.Security.Cryptography.CryptographicException() : stored;
        public bool TryUnprotect(string? stored, out string secret)
        {
            secret = string.Empty;
            if (string.IsNullOrEmpty(stored) || stored.StartsWith("broken:", StringComparison.Ordinal)) return false;
            secret = stored;
            return true;
        }
    }

    private sealed class Harness : IDisposable
    {
        private readonly SqliteConnection _connection = new("DataSource=:memory:");
        public FakeClient Vipps { get; } = new();
        public MemoryStore Store { get; } = new();
        public VippsWebhookRegistrationService<WebhookDbContext> Service { get; }
        public VippsWebhookSecretCache Cache { get; }

        public Harness(bool live, bool test, Action<VippsWebhookOptions>? configure = null)
        {
            _connection.Open();
            var services = new ServiceCollection();
            services.AddDbContext<WebhookDbContext>(o => o.UseSqlite(_connection));
            services.AddSingleton<IVippsClient>(Vipps);
            services.AddSingleton<IVippsWebhookRegistrationStore>(Store);
            services.AddSingleton<IVippsWebhookSecretProtector, PrefixProtector>();
            Cache = new VippsWebhookSecretCache(Options.Create(new VippsWebhookOptions()), TimeProvider.System, NullLogger<VippsWebhookSecretCache>.Instance);
            services.AddSingleton(Cache);
            var provider = services.BuildServiceProvider();
            using (var scope = provider.CreateScope())
                scope.ServiceProvider.GetRequiredService<WebhookDbContext>().Database.EnsureCreated();
            if (live) Vipps.Listed[false] = [];
            if (test) Vipps.Listed[true] = [];

            var options = new VippsWebhookOptions { PublicBaseUrl = "https://api.example.invalid/" };
            options.AddSlot("shop", VippsWebhookKind.Payment, "/api/shop/webhook");
            options.AddSlot("subscription", VippsWebhookKind.Recurring, "/api/subscriptions/webhook");
            configure?.Invoke(options);
            var vipps = new VippsOptions();
            if (live) vipps.Live = Credentials("https://api.vipps.no");
            if (test) vipps.Test = Credentials("https://apitest.vipps.no");
            Service = new VippsWebhookRegistrationService<WebhookDbContext>(provider.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(options), Options.Create(vipps), NullLogger<VippsWebhookRegistrationService<WebhookDbContext>>.Instance);
        }

        private static VippsCredentials Credentials(string baseUrl) =>
            new() { BaseUrl = baseUrl, ClientId = "id", ClientSecret = "s", SubscriptionKey = "k", MerchantSerialNumber = "msn" };

        public Task RunAsync() => Service.RegisterAsync(CancellationToken.None);

        public void Stored(string slot, bool isTest, string id, string secret) => Store.Rows[(slot, isTest)] = new VippsWebhookRegistration(id, secret);

        public void Dispose() => _connection.Dispose();
    }

    private static VippsWebhook Hook(string id, string url) => new() { Id = id, Url = url };

    [Fact]
    public async Task NothingStored_RegistersEverySlotInEachConfiguredEnvironment()
    {
        using var h = new Harness(live: true, test: true);

        await h.RunAsync();

        Assert.Equal(
            [(ShopUrl, false), (SubscriptionUrl, false), (ShopUrl, true), (SubscriptionUrl, true)],
            h.Vipps.Registered.Select(r => (r.Url, r.IsTest)));
        Assert.Equal(VippsEvents.Payments, h.Vipps.Registered[0].Events);
        Assert.Equal(VippsEvents.Recurring, h.Vipps.Registered[1].Events);
        Assert.StartsWith("new-live-", h.Store.Id("shop", false));
        Assert.StartsWith("new-test-", h.Store.Id("subscription", true));
        Assert.StartsWith("protected:secret-", h.Store.Secret("shop", false));
        Assert.NotEqual(h.Store.Secret("shop", false), h.Store.Secret("shop", true));
        Assert.Equal(1, h.Store.Saved);
    }

    [Fact]
    public async Task OnlyLiveConfigured_TheTestEnvironmentIsLeftAlone()
    {
        using var h = new Harness(live: true, test: false);

        await h.RunAsync();

        Assert.DoesNotContain("list True", h.Vipps.Calls);
        Assert.All(h.Vipps.Registered, r => Assert.False(r.IsTest));
        Assert.NotNull(h.Store.Id("shop", false));
        Assert.Null(h.Store.Id("shop", true));
    }

    [Fact]
    public async Task StoredRegistrationsStillListed_AreKept()
    {
        using var h = new Harness(live: true, test: true);
        h.Stored("shop", false, "live-shop", "a");
        h.Stored("subscription", false, "live-sub", "b");
        h.Stored("shop", true, "test-shop", "c");
        h.Stored("subscription", true, "test-sub", "d");
        h.Vipps.Listed[false] = [Hook("live-shop", ShopUrl), Hook("live-sub", SubscriptionUrl)];
        h.Vipps.Listed[true] = [Hook("test-shop", ShopUrl), Hook("test-sub", SubscriptionUrl)];

        await h.RunAsync();

        Assert.Empty(h.Vipps.Registered);
        Assert.Empty(h.Vipps.Deleted);
        Assert.Equal(0, h.Store.Saved);
        Assert.Equal("d", h.Store.Secret("subscription", true));
    }

    [Fact]
    public async Task AListedRegistrationWhoseSecretCannotBeRead_IsReplaced_AndTheOldOneDeleted()
    {
        using var h = new Harness(live: true, test: false);
        h.Stored("shop", false, "live-shop", "broken:keys-gone");
        h.Stored("subscription", false, "live-sub", "b");
        h.Vipps.Listed[false] = [Hook("live-shop", ShopUrl), Hook("live-sub", SubscriptionUrl)];

        await h.RunAsync();

        Assert.Equal([(ShopUrl, false)], h.Vipps.Registered.Select(r => (r.Url, r.IsTest)));
        Assert.StartsWith("new-live-", h.Store.Id("shop", false));
        Assert.Equal([("live-shop", false)], h.Vipps.Deleted);
        Assert.Equal("live-sub", h.Store.Id("subscription", false));
    }

    [Fact]
    public async Task LegacyTestRegistrationInTheLiveSlot_MovesToTheTestSlot()
    {
        using var h = new Harness(live: true, test: true);
        h.Stored("shop", false, "old-shop", "shop-secret");
        h.Stored("subscription", false, "old-sub", "sub-secret");
        h.Vipps.Listed[true] = [Hook("old-shop", ShopUrl), Hook("old-sub", SubscriptionUrl)];

        await h.RunAsync();

        Assert.Equal("old-shop", h.Store.Id("shop", true));
        Assert.Equal("shop-secret", h.Store.Secret("shop", true));
        Assert.Equal("old-sub", h.Store.Id("subscription", true));
        Assert.StartsWith("new-live-", h.Store.Id("shop", false));
        Assert.StartsWith("new-live-", h.Store.Id("subscription", false));
        Assert.All(h.Vipps.Registered, r => Assert.False(r.IsTest));
        Assert.Empty(h.Vipps.Deleted);
    }

    [Fact]
    public async Task LegacyTestRegistrationInTheLiveSlot_WhenTheTestSlotHasItsOwn_IsDeleted()
    {
        using var h = new Harness(live: true, test: true);
        h.Stored("shop", false, "old-shop", "lost-secret");
        h.Stored("shop", true, "test-shop", "c");
        h.Vipps.Listed[true] = [Hook("old-shop", ShopUrl), Hook("test-shop", ShopUrl)];

        await h.RunAsync();

        Assert.Contains(("old-shop", true), h.Vipps.Deleted);
        Assert.DoesNotContain(("test-shop", true), h.Vipps.Deleted);
        Assert.Equal("test-shop", h.Store.Id("shop", true));
        Assert.StartsWith("new-live-", h.Store.Id("shop", false));
    }

    [Fact]
    public async Task ALiveSlotHoldingTheSameIdAsTheTestSlot_DeletesNothingInTest()
    {
        using var h = new Harness(live: true, test: true);
        h.Stored("shop", false, "test-shop", "c");
        h.Stored("shop", true, "test-shop", "c");
        h.Vipps.Listed[true] = [Hook("test-shop", ShopUrl)];

        await h.RunAsync();

        Assert.DoesNotContain(h.Vipps.Deleted, d => d.IsTest);
        Assert.Equal("test-shop", h.Store.Id("shop", true));
    }

    [Fact]
    public async Task NewRegistrations_ClearTheEndpointsSecretCache()
    {
        using var h = new Harness(live: true, test: false);
        var cache = h.Cache;
        Assert.Null((await cache.GetAsync("shop", h.Store, new PrefixProtector(), default)).Live);

        await h.RunAsync();

        Assert.StartsWith("protected:secret-", (await cache.GetAsync("shop", h.Store, new PrefixProtector(), default)).Live);
    }

    [Fact]
    public async Task StoredRegistrationGoneAtVipps_OthersForTheUrlAreDeletedAfterItIsReplaced()
    {
        using var h = new Harness(live: true, test: false);
        h.Stored("shop", false, "live-shop", "a");
        h.Stored("subscription", false, "gone", "b");
        h.Vipps.Listed[false] = [Hook("live-shop", ShopUrl), Hook("orphan", SubscriptionUrl), Hook("someone-else", "https://other.example.invalid/hook")];

        await h.RunAsync();

        Assert.Equal([("orphan", false)], h.Vipps.Deleted);
        Assert.True(h.Vipps.Calls.IndexOf("register") < h.Vipps.Calls.IndexOf("delete"), string.Join(",", h.Vipps.Calls));
        Assert.Equal("live-shop", h.Store.Id("shop", false));
        Assert.StartsWith("new-live-", h.Store.Id("subscription", false));
    }

    [Fact]
    public async Task RegistrationFails_TheOldRegistrationsForTheUrlAreKept()
    {
        // Deleting first would leave Vipps with nowhere to send events until the next start.
        using var h = new Harness(live: true, test: false);
        h.Stored("shop", false, "gone", "a");
        h.Vipps.Listed[false] = [Hook("orphan", ShopUrl)];
        h.Vipps.RegisterFails.Add(ShopUrl);

        await h.RunAsync();

        Assert.Empty(h.Vipps.Deleted);
        Assert.Equal("gone", h.Store.Id("shop", false));
    }

    [Fact]
    public async Task DeleteFails_TheNewRegistrationIsStillStored()
    {
        using var h = new Harness(live: true, test: false);
        h.Vipps.Listed[false] = [Hook("orphan", ShopUrl)];
        h.Vipps.DeleteFails.Add("orphan");

        await h.RunAsync();

        Assert.StartsWith("new-live-", h.Store.Id("shop", false));
    }

    [Fact]
    public async Task OtherUrlWithATrailingSlashOrOtherCase_IsDeleted()
    {
        using var h = new Harness(live: true, test: false);
        h.Vipps.Listed[false] = [Hook("slash", ShopUrl + "/"), Hook("upper", ShopUrl.ToUpperInvariant())];

        await h.RunAsync();

        Assert.Equal([("slash", false), ("upper", false)], h.Vipps.Deleted);
    }

    [Fact]
    public async Task TestListingFails_ALiveSlotThatMayHoldATestRegistrationIsLeftAlone()
    {
        using var h = new Harness(live: true, test: true);
        h.Stored("shop", false, "maybe-test", "a");
        h.Vipps.ListFails.Add(true);

        await h.RunAsync();

        Assert.Equal([(SubscriptionUrl, false)], h.Vipps.Registered.Select(r => (r.Url, r.IsTest)));
        Assert.Equal("maybe-test", h.Store.Id("shop", false));
        Assert.Equal("a", h.Store.Secret("shop", false));
    }

    [Fact]
    public async Task ListingFails_NothingIsRegisteredOrDeletedInThatEnvironment()
    {
        using var h = new Harness(live: true, test: true);
        h.Stored("shop", false, "live-shop", "a");
        h.Vipps.ListFails.Add(false);

        await h.RunAsync();

        Assert.All(h.Vipps.Registered, r => Assert.True(r.IsTest));
        Assert.Contains((ShopUrl, true), h.Vipps.Registered.Select(r => (r.Url, r.IsTest)));
        Assert.Empty(h.Vipps.Deleted);
        Assert.Equal("live-shop", h.Store.Id("shop", false));
    }

    [Fact]
    public async Task RegistrationFails_KeepsTheStoredValuesForThatSlot()
    {
        using var h = new Harness(live: true, test: false);
        h.Vipps.RegisterFails.Add(ShopUrl);

        await h.RunAsync();

        Assert.Null(h.Store.Id("shop", false));
        Assert.StartsWith("new-live-", h.Store.Id("subscription", false));
    }

    [Fact]
    public async Task WithoutAPublicBaseUrl_NothingIsCalled()
    {
        using var h = new Harness(live: true, test: true, o => o.PublicBaseUrl = "");

        await h.RunAsync();

        Assert.Empty(h.Vipps.Calls);
    }

    [Fact]
    public async Task ASlotsOwnEventsAreRegistered()
    {
        using var h = new Harness(live: true, test: false, o =>
        {
            o.Slots.Clear();
            o.Slots.Add(new VippsWebhookSlot { Name = "charges", Kind = VippsWebhookKind.Recurring, Path = "/hook", Events = [VippsEvents.ChargeCaptured] });
        });

        await h.RunAsync();

        var registered = Assert.Single(h.Vipps.Registered);
        Assert.Equal("https://api.example.invalid/hook", registered.Url);
        Assert.Equal([VippsEvents.ChargeCaptured], registered.Events);
    }

    [Fact]
    public async Task TheDefaultStore_KeepsRegistrationsInItsTable()
    {
        await using var app = await WebhookTestApp.StartAsync(liveSecret: false, testSecret: false);
        await app.DbAsync(async db =>
        {
            var store = new EfVippsWebhookRegistrationStore<WebhookDbContext>(db);
            await store.SetAsync("shop", false, new VippsWebhookRegistration("a", "s1"), default);
            await store.SetAsync("shop", false, new VippsWebhookRegistration("b", "s2"), default);
            await store.SetAsync("shop", true, new VippsWebhookRegistration("t", "s3"), default);
            await db.SaveChangesAsync();
            await store.SetAsync("shop", true, null, default);
            return await db.SaveChangesAsync();
        });

        var rows = await app.DbAsync(db => db.Set<VippsWebhookRegistrationEntry>().AsNoTracking().ToListAsync());
        var row = Assert.Single(rows);
        Assert.Equal(("shop", false, "b", "s2"), (row.Slot, row.IsTest, row.WebhookId, row.ProtectedSecret));
    }
}
