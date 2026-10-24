using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SjolystInnovation.AppKit.Consent;

/// <summary>
/// Deletes consent rows older than <see cref="AppKitConsentOptions.KeepFor"/> when the app starts and once
/// a day after that.
/// </summary>
internal sealed partial class ConsentRetentionService<TContext>(
    IServiceScopeFactory scopes, IOptions<AppKitConsentOptions> options, TimeProvider time,
    ILogger<ConsentRetentionService<TContext>> logger) : BackgroundService
    where TContext : DbContext
{
    internal static readonly TimeSpan Interval = TimeSpan.FromDays(1);

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
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TContext>();
            var cutoff = time.GetUtcNow().UtcDateTime - options.Value.KeepFor;
            var deleted = await db.Set<ConsentRecord>().Where(r => r.DecidedAt < cutoff).ExecuteDeleteAsync(cancellationToken);
            if (deleted > 0)
                Log.Deleted(logger, deleted, cutoff);
            return deleted;
        }
        // Log a failed run and try again on the next tick.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Failed(logger, ex);
            return 0;
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
