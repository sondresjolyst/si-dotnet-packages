using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SjolystInnovation.AppKit.Auth;

/// <summary>
/// Deletes refresh tokens past their expiry when the app starts and once a day after. A revoked
/// token is kept until it expires, because presenting it again is how a replay is caught.
/// </summary>
internal sealed partial class RefreshTokenCleanupService<TContext>(
    IServiceScopeFactory scopes, TimeProvider time, ILogger<RefreshTokenCleanupService<TContext>> logger) : BackgroundService
    where TContext : DbContext, IAuthDbContext
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
            var now = time.GetUtcNow().UtcDateTime;
            var deleted = await db.RefreshTokens.Where(t => t.Expires < now).ExecuteDeleteAsync(cancellationToken);
            if (deleted > 0)
                Log.Deleted(logger, deleted);
            return deleted;
        }
        // A failed run is logged and tried again on the next tick.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Failed(logger, ex);
            return 0;
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Count} expired refresh token(s).")]
        public static partial void Deleted(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Error, Message = "Deleting expired refresh tokens failed.")]
        public static partial void Failed(ILogger logger, Exception exception);
    }
}
