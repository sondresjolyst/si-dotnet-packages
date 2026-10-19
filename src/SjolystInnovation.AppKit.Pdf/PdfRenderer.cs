using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PuppeteerSharp;
using PuppeteerSharp.Media;

namespace SjolystInnovation.AppKit.Pdf;

/// <summary>Renders an HTML document to a PDF.</summary>
public interface IPdfRenderer
{
    /// <summary>
    /// Renders <paramref name="html"/> to the bytes of a PDF. A render that takes longer than the
    /// configured timeout throws <see cref="TimeoutException"/>. The caller's cancellation throws
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<byte[]> RenderAsync(string html, CancellationToken cancellationToken = default);
}

/// <summary>
/// Renders through one headless Chromium that the renderer launches on the first call and reuses. Each
/// render gets its own page. A render that fails or times out closes the browser, so the next render
/// launches a fresh one.
/// </summary>
internal sealed partial class ChromiumPdfRenderer : IPdfRenderer, IAsyncDisposable, IDisposable
{
    private readonly PdfRendererOptions _options;
    private readonly ILogger _logger;
    private readonly Func<LaunchOptions, Task<IBrowser>> _launch;
    private readonly SemaphoreSlim _browserLock = new(1, 1);
    private IBrowser? _browser;
    private bool _disposed;

    public ChromiumPdfRenderer(IOptions<PdfRendererOptions> options, ILogger<ChromiumPdfRenderer> logger)
        : this(options.Value, logger, LaunchChromiumAsync)
    {
    }

    internal ChromiumPdfRenderer(PdfRendererOptions options, ILogger logger, Func<LaunchOptions, Task<IBrowser>> launch)
    {
        _options = options;
        _logger = logger;
        _launch = launch;
    }

    public async Task<byte[]> RenderAsync(string html, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(html);
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);
        IBrowser? browser = null;
        try
        {
            browser = await GetBrowserAsync(timeout.Token);
            return await RenderPageAsync(browser, html).WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Log.TimedOut(_logger, _options.Timeout);
            await DiscardAsync(browser);
            throw new TimeoutException($"Rendering the PDF took longer than {_options.Timeout}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Failed(_logger, ex);
            await DiscardAsync(browser);
            throw;
        }
    }

    internal static LaunchOptions BuildLaunchOptions(PdfRendererOptions options)
    {
        var args = new List<string> { "--disable-dev-shm-usage" };
        if (options.NoSandbox)
        {
            args.Add("--no-sandbox");
            args.Add("--disable-setuid-sandbox");
        }

        return new LaunchOptions
        {
            Headless = true,
            ExecutablePath = string.IsNullOrWhiteSpace(options.ExecutablePath) ? null : options.ExecutablePath,
            Args = [.. args],
        };
    }

    internal static PdfOptions BuildPdfOptions() => new()
    {
        Format = PaperFormat.A4,
        PrintBackground = true,
        // The page's own @page rule may set the size and the margins.
        PreferCSSPageSize = true,
    };

    private static async Task<IBrowser> LaunchChromiumAsync(LaunchOptions options)
    {
        if (options.ExecutablePath == null)
            await new BrowserFetcher().DownloadAsync();
        return await Puppeteer.LaunchAsync(options);
    }

    private static async Task<byte[]> RenderPageAsync(IBrowser browser, string html)
    {
        await using var page = await browser.NewPageAsync();
        await page.SetContentAsync(html, new SetContentOptions { WaitUntil = [WaitUntilNavigation.Load] });
        return await page.PdfDataAsync(BuildPdfOptions());
    }

    private async Task<IBrowser> GetBrowserAsync(CancellationToken cancellationToken)
    {
        await _browserLock.WaitAsync(cancellationToken);
        try
        {
            if (_browser is { IsConnected: true, IsClosed: false })
                return _browser;

            var stale = _browser;
            _browser = null;
            if (stale != null) await CloseQuietlyAsync(stale);

            var launching = _launch(BuildLaunchOptions(_options));
            try
            {
                _browser = await launching.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // The launch goes on in the background. Close the browser it brings, if any.
                _ = launching.ContinueWith(t => CloseQuietlyAsync(t.Result), CancellationToken.None,
                    TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default).Unwrap();
                throw;
            }
            return _browser;
        }
        finally
        {
            _browserLock.Release();
        }
    }

    private async Task DiscardAsync(IBrowser? browser)
    {
        if (browser == null) return;
        await _browserLock.WaitAsync(CancellationToken.None);
        try
        {
            if (ReferenceEquals(_browser, browser)) _browser = null;
        }
        finally
        {
            _browserLock.Release();
        }
        await CloseQuietlyAsync(browser);
    }

    private async Task CloseQuietlyAsync(IBrowser browser)
    {
        try
        {
            await browser.DisposeAsync();
        }
        // A browser that is already gone cannot be closed again. That is not the caller's problem.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.CloseFailed(_logger, ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        var browser = _browser;
        _browser = null;
        if (browser != null) await CloseQuietlyAsync(browser);
        _browserLock.Dispose();
    }

    // A container that is disposed synchronously still closes the browser.
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Error, Message = "Rendering a PDF took longer than {Timeout}. The browser is closed.")]
        public static partial void TimedOut(ILogger logger, TimeSpan timeout);

        [LoggerMessage(Level = LogLevel.Error, Message = "Rendering a PDF failed. The browser is closed.")]
        public static partial void Failed(ILogger logger, Exception exception);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Closing the PDF browser failed.")]
        public static partial void CloseFailed(ILogger logger, Exception exception);
    }
}
