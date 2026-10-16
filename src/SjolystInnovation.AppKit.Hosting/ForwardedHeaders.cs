using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SjolystInnovation.AppKit.Hosting;

/// <summary>Configures the forwarded headers middleware for trusted proxies.</summary>
public static partial class ForwardedHeadersExtensions
{
    /// <summary>
    /// Reads the client IP and scheme from <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c>, but only
    /// when the request comes from a network listed in the <paramref name="section"/> configuration
    /// array, and only one hop back. Requests from anywhere else keep their own address. The app fails
    /// to start on an entry that is not a CIDR. With an empty list, the app reads no forwarded header at
    /// all. Outside Development it then logs a warning, because behind an ingress every client would
    /// share the ingress address.
    /// </summary>
    public static IApplicationBuilder UseAppKitForwardedHeaders(this IApplicationBuilder app, string section = "TrustedProxies")
    {
        ArgumentNullException.ThrowIfNull(app);
        var services = app.ApplicationServices;
        var cidrs = services.GetRequiredService<IConfiguration>().GetSection(section).Get<string[]>() ?? [];
        var options = Build(cidrs, section);

        // With no known networks the middleware trusts every caller, so leave it out.
        if (options.KnownIPNetworks.Count == 0)
        {
            if (!services.GetRequiredService<IHostEnvironment>().IsDevelopment())
                Log.NoTrustedProxies(services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ForwardedHeadersExtensions)), section);
            return app;
        }

        return app.UseForwardedHeaders(options);
    }

    internal static ForwardedHeadersOptions Build(IEnumerable<string> cidrs, string section)
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = 1,
        };
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        foreach (var cidr in cidrs)
        {
            if (!System.Net.IPNetwork.TryParse(cidr, out var network))
                throw new InvalidOperationException($"{section} entry '{cidr}' is not a CIDR such as 10.0.0.0/8.");
            options.KnownIPNetworks.Add(network);
        }
        return options;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "{Section} is empty, so X-Forwarded-For is ignored and every client behind a proxy shares its address.")]
        public static partial void NoTrustedProxies(ILogger logger, string section);
    }
}
