using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SjolystInnovation.AppKit.Hosting.Tests;

public sealed class ProbeDbContext(DbContextOptions<ProbeDbContext> options) : DbContext(options);

public sealed class HealthChecksTests : IDisposable
{
    // Kept open so the in-memory database lives for the whole test.
    private readonly SqliteConnection _memory = new("Data Source=:memory:");

    public HealthChecksTests() => _memory.Open();

    public void Dispose() => _memory.Dispose();

    private Task<TestApp> StartAsync(bool databaseUp) => TestApp.StartAsync(_ => { }, s =>
    {
        s.AddDbContext<ProbeDbContext>(o =>
        {
            if (databaseUp)
                o.UseSqlite(_memory);
            else
                o.UseSqlite("Data Source=Z:/no/such/folder/probe.db;Mode=ReadOnly");
        });
        s.AddAppKitHealthChecks<ProbeDbContext>();
    }, endpoints: e => e.MapAppKitHealthChecks());

    [Fact]
    public async Task Both_probes_pass_while_the_database_is_up()
    {
        await using var app = await StartAsync(databaseUp: true);

        Assert.Equal(StatusCodes.Status200OK, (await app.SendAsync("/health")).Response.StatusCode);
        Assert.Equal(StatusCodes.Status200OK, (await app.SendAsync("/health/ready")).Response.StatusCode);
    }

    [Fact]
    public async Task Without_the_database_only_readiness_fails()
    {
        await using var app = await StartAsync(databaseUp: false);

        Assert.Equal(StatusCodes.Status200OK, (await app.SendAsync("/health")).Response.StatusCode);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, (await app.SendAsync("/health/ready")).Response.StatusCode);
    }

    [Fact]
    public async Task Both_probes_allow_anonymous_callers()
    {
        await using var app = await StartAsync(databaseUp: true);

        var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText is "/health" or "/health/ready")
            .ToList();

        Assert.Equal(2, endpoints.Count);
        Assert.All(endpoints, e => Assert.NotNull(e.Metadata.GetMetadata<IAllowAnonymous>()));
    }
}
