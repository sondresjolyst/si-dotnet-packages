using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace SjolystInnovation.AppKit.Storage.Images;

/// <summary>A WebP rendition that <see cref="ImageProcessor"/> wrote to the store.</summary>
internal sealed record GeneratedVariant(int Width, string StoredPath, long SizeBytes);

/// <summary>The outcome of <see cref="ImageProcessor.GenerateWebpVariantsAsync"/>.</summary>
/// <param name="Variants">The renditions written, smallest first.</param>
/// <param name="Undecodable">
/// True when the original cannot be decoded or is over a size cap, so a new try gives the same result.
/// False after a passing error, such as a failed write.
/// </param>
internal sealed record VariantResult(IReadOnlyList<GeneratedVariant> Variants, bool Undecodable);

/// <summary>The outcome of <see cref="ImageProcessor.ProbeAsync"/>.</summary>
/// <param name="Dimensions">The dimensions as displayed, or <c>null</c> when the file cannot be read.</param>
/// <param name="Undecodable">True when no codec can read the file. False after an error such as a failed read.</param>
internal sealed record ProbeResult((int Width, int Height)? Dimensions, bool Undecodable);

/// <summary>Reads image dimensions and writes resized WebP renditions with SkiaSharp.</summary>
internal sealed partial class ImageProcessor(
    IOptions<AppKitContentImagesOptions> options,
    [FromKeyedServices(AppKitContentImages.StoreName)] IAppKitFileStore store,
    ILogger<ImageProcessor> logger) : IDisposable
{
    private readonly AppKitContentImagesOptions _options = options.Value;
    // A decode can take hundreds of megabytes, so only a few run at once.
    private readonly SemaphoreSlim _decodes = new(options.Value.MaxConcurrentDecodes, options.Value.MaxConcurrentDecodes);
    private int _decodeAttempts;
    private int _probeAttempts;

    /// <summary>The number of times <see cref="GenerateWebpVariantsAsync"/> opened a file to decode it.</summary>
    internal int DecodeAttempts => Volatile.Read(ref _decodeAttempts);

    /// <summary>The number of times <see cref="ProbeAsync"/> opened a file.</summary>
    internal int ProbeAttempts => Volatile.Read(ref _probeAttempts);

    public void Dispose() => _decodes.Dispose();

    /// <summary>
    /// Reads a stored image's dimensions as displayed, and tells an undecodable file from a passing error. A
    /// quarter-turn EXIF orientation swaps the axes, the same way <see cref="Orient"/> does.
    /// </summary>
    /// <param name="storedPath">The stored path of the original.</param>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The dimensions, or none with the reason.</returns>
    public async Task<ProbeResult> ProbeAsync(string storedPath, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _probeAttempts);
        try
        {
            await using var input = await store.OpenReadAsync(storedPath, cancellationToken);
            using var codec = SKCodec.Create(input);
            if (codec is null || codec.Info.Width == 0 || codec.Info.Height == 0)
                return new ProbeResult(null, Undecodable: true);
            var info = codec.Info;
            return new ProbeResult(IsQuarterTurn(codec.EncodedOrigin) ? (info.Height, info.Width) : (info.Width, info.Height), Undecodable: false);
        }
#pragma warning disable CA1031 // A file that cannot be read has no dimensions. The upload goes on without them.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogProbeFailed(ex, storedPath);
            return new ProbeResult(null, Undecodable: false);
        }
    }

    /// <summary>
    /// Writes WebP renditions of a stored original at each configured width below its displayed width, and
    /// at its displayed width up to <see cref="AppKitContentImagesOptions.MaxWidth"/>. If a width fails, the
    /// renditions already written are deleted again.
    /// </summary>
    /// <returns>The renditions, or none with the reason.</returns>
    /// <exception cref="OperationCanceledException">When <paramref name="cancellationToken"/> is canceled. No renditions stay behind.</exception>
    public async Task<VariantResult> GenerateWebpVariantsAsync(string storedPath, CancellationToken cancellationToken)
    {
        await _decodes.WaitAsync(cancellationToken);
        try
        {
            Interlocked.Increment(ref _decodeAttempts);
            using var original = await DecodeAsync(storedPath, cancellationToken);
            if (original is null)
                return new VariantResult([], Undecodable: true);
            return new VariantResult(await EncodeWidthsAsync(original, cancellationToken), Undecodable: false);
        }
#pragma warning disable CA1031 // Renditions are an optimization. The original stays servable when they fail.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogGenerateFailed(ex, storedPath);
            return new VariantResult([], Undecodable: false);
        }
        finally
        {
            _decodes.Release();
        }
    }

    /// <summary>
    /// Decodes a stored original, oriented as displayed. A codec that can scale, such as JPEG, decodes at the
    /// smallest supported size whose displayed width is still at least
    /// <see cref="AppKitContentImagesOptions.MaxWidth"/>. A codec that cannot, such as PNG or GIF, decodes at
    /// full size.
    /// </summary>
    /// <returns>The bitmap, or <c>null</c> when the file cannot be decoded or is over a size cap.</returns>
    private async Task<SKBitmap?> DecodeAsync(string storedPath, CancellationToken cancellationToken)
    {
        await using var input = await store.OpenReadAsync(storedPath, cancellationToken);
        using var codec = SKCodec.Create(input);
        if (codec is null)
        {
            LogUndecodable(storedPath);
            return null;
        }

        var info = codec.Info;
        if (info.Width == 0 || info.Height == 0)
            return null;
        if (info.Width > _options.MaxSourceDimension || info.Height > _options.MaxSourceDimension
            || (long)info.Width * info.Height > _options.MaxSourcePixels)
        {
            LogTooLarge(storedPath, info.Width, info.Height);
            return null;
        }

        // The renditions are as wide as the image is displayed, so a quarter turn measures the stored height.
        var quarterTurn = IsQuarterTurn(codec.EncodedOrigin);
        int DisplayWidth(SKSizeI size) => quarterTurn ? size.Height : size.Width;
        var displayWidth = DisplayWidth(info.Size);
        var targetWidth = Math.Min(displayWidth, _options.MaxWidth);
        var sample = 1;
        while (displayWidth / (sample * 2) >= targetWidth)
            sample *= 2;
        // The codec rounds the scale to one it supports and answers the full size when it supports none.
        var size = sample == 1 ? info.Size : codec.GetScaledDimensions(1f / sample);
        if (DisplayWidth(size) < targetWidth)
            size = info.Size;

        var decodeInfo = new SKImageInfo(size.Width, size.Height, SKImageInfo.PlatformColorType, SKAlphaType.Premul);
        var decoded = SKBitmap.Decode(codec, decodeInfo);
        if (decoded is null && size != info.Size)
            decoded = SKBitmap.Decode(codec, decodeInfo.WithSize(info.Width, info.Height));
        if (decoded is null)
        {
            LogUndecodable(storedPath);
            return null;
        }

        // SKBitmap.Decode ignores the EXIF orientation, so it is applied here.
        var oriented = Orient(decoded, codec.EncodedOrigin);
        if (!ReferenceEquals(oriented, decoded))
            decoded.Dispose();
        return oriented;
    }

    private async Task<IReadOnlyList<GeneratedVariant>> EncodeWidthsAsync(SKBitmap original, CancellationToken cancellationToken)
    {
        var fullWidth = Math.Min(original.Width, _options.MaxWidth);
        var widths = _options.Widths
            .Where(w => w > 0 && w < fullWidth)
            .Append(fullWidth)
            .Distinct()
            .Order()
            .ToList();

        var results = new List<GeneratedVariant>();
        try
        {
            foreach (var width in widths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var height = Math.Max(1, (int)Math.Round((double)original.Height * width / original.Width));
                var target = width == original.Width
                    ? original
                    : original.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKCubicResampler.Mitchell));
                if (target is null)
                    continue;
                try
                {
                    using var image = SKImage.FromBitmap(target);
                    using var data = image.Encode(SKEncodedImageFormat.Webp, _options.Quality);
                    if (data is null)
                        continue;
                    await using var stream = new MemoryStream(data.ToArray(), writable: false);
                    var stored = await store.SaveAsync(stream, $"{width}.webp", cancellationToken);
                    results.Add(new GeneratedVariant(width, stored.StoredPath, stored.SizeBytes));
                }
                catch (AppKitFileRefusedException ex)
                {
                    // A rendition larger than the upload limit is skipped. The other widths still serve.
                    LogVariantRefused(width, ex.Message);
                }
                finally
                {
                    if (!ReferenceEquals(target, original))
                        target.Dispose();
                }
            }
        }
        catch
        {
            // Nothing records the renditions written so far, so they go too, even after a cancel.
            foreach (var variant in results)
                await store.DeleteAsync(variant.StoredPath, CancellationToken.None);
            throw;
        }

        LogGenerated(results.Count);
        return results;
    }

    private static bool IsQuarterTurn(SKEncodedOrigin origin) =>
        origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

    /// <summary>
    /// Draws a bitmap as an EXIF orientation says it is displayed, for all eight orientations with one
    /// matrix. Returns the bitmap itself for <see cref="SKEncodedOrigin.TopLeft"/>.
    /// </summary>
    internal static SKBitmap Orient(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        float w = bitmap.Width, h = bitmap.Height;
        // Each matrix maps a stored pixel (x, y) to where it is displayed: x' = a*x + b*y + c, y' = d*x + e*y + f.
        SKMatrix? matrix = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
            _ => null,
        };
        if (matrix is null)
            return bitmap;

        var oriented = IsQuarterTurn(origin)
            ? new SKBitmap(bitmap.Height, bitmap.Width, bitmap.ColorType, bitmap.AlphaType)
            : new SKBitmap(bitmap.Width, bitmap.Height, bitmap.ColorType, bitmap.AlphaType);
        using var canvas = new SKCanvas(oriented);
        using var source = SKImage.FromBitmap(bitmap);
        canvas.SetMatrix(matrix.Value);
        canvas.DrawImage(source, 0, 0, SKSamplingOptions.Default);
        return oriented;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the dimensions of {StoredPath}")]
    private partial void LogProbeFailed(Exception ex, string storedPath);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not decode image {StoredPath} for WebP renditions")]
    private partial void LogUndecodable(string storedPath);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Image {StoredPath} ({Width}x{Height}) is over the size cap, so it gets no WebP renditions")]
    private partial void LogTooLarge(string storedPath, int width, int height);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed writing WebP renditions for {StoredPath}")]
    private partial void LogGenerateFailed(Exception ex, string storedPath);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipped the {Width} px WebP rendition: {Reason}")]
    private partial void LogVariantRefused(int width, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Generated {Count} WebP renditions")]
    private partial void LogGenerated(int count);
}
