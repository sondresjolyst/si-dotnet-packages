using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SjolystInnovation.AppKit.SiteSettings.Tests;

/// <summary>An app's settings row with company details, branding and a setting of its own.</summary>
public class TestSettings : ICompanySettings, IBrandingSettings
{
    public int Id { get; set; } = 1;
    public string ContactRecipientEmail { get; set; } = "";
    public string CompanyName { get; set; } = "Pyttogpanne";
    public string CompanyLegalName { get; set; } = "";
    public string OrgNumber { get; set; } = "";
    public bool VatRegistered { get; set; }
    public string StreetAddress { get; set; } = "";
    public string PostalCode { get; set; } = "";
    public string AddressLocality { get; set; } = "";
    public string AddressRegion { get; set; } = "";
    public string PublicEmail { get; set; } = "";
    public string PublicPhone { get; set; } = "";
    public string? LogoData { get; set; }
    public string? LogoContentType { get; set; }
    public string? IconData { get; set; }
    public string? IconContentType { get; set; }
    public bool CookieBannerEnabled { get; set; } = true;
}

public class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
{
    public DbSet<TestSettings> AppSettings => Set<TestSettings>();
}

/// <summary>Signs a request in with the roles in <c>X-Test-Roles</c>.</summary>
internal sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-Roles", out var roles))
            return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "u1") };
        claims.AddRange(roles.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(r => new Claim(ClaimTypes.Role, r)));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")), "Test")));
    }
}

/// <summary>A test server with the site settings endpoints on a SQLite database in memory.</summary>
public sealed class TestApp : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private IHost _host = default!;
    public HttpClient Client { get; private set; } = default!;

    public static async Task<TestApp> StartAsync(Action<AppKitSiteSettingsOptions>? configure = null)
    {
        var app = new TestApp();
        await app._connection.OpenAsync();
        app._host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(s =>
                {
                    s.AddDbContext<TestDbContext>(o => o.UseSqlite(app._connection));
                    s.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                    s.AddAuthorization(o =>
                    {
                        o.AddPolicy("Admin", p => p.RequireRole("Admin"));
                        o.AddPolicy("Owner", p => p.RequireRole("Owner"));
                    });
                    s.AddRouting();
                })
                .Configure(web =>
                {
                    web.UseRouting();
                    web.UseAuthentication();
                    web.UseAuthorization();
                    web.UseEndpoints(e => e.MapAppKitSiteSettings<TestDbContext, TestSettings>(configure: configure));
                }))
            .Build();
        using (var scope = app._host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<TestDbContext>().Database.EnsureCreatedAsync();
        await app._host.StartAsync();
        app.Client = app._host.GetTestClient();
        return app;
    }

    public async Task<TestSettings?> RowAsync()
    {
        using var scope = _host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TestDbContext>().AppSettings.AsNoTracking().SingleOrDefaultAsync();
    }

    public async Task SeedAsync(TestSettings settings)
    {
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
        db.AppSettings.Add(settings);
        await db.SaveChangesAsync();
    }

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content = null, string? roles = "Admin")
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        if (roles is not null)
            request.Headers.Add("X-Test-Roles", roles);
        return Client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        await _connection.DisposeAsync();
    }
}
