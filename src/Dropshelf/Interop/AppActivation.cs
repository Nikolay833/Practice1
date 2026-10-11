using System;
using System.Runtime.InteropServices;

namespace Dropshelf.Interop;

[ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
internal class ApplicationActivationManagerComObject
{
}

/// <summary>Only the first vtable slot (ActivateApplication) is declared; the other two methods are never called.</summary>
[ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IApplicationActivationManager
{
    [PreserveSig]
    int ActivateApplication(
        [In, MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
        [In, MarshalAs(UnmanagedType.LPWStr)] string? arguments,
        [In] int options,
        [Out] out uint processId);
}

/// <summary>
/// Starts an app by its AppUserModelID through the shell's activation manager. Works for packaged apps (the MSIX build
/// relaunching itself with <c>--restarted</c>) and for any app that registered an AUMID (clicking a notification on the pill).
/// </summary>
internal static class AppActivation
{
    /// <summary>Returns true when Windows accepted the activation (the app may still take a moment to appear).</summary>
    public static bool TryActivate(string appUserModelId, string? arguments = null)
    {
        if (string.IsNullOrWhiteSpace(appUserModelId))
        {
            return false;
        }

        object? com = null;
        try
        {
            com = new ApplicationActivationManagerComObject();
            int hr = ((IApplicationActivationManager)com).ActivateApplication(appUserModelId, arguments, 0, out _);
            return hr >= 0;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            if (com != null && Marshal.IsComObject(com))
            {
                Marshal.FinalReleaseComObject(com);
            }
        }
    }
}
