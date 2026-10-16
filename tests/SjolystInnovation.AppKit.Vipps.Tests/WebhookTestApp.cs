using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace SjolystInnovation.AppKit.Vipps.Tests;

/// <summary>An app entity that the handlers change.</summary>
public class TestOrder
{
    public int Id { get; set; }
    public string Reference { get; set; } = "";
    public int Applied { get; set; }
}

public class WebhookDbContext(DbContextOptions<WebhookDbContext> options) : DbContext(options), IVippsWebhookDbContext
{
    public DbSet<VippsProcessedWebhookEvent> ProcessedWebhookEvents => Set<VippsProcessedWebhookEvent>();
    public DbSet<TestOrder> Orders => Set<TestOrder>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.AddAppKitVippsWebhooks().AddAppKitVippsWebhookRegistrations();
}

/// <summary>What the test handlers do, set by each test.</summary>
internal sealed class HandlerScript
{
    public List<VippsWebhookDelivery<VippsPaymentEvent>> Payments { get; } = [];
    public List<VippsWebhookDelivery<VippsRecurringEvent>> Recurring { get; } = [];
    public int AfterCommits;

    /// <summary>Runs inside the handler, after it changed the order. Defaults to applying the event.</summary>
    public Func<WebhookDbContext, Task<VippsWebhookOutcome>>? Handle { get; set; }

    public async Task<VippsWebhookOutcome> RunAsync(WebhookDbContext db)
    {
        var order = await db.Orders.FirstAsync();
        order.Applied++;
        if (Handle is not null) return await Handle(db);
        return VippsWebhookOutcome.Applied(_ => { Interlocked.Increment(ref AfterCommits); return Task.CompletedTask; });
    }
}

internal sealed class TestHandler(WebhookDbContext db, HandlerScript script) : IVippsPaymentWebhookHandler, IVippsRecurringWebhookHandler
{
    public Task<VippsWebhookOutcome> HandleAsync(VippsWebhookDelivery<VippsPaymentEvent> delivery, CancellationToken cancellationToken)
    {
        lock (script.Payments) script.Payments.Add(delivery);
        return script.RunAsync(db);
    }

    public Task<VippsWebhookOutcome> HandleAsync(VippsWebhookDelivery<VippsRecurringEvent> delivery, CancellationToken cancellationToken)
    {
        lock (script.Recurring) script.Recurring.Add(delivery);
        return script.RunAsync(db);
    }
}

/// <summary>Fails the next saves that change anything besides the processed marker, the way a dropped connection would.</summary>
internal sealed class SaveFaults : SaveChangesInterceptor
{
    public int FailuresLeft;

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        var changesState = eventData.Context!.ChangeTracker.Entries()
            .Any(e => e.Entity is not VippsProcessedWebhookEvent && e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
        if (changesState && Interlocked.Decrement(ref FailuresLeft) >= 0)
            throw new DbUpdateException("Simulated database failure");
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}

/// <summary>A test server with a shop and a subscription webhook on a SQLite file database.</summary>
internal sealed class WebhookTestApp : IAsyncDisposable
{
    public const string LiveSecret = "live-secret";
    public const string TestSecret = "test-secret";
    public const string LiveMsn = "msn-live";
    public const string TestMsn = "msn-test";

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vipps-webhooks-{Guid.NewGuid():N}.db");
    private IHost _host = default!;

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
    public HandlerScript Script { get; } = new();
    public SaveFaults Faults { get; } = new();
    public HttpClient Client { get; private set; } = default!;
    public IServiceProvider Services => _host.Services;

    public static async Task<WebhookTestApp> StartAsync(Action<VippsWebhookOptions>? configure = null, bool liveSecret = true, bool testSecret = true, string? pathBase = null)
    {
        var app = new WebhookTestApp();
        var connection = $"Data Source={app._path};Pooling=False";
        app._host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(s =>
                {
                    s.AddDbContext<WebhookDbContext>(o => o.UseSqlite(connection).AddInterceptors(app.Faults));
                    s.AddSingleton<TimeProvider>(app.Time);
                    s.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
                    s.AddSingleton(app.Script);
                    s.AddScoped<TestHandler>();
                    s.AddScoped<IVippsPaymentWebhookHandler>(sp => sp.GetRequiredService<TestHandler>());
                    s.AddScoped<IVippsRecurringWebhookHandler>(sp => sp.GetRequiredService<TestHandler>());
                    s.AddVipps(o =>
                    {
                        o.Live.MerchantSerialNumber = LiveMsn;
                        o.Test.MerchantSerialNumber = TestMsn;
                    });
                    s.AddAppKitVippsWebhooks<WebhookDbContext>(o =>
                    {
                        o.AddSlot("shop", VippsWebhookKind.Payment, "/api/shop/webhook");
                        o.AddSlot("subscription", VippsWebhookKind.Recurring, "/api/subscriptions/webhook");
                        o.RegisterOnStart = false;
                        configure?.Invoke(o);
                    });
                    s.AddRouting();
                })
                .Configure(web =>
                {
                    if (pathBase is not null) web.UsePathBase(pathBase);
                    web.UseRouting();
                    web.UseEndpoints(e =>
                    {
                        e.MapAppKitVippsWebhook<WebhookDbContext>("shop");
                        e.MapAppKitVippsWebhook<WebhookDbContext>("subscription");
                    });
                }))
            .Build();
        using (var scope = app._host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WebhookDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.Orders.Add(new TestOrder { Reference = "order-1" });
            await db.SaveChangesAsync();
            var store = scope.ServiceProvider.GetRequiredService<IVippsWebhookRegistrationStore>();
            var protector = scope.ServiceProvider.GetRequiredService<IVippsWebhookSecretProtector>();
            foreach (var slot in new[] { "shop", "subscription" })
            {
                if (liveSecret) await store.SetAsync(slot, false, new VippsWebhookRegistration($"{slot}-live", protector.Protect(LiveSecret)), default);
                if (testSecret) await store.SetAsync(slot, true, new VippsWebhookRegistration($"{slot}-test", protector.Protect(TestSecret)), default);
            }
            await db.SaveChangesAsync();
        }
        await app._host.StartAsync();
        app.Client = app._host.GetTestClient();
        return app;
    }

    /// <summary>Sends a body signed the way Vipps signs one.</summary>
    public Task<HttpResponseMessage> PostAsync(string path, byte[] body, string secret = LiveSecret, DateTimeOffset? signedAt = null) =>
        Client.SendAsync(Signed(path, new ByteArrayContent(body), body, secret, signedAt));

    /// <summary>Builds a request signed the way Vipps signs one, with the content to send.</summary>
    public HttpRequestMessage Signed(string path, HttpContent content, byte[] body, string secret = LiveSecret, DateTimeOffset? signedAt = null)
    {
        var date = (signedAt ?? Time.GetUtcNow()).ToString("R");
        var hash = Convert.ToBase64String(SHA256.HashData(body));
        var canonical = $"POST\n{path}\n{date};localhost;{hash}";
        var signature = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(canonical)));
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        request.Headers.TryAddWithoutValidation("x-ms-date", date);
        request.Headers.TryAddWithoutValidation("x-ms-content-sha256", hash);
        request.Headers.TryAddWithoutValidation("Authorization", $"HMAC-SHA256 SignedHeaders=x-ms-date;host;x-ms-content-sha256&Signature={signature}");
        return request;
    }

    public Task<HttpResponseMessage> PostAsync(string path, string body, string secret = LiveSecret) => PostAsync(path, Encoding.UTF8.GetBytes(body), secret);

    public async Task<T> DbAsync<T>(Func<WebhookDbContext, Task<T>> query)
    {
        using var scope = _host.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<WebhookDbContext>());
    }

    public Task<List<VippsProcessedWebhookEvent>> MarkersAsync() => DbAsync(db => db.ProcessedWebhookEvents.AsNoTracking().OrderBy(e => e.Id).ToListAsync());

    public Task<int> AppliedAsync() => DbAsync(async db => (await db.Orders.AsNoTracking().FirstAsync()).Applied);

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        SqliteConnection.ClearAllPools();
        try { File.Delete(_path); } catch (IOException) { }
    }
}
