using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace SjolystInnovation.AppKit.Hosting;

/// <summary>Adds security headers and HTTPS enforcement.</summary>
public static class SecurityAndHttpsExtensions
{
    /// <summary>
    /// Adds <c>X-Content-Type-Options: nosniff</c> and <c>X-Frame-Options: DENY</c> to every response,
    /// including error responses.
    /// </summary>
    public static IApplicationBuilder UseAppKitSecurityHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                context.Response.Headers.XContentTypeOptions = "nosniff";
                context.Response.Headers.XFrameOptions = "DENY";
                return Task.CompletedTask;
            });
            await next(context);
        });
    }

    /// <summary>
    /// Sends HSTS and redirects plain HTTP to HTTPS outside Development. Requests under <c>/health</c>
    /// are never redirected, because probes reach the pod over plain HTTP without
    /// <c>X-Forwarded-Proto</c>. Call it after <see cref="ForwardedHeadersExtensions.UseAppKitForwardedHeaders"/>,
    /// so the scheme the ingress saw counts.
    /// </summary>
    public static IApplicationBuilder UseAppKitHttps(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.ApplicationServices.GetRequiredService<IHostEnvironment>().IsDevelopment())
            return app;

        app.UseHsts();
        return app.UseWhen(
            context => !context.Request.Path.StartsWithSegments(HealthChecksExtensions.LivenessPath),
            branch => branch.UseHttpsRedirection());
    }
}
