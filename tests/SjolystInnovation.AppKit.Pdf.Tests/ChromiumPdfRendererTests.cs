using Microsoft.Extensions.Logging.Testing;
using PuppeteerSharp;
using PuppeteerSharp.Media;
using Xunit;

namespace SjolystInnovation.AppKit.Pdf.Tests;

public sealed class ChromiumPdfRendererTests
{
    // A render that hangs fails the test after this long instead of stalling the run.
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private readonly FakeLogger _logger = new();
    private readonly List<FakeBrowser> _launched = [];
    private readonly List<LaunchOptions> _launchOptions = [];

    private ChromiumPdfRenderer Renderer(TimeSpan? timeout = null, Func<LaunchOptions, Task<IBrowser>>? launch = null) =>
        new(new PdfRendererOptions { Timeout = timeout ?? TimeSpan.FromSeconds(10) }, _logger, launch ?? Launch);

    private Task<IBrowser> Launch(LaunchOptions options)
    {
        _launchOptions.Add(options);
        var browser = new FakeBrowser();
        _launched.Add(browser);
        return Task.FromResult(browser.Browser);
    }

    [Fact]
    public async Task Render_ReturnsThePdf_FromAPageWithTheHtml_AndClosesThePage()
    {
        await using var renderer = Renderer();

        var pdf = await renderer.RenderAsync("<p>Hei</p>");

        Assert.Equal(FakeBrowser.Pdf, pdf);
        var browser = Assert.Single(_launched);
        Assert.Equal(["<p>Hei</p>"], browser.Html);
        Assert.Equal(1, browser.PagesOpened);
        Assert.Equal(1, browser.PagesDisposed);
    }

    [Fact]
    public async Task Render_PrintsA4_WithBackgrounds_AndLetsThePageSetItsSize()
    {
        await using var renderer = Renderer();

        await renderer.RenderAsync("<p>x</p>");

        var options = Assert.Single(_launched[0].PdfOptions);
        Assert.Equal(PaperFormat.A4, options.Format);
        Assert.True(options.PrintBackground);
        Assert.True(options.PreferCSSPageSize);
        var content = Assert.Single(_launched[0].ContentOptions);
        Assert.Equal([WaitUntilNavigation.Load], content!.WaitUntil);
    }

    [Fact]
    public async Task Render_ReusesOneBrowser_AcrossCalls()
    {
        await using var renderer = Renderer();

        await renderer.RenderAsync("<p>1</p>");
        await renderer.RenderAsync("<p>2</p>");

        var browser = Assert.Single(_launched);
        Assert.Equal(2, browser.PagesOpened);
        Assert.Equal(0, browser.Disposed);
    }

    [Fact]
    public async Task Render_CalledAtOnce_LaunchesOneBrowser()
    {
        var gate = new TaskCompletionSource();
        await using var renderer = Renderer(launch: async o =>
        {
            await gate.Task;
            return await Launch(o);
        });

        var renders = Enumerable.Range(0, 5).Select(i => renderer.RenderAsync($"<p>{i}</p>")).ToList();
        gate.SetResult();
        await Task.WhenAll(renders).WaitAsync(Bound);

        Assert.Equal(5, Assert.Single(_launched).PagesOpened);
    }

    [Fact]
    public async Task Render_AfterTheBrowserDisconnected_LaunchesAnother_AndClosesTheOldOne()
    {
        await using var renderer = Renderer();
        await renderer.RenderAsync("<p>1</p>");
        _launched[0].Connected = false;

        await renderer.RenderAsync("<p>2</p>");

        Assert.Equal(2, _launched.Count);
        Assert.Equal(1, _launched[0].Disposed);
        Assert.Equal(["<p>2</p>"], _launched[1].Html);
    }

    [Fact]
    public async Task Render_ThatFails_ClosesTheBrowser_AndTheNextRenderLaunchesAnother()
    {
        await using var renderer = Renderer();
        await renderer.RenderAsync("<p>1</p>");
        _launched[0].OnSetContent = () => throw new TargetClosedException("gone", "crash");

        await Assert.ThrowsAsync<TargetClosedException>(() => renderer.RenderAsync("<p>2</p>").WaitAsync(Bound));
        Assert.Equal(1, _launched[0].Disposed);

        Assert.Equal(FakeBrowser.Pdf, await renderer.RenderAsync("<p>3</p>"));
        Assert.Equal(2, _launched.Count);
        Assert.Equal(1, _launched[0].Disposed);
        Assert.Contains(_logger.Collector.GetSnapshot(), r => r.Message == "Rendering a PDF failed. The browser is closed.");
    }

    [Fact]
    public async Task Render_ThatHangs_ThrowsTimeoutException_ClosesTheBrowser_AndTheNextRenderLaunchesAnother()
    {
        await using var renderer = Renderer(TimeSpan.FromMilliseconds(200));
        await renderer.RenderAsync("<p>1</p>");
        _launched[0].OnSetContent = () => new TaskCompletionSource().Task;

        var error = await Assert.ThrowsAsync<TimeoutException>(() => renderer.RenderAsync("<p>2</p>").WaitAsync(Bound));

        Assert.Contains("00:00:00.2", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, _launched[0].Disposed);
        Assert.Equal(FakeBrowser.Pdf, await renderer.RenderAsync("<p>3</p>"));
        Assert.Equal(2, _launched.Count);
    }

    [Fact]
    public async Task Render_WhoseLaunchHangs_ThrowsTimeoutException_AndClosesTheBrowserWhenItArrives()
    {
        var launch = new TaskCompletionSource<IBrowser>();
        await using var renderer = Renderer(TimeSpan.FromMilliseconds(200), _ => launch.Task);

        await Assert.ThrowsAsync<TimeoutException>(() => renderer.RenderAsync("<p>x</p>").WaitAsync(Bound));

        var late = new FakeBrowser();
        launch.SetResult(late.Browser);
        for (var i = 0; i < 50 && late.Disposed == 0; i++) await Task.Delay(20);
        Assert.Equal(1, late.Disposed);
    }

    [Fact]
    public async Task Render_CancelledByTheCaller_ThrowsOperationCanceled_AndKeepsTheBrowser()
    {
        await using var renderer = Renderer();
        await renderer.RenderAsync("<p>1</p>");
        _launched[0].OnSetContent = () => new TaskCompletionSource().Task;
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => renderer.RenderAsync("<p>2</p>", cancel.Token).WaitAsync(Bound));

        Assert.Equal(0, _launched[0].Disposed);
    }

    [Fact]
    public async Task Render_RefusesNullHtml()
    {
        await using var renderer = Renderer();

        await Assert.ThrowsAsync<ArgumentNullException>(() => renderer.RenderAsync(null!));
        Assert.Empty(_launched);
    }

    [Fact]
    public async Task Dispose_ClosesTheBrowser_AndLaterRendersThrow()
    {
        var renderer = Renderer();
        await renderer.RenderAsync("<p>1</p>");

        await renderer.DisposeAsync();
        await renderer.DisposeAsync();

        Assert.Equal(1, _launched[0].Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => renderer.RenderAsync("<p>2</p>").WaitAsync(Bound));
    }

    [Fact]
    public void LaunchOptions_KeepTheSandboxOnByDefault()
    {
        var options = ChromiumPdfRenderer.BuildLaunchOptions(new PdfRendererOptions());

        Assert.True(options.Headless);
        Assert.Null(options.ExecutablePath);
        Assert.Equal(["--disable-dev-shm-usage"], options.Args);
    }

    [Fact]
    public void LaunchOptions_WithoutTheSandbox_AddTheNoSandboxFlags_AndUseTheGivenBinary()
    {
        var options = ChromiumPdfRenderer.BuildLaunchOptions(new PdfRendererOptions { NoSandbox = true, ExecutablePath = "/usr/bin/chromium" });

        Assert.Equal("/usr/bin/chromium", options.ExecutablePath);
        Assert.Equal(["--disable-dev-shm-usage", "--no-sandbox", "--disable-setuid-sandbox"], options.Args);
    }

    [Fact]
    public void LaunchOptions_TreatABlankBinaryAsNone()
    {
        Assert.Null(ChromiumPdfRenderer.BuildLaunchOptions(new PdfRendererOptions { ExecutablePath = "  " }).ExecutablePath);
    }
}
