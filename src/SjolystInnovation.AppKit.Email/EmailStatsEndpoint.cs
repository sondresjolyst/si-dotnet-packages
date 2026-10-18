using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SjolystInnovation.AppKit.Email;

/// <summary>The options for <see cref="EmailStatsEndpointExtensions.MapAppKitEmailStats"/>.</summary>
public sealed class AppKitEmailStatsEndpointOptions
{
    /// <summary>The authorization policy that the endpoint requires. Defaults to <c>Admin</c>.</summary>
    public string Policy { get; set; } = "Admin";
}

/// <summary>Maps the email statistics endpoint.</summary>
public static partial class EmailStatsEndpointExtensions
{
    /// <summary>
    /// Maps <c>GET</c> on <paramref name="pattern"/>, behind <see cref="AppKitEmailStatsEndpointOptions.Policy"/>.
    /// It answers <see cref="EmailStats"/> from <see cref="IEmailStats"/> for the last <c>days</c> days,
    /// today included, as <c>{ days, requests, delivered, hardBounces, softBounces, spamReports, blocked, invalid }</c>.
    /// The optional <c>days</c> query parameter defaults to 30. A value below 1, or above
    /// <see cref="IEmailStats.MaxDays"/>, answers a validation problem on <c>days</c>. When the provider cannot be
    /// read, the endpoint logs the error and answers <c>502</c> with a problem whose <c>detail</c> is <c>Failed to fetch email stats from Brevo.</c>
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="pattern">The route. Defaults to <c>/api/admin/email-stats</c>.</param>
    /// <param name="configure">A callback that sets the endpoint options.</param>
    /// <returns>The endpoint, for further conventions.</returns>
    /// <exception cref="ArgumentException">When the policy is null or blank.</exception>
    public static RouteHandlerBuilder MapAppKitEmailStats(this IEndpointRouteBuilder endpoints,
        string pattern = "/api/admin/email-stats", Action<AppKitEmailStatsEndpointOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var options = new AppKitEmailStatsEndpointOptions();
        configure?.Invoke(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Policy, nameof(configure));
        var logger = endpoints.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("SjolystInnovation.AppKit.Email.Stats");

        return endpoints.MapGet(pattern, async (HttpContext http, int days = 30) =>
        {
            if (days < 1)
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["days"] = ["Must be at least 1."] });
            var stats = http.RequestServices.GetRequiredService<IEmailStats>();
            if (days > stats.MaxDays)
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["days"] = [$"Must be at most {stats.MaxDays}."] });
            try
            {
                return (IResult)TypedResults.Ok(await stats.GetAsync(days, http.RequestAborted));
            }
            catch (Exception e) when (e is not OperationCanceledException || !http.RequestAborted.IsCancellationRequested)
            {
                StatsFailed(logger, e);
                return TypedResults.Problem("Failed to fetch email stats from Brevo.", statusCode: StatusCodes.Status502BadGateway);
            }
        }).RequireAuthorization(options.Policy);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not read the email stats.")]
    private static partial void StatsFailed(ILogger logger, Exception exception);
}
