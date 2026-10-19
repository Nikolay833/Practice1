using System;
using System.IO;
using System.Windows;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace Dropshelf.Services;

/// <summary>System tray icon and its context menu, styled like a macOS dark menu (accent-blue highlight).</summary>
internal sealed class TrayService : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ContextMenuStrip _menu;
    private readonly Drawing.Icon? _ownedIcon;

    public TrayService(Action showIsland, Action openBasket, Action openClipboard, Action openSettings, Action uninstall, Action quit,
        Action screenshot, Action openTerminal)
    {
        _menu = new WinForms.ContextMenuStrip
        {
            Renderer = new WinForms.ToolStripProfessionalRenderer(new DarkColorTable()) { RoundedEdges = false },
            ShowImageMargin = false,
            BackColor = DarkColorTable.Background,
            ForeColor = Drawing.Color.White,
            Font = MenuFont(),
            Padding = new WinForms.Padding(4),
        };

        AddItem("Open Dropshelf", showIsland);
        AddItem("Open basket", openBasket);
        AddItem("Clipboard history", openClipboard);
        AddItem("Take screenshot", screenshot);
        AddItem("Terminal", openTerminal);
        _menu.Items.Add(new WinForms.ToolStripSeparator());
        AddItem("Settings…", openSettings);
        // The MSIX build is removed by Windows (Settings > Apps), not by a cleanup script of its own: the item says so and opens that page.
        AddItem(PackageIdentity.IsPackaged ? "Uninstall in Settings → Apps" : "Uninstall…", uninstall);
        _menu.Items.Add(new WinForms.ToolStripSeparator());
        AddItem("Quit", quit);

        _ownedIcon = LoadIcon();
        _icon = new WinForms.NotifyIcon
        {
            Icon = _ownedIcon ?? Drawing.SystemIcons.Application,
            Text = "Dropshelf " + Installer.CurrentVersion, // hover the tray icon to see which version is running
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left)
            {
                showIsland();
            }
        };
    }

    public void ShowBalloon(string title, string text)
    {
        _icon.ShowBalloonTip(4000, title, text, WinForms.ToolTipIcon.Warning);
    }

    private void AddItem(string text, Action action)
    {
        var item = new WinForms.ToolStripMenuItem(text) { ForeColor = Drawing.Color.White, Padding = new WinForms.Padding(4, 3, 4, 3) };
        item.Click += (_, _) =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log.Error($"Tray action '{text}' failed", ex);
            }
        };
        _menu.Items.Add(item);
    }

    /// <summary>San Francisco when it is installed, otherwise Segoe UI (WinForms would silently fall back to MS Sans Serif).</summary>
    private static Drawing.Font MenuFont()
    {
        foreach (string family in new[] { "SF Pro Text", "Segoe UI Variable Text", "Segoe UI" })
        {
            try
            {
                using var probe = new Drawing.FontFamily(family);
                return new Drawing.Font(probe, 9.75f);
            }
            catch (ArgumentException)
            {
                // Not installed: try the next one.
            }
        }

        return WinForms.Control.DefaultFont;
    }

    private static Drawing.Icon? LoadIcon()
    {
        try
        {
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (info?.Stream != null)
            {
                using Stream stream = info.Stream;
                return new Drawing.Icon(stream);
            }
        }
        catch (Exception ex) when (ex is IOException or UriFormatException)
        {
            Log.Error("Could not load tray icon", ex);
        }

        return null;
    }

    public void Dispose()
    {
        _icon.Visible = false; // remove the icon immediately instead of leaving a ghost
        _icon.Dispose();
        _menu.Dispose();
        _ownedIcon?.Dispose();
    }

    /// <summary>macOS dark menu palette for the WinForms menu renderer: grey menu, systemBlue highlight.</summary>
    private sealed class DarkColorTable : WinForms.ProfessionalColorTable
    {
        public static readonly Drawing.Color Background = Drawing.Color.FromArgb(40, 40, 40);
        private static readonly Drawing.Color Hover = Drawing.Color.FromArgb(10, 132, 255);
        private static readonly Drawing.Color Border = Drawing.Color.FromArgb(58, 58, 58);

        public override Drawing.Color ToolStripDropDownBackground => Background;
        public override Drawing.Color ImageMarginGradientBegin => Background;
        public override Drawing.Color ImageMarginGradientMiddle => Background;
        public override Drawing.Color ImageMarginGradientEnd => Background;
        public override Drawing.Color MenuBorder => Border;
        public override Drawing.Color MenuItemBorder => Hover;
        public override Drawing.Color MenuItemSelected => Hover;
        public override Drawing.Color MenuItemSelectedGradientBegin => Hover;
        public override Drawing.Color MenuItemSelectedGradientEnd => Hover;
        public override Drawing.Color MenuItemPressedGradientBegin => Hover;
        public override Drawing.Color MenuItemPressedGradientMiddle => Hover;
        public override Drawing.Color MenuItemPressedGradientEnd => Hover;
        public override Drawing.Color SeparatorDark => Border;
        public override Drawing.Color SeparatorLight => Border;
    }
}
