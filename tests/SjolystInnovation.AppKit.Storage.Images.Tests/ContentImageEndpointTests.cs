using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace SjolystInnovation.AppKit.Storage.Images.Tests;

public class ContentImageEndpointTests
{
    private const string Base = "/api/content-images";

    private static MultipartFormDataContent Form(byte[] file, string name = "photo.png")
    {
        var content = new ByteArrayContent(file);
        // The claimed type is ignored. The type comes from the bytes.
        content.Headers.ContentType = new MediaTypeHeaderValue("image/svg+xml");
        return new MultipartFormDataContent { { content, "file", name } };
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task<string> UploadAsync(TestApp app, byte[] file, string name = "photo.png")
    {
        var response = await app.SendAsync(HttpMethod.Post, Base, Form(file, name));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task An_upload_stores_the_file_the_row_the_dimensions_and_the_renditions()
    {
        await using var app = await TestApp.StartAsync();

        var response = await app.SendAsync(HttpMethod.Post, Base, Form(TestImages.Png(1600, 900), "C:\\fakepath\\Bilde ø.png"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        var id = body.GetProperty("id").GetString()!;
        Assert.Equal(32, id.Length);
        Assert.Equal($"/content-images/{id}", body.GetProperty("url").GetString());
        var image = await app.WithDbAsync(db => db.Set<ContentImage>().Include(i => i.Variants).SingleAsync());
        Assert.Equal("Bilde ø.png", image.FileName);
        Assert.Equal("image/png", image.ContentType);
        Assert.Equal((1600, 900), (image.Width, image.Height));
        Assert.Equal("u1", image.UploadedByUserId);
        Assert.EndsWith(".png", image.StoredPath, StringComparison.Ordinal);
        Assert.Equal([384, 640, 768, 1024, 1366, 1600], image.Variants.Select(v => v.Width).Order());
        Assert.Equal(7, app.Files().Length);
    }

    [Fact]
    public async Task The_public_base_sets_the_url_in_the_answer()
    {
        await using var app = await TestApp.StartAsync(o => o.PublicBase = "/bilder/");

        var response = await app.SendAsync(HttpMethod.Post, Base, Form(TestImages.Png(10, 10)));

        var body = await JsonAsync(response);
        Assert.Equal($"/bilder/{body.GetProperty("id").GetString()}", body.GetProperty("url").GetString());
    }

    [Fact]
    public async Task An_undecodable_upload_is_kept_without_dimensions_or_renditions()
    {
        await using var app = await TestApp.StartAsync();

        await UploadAsync(app, TestImages.BrokenPng());

        var image = await app.WithDbAsync(db => db.Set<ContentImage>().Include(i => i.Variants).SingleAsync());
        Assert.Equal((0, 0), (image.Width, image.Height));
        Assert.Empty(image.Variants);
        Assert.Single(app.Files());
    }

    [Theory]
    [InlineData("pdf")]
    [InlineData("empty")]
    [InlineData("svg")]
    public async Task A_refused_upload_answers_a_validation_problem_and_stores_nothing(string kind)
    {
        await using var app = await TestApp.StartAsync();
        byte[] file = kind switch
        {
            "pdf" => [.. "%PDF-1.4"u8],
            "empty" => [],
            _ => [.. "<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8],
        };
        var expected = kind == "empty" ? "Uploaded file is empty." : "Only PNG, JPEG, WebP, GIF, or AVIF files are accepted.";

        var response = await app.SendAsync(HttpMethod.Post, Base, Form(file));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal(expected, body.GetProperty("title").GetString());
        Assert.Equal(expected, body.GetProperty("errors").GetProperty("file")[0].GetString());
        Assert.Empty(app.Files());
        Assert.Equal(0, await app.WithDbAsync(db => db.Set<ContentImage>().CountAsync()));
    }

    [Fact]
    public async Task An_upload_over_the_limit_is_refused()
    {
        await using var app = await TestApp.StartAsync(storage: new() { ["Storage:MaxImageBytes"] = "1048576" });
        var big = new byte[1024 * 1024 + 1];
        TestImages.Png(1, 1).CopyTo(big, 0);

        var response = await app.SendAsync(HttpMethod.Post, Base, Form(big));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("The file exceeds the 1 MB limit.", (await JsonAsync(response)).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Uploading_and_deleting_require_the_policy()
    {
        await using var app = await TestApp.StartAsync(o => o.Policy = "Owner");
        var id = await app.SeedAsync(TestImages.Png(10, 10), ".png", "image/png");

        Assert.Equal(HttpStatusCode.Unauthorized, (await app.SendAsync(HttpMethod.Post, Base, Form(TestImages.Png(10, 10)), roles: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.SendAsync(HttpMethod.Post, Base, Form(TestImages.Png(10, 10)), roles: "Admin")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.SendAsync(HttpMethod.Delete, $"{Base}/{id}", roles: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.SendAsync(HttpMethod.Delete, $"{Base}/{id}", roles: "Admin")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.SendAsync(HttpMethod.Post, Base, Form(TestImages.Png(10, 10)), roles: "Owner")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await app.SendAsync(HttpMethod.Delete, $"{Base}/{id}", roles: "Owner")).StatusCode);
    }

    [Fact]
    public async Task A_browser_that_accepts_webp_gets_the_smallest_rendition_at_least_as_wide_as_asked()
    {
        await using var app = await TestApp.StartAsync();
        var id = await UploadAsync(app, TestImages.Png(1600, 900));

        var response = await app.SendAsync(HttpMethod.Get, $"{Base}/{id}?w=700", roles: null, accept: "image/avif,image/webp,image/*");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/webp", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("public, max-age=31536000, immutable", response.Headers.CacheControl!.ToString());
        Assert.Equal("Accept", string.Join(",", response.Headers.Vary));
        Assert.Equal("nosniff", string.Join(",", response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal(768, TestImages.SizeOf(await response.Content.ReadAsByteArrayAsync()).Width);
    }

    [Theory]
    [InlineData("", 1600)]
    [InlineData("?w=5000", 1600)]
    [InlineData("?w=1", 384)]
    [InlineData("?w=640", 640)]
    public async Task Without_a_fitting_width_the_largest_rendition_is_served(string query, int width)
    {
        await using var app = await TestApp.StartAsync();
        var id = await UploadAsync(app, TestImages.Png(1600, 900));

        var response = await app.SendAsync(HttpMethod.Get, $"{Base}/{id}{query}", roles: null, accept: "image/webp");

        Assert.Equal(width, TestImages.SizeOf(await response.Content.ReadAsByteArrayAsync()).Width);
    }

    [Fact]
    public async Task Any_other_browser_gets_the_original()
    {
        await using var app = await TestApp.StartAsync();
        var png = TestImages.Png(300, 200);
        var id = await UploadAsync(app, png);

        var response = await app.SendAsync(HttpMethod.Get, $"{Base}/{id}", roles: null, accept: "image/png,image/*");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("public, max-age=31536000, immutable", response.Headers.CacheControl!.ToString());
        Assert.Equal("Accept", string.Join(",", response.Headers.Vary));
        Assert.Equal(png, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task An_unknown_image_or_a_missing_file_answers_not_found()
    {
        await using var app = await TestApp.StartAsync();
        var id = await app.SeedAsync(TestImages.Png(10, 10), ".png", "image/png");
        File.Delete(Path.Combine(app.Root, app.Files()[0]));

        Assert.Equal(HttpStatusCode.NotFound, (await app.SendAsync(HttpMethod.Get, $"{Base}/missing", roles: null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.SendAsync(HttpMethod.Get, $"{Base}/{id}", roles: null)).StatusCode);
    }

    [Fact]
    public async Task An_older_image_gets_its_renditions_on_the_first_webp_request()
    {
        await using var app = await TestApp.StartAsync();
        var id = await app.SeedAsync(TestImages.Jpeg(1000, 500), ".jpg", "image/jpeg");

        var response = await app.SendAsync(HttpMethod.Get, $"{Base}/{id}", roles: null, accept: "image/webp");

        Assert.Equal("image/webp", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("public, max-age=31536000, immutable", response.Headers.CacheControl!.ToString());
        var widths = await app.WithDbAsync(db => db.Set<ContentImageVariant>().Select(v => v.Width).OrderBy(w => w).ToListAsync());
        Assert.Equal([384, 640, 768, 1000], widths);
        Assert.Equal(0, app.Backfill.LockCount);
    }

    [Fact]
    public async Task An_image_that_will_not_decode_serves_the_original_briefly_and_is_not_decoded_again()
    {
        await using var app = await TestApp.StartAsync();
        var broken = TestImages.BrokenPng();
        var id = await app.SeedAsync(broken, ".png", "image/png");

        var first = await app.SendAsync(HttpMethod.Get, $"{Base}/{id}", roles: null, accept: "image/webp");
        var attempts = app.Processor.DecodeAttempts;
        var second = await app.SendAsync(HttpMethod.Get, $"{Base}/{id}", roles: null, accept: "image/webp");

        foreach (var response in new[] { first, second })
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
            Assert.Equal("public, max-age=3600", response.Headers.CacheControl!.ToString());
            Assert.Equal(broken, await response.Content.ReadAsByteArrayAsync());
        }
        Assert.Equal(1, attempts);
        Assert.Equal(1, app.Processor.DecodeAttempts);
        Assert.True(app.Backfill.HasFailed(id));
        Assert.Equal(0, app.Backfill.LockCount);
    }

    [Fact]
    public async Task Concurrent_requests_backfill_an_image_once()
    {
        await using var app = await TestApp.StartAsync();
        var id = await app.SeedAsync(TestImages.Png(800, 400), ".png", "image/png");

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => app.SendAsync(HttpMethod.Get, $"{Base}/{id}", roles: null, accept: "image/webp")));

        Assert.All(responses, r => Assert.Equal("image/webp", r.Content.Headers.ContentType!.MediaType));
        Assert.Equal(1, app.Processor.DecodeAttempts);
        Assert.Equal(4, await app.WithDbAsync(db => db.Set<ContentImageVariant>().CountAsync()));
        Assert.Equal(0, app.Backfill.LockCount);
    }

    [Fact]
    public async Task Dimensions_answers_the_recorded_dimensions_without_decoding()
    {
        await using var app = await TestApp.StartAsync();
        var a = await app.SeedAsync(TestImages.BrokenPng(), ".png", "image/png", 800, 600);
        var b = await app.SeedAsync(TestImages.BrokenPng(), ".png", "image/png", 400, 300);

        var response = await app.SendAsync(HttpMethod.Get, $"{Base}/dimensions?ids={a},{b},{a}", roles: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await JsonAsync(response)).EnumerateArray().ToList();
        Assert.Equal(2, body.Count);
        Assert.Contains(body, e => e.GetProperty("id").GetString() == a && e.GetProperty("width").GetInt32() == 800 && e.GetProperty("height").GetInt32() == 600);
        Assert.Contains(body, e => e.GetProperty("id").GetString() == b && e.GetProperty("width").GetInt32() == 400);
    }

    [Fact]
    public async Task Dimensions_measures_and_keeps_the_dimensions_of_an_older_image()
    {
        await using var app = await TestApp.StartAsync();
        var id = await app.SeedAsync(TestImages.Jpeg(1024, 768, SkiaSharp.SKEncodedOrigin.RightTop), ".jpg", "image/jpeg");

        var body = await JsonAsync(await app.SendAsync(HttpMethod.Get, $"{Base}/dimensions?ids={id}", roles: null));

        Assert.Equal(768, body[0].GetProperty("width").GetInt32());
        Assert.Equal(1024, body[0].GetProperty("height").GetInt32());
        var image = await app.WithDbAsync(db => db.Set<ContentImage>().SingleAsync());
        Assert.Equal((768, 1024), (image.Width, image.Height));
    }

    [Fact]
    public async Task Dimensions_leaves_out_images_it_cannot_measure_and_unknown_ids()
    {
        await using var app = await TestApp.StartAsync();
        var id = await app.SeedAsync(TestImages.BrokenPng(), ".png", "image/png");

        foreach (var query in new[] { $"?ids={id}", "?ids=nope,alsonope", "?ids=", "" })
        {
            var response = await app.SendAsync(HttpMethod.Get, $"{Base}/dimensions{query}", roles: null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(0, (await JsonAsync(response)).GetArrayLength());
        }
    }

    [Fact]
    public async Task Dimensions_reads_at_most_100_ids()
    {
        await using var app = await TestApp.StartAsync();
        var last = await app.SeedAsync(TestImages.BrokenPng(), ".png", "image/png", 10, 10);
        var ids = string.Join(',', Enumerable.Range(0, 100).Select(i => $"id{i}").Append(last));

        var body = await JsonAsync(await app.SendAsync(HttpMethod.Get, $"{Base}/dimensions?ids={ids}", roles: null));

        Assert.Equal(0, body.GetArrayLength());
    }

    [Fact]
    public async Task Delete_removes_the_rows_and_every_file()
    {
        await using var app = await TestApp.StartAsync();
        var keep = await UploadAsync(app, TestImages.Png(500, 500));
        var id = await UploadAsync(app, TestImages.Png(800, 400));
        var kept = app.Files().Length;

        var response = await app.SendAsync(HttpMethod.Delete, $"{Base}/{id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(1, await app.WithDbAsync(db => db.Set<ContentImage>().CountAsync()));
        Assert.Equal(2, await app.WithDbAsync(db => db.Set<ContentImageVariant>().CountAsync()));
        Assert.Equal(kept - 5, app.Files().Length);
        Assert.Equal(HttpStatusCode.NotFound, (await app.SendAsync(HttpMethod.Delete, $"{Base}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.SendAsync(HttpMethod.Get, $"{Base}/{keep}", roles: null)).StatusCode);
    }
}
