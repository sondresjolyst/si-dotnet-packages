using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace SjolystInnovation.AppKit.Consent.Tests;

public sealed class ConsentRetentionTests : IAsyncDisposable
{
    private static readonly DateTime Now = new(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;
    private readonly FakeLogger<ConsentRetentionService<TestDbContext>> _logger = new();

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

    private ConsentRetentionService<TestDbContext> Service(AppKitConsentOptions? options = null, IServiceScopeFactory? scopes = null, int? batchSize = null)
    {
        var factory = scopes ?? _provider.GetRequiredService<IServiceScopeFactory>();
        var settings = Options.Create(options ?? new AppKitConsentOptions());
        var clock = new FakeTimeProvider(Now);
        // Without a batch size the service keeps its own default.
        return batchSize is { } size ? new(factory, settings, clock, _logger) { BatchSize = size } : new(factory, settings, clock, _logger);
    }

    [Fact]
    public void The_default_is_three_years_with_a_leap_day()
    {
        Assert.Equal(1096, AppKitConsentOptions.DefaultKeepForDays);
        Assert.Equal(TimeSpan.FromDays(1096), AppKitConsentOptions.DefaultKeepFor);
        Assert.Equal(AppKitConsentOptions.DefaultKeepFor, new AppKitConsentOptions().KeepFor);
        Assert.Equal(365, AppKitConsentOptions.MinimumKeepForDays);
        Assert.Equal(TimeSpan.FromDays(365), AppKitConsentOptions.MinimumKeepFor);
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
        Assert.Contains(_logger.Collector.GetSnapshot(), l => l.Level == LogLevel.Information && l.Message.StartsWith("Deleted 2 consent record(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task KeepFor_can_be_changed()
    {
        await SeedAsync(Row(Now - TimeSpan.FromDays(401), "old"), Row(Now - TimeSpan.FromDays(399), "young"));

        Assert.Equal(1, await Service(new AppKitConsentOptions { KeepFor = TimeSpan.FromDays(400) }).CleanupAsync(CancellationToken.None));

        Assert.Equal(["young"], await NoticesAsync());
    }

    [Fact]
    public async Task Old_rows_are_deleted_in_batches_until_none_are_left()
    {
        var old = Now - TimeSpan.FromDays(2000);
        await SeedAsync([.. Enumerable.Range(0, 5).Select(i => Row(old.AddMinutes(i), $"old{i}")), Row(Now, "new")]);

        Assert.Equal(5, await Service(batchSize: 2).CleanupAsync(CancellationToken.None));

        Assert.Equal(["new"], await NoticesAsync());
    }

    [Fact]
    public async Task A_relational_provider_deletes_each_batch_with_one_statement()
    {
        var old = Now - TimeSpan.FromDays(2000);
        await SeedAsync([.. Enumerable.Range(0, 5).Select(i => Row(old.AddMinutes(i), $"old{i}")), Row(Now, "new")]);
        var commands = new CommandLog();
        using var provider = new ServiceCollection().AddDbContext<TestDbContext>(o => o.UseSqlite(_connection).AddInterceptors(commands)).BuildServiceProvider();

        Assert.Equal(5, await Service(scopes: provider.GetRequiredService<IServiceScopeFactory>(), batchSize: 2).CleanupAsync(CancellationToken.None));

        Assert.Equal(3, commands.Texts.Count(t => t.TrimStart().StartsWith("DELETE", StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(commands.Texts, t => t.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["new"], await NoticesAsync());
    }

    private sealed class CommandLog : DbCommandInterceptor
    {
        public List<string> Texts { get; } = [];

        /// <summary>Runs after each statement with the number of statements so far.</summary>
        public Action<int>? AfterNonQuery { get; init; }

        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            AfterNonQuery?.Invoke(Texts.Count);
            return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Texts.Add(command.CommandText);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Texts.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private async Task<ServiceProvider> SeedOldAsync(CommandLog commands)
    {
        var old = Now - TimeSpan.FromDays(2000);
        await SeedAsync([.. Enumerable.Range(0, 5).Select(i => Row(old.AddMinutes(i), $"old{i}")), Row(Now, "new")]);
        return new ServiceCollection().AddDbContext<TestDbContext>(o => o.UseSqlite(_connection).AddInterceptors(commands)).BuildServiceProvider();
    }

    [Fact]
    public async Task Each_batch_is_logged_at_debug_level_with_the_running_total()
    {
        using var provider = await SeedOldAsync(new CommandLog());

        await Service(scopes: provider.GetRequiredService<IServiceScopeFactory>(), batchSize: 2).CleanupAsync(CancellationToken.None);

        Assert.Equal(
            ["Deleted a batch of 2 consent record(s), 2 in this run.", "Deleted a batch of 2 consent record(s), 4 in this run.", "Deleted a batch of 1 consent record(s), 5 in this run."],
            _logger.Collector.GetSnapshot().Where(l => l.Level == LogLevel.Debug).Select(l => l.Message));
    }

    [Fact]
    public async Task A_run_that_fails_after_a_batch_logs_the_rows_it_deleted()
    {
        using var provider = await SeedOldAsync(new CommandLog { AfterNonQuery = n => { if (n == 2) throw new InvalidOperationException("boom"); } });

        Assert.Equal(2, await Service(scopes: provider.GetRequiredService<IServiceScopeFactory>(), batchSize: 2).CleanupAsync(CancellationToken.None));

        Assert.Contains(_logger.Collector.GetSnapshot(), l => l.Level == LogLevel.Error && l.Message == "Deleting old consent records failed after 2 record(s) were deleted.");
    }

    [Fact]
    public async Task A_run_cancelled_after_a_batch_logs_the_rows_it_deleted_and_throws()
    {
        using var cancel = new CancellationTokenSource();
        using var provider = await SeedOldAsync(new CommandLog { AfterNonQuery = n => { if (n == 1) cancel.Cancel(); } });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service(scopes: provider.GetRequiredService<IServiceScopeFactory>(), batchSize: 2).CleanupAsync(cancel.Token));

        Assert.Contains(_logger.Collector.GetSnapshot(), l => l.Message == "Deleting old consent records was cancelled after 2 record(s) were deleted.");
        Assert.Equal(4, (await NoticesAsync()).Count);
        Assert.DoesNotContain(_logger.Collector.GetSnapshot(), l => l.Level == LogLevel.Error);
    }

    [Fact]
    public async Task The_cutoff_is_fixed_for_the_whole_run()
    {
        var time = new FakeTimeProvider(Now);
        var old = Now - AppKitConsentOptions.DefaultKeepFor;
        // Two rows past the cutoff, and two that the clock moving a day during the run would also catch.
        await SeedAsync(Row(old.AddDays(-2), "a"), Row(old.AddDays(-1), "b"), Row(old.AddHours(1), "c"), Row(old.AddHours(2), "d"));
        using var provider = new ServiceCollection().AddDbContext<TestDbContext>(o => o.UseSqlite(_connection)
            .AddInterceptors(new CommandLog { AfterNonQuery = _ => time.Advance(TimeSpan.FromDays(1)) })).BuildServiceProvider();
        var service = new ConsentRetentionService<TestDbContext>(provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new AppKitConsentOptions()), time, _logger) { BatchSize = 1 };

        Assert.Equal(2, await service.CleanupAsync(CancellationToken.None));

        Assert.Equal(["c", "d"], await NoticesAsync());
    }

    [Fact]
    public async Task A_backlog_of_exactly_one_batch_is_deleted()
    {
        var old = Now - TimeSpan.FromDays(2000);
        await SeedAsync(Row(old, "a"), Row(old, "b"), Row(Now, "new"));

        Assert.Equal(2, await Service(batchSize: 2).CleanupAsync(CancellationToken.None));

        Assert.Equal(["new"], await NoticesAsync());
    }

    [Fact]
    public void The_default_batch_is_1000_rows()
    {
        Assert.Equal(1000, Service().BatchSize);
    }

    // A batch that is not removed would loop forever, so the test has a time limit.
    [Fact(Timeout = 15000)]
    public async Task The_in_memory_provider_deletes_old_rows_in_batches_too()
    {
        var name = Guid.NewGuid().ToString();
        using var provider = new ServiceCollection().AddDbContext<TestDbContext>(o => o.UseInMemoryDatabase(name)).BuildServiceProvider();
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            db.ConsentRecords.AddRange([.. Enumerable.Range(0, 5).Select(i => Row(Now - TimeSpan.FromDays(2000 + i), $"old{i}")), Row(Now, "new")]);
            await db.SaveChangesAsync();
        }

        var deleted = await Service(scopes: provider.GetRequiredService<IServiceScopeFactory>(), batchSize: 2).CleanupAsync(TestContext.Current.CancellationToken);

        Assert.Equal(5, deleted);
        using var check = provider.CreateScope();
        Assert.Equal(["new"], await check.ServiceProvider.GetRequiredService<TestDbContext>().ConsentRecords.Select(r => r.NoticeVersion).ToListAsync());
        Assert.DoesNotContain(_logger.Collector.GetSnapshot(), l => l.Level == LogLevel.Error);
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
        Assert.Contains(_logger.Collector.GetSnapshot(), l => l.Level == LogLevel.Error && l.Message == "Deleting old consent records failed after 0 record(s) were deleted." && l.Exception is not null);
    }

    [Fact]
    public async Task Cancelling_a_run_throws()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await SeedAsync(Row(Now - TimeSpan.FromDays(5000)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().CleanupAsync(cancelled.Token));
        Assert.Single(await NoticesAsync());
        Assert.DoesNotContain(_logger.Collector.GetSnapshot(), l => l.Level == LogLevel.Error);
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
        services.AddAppKitConsent<TestDbContext>(o => o.KeepFor = TimeSpan.FromDays(700));

        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(ConsentRetentionService<TestDbContext>));
        using var provider = services.BuildServiceProvider();
        Assert.Equal(TimeSpan.FromDays(700), provider.GetRequiredService<IOptions<AppKitConsentOptions>>().Value.KeepFor);
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
    [InlineData(30)]
    [InlineData(364)]
    public void A_KeepFor_under_365_days_is_refused(int days)
    {
        using var provider = new ServiceCollection().AddLogging().AddAppKitConsent<TestDbContext>(o => o.KeepFor = TimeSpan.FromDays(days)).BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<AppKitConsentOptions>>().Value);
    }

    [Fact]
    public void A_KeepFor_of_365_days_is_allowed()
    {
        using var provider = new ServiceCollection().AddLogging().AddAppKitConsent<TestDbContext>(o => o.KeepFor = TimeSpan.FromDays(365)).BuildServiceProvider();

        Assert.Equal(TimeSpan.FromDays(365), provider.GetRequiredService<IOptions<AppKitConsentOptions>>().Value.KeepFor);
    }

    [Fact]
    public async Task The_app_does_not_start_with_a_KeepFor_under_365_days()
    {
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() =>
            TestApp.StartAsync(addServices: true, consent: o => o.KeepFor = TimeSpan.FromDays(364)));
        Assert.Contains("at least 365 days", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_registered_TimeProvider_is_kept()
    {
        var fake = new FakeTimeProvider();
        using var provider = new ServiceCollection().AddLogging().AddSingleton<TimeProvider>(fake).AddAppKitConsent<TestDbContext>().BuildServiceProvider();

        Assert.Same(fake, provider.GetRequiredService<TimeProvider>());
    }
}
