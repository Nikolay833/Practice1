using System;
using System.IO;
using System.Security;
using System.Threading.Tasks;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace Dropshelf.Services;

/// <summary>
/// "Launch at startup". The portable exe uses HKCU\Software\Microsoft\Windows\CurrentVersion\Run. The MSIX build uses the
/// manifest's startup task (<see cref="TaskId"/>) instead: its Run key writes would be virtualized into the package, and Windows
/// lists startup tasks in Task Manager > Startup apps, where the user can turn them off (<see cref="StartupTaskState.DisabledByUser"/>);
/// an app can ask for it to be enabled again but not override that choice.
/// </summary>
internal static class StartupRegistration
{
    public const string TaskId = "DropshelfStartup";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Dropshelf";

    /// <summary>Last known state of the startup task, or null when not packaged or not read yet (see <see cref="RefreshPackagedAsync"/>).</summary>
    public static StartupTaskState? PackagedState { get; private set; }

    /// <summary>Raised on the thread that called <see cref="RefreshPackagedAsync"/> / <see cref="Set"/> (the UI thread) when <see cref="PackagedState"/> changed.</summary>
    public static event Action? PackagedStateChanged;

    public static bool IsEnabled()
    {
        if (PackageIdentity.IsPackaged)
        {
            return PackagedState is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        }

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
    /// copy that is not installed). Never throws, so uninstall always reaches shutdown. In the MSIX build it asks for the startup
    /// task to be enabled or disabled instead (asynchronously; <see cref="PackagedStateChanged"/> reports the outcome).
    /// </summary>
    public static void Set(bool enabled)
    {
        if (PackageIdentity.IsPackaged)
        {
            _ = SetPackagedAsync(enabled);
            return;
        }

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

    /// <summary>Reads the startup task's current state (it can be changed in Task Manager at any time).</summary>
    public static async Task RefreshPackagedAsync()
    {
        if (!PackageIdentity.IsPackaged)
        {
            return;
        }

        try
        {
            StartupTask task = await StartupTask.GetAsync(TaskId);
            Update(task.State);
        }
        catch (Exception ex)
        {
            Log.Error("Could not read the startup task", ex);
        }
    }

    private static async Task SetPackagedAsync(bool enabled)
    {
        try
        {
            StartupTask task = await StartupTask.GetAsync(TaskId);
            if (enabled)
            {
                // Disabled -> Enabled when allowed. DisabledByUser / DisabledByPolicy stay as they are: only the user can undo those.
                if (task.State == StartupTaskState.Disabled)
                {
                    Update(await task.RequestEnableAsync(), force: true);
                    return;
                }
            }
            else if (task.State is StartupTaskState.Enabled)
            {
                task.Disable();
                Update(StartupTaskState.Disabled, force: true);
                return;
            }

            Update(task.State, force: true); // tells Settings what really is the case, e.g. "still off in Task Manager"
        }
        catch (Exception ex)
        {
            Log.Error("Could not update the startup task", ex);
        }
    }

    private static void Update(StartupTaskState state, bool force = false)
    {
        if (PackagedState != state || force)
        {
            PackagedState = state;
            PackagedStateChanged?.Invoke();
        }
    }

    /// <summary>Is there a Run value, as a portable install writes it?</summary>
    public static bool HasPortableAutostart()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) is string;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Removes the Run value a portable install left behind (the MSIX build takes over). Best effort: package registry virtualization
    /// can keep a delete of a value from the real hive private to this app, in which case the portable entry survives.
    /// </summary>
    public static void RemovePortableAutostart()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Error("Could not remove the portable autostart entry", ex);
        }
    }
}
