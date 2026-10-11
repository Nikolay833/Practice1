using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Dropshelf.Activities.Hud;
using Dropshelf.Interop;
using Dropshelf.Services;

namespace Dropshelf.Activities.Notifications;

/// <summary>
/// A Windows notification as a short HUD on the island: the app's logo at the leading edge, "App · Title" over a one-line body at the trailing
/// of it, a "+2" badge when more are waiting. Each one stays about 4 seconds; several that arrive together are shown one after the
/// other. Clicking the HUD opens the sending app (best effort) and dismisses everything that was queued. While a fullscreen app is
/// in front, "do not disturb" drops new notifications instead of queueing them (Windows still shows its own banner).
/// </summary>
internal sealed class NotificationActivity : PillActivityBase, IDisposable
{
    private const double LogoSize = 26, TextWidth = 214, BadgeWidth = 34, BodyRowHeight = 34;

    // The pill's own HUD timer is 4.0 s; advancing a little earlier keeps the next notification from popping in after a collapse.
    private static readonly TimeSpan ShowFor = TimeSpan.FromMilliseconds(4000);
    private static readonly TimeSpan AdvanceAfter = TimeSpan.FromMilliseconds(3800);

    private readonly SettingsService _settings;
    private readonly NotificationService _service;
    private readonly ActivityHost _host;
    private readonly Func<bool> _isFullscreen;
    private readonly Queue<IncomingNotification> _queue = new();
    private readonly DispatcherTimer _advance;
    private IncomingNotification? _current;
    private CompactContent? _content;
    private Border? _badge;
    private TextBlock? _badgeText;

    public NotificationActivity(SettingsService settings, NotificationService service, ActivityHost host, Func<bool> isFullscreen)
    {
        _settings = settings;
        _service = service;
        _host = host;
        _isFullscreen = isFullscreen;

        _advance = new DispatcherTimer { Interval = AdvanceAfter };
        _advance.Tick += (_, _) => Advance();
        _service.Received += OnReceived;
    }

    public override string Id => "notification";
    public override int Priority => 270;
    public override PillActivityKind Kind => PillActivityKind.Hud;
    public override TimeSpan HudDuration => ShowFor;

    /// <summary>A notification that arrives while the island is open is shown when it closes.</summary>
    public override bool QueueHudWhileExpanded => true;

    /// <summary>The user decides in Settings; this only matters while "Hide in fullscreen apps" detects fullscreen.</summary>
    public override bool ShowInFullscreen => !_settings.Current.NotificationsDndFullscreen;

    public override CompactContent? CreateCompact() => _content;

    /// <summary>The notification settings changed: switching the feature off clears what is waiting.</summary>
    public void ApplySettings()
    {
        if (!_settings.Current.NotificationsEnabled)
        {
            Clear();
        }
    }

    private void OnReceived(IncomingNotification notification)
    {
        if (!_settings.Current.NotificationsEnabled)
        {
            return;
        }

        if (_settings.Current.NotificationsDndFullscreen && _isFullscreen())
        {
            return;
        }

        _queue.Enqueue(notification);
        if (_current == null)
        {
            ShowNext();
        }
        else
        {
            UpdateBadge();
        }
    }

    private void ShowNext()
    {
        if (_queue.Count == 0)
        {
            return;
        }

        _current = _queue.Dequeue();
        _content = Build(_current);
        UpdateBadge();
        _advance.Stop();
        _advance.Start();
        RequestHud();
    }

    private void Advance()
    {
        _advance.Stop();
        if (_queue.Count > 0)
        {
            ShowNext();
        }
        else
        {
            _current = null; // the pill's HUD timer ends the HUD itself
        }
    }

    private void Clear()
    {
        _queue.Clear();
        _advance.Stop();
        _current = null;
        _host.DismissHud(this);
    }

    private void OnClicked()
    {
        IncomingNotification? clicked = _current;
        Clear();
        if (clicked is { AppId.Length: > 0 } && !AppActivation.TryActivate(clicked.AppId))
        {
            Log.Info("Could not open the app that sent the notification");
        }
    }

    private void UpdateBadge()
    {
        if (_badge == null || _badgeText == null)
        {
            return;
        }

        int more = _queue.Count;
        _badgeText.Text = "+" + more;
        _badge.Visibility = more > 0 ? Visibility.Visible : Visibility.Hidden; // hidden, not collapsed: the width stays fixed
    }

    // ---- Content ---------------------------------------------------------------------------------

    private CompactContent Build(IncomingNotification n)
    {
        FrameworkElement logo;
        if (n.Logo != null)
        {
            var image = new Image
            {
                Source = n.Logo,
                Width = LogoSize,
                Height = LogoSize,
                Stretch = Stretch.Uniform,
                Clip = new RectangleGeometry(new Rect(0, 0, LogoSize, LogoSize), 6, 6),
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            logo = image;
        }
        else
        {
            logo = HudParts.Glyph("", "AccentBrush", 16); // ringer
        }

        FrameworkElement left = FlatButton(HudParts.Cell(logo, 30, BodyRowHeight), OnClicked);

        bool hidden = _settings.Current.HideNotificationContent;
        string head = n.Title.Length > 0 ? n.AppName + " · " + n.Title : n.AppName;
        string body = hidden ? "New notification" : n.Body;

        var lines = new StackPanel { Width = TextWidth, VerticalAlignment = VerticalAlignment.Center };
        lines.Children.Add(HudParts.Label(head, 12, "TextBrush", FontWeights.SemiBold));
        if (body.Length > 0)
        {
            lines.Children.Add(HudParts.Label(body, 11, "TextSecondaryBrush"));
        }

        _badgeText = HudParts.Label("+1", 10, "TextBrush", FontWeights.SemiBold);
        _badge = new Border
        {
            Child = _badgeText,
            Padding = new Thickness(6, 1, 6, 1),
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Hidden,
        };
        _badge.SetResourceReference(Border.BackgroundProperty, "DangerBrush");

        var row = new Grid { Width = TextWidth + BadgeWidth, Height = BodyRowHeight };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TextWidth) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(BadgeWidth) });
        Grid.SetColumn(lines, 0);
        Grid.SetColumn(_badge, 1);
        row.Children.Add(lines);
        row.Children.Add(_badge);

        FrameworkElement right = FlatButton(row, OnClicked);
        return CompactContent.Of(left, right, extraHeight: 14, interactive: true);
    }

    /// <summary>
    /// A button with no chrome. It has to be a button: a click on any other part of the island opens it, a click on a
    /// <see cref="System.Windows.Controls.Primitives.ButtonBase"/> does not.
    /// </summary>
    private static Button FlatButton(FrameworkElement content, Action click)
    {
        var plate = new FrameworkElementFactory(typeof(Border));
        plate.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        plate.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));

        var button = new Button
        {
            Content = content,
            Cursor = Cursors.Hand,
            Focusable = false,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Template = new ControlTemplate(typeof(Button)) { VisualTree = plate },
        };
        button.Click += (_, _) => click();
        return button;
    }

    public void Dispose()
    {
        _service.Received -= OnReceived;
        _advance.Stop();
    }
}
