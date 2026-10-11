using System;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace Dropshelf.Services;

/// <summary>"Launch at startup" via HKCU\Software\Microsoft\Windows\CurrentVersion\Run.</summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Dropshelf";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) is string;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Error("Could not read startup registration", ex);
            return false;
        }
    }

    /// <summary>
    /// Writes (or removes) the Run value: the quoted path of the installed exe plus <c>--autostart</c>. Re-running it keeps
    /// the value in sync if the exe path changed. Does nothing when there is no sensible exe to register (debug builds, or a
    /// copy that is not installed). Never throws, so uninstall always reaches shutdown.
    /// </summary>
    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled)
            {
                string? exe = Installer.AutostartTarget();
                if (!string.IsNullOrEmpty(exe))
                {
                    string command = $"\"{exe}\" {Installer.AutostartArgument}";
                    if (!string.Equals(key.GetValue(ValueName) as string, command, StringComparison.Ordinal))
                    {
                        key.SetValue(ValueName, command, RegistryValueKind.String);
                    }
                }
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not update startup registration", ex);
        }
    }
}
