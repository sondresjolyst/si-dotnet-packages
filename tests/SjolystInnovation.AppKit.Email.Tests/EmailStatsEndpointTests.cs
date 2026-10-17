using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Xunit;

namespace SjolystInnovation.AppKit.Email.Tests;

/// <summary>Signs a request in with the roles in <c>X-Test-Roles</c>.</summary>
internal sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-Roles", out var roles))
            return Task.FromResult(AuthenticateResult.NoResult());
        var claims = roles.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(r => new Claim(ClaimTypes.Role, r));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")), "Test")));
    }
}

public sealed class EmailStatsEndpointTests : IAsyncDisposable
{
    private sealed class FakeStats : IEmailStats
    {
        public List<int> Asked { get; } = [];
        public Exception? Throws { get; set; }

        public Task<EmailStats> GetAsync(int days = 30, CancellationToken cancellationToken = default)
        {
            Asked.Add(days);
            if (Throws is not null)
                throw Throws;
            return Task.FromResult(new EmailStats(days, 120, 110, 2, 3, 1, 4, 5));
        }
    }

    private readonly FakeStats _stats = new();
    private readonly FakeLogCollector _logs = new();
    private IHost? _host;
    private HttpClient _client = default!;

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    private async Task StartAsync(Action<IEndpointRouteBuilder>? map = null)
    {
        _host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(s =>
                {
                    s.AddSingleton<IEmailStats>(_stats);
                    s.AddLogging(b => b.AddProvider(new FakeLoggerProvider(_logs)));
                    s.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                    s.AddAuthorization(o =>
                    {
                        o.AddPolicy("Admin", p => p.RequireRole("Admin"));
                        o.AddPolicy("Mail", p => p.RequireRole("Mail"));
                    });
                    s.AddRouting();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(e => (map ?? (b => b.MapAppKitEmailStats()))(e));
                }))
            .Build();
        await _host.StartAsync();
        _client = _host.GetTestClient();
    }

    private Task<HttpResponseMessage> GetAsync(string path = "/api/admin/email-stats", string roles = "Admin")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (roles.Length > 0)
            request.Headers.Add("X-Test-Roles", roles);
        return _client.SendAsync(request);
    }

    [Fact]
    public async Task Answers_the_stats_for_30_days_in_the_shape_the_apps_used()
    {
        await StartAsync();

        var response = await GetAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            """{"days":30,"requests":120,"delivered":110,"hardBounces":2,"softBounces":3,"spamReports":1,"blocked":4,"invalid":5}""",
            await response.Content.ReadAsStringAsync());
        Assert.Equal([30], _stats.Asked);
    }

    [Fact]
    public async Task Takes_the_days_from_the_query()
    {
        await StartAsync();

        var response = await GetAsync("/api/admin/email-stats?days=7");

        Assert.Equal(7, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("days").GetInt32());
        Assert.Equal([7], _stats.Asked);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Fewer_than_one_day_is_a_validation_problem_and_asks_nothing(int days)
    {
        await StartAsync();

        var response = await GetAsync($"/api/admin/email-stats?days={days}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("errors");
        Assert.Equal("Must be at least 1.", errors.GetProperty("days")[0].GetString());
        Assert.Empty(_stats.Asked);
    }

    [Fact]
    public async Task One_day_is_allowed()
    {
        await StartAsync();

        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/api/admin/email-stats?days=1")).StatusCode);
        Assert.Equal([1], _stats.Asked);
    }

    [Fact]
    public async Task A_provider_failure_is_a_502_problem_and_is_logged()
    {
        await StartAsync();
        _stats.Throws = new HttpRequestException("Brevo is down");

        var response = await GetAsync();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Failed to fetch email stats from Brevo.", body.GetProperty("detail").GetString());
        Assert.Equal(502, body.GetProperty("status").GetInt32());
        var log = Assert.Single(_logs.GetSnapshot(), r => r.Category == "SjolystInnovation.AppKit.Email.Stats");
        Assert.Equal(LogLevel.Error, log.Level);
        Assert.IsType<HttpRequestException>(log.Exception);
    }

    [Fact]
    public async Task A_provider_timeout_is_a_502_too()
    {
        await StartAsync();
        _stats.Throws = new TaskCanceledException("timed out");

        Assert.Equal(HttpStatusCode.BadGateway, (await GetAsync()).StatusCode);
    }

    [Fact]
    public async Task Needs_the_admin_policy()
    {
        await StartAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(roles: "")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(roles: "Default")).StatusCode);
        Assert.Empty(_stats.Asked);
    }

    [Fact]
    public async Task The_route_and_policy_can_be_changed()
    {
        await StartAsync(e => e.MapAppKitEmailStats("/admin/mail", o => o.Policy = "Mail"));

        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync("/admin/mail", roles: "Admin")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/admin/mail", roles: "Mail")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync()).StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public void A_blank_policy_fails_at_mapping(string? policy)
    {
        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();

        Assert.ThrowsAny<ArgumentException>(() => app.MapAppKitEmailStats(configure: o => o.Policy = policy!));
    }
}
