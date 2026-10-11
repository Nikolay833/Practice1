using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dropshelf.Interop;
using Dropshelf.Services;

namespace Dropshelf.Shelf;

/// <summary>
/// Produces file icons (SHGetFileInfo) and image thumbnails on one dedicated STA thread so the UI never
/// blocks on the shell. Results are frozen, so they can be handed straight to the UI thread.
/// </summary>
internal static class IconLoader
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".ico"];
    private static readonly BlockingCollection<Action> Queue = new();

    static IconLoader()
    {
        var thread = new Thread(() =>
        {
            foreach (var work in Queue.GetConsumingEnumerable())
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    Log.Error("Icon worker failed", ex);
                }
            }
        })
        {
            IsBackground = true,
            Name = "Dropshelf.IconLoader",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public static Task<(ImageSource? Image, bool IsThumbnail)> LoadAsync(string path, bool isFolder)
    {
        var tcs = new TaskCompletionSource<(ImageSource?, bool)>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Add(() =>
        {
            try
            {
                if (!isFolder && IsImage(path))
                {
                    var thumb = LoadThumbnail(path);
                    if (thumb != null)
                    {
                        tcs.SetResult((thumb, true));
                        return;
                    }
                }

                tcs.SetResult((LoadShellIcon(path, isFolder), false));
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    private static bool IsImage(string path)
    {
        string ext = Path.GetExtension(path);
        return Array.Exists(ImageExtensions, e => e.Equals(ext, StringComparison.OrdinalIgnoreCase));
    }

    private static BitmapSource? LoadThumbnail(string path)
    {
        try
        {
            // Read through a stream so the file is never locked after decoding.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 160;
            bmp.StreamSource = stream;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or FileFormatException)
        {
            return null;
        }
    }

    private static BitmapSource? LoadShellIcon(string path, bool isFolder)
    {
        var info = new NativeMethods.SHFILEINFO();
        uint flags = NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON;
        uint attrs = 0;

        // Missing files still get a sensible type icon.
        if (!(isFolder ? Directory.Exists(path) : File.Exists(path)))
        {
            flags |= NativeMethods.SHGFI_USEFILEATTRIBUTES;
            attrs = isFolder ? NativeMethods.FILE_ATTRIBUTE_DIRECTORY : NativeMethods.FILE_ATTRIBUTE_NORMAL;
        }

        IntPtr result = NativeMethods.SHGetFileInfo(path, attrs, ref info, (uint)System.Runtime.InteropServices.Marshal.SizeOf(info), flags);
        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            NativeMethods.DestroyIcon(info.hIcon);
        }
    }
}
