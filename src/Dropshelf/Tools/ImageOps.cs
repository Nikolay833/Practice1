using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Dropshelf.Tools;

internal enum ImageFormat
{
    Png,
    Jpeg,
    Bmp,
    Tiff,
}

/// <summary>The image is bigger than the decoder is allowed to load (see <see cref="ImageOps.MaxPixels"/>); the message says so in plain words.</summary>
internal sealed class ImageTooLargeException(string message) : Exception(message);

/// <summary>One image operation: re-encode and/or scale. Everything is optional except the suffix that names the result.</summary>
internal sealed class ImageJob
{
    /// <summary>Goes into the result's name: "photo (converted).png".</summary>
    public required string Suffix { get; init; }

    /// <summary>Output format; null keeps the source's format (PNG for formats we cannot write, such as GIF, WebP, HEIC).</summary>
    public ImageFormat? Format { get; init; }

    public int JpegQuality { get; init; } = 90;

    /// <summary>Scale to this percentage of the original size (0 = off).</summary>
    public int ScalePercent { get; init; }

    /// <summary>Shrink to this width in pixels when wider (0 = off). Smaller images are skipped, never enlarged.</summary>
    public int MaxWidth { get; init; }
}

/// <summary>
/// Image conversion with WIC through System.Windows.Media.Imaging (so WebP / HEIC / AVIF can be read when Windows has the
/// codec installed). Runs on any thread. The EXIF orientation is applied to the pixels (the result is never rotated twice
/// and viewers that ignore the tag show it upright). Windows has no WebP encoder, so WebP can be read but not written.
/// </summary>
internal static class ImageOps
{
    private static readonly string[] Extensions =
        [".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".bmp", ".dib", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif", ".avif"];

    /// <summary>Largest image that is decoded: 100 megapixels (about 400 MB of pixels). Bigger ones are refused before decoding.</summary>
    public const long MaxPixels = 100_000_000;

    public static bool IsImage(string path) =>
        Array.Exists(Extensions, e => e.Equals(Path.GetExtension(path), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Converts <paramref name="source"/> and writes the result next to it (or to Downloads). Returns the new path, or null
    /// when the job does not apply (a width limit the image already satisfies). Throws when the image cannot be decoded.
    /// </summary>
    public static string? Run(string source, ImageJob job)
    {
        BitmapSource image = LoadOriented(source);
        int width = image.PixelWidth, height = image.PixelHeight;

        double factor = 1;
        if (job.ScalePercent > 0)
        {
            factor = job.ScalePercent / 100.0;
        }
        else if (job.MaxWidth > 0)
        {
            if (width <= job.MaxWidth)
            {
                return null;
            }

            factor = job.MaxWidth / (double)width;
        }

        if (factor < 0.9999 || factor > 1.0001)
        {
            int newWidth = Math.Max(1, (int)Math.Round(width * factor));
            int newHeight = Math.Max(1, (int)Math.Round(height * factor));
            image = new TransformedBitmap(image, new ScaleTransform(newWidth / (double)width, newHeight / (double)height));
        }

        ImageFormat format = job.Format ?? FormatOf(source);
        if (format == ImageFormat.Jpeg)
        {
            image = OverWhite(image);
        }

        BitmapEncoder encoder = format switch
        {
            ImageFormat.Jpeg => new JpegBitmapEncoder { QualityLevel = Math.Clamp(job.JpegQuality, 1, 100) },
            ImageFormat.Bmp => new BmpBitmapEncoder(),
            ImageFormat.Tiff => new TiffBitmapEncoder(),
            _ => new PngBitmapEncoder(),
        };
        encoder.Frames.Add(BitmapFrame.Create(image));

        string name = Path.GetFileNameWithoutExtension(source);
        string? dir = Path.GetDirectoryName(source);
        using FileStream output = OutputNaming.CreateUniqueFile(dir, $"{name} ({job.Suffix})", ExtensionOf(format), out string path);
        try
        {
            encoder.Save(output);
        }
        catch
        {
            output.Dispose();
            TryDelete(path);
            throw;
        }

        return path;
    }

    public static string ExtensionOf(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => ".jpg",
        ImageFormat.Bmp => ".bmp",
        ImageFormat.Tiff => ".tif",
        _ => ".png",
    };

    private static ImageFormat FormatOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".jpe" or ".jfif" => ImageFormat.Jpeg,
        ".bmp" or ".dib" => ImageFormat.Bmp,
        ".tif" or ".tiff" => ImageFormat.Tiff,
        _ => ImageFormat.Png, // PNG, and the read-only formats (GIF would lose its animation anyway)
    };

    /// <summary>Decodes the first frame (fully, so the file is released) and bakes the EXIF orientation into the pixels.</summary>
    private static BitmapSource LoadOriented(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        // Dimensions first: with DelayCreation and no cache only the header is read, so a huge image is refused before any pixels are decoded.
        BitmapDecoder probe = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        BitmapFrame header = probe.Frames[0];
        long pixels = (long)header.PixelWidth * header.PixelHeight;
        if (pixels > MaxPixels)
        {
            throw new ImageTooLargeException($"Image too large: {pixels / 1_000_000.0:0} MP (limit {MaxPixels / 1_000_000} MP)");
        }

        stream.Position = 0;
        BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapFrame frame = decoder.Frames[0];
        int orientation = ReadOrientation(frame);

        BitmapSource source = frame;
        // Flip first, then rotate (two TransformedBitmaps: one only takes a single flip or rotation).
        switch (orientation)
        {
            case 2: source = Flip(source, -1, 1); break;
            case 3: source = Rotate(source, 180); break;
            case 4: source = Flip(source, 1, -1); break;
            case 5: source = Rotate(Flip(source, -1, 1), 270); break;
            case 6: source = Rotate(source, 90); break;
            case 7: source = Rotate(Flip(source, -1, 1), 90); break;
            case 8: source = Rotate(source, 270); break;
        }

        return source;
    }

    private static BitmapSource Flip(BitmapSource s, double x, double y) => new TransformedBitmap(s, new ScaleTransform(x, y));

    private static BitmapSource Rotate(BitmapSource s, double angle) => new TransformedBitmap(s, new RotateTransform(angle));

    private static int ReadOrientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata metadata)
            {
                foreach (string query in new[] { "System.Photo.Orientation", "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
                {
                    if (metadata.ContainsQuery(query) && metadata.GetQuery(query) is { } value)
                    {
                        int orientation = Convert.ToInt32(value);
                        if (orientation is >= 1 and <= 8)
                        {
                            return orientation;
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException or FormatException or OverflowException)
        {
            // No metadata in this format: treat as upright.
        }

        return 1;
    }

    /// <summary>JPEG has no alpha: composite over white (the encoder would turn transparent pixels black).</summary>
    private static BitmapSource OverWhite(BitmapSource source)
    {
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int width = bgra.PixelWidth, height = bgra.PixelHeight;
        long stride = width * 4L;
        long length = stride * height; // long: width * height * 4 overflows an int above ~536 MP
        if (length > Array.MaxLength)
        {
            throw new ImageTooLargeException($"Image too large: {(long)width * height / 1_000_000.0:0} MP (limit {MaxPixels / 1_000_000} MP)");
        }

        var pixels = new byte[length];
        bgra.CopyPixels(pixels, (int)stride, 0);

        for (int i = 0; i < pixels.Length; i += 4)
        {
            int alpha = pixels[i + 3];
            if (alpha == 255)
            {
                continue;
            }

            pixels[i] = (byte)((pixels[i] * alpha + 255 * (255 - alpha)) / 255);
            pixels[i + 1] = (byte)((pixels[i + 1] * alpha + 255 * (255 - alpha)) / 255);
            pixels[i + 2] = (byte)((pixels[i + 2] * alpha + 255 * (255 - alpha)) / 255);
            pixels[i + 3] = 255;
        }

        BitmapSource flat = BitmapSource.Create(width, height, source.DpiX, source.DpiY, PixelFormats.Bgr32, null, pixels, (int)stride);
        flat.Freeze();
        return flat;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover empty file is harmless.
        }
    }
}
