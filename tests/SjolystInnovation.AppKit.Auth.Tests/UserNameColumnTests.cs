using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace SjolystInnovation.AppKit.Auth.Tests;

/// <summary>An Identity context whose user name columns are narrower than the Identity default of 256.</summary>
public class NarrowUserNameDbContext(DbContextOptions<NarrowUserNameDbContext> options, int userName, int normalizedUserName)
    : IdentityDbContext<IdentityTestUser>(options), IAccountConsentDbContext
{
    public DbSet<AccountConsent> AccountConsents => Set<AccountConsent>();
    public DbSet<ConsentRequirement> ConsentRequirements => Set<ConsentRequirement>();
    public DbSet<SignUpMailLog> SignUpMailLogs => Set<SignUpMailLog>();

    public (int UserName, int NormalizedUserName) Widths => (userName, normalizedUserName);

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.AddAppKitAccountConsents<IdentityTestUser>();
        builder.Entity<IdentityTestUser>().Property(u => u.UserName).HasMaxLength(userName);
        builder.Entity<IdentityTestUser>().Property(u => u.NormalizedUserName).HasMaxLength(normalizedUserName);
    }
}

public sealed class UserNameColumnTests : IAsyncDisposable
{
    private sealed class QuietEmails : IRegistrationEmails<IdentityTestUser>
    {
        public Task SendVerificationAsync(IdentityTestUser user, string code, string? locale, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendAlreadyRegisteredAsync(IdentityTestUser user, string? locale, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendRemovalReminderAsync(IdentityTestUser user, string code, DateTime removalAt, string? locale, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private IHost? _host;

    public UserNameColumnTests() => _connection.Open();

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            if (_started)
                await _host.StopAsync();
            _host.Dispose();
        }
        await _connection.DisposeAsync();
    }

    private bool _started;

    private IHost Build(int userName, int normalizedUserName, Action<AppKitRegistrationOptions<IdentityTestUser>> configure)
    {
        _host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(s =>
                {
                    s.AddScoped(sp => new NarrowUserNameDbContext(
                        new DbContextOptionsBuilder<NarrowUserNameDbContext>().UseSqlite(_connection)
                            .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, WidthKey>().Options,
                        userName, normalizedUserName));
                    s.AddAppKitIdentity<IdentityTestUser, NarrowUserNameDbContext>();
                    s.AddRouting();
                    s.Configure<AppKitAuthOptions>(o => o.Key = MailKeys.SigningKey);
                    s.AddAppKitSelfRegistration<IdentityTestUser, NarrowUserNameDbContext>(o =>
                    {
                        o.NewUser = r => new IdentityTestUser { Email = r.Email };
                        o.RunCleanupInBackground = false;
                        configure(o);
                    });
                    s.AddSingleton<IRegistrationEmails<IdentityTestUser>>(new QuietEmails());
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapAppKitRegistration<IdentityTestUser, NarrowUserNameDbContext>());
                }))
            .Build();
        return _host;
    }

    private async Task<HttpClient> StartAsync(int userName, int normalizedUserName, Action<AppKitRegistrationOptions<IdentityTestUser>> configure)
    {
        var host = Build(userName, normalizedUserName, configure);
        using (var scope = host.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<NarrowUserNameDbContext>().Database.EnsureCreated();
        await host.StartAsync();
        _started = true;
        return host.GetTestClient();
    }

    private async Task<string> StartFailureAsync(int userName, int normalizedUserName, Action<AppKitRegistrationOptions<IdentityTestUser>> configure)
    {
        var host = Build(userName, normalizedUserName, configure);
        return (await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync())).Message;
    }

    private static object Body(string userName) =>
        new { email = "new@example.invalid", password = "Password1", userName, ageConfirmed = true, termsAccepted = true, termsVersion = "v1" };

    [Theory]
    [InlineData(32, 256, 32)]
    [InlineData(256, 40, 40)]
    [InlineData(32, 20, 20)]
    public async Task Without_an_explicit_maximum_a_column_under_50_becomes_the_maximum(int userName, int normalizedUserName, int expected)
    {
        var client = await StartAsync(userName, normalizedUserName, o => o.AllowUserName = true);

        var tooLong = await client.PostAsJsonAsync("/api/auth/register", Body(new string('x', expected + 1)));
        var fits = await client.PostAsJsonAsync("/api/auth/register", Body(new string('x', expected)));

        Assert.Equal(["UserName=tooLong"], await ErrorJson.FieldCodesAsync(tooLong));
        Assert.Equal([$"UserName={expected}"], await ErrorJson.LimitsAsync(tooLong));
        Assert.Equal(HttpStatusCode.OK, fits.StatusCode);
    }

    [Fact]
    public async Task Without_an_explicit_maximum_a_wide_column_keeps_the_maximum_at_50()
    {
        var client = await StartAsync(256, 256, o => o.RequireUserName = true);

        var response = await client.PostAsJsonAsync("/api/auth/register", Body(new string('x', 51)));

        Assert.Equal(["UserName=50"], await ErrorJson.LimitsAsync(response));
    }

    [Theory]
    [InlineData(32, 256, 50, "UserName", "32")]
    [InlineData(256, 40, 64, "NormalizedUserName", "40")]
    public async Task An_explicit_maximum_over_either_user_name_column_fails_at_start_and_names_the_column(
        int userName, int normalizedUserName, int max, string column, string width)
    {
        var message = await StartFailureAsync(userName, normalizedUserName, o => { o.AllowUserName = true; o.MaxUserNameLength = max; });

        Assert.Contains("MaxUserNameLength", message, StringComparison.Ordinal);
        Assert.Contains($"{column} column", message, StringComparison.Ordinal);
        Assert.Contains(width, message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_explicit_maximum_that_fits_the_narrow_column_starts()
    {
        var client = await StartAsync(32, 32, o => { o.RequireUserName = true; o.MaxUserNameLength = 32; });

        var response = await client.PostAsJsonAsync("/api/auth/register", Body(new string('x', 32)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_minimum_over_a_narrow_column_fails_at_start_when_the_maximum_is_not_set()
    {
        var message = await StartFailureAsync(32, 32, o => { o.RequireUserName = true; o.MinUserNameLength = 40; });

        Assert.Contains("MinUserNameLength", message, StringComparison.Ordinal);
        Assert.Contains("32", message, StringComparison.Ordinal);
    }
}

// The model of NarrowUserNameDbContext depends on the widths, so they are part of its cache key.
internal sealed class WidthKey : Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) =>
        context is NarrowUserNameDbContext narrow ? (context.GetType(), narrow.Widths, designTime) : (context.GetType(), designTime);
}
