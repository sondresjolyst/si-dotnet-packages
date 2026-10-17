using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Xunit;

namespace SjolystInnovation.AppKit.Hosting.Tests;

public sealed class RequestLoggingTests
{
    private const string Category = "SjolystInnovation.AppKit.Hosting.RequestLoggingExtensions";

    private static Task<TestApp> StartAsync(FakeLogCollector logs, Action<AppKitRequestLoggingOptions>? configure = null, LogLevel level = LogLevel.Debug) =>
        TestApp.StartAsync(app => app.UseAppKitRequestLogging(configure), s => s.AddLogging(b =>
        {
            b.SetMinimumLevel(level);
            b.AddProvider(new FakeLoggerProvider(logs));
        }));

    private static List<FakeLogRecord> Records(FakeLogCollector logs) =>
        [.. logs.GetSnapshot().Where(r => r.Category == Category)];

    // Only this middleware's records. ASP.NET Core's own "Request starting" record carries the full URL.
    private static string AllText(FakeLogCollector logs) =>
        string.Join("\n", Records(logs).Select(r => r.Message + " " + string.Join(" ", r.StructuredState?.Select(p => p.Value) ?? [])));

    // What garge-api logged before the move.

    [Fact]
    public async Task Each_request_logs_its_method_and_path_at_information()
    {
        var logs = new FakeLogCollector();
        await using var app = await StartAsync(logs);

        await app.SendAsync("/api/sensors/7", method: "POST");

        var record = Assert.Single(Records(logs), r => r.Level == LogLevel.Information);
        Assert.Equal("Incoming Request { Method = POST, Path = /api/sensors/7 }", record.Message);
        Assert.Contains(record.StructuredState!, p => p.Key == "@LogData");
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/health/ready")]
    [InlineData("/HEALTH")]
    public async Task Health_probes_are_not_logged(string path)
    {
        var logs = new FakeLogCollector();
        await using var app = await StartAsync(logs);

        var context = await app.SendAsync(path, headers: new() { ["X-Probe"] = "kubelet" });

        Assert.Empty(Records(logs));
        Assert.Equal(200, context.Response.StatusCode);
    }

    [Fact]
    public async Task A_path_that_only_starts_with_health_is_logged()
    {
        var logs = new FakeLogCollector();
        await using var app = await StartAsync(logs);

        await app.SendAsync("/healthz");

        Assert.Single(Records(logs), r => r.Level == LogLevel.Information);
    }

    [Fact]
    public async Task Headers_are_logged_at_debug_except_the_sensitive_ones()
    {
        var logs = new FakeLogCollector();
        await using var app = await StartAsync(logs);

        await app.SendAsync("/api/x", headers: new()
        {
            ["User-Agent"] = "garge-app",
            ["authorization"] = "Bearer eyJ.secret.jwt",
            ["Cookie"] = "session=cookie-secret",
            ["Set-Cookie"] = "a=set-cookie-secret",
            ["Proxy-Authorization"] = "Basic proxy-secret",
            ["X-API-KEY"] = "api-key-secret",
            ["Idempotency-Key"] = "idempotency-secret",
        });

        var headers = Records(logs).Where(r => r.Level == LogLevel.Debug).Select(r => r.Message).ToList();
        Assert.Contains("Header { Key = User-Agent, Value = garge-app }", headers);
        Assert.Equal(["User-Agent"], headers.Select(h => h.Split(',')[0].Replace("Header { Key = ", "", StringComparison.Ordinal)).Where(k => k != "Host"));
        Assert.DoesNotContain("secret", AllText(logs), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Headers_are_not_logged_when_debug_is_off()
    {
        var logs = new FakeLogCollector();
        await using var app = await StartAsync(logs, level: LogLevel.Information);

        await app.SendAsync("/api/x", headers: new() { ["User-Agent"] = "garge-app" });

        Assert.Equal([LogLevel.Information], Records(logs).Select(r => r.Level));
    }

    [Fact]
    public async Task Nothing_is_logged_when_information_is_off()
    {
        var logs = new FakeLogCollector();
        await using var app = await StartAsync(logs, level: LogLevel.Warning);

        await app.SendAsync("/api/x");

        Assert.Empty(Records(logs));
    }

    [Fact]
    public async Task The_query_string_is_not_logged_by_default()
    {
        var logs = new FakeLogCollector();
        await using var app = await StartAsync(logs);

        await app.SendAsync("/hubs/sensors", query: "?access_token=eyJ.query.secret&page=2");

        Assert.Equal("Incoming Request { Method = GET, Path = /hubs/sensors }", Records(logs).Single(r => r.Level == LogLevel.Information).Message);
        Assert.DoesNotContain("secret", AllText(logs), StringComparison.Ordinal);
        Assert.DoesNotContain("page", AllText(logs), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_body_is_never_read()
    {
        var logs = new FakeLogCollector();
        await using var app = await StartAsync(logs);
        using var body = new UnreadableStream();

        await app.SendAsync("/api/auth/login", method: "POST", body: body,
            headers: new() { ["Content-Type"] = "application/json" });

        Assert.False(body.WasRead);
        Assert.Single(Records(logs), r => r.Level == LogLevel.Information);
    }

    [Fact]
    public async Task The_next_middleware_still_runs()
    {
        var logs = new FakeLogCollector();
        await using var app = await StartAsync(logs);

        var context = await app.SendAsync("/api/x", remoteIp: "198.51.100.7");

        Assert.Equal("198.51.100.7|http", await TestApp.BodyAsync(context));
    }

    // Redaction and line breaks.

    [Fact]
    public async Task A_path_segment_that_looks_like_an_email_is_redacted()
    {
        var logs = new FakeLogCollector();
        await using var app = await StartAsync(logs);

        await app.SendAsync("/api/users/ola.nordmann@example.no/profile");

        Assert.Equal("Incoming Request { Method = GET, Path = /api/users/[Redacted]/profile }", Records(logs).Single(r => r.Level == LogLevel.Information).Message);
        Assert.DoesNotContain("nordmann", AllText(logs), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Line_breaks_in_the_path_and_header_values_cannot_forge_a_log_line()
    {
        var logs = new FakeLogCollector();
        await using var app = await StartAsync(logs);

        await app.SendAsync("/api/x\r\nIncoming Request fake", headers: new() { ["X-Note"] = "a\nb\rc" });

        var messages = Records(logs).Select(r => r.Message).ToList();
        Assert.Contains("Incoming Request { Method = GET, Path = /api/xIncoming Request fake }", messages);
        Assert.Contains("Header { Key = X-Note, Value = abc }", messages);
        Assert.DoesNotContain(messages, m => m.Contains('\n', StringComparison.Ordinal) || m.Contains('\r', StringComparison.Ordinal));
    }

    // Options.

    [Fact]
    public async Task Skipped_paths_can_be_changed()
    {
        var logs = new FakeLogCollector();
        await using var app = await StartAsync(logs, o =>
        {
            o.SkippedPaths.Clear();
            o.SkippedPaths.Add("/metrics");
        });

        await app.SendAsync("/metrics");
        await app.SendAsync("/metrics/http");
        await app.SendAsync("/health");

        Assert.Equal(["Incoming Request { Method = GET, Path = /health }"], Records(logs).Where(r => r.Level == LogLevel.Information).Select(r => r.Message));
    }

    [Fact]
    public async Task A_skipped_path_without_a_leading_slash_stops_the_app()
    {
        var logs = new FakeLogCollector();

        await Assert.ThrowsAsync<ArgumentException>(() => StartAsync(logs, o => o.SkippedPaths.Add("metrics")));
    }

    [Fact]
    public async Task More_headers_can_be_marked_sensitive_ignoring_case()
    {
        var logs = new FakeLogCollector();
        await using var app = await StartAsync(logs, o => o.SensitiveHeaders.Add("X-Tenant-Secret"));

        await app.SendAsync("/api/x", headers: new() { ["x-tenant-secret"] = "tenant-value", ["Authorization"] = "Bearer jwt-value" });

        Assert.DoesNotContain("tenant-value", AllText(logs), StringComparison.Ordinal);
        Assert.DoesNotContain("jwt-value", AllText(logs), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_default_sensitive_header_can_be_logged_after_removing_it()
    {
        var logs = new FakeLogCollector();
        await using var app = await StartAsync(logs, o =>
        {
            o.SensitiveHeaders.Remove("Idempotency-Key");
            o.SensitiveHeaderParts.Remove("key");
        });

        await app.SendAsync("/api/x", headers: new() { ["Idempotency-Key"] = "k-1" });

        Assert.Contains(Records(logs), r => r.Message == "Header { Key = Idempotency-Key, Value = k-1 }");
    }

    [Fact]
    public async Task Headers_whose_names_hold_a_sensitive_part_are_logged_redacted()
    {
        var logs = new FakeLogCollector();
        await using var app = await StartAsync(logs);

        await app.SendAsync("/api/x", headers: new()
        {
            ["X-Vipps-Signature"] = "sig-secret",
            ["X-CSRF-TOKEN"] = "csrf-secret",
            ["X-Session-Id"] = "session-secret",
            ["X-Auth-Request-User"] = "auth-secret",
            ["X-Client-Password"] = "password-secret",
            ["X-Xsrf"] = "xsrf-secret",
            ["X-Csrf-Guard"] = "guard-secret",
            ["X-Webhook-Secret"] = "webhook-secret",
            ["X-Cookie-Id"] = "cookie-secret",
            ["X-Note"] = "plain",
        });

        var messages = Records(logs).Select(r => r.Message).ToList();
        Assert.DoesNotContain("secret", AllText(logs), StringComparison.Ordinal);
        Assert.Contains("Header { Key = X-Vipps-Signature, Value = [Redacted] }", messages);
        Assert.Contains("Header { Key = X-CSRF-TOKEN, Value = [Redacted] }", messages);
        Assert.Contains("Header { Key = X-Note, Value = plain }", messages);
    }

    [Fact]
    public async Task A_header_value_that_looks_like_an_email_is_redacted()
    {
        var logs = new FakeLogCollector();
        await using var app = await StartAsync(logs);

        await app.SendAsync("/api/x", headers: new() { ["X-Forwarded-User"] = "ola@example.com" });

        Assert.Contains(Records(logs), r => r.Message == "Header { Key = X-Forwarded-User, Value = [Redacted] }");
        Assert.DoesNotContain("ola@example.com", AllText(logs), StringComparison.Ordinal);
    }

    [Fact]
    public async Task More_header_name_parts_can_be_marked_sensitive()
    {
        var logs = new FakeLogCollector();
        await using var app = await StartAsync(logs, o => o.SensitiveHeaderParts.Add("tenant"));

        await app.SendAsync("/api/x", headers: new() { ["X-TENANT-Id"] = "tenant-value" });

        Assert.Contains(Records(logs), r => r.Message == "Header { Key = X-TENANT-Id, Value = [Redacted] }");
    }

    [Fact]
    public async Task An_opted_in_query_string_is_logged_with_its_secrets_and_emails_redacted()
    {
        var logs = new FakeLogCollector();
        await using var app = await StartAsync(logs, o => o.LogQueryString = true);

        await app.SendAsync("/hubs/sensors", query: "?access_token=t1&page=2&Email=ola%40example.no&q=kari@example.no&code=123456&newPassword=p1&client_secret=s1&apiKey=k1&sig=x1&flag");

        Assert.Equal(
            "Incoming Request { Method = GET, Path = /hubs/sensors, Query = ?access_token=[Redacted]&page=2&Email=[Redacted]&q=[Redacted]&code=[Redacted]&newPassword=[Redacted]&client_secret=[Redacted]&apiKey=[Redacted]&sig=[Redacted]&flag }",
            Records(logs).Single(r => r.Level == LogLevel.Information).Message);
    }

    [Fact]
    public async Task The_sensitive_query_names_can_be_changed()
    {
        var logs = new FakeLogCollector();
        await using var app = await StartAsync(logs, o =>
        {
            o.LogQueryString = true;
            o.SensitiveQueryParameters.Clear();
            o.SensitiveQueryParameters.Add("pin");
        });

        await app.SendAsync("/api/x", query: "?pin=1234&code=abc");

        Assert.EndsWith("Query = ?pin=[Redacted]&code=abc }", Records(logs).Single(r => r.Level == LogLevel.Information).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_opted_in_empty_query_string_is_logged_as_empty()
    {
        var logs = new FakeLogCollector();
        await using var app = await StartAsync(logs, o => o.LogQueryString = true);

        await app.SendAsync("/api/x");

        Assert.Equal("Incoming Request { Method = GET, Path = /api/x, Query =  }", Records(logs).Single(r => r.Level == LogLevel.Information).Message);
    }

    private sealed class UnreadableStream : MemoryStream
    {
        public bool WasRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            WasRead = true;
            return 0;
        }

        public override int Read(Span<byte> buffer)
        {
            WasRead = true;
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            WasRead = true;
            return Task.FromResult(0);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WasRead = true;
            return ValueTask.FromResult(0);
        }
    }
}

public sealed class LogSanitizerTests
{
    private static readonly string[] Defaults = [.. new AppKitRequestLoggingOptions().SensitiveQueryParameters];

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a\rb", "ab")]
    [InlineData("a\nb", "ab")]
    [InlineData("a\r\nb\n", "ab")]
    [InlineData("a\u0085b", "ab")]
    [InlineData("tab\tstays", "tab\tstays")]
    [InlineData("", "")]
    public void Sanitize_removes_line_breaks(string input, string expected) =>
        Assert.Equal(expected, LogSanitizer.Sanitize(input));

    [Fact]
    public void Sanitize_removes_the_unicode_line_and_paragraph_separators()
    {
        Assert.Equal("ab", LogSanitizer.Sanitize("a" + (char)0x2028 + "b"));
        Assert.Equal("ab", LogSanitizer.Sanitize("a" + (char)0x2029 + "b"));
    }

    [Fact]
    public void Sanitize_keeps_null() => Assert.Null(LogSanitizer.Sanitize(null));

    [Theory]
    [InlineData("ola@example.no", true)]
    [InlineData("o@e.no", true)]
    [InlineData("first.last+tag@sub.example.com", true)]
    [InlineData("@example.no", false)]
    [InlineData("ola@", false)]
    [InlineData("ola@example", false)]
    [InlineData("ola@.no", false)]
    [InlineData("ola@example.", false)]
    [InlineData("sensor-7", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void LooksLikeEmail_matches_the_shape_of_an_address(string? value, bool expected) =>
        Assert.Equal(expected, LogSanitizer.LooksLikeEmail(value));

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("/", "/")]
    [InlineData("/api/sensors/7", "/api/sensors/7")]
    [InlineData("/a@b.no", "/[Redacted]")]
    [InlineData("/api/a@b.no/x/c@d.no", "/api/[Redacted]/x/[Redacted]")]
    [InlineData("/api/@handle", "/api/@handle")]
    public void RedactPath_replaces_email_segments(string? path, string expected) =>
        Assert.Equal(expected, LogSanitizer.RedactPath(path));

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("?", "")]
    [InlineData("?page=2", "?page=2")]
    [InlineData("?flag", "?flag")]
    [InlineData("?flag&page=2", "?flag&page=2")]
    [InlineData("?page=", "?page=")]
    [InlineData("?token=", "?token=[Redacted]")]
    [InlineData("?ACCESS_TOKEN=abc", "?ACCESS_TOKEN=[Redacted]")]
    [InlineData("?refresh_token=abc&id_token=def", "?refresh_token=[Redacted]&id_token=[Redacted]")]
    [InlineData("?password=hunter2", "?password=[Redacted]")]
    [InlineData("?pass%77ord=hunter2", "?pass%77ord=[Redacted]")]
    [InlineData("?secret=s", "?secret=[Redacted]")]
    [InlineData("?code=123456", "?code=[Redacted]")]
    [InlineData("?key=k", "?key=[Redacted]")]
    [InlineData("?signature=s", "?signature=[Redacted]")]
    [InlineData("?email=x", "?email=[Redacted]")]
    [InlineData("?to=ola%40example.no", "?to=[Redacted]")]
    [InlineData("?to=ola@example.no", "?to=[Redacted]")]
    [InlineData("?q=ola+nordmann@example.no", "?q=[Redacted]")]
    [InlineData("?q=a=b", "?q=a=b")]
    [InlineData("?token=a=b&page=1", "?token=[Redacted]&page=1")]
    [InlineData("page=2", "?page=2")]
    [InlineData("?a=%0d%0a", "?a=%0d%0a")]
    public void RedactQuery_redacts_sensitive_names_and_email_values(string? query, string expected) =>
        Assert.Equal(expected, LogSanitizer.RedactQuery(query, Defaults));

    [Fact]
    public void RedactQuery_with_no_sensitive_names_still_redacts_emails() =>
        Assert.Equal("?token=t&to=[Redacted]", LogSanitizer.RedactQuery("?token=t&to=a@b.no", []));

    [Fact]
    public void The_redacted_marker_is_stable() => Assert.Equal("[Redacted]", LogSanitizer.Redacted);
}
