namespace SjolystInnovation.AppKit.Pdf;

/// <summary>The options of the Chromium PDF renderer.</summary>
public sealed class PdfRendererOptions
{
    /// <summary>The environment variable that sets <see cref="ExecutablePath"/>.</summary>
    public const string ExecutablePathVariable = "PUPPETEER_EXECUTABLE_PATH";

    /// <summary>The environment variable that turns the sandbox off when it is exactly <c>1</c>.</summary>
    public const string NoSandboxVariable = "PUPPETEER_NO_SANDBOX";

    /// <summary>
    /// The Chrome or Chromium binary to launch. Defaults to <c>PUPPETEER_EXECUTABLE_PATH</c>. When blank,
    /// PuppeteerSharp downloads a Chromium build on the first render and launches that.
    /// </summary>
    public string? ExecutablePath { get; set; }

    /// <summary>
    /// When true, Chromium runs without its sandbox. Defaults to true only when <c>PUPPETEER_NO_SANDBOX</c>
    /// is <c>1</c>. Keep the sandbox on and run the app as a non-root user. Turn it off only where the
    /// environment already isolates the process.
    /// </summary>
    public bool NoSandbox { get; set; }

    /// <summary>
    /// How long one render may take, launching the browser included, before it fails with a
    /// <see cref="TimeoutException"/>. Defaults to 60 seconds.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// When true, scripts in the HTML run while the page renders. Defaults to false. Turn it on only for
    /// HTML your app fully controls.
    /// </summary>
    public bool AllowJavaScript { get; set; }

    /// <summary>
    /// The origins the page may load assets from, such as <c>https://cdn.example.com</c>. A request is
    /// allowed only when its scheme, host and port match one of them exactly, and each redirect is checked
    /// the same way. Empty by default, so only <c>data:</c> URLs and <c>about:blank</c> load.
    /// </summary>
    public IList<string> AllowedOrigins { get; } = [];

    internal static bool IsValidOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
        && string.IsNullOrEmpty(uri.UserInfo);

    internal static void ReadEnvironment(PdfRendererOptions options, Func<string, string?> variable)
    {
        var path = variable(ExecutablePathVariable);
        if (!string.IsNullOrWhiteSpace(path)) options.ExecutablePath = path;
        options.NoSandbox = string.Equals(variable(NoSandboxVariable), "1", StringComparison.Ordinal);
    }
}
