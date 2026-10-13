using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace SjolystInnovation.AppKit.Hosting.Tests;

public sealed class SecurityAndHttpsTests
{
    private static readonly Dictionary<string, string?> HttpsPort = new() { ["https_port"] = "443" };

    [Fact]
    public async Task Every_response_gets_the_security_headers()
    {
        await using var app = await TestApp.StartAsync(a => a.UseAppKitSecurityHeaders(),
            endpoints: e => e.MapGet("/missing", () => Results.NotFound()));

        foreach (var path in new[] { "/", "/missing" })
        {
            var context = await app.SendAsync(path);
            Assert.Equal("nosniff", context.Response.Headers.XContentTypeOptions.ToString());
            Assert.Equal("DENY", context.Response.Headers.XFrameOptions.ToString());
        }
    }

    [Fact]
    public async Task Plain_http_is_redirected_to_https_outside_development()
    {
        await using var app = await TestApp.StartAsync(a => a.UseAppKitHttps(), config: HttpsPort);

        var context = await app.SendAsync("/orders");

        Assert.Equal(StatusCodes.Status307TemporaryRedirect, context.Response.StatusCode);
        Assert.Equal("https://api.example.com/orders", context.Response.Headers.Location.ToString());
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/health/ready")]
    public async Task Health_probes_are_never_redirected(string path)
    {
        await using var app = await TestApp.StartAsync(a => a.UseAppKitHttps(), config: HttpsPort);

        var context = await app.SendAsync(path);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task A_path_that_only_starts_like_health_is_redirected()
    {
        await using var app = await TestApp.StartAsync(a => a.UseAppKitHttps(), config: HttpsPort);

        var context = await app.SendAsync("/healthy");

        Assert.Equal(StatusCodes.Status307TemporaryRedirect, context.Response.StatusCode);
    }

    [Fact]
    public async Task Https_responses_carry_hsts_outside_development()
    {
        await using var app = await TestApp.StartAsync(a => a.UseAppKitHttps(), config: HttpsPort);

        var context = await app.SendAsync("/", scheme: "https");

        Assert.StartsWith("max-age=", context.Response.Headers.StrictTransportSecurity.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Development_neither_redirects_nor_sends_hsts()
    {
        await using var app = await TestApp.StartAsync(a => a.UseAppKitHttps(), config: HttpsPort, environment: "Development");

        var plain = await app.SendAsync("/orders");
        var secure = await app.SendAsync("/", scheme: "https");

        Assert.Equal(StatusCodes.Status200OK, plain.Response.StatusCode);
        Assert.Empty(secure.Response.Headers.StrictTransportSecurity.ToString());
    }

    [Fact]
    public async Task The_scheme_the_ingress_saw_counts()
    {
        await using var app = await TestApp.StartAsync(a =>
        {
            a.UseAppKitForwardedHeaders();
            a.UseAppKitHttps();
        }, config: new() { ["https_port"] = "443", ["TrustedProxies:0"] = "192.169.0.0/16" });

        var context = await app.SendAsync("/orders", remoteIp: "192.169.36.80",
            headers: new() { ["X-Forwarded-For"] = "203.0.113.5", ["X-Forwarded-Proto"] = "https" });

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }
}
