using System;
using System.Windows;
using System.Windows.Interop;
using Dropshelf.Interop;

namespace Dropshelf.Settings;

/// <summary>macOS-style confirmation alert for "Uninstall...", with the "also delete my data" choice. Esc cancels.</summary>
public partial class UninstallWindow : Window
{
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        GlassHelper.TryRoundCorners(new WindowInteropHelper(this).Handle);
    }

    public UninstallWindow()
    {
        InitializeComponent();
    }

    /// <summary>True when the user turned on "Also delete my data".</summary>
    public bool DeleteData => DeleteDataCheck.IsChecked == true;

    private void Uninstall_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
