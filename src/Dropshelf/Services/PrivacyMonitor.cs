using System;
using System.IO;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Dropshelf.Services;

/// <summary>
/// Knows when an app is using the camera or the microphone, the same way the Windows privacy icon in the taskbar does:
/// Windows records each app's use under <c>HKCU\...\CapabilityAccessManager\ConsentStore\{webcam|microphone}</c>, with a start
/// time and a stop time that stays 0 while the app is still using it. The keys are read every 1.5 s (a few dozen small
/// registry reads); nothing is ever written.
/// </summary>
internal sealed class PrivacyMonitor : IDisposable
{
    private const string StoreKey = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";

    private readonly DispatcherTimer _timer;

    public PrivacyMonitor()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1500) };
        _timer.Tick += (_, _) => Poll();
    }

    public bool CameraInUse { get; private set; }
    public bool MicrophoneInUse { get; private set; }

    /// <summary>The app using the camera / microphone ("Zoom", "WindowsCamera"), or empty.</summary>
    public string CameraApp { get; private set; } = string.Empty;
    public string MicrophoneApp { get; private set; } = string.Empty;

    /// <summary>Camera or microphone use started or stopped (UI thread).</summary>
    public event Action? Changed;

    public bool Enabled
    {
        get => _timer.IsEnabled;
        set
        {
            if (value)
            {
                Poll();
                _timer.Start();
            }
            else
            {
                _timer.Stop();
                if (CameraInUse || MicrophoneInUse)
                {
                    CameraInUse = MicrophoneInUse = false;
                    CameraApp = MicrophoneApp = string.Empty;
                    Changed?.Invoke();
                }
            }
        }
    }

    private void Poll()
    {
        string camApp = InUseBy("webcam");
        string micApp = InUseBy("microphone");
        bool cam = camApp.Length > 0, mic = micApp.Length > 0;
        if (cam != CameraInUse || mic != MicrophoneInUse || camApp != CameraApp || micApp != MicrophoneApp)
        {
            CameraInUse = cam;
            MicrophoneInUse = mic;
            CameraApp = camApp;
            MicrophoneApp = micApp;
            Changed?.Invoke();
        }
    }

    /// <summary>The name of an app that is using <paramref name="capability"/> right now, or empty.</summary>
    private static string InUseBy(string capability)
    {
        try
        {
            using RegistryKey? root = Registry.CurrentUser.OpenSubKey(StoreKey + capability);
            if (root == null)
            {
                return string.Empty;
            }

            foreach (string name in root.GetSubKeyNames())
            {
                if (name == "NonPackaged")
                {
                    using RegistryKey? desktop = root.OpenSubKey(name);
                    if (desktop == null)
                    {
                        continue;
                    }

                    foreach (string app in desktop.GetSubKeyNames())
                    {
                        using RegistryKey? key = desktop.OpenSubKey(app);
                        if (IsActive(key))
                        {
                            return Path.GetFileNameWithoutExtension(app.Replace('#', '\\'));
                        }
                    }
                }
                else
                {
                    using RegistryKey? key = root.OpenSubKey(name);
                    if (IsActive(key))
                    {
                        // "Microsoft.WindowsCamera_8wekyb3d8bbwe" -> "WindowsCamera"
                        string family = name.Split('_')[0];
                        int dot = family.LastIndexOf('.');
                        return dot >= 0 ? family[(dot + 1)..] : family;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Not readable here: report nothing.
        }

        return string.Empty;
    }

    private static bool IsActive(RegistryKey? key) =>
        key != null && key.GetValue("LastUsedTimeStart") is long start && start > 0
        && key.GetValue("LastUsedTimeStop") is long stop && stop == 0;

    public void Dispose() => _timer.Stop();
}
