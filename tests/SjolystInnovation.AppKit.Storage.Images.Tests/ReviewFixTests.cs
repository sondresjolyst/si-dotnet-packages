using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SkiaSharp;
using Xunit;

namespace SjolystInnovation.AppKit.Storage.Images.Tests;

/// <summary>Rendition cleanup, size caps, display width, and what the backfill remembers.</summary>
public sealed class ReviewFixTests : IDisposable
{
    private const string Base = "/api/content-images";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"appkit-flaky-{Guid.NewGuid():N}");
    private readonly ServiceProvider _provider;

    public ReviewFixTests()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddAppKitFileStore("real", o => { o.Path = _root; o.Allowed = AppKitFileKinds.Images; });
        _provider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _provider.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>A store that fails or cancels on the save with the given number, counting the original as 1.</summary>
    private sealed class FlakyStore(IAppKitFileStore inner, int failOnSave, Action? onFail = null) : IAppKitFileStore
    {
        private int _saves;

        public async Task<StoredFile> SaveAsync(Stream content, string fileName, CancellationToken cancellationToken = default)
        {
            if (++_saves == failOnSave)
            {
                if (onFail is null)
                    throw new IOException("The disk is full.");
                onFail();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return await inner.SaveAsync(content, fileName, cancellationToken);
        }

        public Task<Stream> OpenReadAsync(string storedPath, CancellationToken cancellationToken = default) => inner.OpenReadAsync(storedPath, cancellationToken);
        public Task<bool> ExistsAsync(string storedPath, CancellationToken cancellationToken = default) => inner.ExistsAsync(storedPath, cancellationToken);
        public Task DeleteAsync(string storedPath, CancellationToken cancellationToken = default) => inner.DeleteAsync(storedPath, cancellationToken);
    }

    private static ImageProcessor Processor(IAppKitFileStore store, AppKitContentImagesOptions? options = null) =>
        new(Options.Create(options ?? new AppKitContentImagesOptions()), store, NullLogger<ImageProcessor>.Instance);

    private IAppKitFileStore Real => _provider.GetRequiredKeyedService<IAppKitFileStore>("real");

    [Fact]
    public async Task A_failed_later_width_deletes_the_renditions_already_written()
    {
        var store = new FlakyStore(Real, failOnSave: 4);
        var original = (await store.SaveAsync(new MemoryStream(TestImages.Png(1600, 900)), "a.png")).StoredPath;
        using var processor = Processor(store);

        var result = await processor.GenerateWebpVariantsAsync(original, CancellationToken.None);

        Assert.Empty(result.Variants);
        Assert.False(result.Undecodable);
        Assert.Equal([original], Directory.GetFiles(_root).Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_canceled_generation_deletes_the_renditions_already_written()
    {
        using var cts = new CancellationTokenSource();
        var store = new FlakyStore(Real, failOnSave: 3, onFail: cts.Cancel);
        var original = (await store.SaveAsync(new MemoryStream(TestImages.Png(1600, 900)), "a.png")).StoredPath;
        using var processor = Processor(store);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processor.GenerateWebpVariantsAsync(original, cts.Token));

        Assert.Equal([original], Directory.GetFiles(_root).Select(Path.GetFileName));
    }

    [Fact]
    public async Task An_image_over_the_pixel_cap_is_undecodable()
    {
        var original = (await Real.SaveAsync(new MemoryStream(TestImages.Png(1000, 1000)), "a.png")).StoredPath;
        using var processor = Processor(Real, new AppKitContentImagesOptions { MaxSourcePixels = 999_999 });

        var result = await processor.GenerateWebpVariantsAsync(original, CancellationToken.None);

        Assert.Empty(result.Variants);
        Assert.True(result.Undecodable);
    }

    [Fact]
    public async Task Decodes_wait_for_a_free_slot()
    {
        var original = (await Real.SaveAsync(new MemoryStream(TestImages.Png(800, 400)), "a.png")).StoredPath;
        using var processor = Processor(Real, new AppKitContentImagesOptions { MaxConcurrentDecodes = 1 });

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => processor.GenerateWebpVariantsAsync(original, CancellationToken.None)));

        Assert.All(results, r => Assert.Equal(4, r.Variants.Count));
    }

    [Fact]
    public async Task A_large_rotated_jpeg_still_reaches_the_widest_rendition()
    {
        var original = (await Real.SaveAsync(new MemoryStream(TestImages.Jpeg(4032, 3024, SKEncodedOrigin.RightTop)), "phone.jpg")).StoredPath;
        using var processor = Processor(Real);

        var variants = (await processor.GenerateWebpVariantsAsync(original, CancellationToken.None)).Variants;

        Assert.Equal(2000, variants[^1].Width);
        await using var stream = await Real.OpenReadAsync(variants[^1].StoredPath);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        Assert.Equal((2000, 2667), TestImages.SizeOf(copy.ToArray()));
    }

    [Fact]
    public async Task An_upload_over_the_pixel_cap_keeps_its_original_and_is_remembered()
    {
        await using var app = await TestApp.StartAsync(storage: new() { ["Storage:MaxSourcePixels"] = "100000" });

        var image = await app.WithServiceAsync((images, _) => images.CreateAsync(new MemoryStream(TestImages.Png(400, 400)), "big.png", "u1"));
        var response = await app.SendAsync(HttpMethod.Get, $"{Base}/{image.Id}", roles: null, accept: "image/webp");

        Assert.Empty(image.Variants);
        Assert.Equal((400, 400), (image.Width, image.Height));
        Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
        Assert.True(app.Backfill.HasFailed(image.Id));
        Assert.Equal(1, app.Processor.DecodeAttempts);
    }

    [Fact]
    public async Task A_backfill_whose_rows_cannot_be_saved_leaves_no_files_and_is_tried_again()
    {
        await using var app = await TestApp.StartAsync();
        var id = await app.SeedAsync(TestImages.Png(800, 400), ".png", "image/png");
        app.Saves.Fail = true;

        var failed = await app.SendAsync(HttpMethod.Get, $"{Base}/{id}", roles: null, accept: "image/webp");

        Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
        Assert.Equal("image/png", failed.Content.Headers.ContentType!.MediaType);
        Assert.Equal("public, max-age=3600", failed.Headers.CacheControl!.ToString());
        Assert.Single(app.Files());
        Assert.False(app.Backfill.HasFailed(id));

        app.Saves.Fail = false;
        var retried = await app.SendAsync(HttpMethod.Get, $"{Base}/{id}", roles: null, accept: "image/webp");

        Assert.Equal("image/webp", retried.Content.Headers.ContentType!.MediaType);
        Assert.Equal(5, app.Files().Length);
    }

    [Fact]
    public async Task Dimensions_saves_nothing_when_nothing_was_measured_and_does_not_read_a_broken_file_again()
    {
        await using var app = await TestApp.StartAsync();
        var id = await app.SeedAsync(TestImages.BrokenPng(), ".png", "image/png");
        var savesBefore = app.Saves.Count;

        await app.SendAsync(HttpMethod.Get, $"{Base}/dimensions?ids={id}", roles: null);
        await app.SendAsync(HttpMethod.Get, $"{Base}/dimensions?ids={id}", roles: null);

        Assert.Equal(savesBefore, app.Saves.Count);
        Assert.Equal(1, app.Processor.ProbeAttempts);
        Assert.True(app.Backfill.HasFailed(id));
    }

    [Fact]
    public async Task Dimensions_does_not_remember_a_missing_file()
    {
        await using var app = await TestApp.StartAsync();
        var id = await app.SeedAsync(TestImages.Png(10, 10), ".png", "image/png");
        File.Delete(Path.Combine(app.Root, app.Files()[0]));

        await app.SendAsync(HttpMethod.Get, $"{Base}/dimensions?ids={id}", roles: null);

        Assert.False(app.Backfill.HasFailed(id));
        Assert.Equal(1, await app.WithDbAsync(db => db.Set<ContentImage>().CountAsync()));
    }
}
