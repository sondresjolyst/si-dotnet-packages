using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace SjolystInnovation.AppKit.Pdf.Tests;

public sealed class PdfServiceCollectionTests
{
    [Fact]
    public void AddAppKitPdf_RegistersOneRendererForTheApp()
    {
        using var provider = new ServiceCollection().AddLogging().AddAppKitPdf().BuildServiceProvider();

        var renderer = provider.GetRequiredService<IPdfRenderer>();

        Assert.IsType<ChromiumPdfRenderer>(renderer);
        Assert.Same(renderer, provider.GetRequiredService<IPdfRenderer>());
    }

    [Fact]
    public void AddAppKitPdf_KeepsARendererRegisteredBefore()
    {
        var mine = new StubRenderer();
        using var provider = new ServiceCollection().AddSingleton<IPdfRenderer>(mine).AddAppKitPdf().BuildServiceProvider();

        Assert.Same(mine, provider.GetRequiredService<IPdfRenderer>());
    }

    [Fact]
    public void AddAppKitPdf_DefaultsToASixtySecondTimeout_AndRunsTheCallback()
    {
        using var provider = new ServiceCollection()
            .AddAppKitPdf(o => o.Timeout = TimeSpan.FromSeconds(5))
            .BuildServiceProvider();
        using var defaults = new ServiceCollection().AddAppKitPdf().BuildServiceProvider();

        Assert.Equal(TimeSpan.FromSeconds(5), provider.GetRequiredService<IOptions<PdfRendererOptions>>().Value.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(60), defaults.GetRequiredService<IOptions<PdfRendererOptions>>().Value.Timeout);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AddAppKitPdf_RefusesATimeoutThatIsNotPositive(int seconds)
    {
        using var provider = new ServiceCollection()
            .AddAppKitPdf(o => o.Timeout = TimeSpan.FromSeconds(seconds))
            .BuildServiceProvider();

        var error = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<PdfRendererOptions>>().Value);
        Assert.Contains("PdfRendererOptions.Timeout must be positive.", error.Failures);
    }

    [Fact]
    public void ReadEnvironment_TakesTheBinary_AndTurnsTheSandboxOffOnlyForOne()
    {
        var options = new PdfRendererOptions();
        PdfRendererOptions.ReadEnvironment(options, Env(("PUPPETEER_EXECUTABLE_PATH", "/usr/bin/google-chrome-stable"), ("PUPPETEER_NO_SANDBOX", "1")));

        Assert.Equal("/usr/bin/google-chrome-stable", options.ExecutablePath);
        Assert.True(options.NoSandbox);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData(" 1")]
    public void ReadEnvironment_KeepsTheSandboxOn_UnlessTheValueIsExactlyOne(string? value)
    {
        var options = new PdfRendererOptions();
        PdfRendererOptions.ReadEnvironment(options, Env(("PUPPETEER_NO_SANDBOX", value)));

        Assert.False(options.NoSandbox);
        Assert.Null(options.ExecutablePath);
    }

    [Fact]
    public void ReadEnvironment_IgnoresABlankBinary()
    {
        var options = new PdfRendererOptions();
        PdfRendererOptions.ReadEnvironment(options, Env(("PUPPETEER_EXECUTABLE_PATH", " ")));

        Assert.Null(options.ExecutablePath);
    }

    private static Func<string, string?> Env(params (string Name, string? Value)[] values) =>
        name => values.FirstOrDefault(v => v.Name == name).Value;

    private sealed class StubRenderer : IPdfRenderer
    {
        public Task<byte[]> RenderAsync(string html, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
    }
}
