using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SjolystInnovation.AppKit.Hosting;

/// <summary>The options for <see cref="RequestLoggingExtensions.UseAppKitRequestLogging"/>.</summary>
public sealed class AppKitRequestLoggingOptions
{
    /// <summary>
    /// The path prefixes that are not logged, matched by whole segments and ignoring case. Each entry starts
    /// with <c>/</c>. Defaults to <c>/health</c>, because probes run every few seconds for the life of the pod.
    /// </summary>
    public ICollection<string> SkippedPaths { get; } = new List<string> { HealthChecksExtensions.LivenessPath };

    /// <summary>
    /// The request headers that are never logged, ignoring case. Defaults to <c>Authorization</c>,
    /// <c>Cookie</c>, <c>Set-Cookie</c>, <c>Proxy-Authorization</c>, <c>X-Api-Key</c>, and <c>Idempotency-Key</c>.
    /// </summary>
    public ISet<string> SensitiveHeaders { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Cookie",
        "Set-Cookie",
        "Proxy-Authorization",
        "X-Api-Key",
        "Idempotency-Key",
    };

    /// <summary>
    /// The parts of header names whose values are redacted, ignoring case. A header whose name contains an
    /// entry is logged with <see cref="LogSanitizer.Redacted"/> as its value, so a custom header such as
    /// <c>X-Vipps-Signature</c> or <c>X-CSRF-Token</c> never reaches the logs. Defaults to <c>token</c>,
    /// <c>auth</c>, <c>secret</c>, <c>session</c>, <c>signature</c>, <c>csrf</c>, <c>xsrf</c>, <c>cookie</c>,
    /// <c>key</c>, and <c>password</c>. A header value that looks like an email address is redacted too.
    /// </summary>
    public ISet<string> SensitiveHeaderParts { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "token",
        "auth",
        "secret",
        "session",
        "signature",
        "csrf",
        "xsrf",
        "cookie",
        "key",
        "password",
    };

    /// <summary>
    /// Whether the query string is logged with the path. Defaults to false, because a query can carry a token,
    /// such as the <c>access_token</c> that SignalR sends.
    /// </summary>
    public bool LogQueryString { get; set; }

    /// <summary>
    /// The parts of query parameter names whose values are redacted when <see cref="LogQueryString"/> is on.
    /// A parameter is redacted when its name contains an entry, ignoring case. Defaults to <c>token</c>,
    /// <c>password</c>, <c>secret</c>, <c>code</c>, <c>key</c>, <c>signature</c>, <c>sig</c>, and <c>email</c>.
    /// </summary>
    public ISet<string> SensitiveQueryParameters { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "token",
        "password",
        "secret",
        "code",
        "key",
        "signature",
        "sig",
        "email",
    };
}

/// <summary>Logs each incoming request without its secrets.</summary>
public static partial class RequestLoggingExtensions
{
    /// <summary>
    /// Logs the method and path of each request at Information, and each request header at Debug. The body is
    /// never read. Headers in <see cref="AppKitRequestLoggingOptions.SensitiveHeaders"/> are left out, the
    /// query string is left out unless <see cref="AppKitRequestLoggingOptions.LogQueryString"/> is on, and
    /// paths under <see cref="AppKitRequestLoggingOptions.SkippedPaths"/> are not logged at all. Path segments
    /// and query values that look like an email address are redacted. Every logged value has its line breaks
    /// removed, so a request cannot forge a log line.
    /// </summary>
    /// <exception cref="ArgumentException">When an entry in <see cref="AppKitRequestLoggingOptions.SkippedPaths"/> does not start with <c>/</c>.</exception>
    public static IApplicationBuilder UseAppKitRequestLogging(this IApplicationBuilder app, Action<AppKitRequestLoggingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        var options = new AppKitRequestLoggingOptions();
        configure?.Invoke(options);
        var skipped = options.SkippedPaths.Select(p => new PathString(p)).ToArray();
        var sensitiveHeaders = new HashSet<string>(options.SensitiveHeaders, StringComparer.OrdinalIgnoreCase);
        var sensitiveQuery = options.SensitiveQueryParameters.ToArray();
        var sensitiveHeaderParts = options.SensitiveHeaderParts.ToArray();
        var logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(RequestLoggingExtensions).FullName!);

        return app.Use(async (context, next) =>
        {
            var request = context.Request;
            if (skipped.Any(p => request.Path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)))
            {
                await next(context);
                return;
            }

            if (logger.IsEnabled(LogLevel.Information))
            {
                var path = LogSanitizer.RedactPath(request.Path.Value);
                object logData = options.LogQueryString
                    ? new { request.Method, Path = path, Query = LogSanitizer.RedactQuery(request.QueryString.Value, sensitiveQuery) }
                    : new { request.Method, Path = path };
                Log.IncomingRequest(logger, logData);
            }

            if (logger.IsEnabled(LogLevel.Debug))
            {
                foreach (var header in request.Headers)
                {
                    if (sensitiveHeaders.Contains(header.Key))
                        continue;
                    var value = header.Value.ToString();
                    var redact = sensitiveHeaderParts.Any(part => header.Key.Contains(part, StringComparison.OrdinalIgnoreCase)) || LogSanitizer.LooksLikeEmail(value);
                    object logData = new { header.Key, Value = redact ? LogSanitizer.Redacted : LogSanitizer.Sanitize(value) };
                    Log.Header(logger, logData);
                }
            }

            await next(context);
        });
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Incoming Request {@LogData}", SkipEnabledCheck = true)]
        public static partial void IncomingRequest(ILogger logger, object logData);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Header {@LogData}", SkipEnabledCheck = true)]
        public static partial void Header(ILogger logger, object logData);
    }
}

/// <summary>Makes values from a request safe to write to a log.</summary>
public static class LogSanitizer
{
    /// <summary>The text that stands in for a redacted value.</summary>
    public const string Redacted = "[Redacted]";

    private static readonly string LineSeparator = char.ConvertFromUtf32(0x2028);
    private static readonly string ParagraphSeparator = char.ConvertFromUtf32(0x2029);

    /// <summary>
    /// Removes carriage returns, line feeds, and the Unicode line and paragraph separators, so a value from a
    /// request cannot start a forged log line.
    /// </summary>
    /// <param name="input">The value to clean.</param>
    /// <returns>The value without line breaks, or null when <paramref name="input"/> is null.</returns>
    [return: NotNullIfNotNull(nameof(input))]
    public static string? Sanitize(string? input) => input?
        .Replace("\r", "", StringComparison.Ordinal)
        .Replace("\n", "", StringComparison.Ordinal)
        .Replace("\u0085", "", StringComparison.Ordinal)
        .Replace(LineSeparator, "", StringComparison.Ordinal)
        .Replace(ParagraphSeparator, "", StringComparison.Ordinal);

    /// <summary>Checks whether the value has the shape of an email address: text, an <c>@</c>, and a domain with a dot.</summary>
    /// <param name="value">The value to check.</param>
    public static bool LooksLikeEmail(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return false;
        var at = value.IndexOf('@', StringComparison.Ordinal);
        return at > 0 && value.IndexOf('.', at + 1) > at + 1 && !value.EndsWith('.');
    }

    // Replaces each path segment that looks like an email address, and removes line breaks.
    internal static string RedactPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return "";
        var segments = path.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            if (LooksLikeEmail(segments[i]))
                segments[i] = Redacted;
        }
        return Sanitize(string.Join('/', segments));
    }

    // Keeps the query's parameter names and order, and replaces the value of each parameter whose name contains
    // a sensitive part or whose value looks like an email address. Line breaks are removed.
    internal static string RedactQuery(string? query, IReadOnlyCollection<string> sensitiveNames)
    {
        if (string.IsNullOrEmpty(query) || query == "?")
            return "";
        var result = new StringBuilder("?");
        foreach (var pair in query.TrimStart('?').Split('&'))
        {
            if (result.Length > 1)
                result.Append('&');
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            var name = equals < 0 ? pair : pair[..equals];
            if (equals < 0)
            {
                result.Append(name);
                continue;
            }
            var decodedName = Uri.UnescapeDataString(name.Replace('+', ' '));
            var decodedValue = Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
            var sensitive = sensitiveNames.Any(s => decodedName.Contains(s, StringComparison.OrdinalIgnoreCase)) || LooksLikeEmail(decodedValue);
            result.Append(name).Append('=').Append(sensitive ? Redacted : pair[(equals + 1)..]);
        }
        return Sanitize(result.ToString());
    }
}
