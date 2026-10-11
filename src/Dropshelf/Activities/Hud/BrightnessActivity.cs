using System;
using System.Management;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Dropshelf.Services;

namespace Dropshelf.Activities.Hud;

/// <summary>
/// Brightness HUD for laptop panels: <c>WmiMonitorBrightnessEvent</c> (root\wmi) reports changes made with the brightness keys
/// or the Windows slider. Desktops and external monitors have no such WMI class; then the activity stays silent and
/// <see cref="IsSupported"/> is false. The WMI work runs on a thread-pool thread; events are marshalled to the UI thread.
/// </summary>
internal sealed class BrightnessActivity : PillActivityBase, IDisposable
{
    private readonly SettingsService _settings;
    private readonly OsdSuppressor _osd;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly TextBlock _glyph;
    private readonly FrameworkElement _left;
    private readonly HudBar _bar;
    private ManagementEventWatcher? _watcher;
    private int _last = -1;
    private bool _disposed;

    public BrightnessActivity(SettingsService settings, OsdSuppressor osd)
    {
        _settings = settings;
        _osd = osd;
        _glyph = HudParts.Glyph("");
        _left = HudParts.Cell(_glyph, 22);
        _bar = new HudBar();
    }

    public override string Id => "brightness";
    public override int Priority => 290;
    public override PillActivityKind Kind => PillActivityKind.Hud;

    /// <summary>The machine reports brightness changes (known after <see cref="StartAsync"/>).</summary>
    public bool IsSupported { get; private set; }

    public override CompactContent? CreateCompact() => CompactContent.Of(_left, _bar, extraHeight: 4);

    public Task StartAsync() => Task.Run(() =>
    {
        try
        {
            int current = -1;
            using (var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE Active = TRUE"))
            using (ManagementObjectCollection results = searcher.Get())
            {
                foreach (ManagementBaseObject o in results)
                {
                    current = Convert.ToInt32(o["CurrentBrightness"]);
                    o.Dispose();
                    break;
                }
            }

            if (current < 0 || _disposed)
            {
                return; // no controllable internal panel
            }

            var watcher = new ManagementEventWatcher(new ManagementScope(@"root\wmi"), new WqlEventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
            watcher.EventArrived += OnEventArrived;
            watcher.Start();
            _watcher = watcher;
            IsSupported = true;
            _last = current;
        }
        catch (Exception ex)
        {
            Log.Info($"Brightness HUD unavailable: {ex.Message}");
        }
    });

    // WMI thread.
    private void OnEventArrived(object sender, EventArrivedEventArgs e)
    {
        try
        {
            int value = Convert.ToInt32(e.NewEvent["Brightness"]);
            _dispatcher.BeginInvoke(new Action(() => OnBrightness(value)));
        }
        catch (Exception ex)
        {
            Log.Error("Reading a brightness event failed", ex);
        }
    }

    private void OnBrightness(int percent)
    {
        if (_disposed || percent == _last)
        {
            return;
        }

        _last = percent;
        if (!_settings.Current.ShowBrightnessHud)
        {
            return;
        }

        _bar.Set(percent / 100.0, $"{percent}%");
        if (_settings.Current.ReplaceWindowsOsd)
        {
            _osd.Suppress(this);
        }

        RequestHud();
    }

    public void Dispose()
    {
        _disposed = true;
        ManagementEventWatcher? watcher = _watcher;
        _watcher = null;
        if (watcher != null)
        {
            // Stopping WMI watchers can block; do it off the UI thread.
            Task.Run(() =>
            {
                try
                {
                    watcher.EventArrived -= OnEventArrived;
                    watcher.Stop();
                    watcher.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Error("Stopping the brightness watcher failed", ex);
                }
            });
        }
    }
}
