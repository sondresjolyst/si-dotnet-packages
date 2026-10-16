using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SjolystInnovation.AppKit.Vipps;

/// <summary>
/// Deletes processed webhook events older than <see cref="VippsWebhookOptions.Retention"/> when the app
/// starts and every six hours after that.
/// </summary>
internal sealed partial class VippsWebhookCleanupService<TContext>(
    IServiceScopeFactory scopes, IOptions<VippsWebhookOptions> options, TimeProvider time,
    ILogger<VippsWebhookCleanupService<TContext>> logger) : BackgroundService
    where TContext : DbContext, IVippsWebhookDbContext
{
    internal static readonly TimeSpan Interval = TimeSpan.FromHours(6);

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
            var cutoff = time.GetUtcNow().UtcDateTime - options.Value.Retention;
            var deleted = await db.ProcessedWebhookEvents.Where(e => e.ProcessedAt < cutoff).ExecuteDeleteAsync(cancellationToken);
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
        [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Count} processed Vipps webhook event(s) older than {Cutoff}.")]
        public static partial void Deleted(ILogger logger, int count, DateTime cutoff);

        [LoggerMessage(Level = LogLevel.Error, Message = "Deleting processed Vipps webhook events failed.")]
        public static partial void Failed(ILogger logger, Exception exception);
    }
}
