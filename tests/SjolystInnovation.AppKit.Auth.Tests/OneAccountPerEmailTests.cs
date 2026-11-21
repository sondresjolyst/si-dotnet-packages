using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace SjolystInnovation.AppKit.Auth.Tests;

/// <summary>A context that holds the consent sets but never maps them through the library, so it has no unique email index.</summary>
public class UnmappedConsentDbContext(DbContextOptions<UnmappedConsentDbContext> options) : DbContext(options), IAccountConsentDbContext
{
    public DbSet<TestUser> Users => Set<TestUser>();
    public DbSet<AccountConsent> AccountConsents => Set<AccountConsent>();
    public DbSet<ConsentRequirement> ConsentRequirements => Set<ConsentRequirement>();
    public DbSet<SignUpMailLog> SignUpMailLogs => Set<SignUpMailLog>();
}

public sealed class OneAccountPerEmailTests : IAsyncDisposable
{
    private sealed class QuietEmails : IRegistrationEmails<IdentityTestUser>
    {
        public Task SendVerificationAsync(IdentityTestUser user, string code, string? locale, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendAlreadyRegisteredAsync(IdentityTestUser user, string? locale, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendRemovalReminderAsync(IdentityTestUser user, string code, DateTime removalAt, string? locale, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private readonly string _file = Path.Combine(Path.GetTempPath(), $"appkit-auth-{Guid.NewGuid():N}.db");
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

    private async Task<HttpClient> StartAsync()
    {
        _host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(s =>
                {
                    s.AddDbContext<RegistrationTestDbContext>(o => o.UseSqlite($"Data Source={_file};Pooling=False"));
                    s.AddAppKitIdentity<IdentityTestUser, RegistrationTestDbContext>();
                    s.AddRouting();
                    s.AddAppKitSelfRegistration<IdentityTestUser, RegistrationTestDbContext>(o =>
                    {
                        o.NewUser = r => new IdentityTestUser { Email = r.Email, UserName = r.UserName };
                        o.AllowUserName = true;
                    });
                    s.AddSingleton<IRegistrationEmails<IdentityTestUser>>(new QuietEmails());
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapAppKitRegistration<IdentityTestUser, RegistrationTestDbContext>());
                }))
            .Build();
        using (var scope = _host.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<RegistrationTestDbContext>().Database.EnsureCreated();
        await _host.StartAsync();
        return _host.GetTestClient();
    }

    private static object Body(string email, string userName) =>
        new { email, password = "Password1", userName, ageConfirmed = true, termsAccepted = true, termsVersion = "v1" };

    [Fact]
    public async Task Parallel_sign_ups_for_one_email_with_different_user_names_leave_one_account()
    {
        var client = await StartAsync();

        for (var round = 0; round < 5; round++)
        {
            var email = $"race{round}@example.invalid";
            var responses = await Task.WhenAll(
                client.PostAsJsonAsync("/api/auth/register", Body(email, $"first{round}")),
                client.PostAsJsonAsync("/api/auth/register", Body(email, $"second{round}")));

            Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
            using var scope = _host!.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RegistrationTestDbContext>();
            Assert.Equal(1, await db.Users.CountAsync(u => u.Email == email));
        }
    }

    [Fact]
    public void The_model_builder_extension_adds_a_unique_index_on_the_normalised_email()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        using var db = new ConsentTestDbContext(new DbContextOptionsBuilder<ConsentTestDbContext>().UseSqlite(connection).Options);

        var indexes = db.Model.FindEntityType(typeof(TestUser))!.GetIndexes();

        Assert.Contains(indexes, i => i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual(["NormalizedEmail"]));
    }

    [Fact]
    public async Task The_database_refuses_two_users_with_one_normalised_email()
    {
        await StartAsync();
        using var scope = _host!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RegistrationTestDbContext>();
        db.Users.Add(new IdentityTestUser { Id = "a", UserName = "a", NormalizedUserName = "A", NormalizedEmail = "SAME@EXAMPLE.INVALID" });
        db.Users.Add(new IdentityTestUser { Id = "b", UserName = "b", NormalizedUserName = "B", NormalizedEmail = "SAME@EXAMPLE.INVALID" });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Starting_without_a_unique_email_index_fails_and_names_the_model_builder_call()
    {
        using var host = new HostBuilder()
            .ConfigureServices(s =>
            {
                s.AddDbContext<UnmappedConsentDbContext>(o => o.UseSqlite("DataSource=:memory:"));
                s.AddAppKitSelfRegistration<TestUser, UnmappedConsentDbContext>(o => o.NewUser = r => new TestUser { Email = r.Email });
            })
            .Build();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.Contains("AddAppKitAccountConsents", failure.Message, StringComparison.Ordinal);
        Assert.Contains("NormalizedEmail", failure.Message, StringComparison.Ordinal);
    }
}
