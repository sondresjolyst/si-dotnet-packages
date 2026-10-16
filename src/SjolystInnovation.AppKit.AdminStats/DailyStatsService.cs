using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SjolystInnovation.AppKit.AdminStats;

/// <summary>
/// Stores the missed completed days when the app starts, just after each UTC midnight, and at least every
/// <see cref="AppKitDailyStatsOptions{TContext, TSnapshot}.Interval"/>. The midnight run keeps yesterday's row
/// from going missing in the history for hours. After a failed run it tries again after one minute, then
/// after twice as long each time, up to the interval.
/// </summary>
internal sealed partial class DailyStatsService<TContext, TSnapshot>(
    IServiceScopeFactory scopes,
    AppKitDailyStats<TContext, TSnapshot> stats,
    IOptions<AppKitDailyStatsOptions<TContext, TSnapshot>> options,
    TimeProvider time,
    ILogger<DailyStatsService<TContext, TSnapshot>> logger) : BackgroundService
    where TContext : DbContext
    where TSnapshot : class, IAppKitDailyStat
{
    // Leaves room for a clock that runs a little behind, so the run after midnight sees the new day.
    internal static readonly TimeSpan AfterMidnight = TimeSpan.FromMinutes(1);

    internal static readonly TimeSpan FirstRetry = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var interval = options.Value.Interval;
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            // The day the run starts on. A run that ends after midnight has not stored the day that just closed.
            var runDay = stats.Today;
            var succeeded = await RunOnceAsync(stoppingToken);
            if (stoppingToken.IsCancellationRequested)
                break;
            failures = succeeded ? 0 : failures + 1;
            var delay = succeeded ? NextDelay(time.GetUtcNow(), interval, runDay) : RetryDelay(failures, interval);
            try
            {
                await Task.Delay(delay, time, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>Runs once and reports whether the run succeeded. A failure is logged, not thrown.</summary>
    internal async Task<bool> RunOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var added = await stats.FreezeMissingDaysAsync(scope.ServiceProvider.GetRequiredService<TContext>(), stoppingToken);
            if (added > 0)
                Log.Stored(logger, added, typeof(TSnapshot).Name);
            return true;
        }
#pragma warning disable CA1031 // A failed run must not stop the service. The next run tries again.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (!stoppingToken.IsCancellationRequested)
                Log.Failed(logger, ex, typeof(TSnapshot).Name);
            return false;
        }
    }

    /// <summary>
    /// Computes the wait after a successful run: the interval, or the time until one minute after UTC midnight
    /// when that is sooner. If the UTC day has moved on since <paramref name="runDay"/>, the next run is due at
    /// one minute past this midnight, which may be now.
    /// </summary>
    internal static TimeSpan NextDelay(DateTimeOffset now, TimeSpan interval, DateOnly runDay)
    {
        var utc = now.UtcDateTime;
        var midnight = DateOnly.FromDateTime(utc) > runDay ? utc.Date : utc.Date.AddDays(1);
        var untilRun = midnight + AfterMidnight - utc;
        if (untilRun < TimeSpan.Zero)
            untilRun = TimeSpan.Zero;
        return untilRun < interval ? untilRun : interval;
    }

    /// <summary>Computes the wait after a run of failures: one minute, doubling with each failure, up to the interval.</summary>
    internal static TimeSpan RetryDelay(int failures, TimeSpan interval)
    {
        var delay = FirstRetry;
        for (var i = 1; i < failures && delay < interval; i++)
            delay += delay;
        return delay < interval ? delay : interval;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Stored {Count} completed day(s) of {Snapshot}.")]
        public static partial void Stored(ILogger logger, int count, string snapshot);

        [LoggerMessage(Level = LogLevel.Error, Message = "Storing the completed days of {Snapshot} failed.")]
        public static partial void Failed(ILogger logger, Exception exception, string snapshot);
    }
}
