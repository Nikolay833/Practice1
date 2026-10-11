using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Dropshelf.Activities.Calendar;
using Dropshelf.Animation;
using Dropshelf.Services;
using Dropshelf.Themes;

namespace Dropshelf.Settings;

/// <summary>Dark settings window bound directly to <see cref="AppSettings"/>; every change applies live.</summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private bool _initializing = true;

    public SettingsWindow(SettingsService settings)
    {
        InitializeComponent();
        _settings = settings.Current;
        DataContext = _settings;

        ShowAnimationMode();
        BuildAccentSwatches();
        StyleBlack.IsChecked = _settings.PillStyle == PillStyle.Black;
        StyleGlass.IsChecked = _settings.PillStyle == PillStyle.Glass;
        UpdateInstallInfo();
        UnitCelsius.IsChecked = !_settings.WeatherFahrenheit;
        UnitFahrenheit.IsChecked = _settings.WeatherFahrenheit;
        IcsUrlsBox.Text = string.Join(Environment.NewLine, CalendarService.Current?.IcsUrls ?? Array.Empty<string>());
        UpdateWindowsCalendarStatus();
        _initializing = false;

        _settings.PropertyChanged += OnSettingChanged;
        if (ThemeService.Current is { } theme)
        {
            theme.Changed += RefreshWindowsSwatch;
        }

        Closed += (_, _) =>
        {
            _settings.PropertyChanged -= OnSettingChanged;
            if (ThemeService.Current is { } t)
            {
                t.Changed -= RefreshWindowsSwatch;
            }
        };
    }

    private RadioButton? _windowsSwatch;

    /// <summary>One round swatch per accent preset; "Windows" shows the current Windows accent and follows it.</summary>
    private void BuildAccentSwatches()
    {
        foreach (AccentPreset preset in Enum.GetValues<AccentPreset>())
        {
            var swatch = new RadioButton
            {
                Style = (Style)FindResource("Swatch"),
                GroupName = "Accent",
                Tag = preset,
                ToolTip = preset == AccentPreset.Windows ? "Windows accent color" : preset.ToString(),
                Background = new SolidColorBrush(ThemeService.ColorOf(preset)),
                IsChecked = _settings.Accent == preset,
            };
            if (preset == AccentPreset.Windows)
            {
                swatch.Content = "\uE790"; // palette glyph marks "follow Windows"
                _windowsSwatch = swatch;
            }

            swatch.Checked += (_, _) =>
            {
                if (!_initializing)
                {
                    _settings.Accent = preset;
                }
            };
            AccentSwatches.Children.Add(swatch);
        }
    }

    private void RefreshWindowsSwatch()
    {
        if (_windowsSwatch != null)
        {
            _windowsSwatch.Background = new SolidColorBrush(ThemeService.ColorOf(AccentPreset.Windows));
        }
    }

    private void PillStyle_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializing || sender is not RadioButton { Tag: string tag })
        {
            return;
        }

        if (Enum.TryParse(tag, out PillStyle style))
        {
            _settings.PillStyle = style;
        }
    }

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettings.CalendarUseWindows) or nameof(AppSettings.CalendarEnabled))
        {
            // The calendar service refreshes in its own handler; show the state it ends up with a moment later.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(UpdateWindowsCalendarStatus));
        }

        // The app updates the registry in its own handler, which runs before this one.
        if (e.PropertyName == nameof(AppSettings.LaunchAtStartup))
        {
            UpdateInstallInfo();
        }
    }

    /// <summary>Install location and whether Windows will start the app at sign-in.</summary>
    private void UpdateInstallInfo()
    {
        string? current = Environment.ProcessPath;
        InstallPath.Text = Installer.IsRunningInstalled
            ? Installer.InstalledExe
            : File.Exists(Installer.InstalledExe)
                ? $"{Installer.InstalledExe}\n(this session runs from {current})"
                : $"Not installed - running from {current}";

        AutoStartStatus.Text = Installer.AutostartTarget() == null
            ? "Autostart unavailable: app isn't installed"
            : StartupRegistration.IsEnabled()
                ? "Starts automatically when you sign in"
                : "Does not start automatically when you sign in";
    }

    // ---- Calendar & weather ---------------------------------------------------------------------

    private void Units_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializing || sender is not RadioButton { Tag: string tag })
        {
            return;
        }

        _settings.WeatherFahrenheit = tag == "F";
    }

    private void UpdateWindowsCalendarStatus()
    {
        WindowsCalendarStatus.Text = CalendarService.Current?.Windows switch
        {
            WindowsCalendarState.Available => "Connected: events from your Windows calendars are included",
            WindowsCalendarState.Unavailable => "Available in the installed (MSIX) version",
            _ => "Reads the calendars of the Windows Calendar / Mail apps (installed MSIX version only)",
        };
    }

    private void IcsUrls_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        CalendarService.Current?.SetIcsUrls(IcsLines());
    }

    private string[] IcsLines() =>
        IcsUrlsBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Fetches every link once and reports how many events were found (errors name only the host: the rest of a link may be a secret).</summary>
    private async void IcsTest_Click(object sender, RoutedEventArgs e)
    {
        string[] urls = IcsLines();
        if (urls.Length == 0)
        {
            IcsTestResult.Text = "Add a link first.";
            return;
        }

        IcsTestButton.IsEnabled = false;
        IcsTestResult.Text = "Fetching...";
        try
        {
            int total = 0, week = 0, failed = 0;
            string? firstError = null;
            foreach (string url in urls)
            {
                (int t, int w, string? error) = await CalendarService.TestAsync(url);
                if (error == null)
                {
                    total += t;
                    week += w;
                }
                else
                {
                    failed++;
                    firstError ??= (CalendarService.TryNormalize(url, out Uri? uri) ? uri!.Host : "invalid link") + ": " + error;
                }
            }

            string text = $"{total} events, {week} in the next 7 days";
            if (failed > 0)
            {
                text += $" ({failed} of {urls.Length} links failed: {firstError})";
            }

            IcsTestResult.Text = text;
            CalendarService.Current?.SetIcsUrls(urls);
        }
        catch (Exception ex)
        {
            Log.Error("ICS test failed", ex);
            IcsTestResult.Text = "Test failed.";
        }
        finally
        {
            IcsTestButton.IsEnabled = true;
        }
    }

    private void ShowAnimationMode()
    {
        AnimationMode mode = AnimationSettings.Mode;
        AnimFull.IsChecked = mode == AnimationMode.Full;
        AnimSubtle.IsChecked = mode == AnimationMode.Subtle;
        AnimOff.IsChecked = mode == AnimationMode.Off;
        UpdateAnimationHint(mode);
    }

    private void UpdateAnimationHint(AnimationMode mode)
    {
        string text = mode switch
        {
            AnimationMode.Full => "Springs, 3D tile flips and tilts, and the mascot looks around.",
            AnimationMode.Subtle => "Short fades and slides only: no tilts, flips or mascot look-around.",
            _ => "No animation: everything appears instantly.",
        };

        if (_settings.Animations == null && !SystemParameters.ClientAreaAnimation)
        {
            text += " Windows animation effects are off, so Subtle is used until you pick Full.";
        }

        AnimationHint.Text = text;
    }

    private void Animation_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializing || sender is not RadioButton { Tag: string tag })
        {
            return;
        }

        if (Enum.TryParse(tag, out AnimationMode mode))
        {
            _settings.Animations = mode; // an explicit choice, so it beats the Windows animation switch
            UpdateAnimationHint(mode);
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // Released before the move began.
            }
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppPaths.Root) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Log.Error("Could not open data folder", ex);
        }
    }
}
