using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Dropshelf.Interop;

namespace Dropshelf.Services;

/// <summary>
/// The default playback device: master volume and mute (read, set, change notifications) and the live peak level, through
/// Core Audio COM interop. Create it on the UI thread and call it from there; <see cref="VolumeChanged"/> is raised on the
/// UI thread. If COM setup fails, <see cref="IsAvailable"/> is false and everything quietly does nothing. When the default
/// device changes (headphones plugged in...) it re-binds to the new one.
/// </summary>
internal sealed class AudioEndpointService : IDisposable
{
    private const int ERender = 0, EMultimedia = 1;
    private const int ClsctxAll = 23;
    private static Guid _context = new("D5C2F8A1-6B37-4E0C-9F1E-7A2B5C3D8E41"); // marks changes we made ourselves

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private IMMDeviceEnumerator? _enumerator;
    private NotificationClient? _notifications;
    private IMMDevice? _device;
    private IAudioEndpointVolume? _volume;
    private IAudioMeterInformation? _meter;
    private VolumeCallback? _callback;
    private bool _disposed;

    public AudioEndpointService()
    {
        try
        {
            _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            _notifications = new NotificationClient(this);
            _enumerator.RegisterEndpointNotificationCallback(_notifications);
            Bind();
        }
        catch (Exception ex)
        {
            Log.Error("Core Audio is unavailable; volume HUD and audio levels are disabled", ex);
            ReleaseDevice();
        }
    }

    /// <summary>A playback device with volume control is bound.</summary>
    public bool IsAvailable => _volume != null;

    /// <summary>A peak meter is available on the bound device.</summary>
    public bool HasMeter => _meter != null;

    /// <summary>Raised on the UI thread with the new master volume (0..1) and mute state, whoever changed them.</summary>
    public event Action<float, bool>? VolumeChanged;

    /// <summary>Master volume 0..1 (0 when unavailable).</summary>
    public float Volume
    {
        get
        {
            if (_volume != null && Succeeded(_volume.GetMasterVolumeLevelScalar(out float v)))
            {
                return v;
            }

            return 0;
        }
    }

    public bool Muted
    {
        get
        {
            if (_volume != null && Succeeded(_volume.GetMute(out bool m)))
            {
                return m;
            }

            return false;
        }
    }

    public void SetVolume(float level)
    {
        if (_volume == null)
        {
            return;
        }

        try
        {
            level = Math.Clamp(level, 0f, 1f);
            _volume.SetMasterVolumeLevelScalar(level, ref _context);
            if (level > 0 && Muted)
            {
                _volume.SetMute(false, ref _context); // raising the volume from a muted state unmutes, like the volume keys
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException)
        {
            Log.Error("SetMasterVolumeLevelScalar failed", ex);
        }
    }

    public void AdjustVolume(float delta) => SetVolume(Volume + delta);

    /// <summary>The volume keys' step: 2 % up or down, landing on whole 2 % steps like Windows does.</summary>
    public void StepVolume(int direction) => SetVolume((float)(Math.Round(Volume * 50) + direction) / 50f);

    public void SetMuted(bool muted)
    {
        if (_volume == null)
        {
            return;
        }

        try
        {
            _volume.SetMute(muted, ref _context);
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException)
        {
            Log.Error("SetMute failed", ex);
        }
    }

    /// <summary>Peak output level 0..1 right now (0 when there is no meter or the call fails).</summary>
    public float PeakLevel()
    {
        if (_meter == null)
        {
            return 0;
        }

        try
        {
            return Succeeded(_meter.GetPeakValue(out float peak)) ? Math.Clamp(peak, 0f, 1f) : 0f;
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException)
        {
            return 0;
        }
    }

    private static bool Succeeded(int hr) => hr >= 0;

    private void Bind()
    {
        ReleaseDevice();
        if (_enumerator == null || !Succeeded(_enumerator.GetDefaultAudioEndpoint(ERender, EMultimedia, out IMMDevice device)))
        {
            return; // no playback device right now
        }

        _device = device;
        Guid volumeIid = CoreAudioGuids.AudioEndpointVolume;
        if (Succeeded(device.Activate(ref volumeIid, ClsctxAll, IntPtr.Zero, out object volumeObject)))
        {
            _volume = (IAudioEndpointVolume)volumeObject;
            _callback = new VolumeCallback(this);
            _volume.RegisterControlChangeNotify(_callback);
        }

        Guid meterIid = CoreAudioGuids.AudioMeterInformation;
        if (Succeeded(device.Activate(ref meterIid, ClsctxAll, IntPtr.Zero, out object meterObject)))
        {
            _meter = (IAudioMeterInformation)meterObject;
        }
    }

    private void ReleaseDevice()
    {
        try
        {
            if (_volume != null && _callback != null)
            {
                _volume.UnregisterControlChangeNotify(_callback);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException)
        {
            // Device already gone.
        }

        _callback = null;
        Release(ref _volume);
        Release(ref _meter);
        Release(ref _device);
    }

    private static void Release<T>(ref T? com) where T : class
    {
        if (com != null)
        {
            try
            {
                Marshal.ReleaseComObject(com);
            }
            catch (ArgumentException)
            {
                // Not a COM object.
            }

            com = null;
        }
    }

    // Called on a COM thread.
    private void OnNotify(float volume, bool muted)
    {
        if (_disposed)
        {
            return;
        }

        _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            if (!_disposed)
            {
                VolumeChanged?.Invoke(volume, muted);
            }
        }));
    }

    // Called on a COM thread.
    private void OnDefaultDeviceChanged()
    {
        if (_disposed)
        {
            return;
        }

        _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                Bind();
            }
            catch (Exception ex)
            {
                Log.Error("Re-binding the default playback device failed", ex);
            }
        }));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (_enumerator != null && _notifications != null)
            {
                _enumerator.UnregisterEndpointNotificationCallback(_notifications);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException)
        {
            // Ignore on shutdown.
        }

        ReleaseDevice();
        Release(ref _enumerator);
    }

    private sealed class VolumeCallback : IAudioEndpointVolumeCallback
    {
        private readonly AudioEndpointService _owner;

        public VolumeCallback(AudioEndpointService owner) => _owner = owner;

        public int OnNotify(IntPtr notifyData)
        {
            try
            {
                if (notifyData != IntPtr.Zero)
                {
                    var data = Marshal.PtrToStructure<AudioVolumeNotificationData>(notifyData);
                    _owner.OnNotify(data.fMasterVolume, data.bMuted != 0);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Volume callback failed", ex);
            }

            return 0;
        }
    }

    private sealed class NotificationClient : IMMNotificationClient
    {
        private readonly AudioEndpointService _owner;

        public NotificationClient(AudioEndpointService owner) => _owner = owner;

        public int OnDeviceStateChanged(string deviceId, int newState) => 0;
        public int OnDeviceAdded(string deviceId) => 0;
        public int OnDeviceRemoved(string deviceId) => 0;
        public int OnPropertyValueChanged(string deviceId, PropertyKey key) => 0;

        public int OnDefaultDeviceChanged(int flow, int role, string defaultDeviceId)
        {
            if (flow == ERender && role == EMultimedia)
            {
                _owner.OnDefaultDeviceChanged();
            }

            return 0;
        }
    }
}
