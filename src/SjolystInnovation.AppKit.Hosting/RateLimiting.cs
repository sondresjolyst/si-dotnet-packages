using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SjolystInnovation.AppKit.Hosting;

/// <summary>One limit: at most <see cref="Limit"/> requests per client IP in each <see cref="Period"/>.</summary>
public sealed class RateLimitRule
{
    /// <summary>
    /// <c>VERB:/path</c>, where the verb is an HTTP method or <c>*</c> for any, and <c>*</c> in the path
    /// matches any run of characters. The whole path must match, ignoring case.
    /// </summary>
    public string Endpoint { get; set; } = "";

    /// <summary>A whole number with a unit: <c>s</c>, <c>m</c>, <c>h</c> or <c>d</c>, such as <c>1m</c>.</summary>
    public string Period { get; set; } = "";

    public int Limit { get; set; }
}

public sealed class AppKitRateLimitOptions
{
    public IList<RateLimitRule> Rules { get; init; } = [];
}

public static partial class RateLimitingExtensions
{
    /// <summary>
    /// Registers the ASP.NET Core rate limiter with the rules in <paramref name="section"/>. Each rule
    /// keeps a fixed window per client IP, read only from the connection, so behind a proxy it relies on
    /// <see cref="ForwardedHeadersExtensions.UseAppKitForwardedHeaders"/>. A request matching several
    /// rules counts against each. A rejected request gets 429 with <c>Retry-After</c> and a problem
    /// details body. The app fails to start on a rule it cannot read.
    /// </summary>
    public static IServiceCollection AddAppKitRateLimiting(this IServiceCollection services, IConfigurationSection section)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(section);
        services.AddOptions<AppKitRateLimitOptions>()
            .Bind(section)
            .Validate(o => o.Rules.All(r => CompiledRule.TryCompile(r, out _)),
                "Every rate limit rule needs an endpoint like '*:/api/auth/login', a period like '1m' and a positive limit.")
            .ValidateOnStart();
        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<AppKitRateLimitOptions>>((options, kit) =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = BuildLimiter(kit.Value.Rules.Select(CompiledRule.Compile).ToList());
            options.OnRejected = RejectAsync;
        });
        return services;
    }

    /// <summary>Applies the limits. Call it after <c>UseCors</c>, so preflight requests are not counted.</summary>
    public static IApplicationBuilder UseAppKitRateLimiting(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseRateLimiter();
    }

    internal static PartitionedRateLimiter<HttpContext> BuildLimiter(IReadOnlyList<CompiledRule> rules)
    {
        if (rules.Count == 0)
            return PartitionedRateLimiter.Create<HttpContext, string>(_ => RateLimitPartition.GetNoLimiter(""));

        var limiters = rules.Select(rule => PartitionedRateLimiter.Create<HttpContext, string>(context =>
            rule.Matches(context.Request)
                ? RateLimitPartition.GetFixedWindowLimiter(ClientKey(context), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = rule.Limit,
                    Window = rule.Period,
                    QueueLimit = 0,
                    AutoReplenishment = true,
                })
                : RateLimitPartition.GetNoLimiter(""))).ToArray();
        return limiters.Length == 1 ? limiters[0] : PartitionedRateLimiter.CreateChained(limiters);
    }

    /// <summary>The connection's address, with an IPv4 caller on a dual-stack socket written as IPv4.</summary>
    internal static string ClientKey(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address is null)
            return "unknown";
        return (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
    }

    private static async ValueTask RejectAsync(OnRejectedContext rejected, CancellationToken cancellationToken)
    {
        var http = rejected.HttpContext;
        if (rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            http.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

        Log.Rejected(http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(RateLimitingExtensions)),
            http.Request.Method, http.Request.Path);

        // No detail, so a client shows its own message for 429 in its own language.
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title = "Too many requests",
        };
        var writer = http.RequestServices.GetService<IProblemDetailsService>();
        if (writer is null || !await writer.TryWriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = problem }))
            await http.Response.WriteAsJsonAsync(problem, cancellationToken);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Rate limit reached for {Method} {Path}.")]
        public static partial void Rejected(ILogger logger, string method, string path);
    }
}

internal sealed partial class CompiledRule
{
    private readonly string? _verb;
    private readonly Regex _path;

    private CompiledRule(string? verb, Regex path, TimeSpan period, int limit)
    {
        _verb = verb;
        _path = path;
        Period = period;
        Limit = limit;
    }

    public TimeSpan Period { get; }
    public int Limit { get; }

    public bool Matches(HttpRequest request) =>
        (_verb is null || string.Equals(_verb, request.Method, StringComparison.OrdinalIgnoreCase))
        && _path.IsMatch(request.Path.Value ?? "");

    public static CompiledRule Compile(RateLimitRule rule) =>
        TryCompile(rule, out var compiled) ? compiled : throw new InvalidOperationException($"Rate limit rule '{rule.Endpoint}' is invalid.");

    public static bool TryCompile(RateLimitRule rule, out CompiledRule compiled)
    {
        compiled = null!;
        var separator = rule.Endpoint.IndexOf(':', StringComparison.Ordinal);
        if (separator < 1 || rule.Limit < 1 || !TryParsePeriod(rule.Period, out var period))
            return false;

        var verb = rule.Endpoint[..separator].Trim();
        var path = rule.Endpoint[(separator + 1)..].Trim();
        if (!path.StartsWith('/') || (verb != "*" && !VerbPattern().IsMatch(verb)))
            return false;

        var pattern = "^" + Regex.Escape(path).Replace(@"\*", ".*", StringComparison.Ordinal) + "$";
        compiled = new CompiledRule(verb == "*" ? null : verb, new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), period, rule.Limit);
        return true;
    }

    internal static bool TryParsePeriod(string text, out TimeSpan period)
    {
        period = TimeSpan.Zero;
        var match = PeriodPattern().Match(text ?? "");
        if (!match.Success || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var amount) || amount < 1)
            return false;
        period = match.Groups[2].Value switch
        {
            "s" => TimeSpan.FromSeconds(amount),
            "m" => TimeSpan.FromMinutes(amount),
            "h" => TimeSpan.FromHours(amount),
            _ => TimeSpan.FromDays(amount),
        };
        return true;
    }

    [GeneratedRegex("^([0-9]+)([smhd])$")]
    private static partial Regex PeriodPattern();

    [GeneratedRegex("^[A-Za-z]+$")]
    private static partial Regex VerbPattern();
}
