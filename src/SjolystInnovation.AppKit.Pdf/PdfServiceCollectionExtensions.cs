using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SjolystInnovation.AppKit.Pdf;

/// <summary>Registers the PDF renderer.</summary>
public static class PdfServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IPdfRenderer"/> as a singleton on headless Chromium, with
    /// <see cref="PdfRendererOptions"/> read from the <c>PUPPETEER_EXECUTABLE_PATH</c> and
    /// <c>PUPPETEER_NO_SANDBOX</c> environment variables. <paramref name="configure"/> runs after them. A
    /// renderer you register before this call is kept. The app fails to start when the timeout is not
    /// positive.
    /// </summary>
    public static IServiceCollection AddAppKitPdf(this IServiceCollection services, Action<PdfRendererOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = services.AddOptions<PdfRendererOptions>()
            .Configure(o => PdfRendererOptions.ReadEnvironment(o, Environment.GetEnvironmentVariable));
        if (configure != null) options.Configure(configure);
        options.Validate(o => o.Timeout > TimeSpan.Zero, "PdfRendererOptions.Timeout must be positive.").ValidateOnStart();
        services.TryAddSingleton<IPdfRenderer, ChromiumPdfRenderer>();
        return services;
    }
}
