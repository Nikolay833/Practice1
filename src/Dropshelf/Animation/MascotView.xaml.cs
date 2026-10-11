using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Dropshelf.Interop;
using Dropshelf.Themes;

namespace Dropshelf.Animation;

/// <summary>
/// "Drop", the blue teardrop mascot: a procedural 3D mesh whose head follows the cursor (damped springs, limited to
/// +-25 degrees), blinks, looks around and breathes while idle, bounces with wide eyes when files are dragged near and
/// squashes in a "gulp" when something lands on the shelf.
/// Frames are only produced while something moves: <see cref="FrameLoop"/> unsubscribes from CompositionTarget.Rendering
/// when every spring has settled, and a slow timer wakes it for the next idle blink / cursor movement.
/// </summary>
public partial class MascotView : UserControl
{
    private const double ViewSize = 44;
    private const double CameraDistance = 6;
    private const double FieldOfViewDeg = 25;
    private const double MaxAngleDeg = 25;
    private const double NearRadiusDip = 450;     // beyond this the drop stops following the cursor
    private const double EyeAngle = 0.5;          // radians either side of the front
    private const double EyeHeight = -0.12;       // on the model's y axis
    private const double EyeWidthUnits = 0.26;
    private const double EyeHeightUnits = 0.44;
    private const double BlinkClosedSeconds = 0.09;
    private const double BlinkTotalSeconds = 0.26;
    private const double BreathSeconds = 1.6;
    private const double ExcitedBounceSeconds = 0.85;

    private static readonly Random Rng = new();

    private readonly Spring _yaw = new(0, 90, 0.55);
    private readonly Spring _pitch = new(0, 90, 0.55);
    private readonly Spring _squash = new(1, 240, 0.3);
    private readonly Spring _hop = new(0, 260, 0.3);
    private readonly Spring _open = new(1, 900, 0.9);
    private readonly Spring _wide = new(1, 300, 0.6);

    private readonly FrameLoop _loop;
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _idleTimer;

    private readonly ScaleTransform3D _scale = new() { CenterY = -1 };
    private readonly AxisAngleRotation3D _yawRotation = new(new Vector3D(0, 1, 0), 0);
    private readonly AxisAngleRotation3D _pitchRotation = new(new Vector3D(1, 0, 0), 0);
    private readonly TranslateTransform3D _move = new();
    private readonly double _eyeRadius = TeardropMesh.RadiusAtHeight(EyeHeight);
    private readonly double _focalLength = (ViewSize / 2) / Math.Tan(FieldOfViewDeg * Math.PI / 360);

    private double _cursorYaw, _cursorPitch;     // degrees, from the cursor
    private bool _cursorNear;
    private bool _excited;
    private double _excitedClock;
    private double _blinkClock = -1;
    private bool _doubleBlink;
    private double _breathClock = -1;
    private double _scriptClock = -1;
    private int _lastX = int.MinValue, _lastY = int.MinValue;
    private int _quietPolls;
    private bool _started;

    public MascotView()
    {
        InitializeComponent();

        Body.Geometry = TeardropMesh.Create();
        var group = new Transform3DGroup();
        group.Children.Add(_scale);
        group.Children.Add(new RotateTransform3D(_pitchRotation));
        group.Children.Add(new RotateTransform3D(_yawRotation));
        group.Children.Add(_move);
        Body.Transform = group;

        _loop = new FrameLoop(Tick);
        _pollTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        _pollTimer.Tick += (_, _) => PollCursor();
        _idleTimer = new DispatcherTimer(DispatcherPriority.Background);
        _idleTimer.Tick += (_, _) => OnIdleTick();

        Loaded += (_, _) =>
        {
            if (ThemeService.Current is { } theme)
            {
                theme.Changed -= ApplyAccent;
                theme.Changed += ApplyAccent;
            }

            ApplyAccent();
            Start();
        };
        Unloaded += (_, _) =>
        {
            if (ThemeService.Current is { } theme)
            {
                theme.Changed -= ApplyAccent;
            }

            Stop();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                Start();
            }
            else
            {
                Stop();
            }
        };
        ApplyPose();
    }

    // ---- Public behaviour -----------------------------------------------------------------------

    /// <summary>Re-reads the animation mode (call after the setting changes).</summary>
    public void Refresh()
    {
        Stop();
        ResetPose();
        if (IsLoaded && IsVisible)
        {
            Start();
        }
    }

    /// <summary>Files are being dragged near: bounce and widen the eyes. Pass false when they leave.</summary>
    public void SetExcited(bool excited)
    {
        if (_excited == excited)
        {
            return;
        }

        _excited = excited;
        if (AnimationSettings.IsOff)
        {
            return;
        }

        _wide.Target = excited ? 1.3 : 1;
        AnimationSettings.Configure(_wide, 300, 0.6);
        if (excited)
        {
            _excitedClock = 0;
            Bounce();
        }

        EnsureLoop();
    }

    /// <summary>Something was dropped on the shelf: swallow it with a quick squash and a squint.</summary>
    public void Gulp()
    {
        if (AnimationSettings.IsOff || !IsVisible)
        {
            return;
        }

        AnimationSettings.Configure(_squash, 240, 0.3);
        _squash.Target = 1;
        _squash.Displace(0.64);
        _hop.Velocity -= 1.5;
        _blinkClock = 0;
        EnsureLoop();
    }

    // ---- Accent ---------------------------------------------------------------------------------

    /// <summary>
    /// Tints the body, rim light, emissive glow and drop shadow from the accent (or the default blue when the mascot is set
    /// not to follow it). The shades are derived with the same hue/saturation steps that turn the default blue #3B82F6 into
    /// the original tip #7DB2FF and belly #3B5BFF.
    /// </summary>
    private void ApplyAccent()
    {
        System.Windows.Media.Color accent = ThemeService.Current?.MascotAccent ?? ThemeService.DefaultAccent;
        System.Windows.Media.Color tip = ColorUtil.Tip(accent);
        System.Windows.Media.Color deep = ColorUtil.Deep(accent);
        System.Windows.Media.Color glow = ColorUtil.Glow(accent);

        StopTip.Color = ColorUtil.WithAlpha(tip, 0xEB);
        StopMid.Color = ColorUtil.WithAlpha(accent, 0xEB);
        StopBase.Color = ColorUtil.WithAlpha(deep, 0xEB);
        RimLight.Color = glow;
        double tint = accent == ThemeService.DefaultAccent ? 0 : 0.35; // the default keeps its original ambient light
        Ambient.Color = ColorUtil.Mix(System.Windows.Media.Color.FromRgb(0x4A, 0x57, 0x84), ColorUtil.Mix(Colors.Black, accent, 0.45), tint);
        Emissive.Brush = new System.Windows.Media.SolidColorBrush(ColorUtil.WithAlpha(deep, 0x50));
        Glow.Color = deep;
    }

    // ---- Lifecycle ------------------------------------------------------------------------------

    private void Start()
    {
        if (_started || AnimationSettings.IsOff)
        {
            return;
        }

        _started = true;
        _pollTimer.Start();
        ScheduleIdle();
        EnsureLoop();
    }

    private void Stop()
    {
        _started = false;
        _pollTimer.Stop();
        _idleTimer.Stop();
        _loop.Stop();
    }

    private void ResetPose()
    {
        _yaw.Snap(0);
        _pitch.Snap(0);
        _squash.Snap(1);
        _hop.Snap(0);
        _open.Snap(1);
        _wide.Snap(_excited && !AnimationSettings.IsOff ? 1.3 : 1);
        _cursorYaw = _cursorPitch = 0;
        _cursorNear = false;
        _blinkClock = _breathClock = _scriptClock = -1;
        ApplyPose();
    }

    private void EnsureLoop()
    {
        if (_started && !_loop.IsRunning)
        {
            _loop.Start();
        }
    }

    // ---- Cursor and idle scheduling -------------------------------------------------------------

    private void PollCursor()
    {
        if (!IsVisible || !NativeMethods.GetCursorPos(out var p))
        {
            return;
        }

        bool moved = p.X != _lastX || p.Y != _lastY;
        _lastX = p.X;
        _lastY = p.Y;

        // Poll slowly while the mouse rests.
        if (moved)
        {
            _quietPolls = 0;
            _pollTimer.Interval = TimeSpan.FromMilliseconds(120);
        }
        else if (++_quietPolls > 25)
        {
            _pollTimer.Interval = TimeSpan.FromMilliseconds(400);
        }

        if (!AnimationSettings.IsFull)
        {
            return; // Subtle / Off: the head does not follow the cursor
        }

        double dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
        Point center = PointToScreen(new Point(ViewSize / 2, ViewSize / 2));
        double dx = p.X - center.X;
        double dy = p.Y - center.Y;
        bool near = Math.Sqrt(dx * dx + dy * dy) <= NearRadiusDip * dpi;

        double yaw = 0, pitch = 0;
        if (near)
        {
            yaw = MaxAngleDeg * Math.Tanh(dx / (220 * dpi));
            pitch = MaxAngleDeg * Math.Tanh(-dy / (220 * dpi));
        }

        if (near != _cursorNear || Math.Abs(yaw - _cursorYaw) > 0.4 || Math.Abs(pitch - _cursorPitch) > 0.4)
        {
            _cursorNear = near;
            _cursorYaw = yaw;
            _cursorPitch = pitch;
            EnsureLoop();
        }
    }

    private void ScheduleIdle()
    {
        _idleTimer.Interval = TimeSpan.FromSeconds(3 + Rng.NextDouble() * 3);
        _idleTimer.Start();
    }

    private void OnIdleTick()
    {
        _idleTimer.Stop();
        if (IsVisible && !AnimationSettings.IsOff)
        {
            _blinkClock = 0;
            _doubleBlink = Rng.NextDouble() < 0.15;
            _breathClock = 0;
            if (AnimationSettings.IsFull && !_cursorNear && !_excited && Rng.NextDouble() < 0.3)
            {
                _scriptClock = 0; // look left, then right
            }

            EnsureLoop();
        }

        ScheduleIdle();
    }

    private void Bounce()
    {
        AnimationSettings.Configure(_hop, 260, 0.3);
        _hop.Velocity = 6.5;
    }

    // ---- Per-frame update -----------------------------------------------------------------------

    private bool Tick(double dt)
    {
        if (AnimationSettings.IsOff)
        {
            ResetPose();
            return false;
        }

        // Blink: closed briefly, then open again; an optional second blink follows.
        if (_blinkClock >= 0)
        {
            _blinkClock += dt;
            _open.Target = _blinkClock < BlinkClosedSeconds ? 0.06 : 1;
            if (_blinkClock > BlinkTotalSeconds)
            {
                _blinkClock = -1;
                if (_doubleBlink)
                {
                    _doubleBlink = false;
                    _blinkClock = 0;
                }
            }
        }
        else
        {
            _open.Target = 1;
        }

        double breath = 0;
        if (_breathClock >= 0)
        {
            _breathClock += dt;
            double u = _breathClock / BreathSeconds;
            if (u >= 1)
            {
                _breathClock = -1;
            }
            else
            {
                breath = Math.Sin(u * Math.PI);
            }
        }

        double scriptYaw = 0, scriptPitch = 0;
        if (_scriptClock >= 0)
        {
            _scriptClock += dt;
            if (_scriptClock < 0.5)
            {
                scriptYaw = -20;
                scriptPitch = 4;
            }
            else if (_scriptClock < 1.2)
            {
                scriptYaw = 20;
                scriptPitch = 4;
            }
            else if (_scriptClock > 1.6)
            {
                _scriptClock = -1;
            }
        }

        if (_excited)
        {
            _excitedClock += dt;
            if (_excitedClock > ExcitedBounceSeconds)
            {
                _excitedClock = 0;
                Bounce();
            }
        }

        _yaw.Target = Math.Clamp((_cursorNear ? _cursorYaw : 0) + scriptYaw, -MaxAngleDeg, MaxAngleDeg);
        _pitch.Target = Math.Clamp((_cursorNear ? _cursorPitch : 0) + scriptPitch, -MaxAngleDeg, MaxAngleDeg);
        AnimationSettings.Configure(_yaw, 90, 0.55);
        AnimationSettings.Configure(_pitch, 90, 0.55);

        _yaw.Step(dt);
        _pitch.Step(dt);
        _squash.Step(dt);
        _hop.Step(dt);
        _open.Step(dt);
        _wide.Step(dt);

        ApplyPose(breath);

        bool busy = _blinkClock >= 0 || _breathClock >= 0 || _scriptClock >= 0 || _excited;
        bool settled = _yaw.SettleIfClose(0.02, 0.2) & _pitch.SettleIfClose(0.02, 0.2) & _squash.SettleIfClose(0.001, 0.02)
                       & _hop.SettleIfClose(0.001, 0.02) & _open.SettleIfClose(0.005, 0.05) & _wide.SettleIfClose(0.002, 0.02);
        if (!busy && settled)
        {
            ApplyPose();
            return false;
        }

        return true;
    }

    /// <summary>Pushes the spring values into the 3D transforms, the eyes and the glow.</summary>
    private void ApplyPose(double breath = 0)
    {
        double stretch = 1 + Math.Clamp(_hop.Velocity * 0.03, -0.12, 0.18);
        double sy = _squash.Value * stretch * (1 + 0.018 * breath);
        double sx = 1 / Math.Sqrt(Math.Max(sy, 0.2)); // keep the volume roughly constant
        double lift = _hop.Value + 0.04 * breath;

        _scale.ScaleX = sx;
        _scale.ScaleY = sy;
        _scale.ScaleZ = sx;
        _yawRotation.Angle = _yaw.Value;
        _pitchRotation.Angle = -_pitch.Value; // positive pitch = looking up = tilting the front toward +Y
        _move.OffsetY = lift;

        PlaceEye(EyeLeft, -EyeAngle, sx, sy, lift);
        PlaceEye(EyeRight, EyeAngle, sx, sy, lift);

        Glow.Opacity = 0.32 + Math.Clamp((_wide.Value - 1) / 0.3, 0, 1.2) * 0.4;
    }

    /// <summary>
    /// Projects an eye that sits on the front of the body (at angle <paramref name="side"/> around the Y axis) through
    /// the same scale, pitch and yaw as the 3D body, then through the camera, and sizes it by how much it faces us.
    /// </summary>
    private void PlaceEye(System.Windows.Shapes.Ellipse eye, double side, double sx, double sy, double lift)
    {
        double yawRad = _yaw.Value * Math.PI / 180;
        double alpha = -_pitch.Value * Math.PI / 180; // rotation about X (same sign as the 3D transform)
        double ca = Math.Cos(alpha), sa = Math.Sin(alpha);
        double cy = Math.Cos(yawRad), sY = Math.Sin(yawRad);

        double nx0 = Math.Sin(side), nz0 = Math.Cos(side);

        // Position on the surface, scaled about the base (y = -1).
        double x = _eyeRadius * nx0 * sx;
        double y = -1 + (EyeHeight + 1) * sy;
        double z = _eyeRadius * nz0 * sx;

        double y1 = y * ca - z * sa;
        double z1 = y * sa + z * ca;
        double x2 = x * cy + z1 * sY;
        double z2 = -x * sY + z1 * cy;

        // Surface normal (ignoring the small vertical component): how much the eye faces the camera.
        double nz1 = nz0 * ca;
        double facing = -nx0 * sY + nz1 * cy;

        if (facing < 0.05)
        {
            eye.Visibility = Visibility.Hidden;
            return;
        }

        eye.Visibility = Visibility.Visible;
        eye.Opacity = Math.Clamp((facing - 0.05) / 0.25, 0, 1);

        double scale = _focalLength / (CameraDistance - z2);
        double px = ViewSize / 2 + x2 * scale;
        double py = ViewSize / 2 - (y1 + lift) * scale;

        double wide = _wide.Value;
        double w = EyeWidthUnits * scale * Math.Clamp(facing, 0.25, 1) * (1 + (wide - 1) * 0.6);
        double h = Math.Max(0.6, EyeHeightUnits * scale * wide * _open.Value);

        eye.Width = w;
        eye.Height = h;
        Canvas.SetLeft(eye, px - w / 2);
        Canvas.SetTop(eye, py - h / 2);
    }
}
