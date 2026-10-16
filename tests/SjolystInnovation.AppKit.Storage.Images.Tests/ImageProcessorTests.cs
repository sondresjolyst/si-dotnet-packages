using SkiaSharp;
using Xunit;

namespace SjolystInnovation.AppKit.Storage.Images.Tests;

/// <summary>Runs the real SkiaSharp decoder and encoder.</summary>
public class ImageProcessorTests
{
    private static async Task<string> SaveAsync(TestApp app, byte[] bytes, string name = "photo") =>
        (await app.Store.SaveAsync(new MemoryStream(bytes), name)).StoredPath;

    private static byte[] Read(TestApp app, string storedPath)
    {
        using var stream = app.Store.OpenRead(storedPath);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    [Fact]
    public async Task Probe_reads_the_stored_dimensions_of_a_png_and_a_jpeg()
    {
        await using var app = await TestApp.StartAsync();

        Assert.Equal((1600, 900), app.Processor.Probe(await SaveAsync(app, TestImages.Png(1600, 900))));
        Assert.Equal((900, 1600), app.Processor.Probe(await SaveAsync(app, TestImages.Png(900, 1600))));
        Assert.Equal((640, 480), app.Processor.Probe(await SaveAsync(app, TestImages.Jpeg(640, 480))));
    }

    [Fact]
    public async Task Probe_answers_nothing_for_a_broken_or_missing_file()
    {
        await using var app = await TestApp.StartAsync();

        Assert.Null(app.Processor.Probe(await SaveAsync(app, TestImages.BrokenPng())));
        Assert.Null(app.Processor.Probe("does-not-exist.png"));
    }

    [Theory]
    [InlineData(SKEncodedOrigin.TopLeft, 'L', 'R')]
    [InlineData(SKEncodedOrigin.TopRight, 'R', 'L')]
    [InlineData(SKEncodedOrigin.BottomRight, 'B', 'b')]
    [InlineData(SKEncodedOrigin.BottomLeft, 'b', 'B')]
    [InlineData(SKEncodedOrigin.LeftTop, 'L', 'b')]
    [InlineData(SKEncodedOrigin.RightTop, 'R', 'B')]
    [InlineData(SKEncodedOrigin.RightBottom, 'B', 'R')]
    [InlineData(SKEncodedOrigin.LeftBottom, 'b', 'L')]
    public void Orient_puts_the_stored_corners_where_each_orientation_displays_them(SKEncodedOrigin origin, char firstCorner, char secondCorner)
    {
        // L is the top left, R the top right, B the bottom right and b the bottom left, as displayed.
        using var bitmap = new SKBitmap(4, 2);
        bitmap.Erase(SKColors.Blue);
        bitmap.SetPixel(0, 0, SKColors.Red);
        bitmap.SetPixel(3, 0, SKColors.Lime);

        using var oriented = ImageProcessor.Orient(bitmap, origin);

        var quarterTurn = (int)origin >= (int)SKEncodedOrigin.LeftTop;
        Assert.Equal(quarterTurn ? (2, 4) : (4, 2), (oriented.Width, oriented.Height));
        Assert.Equal(SKColors.Red, Corner(oriented, firstCorner));
        Assert.Equal(SKColors.Lime, Corner(oriented, secondCorner));
    }

    private static SKColor Corner(SKBitmap bitmap, char corner) => corner switch
    {
        'L' => bitmap.GetPixel(0, 0),
        'R' => bitmap.GetPixel(bitmap.Width - 1, 0),
        'B' => bitmap.GetPixel(bitmap.Width - 1, bitmap.Height - 1),
        _ => bitmap.GetPixel(0, bitmap.Height - 1),
    };

    [Theory]
    [InlineData(SKEncodedOrigin.TopLeft, 'L', 'R')]
    [InlineData(SKEncodedOrigin.TopRight, 'R', 'L')]
    [InlineData(SKEncodedOrigin.BottomRight, 'B', 'b')]
    [InlineData(SKEncodedOrigin.BottomLeft, 'b', 'B')]
    [InlineData(SKEncodedOrigin.LeftTop, 'L', 'b')]
    [InlineData(SKEncodedOrigin.RightTop, 'R', 'B')]
    [InlineData(SKEncodedOrigin.RightBottom, 'B', 'R')]
    [InlineData(SKEncodedOrigin.LeftBottom, 'b', 'L')]
    public async Task A_jpeg_with_an_exif_orientation_gets_renditions_as_displayed_and_matching_dimensions(SKEncodedOrigin origin, char redCorner, char greenCorner)
    {
        await using var app = await TestApp.StartAsync();
        var stored = await SaveAsync(app, TestImages.Jpeg(800, 400, origin));

        var probed = app.Processor.Probe(stored);
        var variants = (await app.Processor.GenerateWebpVariantsAsync(stored, CancellationToken.None)).Variants;

        var quarterTurn = (int)origin >= (int)SKEncodedOrigin.LeftTop;
        Assert.Equal(quarterTurn ? (400, 800) : (800, 400), probed);
        var largest = Read(app, variants[^1].StoredPath);
        Assert.Equal(probed, TestImages.SizeOf(largest));
        var quarters = TestImages.Quarters(largest);
        Assert.True(TestImages.Near(TestImages.TopLeft, Pick(quarters, redCorner)), $"red is not at {redCorner}");
        Assert.True(TestImages.Near(TestImages.TopRight, Pick(quarters, greenCorner)), $"green is not at {greenCorner}");
    }

    private static SKColor Pick((SKColor TopLeft, SKColor TopRight, SKColor BottomLeft, SKColor BottomRight) q, char corner) => corner switch
    {
        'L' => q.TopLeft,
        'R' => q.TopRight,
        'B' => q.BottomRight,
        _ => q.BottomLeft,
    };

    [Fact]
    public async Task A_png_gets_a_webp_rendition_at_each_width_below_its_own_and_at_its_own()
    {
        await using var app = await TestApp.StartAsync();
        var stored = await SaveAsync(app, TestImages.Png(1600, 900));

        var variants = (await app.Processor.GenerateWebpVariantsAsync(stored, CancellationToken.None)).Variants;

        Assert.Equal([384, 640, 768, 1024, 1366, 1600], variants.Select(v => v.Width));
        foreach (var variant in variants)
        {
            var bytes = Read(app, variant.StoredPath);
            Assert.Equal("RIFF"u8.ToArray(), bytes[..4]);
            Assert.Equal(variant.SizeBytes, bytes.Length);
            Assert.EndsWith(".webp", variant.StoredPath, StringComparison.Ordinal);
            Assert.Equal((variant.Width, (int)Math.Round(900.0 * variant.Width / 1600)), TestImages.SizeOf(bytes));
        }
    }

    [Fact]
    public async Task A_large_png_decodes_at_full_size_and_is_resized_to_the_widest_rendition()
    {
        await using var app = await TestApp.StartAsync();
        var stored = await SaveAsync(app, TestImages.Png(4100, 3000));

        var variants = (await app.Processor.GenerateWebpVariantsAsync(stored, CancellationToken.None)).Variants;

        Assert.Equal([384, 640, 768, 1024, 1366, 1600, 2000], variants.Select(v => v.Width));
        Assert.Equal((2000, 1463), TestImages.SizeOf(Read(app, variants[^1].StoredPath)));
    }

    [Fact]
    public async Task A_large_jpeg_decodes_downscaled_and_still_reaches_the_widest_rendition()
    {
        await using var app = await TestApp.StartAsync();
        var stored = await SaveAsync(app, TestImages.Jpeg(4000, 3000));

        var variants = (await app.Processor.GenerateWebpVariantsAsync(stored, CancellationToken.None)).Variants;

        Assert.Equal(2000, variants[^1].Width);
        Assert.Equal((2000, 1500), TestImages.SizeOf(Read(app, variants[^1].StoredPath)));
    }

    [Fact]
    public async Task A_broken_file_gets_no_renditions()
    {
        await using var app = await TestApp.StartAsync();
        var stored = await SaveAsync(app, TestImages.BrokenPng());

        Assert.Empty((await app.Processor.GenerateWebpVariantsAsync(stored, CancellationToken.None)).Variants);
        Assert.Single(app.Files());
    }

    [Fact]
    public async Task An_image_over_the_source_limit_gets_no_renditions()
    {
        await using var app = await TestApp.StartAsync(storage: new() { ["Storage:MaxSourceDimension"] = "1000" });
        var stored = await SaveAsync(app, TestImages.Png(1200, 300));

        Assert.Empty((await app.Processor.GenerateWebpVariantsAsync(stored, CancellationToken.None)).Variants);
    }

    [Fact]
    public async Task The_widths_and_quality_come_from_the_options()
    {
        await using var app = await TestApp.StartAsync(storage: new()
        {
            ["Storage:Widths:0"] = "100",
            ["Storage:Widths:1"] = "200",
            ["Storage:MaxWidth"] = "1000",
            ["Storage:Quality"] = "50",
        });
        var stored = await SaveAsync(app, TestImages.Png(1200, 400));

        var variants = (await app.Processor.GenerateWebpVariantsAsync(stored, CancellationToken.None)).Variants;

        Assert.Equal([100, 200, 1000], variants.Select(v => v.Width));
    }
}
