using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Dropshelf.Services;

namespace Dropshelf.Clipboard;

/// <summary>
/// Clipboard access with retries (the clipboard is a shared resource and throws COMException while another
/// process holds it) and a "we did this ourselves" marker so our own writes never become history entries.
/// </summary>
internal static class ClipboardAccess
{
    private const int Attempts = 6;
    private const int SelfChangeWindowMs = 500;

    private static long _selfChangeUntil;

    /// <summary>True shortly after Dropshelf itself wrote to the clipboard.</summary>
    public static bool IsSelfChange => Environment.TickCount64 < Interlocked.Read(ref _selfChangeUntil);

    private static void MarkSelfChange() =>
        Interlocked.Exchange(ref _selfChangeUntil, Environment.TickCount64 + SelfChangeWindowMs);

    /// <summary>Sets the clipboard, retrying while it is locked without blocking the UI thread between attempts.</summary>
    public static async Task<bool> TrySetDataObjectAsync(IDataObject data)
    {
        for (int i = 0; i < Attempts; i++)
        {
            MarkSelfChange();
            try
            {
                System.Windows.Clipboard.SetDataObject(data, copy: true);
                MarkSelfChange();
                return true;
            }
            catch (ExternalException)
            {
                await Task.Delay(40);
            }
        }

        Log.Error("Clipboard stayed locked; could not set data");
        return false;
    }

    /// <summary>Runs a clipboard read, retrying while the clipboard is locked. Returns default on failure.</summary>
    public static async Task<T?> ReadAsync<T>(Func<T?> read)
    {
        for (int i = 0; i < Attempts; i++)
        {
            try
            {
                return read();
            }
            catch (ExternalException)
            {
                await Task.Delay(40);
            }
        }

        return default;
    }
}
