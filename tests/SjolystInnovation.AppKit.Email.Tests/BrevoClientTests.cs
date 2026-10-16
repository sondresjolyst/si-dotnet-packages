using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Xunit;

namespace SjolystInnovation.AppKit.Email.Tests;

public sealed class BrevoClientTests : IDisposable
{
    private sealed class FakeBrevo : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Sent { get; } = [];
        public Func<HttpRequestMessage, HttpResponseMessage>? Respond { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Sent.Add((request, body));
            return Respond?.Invoke(request) ?? Json(HttpStatusCode.Created, """{"messageId":"<id-1@smtp-relay.mailin.fr>"}""");
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private readonly FakeBrevo _brevo = new();
    private readonly FakeLogger<BrevoClient> _logger = new();

    public void Dispose() => _brevo.Dispose();

    private BrevoClient Client(string baseUrl = "https://api.brevo.com/v3/") =>
        new(new HttpClient(_brevo), Options.Create(new BrevoOptions
        {
            ApiKey = "xkeysib-test",
            SenderEmail = "post@example.invalid",
            SenderName = "Garge",
            BaseUrl = baseUrl,
        }), _logger);

    private JsonElement SentBody() => JsonDocument.Parse(Assert.Single(_brevo.Sent).Body).RootElement;

    [Fact]
    public async Task Sends_one_email_to_brevo_with_the_key_and_the_sender()
    {
        var result = await Client().SendAsync(new EmailMessage("kari@example.invalid", "Your code", "<p>123456</p>"));

        Assert.Equal(new EmailResult(true, "<id-1@smtp-relay.mailin.fr>"), result);
        var (request, _) = Assert.Single(_brevo.Sent);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.brevo.com/v3/smtp/email", request.RequestUri!.ToString());
        Assert.Equal("xkeysib-test", Assert.Single(request.Headers.GetValues("api-key")));
        Assert.Contains("application/json", request.Headers.Accept.ToString(), StringComparison.Ordinal);
        var body = SentBody();
        Assert.Equal("post@example.invalid", body.GetProperty("sender").GetProperty("email").GetString());
        Assert.Equal("Garge", body.GetProperty("sender").GetProperty("name").GetString());
        Assert.Equal("kari@example.invalid", Assert.Single(body.GetProperty("to").EnumerateArray()).GetProperty("email").GetString());
        Assert.Equal("Your code", body.GetProperty("subject").GetString());
        Assert.Equal("<p>123456</p>", body.GetProperty("htmlContent").GetString());
        Assert.False(body.TryGetProperty("replyTo", out _));
        Assert.False(body.TryGetProperty("attachment", out _));
        Assert.False(body.GetProperty("to")[0].TryGetProperty("name", out _));
    }

    [Fact]
    public async Task Sends_the_reply_to_address_and_the_attachments()
    {
        await Client().SendAsync(new EmailMessage("kari@example.invalid", "Invoice", "<p>Thanks</p>")
        {
            ReplyTo = "ola@example.invalid",
            Attachments = [new EmailAttachment("invoice-0007.pdf", [1, 2, 3]), new EmailAttachment("terms.txt", Encoding.UTF8.GetBytes("ok"))],
        });

        var body = SentBody();
        Assert.Equal("ola@example.invalid", body.GetProperty("replyTo").GetProperty("email").GetString());
        var files = body.GetProperty("attachment").EnumerateArray().ToList();
        Assert.Equal(2, files.Count);
        Assert.Equal(("invoice-0007.pdf", "AQID"), (files[0].GetProperty("name").GetString(), files[0].GetProperty("content").GetString()));
        Assert.Equal(("terms.txt", "b2s="), (files[1].GetProperty("name").GetString(), files[1].GetProperty("content").GetString()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task A_blank_reply_to_is_left_out(string replyTo)
    {
        await Client().SendAsync(new EmailMessage("kari@example.invalid", "S", "B") { ReplyTo = replyTo });

        Assert.False(SentBody().TryGetProperty("replyTo", out _));
    }

    [Theory]
    [InlineData("http://localhost:5952/v3")]
    [InlineData("http://localhost:5952/v3/")]
    public async Task The_api_root_can_be_changed_with_or_without_a_trailing_slash(string baseUrl)
    {
        await Client(baseUrl).SendAsync(new EmailMessage("kari@example.invalid", "S", "B"));

        Assert.Equal("http://localhost:5952/v3/smtp/email", Assert.Single(_brevo.Sent).Request.RequestUri!.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task An_empty_recipient_throws_and_sends_nothing(string to)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Client().SendAsync(new EmailMessage(to, "S", "B")));

        Assert.Empty(_brevo.Sent);
    }

    [Fact]
    public async Task A_refusal_comes_back_as_a_result_and_is_logged()
    {
        _brevo.Respond = _ => Json(HttpStatusCode.BadRequest, """{"code":"invalid_parameter","message":"email is not valid in to"}""");

        var result = await Client().SendAsync(new EmailMessage("not-an-email", "S", "B"));

        Assert.Equal(new EmailResult(false, Error: "invalid_parameter: email is not valid in to"), result);
        var log = Assert.Single(_logger.Collector.GetSnapshot());
        Assert.Equal(LogLevel.Error, log.Level);
        Assert.Contains("status 400", log.Message, StringComparison.Ordinal);
        Assert.Contains("invalid_parameter: email is not valid in to", log.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refusal_with_only_a_message_gives_the_message()
    {
        _brevo.Respond = _ => Json(HttpStatusCode.Unauthorized, """{"message":"Key not found"}""");

        Assert.Equal("Key not found", (await Client().SendAsync(new EmailMessage("kari@example.invalid", "S", "B"))).Error);
    }

    [Theory]
    [InlineData("<html>Bad gateway</html>")]
    [InlineData("{}")]
    [InlineData("")]
    public async Task A_refusal_without_brevo_error_json_gives_the_status(string body)
    {
        _brevo.Respond = _ => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent(body) };

        var result = await Client().SendAsync(new EmailMessage("kari@example.invalid", "S", "B"));

        Assert.Equal(new EmailResult(false, Error: "HTTP 502"), result);
    }

    [Fact]
    public async Task A_failed_connection_comes_back_as_a_result_and_is_logged()
    {
        _brevo.Respond = _ => throw new HttpRequestException("No such host is known.");

        var result = await Client().SendAsync(new EmailMessage("kari@example.invalid", "S", "B"));

        Assert.Equal(new EmailResult(false, Error: "No such host is known."), result);
        Assert.Equal(LogLevel.Error, Assert.Single(_logger.Collector.GetSnapshot()).Level);
    }

    [Fact]
    public async Task A_timeout_comes_back_as_a_result()
    {
        _brevo.Respond = _ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");

        var result = await Client().SendAsync(new EmailMessage("kari@example.invalid", "S", "B"));

        Assert.False(result.Sent);
    }

    [Fact]
    public async Task The_callers_own_cancellation_is_passed_on()
    {
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Client().SendAsync(new EmailMessage("kari@example.invalid", "S", "B"), cancel.Token));
    }

    [Fact]
    public async Task Reads_the_counts_for_the_last_days()
    {
        _brevo.Respond = _ => Json(HttpStatusCode.OK, """
            {"range":"2026-10-07|2026-10-13","requests":120,"delivered":115,"hardBounces":2,"softBounces":1,
             "clicks":9,"opens":40,"spamReports":1,"blocked":3,"invalid":4,"unsubscribed":0}
            """);

        var stats = await Client().GetAsync(7);

        Assert.Equal(new EmailStats(7, 120, 115, 2, 1, 1, 3, 4), stats);
        var (request, _) = Assert.Single(_brevo.Sent);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://api.brevo.com/v3/smtp/statistics/aggregatedReport?days=7", request.RequestUri!.ToString());
        Assert.Equal("xkeysib-test", Assert.Single(request.Headers.GetValues("api-key")));
    }

    [Fact]
    public async Task Counts_default_to_the_last_30_days()
    {
        _brevo.Respond = _ => Json(HttpStatusCode.OK, "{}");

        var stats = await Client().GetAsync();

        Assert.Equal(new EmailStats(30, 0, 0, 0, 0, 0, 0, 0), stats);
        Assert.EndsWith("days=30", _brevo.Sent[0].Request.RequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Counts_that_cannot_be_read_throw()
    {
        _brevo.Respond = _ => Json(HttpStatusCode.Unauthorized, """{"message":"Key not found"}""");

        await Assert.ThrowsAsync<HttpRequestException>(() => Client().GetAsync());
    }

    [Fact]
    public async Task Counts_for_less_than_a_day_throw()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Client().GetAsync(0));
    }

    private static Dictionary<string, string?> Settings(string? apiKey = "k", string? email = "post@example.invalid", string? name = "Garge", string? baseUrl = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["BrevoSettings:ApiKey"] = apiKey,
            ["BrevoSettings:SenderEmail"] = email,
            ["BrevoSettings:SenderName"] = name,
        };
        if (baseUrl is not null)
            settings["BrevoSettings:BaseUrl"] = baseUrl;
        return settings;
    }

    [Fact]
    public void Registration_binds_the_section_and_resolves_both_services()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(Settings(baseUrl: "http://localhost:5952/v3")).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddAppKitBrevo(config.GetSection("BrevoSettings"));
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<BrevoOptions>>().Value;
        Assert.Equal(("k", "post@example.invalid", "Garge", "http://localhost:5952/v3"), (options.ApiKey, options.SenderEmail, options.SenderName, options.BaseUrl));
        Assert.IsType<BrevoClient>(provider.GetRequiredService<IEmailSender>());
        Assert.IsType<BrevoClient>(provider.GetRequiredService<IEmailStats>());
    }

    [Fact]
    public void The_api_root_defaults_to_brevo()
    {
        Assert.Equal("https://api.brevo.com/v3/", new BrevoOptions().BaseUrl);
    }

    [Theory]
    [InlineData(null, "post@example.invalid", "Garge", null)]
    [InlineData("k", " ", "Garge", null)]
    [InlineData("k", "post@example.invalid", "", null)]
    [InlineData("k", "post@example.invalid", "Garge", "api.brevo.com/v3")]
    public async Task Missing_settings_stop_the_app(string? apiKey, string? email, string? name, string? baseUrl)
    {
        using var host = new HostBuilder()
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(Settings(apiKey, email, name, baseUrl)))
            .ConfigureServices((context, s) => s.AddAppKitBrevo(context.Configuration.GetSection("BrevoSettings")))
            .Build();

        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }

    [Fact]
    public async Task A_missing_setting_names_the_section_it_was_read_from()
    {
        using var host = new HostBuilder()
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Email:Brevo:SenderEmail"] = "post@example.invalid",
                ["Email:Brevo:SenderName"] = "Garge",
            }))
            .ConfigureServices((context, s) => s.AddAppKitBrevo(context.Configuration.GetSection("Email:Brevo")))
            .Build();

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Equal(["Email:Brevo:ApiKey is required."], ex.Failures);
    }
}
