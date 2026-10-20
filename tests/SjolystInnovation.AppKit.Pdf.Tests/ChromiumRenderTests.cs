using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
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

    // A 1x1 PNG.
    private const string DataImage = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private static void RequireBrowser() =>
        Assert.SkipWhen(string.IsNullOrWhiteSpace(Browser), "PUPPETEER_EXECUTABLE_PATH does not name a browser.");

    private static ServiceProvider Provider(Action<PdfRendererOptions>? configure = null) =>
        new ServiceCollection().AddLogging().AddAppKitPdf(configure).BuildServiceProvider();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Render_MakesAPdf_TwiceWithTheSameBrowser()
    {
        RequireBrowser();
        await using var provider = Provider();
        var renderer = provider.GetRequiredService<IPdfRenderer>();

        var first = await renderer.RenderAsync("<!doctype html><html><body><h1>Ordrebekreftelse</h1><p>Æ Ø Å</p></body></html>", Ct);
        var second = await renderer.RenderAsync("<!doctype html><html><body><p>Two</p></body></html>", Ct);

        Assert.Equal("%PDF", Encoding.ASCII.GetString(first, 0, 4));
        Assert.Equal("%PDF", Encoding.ASCII.GetString(second, 0, 4));
        Assert.True(first.Length > 500);
    }

    [Fact]
    public async Task Render_NeverRequestsARemoteImage()
    {
        RequireBrowser();
        using var server = new RecordingServer();
        await using var provider = Provider();

        var pdf = await provider.GetRequiredService<IPdfRenderer>().RenderAsync(
            $"<html><body><img src=\"{server.Url}/x.png\"><link rel=\"stylesheet\" href=\"{server.Url}/s.css\"></body></html>", Ct);

        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
        await Task.Delay(300, Ct);
        Assert.Empty(server.Paths);
    }

    [Fact]
    public async Task Render_RequestsAnAllowedOrigin()
    {
        RequireBrowser();
        using var server = new RecordingServer();
        await using var provider = Provider(o => o.AllowedOrigins.Add(server.Url));

        await provider.GetRequiredService<IPdfRenderer>().RenderAsync($"<html><body><img src=\"{server.Url}/x.png\"></body></html>", Ct);

        Assert.Contains("/x.png", server.Paths);
    }

    [Fact]
    public async Task Render_ShowsADataImage()
    {
        RequireBrowser();
        await using var provider = Provider();
        var renderer = provider.GetRequiredService<IPdfRenderer>();

        var with = await renderer.RenderAsync($"<html><body><img src=\"{DataImage}\" width=\"200\" height=\"200\"></body></html>", Ct);

        Assert.Contains("/Image", Encoding.Latin1.GetString(with), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Render_DoesNotRunScripts()
    {
        RequireBrowser();
        using var server = new RecordingServer();
        var html = $"<html><body><script>new Image().src = '{server.Url}/js';</script><p>x</p></body></html>";

        await using (var provider = Provider(o => o.AllowedOrigins.Add(server.Url)))
            await provider.GetRequiredService<IPdfRenderer>().RenderAsync(html, Ct);
        await Task.Delay(300, Ct);
        Assert.DoesNotContain("/js", server.Paths);

        // The same page with scripts allowed proves the script would have made the request.
        await using (var provider = Provider(o => { o.AllowedOrigins.Add(server.Url); o.AllowJavaScript = true; }))
            await provider.GetRequiredService<IPdfRenderer>().RenderAsync(html, Ct);
        await Task.Delay(300, Ct);
        Assert.Contains("/js", server.Paths);
    }

    [Fact]
    public async Task Render_DoesNotFollowARedirectToAnOriginThatIsNotAllowed()
    {
        RequireBrowser();
        using var target = new RecordingServer();
        using var redirect = new RecordingServer(location: $"{target.Url}/secret");
        await using var provider = Provider(o => o.AllowedOrigins.Add(redirect.Url));

        await provider.GetRequiredService<IPdfRenderer>().RenderAsync($"<html><body><img src=\"{redirect.Url}/x.png\"></body></html>", Ct);

        Assert.Contains("/x.png", redirect.Paths);
        await Task.Delay(300, Ct);
        Assert.Empty(target.Paths);
    }

    /// <summary>A tiny HTTP server on 127.0.0.1 that records the paths it is asked for.</summary>
    private sealed class RecordingServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly string? _location;

        public ConcurrentBag<string> Paths { get; } = [];

        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

        public RecordingServer(string? location = null)
        {
            _location = location;
            _listener.Start();
            _ = AcceptAsync();
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }
                _ = AnswerAsync(client);
            }
        }

        private async Task AnswerAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var reader = new StreamReader(stream, Encoding.ASCII);
                    var requestLine = await reader.ReadLineAsync(_stop.Token);
                    if (requestLine == null) return;
                    Paths.Add(requestLine.Split(' ')[1]);
                    var answer = _location == null
                        ? "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"
                        : $"HTTP/1.1 302 Found\r\nLocation: {_location}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(answer), _stop.Token);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
                {
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }
    }
}
