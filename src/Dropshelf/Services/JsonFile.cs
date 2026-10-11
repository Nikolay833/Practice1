using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Dropshelf.Services;

/// <summary>
/// Atomic JSON load/save helpers that swallow IO and parse errors (and log them). A save goes to a temp file that is flushed
/// to disk and then moved over the target, so a power loss leaves either the old or the new file, never an empty one.
/// </summary>
internal static class JsonFile
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static T? Load<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream, Options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            Log.Error($"Failed to read {path}", ex);
            return null;
        }
    }

    public static bool Save<T>(string path, T value) =>
        WriteAtomic(path, () => JsonSerializer.SerializeToUtf8Bytes(value, Options));

    // Extra entropy so the blob is not decryptable by arbitrary other DPAPI callers running as the same user.
    private static readonly byte[] ProtectedEntropy = System.Text.Encoding.UTF8.GetBytes("Dropshelf.JsonFile.v1");

    /// <summary>Like <see cref="Save{T}"/> but encrypts the JSON with DPAPI (current user), so it is not readable at rest.</summary>
    public static bool SaveProtected<T>(string path, T value) =>
        WriteAtomic(path, () =>
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(value, Options);
            return ProtectedData.Protect(json, ProtectedEntropy, DataProtectionScope.CurrentUser);
        });

    /// <summary>
    /// Loads a file written by <see cref="SaveProtected{T}"/>. If it cannot be decrypted it is tried as legacy plain
    /// JSON (<paramref name="wasPlain"/> is then true so the caller can re-save it encrypted); otherwise null.
    /// </summary>
    public static T? LoadProtected<T>(string path, out bool wasPlain) where T : class
    {
        wasPlain = false;
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            byte[] bytes = File.ReadAllBytes(path);
            try
            {
                byte[] json = ProtectedData.Unprotect(bytes, ProtectedEntropy, DataProtectionScope.CurrentUser);
                return JsonSerializer.Deserialize<T>(json, Options);
            }
            catch (CryptographicException)
            {
                // Not an encrypted blob (older plain-text file) or not ours / other user: try plain JSON.
                var plain = JsonSerializer.Deserialize<T>(bytes, Options);
                wasPlain = plain != null;
                return plain;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or CryptographicException)
        {
            Log.Error($"Failed to read {path}", ex);
            return null;
        }
    }

    private static bool WriteAtomic(string path, Func<byte[]> produce)
    {
        string tmp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            byte[] bytes = produce();
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true); // the data must be on disk before the rename makes it the real file
            }

            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            // Serialization can fail with more than IO errors (e.g. invalid UTF-16 text); never let a save crash the app.
            Log.Error($"Failed to write {path}", ex);
            try
            {
                File.Delete(tmp);
            }
            catch (Exception)
            {
                // Best effort: a stale temp file is overwritten by the next save.
            }

            return false;
        }
    }

    /// <summary>
    /// Renames a file that exists but could not be read (DPAPI failure, empty or truncated) to <c>name.bak-yyyyMMddHHmmss</c>, so the
    /// next save cannot overwrite what might still be recoverable. Returns the backup path, or null when there was nothing to move.
    /// </summary>
    public static string? Quarantine(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            string backup = $"{path}.bak-{DateTime.Now:yyyyMMddHHmmss}";
            File.Move(path, backup, overwrite: true);
            Log.Error($"{Path.GetFileName(path)} could not be read; kept as {Path.GetFileName(backup)}");
            return backup;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Could not set aside the unreadable {path}", ex);
            return null;
        }
    }

    public static void Delete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Failed to delete {path}", ex);
        }
    }
}
