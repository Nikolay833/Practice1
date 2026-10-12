using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Dropshelf.Animation;
using Dropshelf.Interop;
using Dropshelf.Services;
using Microsoft.Win32;

namespace Dropshelf.Activities.Hud;

/// <summary>
/// Small "state changed" HUDs, like the iPhone's Silent Mode pill: Caps Lock on / off, Do Not Disturb on / off and Silent Mode
/// (the sound muted) on / off. Each shows a drawn icon that animates in (the bell rings, the moon swings in, the arrow rises)
/// and the state in its colour. Caps Lock is read every 150 ms and Do Not Disturb every 2 s, with cheap calls; the first
/// reading is silent, so nothing shows at start-up.
/// </summary>
internal sealed class IndicatorActivity : PillActivityBase, IDisposable
{
    public enum IndicatorKind { CapsLock, DoNotDisturb, Silent }

    private const string DndKey = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";
    private const string DndValue = "NOC_GLOBAL_SETTING_TOASTS_ENABLED";

    private static readonly Color Indigo = Color.FromRgb(0x5E, 0x5C, 0xE6);   // the Focus colour

    private readonly SettingsService _settings;
    private readonly DispatcherTimer _capsTimer;
    private readonly DispatcherTimer _dndTimer;
    private readonly FrameLoop _loop;
    private readonly Spring _pop = new(1, 380, 0.45);
    private bool? _caps;
    private bool? _dnd;
    private CompactContent? _content;
    private IndicatorKind _kind;
    private double _clock;
    private ScaleTransform? _scale;
    private RotateTransform? _rotate;
    private TranslateTransform? _shift;

    public IndicatorActivity(SettingsService settings)
    {
        _settings = settings;
        _loop = new FrameLoop(OnFrame);
        _capsTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        _capsTimer.Tick += (_, _) => PollCaps();
        _dndTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _dndTimer.Tick += (_, _) => PollDnd();
        ApplySettings();
    }

    public override string Id => "indicator";
    public override int Priority => 290;
    public override PillActivityKind Kind => PillActivityKind.Hud;

    public override CompactContent? CreateCompact() => _content;

    /// <summary>Starts or stops the watchers to match Settings.</summary>
    public void ApplySettings()
    {
        if (_settings.Current.ShowCapsLockHud)
        {
            _caps = null;
            PollCaps();
            _capsTimer.Start();
        }
        else
        {
            _capsTimer.Stop();
        }

        if (_settings.Current.ShowFocusHud)
        {
            _dnd = null;
            PollDnd();
            _dndTimer.Start();
        }
        else
        {
            _dndTimer.Stop();
        }
    }

    private void PollCaps()
    {
        bool on = (NativeMethods.GetKeyState(NativeMethods.VK_CAPITAL) & 1) != 0;
        if (_caps is { } was && was != on)
        {
            Show(IndicatorKind.CapsLock, on);
        }

        _caps = on;
    }

    /// <summary>Windows 11's Do Not Disturb (also switched on by a Focus session) turns the toast setting off.</summary>
    private void PollDnd()
    {
        bool on;
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(DndKey);
            on = key?.GetValue(DndValue) is int enabled && enabled == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            _dndTimer.Stop();
            return;
        }

        if (_dnd is { } was && was != on)
        {
            Show(IndicatorKind.DoNotDisturb, on);
        }

        _dnd = on;
    }

    /// <summary>The sound was muted or unmuted (from the volume watcher).</summary>
    public void ShowSilent(bool muted)
    {
        if (_settings.Current.ShowSilentHud)
        {
            Show(IndicatorKind.Silent, muted);
        }
    }

    public bool SilentEnabled => _settings.Current.ShowSilentHud;

    private void Show(IndicatorKind kind, bool on)
    {
        _kind = kind;
        (string title, Brush tone) = kind switch
        {
            IndicatorKind.CapsLock => ("Caps Lock", on ? Brushes.White : Secondary()),
            IndicatorKind.DoNotDisturb => ("Do Not Disturb", on ? Frozen(Indigo) : Secondary()),
            _ => ("Silent Mode", on ? Frozen(Color.FromRgb(0xFF, 0x45, 0x3A)) : Secondary()),
        };

        FrameworkElement icon = kind switch
        {
            IndicatorKind.CapsLock => CapsIcon(on, tone),
            IndicatorKind.DoNotDisturb => MoonIcon(on, tone),
            _ => BellIcon(on, tone),
        };

        var left = HudParts.Cell(icon, 26);
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(HudParts.Label(title, 12.5, "TextBrush", FontWeights.SemiBold));
        var state = HudParts.Label(on ? "On" : "Off", 12.5, "TextBrush", FontWeights.SemiBold);
        state.Foreground = tone;
        state.Margin = new Thickness(6, 0, 0, 0);
        right.Children.Add(state);
        _content = CompactContent.Of(left, right, extraHeight: 4);

        Animate(icon);
        RequestHud();
    }

    // ---- Icons (drawn, so they look the same on every Windows version) -------------------------------

    private FrameworkElement CapsIcon(bool on, Brush tone)
    {
        var arrow = new Path
        {
            Data = Geometry.Parse("M7,0.8 L13.2,7.4 L9.6,7.4 L9.6,11 L4.4,11 L4.4,7.4 L0.8,7.4 Z"),
            Stroke = tone,
            StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Round,
            Fill = on ? tone : Brushes.Transparent,
        };
        var bar = new Rectangle { Width = 5.2, Height = 2, RadiusX = 1, RadiusY = 1, Fill = tone, Margin = new Thickness(4.4, 13, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var grid = new Grid { Width = 14, Height = 15 };
        grid.Children.Add(arrow);
        grid.Children.Add(bar);
        return grid;
    }

    private static FrameworkElement MoonIcon(bool on, Brush tone) => new Path
    {
        Data = Geometry.Parse("M9.6,0.8 A7.2,7.2 0 1 0 15.2,11.2 A5.8,5.8 0 0 1 9.6,0.8 Z"),
        Fill = on ? tone : Brushes.Transparent,
        Stroke = tone,
        StrokeThickness = 1.4,
        StrokeLineJoin = PenLineJoin.Round,
        Width = 16,
        Height = 16,
        Stretch = Stretch.Uniform,
    };

    private static FrameworkElement BellIcon(bool silent, Brush tone)
    {
        var grid = new Grid { Width = 16, Height = 16 };
        grid.Children.Add(new Path
        {
            Data = Geometry.Parse("M8,1.2 C5.2,1.2 3.6,3.4 3.6,6 L3.6,9.4 L2,11.6 L14,11.6 L12.4,9.4 L12.4,6 C12.4,3.4 10.8,1.2 8,1.2 Z M6.2,12.8 A1.9,1.9 0 0 0 9.8,12.8 Z"),
            Fill = tone,
        });
        if (silent)
        {
            // The slash, with a black gap around it so it reads on the bell.
            grid.Children.Add(new Line { X1 = 2, Y1 = 1, X2 = 14.5, Y2 = 14.5, Stroke = Brushes.Black, StrokeThickness = 3.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
            grid.Children.Add(new Line { X1 = 2, Y1 = 1, X2 = 14.5, Y2 = 14.5, Stroke = tone, StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        }

        return grid;
    }

    // ---- Animation ----------------------------------------------------------------------------------

    private void Animate(FrameworkElement icon)
    {
        _scale = new ScaleTransform(1, 1);
        _rotate = new RotateTransform(0);
        _shift = new TranslateTransform(0, 0);
        icon.RenderTransformOrigin = _kind == IndicatorKind.Silent ? new Point(0.5, 0.1) : new Point(0.5, 0.5);
        icon.RenderTransform = new TransformGroup { Children = { _scale, _rotate, _shift } };
        if (AnimationSettings.IsOff)
        {
            _loop.Stop();
            return;
        }

        _clock = 0;
        _pop.Snap(0.4);
        AnimationSettings.Configure(_pop, 380, 0.45);
        _pop.Target = 1;
        _loop.Start();
    }

    private bool OnFrame(double dt)
    {
        _clock += dt;
        _pop.Step(dt);
        double s = Math.Max(0, _pop.Value);
        if (_scale != null)
        {
            _scale.ScaleX = _scale.ScaleY = s;
        }

        double t = _clock / AnimationSettings.TimeScale;
        switch (_kind)
        {
            case IndicatorKind.Silent when _rotate != null:
                _rotate.Angle = 24 * Math.Exp(-4.2 * t) * Math.Sin(t * 22); // the bell rings and settles
                break;
            case IndicatorKind.DoNotDisturb when _rotate != null:
                _rotate.Angle = -50 * Math.Exp(-6 * t) * Math.Cos(t * 7); // the moon swings in
                break;
            case IndicatorKind.CapsLock when _shift != null:
                _shift.Y = 5 * Math.Exp(-7 * t) * Math.Cos(t * 9); // the arrow rises into place
                break;
        }

        bool done = _pop.SettleIfClose(0.002, 0.02) && t > 1.2;
        if (done)
        {
            if (_scale != null)
            {
                _scale.ScaleX = _scale.ScaleY = 1;
            }

            if (_rotate != null)
            {
                _rotate.Angle = 0;
            }

            if (_shift != null)
            {
                _shift.Y = 0;
            }
        }

        return !done;
    }

    private static Brush Secondary() => Frozen(Color.FromArgb(0x99, 0xEB, 0xEB, 0xF5));

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public void Dispose()
    {
        _capsTimer.Stop();
        _dndTimer.Stop();
        _loop.Stop();
    }
}
