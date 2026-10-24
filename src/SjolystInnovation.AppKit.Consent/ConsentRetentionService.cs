using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SjolystInnovation.AppKit.Consent;

/// <summary>
/// Deletes consent rows older than <see cref="AppKitConsentOptions.KeepFor"/> when the app starts and once
/// a day after that. It deletes <see cref="BatchSize"/> rows per statement until none are left, so a large
/// backlog does not hold one long lock. A provider without bulk delete, such as the in-memory provider,
/// loads and removes each batch instead.
/// </summary>
internal sealed partial class ConsentRetentionService<TContext>(
    IServiceScopeFactory scopes, IOptions<AppKitConsentOptions> options, TimeProvider time,
    ILogger<ConsentRetentionService<TContext>> logger) : BackgroundService
    where TContext : DbContext
{
    internal static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    internal int BatchSize { get; init; } = 1000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            await CleanupAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task<int> CleanupAsync(CancellationToken cancellationToken)
    {
        var deleted = 0;
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TContext>();
            var cutoff = time.GetUtcNow().UtcDateTime - options.Value.KeepFor;
            var relational = db.Database.IsRelational();
            int batch;
            do
            {
                var old = db.Set<ConsentRecord>().Where(r => r.DecidedAt < cutoff).OrderBy(r => r.Id).Take(BatchSize);
                if (relational)
                {
                    batch = await old.ExecuteDeleteAsync(cancellationToken);
                }
                else
                {
                    var rows = await old.ToListAsync(cancellationToken);
                    db.Set<ConsentRecord>().RemoveRange(rows);
                    await db.SaveChangesAsync(cancellationToken);
                    db.ChangeTracker.Clear();
                    batch = rows.Count;
                }
                deleted += batch;
            }
            while (batch == BatchSize);
            if (deleted > 0)
                Log.Deleted(logger, deleted, cutoff);
            return deleted;
        }
        // Log a failed run and try again on the next tick.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Failed(logger, ex);
            return deleted;
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Count} consent record(s) older than {Cutoff}.")]
        public static partial void Deleted(ILogger logger, int count, DateTime cutoff);

        [LoggerMessage(Level = LogLevel.Error, Message = "Deleting old consent records failed.")]
        public static partial void Failed(ILogger logger, Exception exception);
    }
}
