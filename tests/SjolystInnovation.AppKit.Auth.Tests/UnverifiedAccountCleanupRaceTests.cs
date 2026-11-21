using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace SjolystInnovation.AppKit.Auth.Tests;

/// <summary>Runs the cleanup twice at the same moment on a SQLite file database, like two hosts on one database.</summary>
public sealed class UnverifiedAccountCleanupRaceTests : IAsyncDisposable
{
    private sealed class NoEmails : IRegistrationEmails<IdentityTestUser>
    {
        public Task SendVerificationAsync(IdentityTestUser user, string code, string? locale, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendAlreadyRegisteredAsync(IdentityTestUser user, string? locale, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendRemovalReminderAsync(IdentityTestUser user, string code, DateTime removalAt, string? locale, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    // Makes the save that deletes one user fail like a delete that another host did first, which affects no row.
    private sealed class OtherHostRemoves : SaveChangesInterceptor
    {
        public string? UserId { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            UserId is { } id && eventData.Context!.ChangeTracker.Entries<IdentityTestUser>().Any(e => e.State == EntityState.Deleted && e.Entity.Id == id)
                ? throw new DbUpdateConcurrencyException("The row was removed by another host.")
                : base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private const int Accounts = 40;
    private static readonly DateTimeOffset Start = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"appkit-auth-{Guid.NewGuid():N}.db");
    private readonly FakeTimeProvider _time = new(Start);
    private readonly OtherHostRemoves _otherHost = new();
    private IHost? _host;

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
        SqliteConnection.ClearAllPools();
        File.Delete(_file);
    }

    private async Task StartAsync()
    {
        _host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .UseEnvironment("Production")
                .ConfigureServices(s =>
                {
                    s.AddLogging(b => b.SetMinimumLevel(LogLevel.Debug).AddFakeLogging());
                    s.AddSingleton<TimeProvider>(_time);
                    s.AddDbContext<RegistrationTestDbContext>(o => o.UseSqlite($"Data Source={_file};Pooling=False").AddInterceptors(_otherHost));
                    s.AddAppKitIdentity<IdentityTestUser, RegistrationTestDbContext>();
                    s.Configure<AppKitAuthOptions>(o => o.Key = MailKeys.SigningKey);
                    s.AddAppKitSelfRegistration<IdentityTestUser, RegistrationTestDbContext>(o =>
                    {
                        o.NewUser = r => new IdentityTestUser { Email = r.Email, UserName = r.UserName };
                        o.RunCleanupInBackground = false;
                        o.RemindBefore = [];
                    });
                    s.AddSingleton<IRegistrationEmails<IdentityTestUser>>(new NoEmails());
                })
                .Configure(_ => { }))
            .Build();
        using (var scope = _host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RegistrationTestDbContext>();
            db.Database.EnsureCreated();
            var old = _time.GetUtcNow().UtcDateTime.AddDays(-60);
            for (var i = 0; i < Accounts; i++)
            {
                db.Users.Add(new IdentityTestUser { Id = $"u{i}", UserName = $"u{i}", NormalizedUserName = $"U{i}", Email = $"u{i}@example.invalid", NormalizedEmail = $"U{i}@EXAMPLE.INVALID", CreatedAt = old });
                db.AccountConsents.Add(new AccountConsent { UserId = $"u{i}", Document = "terms", Version = "v1", Source = "register", AcceptedAt = old });
            }
            db.SaveChanges();
        }
        await _host.StartAsync();
    }

    [Fact]
    public async Task Two_cleanups_at_the_same_moment_remove_each_account_once_and_neither_reports_a_failure()
    {
        await StartAsync();
        var cleanup = _host!.Services.GetRequiredService<IUnverifiedAccountCleanup>();
        var gate = new TaskCompletionSource();
        var runs = Enumerable.Range(0, 2).Select(async _ =>
        {
            await gate.Task;
            return await Task.Run(() => cleanup.RunOnceAsync());
        }).ToArray();
        gate.SetResult();

        var results = await Task.WhenAll(runs);

        Assert.Equal(Accounts, results.Sum(r => r.Removed));
        Assert.All(results, r => Assert.Equal(0, r.Failed));
        using var scope = _host.Services.CreateScope();
        Assert.Empty(scope.ServiceProvider.GetRequiredService<RegistrationTestDbContext>().Users.ToList());
        Assert.DoesNotContain(_host.Services.GetFakeLogCollector().GetSnapshot(), r => r.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task An_account_that_another_host_removed_at_the_same_moment_is_skipped_and_logged_at_debug()
    {
        await StartAsync();
        _otherHost.UserId = "u7";

        var result = await _host!.Services.GetRequiredService<IUnverifiedAccountCleanup>().RunOnceAsync();

        Assert.Equal(new UnverifiedCleanupResult(Accounts - 1, 0, 0, 1), result);
        var logs = _host.Services.GetFakeLogCollector().GetSnapshot();
        Assert.DoesNotContain(logs, r => r.Level >= LogLevel.Error);
        Assert.Contains(logs, r => r.Level == LogLevel.Debug && r.Message.Contains("u7", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_account_that_is_gone_when_it_is_loaded_again_is_skipped()
    {
        await StartAsync();
        var cleanup = (UnverifiedAccountCleanup<IdentityTestUser, RegistrationTestDbContext>)_host!.Services.GetRequiredService<IUnverifiedAccountCleanup>();

        Assert.Equal(CleanupOutcome.Lost, await cleanup.RemoveAsync("not-there", CancellationToken.None));
    }
}
