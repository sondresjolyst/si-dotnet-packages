using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace SjolystInnovation.AppKit.Auth.Tests;

public sealed class RefreshTokenCleanupTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly FakeLogger<RefreshTokenCleanupService<TestDbContext>> _logger = new();

    public void Dispose() => _db.Dispose();

    private RefreshTokenCleanupService<TestDbContext> Service(Func<TestDbContext>? context = null)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => (context ?? (() => _db.NewContext()))());
        return new RefreshTokenCleanupService<TestDbContext>(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), _time, _logger);
    }

    private void Seed(params (string Token, TimeSpan ExpiresIn, bool Revoked)[] tokens)
    {
        using var db = _db.NewContext();
        db.Users.Add(new TestUser { Id = "u1", UserName = "u1" });
        foreach (var (token, expiresIn, revoked) in tokens)
        {
            db.RefreshTokens.Add(new RefreshToken
            {
                Token = token, UserId = "u1", Created = Now.UtcDateTime.AddMonths(-1),
                Expires = Now.UtcDateTime.Add(expiresIn), Revoked = revoked ? Now.UtcDateTime.AddDays(-1) : null,
            });
        }
        db.SaveChanges();
    }

    private string[] Remaining()
    {
        using var db = _db.NewContext();
        return db.RefreshTokens.OrderBy(t => t.Token).Select(t => t.Token).ToArray();
    }

    [Fact]
    public async Task Deletes_expired_tokens_and_keeps_revoked_ones_until_they_expire()
    {
        Seed(("expired-live", TimeSpan.FromSeconds(-1), false),
            ("expired-revoked", TimeSpan.FromDays(-30), true),
            ("live", TimeSpan.FromDays(90), false),
            ("revoked", TimeSpan.FromDays(90), true),
            ("expires-now", TimeSpan.Zero, false));

        var deleted = await Service().CleanupAsync(CancellationToken.None);

        Assert.Equal(2, deleted);
        Assert.Equal(["expires-now", "live", "revoked"], Remaining());
        Assert.Contains(_logger.Collector.GetSnapshot(), r => r.Message == "Deleted 2 expired refresh token(s).");
    }

    [Fact]
    public async Task Runs_at_start_and_again_each_day()
    {
        Seed(("expired", TimeSpan.FromSeconds(-1), false), ("tomorrow", TimeSpan.FromHours(12), false));
        var service = Service();

        await service.StartAsync(CancellationToken.None);
        await WaitUntil(() => Remaining().Length == 1);
        Assert.Equal(["tomorrow"], Remaining());

        _time.Advance(TimeSpan.FromHours(23));
        await Task.Delay(100);
        Assert.Equal(["tomorrow"], Remaining());

        _time.Advance(TimeSpan.FromHours(1));
        await WaitUntil(() => Remaining().Length == 0);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_failed_run_is_logged_and_does_not_stop_the_service()
    {
        var broken = new TestDb();
        broken.Dispose();

        var deleted = await Service(() => broken.NewContext()).CleanupAsync(CancellationToken.None);

        Assert.Equal(0, deleted);
        Assert.Contains(_logger.Collector.GetSnapshot(), r => r.Level == LogLevel.Error);
    }

    [Fact]
    public void AddAppKitAuth_registers_the_cleanup()
    {
        var services = new ServiceCollection();
        services.AddAppKitAuth<TestDbContext>(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build().GetSection("Jwt"));

        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService)
            && d.ImplementationType == typeof(RefreshTokenCleanupService<TestDbContext>));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(20);
        Assert.True(condition());
    }
}
