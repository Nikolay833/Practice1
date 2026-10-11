using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Dropshelf.Services;

namespace Dropshelf.Settings;

/// <summary>Dark settings window bound directly to <see cref="AppSettings"/>; every change applies live.</summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsService settings)
    {
        InitializeComponent();
        DataContext = settings.Current;
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
