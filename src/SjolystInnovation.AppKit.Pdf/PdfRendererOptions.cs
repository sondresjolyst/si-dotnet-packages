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

    internal static void ReadEnvironment(PdfRendererOptions options, Func<string, string?> variable)
    {
        var path = variable(ExecutablePathVariable);
        if (!string.IsNullOrWhiteSpace(path)) options.ExecutablePath = path;
        options.NoSandbox = string.Equals(variable(NoSandboxVariable), "1", StringComparison.Ordinal);
    }
}
