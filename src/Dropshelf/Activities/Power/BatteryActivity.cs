using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Dropshelf.Animation;
using Dropshelf.Activities.Hud;
using Dropshelf.Services;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace Dropshelf.Activities.Power;

/// <summary>
/// Battery HUD: plugging in or unplugging the charger shows the charge level (with a pulsing bolt while charging), a low
/// battery warning appears at 20 % and 10 % (the glyph pulses red) and a "fully charged" notice when it reaches 100 % on
/// the charger. State comes from <see cref="WinForms.SystemInformation.PowerStatus"/>, read when
/// <see cref="SystemEvents.PowerModeChanged"/> fires and every 30 seconds. Machines without a battery never show anything.
/// </summary>
internal sealed class BatteryActivity : PillActivityBase, IDisposable
{
    private enum Mood { Plugged, Unplugged, Low, Critical, Full }

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly SettingsService _settings;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _pollTimer;
    private readonly TextBlock _glyph;
    private readonly FrameworkElement _left;
    private readonly TextBlock _bolt;
    private readonly TextBlock _percent;
    private readonly TextBlock _status;
    private readonly FrameworkElement _right;
    private readonly FrameLoop _pulseLoop;
    private readonly ScaleTransform _boltScale = new(1, 1) ;
    private bool _initialized;
    private bool _plugged;
    private bool _warned20, _warned10, _notifiedFull;
    private Mood _mood;
    private double _pulseClock;
    private bool _disposed;

    public BatteryActivity(SettingsService settings)
    {
        _settings = settings;
        _pulseLoop = new FrameLoop(OnPulse);

        _glyph = HudParts.Glyph("");
        _left = HudParts.Cell(_glyph, 22);

        _bolt = HudParts.Glyph("", "AccentBrush", 12);
        _bolt.RenderTransform = _boltScale;
        _bolt.RenderTransformOrigin = new Point(0.5, 0.5);
        _percent = HudParts.Label(string.Empty, 13, "TextBrush", FontWeights.SemiBold);
        _status = HudParts.Label(string.Empty, 11, "TextSecondaryBrush");
        _status.Margin = new Thickness(6, 0, 0, 0);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Height = 24 };
        row.Children.Add(_bolt);
        row.Children.Add(_percent);
        row.Children.Add(_status);
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

        _mood = mood;
        int level = Math.Min(10, percent / 10);
        _glyph.Text = level >= 10 ? "" : ((char)(0xE850 + level)).ToString();
        string tone = mood is Mood.Low or Mood.Critical ? "DangerBrush" : mood == Mood.Full ? "AccentBrush" : "TextBrush";
        _glyph.SetResourceReference(TextBlock.ForegroundProperty, tone);
        _bolt.Visibility = plugged ? Visibility.Visible : Visibility.Collapsed;
        _percent.Text = $"{percent}%";
        _status.Text = mood switch
        {
            Mood.Plugged => "Charging",
            Mood.Unplugged => "On battery",
            Mood.Low => "Low battery",
            Mood.Critical => "Very low",
            _ => "Fully charged",
        };

        _pulseClock = 0;
        if (!AnimationSettings.IsOff)
        {
            _pulseLoop.Start();
        }
        else
        {
            ResetPulse();
        }

        RequestHud();
    }

    // Charging: the bolt breathes. Low / very low: the glyph pulses red (faster when critical).
    private bool OnPulse(double dt)
    {
        _pulseClock += dt;
        if (_pulseClock > ActivityHost.HudDuration.TotalSeconds + 0.3)
        {
            ResetPulse();
            return false;
        }

        double wave = 0.5 + 0.5 * Math.Sin(_pulseClock * (_mood == Mood.Critical ? 11 : 7));
        if (_mood is Mood.Low or Mood.Critical)
        {
            _glyph.Opacity = 0.35 + 0.65 * wave;
        }
        else
        {
            double s = 1 + 0.25 * wave;
            _boltScale.ScaleX = s;
            _boltScale.ScaleY = s;
            _bolt.Opacity = 0.6 + 0.4 * wave;
        }

        return true;
    }

    private void ResetPulse()
    {
        _glyph.Opacity = 1;
        _bolt.Opacity = 1;
        _boltScale.ScaleX = _boltScale.ScaleY = 1;
    }

    public void Dispose()
    {
        _disposed = true;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _pollTimer.Stop();
        _pulseLoop.Stop();
    }
}
