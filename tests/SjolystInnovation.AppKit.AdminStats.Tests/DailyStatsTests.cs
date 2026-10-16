using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;
using static SjolystInnovation.AppKit.AdminStats.Tests.TestDb;

namespace SjolystInnovation.AppKit.AdminStats.Tests;

/// <summary>garge's eight cases, ported to the past-day sampler that garge would pass.</summary>
public class GargeBackfillTests
{
    private static int UsersOn(List<DailyStatSnapshot> series, DateOnly date) => series.Single(s => s.Date == date).TotalUsers;

    private static async Task<List<DailyStatSnapshot>> FreezeAndReadAsync(TestDb db, AppKitDailyStats<TestDbContext, DailyStatSnapshot> stats)
    {
        await using var context = db.NewContext();
        await stats.FreezeMissingDaysAsync(context);
        return await stats.GetHistoryAsync(context);
    }

    [Fact]
    public async Task History_climbs_on_sign_up_and_dips_on_deletion()
    {
        await using var db = await CreateAsync();
        var d1 = Today.AddDays(-3);
        var d2 = Today.AddDays(-2);
        await db.AddAsync(MakeUser("u1", d1), MakeUser("u2", d1), MakeUser("u3", d1, deleted: d2));

        var series = await FreezeAndReadAsync(db, db.Garge());

        Assert.Equal(3, UsersOn(series, d1));
        Assert.Equal(2, UsersOn(series, d2));
        Assert.Equal(2, UsersOn(series, Today));
    }

    [Fact]
    public async Task Only_completed_days_are_stored_never_today()
    {
        await using var db = await CreateAsync();
        await db.AddAsync(MakeUser("u1", Today.AddDays(-2)), MakeUser("u2", Today));

        await using (var context = db.NewContext())
            await db.Garge().FreezeMissingDaysAsync(context);

        Assert.Equal(1, (await db.FrozenAsync(Today.AddDays(-1)))!.TotalUsers);
        Assert.Null(await db.FrozenAsync(Today));
    }

    [Fact]
    public async Task A_sign_up_and_deletion_on_the_same_day_nets_to_zero()
    {
        await using var db = await CreateAsync();
        var pastDay = Today.AddDays(-3);
        await db.AddAsync(
            MakeUser("baseline", Today.AddDays(-5)),
            MakeUser("churned-live", Today, deleted: Today),
            MakeUser("churned-past", pastDay, deleted: pastDay));

        var series = await FreezeAndReadAsync(db, db.Garge());

        Assert.Equal(1, UsersOn(series, pastDay));
        Assert.Equal(1, UsersOn(series, Today));
        Assert.Equal(1, (await db.FrozenAsync(pastDay))!.TotalUsers);
    }

    [Fact]
    public async Task Today_is_never_stored_so_a_later_sign_up_the_same_day_shows()
    {
        await using var db = await CreateAsync();
        var stats = db.Garge();
        await db.AddAsync(MakeUser("u1", Today.AddDays(-1)));

        Assert.Equal(1, UsersOn(await FreezeAndReadAsync(db, stats), Today));

        await db.AddAsync(MakeUser("u2", Today));

        Assert.Equal(2, UsersOn(await FreezeAndReadAsync(db, stats), Today));
    }

    [Fact]
    public async Task Stored_rows_are_left_alone_and_only_later_days_are_added()
    {
        await using var db = await CreateAsync();
        var old = Today.AddDays(-10);
        await db.AddAsync(new DailyStatSnapshot { Date = old, TotalUsers = 500, TotalSensors = 10 }, MakeUser("u1", Today.AddDays(-1)));

        int added;
        await using (var context = db.NewContext())
            added = await db.Garge().FreezeMissingDaysAsync(context);

        Assert.Equal(9, added);
        Assert.Equal(500, (await db.FrozenAsync(old))!.TotalUsers);
        // garge carried 500 forward and added the sign-up. The past-day sampler counts the rows that exist instead.
        Assert.Equal(1, (await db.FrozenAsync(Today.AddDays(-1)))!.TotalUsers);
        Assert.Null(await db.FrozenAsync(old.AddDays(-1)));
    }

    [Fact]
    public async Task A_second_run_adds_nothing()
    {
        await using var db = await CreateAsync();
        var stats = db.Garge();
        await db.AddAsync(MakeUser("u1", Today.AddDays(-2)));

        await using var context = db.NewContext();
        await stats.FreezeMissingDaysAsync(context);
        var count = (await db.StoredAsync()).Count;

        Assert.Equal(0, await stats.FreezeMissingDaysAsync(context));
        Assert.Equal(count, (await db.StoredAsync()).Count);
    }

    [Fact]
    public async Task Sensors_take_the_live_count_and_dip_when_removed()
    {
        await using var db = await CreateAsync();
        var stats = db.Garge();
        var twoDaysAgo = Today.AddDays(-2);
        var yesterday = Today.AddDays(-1);
        await db.AddAsync(MakeSensor(1, twoDaysAgo), MakeSensor(2, twoDaysAgo), MakeSensor(3, twoDaysAgo));

        var before = await FreezeAndReadAsync(db, stats);
        Assert.Equal(3, before[^1].TotalSensors);
        Assert.Equal(3, (await db.FrozenAsync(yesterday))!.TotalSensors);

        await using (var context = db.NewContext())
        {
            context.Sensors.Remove(await context.Sensors.SingleAsync(s => s.Id == 3));
            await context.SaveChangesAsync();
        }

        var after = await FreezeAndReadAsync(db, stats);
        Assert.Equal(2, after[^1].TotalSensors);
        Assert.Equal(3, (await db.FrozenAsync(yesterday))!.TotalSensors);
    }

    [Fact]
    public async Task No_activity_adds_nothing_and_the_history_is_empty()
    {
        await using var db = await CreateAsync();
        var stats = db.Garge();
        await using var context = db.NewContext();

        Assert.Equal(0, await stats.FreezeMissingDaysAsync(context));
        Assert.Empty(await stats.GetHistoryAsync(context));
    }
}

public class FreezeMissingDaysTests
{
    [Fact]
    public async Task The_past_day_sampler_rebuilds_each_missed_day_from_its_own_counts()
    {
        await using var db = await CreateAsync();
        await db.AddAsync(
            new DailyStatSnapshot { Date = Today.AddDays(-6) },
            MakeUser("a", Today.AddDays(-5)), MakeUser("b", Today.AddDays(-3)), MakeUser("c", Today.AddDays(-2)));

        await using (var context = db.NewContext())
            Assert.Equal(5, await db.Garge().FreezeMissingDaysAsync(context));

        Assert.Equal([0, 1, 1, 2, 3, 3], (await db.StoredAsync()).Select(s => s.TotalUsers));
    }

    [Fact]
    public async Task A_first_run_starts_at_BackfillFrom_before_the_earliest_day()
    {
        await using var db = await CreateAsync();
        await db.AddAsync(MakeUser("a", Today.AddDays(-20)));
        var stats = db.Stats<DailyStatSnapshot>(o =>
        {
            o.LiveSampler = GargeSamplers.LiveAsync;
            o.AsOfSampler = GargeSamplers.AsOf(db.Time);
            o.EarliestDay = GargeSamplers.EarliestAsync;
            o.BackfillFrom = Today.AddDays(-4);
        });

        await using (var context = db.NewContext())
            await stats.FreezeMissingDaysAsync(context);

        Assert.Equal(Today.AddDays(-4), (await db.StoredAsync())[0].Date);
        Assert.Equal(4, (await db.StoredAsync()).Count);
    }

    [Fact]
    public async Task A_first_run_without_a_start_day_stores_only_yesterday()
    {
        await using var db = await CreateAsync();
        await db.AddAsync(MakeUser("a", Today.AddDays(-20)));
        var stats = db.Stats<DailyStatSnapshot>(o =>
        {
            o.LiveSampler = GargeSamplers.LiveAsync;
            o.AsOfSampler = GargeSamplers.AsOf(db.Time);
        });

        await using (var context = db.NewContext())
            await stats.FreezeMissingDaysAsync(context);

        Assert.Equal([Today.AddDays(-1)], (await db.StoredAsync()).Select(s => s.Date));
    }

    [Fact]
    public async Task MaxBackfillDays_caps_a_stray_early_timestamp()
    {
        await using var db = await CreateAsync();
        await db.AddAsync(MakeUser("epoch", new DateOnly(1970, 1, 1)), MakeUser("a", Today.AddDays(-2)));
        var stats = db.Stats<DailyStatSnapshot>(o =>
        {
            o.LiveSampler = GargeSamplers.LiveAsync;
            o.AsOfSampler = GargeSamplers.AsOf(db.Time);
            o.EarliestDay = GargeSamplers.EarliestAsync;
            o.MaxBackfillDays = 30;
        });

        await using (var context = db.NewContext())
            Assert.Equal(30, await stats.FreezeMissingDaysAsync(context));

        var stored = await db.StoredAsync();
        Assert.Equal(Today.AddDays(-30), stored[0].Date);
        Assert.Equal(Today.AddDays(-1), stored[^1].Date);
        Assert.Equal(2, stored[^1].TotalUsers);
    }

    [Fact]
    public async Task Without_the_past_day_sampler_every_missed_day_gets_the_live_counts()
    {
        await using var db = await CreateAsync();
        await db.AddAsync(
            new DailyStatSnapshot { Date = Today.AddDays(-4) },
            MakeUser("a", Today.AddDays(-3)), MakeUser("b", Today.AddDays(-2)), MakeUser("c", Today));

        await using (var context = db.NewContext())
            Assert.Equal(3, await db.LiveOnly().FreezeMissingDaysAsync(context));

        Assert.Equal([0, 3, 3, 3], (await db.StoredAsync()).Select(s => s.TotalUsers));
    }

    [Fact]
    public async Task Without_the_past_day_sampler_a_first_run_stores_only_yesterday()
    {
        await using var db = await CreateAsync();
        await db.AddAsync(MakeUser("a", Today.AddDays(-20)));
        var stats = db.Stats<DailyStatSnapshot>(o =>
        {
            o.LiveSampler = GargeSamplers.LiveAsync;
            o.BackfillFrom = Today.AddDays(-10);
            o.EarliestDay = GargeSamplers.EarliestAsync;
        });

        await using (var context = db.NewContext())
            await stats.FreezeMissingDaysAsync(context);

        Assert.Equal([Today.AddDays(-1)], (await db.StoredAsync()).Select(s => s.Date));
    }

    [Fact]
    public async Task Nothing_is_stored_before_the_first_completed_day()
    {
        await using var db = await CreateAsync();
        await db.AddAsync(MakeUser("a", Today));

        await using var context = db.NewContext();
        Assert.Equal(0, await db.Garge().FreezeMissingDaysAsync(context));
        Assert.Empty(await db.StoredAsync());
    }
}

public class ConcurrentWriterTests
{
    /// <summary>A past-day sampler that lets another writer store <paramref name="taken"/> first, as a second replica would.</summary>
    private static AppKitDailyStats<TestDbContext, DailyStatSnapshot> WithRival(TestDb db, params DateOnly[] taken)
    {
        var asOf = GargeSamplers.AsOf(db.Time);
        var done = false;
        return db.Stats<DailyStatSnapshot>(o =>
        {
            o.LiveSampler = GargeSamplers.LiveAsync;
            o.AsOfSampler = async (context, day, ct) =>
            {
                if (!done)
                {
                    done = true;
                    await using var rival = db.NewContext();
                    rival.AddRange(taken.Select(d => new DailyStatSnapshot { Date = d, TotalUsers = 99 }));
                    await rival.SaveChangesAsync(ct);
                }
                return await asOf(context, day, ct);
            };
            o.BackfillFrom = Today.AddDays(-3);
        });
    }

    [Fact]
    public async Task A_day_that_another_writer_stored_first_is_skipped()
    {
        await using var db = await CreateAsync();
        await db.AddAsync(MakeUser("a", Today.AddDays(-5)));
        var stats = WithRival(db, Today.AddDays(-2));

        await using var context = db.NewContext();
        Assert.Equal(2, await stats.FreezeMissingDaysAsync(context));

        var stored = await db.StoredAsync();
        Assert.Equal([Today.AddDays(-3), Today.AddDays(-2), Today.AddDays(-1)], stored.Select(s => s.Date));
        Assert.Equal([1, 99, 1], stored.Select(s => s.TotalUsers));
        Assert.DoesNotContain(context.ChangeTracker.Entries(), e => e.State == EntityState.Added);
    }

    [Fact]
    public async Task Every_day_stored_by_another_writer_adds_nothing_and_does_not_throw()
    {
        await using var db = await CreateAsync();
        var stats = WithRival(db, Today.AddDays(-3), Today.AddDays(-2), Today.AddDays(-1));

        await using var context = db.NewContext();
        Assert.Equal(0, await stats.FreezeMissingDaysAsync(context));
        Assert.All(await db.StoredAsync(), s => Assert.Equal(99, s.TotalUsers));
    }

    [Fact]
    public async Task A_save_that_fails_for_another_reason_still_throws()
    {
        await using var db = await CreateAsync();
        await db.AddAsync(MakeUser("a", Today.AddDays(-2)));
        await using var context = db.NewContext(new FailingSaves());

        await Assert.ThrowsAsync<DbUpdateException>(() => db.Garge().FreezeMissingDaysAsync(context, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Concurrent_runs_on_separate_contexts_store_each_day_once()
    {
        await using var db = await CreateAsync(file: true);
        await db.AddAsync(MakeUser("a", Today.AddDays(-30)));
        var stats = db.Stats<DailyStatSnapshot>(o =>
        {
            o.LiveSampler = GargeSamplers.LiveAsync;
            o.AsOfSampler = GargeSamplers.AsOf(db.Time);
            o.BackfillFrom = Today.AddDays(-30);
        });

        // Both runs read an empty table before either saves, so both try to store all thirty days.
        var bothRead = new Barrier(2);
        var gate = new BlockAfterRead(bothRead);
        await using var first = db.NewContext(gate);
        await using var second = db.NewContext(gate);
        var results = await Task.WhenAll(
            Task.Run(() => stats.FreezeMissingDaysAsync(first)),
            Task.Run(() => stats.FreezeMissingDaysAsync(second)));

        Assert.Equal(30, results.Sum());
        Assert.Equal(30, (await db.StoredAsync()).Count);
    }
}

/// <summary>Fails every save, to stand in for a database that refuses the write for a reason other than a duplicate day.</summary>
public sealed class FailingSaves : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
        throw new DbUpdateException("The database is down.");
}

/// <summary>Holds each context's first save until both contexts reach it, so both have read the table first.</summary>
public sealed class BlockAfterRead(Barrier barrier) : SaveChangesInterceptor
{
    private readonly HashSet<DbContext> _seen = [];

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        bool first;
        lock (_seen)
            first = _seen.Add(eventData.Context!);
        if (first)
            barrier.SignalAndWait(TimeSpan.FromSeconds(10), cancellationToken);
        return ValueTask.FromResult(result);
    }
}
