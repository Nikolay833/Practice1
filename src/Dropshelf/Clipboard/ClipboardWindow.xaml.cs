using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Dropshelf.Interop;
using Dropshelf.Services;

namespace Dropshelf.Clipboard;

/// <summary>
/// Centered, searchable clipboard history popup (Ctrl+Shift+V). Enter copies the entry back to the clipboard,
/// hides the popup, restores the previously focused window and sends Ctrl+V to it.
/// </summary>
public partial class ClipboardWindow : Window
{
    private readonly ClipboardHistoryService _history;
    private readonly ListCollectionView _view;
    private readonly DispatcherTimer _refreshTimer;
    private IntPtr _previousWindow;
    private bool _pasteAfterCopy;
    private bool _styled;
    private bool _busy; // true while pasting so deactivation does not interfere

    public ClipboardWindow(ClipboardHistoryService history)
    {
        InitializeComponent();
        _history = history;

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

        _view.Filter = MatchesQuery;
        EntryList.ItemsSource = _view;

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _refreshTimer.Tick += (_, _) => RefreshTimes();
    }

    /// <summary>Shows the popup; shows again hides it. <paramref name="fromHotkey"/> enables the paste simulation.</summary>
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
        WindowHelper.CenterOnMonitor(this, monitor, Width, Height);

        Show();
        WindowHelper.CenterOnMonitor(this, monitor, Width, Height);
        WindowHelper.ForceForeground(hwnd);
        Activate();
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        _refreshTimer.Start();
    }

    private void HidePopup()
    {
        _refreshTimer.Stop();
        Hide();
    }

    private void RefreshTimes()
    {
        foreach (var entry in _history.Entries)
        {
            entry.RefreshTime();
        }
    }

    private bool MatchesQuery(object item)
    {
        string query = SearchBox?.Text ?? string.Empty;
        return string.IsNullOrWhiteSpace(query) ||
               (item is ClipboardEntry entry && entry.SearchText.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _view.Refresh();
        UpdateEmptyText();
        EntryList.SelectedIndex = _view.Count > 0 ? 0 : -1;
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
            case Key.Down: MoveSelection(1); e.Handled = true; break;
            case Key.Up: MoveSelection(-1); e.Handled = true; break;
            case Key.PageDown: MoveSelection(6); e.Handled = true; break;
            case Key.PageUp: MoveSelection(-6); e.Handled = true; break;
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

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ClipboardEntry entry })
        {
            TogglePin(entry);
        }

        e.Handled = true;
    }

    private void EntryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Ignore double clicks that land on the pin button.
        for (var node = e.OriginalSource as DependencyObject; node != null && node != EntryList; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is System.Windows.Controls.Primitives.ButtonBase)
            {
                return;
            }
        }

        _ = PasteSelectedAsync();
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        if (!_busy && IsVisible)
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
