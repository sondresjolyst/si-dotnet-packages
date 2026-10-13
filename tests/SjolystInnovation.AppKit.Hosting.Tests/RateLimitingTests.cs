using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace SjolystInnovation.AppKit.Hosting.Tests;

public sealed class RateLimitingTests
{
    private static Dictionary<string, string?> Rules(params (string Endpoint, string Period, int Limit)[] rules)
    {
        var config = new Dictionary<string, string?>();
        for (var i = 0; i < rules.Length; i++)
        {
            config[$"RateLimiting:Rules:{i}:Endpoint"] = rules[i].Endpoint;
            config[$"RateLimiting:Rules:{i}:Period"] = rules[i].Period;
            config[$"RateLimiting:Rules:{i}:Limit"] = rules[i].Limit.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return config;
    }

    private static Task<TestApp> StartAsync(Dictionary<string, string?> config, bool forwarded = false) =>
        TestApp.StartAsync(app =>
        {
            if (forwarded)
                app.UseAppKitForwardedHeaders();
            app.UseAppKitRateLimiting();
        }, s =>
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
            s.AddAppKitRateLimiting(configuration.GetSection("RateLimiting"));
        }, config);

    private static async Task<int> StatusAfterAsync(TestApp app, int requests, string path, string remoteIp = "203.0.113.9",
        string method = "GET", Dictionary<string, string>? headers = null)
    {
        var status = 0;
        for (var i = 0; i < requests; i++)
            status = (await app.SendAsync(path, remoteIp, method, headers)).Response.StatusCode;
        return status;
    }

    [Fact]
    public async Task Requests_over_the_limit_get_429_with_retry_after_and_a_problem()
    {
        await using var app = await StartAsync(Rules(("*:/api/auth/login", "1m", 2)));

        Assert.Equal(200, await StatusAfterAsync(app, 2, "/api/auth/login"));
        var rejected = await app.SendAsync("/api/auth/login");

        Assert.Equal(StatusCodes.Status429TooManyRequests, rejected.Response.StatusCode);
        var retryAfter = int.Parse(rejected.Response.Headers.RetryAfter.ToString(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(retryAfter, 1, 60);
        using var problem = JsonDocument.Parse(await TestApp.BodyAsync(rejected));
        Assert.Equal(429, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("Too many requests", problem.RootElement.GetProperty("title").GetString());
        Assert.False(problem.RootElement.TryGetProperty("detail", out _));
    }

    [Fact]
    public async Task Each_client_ip_has_its_own_limit()
    {
        await using var app = await StartAsync(Rules(("*:/api/auth/login", "1m", 2)));

        Assert.Equal(429, await StatusAfterAsync(app, 3, "/api/auth/login", "203.0.113.1"));
        Assert.Equal(200, await StatusAfterAsync(app, 2, "/api/auth/login", "203.0.113.2"));
    }

    [Fact]
    public async Task An_ipv4_caller_counts_the_same_on_a_dual_stack_socket()
    {
        await using var app = await StartAsync(Rules(("*:/api/auth/login", "1m", 2)));

        await StatusAfterAsync(app, 2, "/api/auth/login", "203.0.113.1");

        Assert.Equal(429, await StatusAfterAsync(app, 1, "/api/auth/login", "::ffff:203.0.113.1"));
    }

    [Fact]
    public async Task Paths_without_a_rule_are_not_limited()
    {
        await using var app = await StartAsync(Rules(("*:/api/auth/login", "1m", 1)));

        Assert.Equal(200, await StatusAfterAsync(app, 5, "/api/products"));
        Assert.Equal(200, await StatusAfterAsync(app, 3, "/api/auth/login/extra"));
        Assert.Equal(200, await StatusAfterAsync(app, 3, "/v2/api/auth/login"));
    }

    [Fact]
    public async Task A_rule_for_one_verb_leaves_the_others_alone()
    {
        await using var app = await StartAsync(Rules(("POST:/api/contact", "1m", 1)));

        Assert.Equal(200, await StatusAfterAsync(app, 3, "/api/contact", method: "GET"));
        Assert.Equal(429, await StatusAfterAsync(app, 2, "/api/contact", method: "post"));
    }

    [Fact]
    public async Task A_star_in_the_path_matches_any_run_of_characters_and_case_is_ignored()
    {
        await using var app = await StartAsync(Rules(("*:/api/products*", "1m", 2)));

        await app.SendAsync("/api/products");
        await app.SendAsync("/API/Products/5");

        Assert.Equal(429, (await app.SendAsync("/api/products/7/photos")).Response.StatusCode);
    }

    [Fact]
    public async Task A_request_matching_two_rules_counts_against_both()
    {
        await using var app = await StartAsync(Rules(("*:/api/*", "1m", 4), ("*:/api/auth/login", "1m", 2)));

        Assert.Equal(429, await StatusAfterAsync(app, 3, "/api/auth/login"));
        Assert.Equal(200, await StatusAfterAsync(app, 2, "/api/other", "203.0.113.50"));
        await StatusAfterAsync(app, 2, "/api/auth/login", "203.0.113.50");

        Assert.Equal(429, (await app.SendAsync("/api/other", "203.0.113.50")).Response.StatusCode);
    }

    [Fact]
    public async Task A_bad_rule_stops_the_app_even_without_the_middleware()
    {
        var config = Rules(("*:/api/auth/login", "1x", 1));
        await Assert.ThrowsAsync<OptionsValidationException>(() => TestApp.StartAsync(_ => { }, s =>
            s.AddAppKitRateLimiting(new ConfigurationBuilder().AddInMemoryCollection(config).Build().GetSection("RateLimiting")), config));
    }

    [Fact]
    public async Task No_rules_means_no_limit()
    {
        await using var app = await StartAsync([]);

        Assert.Equal(200, await StatusAfterAsync(app, 20, "/api/auth/login"));
    }

    [Fact]
    public async Task The_limit_comes_back_after_the_period()
    {
        await using var app = await StartAsync(Rules(("*:/api/auth/login", "1s", 1)));

        Assert.Equal(429, await StatusAfterAsync(app, 2, "/api/auth/login"));
        await Task.Delay(TimeSpan.FromMilliseconds(2100));

        Assert.Equal(200, (await app.SendAsync("/api/auth/login")).Response.StatusCode);
    }

    [Fact]
    public async Task Behind_a_trusted_proxy_each_forwarded_client_has_its_own_limit()
    {
        var config = Rules(("*:/api/auth/login", "1m", 2));
        config["TrustedProxies:0"] = "192.169.0.0/16";
        await using var app = await StartAsync(config, forwarded: true);

        static Dictionary<string, string> From(string ip) => new() { ["X-Forwarded-For"] = ip };
        Assert.Equal(429, await StatusAfterAsync(app, 3, "/api/auth/login", "192.169.36.80", headers: From("203.0.113.1")));
        Assert.Equal(200, await StatusAfterAsync(app, 2, "/api/auth/login", "192.169.36.80", headers: From("203.0.113.2")));
    }

    [Fact]
    public async Task Headers_from_a_direct_caller_do_not_change_its_limit()
    {
        var config = Rules(("*:/api/auth/login", "1m", 2));
        config["TrustedProxies:0"] = "192.169.0.0/16";
        await using var app = await StartAsync(config, forwarded: true);

        for (var i = 1; i <= 2; i++)
        {
            await app.SendAsync("/api/auth/login", "198.51.100.7",
                headers: new() { ["X-Forwarded-For"] = $"203.0.113.{i}", ["X-Real-IP"] = $"203.0.113.{i}" });
        }
        var third = await app.SendAsync("/api/auth/login", "198.51.100.7",
            headers: new() { ["X-Forwarded-For"] = "203.0.113.3", ["X-Real-IP"] = "203.0.113.3" });

        Assert.Equal(429, third.Response.StatusCode);
    }

    [Theory]
    [InlineData("/api/auth/login", "1m", 1)]
    [InlineData("*:api/auth/login", "1m", 1)]
    [InlineData("G E T:/api/auth/login", "1m", 1)]
    [InlineData(":/api/auth/login", "1m", 1)]
    [InlineData("*:/api/auth/login", "1x", 1)]
    [InlineData("*:/api/auth/login", "0m", 1)]
    [InlineData("*:/api/auth/login", "m", 1)]
    [InlineData("*:/api/auth/login", "-1m", 1)]
    [InlineData("*:/api/auth/login", "1m", 0)]
    public async Task A_rule_it_cannot_read_stops_the_app(string endpoint, string period, int limit)
    {
        await Assert.ThrowsAsync<OptionsValidationException>(() => StartAsync(Rules(("*:/api/ok", "1m", 1), (endpoint, period, limit))));
    }

    [Theory]
    [InlineData("30s", 30)]
    [InlineData("1m", 60)]
    [InlineData("2h", 7200)]
    [InlineData("1d", 86400)]
    public void Periods_take_seconds_minutes_hours_and_days(string text, int seconds)
    {
        Assert.True(CompiledRule.TryParsePeriod(text, out var period));
        Assert.Equal(TimeSpan.FromSeconds(seconds), period);
    }

    [Fact]
    public void The_rules_bind_from_configuration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(Rules(("POST:/api/contact", "1m", 5))).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddAppKitRateLimiting(configuration.GetSection("RateLimiting"));
        using var provider = services.BuildServiceProvider();

        var rule = Assert.Single(provider.GetRequiredService<IOptions<AppKitRateLimitOptions>>().Value.Rules);
        Assert.Equal(("POST:/api/contact", "1m", 5), (rule.Endpoint, rule.Period, rule.Limit));
    }
}
