using System;
using System.IO;
using System.Linq;
using System.Windows;
using Dropshelf.Services;

namespace Dropshelf.Items;

/// <summary>Turns drag-and-drop payloads (files, text, links) into items and decides which drags we accept.</summary>
internal static class DropHelper
{
    private const string UrlFormatW = "UniformResourceLocatorW";

    public static bool CanAccept(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) ||
        data.GetDataPresent(DataFormats.UnicodeText) ||
        data.GetDataPresent(DataFormats.Text) ||
        data.GetDataPresent(UrlFormatW);

    /// <summary>
    /// Effect to report for a drag that started in one of our own stores: Move when the source allows it, otherwise
    /// Copy (file and folder items are offered as copy-only). Accept transfers the items either way.
    /// </summary>
    public static DragDropEffects InternalEffect(DragEventArgs e) =>
        e.AllowedEffects.HasFlag(DragDropEffects.Move) ? DragDropEffects.Move : DragDropEffects.Copy;

    /// <summary>
    /// Adds whatever is in <paramref name="data"/> to <paramref name="target"/>.
    /// Drags that started in another Dropshelf store are transferred (moved); drags from the same store are ignored.
    /// </summary>
    public static int Accept(IDataObject data, ItemStore target)
    {
        try
        {
            if (DragState.IsActive)
            {
                var origin = DragState.Origin!;
                if (ReferenceEquals(origin, target))
                {
                    return 0;
                }

                var moved = DragState.Items.ToList();
                origin.Remove(moved, deleteOwnedFiles: false);
                target.AddRange(moved);
                return moved.Count;
            }

            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files)
            {
                return target.AddPaths(files);
            }

            string? url = ReadUrl(data);
            if (url != null)
            {
                return target.AddUrl(url) != null ? 1 : 0;
            }

            string? text = (data.GetData(DataFormats.UnicodeText) ?? data.GetData(DataFormats.Text)) as string;
            if (!string.IsNullOrWhiteSpace(text))
            {
                string trimmed = text.Trim();
                bool looksLikeUrl = !trimmed.Contains('\n') && Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
                                    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeFtp);
                return (looksLikeUrl ? target.AddUrl(trimmed) : target.AddText(text)) != null ? 1 : 0;
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or InvalidOperationException)
        {
            Log.Error("Drop failed", ex);
        }

        return 0;
    }

    /// <summary>Reads the browser "link" drag format (a UTF-16 string in a stream), if present.</summary>
    private static string? ReadUrl(IDataObject data)
    {
        if (!data.GetDataPresent(UrlFormatW))
        {
            return null;
        }

        try
        {
            if (data.GetData(UrlFormatW) is MemoryStream ms)
            {
                string s = System.Text.Encoding.Unicode.GetString(ms.ToArray()).TrimEnd('\0').Trim();
                // Saved links are shell-launched on double-click, so only web links are accepted.
                return Uri.TryCreate(s, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                    ? s
                    : null;
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException)
        {
            // Fall back to plain text handling.
        }

        return null;
    }
}
