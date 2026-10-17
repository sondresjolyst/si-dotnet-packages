using System.Data.Common;
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
using Microsoft.Extensions.Time.Testing;

namespace SjolystInnovation.AppKit.AdminStats.Tests;

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

/// <summary>
/// A test server with the history of <see cref="ShapeSnapshot"/> on SQLite in memory, with the clock at
/// <see cref="TestDb.Now"/>. The hosted service is left out unless <c>runService</c> is set. With the
/// service running, the database is a temporary file, because the service and the test use it from
/// several threads at once and one in-memory connection is not safe for that.
/// </summary>
public sealed class TestApp : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private string? _file;
    private IHost _host = default!;

    public FakeTimeProvider Time { get; } = new(TestDb.Now);
    public HttpClient Client { get; private set; } = default!;

    /// <summary>The counts the live sampler answers.</summary>
    public ShapeSnapshot Live { get; set; } = new() { TotalUsers = 3, StorageBytes = 5_000_000_000, Note = "live", Optional = 7, Small = 2, Flag = true, Amount = 1.5m };

    public static async Task<TestApp> StartAsync(
        Action<AppKitDailyStatsEndpointOptions>? endpoints = null,
        Action<AppKitDailyStatsOptions<TestDbContext, ShapeSnapshot>>? stats = null,
        bool runService = false,
        IEnumerable<ShapeSnapshot>? seed = null)
    {
        var app = new TestApp();
        if (runService)
            app._file = Path.Combine(Path.GetTempPath(), $"appkit-adminstats-app-{Guid.NewGuid():N}.db");
        else
            await app._connection.OpenAsync();
        app._host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(s =>
                {
                    s.AddDbContext<TestDbContext>(o =>
                    {
                        if (app._file is null)
                            o.UseSqlite((DbConnection)app._connection);
                        else
                            o.UseSqlite($"Data Source={app._file};Pooling=False");
                    });
                    s.AddSingleton<TimeProvider>(app.Time);
                    s.AddAppKitDailyStats<TestDbContext, ShapeSnapshot>(o =>
                    {
                        o.LiveSampler = (_, _) => Task.FromResult(new ShapeSnapshot
                        {
                            TotalUsers = app.Live.TotalUsers,
                            StorageBytes = app.Live.StorageBytes,
                            Note = app.Live.Note,
                            Optional = app.Live.Optional,
                            Small = app.Live.Small,
                            Flag = app.Live.Flag,
                            Amount = app.Live.Amount,
                        });
                        stats?.Invoke(o);
                    });
                    if (!runService)
                    {
                        foreach (var service in s.Where(d => d.ImplementationType == typeof(DailyStatsService<TestDbContext, ShapeSnapshot>)).ToList())
                            s.Remove(service);
                    }
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
                    web.UseEndpoints(e => e.MapAppKitDailyStats<TestDbContext, ShapeSnapshot>(configure: endpoints));
                }))
            .Build();
        using (var scope = app._host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.AddRange(seed ?? []);
            await db.SaveChangesAsync();
        }
        await app._host.StartAsync();
        app.Client = app._host.GetTestClient();
        return app;
    }

    public async Task<List<ShapeSnapshot>> StoredAsync()
    {
        using var scope = _host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TestDbContext>().ShapeSnapshots.AsNoTracking().OrderBy(s => s.Date).ToListAsync();
    }

    public Task<HttpResponseMessage> GetAsync(string path, string? roles = "Admin")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (roles is not null)
            request.Headers.Add("X-Test-Roles", roles);
        return Client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        await _connection.DisposeAsync();
        if (_file is not null && File.Exists(_file))
            File.Delete(_file);
    }
}
