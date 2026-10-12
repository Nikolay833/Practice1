using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Dropshelf.Activities.Hud;
using Dropshelf.Services;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace Dropshelf.Activities.Power;

/// <summary>
/// Battery HUD, like the iPhone's: plugging the charger in shows "Charging" and the level next to a drawn battery whose green
/// fill glides up from empty (the percentage counts along), a bolt pops in and a highlight sweeps across it. Unplugging shows
/// "On Battery", a low battery (20 % and 10 %) breathes red, and reaching 100 % on the charger says "Charged". State comes from
/// <see cref="WinForms.SystemInformation.PowerStatus"/>, read when <see cref="SystemEvents.PowerModeChanged"/> fires and every
/// 30 seconds. Machines without a battery never show anything.
/// </summary>
internal sealed class BatteryActivity : PillActivityBase, IDisposable
{
    private enum Mood { Plugged, Unplugged, Low, Critical, Full }

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly Color Green = Color.FromRgb(0x30, 0xD1, 0x58);
    private static readonly Color Red = Color.FromRgb(0xFF, 0x45, 0x3A);

    private readonly SettingsService _settings;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _pollTimer;
    private readonly TextBlock _status;
    private readonly FrameworkElement _left;
    private readonly TextBlock _percent;
    private readonly BatteryGlyph _battery;
    private readonly FrameworkElement _right;
    private bool _initialized;
    private bool _plugged;
    private bool _warned20, _warned10, _notifiedFull;
    private int _shownPercent;
    private bool _disposed;

    public BatteryActivity(SettingsService settings)
    {
        _settings = settings;
        _status = HudParts.Label(string.Empty, 12.5, "TextBrush", FontWeights.SemiBold);
        _left = _status;

        _percent = HudParts.Label(string.Empty, 13, "TextBrush", FontWeights.SemiBold);
        _percent.SetResourceReference(TextBlock.FontFamilyProperty, "RoundedFont");
        Typography.SetNumeralAlignment(_percent, FontNumeralAlignment.Tabular);
        _percent.TextTrimming = TextTrimming.None;
        _percent.MinWidth = 34;
        _percent.TextAlignment = TextAlignment.Right;
        _battery = new BatteryGlyph { Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        _battery.LevelChanged = level => _percent.Text = $"{Math.Min(_shownPercent, (int)Math.Round(level * 100))}%";
        var row = new StackPanel { Orientation = Orientation.Horizontal, Height = 24 };
        row.Children.Add(_percent);
        row.Children.Add(_battery);
        _right = row;

        _pollTimer = new DispatcherTimer { Interval = PollInterval };
        _pollTimer.Tick += (_, _) => Poll();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        Poll();
        _pollTimer.Start();
    }

    public override string Id => "battery";
    public override int Priority => 250;
    public override PillActivityKind Kind => PillActivityKind.Hud;

    public override CompactContent? CreateCompact() => CompactContent.Of(_left, _right, extraHeight: 4);

    // SystemEvents raises on its own thread.
    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.StatusChange && !_disposed)
        {
            _dispatcher.BeginInvoke(new Action(Poll));
        }
    }

    private void Poll()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            WinForms.PowerStatus ps = WinForms.SystemInformation.PowerStatus;
            WinForms.BatteryChargeStatus charge = ps.BatteryChargeStatus;
            bool hasBattery = charge != WinForms.BatteryChargeStatus.Unknown && (charge & WinForms.BatteryChargeStatus.NoSystemBattery) == 0;
            float fraction = ps.BatteryLifePercent;
            if (!hasBattery || fraction < 0 || fraction > 1)
            {
                if (!hasBattery)
                {
                    _pollTimer.Stop(); // a desktop: nothing to watch
                }

                return;
            }

            bool plugged = ps.PowerLineStatus == WinForms.PowerLineStatus.Online;
            int percent = (int)Math.Round(fraction * 100);

            if (percent > 25)
            {
                _warned20 = _warned10 = false;
            }

            if (percent < 95)
            {
                _notifiedFull = false;
            }

            if (!_initialized)
            {
                _initialized = true;
                _plugged = plugged;
                _warned20 = !plugged && percent <= 20; // no warning for a level that was already low at start-up
                _warned10 = !plugged && percent <= 10;
                _notifiedFull = plugged && percent >= 100;
                return;
            }

            if (plugged != _plugged)
            {
                _plugged = plugged;
                _warned20 = !plugged && percent <= 20;
                _warned10 = !plugged && percent <= 10;
                _notifiedFull = plugged && percent >= 100;
                Show(plugged ? Mood.Plugged : Mood.Unplugged, percent, plugged);
                return;
            }

            if (!plugged)
            {
                if (percent <= 10 && !_warned10)
                {
                    _warned10 = _warned20 = true;
                    Show(Mood.Critical, percent, plugged: false);
                }
                else if (percent <= 20 && !_warned20)
                {
                    _warned20 = true;
                    Show(Mood.Low, percent, plugged: false);
                }
            }
            else if (percent >= 100 && !_notifiedFull)
            {
                _notifiedFull = true;
                Show(Mood.Full, percent, plugged: true);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Reading the power status failed", ex);
            _pollTimer.Stop();
        }
    }

    private void Show(Mood mood, int percent, bool plugged)
    {
        if (!_settings.Current.ShowBatteryHud)
        {
            return;
        }

        _shownPercent = percent;
        (string text, string brush, Color tone) = mood switch
        {
            Mood.Plugged => ("Charging", "TextBrush", Green),
            Mood.Unplugged => ("On Battery", "TextSecondaryBrush", Colors.White),
            Mood.Low => ("Low Battery", "DangerBrush", Red),
            Mood.Critical => ("Battery Very Low", "DangerBrush", Red),
            _ => ("Charged", "SuccessBrush", Green),
        };
        _status.Text = text;
        _status.SetResourceReference(TextBlock.ForegroundProperty, brush);
        _percent.SetResourceReference(TextBlock.ForegroundProperty, mood is Mood.Low or Mood.Critical ? "DangerBrush" : mood == Mood.Unplugged ? "TextBrush" : "SuccessBrush");
        _percent.Text = $"{percent}%";
        _battery.Play(percent / 100.0, tone, charging: plugged, fromEmpty: mood == Mood.Plugged, pulse: mood is Mood.Low or Mood.Critical);
        RequestHud();
    }

    public void Dispose()
    {
        _disposed = true;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _pollTimer.Stop();
    }
}
