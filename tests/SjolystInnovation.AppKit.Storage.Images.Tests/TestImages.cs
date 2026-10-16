using SkiaSharp;

namespace SjolystInnovation.AppKit.Storage.Images.Tests;

/// <summary>Real images made with SkiaSharp for the tests.</summary>
internal static class TestImages
{
    /// <summary>A marker in the stored top-left quarter.</summary>
    public static readonly SKColor TopLeft = SKColors.Red;
    /// <summary>A marker in the stored top-right quarter.</summary>
    public static readonly SKColor TopRight = SKColors.Lime;
    /// <summary>The rest of the image.</summary>
    public static readonly SKColor Rest = SKColors.Blue;

    /// <summary>Draws an image with a red top-left and a green top-right quarter on blue.</summary>
    public static SKBitmap Marked(int width, int height)
    {
        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(Rest);
        using var red = new SKPaint { Color = TopLeft };
        using var green = new SKPaint { Color = TopRight };
        canvas.DrawRect(0, 0, width / 2f, height / 2f, red);
        canvas.DrawRect(width / 2f, 0, width / 2f, height / 2f, green);
        return bitmap;
    }

    public static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format, int quality = 90)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, quality);
        return data.ToArray();
    }

    public static byte[] Png(int width, int height)
    {
        using var bitmap = Marked(width, height);
        return Encode(bitmap, SKEncodedImageFormat.Png);
    }

    public static byte[] Jpeg(int width, int height, SKEncodedOrigin origin = SKEncodedOrigin.TopLeft)
    {
        using var bitmap = Marked(width, height);
        var jpeg = Encode(bitmap, SKEncodedImageFormat.Jpeg);
        return origin == SKEncodedOrigin.TopLeft ? jpeg : InsertExifOrientation(jpeg, origin);
    }

    /// <summary>A file with a PNG signature that no decoder can read.</summary>
    public static byte[] BrokenPng() => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. "this is not really an image"u8];

    /// <summary>Reads the size of an encoded image.</summary>
    public static (int Width, int Height) SizeOf(byte[] encoded)
    {
        using var bitmap = SKBitmap.Decode(encoded);
        return (bitmap.Width, bitmap.Height);
    }

    /// <summary>Decodes an image and reads the color in the middle of each quarter.</summary>
    public static (SKColor TopLeft, SKColor TopRight, SKColor BottomLeft, SKColor BottomRight) Quarters(byte[] encoded)
    {
        using var bitmap = SKBitmap.Decode(encoded);
        int x1 = bitmap.Width / 4, x2 = bitmap.Width * 3 / 4, y1 = bitmap.Height / 4, y2 = bitmap.Height * 3 / 4;
        return (bitmap.GetPixel(x1, y1), bitmap.GetPixel(x2, y1), bitmap.GetPixel(x1, y2), bitmap.GetPixel(x2, y2));
    }

    /// <summary>Checks whether two colors are the same, give or take lossy compression.</summary>
    public static bool Near(SKColor a, SKColor b) =>
        Math.Abs(a.Red - b.Red) < 60 && Math.Abs(a.Green - b.Green) < 60 && Math.Abs(a.Blue - b.Blue) < 60;

    /// <summary>
    /// Splices an EXIF APP1 segment with one orientation tag in after the SOI marker. Skia writes no EXIF of
    /// its own.
    /// </summary>
    public static byte[] InsertExifOrientation(byte[] jpeg, SKEncodedOrigin origin)
    {
        // A little-endian TIFF header with one IFD entry: tag 0x0112 (orientation), type SHORT.
        var tiff = new List<byte>();
        tiff.AddRange("II"u8.ToArray());
        tiff.AddRange(BitConverter.GetBytes((ushort)42));
        tiff.AddRange(BitConverter.GetBytes(8u));
        tiff.AddRange(BitConverter.GetBytes((ushort)1));
        tiff.AddRange(BitConverter.GetBytes((ushort)0x0112));
        tiff.AddRange(BitConverter.GetBytes((ushort)3));
        tiff.AddRange(BitConverter.GetBytes(1u));
        tiff.AddRange(BitConverter.GetBytes((ushort)origin));
        tiff.AddRange(BitConverter.GetBytes((ushort)0));
        tiff.AddRange(BitConverter.GetBytes(0u));

        var payload = new List<byte>("Exif"u8.ToArray()) { 0, 0 };
        payload.AddRange(tiff);
        var length = payload.Count + 2;
        var segment = new List<byte> { 0xFF, 0xE1, (byte)(length >> 8), (byte)(length & 0xFF) };
        segment.AddRange(payload);

        var withExif = new List<byte>(jpeg[..2]);
        withExif.AddRange(segment);
        withExif.AddRange(jpeg[2..]);
        return withExif.ToArray();
    }
}
