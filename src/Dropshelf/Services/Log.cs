using System;
using System.Diagnostics;
using System.IO;

namespace Dropshelf.Services;

/// <summary>Tiny append-only file logger; never throws.</summary>
internal static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        Debug.WriteLine($"[Dropshelf] {level} {message}");
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.Root);
                var info = new FileInfo(AppPaths.LogFile);
                if (info.Exists && info.Length > 512 * 1024)
                {
                    info.Delete();
                }

                File.AppendAllText(AppPaths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // Logging must never break the app.
        }
    }
}
