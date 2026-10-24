using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace SjolystInnovation.AppKit.Consent.Tests;

public class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
{
    public DbSet<ConsentRecord> ConsentRecords => Set<ConsentRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddAppKitConsent();
}

/// <summary>
/// Signs a request in with the roles in <c>X-Test-Roles</c>. <c>X-Test-Anonymous</c> gives a user with a
/// name identifier claim whose identity is not authenticated.
/// </summary>
internal sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers.ContainsKey("X-Test-Anonymous"))
        {
            var anonymous = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "ghost")]));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(anonymous, "Test")));
        }
        if (!Request.Headers.TryGetValue("X-Test-Roles", out var roles))
            return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new List<Claim>();
        if (Request.Headers.TryGetValue("X-Test-Sub", out var sub))
            claims.Add(new Claim("sub", sub.ToString()));
        else
            claims.Add(new Claim(ClaimTypes.NameIdentifier, "u1"));
        claims.AddRange(roles.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(r => new Claim(ClaimTypes.Role, r)));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")), "Test")));
    }
}

/// <summary>A test server with the consent endpoints on a SQLite database in memory.</summary>
public sealed class TestApp : IAsyncDisposable
{
    public const string RateLimitPolicy = "consent";
    public const int RateLimit = 3;

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
    public FakeLogCollector Logs { get; } = new();
    private IHost _host = default!;
    public HttpClient Client { get; private set; } = default!;

    public IServiceProvider Services => _host.Services;

    public static async Task<TestApp> StartAsync(Action<AppKitConsentEndpointOptions>? endpoints = null,
        Action<AppKitConsentOptions>? consent = null, bool addServices = false, IEnumerable<ConsentRecord>? seed = null, string prefix = "/api", bool signInByDefault = false)
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
                        if (signInByDefault)
                            o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
                    });
                    s.AddRateLimiter(o =>
                    {
                        o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                        o.AddFixedWindowLimiter(RateLimitPolicy, l =>
                        {
                            l.PermitLimit = RateLimit;
                            l.Window = TimeSpan.FromHours(1);
                            l.QueueLimit = 0;
                        });
                    });
                    s.AddSingleton<TimeProvider>(app.Time);
                    s.AddLogging(b => b.AddProvider(new FakeLoggerProvider(app.Logs)));
                    if (addServices)
                        s.AddAppKitConsent<TestDbContext>(consent);
                    s.AddRouting();
                })
                .Configure(web =>
                {
                    web.UseRouting();
                    web.UseAuthentication();
                    web.UseAuthorization();
                    web.UseRateLimiter();
                    web.UseEndpoints(e => e.MapAppKitConsent<TestDbContext>(prefix, endpoints));
                }))
            .Build();
        using (var scope = app._host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            await db.Database.EnsureCreatedAsync();
            if (seed is not null)
            {
                db.ConsentRecords.AddRange(seed);
                await db.SaveChangesAsync();
            }
        }
        await app._host.StartAsync();
        app.Client = app._host.GetTestClient();
        return app;
    }

    public async Task<List<ConsentRecord>> RowsAsync()
    {
        using var scope = _host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TestDbContext>().ConsentRecords.AsNoTracking().OrderBy(r => r.Id).ToListAsync();
    }

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content = null, string? roles = null,
        IDictionary<string, string>? headers = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        if (roles is not null)
            request.Headers.Add("X-Test-Roles", roles);
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
            request.Headers.Add(name, value);
        return Client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        await _connection.DisposeAsync();
    }
}
