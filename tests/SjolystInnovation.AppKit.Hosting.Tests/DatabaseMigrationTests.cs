using System.Data.Common;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Xunit;

namespace SjolystInnovation.AppKit.Hosting.Tests;

public sealed class MigrateDbContext(DbContextOptions<MigrateDbContext> options) : DbContext(options);

public sealed class BrokenDbContext(DbContextOptions<BrokenDbContext> options) : DbContext(options);

[DbContext(typeof(MigrateDbContext))]
[Migration("20260101000000_CreateNotes")]
public sealed class CreateNotes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("CREATE TABLE Notes (Id INTEGER PRIMARY KEY)");
}

[DbContext(typeof(BrokenDbContext))]
[Migration("20260101000000_Broken")]
public sealed class Broken : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("INSERT INTO NoSuchTable VALUES (1)");
}

public sealed class DatabaseMigrationTests : IDisposable
{
    private const int ProviderTimeoutSeconds = 30;

    // A shared in-memory database, kept alive by this connection, so EF opens connections of its own.
    private readonly string _connectionString = $"Data Source=migrate-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private readonly SqliteConnection _keepAlive;
    private readonly FakeLogCollector _logs = new();
    private readonly CommandRecorder _commands = new();
    private readonly FlakyOpener _opener = new();

    public DatabaseMigrationTests()
    {
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();
    }

    public void Dispose() => _keepAlive.Dispose();

    private ServiceProvider Services<TContext>() where TContext : DbContext =>
        new ServiceCollection()
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(new FakeLoggerProvider(_logs)))
            // A singleton context outlives the helper's scope, so the test can read its command timeout.
            .AddDbContext<TContext>(o => o
                .UseSqlite(_connectionString, s => s.CommandTimeout(ProviderTimeoutSeconds))
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                .AddInterceptors(_commands, _opener),
                ServiceLifetime.Singleton, ServiceLifetime.Singleton)
            .BuildServiceProvider();

    private static IConfiguration Config(bool? autoMigrate = null) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(autoMigrate is { } value
                ? new Dictionary<string, string?> { ["Database:AutoMigrate"] = value.ToString() }
                : [])
            .Build();

    private bool TableExists(string name)
    {
        using var command = _keepAlive.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", name);
        return (long)command.ExecuteScalar()! > 0;
    }

    // Only the helper's own records. EF logs its commands too.
    private List<FakeLogRecord> Logs(LogLevel level) => _logs.GetSnapshot()
        .Where(r => r.Category == DatabaseMigrationExtensions.LogCategory && r.Level == level)
        .ToList();

    [Fact]
    public async Task Skips_when_AutoMigrate_is_false()
    {
        await using var services = Services<MigrateDbContext>();

        await services.MigrateAppKitDatabaseAsync<MigrateDbContext>(Config(autoMigrate: false));

        Assert.False(TableExists("Notes"));
        Assert.False(TableExists("__EFMigrationsHistory"));
        Assert.Contains(Logs(LogLevel.Information), r => r.Message.Contains("Database:AutoMigrate is false", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Applies_pending_migrations_when_AutoMigrate_is_unset()
    {
        await using var services = Services<MigrateDbContext>();

        await services.MigrateAppKitDatabaseAsync<MigrateDbContext>(Config());

        Assert.True(TableExists("Notes"));
        var applying = Assert.Single(Logs(LogLevel.Information), r => r.Message.StartsWith("Applying", StringComparison.Ordinal));
        Assert.Equal("Applying 1 pending migration(s): 20260101000000_CreateNotes", applying.Message);
    }

    [Fact]
    public async Task Only_logs_at_debug_when_nothing_is_pending()
    {
        await using var services = Services<MigrateDbContext>();
        await services.MigrateAppKitDatabaseAsync<MigrateDbContext>(Config(autoMigrate: true));
        _logs.Clear();
        _commands.Timeouts.Clear();

        await services.MigrateAppKitDatabaseAsync<MigrateDbContext>(Config(autoMigrate: true));

        Assert.Empty(Logs(LogLevel.Information));
        Assert.Contains(Logs(LogLevel.Debug), r => r.Message == "No pending migrations.");
        Assert.Empty(_commands.Timeouts);
    }

    [Fact]
    public async Task Raises_the_command_timeout_while_migrating_and_restores_it_after_success()
    {
        await using var services = Services<MigrateDbContext>();

        await services.MigrateAppKitDatabaseAsync<MigrateDbContext>(Config(), o => o.CommandTimeout = TimeSpan.FromMinutes(15));

        Assert.Equal(15 * 60, Assert.Single(_commands.Timeouts));
        Assert.Equal(ProviderTimeoutSeconds, services.GetRequiredService<MigrateDbContext>().Database.GetCommandTimeout());
    }

    [Fact]
    public async Task Keeps_the_provider_timeout_when_no_command_timeout_is_set()
    {
        await using var services = Services<MigrateDbContext>();

        await services.MigrateAppKitDatabaseAsync<MigrateDbContext>(Config());

        Assert.Equal(ProviderTimeoutSeconds, Assert.Single(_commands.Timeouts));
    }

    [Fact]
    public async Task Restores_the_command_timeout_and_does_not_retry_when_a_migration_fails()
    {
        await using var services = Services<BrokenDbContext>();

        await Assert.ThrowsAsync<SqliteException>(() =>
            services.MigrateAppKitDatabaseAsync<BrokenDbContext>(Config(), o =>
            {
                o.CommandTimeout = TimeSpan.FromMinutes(15);
                o.RetryDelay = TimeSpan.Zero;
            }));

        Assert.Equal(ProviderTimeoutSeconds, services.GetRequiredService<BrokenDbContext>().Database.GetCommandTimeout());
        Assert.Empty(Logs(LogLevel.Warning));
    }

    [Fact]
    public async Task Retries_a_transient_connection_failure_then_applies_the_migrations()
    {
        await using var services = Services<MigrateDbContext>();
        _opener.FailuresLeft = 2;

        await services.MigrateAppKitDatabaseAsync<MigrateDbContext>(Config(), o => o.RetryDelay = TimeSpan.FromMilliseconds(1));

        Assert.True(TableExists("Notes"));
        Assert.Equal(2, _opener.Thrown);
        Assert.Equal(
            ["Database unreachable. Retry 1 of 5 in 00:00:00.0010000.", "Database unreachable. Retry 2 of 5 in 00:00:00.0020000."],
            Logs(LogLevel.Warning).Select(r => r.Message));
    }

    [Fact]
    public async Task Gives_up_after_the_configured_retries()
    {
        await using var services = Services<MigrateDbContext>();
        _opener.FailuresLeft = int.MaxValue;

        await Assert.ThrowsAnyAsync<DbException>(() =>
            services.MigrateAppKitDatabaseAsync<MigrateDbContext>(Config(), o =>
            {
                o.Retries = 2;
                o.RetryDelay = TimeSpan.Zero;
            }));

        Assert.Equal(3, _opener.Thrown);
        Assert.False(TableExists("Notes"));
    }

    [Fact]
    public async Task Does_not_retry_a_non_transient_connection_failure()
    {
        await using var services = Services<MigrateDbContext>();
        _opener.FailuresLeft = int.MaxValue;
        _opener.Transient = false;

        await Assert.ThrowsAnyAsync<DbException>(() =>
            services.MigrateAppKitDatabaseAsync<MigrateDbContext>(Config(), o => o.RetryDelay = TimeSpan.Zero));

        Assert.Equal(1, _opener.Thrown);
        Assert.Empty(Logs(LogLevel.Warning));
    }

    [Fact]
    public void Treats_socket_errors_timeouts_and_transient_provider_errors_as_transient()
    {
        Assert.True(DatabaseMigrationExtensions.IsTransient(new FakeDbException(transient: true)));
        Assert.True(DatabaseMigrationExtensions.IsTransient(new InvalidOperationException("Wrapped.", new SocketException())));
        Assert.True(DatabaseMigrationExtensions.IsTransient(new TimeoutException()));
        Assert.False(DatabaseMigrationExtensions.IsTransient(new FakeDbException(transient: false)));
        Assert.False(DatabaseMigrationExtensions.IsTransient(new InvalidOperationException()));
    }

    private sealed class FakeDbException(bool transient) : DbException("Simulated connection failure.")
    {
        public override bool IsTransient => transient;
    }

    private sealed class FlakyOpener : DbConnectionInterceptor
    {
        public int FailuresLeft { get; set; }
        public bool Transient { get; set; } = true;
        public int Thrown { get; private set; }

        public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
        {
            Fail();
            return result;
        }

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Fail();
            return ValueTask.FromResult(result);
        }

        private void Fail()
        {
            if (FailuresLeft <= 0)
                return;
            FailuresLeft--;
            Thrown++;
            throw new FakeDbException(Transient);
        }
    }

    private sealed class CommandRecorder : DbCommandInterceptor
    {
        public List<int> Timeouts { get; } = [];

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Record(command);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        // Only the migration's own statement, not the history table bookkeeping.
        private void Record(DbCommand command)
        {
            if (command.CommandText.Contains("CREATE TABLE Notes", StringComparison.Ordinal))
                Timeouts.Add(command.CommandTimeout);
        }
    }
}
