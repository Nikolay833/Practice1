using System;
using System.Collections.Generic;
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
        _capsTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _capsTimer.Tick += (_, _) => PollCaps();
        _dndTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _dndTimer.Tick += (_, _) => PollDnd();
        ApplySettings();
    }

    public override string Id => "indicator";
    public override int Priority => 290;
    public override PillActivityKind Kind => PillActivityKind.Hud;

    public override CompactContent? CreateCompact() => _content;

    /// <summary>Hides other Caps Lock popups while ours shows.</summary>
    public OsdSuppressor? Osd { get; set; }

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

    private long _capsChangedAt;

    /// <summary>
    /// Reads Caps Lock right now (a maker's Caps Lock popup just appeared) and says whether it changed in the last moment, so the
    /// popup can be hidden and the island's pill shown at the same time.
    /// </summary>
    public bool CapsJustChanged()
    {
        if (!_settings.Current.ShowCapsLockHud)
        {
            return false;
        }

        PollCaps();
        return Environment.TickCount64 - _capsChangedAt < 800;
    }

    private void PollCaps()
    {
        bool on = (NativeMethods.GetKeyState(NativeMethods.VK_CAPITAL) & 1) != 0;
        if (_caps is { } was && was != on)
        {
            _capsChangedAt = Environment.TickCount64;
            if (_settings.Current.ReplaceWindowsOsd)
            {
                Osd?.Suppress(this); // a laptop maker's Caps Lock popup, if there is one
            }

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

    /// <summary>The elements of one kind of pill, built once and repainted, so a quick on/off/on never rebuilds or resizes it.</summary>
    private sealed class Parts
    {
        public required FrameworkElement Icon;
        public required TextBlock State;
        public required CompactContent Content;
        public required Action<bool, Brush> Paint;
        public readonly ScaleTransform Scale = new(1, 1);
        public readonly RotateTransform Rotate = new(0);
        public readonly TranslateTransform Shift = new(0, 0);
    }

    private readonly Dictionary<IndicatorKind, Parts> _parts = new();
    private Parts? _active;
    private long _shownAt;

    private void Show(IndicatorKind kind, bool on)
    {
        Brush tone = kind switch
        {
            IndicatorKind.CapsLock => on ? Brushes.White : Secondary(),
            IndicatorKind.DoNotDisturb => on ? Frozen(Indigo) : Secondary(),
            _ => on ? Frozen(Color.FromRgb(0xFF, 0x45, 0x3A)) : Secondary(),
        };

        Parts parts = PartsFor(kind);
        parts.Paint(on, tone);
        parts.State.Text = on ? "On" : "Off";
        parts.State.Foreground = tone;

        // Toggled again while the pill is still up: the same pill just changes its word and colour and gives a small nudge,
        // instead of starting over (which looked like a glitch when Caps Lock was pressed a few times).
        bool again = _kind == kind && ReferenceEquals(_active, parts) && Environment.TickCount64 - _shownAt < ActivityHost.HudDuration.TotalMilliseconds;
        _kind = kind;
        _active = parts;
        _shownAt = Environment.TickCount64;
        _content = parts.Content;
        Animate(parts, again);
        RequestHud();
    }

    private Parts PartsFor(IndicatorKind kind)
    {
        if (_parts.TryGetValue(kind, out Parts? existing))
        {
            return existing;
        }

        (FrameworkElement icon, Action<bool, Brush> paint) = kind switch
        {
            IndicatorKind.CapsLock => CapsIcon(),
            IndicatorKind.DoNotDisturb => MoonIcon(),
            _ => BellIcon(),
        };

        string title = kind switch
        {
            IndicatorKind.CapsLock => "Caps Lock",
            IndicatorKind.DoNotDisturb => "Do Not Disturb",
            _ => "Silent Mode",
        };

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(HudParts.Label(title, 12.5, "TextBrush", FontWeights.SemiBold));
        var state = HudParts.Label("Off", 12.5, "TextBrush", FontWeights.SemiBold);
        state.Margin = new Thickness(6, 0, 0, 0);
        state.MinWidth = 24; // "On" and "Off" take the same room: the island never resizes between them
        state.TextTrimming = TextTrimming.None;
        right.Children.Add(state);

        var parts = new Parts
        {
            Icon = icon,
            State = state,
            Content = CompactContent.Of(HudParts.Cell(icon, 26), right, extraHeight: 4),
            Paint = paint,
        };
        icon.RenderTransformOrigin = kind == IndicatorKind.Silent ? new Point(0.5, 0.1) : new Point(0.5, 0.5);
        icon.RenderTransform = new TransformGroup { Children = { parts.Scale, parts.Rotate, parts.Shift } };
        _parts[kind] = parts;
        return parts;
    }

    // ---- Icons (drawn, so they look the same on every Windows version) -------------------------------

    private static (FrameworkElement, Action<bool, Brush>) CapsIcon()
    {
        var arrow = new Path
        {
            Data = Geometry.Parse("M7,0.8 L13.2,7.4 L9.6,7.4 L9.6,11 L4.4,11 L4.4,7.4 L0.8,7.4 Z"),
            StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Round,
        };
        var bar = new Rectangle { Width = 5.2, Height = 2, RadiusX = 1, RadiusY = 1, Margin = new Thickness(4.4, 13, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var grid = new Grid { Width = 14, Height = 15, Children = { arrow, bar } };
        return (grid, (on, tone) =>
        {
            arrow.Stroke = tone;
            arrow.Fill = on ? tone : Brushes.Transparent;
            bar.Fill = tone;
        });
    }

    private static (FrameworkElement, Action<bool, Brush>) MoonIcon()
    {
        var moon = new Path
        {
            Data = Geometry.Parse("M9.6,0.8 A7.2,7.2 0 1 0 15.2,11.2 A5.8,5.8 0 0 1 9.6,0.8 Z"),
            StrokeThickness = 1.4,
            StrokeLineJoin = PenLineJoin.Round,
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
        };
        return (moon, (on, tone) =>
        {
            moon.Stroke = tone;
            moon.Fill = on ? tone : Brushes.Transparent;
        });
    }

    private static (FrameworkElement, Action<bool, Brush>) BellIcon()
    {
        var bell = new Path
        {
            Data = Geometry.Parse("M8,1.2 C5.2,1.2 3.6,3.4 3.6,6 L3.6,9.4 L2,11.6 L14,11.6 L12.4,9.4 L12.4,6 C12.4,3.4 10.8,1.2 8,1.2 Z M6.2,12.8 A1.9,1.9 0 0 0 9.8,12.8 Z"),
        };

        // The slash, with a black gap around it so it reads on the bell.
        var gap = new Line { X1 = 2, Y1 = 1, X2 = 14.5, Y2 = 14.5, Stroke = Brushes.Black, StrokeThickness = 3.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        var slash = new Line { X1 = 2, Y1 = 1, X2 = 14.5, Y2 = 14.5, StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        var grid = new Grid { Width = 16, Height = 16, Children = { bell, gap, slash } };
        return (grid, (silent, tone) =>
        {
            bell.Fill = tone;
            slash.Stroke = tone;
            gap.Visibility = slash.Visibility = silent ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    // ---- Animation ----------------------------------------------------------------------------------

    private void Animate(Parts parts, bool again)
    {
        _scale = parts.Scale;
        _rotate = parts.Rotate;
        _shift = parts.Shift;
        if (AnimationSettings.IsOff)
        {
            _loop.Stop();
            _scale.ScaleX = _scale.ScaleY = 1;
            _rotate.Angle = 0;
            _shift.Y = 0;
            return;
        }

        // First showing: the icon pops in from small. Toggled again: a gentle nudge from where it is.
        _clock = again ? 0.35 : 0;
        _pop.Snap(again ? Math.Min(Math.Max(_pop.Value, 0.6), 0.88) : 0.4);
        AnimationSettings.Configure(_pop, 380, again ? 0.6 : 0.45);
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
