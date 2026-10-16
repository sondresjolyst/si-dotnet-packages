using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace SjolystInnovation.AppKit.Storage.Tests;

public sealed class FileStoreTests : IDisposable
{
    internal static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13];
    internal static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 16];
    internal static readonly byte[] Webp = [.. "RIFF"u8, 0x24, 0, 0, 0, .. "WEBPVP8 "u8];
    internal static readonly byte[] Gif = [.. "GIF89a"u8, 1, 0, 1, 0];
    internal static readonly byte[] Avif = [0, 0, 0, 0x1C, .. "ftypavif"u8, 0, 0, 0, 0, .. "avifmif1miaf"u8];
    internal static readonly byte[] Pdf = [.. "%PDF-1.4 data"u8];
    internal static readonly byte[] Ico = [0, 0, 1, 0, 1, 0, 16, 16];

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"appkit-storage-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private LocalFileStore Store(AppKitFileKinds allowed = AppKitFileKinds.Images, long maxBytes = 1024 * 1024, string? path = null) =>
        new(new AppKitFileStoreOptions { Path = path ?? _root, MaxBytes = maxBytes, Allowed = allowed }, NullLogger<LocalFileStore>.Instance);

    private static MemoryStream Bytes(byte[] bytes) => new(bytes);

    // Near misses: a file that only starts like a known type is not that type.
    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0, 0, 0 })]
    [InlineData(new byte[] { 0x25, 0x50, 0x58, 0x46, 0x2D, 0x31 })]
    [InlineData(new byte[] { 0, 0, 1, 0, 0, 0, 16, 16 })]
    [InlineData(new byte[] { 0xFF, 0xD8, 0x00, 0xE0 })]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x38, 0x61 })]
    public void Detect_refuses_a_file_that_only_starts_like_a_known_type(byte[] header) =>
        Assert.Equal(AppKitFileKinds.None, FileSignature.Detect(header));

    [Theory]
    [InlineData("png", "image/png", ".png")]
    [InlineData("jpeg", "image/jpeg", ".jpg")]
    [InlineData("webp", "image/webp", ".webp")]
    [InlineData("gif", "image/gif", ".gif")]
    [InlineData("avif", "image/avif", ".avif")]
    [InlineData("pdf", "application/pdf", ".pdf")]
    [InlineData("ico", "image/x-icon", ".ico")]
    public async Task A_file_is_stored_with_the_kind_its_bytes_show(string kind, string contentType, string extension)
    {
        var bytes = kind switch { "png" => Png, "jpeg" => Jpeg, "webp" => Webp, "gif" => Gif, "avif" => Avif, "pdf" => Pdf, _ => Ico };
        var store = Store(AppKitFileKinds.Images | AppKitFileKinds.Pdf | AppKitFileKinds.Ico);

        // The name claims a different kind. Only the bytes count.
        var stored = await store.SaveAsync(Bytes(bytes), "upload.svg");

        Assert.Equal(contentType, stored.ContentType);
        Assert.EndsWith(extension, stored.StoredPath, StringComparison.Ordinal);
        Assert.Equal(32 + extension.Length, stored.StoredPath.Length);
        Assert.Equal(bytes.Length, stored.SizeBytes);
        Assert.Equal("upload.svg", stored.FileName);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(_root, stored.StoredPath)));
    }

    [Fact]
    public async Task An_avif_with_the_brand_among_the_compatible_brands_is_recognized()
    {
        byte[] bytes = [0, 0, 0, 0x18, .. "ftypmif1"u8, 0, 0, 0, 0, .. "mif1avif"u8];

        var stored = await Store().SaveAsync(Bytes(bytes), "a.avif");

        Assert.Equal("image/avif", stored.ContentType);
    }

    [Fact]
    public async Task Another_iso_media_file_is_not_taken_for_avif()
    {
        byte[] bytes = [0, 0, 0, 0x18, .. "ftypisom"u8, 0, 0, 2, 0, .. "isomiso2"u8];

        var ex = await Assert.ThrowsAsync<AppKitFileRefusedException>(() => Store().SaveAsync(Bytes(bytes), "a.mp4"));

        Assert.Equal("Only PNG, JPEG, WebP, GIF, or AVIF files are accepted.", ex.Message);
    }

    [Fact]
    public async Task A_kind_the_store_does_not_take_is_refused_and_nothing_is_written()
    {
        var ex = await Assert.ThrowsAsync<AppKitFileRefusedException>(() => Store().SaveAsync(Bytes(Pdf), "report.pdf"));

        Assert.Equal("Only PNG, JPEG, WebP, GIF, or AVIF files are accepted.", ex.Message);
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task A_report_store_takes_only_pdf()
    {
        var store = Store(AppKitFileKinds.Pdf);

        var ex = await Assert.ThrowsAsync<AppKitFileRefusedException>(() => store.SaveAsync(Bytes(Png), "file.pdf"));
        var stored = await store.SaveAsync(Bytes(Pdf), "file.pdf");

        Assert.Equal("Only PDF files are accepted.", ex.Message);
        using var reader = new StreamReader(await store.OpenReadAsync(stored.StoredPath));
        Assert.StartsWith("%PDF", await reader.ReadToEndAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Svg_and_other_unknown_bytes_are_refused()
    {
        var svg = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");

        await Assert.ThrowsAsync<AppKitFileRefusedException>(() => Store().SaveAsync(Bytes(svg), "logo.png"));
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task An_empty_file_is_refused()
    {
        var ex = await Assert.ThrowsAsync<AppKitFileRefusedException>(() => Store().SaveAsync(Bytes([]), "empty.png"));

        Assert.Equal("Uploaded file is empty.", ex.Message);
    }

    [Fact]
    public async Task A_file_over_the_limit_is_refused_before_it_is_read()
    {
        var big = new byte[2 * 1024 * 1024];
        Png.CopyTo(big, 0);

        var ex = await Assert.ThrowsAsync<AppKitFileRefusedException>(() => Store().SaveAsync(Bytes(big), "big.png"));

        Assert.Equal("The file exceeds the 1 MB limit.", ex.Message);
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task A_stream_without_a_length_is_cut_off_at_the_limit_and_leaves_no_file()
    {
        var big = new byte[3000];
        Png.CopyTo(big, 0);

        var ex = await Assert.ThrowsAsync<AppKitFileRefusedException>(() => Store(maxBytes: 2048).SaveAsync(new ForwardOnlyStream(big), "big.png"));

        Assert.Equal("The file exceeds the 2 KB limit.", ex.Message);
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task A_file_of_exactly_the_limit_is_taken()
    {
        var bytes = new byte[2048];
        Png.CopyTo(bytes, 0);

        var stored = await Store(maxBytes: 2048).SaveAsync(new ForwardOnlyStream(bytes), "edge.png");

        Assert.Equal(2048, stored.SizeBytes);
    }

    [Fact]
    public async Task A_canceled_save_leaves_no_file()
    {
        var bytes = new byte[300_000];
        Png.CopyTo(bytes, 0);
        using var cts = new CancellationTokenSource();
        var stream = new ForwardOnlyStream(bytes, onRead: cts.Cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store().SaveAsync(stream, "x.png", cts.Token));

        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task Delete_removes_the_file_and_a_missing_file_is_no_error()
    {
        var store = Store();
        var stored = await store.SaveAsync(Bytes(Png), "a.png");

        await store.DeleteAsync(stored.StoredPath);
        await store.DeleteAsync(stored.StoredPath);

        Assert.False(await store.ExistsAsync(stored.StoredPath));
    }

    [Fact]
    public async Task A_canceled_token_stops_open_exists_and_delete()
    {
        var store = Store();
        var stored = await store.SaveAsync(Bytes(Png), "a.png");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.OpenReadAsync(stored.StoredPath, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ExistsAsync(stored.StoredPath, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.DeleteAsync(stored.StoredPath, cts.Token));
        Assert.True(await store.ExistsAsync(stored.StoredPath));
    }

    [Fact]
    public async Task A_stored_path_cannot_leave_the_root()
    {
        var store = Store(path: Path.Combine(_root, "store"));
        Directory.CreateDirectory(Path.Combine(_root, "store2"));
        File.WriteAllBytes(Path.Combine(_root, "secret.png"), Png);
        File.WriteAllBytes(Path.Combine(_root, "store2", "sibling.png"), Png);

        await Assert.ThrowsAsync<FileNotFoundException>(() => store.OpenReadAsync("../../etc/passwd"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => store.OpenReadAsync("..\\secret.png"));
        Assert.False(await store.ExistsAsync("../secret.png"));
        Assert.False(await store.ExistsAsync("../store2/sibling.png"));
        Assert.Throws<InvalidOperationException>(() => store.ResolvePath(".."));
        Assert.Throws<InvalidOperationException>(() => store.ResolvePath(""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ExistsAsync("."));
        await store.DeleteAsync("../secret.png");
        Assert.True(File.Exists(Path.Combine(_root, "secret.png")));
    }

    [Theory]
    [InlineData("photo.png", "photo.png")]
    [InlineData("C:\\fakepath\\photo.png", "photo.png")]
    [InlineData("../../etc/photo.png", "photo.png")]
    [InlineData("  spaced.png  ", "spaced.png")]
    public void The_file_name_keeps_only_its_last_segment(string given, string kept)
    {
        Assert.Equal(kept, LocalFileStore.CleanFileName(given, "stored.png"));
    }

    [Fact]
    public void A_blank_file_name_falls_back_to_the_stored_name()
    {
        Assert.Equal("stored.png", LocalFileStore.CleanFileName("", "stored.png"));
        Assert.Equal("stored.png", LocalFileStore.CleanFileName("dir/", "stored.png"));
        Assert.Equal("stored.png", LocalFileStore.CleanFileName(null, "stored.png"));
    }

    [Fact]
    public async Task A_long_file_name_is_cut_to_260_characters()
    {
        var name = new string('a', 300) + ".png";

        var stored = await Store().SaveAsync(Bytes(Png), name);

        Assert.Equal(260, stored.FileName.Length);
        Assert.Equal(name[..260], stored.FileName);
    }

    [Fact]
    public void A_cut_does_not_split_a_character_outside_the_basic_plane()
    {
        var name = new string('a', 259) + "\U0001F600" + ".png";

        var kept = LocalFileStore.CleanFileName(name, "stored.png");

        Assert.Equal(259, kept.Length);
    }

    [Fact]
    public void Named_stores_are_registered_apart()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddAppKitFileStore("images", o => { o.Path = Path.Combine(_root, "images"); o.Allowed = AppKitFileKinds.Images; });
        services.AddAppKitFileStore("reports", o => { o.Path = Path.Combine(_root, "reports"); o.Allowed = AppKitFileKinds.Pdf; o.MaxBytes = 50 * 1024 * 1024; });
        using var provider = services.BuildServiceProvider();

        var images = provider.GetRequiredKeyedService<IAppKitFileStore>("images");
        var reports = provider.GetRequiredKeyedService<IAppKitFileStore>("reports");

        Assert.NotSame(images, reports);
        Assert.Same(images, provider.GetRequiredKeyedService<IAppKitFileStore>("images"));
        Assert.True(Directory.Exists(Path.Combine(_root, "images")));
        Assert.Equal(50 * 1024 * 1024, provider.GetRequiredService<IOptionsMonitor<AppKitFileStoreOptions>>().Get("reports").MaxBytes);
    }

    [Fact]
    public void A_store_without_a_path_or_kinds_fails_validation()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddAppKitFileStore("nopath", o => o.Allowed = AppKitFileKinds.Pdf);
        services.AddAppKitFileStore("nokinds", o => o.Path = _root);
        using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IOptionsMonitor<AppKitFileStoreOptions>>();

        Assert.Contains("needs a Path", Assert.Throws<OptionsValidationException>(() => monitor.Get("nopath")).Message, StringComparison.Ordinal);
        Assert.Contains("at least one kind", Assert.Throws<OptionsValidationException>(() => monitor.Get("nokinds")).Message, StringComparison.Ordinal);
    }

    /// <summary>A stream that cannot seek, like a request body.</summary>
    private sealed class ForwardOnlyStream(byte[] bytes, Action? onRead = null) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            onRead?.Invoke();
            return _inner.Read(buffer, offset, Math.Min(count, 1000));
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
