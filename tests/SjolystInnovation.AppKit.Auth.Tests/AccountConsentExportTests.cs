using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace SjolystInnovation.AppKit.Auth.Tests;

public sealed class AccountConsentExportTests : IAsyncDisposable
{
    private sealed class QuietEmails : IRegistrationEmails<IdentityTestUser>
    {
        public Task SendVerificationAsync(IdentityTestUser user, string code, string? locale, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendAlreadyRegisteredAsync(IdentityTestUser user, string? locale, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendRemovalReminderAsync(IdentityTestUser user, string code, DateTime removalAt, string? locale, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private const string Key = "export-test-signing-key-0123456789abcdef";
    private static readonly DateTime Accepted = new(2026, 11, 1, 10, 30, 0, DateTimeKind.Utc);
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private IHost? _host;
    private HttpClient _client = default!;

    public AccountConsentExportTests() => _connection.Open();

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
        await _connection.DisposeAsync();
    }

    private async Task StartAsync(bool optIn = true)
    {
        _host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .UseEnvironment("Production")
                .ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Key"] = Key, ["Jwt:Issuer"] = "appkit" }))
                .ConfigureServices((context, s) =>
                {
                    s.AddDbContext<RegistrationTestDbContext>(o => o.UseSqlite(_connection));
                    s.AddAppKitIdentity<IdentityTestUser, RegistrationTestDbContext>();
                    s.AddAppKitAuth<RegistrationTestDbContext>(context.Configuration.GetSection("Jwt"));
                    s.AddAppKitJwtBearer<IdentityTestUser>(u => !u.IsDeleted);
                    s.AddAppKitAccounts<IdentityTestUser, RegistrationTestDbContext>();
                    s.AddAuthorization();
                    s.AddRouting();
                    if (optIn)
                    {
                        s.AddAppKitSelfRegistration<IdentityTestUser, RegistrationTestDbContext>(o =>
                        {
                            o.NewUser = r => new IdentityTestUser { Email = r.Email, UserName = r.UserName };
                            o.RunCleanupInBackground = false;
                        });
                        s.AddSingleton<IRegistrationEmails<IdentityTestUser>>(new QuietEmails());
                    }
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(e => e.MapAppKitAccount<IdentityTestUser>());
                }))
            .Build();
        using (var scope = _host.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<RegistrationTestDbContext>().Database.EnsureCreated();
        await _host.StartAsync();
        _client = _host.GetTestClient();
    }

    private async Task<string> SeedAsync(string id, params AccountConsent[] consents)
    {
        using var scope = _host!.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityTestUser>>();
        var user = new IdentityTestUser { Id = id, UserName = id, Email = $"{id}@example.invalid", EmailConfirmed = true };
        Assert.True((await users.CreateAsync(user, "Password1")).Succeeded);
        var db = scope.ServiceProvider.GetRequiredService<RegistrationTestDbContext>();
        foreach (var consent in consents)
        {
            consent.UserId = id;
            db.AccountConsents.Add(consent);
        }
        db.SaveChanges();
        return scope.ServiceProvider.GetRequiredService<IJwtTokenService>().Build(user, []);
    }

    private static AccountConsent Consent(string document, string version, string source, string? locale, string? ip) =>
        new() { Document = document, Version = version, Source = source, Locale = locale, Ip = ip, AcceptedAt = Accepted };

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _client.SendAsync(request);
    }

    [Fact]
    public async Task The_export_lists_the_consent_rows_without_the_ip()
    {
        await StartAsync();
        var token = await SeedAsync("u1", Consent("terms", "v1", "register", "nb", "203.0.113.0"), Consent("age", "16", "register", null, "203.0.113.0"));
        await SeedAsync("u2", Consent("terms", "v9", "register", "en", null));

        var response = await SendAsync(HttpMethod.Get, "/api/users/u1/export", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("203.0.113", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ip\"", text, StringComparison.OrdinalIgnoreCase);
        var consents = JsonElement.Parse(text).GetProperty("consents").EnumerateArray().ToList();
        Assert.Equal(["terms=v1", "age=16"], consents.Select(c => $"{c.GetProperty("document").GetString()}={c.GetProperty("version").GetString()}"));
        var terms = consents[0];
        Assert.Equal(["document", "version", "acceptedAt", "source", "locale"], terms.EnumerateObject().Select(p => p.Name));
        Assert.Equal("register", terms.GetProperty("source").GetString());
        Assert.Equal("nb", terms.GetProperty("locale").GetString());
        Assert.Equal(Accepted, terms.GetProperty("acceptedAt").GetDateTime().ToUniversalTime());
    }

    [Fact]
    public async Task An_app_that_did_not_opt_in_exports_no_consents()
    {
        await StartAsync(optIn: false);
        var token = await SeedAsync("u1");

        var text = await (await SendAsync(HttpMethod.Get, "/api/users/u1/export", token)).Content.ReadAsStringAsync();

        Assert.False(JsonElement.Parse(text).TryGetProperty("consents", out _));
    }

    [Fact]
    public async Task Deleting_the_account_clears_the_ip_and_locale_of_its_consent_rows_and_keeps_the_rows()
    {
        await StartAsync();
        var token = await SeedAsync("u1", Consent("terms", "v1", "register", "nb", "203.0.113.0"), Consent("age", "16", "register", "nb", "203.0.113.0"));
        await SeedAsync("u2", Consent("terms", "v1", "register", "en", "198.51.100.0"));

        var response = await SendAsync(HttpMethod.Delete, "/api/users/u1/account", token);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var scope = _host!.Services.CreateScope();
        var rows = scope.ServiceProvider.GetRequiredService<RegistrationTestDbContext>().AccountConsents.AsNoTracking().OrderBy(c => c.Id).ToList();
        Assert.Equal(3, rows.Count);
        Assert.All(rows.Where(r => r.UserId == "u1"), r =>
        {
            Assert.Null(r.Ip);
            Assert.Null(r.Locale);
            Assert.Equal(("register", Accepted), (r.Source, r.AcceptedAt));
        });
        Assert.Equal(["terms=v1", "age=16"], rows.Where(r => r.UserId == "u1").Select(r => $"{r.Document}={r.Version}"));
        var other = rows.Single(r => r.UserId == "u2");
        Assert.Equal(("en", "198.51.100.0"), (other.Locale, other.Ip));
    }
}
