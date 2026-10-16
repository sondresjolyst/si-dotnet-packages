using System.Data.Common;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SjolystInnovation.AppKit.Hosting;

/// <summary>The options for <see cref="DatabaseMigrationExtensions.MigrateAppKitDatabaseAsync{TContext}"/>.</summary>
public sealed class AppKitMigrationOptions
{
    /// <summary>
    /// The command timeout while migrations run, for example to let an index build on a large table
    /// finish. The previous timeout comes back afterward. Defaults to <c>null</c>, which keeps the
    /// provider's timeout.
    /// </summary>
    public TimeSpan? CommandTimeout { get; set; }

    /// <summary>
    /// The number of retries when the database cannot be reached, for example while it starts next to
    /// the app. A failing migration is never retried. Defaults to 5.
    /// </summary>
    public int Retries { get; set; } = 5;

    /// <summary>The wait before the first retry. Each later wait is twice as long. Defaults to 2 seconds.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);
}

/// <summary>Applies pending Entity Framework Core migrations when the app starts.</summary>
public static partial class DatabaseMigrationExtensions
{
    internal const string AutoMigrateKey = "Database:AutoMigrate";
    internal const string LogCategory = "SjolystInnovation.AppKit.Hosting.Migrations";

    /// <summary>
    /// Applies the pending migrations of <typeparamref name="TContext"/> in a new scope, unless
    /// <c>Database:AutoMigrate</c> is <c>false</c>. If the database cannot be reached, the check for
    /// pending migrations is retried with a growing wait. A failing migration is not retried.
    /// </summary>
    /// <param name="services">The app's root service provider, such as <c>app.Services</c>.</param>
    /// <param name="configuration">The configuration that holds <c>Database:AutoMigrate</c>. It defaults to <c>true</c>.</param>
    /// <param name="configure">A callback that sets the command timeout and the retries.</param>
    /// <param name="cancellationToken">The token that cancels the migration and the waits between retries.</param>
    public static async Task MigrateAppKitDatabaseAsync<TContext>(
        this IServiceProvider services,
        IConfiguration configuration,
        Action<AppKitMigrationOptions>? configure = null,
        CancellationToken cancellationToken = default)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new AppKitMigrationOptions();
        configure?.Invoke(options);
        ArgumentOutOfRangeException.ThrowIfNegative(options.Retries);

        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategory);
        if (!configuration.GetValue(AutoMigrateKey, true))
        {
            Log.Skipped(logger);
            return;
        }

        await using var scope = services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<TContext>().Database;

        var pending = (await GetPendingWithRetriesAsync(database, options, logger, cancellationToken)).ToList();
        if (pending.Count == 0)
        {
            Log.NonePending(logger);
            return;
        }

        var names = string.Join(", ", pending);
        Log.Applying(logger, pending.Count, names);
        var previousTimeout = database.GetCommandTimeout();
        try
        {
            if (options.CommandTimeout is { } timeout)
                database.SetCommandTimeout(timeout);
            await database.MigrateAsync(cancellationToken);
        }
        finally
        {
            database.SetCommandTimeout(previousTimeout);
        }
    }

    private static async Task<IEnumerable<string>> GetPendingWithRetriesAsync(
        DatabaseFacade database,
        AppKitMigrationOptions options,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await database.GetPendingMigrationsAsync(cancellationToken);
            }
            catch (Exception ex) when (attempt < options.Retries && IsTransient(ex))
            {
                var delay = options.RetryDelay * Math.Pow(2, attempt);
                Log.Retrying(logger, ex, attempt + 1, options.Retries, delay);
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Checks whether an exception or one of its inner exceptions is a connection failure that can pass,
    /// such as a provider error marked transient, a socket error, or a timeout.
    /// </summary>
    internal static bool IsTransient(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is DbException { IsTransient: true } or SocketException or TimeoutException)
                return true;
        }
        return false;
    }

    internal static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Skipping migrations because Database:AutoMigrate is false.")]
        public static partial void Skipped(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "No pending migrations.")]
        public static partial void NonePending(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Applying {Count} pending migration(s): {Migrations}")]
        public static partial void Applying(ILogger logger, int count, string migrations);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Database unreachable. Retry {Attempt} of {Retries} in {Delay}.")]
        public static partial void Retrying(ILogger logger, Exception exception, int attempt, int retries, TimeSpan delay);
    }
}
