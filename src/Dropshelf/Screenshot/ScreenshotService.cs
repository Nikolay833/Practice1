using System;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using Dropshelf.Activities.Hud;
using Dropshelf.Clipboard;
using Dropshelf.Services;
using Dropshelf.Tools;
using Drawing = System.Drawing;

namespace Dropshelf.Screenshot;

/// <summary>
/// Ctrl+Shift+S: pick a region (<see cref="CaptureOverlay"/>), grab it with GDI (<c>CopyFromScreen</c>, physical pixels), save it
/// as <c>Pictures\Screenshots\Dropshelf yyyy-MM-dd HH.mm.ss.png</c>, put the picture on the clipboard (flagged as our own copy so
/// the clipboard history skips it), add it to the shelf and show a HUD with a thumbnail and a "Copy text" (OCR) button.
/// </summary>
internal sealed class ScreenshotService
{
    private readonly Action<string> _addToShelf;
    private readonly ToastActivity _toast;

    public ScreenshotService(Action<string> addToShelf, ToastActivity toast)
    {
        _addToShelf = addToShelf;
        _toast = toast;
    }

    public static string ScreenshotsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures", "Screenshots");

    /// <summary>Starts a capture (UI thread). Does nothing while an overlay is already open.</summary>
    public async void Begin()
    {
        try
        {
            Drawing.Rectangle? region = await CaptureOverlay.PickAsync();
            if (region is not { } rect)
            {
                return;
            }

            byte[] png = await Task.Run(() => CapturePng(rect));
            string? path = await Task.Run(() => Save(png));
            if (path == null)
            {
                _toast.Show(new ToastSpec { Glyph = "", Tone = "WarnBrush", Text = "Could not save the screenshot" });
                return;
            }

            BitmapImage image = Decode(png);
            await CopyToClipboardAsync(image);
            _addToShelf(path);

            string captured = path;
            _toast.Show(new ToastSpec
            {
                Image = Thumbnail(png),
                Text = "Screenshot saved",
                ButtonText = "Copy text",
                ButtonClick = () => _ = OcrActions.CopyTextAsync([captured]),
                Duration = TimeSpan.FromSeconds(5),
                ExtraHeight = 6,
                Bounce = true,
            });
        }
        catch (Exception ex)
        {
            Log.Error("Screenshot failed", ex);
            _toast.Show(new ToastSpec { Glyph = "", Tone = "WarnBrush", Text = "Screenshot failed" });
        }
    }

    private static byte[] CapturePng(Drawing.Rectangle rect)
    {
        using var bitmap = new Drawing.Bitmap(rect.Width, rect.Height, PixelFormat.Format24bppRgb);
        using (Drawing.Graphics g = Drawing.Graphics.FromImage(bitmap))
        {
            // CaptureBlt also copies layered (transparent) windows.
            g.CopyFromScreen(rect.Left, rect.Top, 0, 0, rect.Size, Drawing.CopyPixelOperation.SourceCopy | Drawing.CopyPixelOperation.CaptureBlt);
        }

        using var ms = new MemoryStream();
        bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return ms.ToArray();
    }

    private static string? Save(byte[] png)
    {
        try
        {
            string dir = ScreenshotsDir;
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error("Could not create the Screenshots folder", ex);
            }

            using FileStream stream = OutputNaming.CreateUniqueFile(dir, $"Dropshelf {DateTime.Now:yyyy-MM-dd HH.mm.ss}", ".png", out string path);
            stream.Write(png, 0, png.Length);
            return path;
        }
        catch (Exception ex)
        {
            Log.Error("Could not save the screenshot", ex);
            return null;
        }
    }

    private static BitmapImage Decode(byte[] png, int decodeWidth = 0)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        if (decodeWidth > 0)
        {
            image.DecodePixelWidth = decodeWidth;
        }

        image.StreamSource = new MemoryStream(png);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static BitmapImage Thumbnail(byte[] png) => Decode(png, 96);

    private static async Task CopyToClipboardAsync(BitmapSource image)
    {
        var data = new DataObject();
        data.SetImage(image);
        await ClipboardAccess.TrySetDataObjectAsync(data); // marks the change as ours, so the history ignores it
    }
}
