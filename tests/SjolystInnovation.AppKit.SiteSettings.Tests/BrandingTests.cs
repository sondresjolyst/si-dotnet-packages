using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Xunit;

namespace SjolystInnovation.AppKit.SiteSettings.Tests;

public class BrandingTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 16];
    private static readonly byte[] Webp = [.. "RIFF"u8, 0x24, 0, 0, 0, .. "WEBPVP8 "u8];
    private static readonly byte[] Gif = [.. "GIF89a"u8, 1, 0, 1, 0];
    private static readonly byte[] Ico = [0, 0, 1, 0, 1, 0, 16, 16];

    private static MultipartFormDataContent Form(params (string Name, byte[]? File, string? Value)[] parts)
    {
        var form = new MultipartFormDataContent();
        foreach (var (name, file, value) in parts)
        {
            if (file is not null)
            {
                var content = new ByteArrayContent(file);
                // The claimed type is ignored. The type comes from the bytes.
                content.Headers.ContentType = new MediaTypeHeaderValue("image/svg+xml");
                form.Add(content, name, $"{name}.bin");
            }
            else
            {
                form.Add(new StringContent(value!), name);
            }
        }
        return form;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private const string ImageRule = "The image must be a PNG, JPEG, WebP, GIF or ICO file of at most 2 MB.";

    // The refusal has one field, the English rule as its message, the given code, and no limits.
    private static async Task AssertRefusedAsync(HttpResponseMessage response, string field, string code, string message = ImageRule)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await JsonAsync(response);
        Assert.Equal(field, Assert.Single(json.GetProperty("errors").EnumerateObject()).Name);
        Assert.Equal(message, Assert.Single(json.GetProperty("errors").GetProperty(field).EnumerateArray()).GetString());
        Assert.Equal(field, Assert.Single(json.GetProperty("codes").EnumerateObject()).Name);
        Assert.Equal(code, Assert.Single(json.GetProperty("codes").GetProperty(field).EnumerateArray()).GetString());
        Assert.Empty(json.GetProperty("limits").EnumerateObject());
    }

    [Theory]
    [InlineData("png", "image/png")]
    [InlineData("jpeg", "image/jpeg")]
    [InlineData("webp", "image/webp")]
    [InlineData("gif", "image/gif")]
    [InlineData("ico", "image/x-icon")]
    public async Task An_image_is_stored_with_the_type_its_bytes_show(string kind, string contentType)
    {
        var bytes = kind switch { "png" => Png, "jpeg" => Jpeg, "webp" => Webp, "gif" => Gif, _ => Ico };
        await using var app = await TestApp.StartAsync();

        var response = await app.SendAsync(HttpMethod.Put, "/api/branding", Form(("logo", bytes, null)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal(Convert.ToBase64String(bytes), body.GetProperty("logoData").GetString());
        Assert.Equal(contentType, body.GetProperty("logoContentType").GetString());
        var row = await app.RowAsync();
        Assert.Equal(Convert.ToBase64String(bytes), row!.LogoData);
        Assert.Equal(contentType, row.LogoContentType);
        Assert.Equal("Pyttogpanne", row.CompanyName);
    }

    [Fact]
    public async Task The_logo_and_icon_are_set_together_and_read_publicly()
    {
        await using var app = await TestApp.StartAsync();

        await app.SendAsync(HttpMethod.Put, "/api/branding", Form(("logo", Png, null), ("icon", Ico, null)));
        var body = await JsonAsync(await app.SendAsync(HttpMethod.Get, "/api/branding", roles: null));

        Assert.Equal("image/png", body.GetProperty("logoContentType").GetString());
        Assert.Equal(Convert.ToBase64String(Ico), body.GetProperty("iconData").GetString());
        Assert.Equal("image/x-icon", body.GetProperty("iconContentType").GetString());
    }

    [Fact]
    public async Task A_part_not_sent_stays_and_a_removed_part_goes()
    {
        await using var app = await TestApp.StartAsync();
        await app.SendAsync(HttpMethod.Put, "/api/branding", Form(("logo", Png, null), ("icon", Ico, null)));

        await app.SendAsync(HttpMethod.Put, "/api/branding", Form(("icon", Gif, null)));
        var row = await app.RowAsync();
        Assert.Equal("image/png", row!.LogoContentType);
        Assert.Equal("image/gif", row.IconContentType);

        await app.SendAsync(HttpMethod.Put, "/api/branding", Form(("removeLogo", null, "true")));
        row = await app.RowAsync();
        Assert.Null(row!.LogoData);
        Assert.Null(row.LogoContentType);
        Assert.Equal("image/gif", row.IconContentType);

        await app.SendAsync(HttpMethod.Put, "/api/branding", Form(("removeIcon", null, "true"), ("icon", Png, null)));
        row = await app.RowAsync();
        Assert.Null(row!.IconData);
        Assert.Null(row.IconContentType);
    }

    [Fact]
    public async Task A_file_sent_with_its_removal_is_not_checked()
    {
        await using var app = await TestApp.StartAsync();
        await app.SendAsync(HttpMethod.Put, "/api/branding", Form(("logo", Png, null), ("icon", Ico, null)));

        var response = await app.SendAsync(HttpMethod.Put, "/api/branding",
            Form(("removeLogo", null, "true"), ("logo", "<svg/>"u8.ToArray(), null), ("removeIcon", null, "true"), ("icon", "<svg/>"u8.ToArray(), null)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var row = await app.RowAsync();
        Assert.Null(row!.LogoData);
        Assert.Null(row.IconData);
    }

    [Fact]
    public async Task Removing_wins_over_a_logo_sent_in_the_same_form()
    {
        await using var app = await TestApp.StartAsync();
        await app.SendAsync(HttpMethod.Put, "/api/branding", Form(("logo", Png, null)));

        await app.SendAsync(HttpMethod.Put, "/api/branding", Form(("removeLogo", null, "true"), ("logo", Jpeg, null)));

        Assert.Null((await app.RowAsync())!.LogoData);
    }

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>")]
    [InlineData("plain text")]
    [InlineData("RIFF....WAVE")]
    [InlineData("GIF90a")]
    public async Task A_file_that_is_not_an_accepted_image_is_refused_and_nothing_changes(string text)
    {
        await using var app = await TestApp.StartAsync();
        await app.SendAsync(HttpMethod.Put, "/api/branding", Form(("logo", Png, null)));

        var response = await app.SendAsync(HttpMethod.Put, "/api/branding", Form(("icon", Ico, null), ("logo", Encoding.UTF8.GetBytes(text), null)));

        await AssertRefusedAsync(response, "Logo", "invalidImage");
        var row = await app.RowAsync();
        Assert.Equal("image/png", row!.LogoContentType);
        Assert.Null(row.IconData);
    }

    [Fact]
    public async Task A_bad_icon_is_refused_too()
    {
        await using var app = await TestApp.StartAsync();

        var response = await app.SendAsync(HttpMethod.Put, "/api/branding", Form(("logo", Png, null), ("icon", "<svg/>"u8.ToArray(), null)));

        await AssertRefusedAsync(response, "Icon", "invalidImage");
        Assert.Null(await app.RowAsync());
    }

    [Fact]
    public async Task An_empty_file_or_one_over_2_MB_is_refused()
    {
        await using var app = await TestApp.StartAsync();
        var justFits = new byte[BrandingHandlers<TestDbContext, TestSettings>.MaxImageBytes];
        Png.CopyTo(justFits, 0);
        var tooBig = new byte[justFits.Length + 1];
        Png.CopyTo(tooBig, 0);

        await AssertRefusedAsync(await app.SendAsync(HttpMethod.Put, "/api/branding", Form(("logo", [], null))), "Logo", "invalidImage");
        await AssertRefusedAsync(await app.SendAsync(HttpMethod.Put, "/api/branding", Form(("logo", tooBig, null))), "Logo", "imageTooLarge");
        await AssertRefusedAsync(await app.SendAsync(HttpMethod.Put, "/api/branding", Form(("icon", tooBig, null))), "Icon", "imageTooLarge");
        Assert.Equal(HttpStatusCode.OK, (await app.SendAsync(HttpMethod.Put, "/api/branding", Form(("logo", justFits, null)))).StatusCode);
    }

    [Fact]
    public async Task A_body_that_is_not_a_form_is_refused()
    {
        await using var app = await TestApp.StartAsync();

        var response = await app.SendAsync(HttpMethod.Put, "/api/branding", new StringContent("{}", Encoding.UTF8, "application/json"));

        await AssertRefusedAsync(response, "Logo", "notAForm", "Send the images as a form.");
        Assert.Null(await app.RowAsync());
    }

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A }, null)]
    [InlineData(new byte[] { 0xFF, 0xD8 }, null)]
    [InlineData(new byte[] { 0, 0, 1, 0, 0, 0 }, null)]
    [InlineData(new byte[] { 0, 0, 1, 0, 1 }, null)]
    [InlineData(new byte[] { 0, 0, 2, 0, 1, 0 }, null)]
    [InlineData(new byte[] { 0, 0, 1, 0, 0, 1 }, "image/x-icon")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42 }, null)]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x37, 0x61 }, "image/gif")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x38, 0x61 }, null)]
    public void Short_or_near_miss_signatures_are_judged_exactly(byte[] bytes, string? expected) =>
        Assert.Equal(expected, ImageSignature.ContentType(bytes));

    [Fact]
    public async Task OnSaved_runs_after_branding_is_saved_and_not_after_a_refused_image()
    {
        var saved = new List<(SiteSettingsSaved Saved, string Path)>();
        await using var app = await TestApp.StartAsync(o => o.OnSaved = s => { saved.Add((s, s.Http.Request.Path)); return Task.CompletedTask; });

        await app.SendAsync(HttpMethod.Put, "/api/branding", Form(("logo", [1, 2, 3], null)));
        Assert.Empty(saved);

        await app.SendAsync(HttpMethod.Put, "/api/branding", Form(("logo", Png, null)));
        var one = Assert.Single(saved);
        Assert.Equal(SiteSettingsPart.Branding, one.Saved.Part);
        Assert.Equal("image/png", ((TestSettings)one.Saved.Settings).LogoContentType);
        Assert.Equal("/api/branding", one.Path);
    }
}
