using System;
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
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled)
            {
                string? exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                {
                    key.SetValue(ValueName, $"\"{exe}\"", RegistryValueKind.String);
                }
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
        {
            Log.Error("Could not update startup registration", ex);
        }
    }
}
