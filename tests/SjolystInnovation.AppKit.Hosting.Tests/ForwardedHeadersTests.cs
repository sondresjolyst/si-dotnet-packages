using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Xunit;

namespace SjolystInnovation.AppKit.Hosting.Tests;

public sealed class ForwardedHeadersTests
{
    private static readonly Dictionary<string, string?> Trusted = new()
    {
        ["TrustedProxies:0"] = "192.169.0.0/16",
        ["TrustedProxies:1"] = "10.0.0.0/8",
    };

    private static Dictionary<string, string> Forwarded(string forFor, string proto = "https") =>
        new() { ["X-Forwarded-For"] = forFor, ["X-Forwarded-Proto"] = proto };

    private static Task<TestApp> StartAsync(Dictionary<string, string?> config, string environment = "Production", FakeLogCollector? logs = null) =>
        TestApp.StartAsync(app => app.UseAppKitForwardedHeaders(), s =>
        {
            if (logs is not null)
                s.AddLogging(b => b.AddProvider(new FakeLoggerProvider(logs)));
        }, config, environment);

    [Fact]
    public async Task A_trusted_proxy_sets_the_client_ip_and_scheme()
    {
        await using var app = await StartAsync(Trusted);

        var context = await app.SendAsync("/", remoteIp: "192.169.36.80", headers: Forwarded("203.0.113.5"));

        Assert.Equal("203.0.113.5|https", await TestApp.BodyAsync(context));
    }

    [Fact]
    public async Task A_trusted_proxy_on_a_dual_stack_socket_counts_as_trusted()
    {
        await using var app = await StartAsync(Trusted);

        var context = await app.SendAsync("/", remoteIp: "::ffff:192.169.36.80", headers: Forwarded("203.0.113.5"));

        Assert.Equal("203.0.113.5|https", await TestApp.BodyAsync(context));
    }

    [Fact]
    public async Task An_untrusted_caller_keeps_its_own_ip_and_scheme()
    {
        await using var app = await StartAsync(Trusted);

        var context = await app.SendAsync("/", remoteIp: "198.51.100.7", headers: Forwarded("203.0.113.5"));

        Assert.Equal("198.51.100.7|http", await TestApp.BodyAsync(context));
    }

    [Fact]
    public async Task Only_the_hop_the_proxy_added_is_taken()
    {
        await using var app = await StartAsync(Trusted);

        var context = await app.SendAsync("/", remoteIp: "192.169.36.80", headers: Forwarded("10.1.2.3, 203.0.113.5"));

        Assert.Equal("203.0.113.5|https", await TestApp.BodyAsync(context));
    }

    [Fact]
    public async Task A_client_cannot_add_a_hop_from_a_trusted_network()
    {
        await using var app = await StartAsync(Trusted);

        var context = await app.SendAsync("/", remoteIp: "192.169.36.80", headers: Forwarded("203.0.113.66, 10.1.2.3"));

        Assert.Equal("10.1.2.3|https", await TestApp.BodyAsync(context));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task An_empty_list_reads_no_forwarded_header(string environment)
    {
        await using var app = await StartAsync([], environment);

        var context = await app.SendAsync("/", remoteIp: "198.51.100.7", headers: Forwarded("203.0.113.5"));

        Assert.Equal("198.51.100.7|http", await TestApp.BodyAsync(context));
    }

    [Fact]
    public async Task The_section_name_can_be_changed()
    {
        await using var app = await TestApp.StartAsync(a => a.UseAppKitForwardedHeaders("Proxies"),
            config: new() { ["Proxies:0"] = "192.169.0.0/16" });

        var context = await app.SendAsync("/", remoteIp: "192.169.36.80", headers: Forwarded("203.0.113.5"));

        Assert.Equal("203.0.113.5|https", await TestApp.BodyAsync(context));
    }

    [Fact]
    public async Task An_entry_that_is_not_a_cidr_stops_the_app()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            StartAsync(new() { ["TrustedProxies:0"] = "192.169.0.0/16", ["TrustedProxies:1"] = "everyone" }));

        Assert.Contains("'everyone'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_list_outside_development_warns()
    {
        var logs = new FakeLogCollector();
        await using var app = await StartAsync([], logs: logs);

        var warning = Assert.Single(logs.GetSnapshot(), r => r.Level == LogLevel.Warning);
        Assert.Contains("TrustedProxies is empty", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_list_in_development_stays_quiet()
    {
        var logs = new FakeLogCollector();
        await using var app = await StartAsync([], "Development", logs);

        Assert.DoesNotContain(logs.GetSnapshot(), r => r.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task A_list_with_entries_stays_quiet()
    {
        var logs = new FakeLogCollector();
        await using var app = await StartAsync(Trusted, logs: logs);

        Assert.DoesNotContain(logs.GetSnapshot(), r => r.Level == LogLevel.Warning);
    }
}
