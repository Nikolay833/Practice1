using System.Windows;

namespace Dropshelf.Services;

/// <summary>
/// The first time the MSIX build runs on a PC that also has the portable build set to start with Windows, ask (once) whether to
/// remove the portable build's autostart entry. The single-instance mutex already stops both from running at once; this just keeps
/// the sign-in tidy. The portable program files are left alone.
/// </summary>
internal static class PortableCleanup
{
    public static void OfferOnce(SettingsService settings)
    {
        if (!PackageIdentity.IsPackaged || settings.Current.PortableCleanupOffered || !StartupRegistration.HasPortableAutostart())
        {
            return;
        }

        settings.Current.PortableCleanupOffered = true;
        MessageBoxResult answer = MessageBox.Show(
            "A portable copy of Dropshelf is also set to start with Windows.\n\n"
            + "Remove its startup entry so only this installed version starts? The portable program itself is not deleted.",
            "Dropshelf",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
        {
            StartupRegistration.RemovePortableAutostart();
            Log.Info("Removed the portable build's autostart entry");
        }
    }
}
