using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dropshelf.Services;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Dropshelf.Screenshot;

internal enum OcrStatus
{
    Ok,

    /// <summary>The picture holds no readable text.</summary>
    Empty,

    /// <summary>Windows has no OCR language pack for the user's languages.</summary>
    NoEngine,

    Failed,
}

internal sealed record OcrOutcome(OcrStatus Status, string Text);

/// <summary>Text recognition with the Windows OCR engine (<c>Windows.Media.Ocr</c>), for the languages in the user's profile.</summary>
internal static class OcrService
{
    public const string NoEngineMessage = "No OCR language pack installed (Settings > Time & language > Language)";

    /// <summary>Recognizes the text of an image file. Lines are joined with newlines.</summary>
    public static async Task<OcrOutcome> RecognizeFileAsync(string path)
    {
        try
        {
            OcrEngine? engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine == null)
            {
                return new OcrOutcome(OcrStatus.NoEngine, string.Empty);
            }

            byte[] bytes = await File.ReadAllBytesAsync(path);
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                writer.DetachStream();
            }

            stream.Seek(0);
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);

            // The engine refuses images larger than MaxImageDimension: let the decoder scale them down.
            var transform = new BitmapTransform();
            double largest = Math.Max(decoder.PixelWidth, decoder.PixelHeight);
            if (largest > OcrEngine.MaxImageDimension)
            {
                double scale = OcrEngine.MaxImageDimension / largest;
                transform.ScaledWidth = Math.Max(1, (uint)(decoder.PixelWidth * scale));
                transform.ScaledHeight = Math.Max(1, (uint)(decoder.PixelHeight * scale));
            }

            using SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);

            OcrResult result = await engine.RecognizeAsync(bitmap);
            string text = string.Join("\n", result.Lines.Select(l => l.Text)).Trim();
            return text.Length == 0 ? new OcrOutcome(OcrStatus.Empty, string.Empty) : new OcrOutcome(OcrStatus.Ok, text);
        }
        catch (Exception ex)
        {
            Log.Error($"OCR failed for {path}", ex);
            return new OcrOutcome(OcrStatus.Failed, string.Empty);
        }
    }
}
