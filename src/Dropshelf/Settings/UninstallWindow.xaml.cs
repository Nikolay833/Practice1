using System.Windows;

namespace Dropshelf.Settings;

/// <summary>Dark confirmation dialog for "Uninstall...", with the "also delete my data" choice. Esc cancels.</summary>
public partial class UninstallWindow : Window
{
    public UninstallWindow()
    {
        InitializeComponent();
    }

    /// <summary>True when the user ticked "Also delete my shelf &amp; clipboard data".</summary>
    public bool DeleteData => DeleteDataCheck.IsChecked == true;

    private void Uninstall_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
