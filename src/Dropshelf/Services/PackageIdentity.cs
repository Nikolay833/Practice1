using System;
using System.Diagnostics;
using System.ComponentModel;
using System.Text;
using Dropshelf.Interop;

namespace Dropshelf.Services;

/// <summary>
/// Is this process running with package identity (installed from the MSIX)? A packaged app does not self-install, does not
/// write the Run key (the manifest's startup task does that job), cannot be uninstalled from its own tray menu and is
/// relaunched through the shell instead of by path. A portable exe has no identity, which also keeps the WinRT APIs that
/// need one (notification listener, appointment store) switched off.
/// </summary>
internal static class PackageIdentity
{
    /// <summary>The application Id in <c>packaging/Package.appxmanifest</c>.</summary>
    private const string ApplicationId = "App";

    static PackageIdentity()
    {
        IsPackaged = Detect(out string? family);
        FamilyName = family;
    }

    public static bool IsPackaged { get; }

    /// <summary>Package family name (e.g. <c>Dropshelf_8wekyb3d8bbwe</c>), or null for a plain exe.</summary>
    public static string? FamilyName { get; }

    /// <summary>The AppUserModelID of the packaged app: <c>FamilyName!App</c>.</summary>
    public static string? AppUserModelId => FamilyName == null ? null : FamilyName + "!" + ApplicationId;

    private static bool Detect(out string? family)
    {
        family = null;
        try
        {
            uint length = 0;
            int rc = NativeMethods.GetCurrentPackageFullName(ref length, null);
            if (rc != NativeMethods.ERROR_INSUFFICIENT_BUFFER && rc != 0)
            {
                return false; // APPMODEL_ERROR_NO_PACKAGE: a plain exe
            }

            uint familyLength = 0;
            if (NativeMethods.GetCurrentPackageFamilyName(ref familyLength, null) == NativeMethods.ERROR_INSUFFICIENT_BUFFER)
            {
                var buffer = new StringBuilder((int)familyLength);
                if (NativeMethods.GetCurrentPackageFamilyName(ref familyLength, buffer) == 0)
                {
                    family = buffer.ToString();
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return false; // Windows older than 8: no package model at all
        }
    }

    /// <summary>
    /// Starts a fresh copy of the packaged app (crash relaunch): through the activation manager, which can pass arguments, and
    /// failing that through <c>explorer.exe shell:AppsFolder\FamilyName!App</c>, which cannot.
    /// </summary>
    public static bool Relaunch(string arguments)
    {
        string? aumid = AppUserModelId;
        if (aumid == null)
        {
            return false;
        }

        if (AppActivation.TryActivate(aumid, arguments))
        {
            return true;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", @"shell:AppsFolder\" + aumid) { UseShellExecute = false });
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Log.Error("Could not relaunch the packaged app", ex);
            return false;
        }
    }
}
