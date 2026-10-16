using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Net.Http.Headers;
using Xunit;

namespace SjolystInnovation.AppKit.Storage.Tests;

public sealed class AppKitFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"appkit-files-{Guid.NewGuid():N}");
    private readonly LocalFileStore _store;

    public AppKitFilesTests()
    {
        _store = new LocalFileStore(new AppKitFileStoreOptions { Path = _root, Allowed = AppKitFileKinds.Pdf }, NullLogger<LocalFileStore>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static async Task<HttpContext> ExecuteAsync(IResult result)
    {
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        http.Response.Body = new MemoryStream();
        await result.ExecuteAsync(http);
        return http;
    }

    [Fact]
    public void An_ascii_name_is_sent_as_is()
    {
        var value = ContentDispositionHeaderValue.Parse(AppKitFiles.ContentDisposition("report.pdf", inline: true));

        Assert.Equal("inline", value.DispositionType.Value);
        Assert.Equal("report.pdf", value.FileName.Value);
    }

    [Fact]
    public void A_name_with_letters_outside_ascii_gets_an_encoded_filename_star()
    {
        var header = AppKitFiles.ContentDisposition("Måling på bil ø.pdf", inline: true);
        var value = ContentDispositionHeaderValue.Parse(header);

        Assert.True(header.All(char.IsAscii));
        Assert.Contains("filename*=UTF-8''M%C3%A5ling", header, StringComparison.Ordinal);
        Assert.Equal("Måling på bil ø.pdf", value.FileNameStar.Value);
        Assert.Equal("M_ling p_ bil _.pdf", value.FileName.Value);
    }

    [Fact]
    public void Quotes_and_line_breaks_cannot_break_the_header()
    {
        var header = AppKitFiles.ContentDisposition("evil\".pdf\r\nSet-Cookie: a=b", inline: false);
        var value = ContentDispositionHeaderValue.Parse(header);

        Assert.DoesNotContain('\r', header);
        Assert.DoesNotContain('\n', header);
        Assert.Equal("attachment", value.DispositionType.Value);
        Assert.Equal("evil\".pdf__Set-Cookie: a=b", HeaderUtilities.UnescapeAsQuotedString(value.FileName).Value);
    }

    [Fact]
    public void A_blank_name_sends_only_the_disposition()
    {
        Assert.Equal("attachment", AppKitFiles.ContentDisposition("", inline: false));
    }

    [Fact]
    public async Task Serve_sends_the_file_with_its_type_name_and_nosniff()
    {
        var stored = await _store.SaveAsync(new MemoryStream([.. "%PDF-1.4 data"u8]), "dyno.pdf");

        var http = await ExecuteAsync(await AppKitFiles.ServeAsync(_store, stored.StoredPath, "Dyno ø.pdf", "application/pdf"));

        Assert.Equal(200, http.Response.StatusCode);
        Assert.Equal("application/pdf", http.Response.ContentType);
        Assert.Equal("nosniff", http.Response.Headers.XContentTypeOptions.ToString());
        var disposition = ContentDispositionHeaderValue.Parse(http.Response.Headers.ContentDisposition.ToString());
        Assert.Equal("inline", disposition.DispositionType.Value);
        Assert.Equal("Dyno ø.pdf", disposition.FileNameStar.Value);
        Assert.Equal("%PDF-1.4 data"u8.ToArray(), ((MemoryStream)http.Response.Body).ToArray());
    }

    [Theory]
    [InlineData("application/pdf", true, true)]
    [InlineData("application/pdf", false, true)]
    [InlineData("text/html", true, true)]
    [InlineData("image/svg+xml", true, true)]
    [InlineData("image/png", true, false)]
    [InlineData("image/webp", false, false)]
    public async Task Serve_sandboxes_every_type_but_a_raster_image(string contentType, bool inline, bool sandboxed)
    {
        var stored = await _store.SaveAsync(new MemoryStream([.. "%PDF-1.4 data"u8]), "dyno.pdf");

        var http = await ExecuteAsync(await AppKitFiles.ServeAsync(_store, stored.StoredPath, "dyno.pdf", contentType, inline));

        Assert.Equal(sandboxed ? "sandbox" : "", http.Response.Headers.ContentSecurityPolicy.ToString());
        Assert.StartsWith(inline ? "inline" : "attachment", http.Response.Headers.ContentDisposition.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Serve_answers_not_found_for_a_missing_file()
    {
        Assert.IsType<NotFound>(await AppKitFiles.ServeAsync(_store, "0123.pdf", "x.pdf", "application/pdf"));
        Assert.IsType<NotFound>(await AppKitFiles.ServeAsync(_store, "", "x.pdf", "application/pdf"));
    }
}
