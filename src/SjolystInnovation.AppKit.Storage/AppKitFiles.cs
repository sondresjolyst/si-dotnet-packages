using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace SjolystInnovation.AppKit.Storage;

/// <summary>Serves stored files from endpoints.</summary>
public static class AppKitFiles
{
    /// <summary>
    /// Serves a stored file with a <c>Content-Disposition</c> header and <c>X-Content-Type-Options: nosniff</c>.
    /// Any type other than a raster image, PDF included, also gets <c>Content-Security-Policy: sandbox</c>, so a
    /// document shown inline cannot run script on your site's origin.
    /// The header carries the name as a plain ASCII <c>filename</c> and, for any other name, as a UTF-8
    /// <c>filename*</c> too, so quotes, line breaks, and letters such as <c>ø</c> cannot break it.
    /// </summary>
    /// <param name="store">The store that holds the file.</param>
    /// <param name="storedPath">The <see cref="StoredFile.StoredPath"/> of the file.</param>
    /// <param name="downloadName">The file name the browser shows and saves the file as.</param>
    /// <param name="contentType">The content type to send.</param>
    /// <param name="inline">Whether the browser shows the file instead of downloading it. Defaults to <c>true</c>.</param>
    /// <returns>The file, or <c>404</c> when the store has no such file.</returns>
    public static IResult Serve(IAppKitFileStore store, string storedPath, string downloadName, string contentType, bool inline = true)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(contentType);
        if (string.IsNullOrEmpty(storedPath) || !store.Exists(storedPath))
            return TypedResults.NotFound();
        return new ServedFile(store.OpenRead(storedPath), ContentDisposition(downloadName, inline), contentType);
    }

    /// <summary>Builds the <c>Content-Disposition</c> value for a file name.</summary>
    internal static string ContentDisposition(string? downloadName, bool inline)
    {
        // Control characters have no place in a file name, and a line break would end the header.
        var name = string.Concat((downloadName ?? string.Empty).Select(c => char.IsControl(c) ? '_' : c));
        var value = new ContentDispositionHeaderValue(inline ? "inline" : "attachment");
        if (name.Length > 0)
            value.SetHttpFileName(name);
        return value.ToString();
    }

    /// <summary>
    /// Checks whether a content type gets <c>Content-Security-Policy: sandbox</c>. That is every type except
    /// raster images. SVG counts as a document, because it can carry script.
    /// </summary>
    internal static bool NeedsSandbox(string contentType) =>
        !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
        || contentType.StartsWith("image/svg", StringComparison.OrdinalIgnoreCase);

    private sealed class ServedFile(Stream content, string contentDisposition, string contentType) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.ContentDisposition = contentDisposition;
            httpContext.Response.Headers.XContentTypeOptions = "nosniff";
            if (NeedsSandbox(contentType))
                httpContext.Response.Headers.ContentSecurityPolicy = "sandbox";
            return TypedResults.File(content, contentType).ExecuteAsync(httpContext);
        }
    }
}
