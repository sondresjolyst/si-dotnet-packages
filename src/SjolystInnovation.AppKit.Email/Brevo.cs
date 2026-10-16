using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SjolystInnovation.AppKit.Email;

/// <summary>The Brevo options, bound from the app's configuration section.</summary>
public sealed class BrevoOptions
{
    /// <summary>The API key, sent as the <c>api-key</c> header.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>The From address. It must be a known sender in Brevo.</summary>
    public string SenderEmail { get; set; } = string.Empty;

    /// <summary>The From name.</summary>
    public string SenderName { get; set; } = string.Empty;

    /// <summary>The API root URL. Defaults to Brevo's own, <c>https://api.brevo.com/v3/</c>.</summary>
    public string BaseUrl { get; set; } = "https://api.brevo.com/v3/";
}

/// <summary>Registers the Brevo email services.</summary>
public static class BrevoServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IEmailSender"/> and <see cref="IEmailStats"/> on Brevo's API with their own
    /// <see cref="HttpClient"/>, and <see cref="BrevoOptions"/> bound from <paramref name="section"/>. The
    /// app fails to start without an API key, a sender address, a sender name, and an absolute API root.
    /// </summary>
    public static IHttpClientBuilder AddAppKitBrevo(this IServiceCollection services, IConfigurationSection section)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(section);
        services.AddOptions<BrevoOptions>()
            .Bind(section)
            .Validate(o => !string.IsNullOrWhiteSpace(o.ApiKey), "BrevoSettings:ApiKey is required.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.SenderEmail), "BrevoSettings:SenderEmail is required.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.SenderName), "BrevoSettings:SenderName is required.")
            .Validate(o => Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out _), "BrevoSettings:BaseUrl must be an absolute URL.")
            .ValidateOnStart();
        services.TryAddTransient<IEmailSender>(sp => sp.GetRequiredService<BrevoClient>());
        services.TryAddTransient<IEmailStats>(sp => sp.GetRequiredService<BrevoClient>());
        return services.AddHttpClient<BrevoClient>();
    }
}

internal sealed partial class BrevoClient(HttpClient http, IOptions<BrevoOptions> options, ILogger<BrevoClient> logger) : IEmailSender, IEmailStats
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly BrevoOptions _options = options.Value;

    public async Task<EmailResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (string.IsNullOrWhiteSpace(message.To))
            throw new ArgumentException("The recipient address is empty.", nameof(message));

        var body = new SendRequest(
            new Address(_options.SenderEmail, _options.SenderName),
            [new Address(message.To, null)],
            message.Subject,
            message.HtmlBody,
            string.IsNullOrWhiteSpace(message.ReplyTo) ? null : new Address(message.ReplyTo, null),
            message.Attachments.Count == 0 ? null : message.Attachments.Select(a => new Attachment(a.Name, Convert.ToBase64String(a.Content))).ToList());

        try
        {
            using var request = Request(HttpMethod.Post, "smtp/email");
            request.Content = JsonContent.Create(body, options: Json);
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var sent = await response.Content.ReadFromJsonAsync<SendResponse>(Json, cancellationToken);
                return new EmailResult(true, sent?.MessageId);
            }

            var error = await ReadErrorAsync(response, cancellationToken);
            Log.Refused(logger, (int)response.StatusCode, error);
            return new EmailResult(false, Error: error);
        }
        // Report a connection that fails or times out like a refusal, so an email never breaks the
        // request that sends it. Only the caller's own cancellation passes through.
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            Log.Failed(logger, ex);
            return new EmailResult(false, Error: ex.Message);
        }
    }

    public async Task<EmailStats> GetAsync(int days = 30, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(days, 1);
        using var request = Request(HttpMethod.Get, $"smtp/statistics/aggregatedReport?days={days}");
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var report = await response.Content.ReadFromJsonAsync<AggregatedReport>(Json, cancellationToken) ?? new AggregatedReport();
        return new EmailStats(days, report.Requests, report.Delivered, report.HardBounces, report.SoftBounces,
            report.SpamReports, report.Blocked, report.Invalid);
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var root = _options.BaseUrl.EndsWith('/') ? _options.BaseUrl : _options.BaseUrl + "/";
        var request = new HttpRequestMessage(method, new Uri(new Uri(root), path));
        request.Headers.Add("api-key", _options.ApiKey);
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(Json, cancellationToken);
            if (!string.IsNullOrEmpty(error?.Message))
                return string.IsNullOrEmpty(error.Code) ? error.Message : $"{error.Code}: {error.Message}";
        }
        // If the body is not Brevo's error JSON, the status code is the reason.
        catch (JsonException)
        {
        }
        return $"HTTP {(int)response.StatusCode}";
    }

    private sealed record Address(string Email, string? Name);
    private sealed record Attachment(string Name, string Content);
    private sealed record SendRequest(Address Sender, IReadOnlyList<Address> To, string Subject, string HtmlContent, Address? ReplyTo,
        [property: JsonPropertyName("attachment")] IReadOnlyList<Attachment>? Attachments);
    private sealed record SendResponse(string? MessageId);
    private sealed record ErrorResponse(string? Code, string? Message);

    private sealed class AggregatedReport
    {
        public int Requests { get; set; }
        public int Delivered { get; set; }
        public int HardBounces { get; set; }
        public int SoftBounces { get; set; }
        public int SpamReports { get; set; }
        public int Blocked { get; set; }
        public int Invalid { get; set; }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Error, Message = "Brevo refused an email with status {Status}: {Error}")]
        public static partial void Refused(ILogger logger, int status, string error);

        [LoggerMessage(Level = LogLevel.Error, Message = "Sending an email through Brevo failed.")]
        public static partial void Failed(ILogger logger, Exception exception);
    }
}
