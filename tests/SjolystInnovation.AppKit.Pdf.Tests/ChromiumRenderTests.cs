using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SjolystInnovation.AppKit.Pdf.Tests;

/// <summary>
/// Renders through a real Chrome or Chromium. The tests run only when <c>PUPPETEER_EXECUTABLE_PATH</c>
/// names a browser, as it does in CI, so a local run never downloads one.
/// </summary>
public sealed class ChromiumRenderTests
{
    private static readonly string? Browser = Environment.GetEnvironmentVariable(PdfRendererOptions.ExecutablePathVariable);

    [Fact]
    public async Task Render_MakesAPdf_TwiceWithTheSameBrowser()
    {
        Assert.SkipWhen(string.IsNullOrWhiteSpace(Browser), "PUPPETEER_EXECUTABLE_PATH does not name a browser.");
        await using var provider = new ServiceCollection().AddLogging().AddAppKitPdf().BuildServiceProvider();
        var renderer = provider.GetRequiredService<IPdfRenderer>();

        var first = await renderer.RenderAsync("<!doctype html><html><body><h1>Ordrebekreftelse</h1><p>Æ Ø Å</p></body></html>", TestContext.Current.CancellationToken);
        var second = await renderer.RenderAsync("<!doctype html><html><body><p>Two</p></body></html>", TestContext.Current.CancellationToken);

        Assert.Equal("%PDF", Encoding.ASCII.GetString(first, 0, 4));
        Assert.Equal("%PDF", Encoding.ASCII.GetString(second, 0, 4));
        Assert.True(first.Length > 500);
    }
}
