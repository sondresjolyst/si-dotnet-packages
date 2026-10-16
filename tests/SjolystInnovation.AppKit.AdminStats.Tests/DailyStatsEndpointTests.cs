using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;
using static SjolystInnovation.AppKit.AdminStats.Tests.TestDb;

namespace SjolystInnovation.AppKit.AdminStats.Tests;

public class DailyStatsEndpointTests
{
    private static readonly ShapeSnapshot[] TwoDays =
    [
        new() { Date = Today.AddDays(-3), TotalUsers = 1, StorageBytes = 10, Note = "x", Optional = 1 },
        new() { Date = Today.AddDays(-2), TotalUsers = 2, StorageBytes = 20 },
    ];

    [Fact]
    public async Task History_answers_the_stored_days_and_today_in_the_DailyStatDto_shape()
    {
        await using var app = await TestApp.StartAsync(seed: TwoDays);

        var response = await app.GetAsync("/api/admin/stats/history");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            """[{"date":"2026-10-13","totalUsers":1,"storageBytes":10},{"date":"2026-10-14","totalUsers":2,"storageBytes":20},{"date":"2026-10-16","totalUsers":3,"storageBytes":5000000000}]""",
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task From_sets_the_first_day()
    {
        await using var app = await TestApp.StartAsync(seed: TwoDays);

        var body = await (await app.GetAsync("/api/admin/stats/history?from=2026-10-14")).Content.ReadAsStringAsync();

        Assert.Equal("""[{"date":"2026-10-14","totalUsers":2,"storageBytes":20},{"date":"2026-10-16","totalUsers":3,"storageBytes":5000000000}]""", body);
    }

    [Fact]
    public async Task From_after_today_answers_an_empty_list()
    {
        await using var app = await TestApp.StartAsync(seed: TwoDays);

        Assert.Equal("[]", await (await app.GetAsync("/api/admin/stats/history?from=2026-10-17")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_malformed_from_answers_400()
    {
        await using var app = await TestApp.StartAsync(seed: TwoDays);

        Assert.Equal(HttpStatusCode.BadRequest, (await app.GetAsync("/api/admin/stats/history?from=16.10.2026")).StatusCode);
    }

    [Fact]
    public async Task An_empty_table_and_zero_counts_answer_an_empty_list()
    {
        await using var app = await TestApp.StartAsync();
        app.Live = new ShapeSnapshot { Note = "not a counter", Optional = 5, Small = 1, Flag = true, Amount = 2m };

        Assert.Equal("[]", await (await app.GetAsync("/api/admin/stats/history")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_empty_table_with_counts_today_answers_today()
    {
        await using var app = await TestApp.StartAsync();

        Assert.Equal("""[{"date":"2026-10-16","totalUsers":3,"storageBytes":5000000000}]""", await (await app.GetAsync("/api/admin/stats/history")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task History_does_not_write_even_with_missed_days()
    {
        await using var app = await TestApp.StartAsync(seed: TwoDays);

        await app.GetAsync("/api/admin/stats/history");

        Assert.Equal(2, (await app.StoredAsync()).Count);
    }

    [Fact]
    public async Task History_needs_a_signed_in_admin()
    {
        await using var app = await TestApp.StartAsync(seed: TwoDays);

        Assert.Equal(HttpStatusCode.Unauthorized, (await app.GetAsync("/api/admin/stats/history", roles: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.GetAsync("/api/admin/stats/history", roles: "Default")).StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public async Task Mapping_with_a_blank_policy_throws(string? policy)
    {
        var error = await Record.ExceptionAsync(() => TestApp.StartAsync(endpoints: o => o.Policy = policy!));

        Assert.IsAssignableFrom<ArgumentException>(error);
    }

    [Fact]
    public async Task History_follows_the_configured_policy()
    {
        await using var app = await TestApp.StartAsync(endpoints: o => o.Policy = "Owner", seed: TwoDays);

        Assert.Equal(HttpStatusCode.Forbidden, (await app.GetAsync("/api/admin/stats/history", roles: "Admin")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.GetAsync("/api/admin/stats/history", roles: "Owner")).StatusCode);
    }
}

public class DailyStatsServiceTests
{
    [Fact]
    public async Task The_hosted_service_stores_the_missed_days_when_the_app_starts()
    {
        await using var app = await TestApp.StartAsync(runService: true, seed: [new ShapeSnapshot { Date = Today.AddDays(-4) }]);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        List<ShapeSnapshot> stored;
        while ((stored = await app.StoredAsync()).Count < 4 && DateTime.UtcNow < deadline)
            await Task.Delay(20, TestContext.Current.CancellationToken);

        Assert.Equal([Today.AddDays(-4), Today.AddDays(-3), Today.AddDays(-2), Today.AddDays(-1)], stored.Select(s => s.Date));
        Assert.Equal(3, stored[^1].TotalUsers);
    }

    [Fact]
    public async Task A_failed_run_is_logged_and_the_service_keeps_going()
    {
        await using var db = await CreateAsync();
        var services = new ServiceCollection();
        services.AddScoped(_ => db.NewContext(new FailingSaves()));
        services.AddSingleton<TimeProvider>(db.Time);
        services.AddLogging();
        services.AddAppKitDailyStats<TestDbContext, DailyStatSnapshot>(o => o.LiveSampler = GargeSamplers.LiveAsync);
        await using var provider = services.BuildServiceProvider();
        var service = (DailyStatsService<TestDbContext, DailyStatSnapshot>)provider.GetServices<IHostedService>().Single();

        Assert.False(await service.RunOnceAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_cancellation_that_is_not_the_app_stopping_is_a_failed_run()
    {
        await using var db = await CreateAsync();
        var services = new ServiceCollection();
        services.AddScoped(_ => db.NewContext());
        services.AddSingleton<TimeProvider>(db.Time);
        services.AddLogging();
        services.AddAppKitDailyStats<TestDbContext, DailyStatSnapshot>(o => o.LiveSampler = (_, _) => throw new TaskCanceledException("A command timed out."));
        await using var provider = services.BuildServiceProvider();
        var service = (DailyStatsService<TestDbContext, DailyStatSnapshot>)provider.GetServices<IHostedService>().Single();

        Assert.False(await service.RunOnceAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(4, 8)]
    [InlineData(9, 256)]
    [InlineData(10, 360)]
    [InlineData(1000, 360)]
    public void A_failed_run_is_retried_after_a_doubling_backoff_up_to_the_interval(int failures, int minutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(minutes), DailyStatsService<TestDbContext, DailyStatSnapshot>.RetryDelay(failures, TimeSpan.FromHours(6)));
    }

    [Fact]
    public async Task The_service_retries_a_failed_run_after_a_minute()
    {
        var calls = 0;
        await using var app = await TestApp.StartAsync(runService: true, seed: [new ShapeSnapshot { Date = Today.AddDays(-2) }], stats: o =>
        {
            var live = o.LiveSampler!;
            o.LiveSampler = (db, ct) => Interlocked.Increment(ref calls) == 1 ? throw new InvalidOperationException("The database is down.") : live(db, ct);
        });
        await WaitAsync(() => Volatile.Read(ref calls) >= 1);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(await app.StoredAsync(), s => s.Date == Today.AddDays(-1));

        app.Time.Advance(TimeSpan.FromMinutes(1));

        await WaitAsync(async () => (await app.StoredAsync()).Count == 2);
        Assert.Equal(Today.AddDays(-1), (await app.StoredAsync())[^1].Date);
    }

    private static Task WaitAsync(Func<bool> condition) => WaitAsync(() => Task.FromResult(condition()));

    private static async Task WaitAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("2026-10-16T12:00:00Z", 0, 6 * 3600)]
    [InlineData("2026-10-16T22:00:00Z", 0, 2 * 3600 + 60)]
    [InlineData("2026-10-16T23:59:30Z", 0, 90)]
    [InlineData("2026-10-16T23:59:30+02:00", 0, 2 * 3600 + 90)]
    [InlineData("2026-10-17T00:00:20Z", -1, 40)]
    [InlineData("2026-10-17T00:05:00Z", -1, 0)]
    [InlineData("2026-10-17T00:05:00Z", 0, 6 * 3600)]
    public void The_next_run_is_after_the_interval_or_just_after_UTC_midnight(string now, int runDayOffset, int seconds)
    {
        var at = DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture);
        var runDay = DateOnly.FromDateTime(at.UtcDateTime).AddDays(runDayOffset);

        var delay = DailyStatsService<TestDbContext, DailyStatSnapshot>.NextDelay(at, TimeSpan.FromHours(6), runDay);

        Assert.Equal(TimeSpan.FromSeconds(seconds), delay);
    }

    [Fact]
    public async Task Starting_without_a_live_sampler_fails()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddAppKitDailyStats<TestDbContext, DailyStatSnapshot>(_ => { });
        using var host = builder.Build();

        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));
        Assert.Contains("LiveSampler", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(50 * 24)]
    public async Task Starting_with_an_interval_out_of_range_fails(int hours)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddAppKitDailyStats<TestDbContext, DailyStatSnapshot>(o =>
        {
            o.LiveSampler = GargeSamplers.LiveAsync;
            o.Interval = TimeSpan.FromHours(hours);
        });
        using var host = builder.Build();

        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));
        Assert.Contains("Interval", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_snapshot_counts_only_public_int_and_long_properties_apart_from_Id()
    {
        Assert.True(DailyStatColumns<ShapeSnapshot>.IsZero(new ShapeSnapshot { Id = 9, Note = "n", Optional = 3, Small = 4, Flag = true, Amount = 5m, Date = Today }));
        Assert.False(DailyStatColumns<ShapeSnapshot>.IsZero(new ShapeSnapshot { StorageBytes = 1 }));
    }
}
