using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace SjolystInnovation.AppKit.AdminStats;

/// <summary>
/// A row of your app's daily statistics. Your entity adds its own <c>int</c> or <c>long</c> counters and
/// keeps a unique index on <see cref="Date"/>. The history's JSON keys come from the counters' property
/// names, so keep the names your frontend reads.
/// </summary>
public interface IAppKitDailyStat
{
    /// <summary>The UTC day that the counts belong to.</summary>
#pragma warning disable CA1716 // The apps' entities and tables already call the column Date.
    DateOnly Date { get; set; }
#pragma warning restore CA1716
}

/// <summary>The options for <see cref="DailyStatsServiceCollectionExtensions.AddAppKitDailyStats{TContext, TSnapshot}"/>.</summary>
/// <typeparam name="TContext">Your app's <c>DbContext</c>.</typeparam>
/// <typeparam name="TSnapshot">Your app's snapshot entity.</typeparam>
public sealed class AppKitDailyStatsOptions<TContext, TSnapshot>
    where TContext : DbContext
    where TSnapshot : class, IAppKitDailyStat
{
    /// <summary>
    /// The sampler for the counts right now. It returns a new snapshot each time, and the package sets
    /// <see cref="IAppKitDailyStat.Date"/>. Required.
    /// </summary>
    public Func<TContext, CancellationToken, Task<TSnapshot>>? LiveSampler { get; set; }

    /// <summary>
    /// The sampler for the counts at the end of a past UTC day, from timestamps such as <c>CreatedAt</c> and
    /// <c>DeletedAt</c>. With it, the package rebuilds each missed day from its own counts. For a counter that
    /// your data cannot rebuild, such as hard-deleted rows, return the live value. Without this sampler, every
    /// missed day gets the counts at the time the run happens, not at the end of that day, so a gap of several
    /// days shows as a flat line. Optional.
    /// </summary>
    public Func<TContext, DateOnly, CancellationToken, Task<TSnapshot>>? AsOfSampler { get; set; }

    /// <summary>
    /// The first day to backfill when the table is empty. It applies only with <see cref="AsOfSampler"/>, and
    /// it wins over <see cref="EarliestDay"/>. Optional.
    /// </summary>
    public DateOnly? BackfillFrom { get; set; }

    /// <summary>
    /// A function that finds the earliest day with any data, such as the first sign-up, to backfill from when
    /// the table is empty. When it returns <see langword="null"/>, nothing has happened yet and the package
    /// stores nothing. It applies only with <see cref="AsOfSampler"/>. Optional.
    /// </summary>
    public Func<TContext, CancellationToken, Task<DateOnly?>>? EarliestDay { get; set; }

    /// <summary>
    /// The most days that one run stores. Older missed days are left out, which also guards against a stray
    /// timestamp such as <c>0001-01-01</c>. The first backfill calls <see cref="AsOfSampler"/> once per day, one
    /// after the other, on every replica that starts with an empty table. Defaults to 400.
    /// </summary>
    public int MaxBackfillDays { get; set; } = 400;

    /// <summary>
    /// The longest wait between runs. The service also runs just after each UTC midnight. At most about 49 days,
    /// the longest wait <see cref="Task.Delay(TimeSpan)"/> takes. Defaults to 6 hours.
    /// </summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(6);
}

/// <summary>Registers the daily statistics.</summary>
public static class DailyStatsServiceCollectionExtensions
{
    // The longest wait that Task.Delay takes.
    internal static readonly TimeSpan MaxInterval = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    /// <summary>
    /// Registers <see cref="AppKitDailyStats{TContext, TSnapshot}"/> and a hosted service that stores the
    /// missed completed days when the app starts, just after each UTC midnight, and at least every
    /// <see cref="AppKitDailyStatsOptions{TContext, TSnapshot}.Interval"/>.
    /// </summary>
    /// <typeparam name="TContext">Your app's <c>DbContext</c>, which maps <typeparamref name="TSnapshot"/>.</typeparam>
    /// <typeparam name="TSnapshot">Your app's snapshot entity.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">A callback that sets the samplers and options.</param>
    /// <returns>The service collection, for chaining.</returns>
    /// <exception cref="OptionsValidationException">When the host starts without a live sampler, with an interval that is not positive or is over the maximum, or with a backfill cap below 1.</exception>
    public static IServiceCollection AddAppKitDailyStats<TContext, TSnapshot>(this IServiceCollection services,
        Action<AppKitDailyStatsOptions<TContext, TSnapshot>> configure)
        where TContext : DbContext
        where TSnapshot : class, IAppKitDailyStat
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        services.AddOptions<AppKitDailyStatsOptions<TContext, TSnapshot>>()
            .Configure(configure)
            .Validate(o => o.LiveSampler is not null, "AddAppKitDailyStats needs a LiveSampler.")
            .Validate(o => o.MaxBackfillDays > 0, "MaxBackfillDays must be at least 1.")
            .Validate(o => o.Interval > TimeSpan.Zero && o.Interval <= MaxInterval, "Interval must be positive and at most 49 days.")
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<AppKitDailyStats<TContext, TSnapshot>>();
        services.AddHostedService<DailyStatsService<TContext, TSnapshot>>();
        return services;
    }
}

/// <summary>
/// Stores one row per completed UTC day and reads the history. Today is never stored. It is sampled live on
/// every read, so a change later in the day still shows.
/// </summary>
/// <typeparam name="TContext">Your app's <c>DbContext</c>.</typeparam>
/// <typeparam name="TSnapshot">Your app's snapshot entity.</typeparam>
public sealed class AppKitDailyStats<TContext, TSnapshot>
    where TContext : DbContext
    where TSnapshot : class, IAppKitDailyStat
{
    private readonly AppKitDailyStatsOptions<TContext, TSnapshot> _options;
    private readonly TimeProvider _time;

    /// <summary>Creates the service. The container calls this.</summary>
    /// <param name="options">The options.</param>
    /// <param name="time">The clock.</param>
    public AppKitDailyStats(IOptions<AppKitDailyStatsOptions<TContext, TSnapshot>> options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _time = time;
    }

    /// <summary>The current UTC day.</summary>
    public DateOnly Today => DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);

    /// <summary>
    /// Stores a row for each completed day after the latest stored row, through yesterday. Existing rows are
    /// never changed. A day that another writer stores at the same time is skipped, not an error.
    /// </summary>
    /// <remarks>
    /// The method saves <paramref name="db"/>, so pass a context without other pending changes and without an
    /// open transaction. A duplicate day fails its insert, which aborts an open transaction in PostgreSQL. The
    /// hosted service uses a scope of its own.
    /// </remarks>
    /// <param name="db">The context to read and save.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of rows this call stored.</returns>
    public async Task<int> FreezeMissingDaysAsync(TContext db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var yesterday = Today.AddDays(-1);
        var last = await db.Set<TSnapshot>().MaxAsync(s => (DateOnly?)s.Date, cancellationToken);

        DateOnly start;
        if (last is { } latest)
            start = latest.AddDays(1);
        else if (_options.AsOfSampler is null)
            start = yesterday;
        else if (_options.BackfillFrom is { } from)
            start = from;
        else if (_options.EarliestDay is { } earliestDay)
        {
            if (await earliestDay(db, cancellationToken) is not { } earliest)
                return 0;
            start = earliest;
        }
        else
            start = yesterday;

        var floor = yesterday.AddDays(1 - _options.MaxBackfillDays);
        if (start < floor)
            start = floor;
        if (start > yesterday)
            return 0;

        var rows = new List<TSnapshot>();
        for (var day = start; day <= yesterday; day = day.AddDays(1))
        {
            var row = _options.AsOfSampler is { } asOf
                ? await asOf(db, day, cancellationToken)
                : await Live(db, cancellationToken);
            row.Date = day;
            rows.Add(row);
        }
        return await InsertAsync(db, rows, cancellationToken);
    }

    /// <summary>
    /// Reads the stored rows, oldest first, and adds a live row for today. It does not write. When nothing is
    /// stored and every count today is zero, the result is empty, so a new app shows no lone zero row.
    /// </summary>
    /// <param name="db">The context to read.</param>
    /// <param name="from">The first day to include. Optional.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The rows, with today last.</returns>
    public async Task<List<TSnapshot>> GetHistoryAsync(TContext db, DateOnly? from = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var query = db.Set<TSnapshot>().AsNoTracking();
        if (from is { } first)
            query = query.Where(s => s.Date >= first);
        var rows = await query.OrderBy(s => s.Date).ToListAsync(cancellationToken);

        var today = Today;
        if (from > today)
            return rows;
        var live = await Live(db, cancellationToken);
        live.Date = today;
        if (rows.Count == 0 && DailyStatColumns<TSnapshot>.IsZero(live))
            return rows;
        rows.Add(live);
        return rows;
    }

    private Task<TSnapshot> Live(TContext db, CancellationToken cancellationToken) =>
        (_options.LiveSampler ?? throw new InvalidOperationException("AddAppKitDailyStats needs a LiveSampler."))(db, cancellationToken);

    // Two writers can store the same day at once: the hosted service on two replicas, or an app that also
    // freezes days on a request. The unique index on Date stops the second one, which then skips that day.
    private static async Task<int> InsertAsync(TContext db, List<TSnapshot> rows, CancellationToken cancellationToken)
    {
        var set = db.Set<TSnapshot>();
        set.AddRange(rows);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return rows.Count;
        }
        catch (DbUpdateException)
        {
            Detach(db, rows);
        }

        var added = 0;
        foreach (var row in rows)
        {
            if (await ExistsAsync(db, row.Date, cancellationToken))
                continue;
            set.Add(row);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                added++;
            }
            catch (DbUpdateException)
            {
                Detach(db, [row]);
                if (!await ExistsAsync(db, row.Date, cancellationToken))
                    throw;
            }
        }
        return added;
    }

    private static Task<bool> ExistsAsync(TContext db, DateOnly date, CancellationToken cancellationToken) =>
        db.Set<TSnapshot>().AsNoTracking().AnyAsync(s => s.Date == date, cancellationToken);

    private static void Detach(TContext db, IEnumerable<TSnapshot> rows)
    {
        foreach (var row in rows)
            db.Entry(row).State = EntityState.Detached;
    }
}
