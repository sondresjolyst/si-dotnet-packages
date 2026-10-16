using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace SjolystInnovation.AppKit.Hosting;

/// <summary>Registers and maps the liveness and readiness health checks.</summary>
public static class HealthChecksExtensions
{
    internal const string LivenessPath = "/health";
    internal const string ReadinessPath = "/health/ready";
    internal const string ReadyTag = "ready";

    /// <summary>
    /// Registers health checks with a check that <typeparamref name="TContext"/> can reach its database,
    /// tagged for <see cref="MapAppKitHealthChecks"/> to report on <c>/health/ready</c>. Add more
    /// readiness checks to the returned builder with the tag <c>"ready"</c>.
    /// </summary>
    public static IHealthChecksBuilder AddAppKitHealthChecks<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddHealthChecks().AddDbContextCheck<TContext>("db", tags: [ReadyTag]);
    }

    /// <summary>
    /// Maps <c>/health</c> and <c>/health/ready</c>. <c>/health</c> runs no checks, so a database outage
    /// does not restart the pod. <c>/health/ready</c> fails while a check tagged <c>"ready"</c> fails,
    /// which takes the pod out of the Service. Both allow anonymous callers.
    /// </summary>
    public static IEndpointRouteBuilder MapAppKitHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapHealthChecks(LivenessPath, new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
        endpoints.MapHealthChecks(ReadinessPath, new HealthCheckOptions { Predicate = c => c.Tags.Contains(ReadyTag) }).AllowAnonymous();
        return endpoints;
    }
}
