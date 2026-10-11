using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;
using Dropshelf.Activities.Media;
using Dropshelf.Services;
using Windows.ApplicationModel;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Dropshelf.Activities.Notifications;

/// <summary>Where the notification feature stands; Settings shows it.</summary>
internal enum NotificationAccess
{
    /// <summary>Switched off in Settings.</summary>
    Off,

    /// <summary>A portable exe has no package identity, and the notification listener needs one.</summary>
    RequiresPackage,

    /// <summary>Waiting for the user to answer Windows' permission prompt.</summary>
    Requesting,

    Allowed,

    /// <summary>The user said no (or Windows policy did): Settings > Privacy & security > Notifications can change it.</summary>
    Denied,

    /// <summary>The API failed in a way that is not a plain "no".</summary>
    Unavailable,
}

/// <summary>One toast that arrived, ready to show: who sent it, its text (empty when content is hidden) and the app's logo.</summary>
internal sealed class IncomingNotification
{
    public uint Id { get; init; }
    public string AppId { get; init; } = string.Empty;
    public string AppName { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public ImageSource? Logo { get; init; }
}

/// <summary>
/// Windows toast notifications through <see cref="UserNotificationListener"/>. Needs package identity (the MSIX build) and the
/// user's permission. New toasts arrive through the <c>NotificationChanged</c> event; if subscribing to it throws, the service polls
/// <c>GetNotificationsAsync</c> every 2 seconds and diffs by id. Notifications that were already in Action Center when the
/// feature started are not shown. Everything is raised on the UI thread. Windows' own banner cannot be suppressed from here.
/// </summary>
internal sealed class NotificationService : IDisposable
{
    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(30);

    private readonly SettingsService _settings;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _poll;
    private readonly HashSet<uint> _seen = new();
    private readonly Dictionary<string, ImageSource?> _logos = new();
    private UserNotificationListener? _listener;
    private bool _subscribed;
    private bool _polling;
    private bool _pollBusy;
    private int _generation;
    private bool _disposed;

    public NotificationService(SettingsService settings)
    {
        _settings = settings;
        Current = this;
        _poll = new DispatcherTimer { Interval = PollEvery };
        _poll.Tick += async (_, _) => await PollOnceAsync();
        ApplySettings();
    }

    public static NotificationService? Current { get; private set; }

    public NotificationAccess Access { get; private set; } = NotificationAccess.Off;

    /// <summary>A toast arrived that passed the mute list (UI thread).</summary>
    public event Action<IncomingNotification>? Received;

    /// <summary>The access state changed.</summary>
    public event Action? AccessChanged;

    /// <summary>A new app showed up (Settings adds it to the mute list).</summary>
    public event Action? AppsChanged;

    /// <summary>Starts or stops listening to match the settings.</summary>
    public void ApplySettings()
    {
        if (_disposed)
        {
            return;
        }

        if (!PackageIdentity.IsPackaged)
        {
            SetAccess(NotificationAccess.RequiresPackage);
            return;
        }

        if (_settings.Current.NotificationsEnabled)
        {
            if (Access is NotificationAccess.Off or NotificationAccess.Denied or NotificationAccess.Unavailable)
            {
                _ = StartAsync();
            }
        }
        else
        {
            Stop();
            SetAccess(NotificationAccess.Off);
        }
    }

    private void SetAccess(NotificationAccess access)
    {
        if (Access != access)
        {
            Access = access;
            AccessChanged?.Invoke();
        }
    }

    private async Task StartAsync()
    {
        int generation = ++_generation;
        SetAccess(NotificationAccess.Requesting);
        try
        {
            _listener = UserNotificationListener.Current;
            UserNotificationListenerAccessStatus status = _listener.GetAccessStatus();
            if (status == UserNotificationListenerAccessStatus.Unspecified)
            {
                status = await _listener.RequestAccessAsync(); // Windows asks the user; must run on the UI thread
            }

            if (generation != _generation || _disposed)
            {
                return; // switched off (or on again) while the prompt was open
            }

            if (status != UserNotificationListenerAccessStatus.Allowed)
            {
                SetAccess(NotificationAccess.Denied);
                return;
            }

            await SeedSeenAsync();
            if (generation != _generation || _disposed)
            {
                return;
            }

            Subscribe();
            SetAccess(NotificationAccess.Allowed);
        }
        catch (Exception ex)
        {
            Log.Error("Notification access failed", ex);
            if (generation == _generation)
            {
                SetAccess(NotificationAccess.Unavailable);
            }
        }
    }

    /// <summary>Remembers what is in Action Center right now so only toasts that arrive later are shown.</summary>
    private async Task SeedSeenAsync()
    {
        _seen.Clear();
        IReadOnlyList<UserNotification> current = await _listener!.GetNotificationsAsync(NotificationKinds.Toast);
        foreach (UserNotification n in current)
        {
            _seen.Add(n.Id);
        }
    }

    private void Subscribe()
    {
        try
        {
            _listener!.NotificationChanged += OnNotificationChanged;
            _subscribed = true;
            _polling = false;
        }
        catch (Exception ex)
        {
            // Not every host allows the in-process event; polling does the same job a little later.
            Log.Info($"Notification event unavailable ({ex.GetType().Name}); polling every 2 s instead");
            _subscribed = false;
            _polling = true;
            _poll.Start();
        }
    }

    private void Stop()
    {
        _generation++;
        _poll.Stop();
        _polling = false;
        if (_subscribed && _listener != null)
        {
            try
            {
                _listener.NotificationChanged -= OnNotificationChanged;
            }
            catch (Exception ex)
            {
                Log.Error("Could not unsubscribe from notifications", ex);
            }
        }

        _subscribed = false;
        _seen.Clear();
    }

    // ---- Receiving -------------------------------------------------------------------------------

    /// <summary>WinRT raises this on a thread-pool thread.</summary>
    private void OnNotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
    {
        if (args.ChangeKind != UserNotificationChangedKind.Added)
        {
            return;
        }

        uint id = args.UserNotificationId;
        _dispatcher.BeginInvoke(new Action(async () =>
        {
            if (_disposed || _listener == null || !_subscribed)
            {
                return;
            }

            try
            {
                UserNotification? notification = _listener.GetNotification(id);
                if (notification != null)
                {
                    await HandleAsync(notification);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Reading a notification failed", ex);
            }
        }));
    }

    private async Task PollOnceAsync()
    {
        if (_pollBusy || !_polling || _listener == null || _disposed)
        {
            return;
        }

        _pollBusy = true;
        try
        {
            IReadOnlyList<UserNotification> current = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            var ids = new HashSet<uint>();
            foreach (UserNotification n in current.OrderBy(item => item.CreationTime))
            {
                ids.Add(n.Id);
                await HandleAsync(n);
            }

            _seen.IntersectWith(ids); // forget the ones the user cleared
        }
        catch (Exception ex)
        {
            Log.Error("Polling notifications failed", ex);
        }
        finally
        {
            _pollBusy = false;
        }
    }

    private async Task HandleAsync(UserNotification notification)
    {
        if (!_seen.Add(notification.Id))
        {
            return;
        }

        if (_seen.Count > 2000)
        {
            _seen.Clear();
            _seen.Add(notification.Id);
        }

        if (DateTimeOffset.Now - notification.CreationTime > MaxAge)
        {
            return; // arrived while the PC slept or Dropshelf was away: not news any more
        }

        AppInfo? app = notification.AppInfo;
        string appId = app?.AppUserModelId ?? string.Empty;
        string appName = app?.DisplayInfo?.DisplayName ?? string.Empty;
        if (string.IsNullOrWhiteSpace(appName))
        {
            appName = "Notification";
        }

        if (appId.Length > 0 && _settings.Current.RememberNotificationApp(appId, appName))
        {
            AppsChanged?.Invoke();
        }

        if (appId.Length > 0 && _settings.Current.IsNotificationMuted(appId))
        {
            return;
        }

        string title = string.Empty, body = string.Empty;
        if (!_settings.Current.HideNotificationContent)
        {
            (title, body) = ReadText(notification);
        }

        ImageSource? logo = app == null ? null : await LogoAsync(appId, app);
        if (_disposed || (Access != NotificationAccess.Allowed && Access != NotificationAccess.Requesting))
        {
            return;
        }

        Received?.Invoke(new IncomingNotification
        {
            Id = notification.Id,
            AppId = appId,
            AppName = appName,
            Title = title,
            Body = body,
            Logo = logo,
        });
    }

    /// <summary>Title = first text line of the toast, body = the rest on one line.</summary>
    private static (string Title, string Body) ReadText(UserNotification notification)
    {
        try
        {
            NotificationBinding? binding = notification.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
            if (binding == null)
            {
                return (string.Empty, string.Empty);
            }

            List<string> lines = binding.GetTextElements()
                .Select(t => OneLine(t.Text))
                .Where(t => t.Length > 0)
                .ToList();
            return (lines.Count > 0 ? lines[0] : string.Empty, string.Join("  ", lines.Skip(1)));
        }
        catch (Exception ex)
        {
            Log.Error("Reading notification text failed", ex);
            return (string.Empty, string.Empty);
        }
    }

    private static string OneLine(string? text) =>
        string.IsNullOrWhiteSpace(text) ? string.Empty : string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>The app's logo, decoded once per app.</summary>
    private async Task<ImageSource?> LogoAsync(string appId, AppInfo app)
    {
        if (appId.Length > 0 && _logos.TryGetValue(appId, out ImageSource? cached))
        {
            return cached;
        }

        ImageSource? logo = null;
        try
        {
            var reference = app.DisplayInfo.GetLogo(new Windows.Foundation.Size(64, 64));
            byte[]? bytes = await MediaSessionService.ReadBytesAsync(reference);
            if (bytes != null)
            {
                logo = MediaSessionService.Decode(bytes, 64);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Reading a notification logo failed", ex);
        }

        if (appId.Length > 0 && _logos.Count < 200)
        {
            _logos[appId] = logo;
        }

        return logo;
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
    }
}
