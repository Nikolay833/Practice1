using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Dropshelf.Animation;
using Dropshelf.Services;

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
        UpdateInstallInfo();
        _initializing = false;

        _settings.PropertyChanged += OnSettingChanged;
        Closed += (_, _) => _settings.PropertyChanged -= OnSettingChanged;
    }

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
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

        AutoStartStatus.Text = StartupRegistration.IsEnabled()
            ? "Starts automatically when you sign in"
            : "Does not start automatically when you sign in";
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
