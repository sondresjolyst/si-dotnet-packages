using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace SjolystInnovation.AppKit.Consent.Tests;

public sealed class ConsentRetentionTests : IAsyncDisposable
{
    private static readonly DateTime Now = new(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;

    public ConsentRetentionTests()
    {
        _connection.Open();
        _provider = new ServiceCollection().AddDbContext<TestDbContext>(o => o.UseSqlite(_connection)).BuildServiceProvider();
        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<TestDbContext>().Database.EnsureCreated();
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private static ConsentRecord Row(DateTime decidedAt, string notice = "v1") =>
        new() { ConsentId = Guid.NewGuid(), Marketing = true, NoticeVersion = notice, SchemaVersion = 1, DecidedAt = decidedAt, Source = "banner" };

    private async Task SeedAsync(params ConsentRecord[] rows)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
        db.ConsentRecords.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private async Task<List<string>> NoticesAsync()
    {
        using var scope = _provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TestDbContext>().ConsentRecords.OrderBy(r => r.Id).Select(r => r.NoticeVersion).ToListAsync();
    }

    private ConsentRetentionService<TestDbContext> Service(AppKitConsentOptions? options = null, IServiceScopeFactory? scopes = null) =>
        new(scopes ?? _provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(options ?? new AppKitConsentOptions()),
            new FakeTimeProvider(Now), NullLogger<ConsentRetentionService<TestDbContext>>.Instance);

    [Fact]
    public void The_default_is_three_years_with_a_leap_day()
    {
        Assert.Equal(1096, AppKitConsentOptions.DefaultKeepForDays);
        Assert.Equal(TimeSpan.FromDays(1096), AppKitConsentOptions.DefaultKeepFor);
        Assert.Equal(AppKitConsentOptions.DefaultKeepFor, new AppKitConsentOptions().KeepFor);
        Assert.Equal(TimeSpan.FromDays(1), ConsentRetentionService<TestDbContext>.Interval);
    }

    [Fact]
    public async Task Only_rows_older_than_the_default_are_deleted()
    {
        var keep = AppKitConsentOptions.DefaultKeepFor;
        await SeedAsync(
            Row(Now - keep - TimeSpan.FromSeconds(1), "old"),
            Row(Now - keep, "edge"),
            Row(Now - keep + TimeSpan.FromSeconds(1), "young"),
            Row(Now, "new"),
            Row(Now - TimeSpan.FromDays(5000), "ancient"));

        Assert.Equal(2, await Service().CleanupAsync(CancellationToken.None));

        Assert.Equal(["edge", "young", "new"], await NoticesAsync());
    }

    [Fact]
    public async Task KeepFor_can_be_changed()
    {
        await SeedAsync(Row(Now - TimeSpan.FromDays(31), "old"), Row(Now - TimeSpan.FromDays(29), "young"));

        Assert.Equal(1, await Service(new AppKitConsentOptions { KeepFor = TimeSpan.FromDays(30) }).CleanupAsync(CancellationToken.None));

        Assert.Equal(["young"], await NoticesAsync());
    }

    [Fact]
    public async Task Nothing_is_deleted_when_every_row_is_young()
    {
        await SeedAsync(Row(Now - TimeSpan.FromDays(400)), Row(Now));

        Assert.Equal(0, await Service().CleanupAsync(CancellationToken.None));

        Assert.Equal(2, (await NoticesAsync()).Count);
    }

    [Fact]
    public async Task A_failed_run_is_logged_and_does_not_throw()
    {
        using var broken = new ServiceCollection().BuildServiceProvider();

        Assert.Equal(0, await Service(scopes: broken.GetRequiredService<IServiceScopeFactory>()).CleanupAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Cancelling_a_run_throws()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await SeedAsync(Row(Now - TimeSpan.FromDays(5000)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().CleanupAsync(cancelled.Token));
    }

    [Fact]
    public async Task The_app_deletes_old_rows_when_it_starts()
    {
        var now = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
        await using var app = await TestApp.StartAsync(addServices: true,
            seed: [Row(now - TimeSpan.FromDays(2000), "old"), Row(now - TimeSpan.FromDays(10), "young")]);

        for (var i = 0; i < 100 && (await app.RowsAsync()).Count > 1; i++)
            await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(["young"], (await app.RowsAsync()).Select(r => r.NoticeVersion));
    }

    [Fact]
    public void AddAppKitConsent_registers_the_retention_job_and_the_options()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddAppKitConsent<TestDbContext>(o => o.KeepFor = TimeSpan.FromDays(7));

        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(ConsentRetentionService<TestDbContext>));
        using var provider = services.BuildServiceProvider();
        Assert.Equal(TimeSpan.FromDays(7), provider.GetRequiredService<IOptions<AppKitConsentOptions>>().Value.KeepFor);
        Assert.Same(TimeProvider.System, provider.GetRequiredService<TimeProvider>());
    }

    [Fact]
    public void Without_configure_the_default_is_used()
    {
        using var provider = new ServiceCollection().AddLogging().AddAppKitConsent<TestDbContext>().BuildServiceProvider();

        Assert.Equal(AppKitConsentOptions.DefaultKeepFor, provider.GetRequiredService<IOptions<AppKitConsentOptions>>().Value.KeepFor);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_KeepFor_that_is_not_positive_is_refused(int days)
    {
        using var provider = new ServiceCollection().AddLogging().AddAppKitConsent<TestDbContext>(o => o.KeepFor = TimeSpan.FromDays(days)).BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<AppKitConsentOptions>>().Value);
    }

    [Fact]
    public async Task The_app_does_not_start_with_a_KeepFor_that_is_not_positive()
    {
        await Assert.ThrowsAsync<OptionsValidationException>(() =>
            TestApp.StartAsync(addServices: true, consent: o => o.KeepFor = TimeSpan.Zero));
    }

    [Fact]
    public void A_registered_TimeProvider_is_kept()
    {
        var fake = new FakeTimeProvider();
        using var provider = new ServiceCollection().AddLogging().AddSingleton<TimeProvider>(fake).AddAppKitConsent<TestDbContext>().BuildServiceProvider();

        Assert.Same(fake, provider.GetRequiredService<TimeProvider>());
    }
}
