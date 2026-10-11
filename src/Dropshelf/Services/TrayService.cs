using System;
using System.IO;
using System.Windows;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace Dropshelf.Services;

/// <summary>System tray icon and its (dark-themed) context menu.</summary>
internal sealed class TrayService : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ContextMenuStrip _menu;
    private readonly Drawing.Icon? _ownedIcon;

    public TrayService(Action showShelf, Action openBasket, Action openClipboard, Action openSettings, Action uninstall, Action quit,
        Action screenshot, Action quickNote)
    {
        _menu = new WinForms.ContextMenuStrip
        {
            Renderer = new WinForms.ToolStripProfessionalRenderer(new DarkColorTable()) { RoundedEdges = false },
            ShowImageMargin = false,
            BackColor = Drawing.Color.FromArgb(28, 28, 28),
            ForeColor = Drawing.Color.White,
            Font = new Drawing.Font("Segoe UI", 9.5f),
        };

        AddItem("Show shelf", showShelf);
        AddItem("Open basket", openBasket);
        AddItem("Clipboard history", openClipboard);
        AddItem("Screenshot to shelf", screenshot);
        AddItem("Quick note", quickNote);
        _menu.Items.Add(new WinForms.ToolStripSeparator());
        AddItem("Settings…", openSettings);
        AddItem("Uninstall…", uninstall);
        _menu.Items.Add(new WinForms.ToolStripSeparator());
        AddItem("Quit", quit);

        _ownedIcon = LoadIcon();
        _icon = new WinForms.NotifyIcon
        {
            Icon = _ownedIcon ?? Drawing.SystemIcons.Application,
            Text = "Dropshelf",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left)
            {
                showShelf();
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

    /// <summary>Dark palette for the WinForms menu renderer.</summary>
    private sealed class DarkColorTable : WinForms.ProfessionalColorTable
    {
        private static readonly Drawing.Color Background = Drawing.Color.FromArgb(28, 28, 28);
        private static readonly Drawing.Color Hover = Drawing.Color.FromArgb(48, 48, 48);
        private static readonly Drawing.Color Border = Drawing.Color.FromArgb(52, 52, 52);

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
