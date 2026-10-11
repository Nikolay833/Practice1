using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Dropshelf.Activities.Hud;
using Dropshelf.Services;
using Windows.Devices.Enumeration;

namespace Dropshelf.Activities.Devices;

/// <summary>
/// Bluetooth HUD: a <see cref="DeviceWatcher"/> over paired Bluetooth (classic and LE) endpoints follows
/// <c>System.Devices.Aep.IsConnected</c>. When a device connects or disconnects the pill shows its icon (headphones, keyboard,
/// mouse, ... from the AEP category or the name), its name and, when Windows reports it, the battery level
/// (<c>DEVPKEY_Bluetooth_Battery</c>). The initial enumeration is silent. WinRT events arrive on other threads and are
/// marshalled to the UI thread; if the API fails to start the feature is simply off.
/// </summary>
internal sealed class BluetoothActivity : PillActivityBase, IDisposable
{
    private const string ConnectedKey = "System.Devices.Aep.IsConnected";
    private const string CategoryKey = "System.Devices.Aep.Category";
    private const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";

    // Bluetooth (classic) and Bluetooth LE association endpoint protocols, paired devices only.
    private const string Filter =
        "(System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\" OR System.Devices.Aep.ProtocolId:=\"{bb7bb05e-5972-42b5-94fc-76eaa7084d49}\")" +
        " AND System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#True";

    private sealed class Device
    {
        public string Name = string.Empty;
        public bool Connected;
        public string Category = string.Empty;
        public int Battery = -1;
        public DateTime LastNotice = DateTime.MinValue;
    }

    private readonly SettingsService _settings;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Dictionary<string, Device> _devices = new();
    private readonly TextBlock _glyph;
    private readonly FrameworkElement _left;
    private readonly TextBlock _name;
    private readonly TextBlock _detail;
    private readonly FrameworkElement _right;
    private DeviceWatcher? _watcher;
    private bool _enumerated;
    private string? _shownId;
    private bool _disposed;

    public BluetoothActivity(SettingsService settings)
    {
        _settings = settings;
        _glyph = HudParts.Glyph("", "AccentBrush", 16);
        _left = HudParts.Cell(_glyph, 24);
        _name = HudParts.Label(string.Empty, 12, "TextBrush", FontWeights.SemiBold);
        _name.MaxWidth = 150;
        _detail = HudParts.Label(string.Empty, 10.5, "TextSecondaryBrush");
        _detail.MaxWidth = 150;
        _right = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _name, _detail } };
    }

    public override string Id => "bluetooth";
    public override int Priority => 240;
    public override PillActivityKind Kind => PillActivityKind.Hud;

    public override CompactContent? CreateCompact() => CompactContent.Of(_left, _right, extraHeight: 6);

    public void Start()
    {
        try
        {
            DeviceWatcher watcher = DeviceInformation.CreateWatcher(
                Filter, new[] { ConnectedKey, CategoryKey, BatteryKey }, DeviceInformationKind.AssociationEndpoint);
            watcher.Added += (_, info) => Post(() => OnAdded(info));
            watcher.Updated += (_, update) => Post(() => OnUpdated(update));
            watcher.Removed += (_, update) => Post(() => _devices.Remove(update.Id));
            watcher.EnumerationCompleted += (_, _) => Post(() => _enumerated = true);
            watcher.Start();
            _watcher = watcher;
        }
        catch (Exception ex)
        {
            Log.Info($"Bluetooth HUD unavailable: {ex.Message}");
            _watcher = null;
        }
    }

    private void Post(Action action)
    {
        if (!_disposed)
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                if (_disposed)
                {
                    return;
                }

                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Log.Error("Handling a Bluetooth device event failed", ex);
                }
            }));
        }
    }

    private void OnAdded(DeviceInformation info)
    {
        var device = new Device { Name = info.Name };
        Merge(device, info.Properties);
        _devices[info.Id] = device;
    }

    private void OnUpdated(DeviceInformationUpdate update)
    {
        if (!_devices.TryGetValue(update.Id, out Device? device))
        {
            return;
        }

        bool wasConnected = device.Connected;
        int oldBattery = device.Battery;
        Merge(device, update.Properties);

        if (!_enumerated || !_settings.Current.ShowBluetoothHud)
        {
            return;
        }

        if (device.Connected != wasConnected)
        {
            if (DateTime.UtcNow - device.LastNotice < TimeSpan.FromSeconds(1.5))
            {
                return; // a flapping connection
            }

            device.LastNotice = DateTime.UtcNow;
            Present(update.Id, device);
        }
        else if (device.Battery != oldBattery && update.Id == _shownId)
        {
            Present(update.Id, device); // the battery level arrived just after the connection
        }
    }

    private static void Merge(Device device, IReadOnlyDictionary<string, object> properties)
    {
        foreach ((string key, object value) in properties)
        {
            if (value == null)
            {
                continue;
            }

            switch (key)
            {
                case ConnectedKey when value is bool connected:
                    device.Connected = connected;
                    break;
                case CategoryKey:
                    device.Category = value switch
                    {
                        string s => s,
                        IEnumerable<string> list => string.Join(' ', list),
                        _ => string.Empty,
                    };
                    break;
                case BatteryKey:
                    try
                    {
                        int level = Convert.ToInt32(value);
                        device.Battery = level is >= 0 and <= 100 ? level : -1;
                    }
                    catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
                    {
                        device.Battery = -1;
                    }

                    break;
            }
        }
    }

    private void Present(string id, Device device)
    {
        _shownId = id;
        _glyph.Text = GlyphFor(device);
        _glyph.SetResourceReference(TextBlock.ForegroundProperty, device.Connected ? "AccentBrush" : "TextSecondaryBrush");
        _name.Text = string.IsNullOrWhiteSpace(device.Name) ? "Bluetooth device" : device.Name;
        string state = device.Connected ? "Connected" : "Disconnected";
        _detail.Text = device.Connected && device.Battery >= 0 ? $"{state} · {device.Battery}%" : state;
        RequestHud();
    }

    /// <summary>Segoe MDL2 / Fluent: E7F6 headphones, E765 keyboard, E962 mouse, E7FC gamepad, E8EA phone, E702 Bluetooth.</summary>
    private static string GlyphFor(Device device)
    {
        string text = (device.Category + " " + device.Name).ToLowerInvariant();
        if (text.Contains("keyboard"))
        {
            return "";
        }

        if (text.Contains("mouse") || text.Contains("pointer") || text.Contains("trackpad"))
        {
            return "";
        }

        if (text.Contains("headphone") || text.Contains("headset") || text.Contains("earbud") || text.Contains("buds")
            || text.Contains("airpods") || text.Contains("audio") || text.Contains("speaker"))
        {
            return "";
        }

        if (text.Contains("gamepad") || text.Contains("controller") || text.Contains("game"))
        {
            return "";
        }

        if (text.Contains("phone"))
        {
            return "";
        }

        return "";
    }

    public void Dispose()
    {
        _disposed = true;
        try
        {
            if (_watcher != null && _watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
            {
                _watcher.Stop();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Stopping the Bluetooth watcher failed", ex);
        }
    }
}
