using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Dropshelf.Animation;
using Dropshelf.Interop;
using Dropshelf.Services;

namespace Dropshelf.Clipboard;

/// <summary>
/// The clipboard history (Ctrl+Shift+V), laid out like Droppy's: a black strip at the bottom of the screen with a search
/// field and filter capsules over a horizontal row of cards (kind, age, preview, size; the selected card has a white
/// outline). Typing searches, Left / Right choose, Enter copies the entry back to the clipboard, hides the strip, restores
/// the previously focused window and sends Ctrl+V to it. The cards are virtualized, so only the visible ones exist.
/// </summary>
public partial class ClipboardWindow : Window
{
    private const double StripHeight = 262, MaxStripWidth = 1180, ScreenMargin = 24;

    private readonly ClipboardHistoryService _history;
    private readonly ListCollectionView _view;
    private readonly DispatcherTimer _refreshTimer;
    private IntPtr _previousWindow;
    private bool _pasteAfterCopy;
    private bool _styled;
    private bool _busy; // true while pasting so deactivation does not interfere

    // Open animation: one spring from 0.94 to 1 drives a slide up from the bottom edge, a slight scale and the fade.
    private const double OpenFrom = 0.94;
    private readonly Spring _open = new(1, 520, 0.7);
    private readonly FrameLoop _openLoop;

    public ClipboardWindow(ClipboardHistoryService history)
    {
        InitializeComponent();
        _history = history;
        _openLoop = new FrameLoop(OnOpenFrame);

        // Pinned first, then newest first. Live sorting keeps the order right when a pin is toggled.
        _view = new ListCollectionView(history.Entries);
        _view.SortDescriptions.Add(new SortDescription(nameof(ClipboardEntry.IsPinned), ListSortDirection.Descending));
        _view.SortDescriptions.Add(new SortDescription(nameof(ClipboardEntry.TimestampUtc), ListSortDirection.Descending));
        if (_view.CanChangeLiveSorting)
        {
            _view.IsLiveSorting = true;
            _view.LiveSortingProperties.Add(nameof(ClipboardEntry.IsPinned));
            _view.LiveSortingProperties.Add(nameof(ClipboardEntry.TimestampUtc));
        }

        _view.Filter = Matches;
        EntryList.ItemsSource = _view;

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _refreshTimer.Tick += (_, _) => RefreshTimes();
    }

    /// <summary>Shows the strip; shows again hides it. <paramref name="fromHotkey"/> enables the paste simulation.</summary>
    public void Toggle(bool fromHotkey)
    {
        if (IsVisible)
        {
            HidePopup();
            return;
        }

        IntPtr foreground = NativeMethods.GetForegroundWindow();
        _previousWindow = WindowHelper.IsOwnWindow(foreground) ? IntPtr.Zero : foreground;
        _pasteAfterCopy = fromHotkey && _previousWindow != IntPtr.Zero;

        SearchBox.Text = string.Empty;
        FilterAll.IsChecked = true;
        _view.Refresh();
        RefreshTimes();
        UpdateEmptyText();
        EntryList.SelectedIndex = _view.Count > 0 ? 0 : -1;

        IntPtr hwnd = new WindowInteropHelper(this).EnsureHandle();
        if (!_styled)
        {
            WindowHelper.MakeToolWindow(hwnd, noActivate: false); // keep it out of Alt+Tab
            _styled = true;
        }

        var monitor = _previousWindow != IntPtr.Zero
            ? MonitorInfo.FromHandle(NativeMethods.MonitorFromWindow(_previousWindow, NativeMethods.MONITOR_DEFAULTTONEAREST))
            : MonitorInfo.UnderCursor();
        PlaceAtBottom(monitor);

        StartOpenAnimation();
        Show();
        PlaceAtBottom(monitor);
        WindowHelper.ForceForeground(hwnd);
        Activate();
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        _refreshTimer.Start();

        // Once the cards exist: let the first ones slide in one after another and bring the selection into view.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            AnimateCards();
            if (EntryList.SelectedItem != null)
            {
                EntryList.ScrollIntoView(EntryList.SelectedItem);
            }
        }));
    }

    /// <summary>Centred along the bottom of the monitor's work area, as wide as fits (up to <see cref="MaxStripWidth"/>).</summary>
    private void PlaceAtBottom(MonitorInfo monitor)
    {
        double widthDip = Math.Min(MaxStripWidth, monitor.WorkWidth / monitor.Scale - 2 * ScreenMargin);
        int w = (int)Math.Round(widthDip * monitor.Scale);
        int h = (int)Math.Round(StripHeight * monitor.Scale);
        int x = monitor.WorkLeft + (monitor.WorkWidth - w) / 2;
        int y = monitor.WorkBottom - h - (int)Math.Round(ScreenMargin * 0.5 * monitor.Scale);
        WindowHelper.Place(this, x, y, widthDip, StripHeight, monitor.Scale);
    }

    private void HidePopup()
    {
        _refreshTimer.Stop();
        _openLoop.Stop();
        Hide();
    }

    // ---- Animations -----------------------------------------------------------------------------

    private void StartOpenAnimation()
    {
        if (AnimationSettings.IsOff)
        {
            _open.Snap(1);
            ApplyOpen();
            return;
        }

        AnimationSettings.Configure(_open, 520, 0.7);
        _open.Target = 1;
        _open.Displace(OpenFrom);
        ApplyOpen();
        _openLoop.Start();
    }

    private bool OnOpenFrame(double dt)
    {
        _open.Step(dt);
        ApplyOpen();
        if (_open.SettleIfClose(0.0004, 0.01))
        {
            ApplyOpen();
            return false;
        }

        return true;
    }

    private void ApplyOpen()
    {
        double v = _open.Value;
        double u = Math.Clamp((v - OpenFrom) / (1 - OpenFrom), 0, 1); // 0..1 progress, without the overshoot
        double scale = 0.98 + 0.02 * u;
        PanelScale.ScaleX = scale;
        PanelScale.ScaleY = scale;
        PanelShift.Y = 36 * (1 - u);
        Panel.Opacity = Math.Clamp(u * 1.8, 0, 1);
    }

    /// <summary>The first few cards (the only ones that exist: the row is virtualized) slide in one after another.</summary>
    private void AnimateCards()
    {
        if (AnimationSettings.IsOff)
        {
            return;
        }

        int count = Math.Min(_view.Count, 8);
        for (int i = 0; i < count; i++)
        {
            if (EntryList.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement card)
            {
                TileAnimator.SlideFadeIn(card, i * 22);
            }
        }
    }

    private void RefreshTimes()
    {
        foreach (var entry in _history.Entries)
        {
            entry.RefreshTime();
        }
    }

    // ---- Search and filters ---------------------------------------------------------------------

    private bool Matches(object item)
    {
        if (item is not ClipboardEntry entry)
        {
            return false;
        }

        bool kind = FilterPinned?.IsChecked == true ? entry.IsPinned
            : FilterText?.IsChecked == true ? entry.Kind == ClipboardEntryKind.Text
            : FilterImages?.IsChecked == true ? entry.Kind == ClipboardEntryKind.Image
            : FilterFiles?.IsChecked != true || entry.Kind == ClipboardEntryKind.Files;
        string query = SearchBox?.Text ?? string.Empty;
        return kind && (string.IsNullOrWhiteSpace(query) || entry.SearchText.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_view != null)
        {
            ApplyFilter();
        }
    }

    private void Filter_Checked(object sender, RoutedEventArgs e)
    {
        if (_view != null)
        {
            ApplyFilter();
            Keyboard.Focus(SearchBox); // typing keeps searching after a click on a filter
        }
    }

    private void ApplyFilter()
    {
        _view.Refresh();
        UpdateEmptyText();
        EntryList.SelectedIndex = _view.Count > 0 ? 0 : -1;
        if (EntryList.SelectedItem != null)
        {
            EntryList.ScrollIntoView(EntryList.SelectedItem);
        }
    }

    private void UpdateEmptyText()
    {
        EmptyText.Text = _view.Count > 0
            ? string.Empty
            : _history.Entries.Count == 0
                ? (_history.IsEnabled ? "Nothing copied yet" : "Clipboard history is turned off in Settings")
                : "No matches";
    }

    // ---- Keyboard -------------------------------------------------------------------------------

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Right:
            case Key.Down:
                MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Left:
            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.PageDown: MoveSelection(5); e.Handled = true; break;
            case Key.PageUp: MoveSelection(-5); e.Handled = true; break;
            case Key.Enter:
                _ = PasteSelectedAsync();
                e.Handled = true;
                break;
            case Key.Escape:
                HidePopup();
                e.Handled = true;
                break;
            case Key.Delete:
                // Plain text editing wins unless there is nothing left to delete in the search box.
                if (SearchBox.SelectionLength == 0 && SearchBox.CaretIndex >= SearchBox.Text.Length)
                {
                    DeleteSelected();
                    e.Handled = true;
                }

                break;
            case Key.P when Keyboard.Modifiers == ModifierKeys.Control:
                if (EntryList.SelectedItem is ClipboardEntry entry)
                {
                    TogglePin(entry);
                }

                e.Handled = true;
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        if (_view.Count == 0)
        {
            return;
        }

        int index = Math.Clamp(EntryList.SelectedIndex + delta, 0, _view.Count - 1);
        EntryList.SelectedIndex = index;
        EntryList.ScrollIntoView(EntryList.SelectedItem);
    }

    private void DeleteSelected()
    {
        if (EntryList.SelectedItem is not ClipboardEntry entry)
        {
            return;
        }

        int index = EntryList.SelectedIndex;
        _history.Remove(entry);
        UpdateEmptyText();
        if (_view.Count > 0)
        {
            EntryList.SelectedIndex = Math.Min(index, _view.Count - 1);
        }
    }

    private void TogglePin(ClipboardEntry entry)
    {
        entry.IsPinned = !entry.IsPinned;
        _history.NotifyPinChanged();
        EntryList.ScrollIntoView(entry);
    }

    // ---- Mouse ----------------------------------------------------------------------------------

    /// <summary>The wheel scrolls the row of cards sideways.</summary>
    private void EntryList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (FindScrollViewer(EntryList) is { } scroll)
        {
            scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - e.Delta);
            e.Handled = true;
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer scroll)
            {
                return scroll;
            }

            if (FindScrollViewer(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private void EntryList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => _ = PasteSelectedAsync();

    private void PasteMenu_Click(object sender, RoutedEventArgs e) => _ = PasteSelectedAsync();

    private void PinMenu_Click(object sender, RoutedEventArgs e)
    {
        if (EntryList.SelectedItem is ClipboardEntry entry)
        {
            TogglePin(entry);
        }
    }

    private void DeleteMenu_Click(object sender, RoutedEventArgs e) => DeleteSelected();

    private void Window_Deactivated(object sender, EventArgs e)
    {
        // A context menu takes the focus for a moment; that is not "clicking elsewhere".
        if (!_busy && IsVisible && EntryList.ContextMenu is not { IsOpen: true })
        {
            HidePopup();
        }
    }

    // ---- Paste ----------------------------------------------------------------------------------

    private async System.Threading.Tasks.Task PasteSelectedAsync()
    {
        if (_busy || EntryList.SelectedItem is not ClipboardEntry entry)
        {
            return;
        }

        _busy = true;
        try
        {
            IntPtr target = _previousWindow;
            bool paste = _pasteAfterCopy;

            bool copied = await _history.SetClipboardAsync(entry);
            HidePopup();
            if (!copied || !paste || target == IntPtr.Zero || !NativeMethods.IsWindow(target))
            {
                return;
            }

            // Give focus back and wait until Windows really switched before synthesising Ctrl+V.
            WindowHelper.ForceForeground(target);
            for (int i = 0; i < 25 && NativeMethods.GetForegroundWindow() != target; i++)
            {
                await System.Threading.Tasks.Task.Delay(20);
            }

            if (NativeMethods.GetForegroundWindow() != target)
            {
                Log.Error("Paste skipped: previous window did not regain focus");
                return; // never send Ctrl+V to whatever else has focus; the entry is on the clipboard already
            }

            await System.Threading.Tasks.Task.Delay(40);
            await KeyboardInput.SendCtrlVAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Paste from history failed", ex);
        }
        finally
        {
            _busy = false;
        }
    }
}
