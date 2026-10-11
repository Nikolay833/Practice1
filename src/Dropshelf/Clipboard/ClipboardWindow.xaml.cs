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

    // Open animation: one spring from 0.94 to 1 drives scale, fade and the 8 px slide-up.
    private const double OpenFrom = 0.94;
    private readonly Spring _open = new(1, 520, 0.6);
    private readonly FrameLoop _openLoop;

    // Selection highlight: springs for the bar's top and height.
    private readonly Spring _glideY = new(0, 520, 0.8);
    private readonly Spring _glideH = new(0, 520, 0.8);
    private readonly FrameLoop _glideLoop;
    private bool _glideQueued;
    private bool _glideSnapQueued;
    private bool _glideVisible;

    public ClipboardWindow(ClipboardHistoryService history)
    {
        InitializeComponent();
        _history = history;
        _openLoop = new FrameLoop(OnOpenFrame);
        _glideLoop = new FrameLoop(OnGlideFrame);
        EntryList.SelectionChanged += (_, _) => QueueGlide(snap: false);
        EntryList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
        {
            if (e.VerticalChange != 0)
            {
                QueueGlide(snap: true); // the bar must stay glued to its row while the list scrolls
            }
        }));

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

        StartOpenAnimation();
        Show();
        WindowHelper.CenterOnMonitor(this, monitor, Width, Height);
        WindowHelper.ForceForeground(hwnd);
        Activate();
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        _refreshTimer.Start();

        // Once the rows exist: stagger them in and put the highlight on the first one.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            AnimateRows();
            QueueGlide(snap: true);
        }));
    }

    private void HidePopup()
    {
        _refreshTimer.Stop();
        _openLoop.Stop();
        _glideLoop.Stop();
        Hide();
    }

    // ---- Animations -----------------------------------------------------------------------------

    /// <summary>Spring scale 0.94 to 1 with a fade and an 8 px slide up.</summary>
    private void StartOpenAnimation()
    {
        if (AnimationSettings.IsOff)
        {
            _open.Snap(1);
            ApplyOpen();
            return;
        }

        AnimationSettings.Configure(_open, 520, 0.6);
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
        PanelScale.ScaleX = v;
        PanelScale.ScaleY = v;
        PanelShift.Y = 8 * (1 - u);
        Panel.Opacity = Math.Clamp(u * 1.8, 0, 1);
    }

    /// <summary>List rows fade and slide in one after the other (a few ms apart).</summary>
    private void AnimateRows()
    {
        if (AnimationSettings.IsOff)
        {
            return;
        }

        int count = Math.Min(_view.Count, 12);
        for (int i = 0; i < count; i++)
        {
            if (EntryList.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement row)
            {
                TileAnimator.SlideFadeIn(row, i * 28);
            }
        }
    }

    /// <summary>Moves the highlight bar to the selected row after layout has run (coalesces repeated requests).</summary>
    private void QueueGlide(bool snap)
    {
        _glideSnapQueued |= snap;
        if (_glideQueued)
        {
            return;
        }

        _glideQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _glideQueued = false;
            bool snapNow = _glideSnapQueued;
            _glideSnapQueued = false;
            UpdateGlide(snapNow);
        }));
    }

    private void UpdateGlide(bool snap)
    {
        if (!IsVisible || EntryList.SelectedItem == null
            || EntryList.ItemContainerGenerator.ContainerFromItem(EntryList.SelectedItem) is not FrameworkElement row
            || row.ActualHeight < 1)
        {
            GlideBar.Visibility = Visibility.Collapsed;
            _glideVisible = false;
            _glideLoop.Stop();
            return;
        }

        double top;
        try
        {
            top = row.TranslatePoint(new Point(0, 0), ListHost).Y;
        }
        catch (InvalidOperationException)
        {
            return; // row not connected yet; the next layout pass will ask again
        }

        GlideBar.Visibility = Visibility.Visible;
        if (snap || !_glideVisible || AnimationSettings.IsOff)
        {
            _glideY.Snap(top);
            _glideH.Snap(row.ActualHeight);
            _glideLoop.Stop();
            ApplyGlide();
        }
        else
        {
            AnimationSettings.Configure(_glideY, 520, 0.8);
            AnimationSettings.Configure(_glideH, 520, 0.8);
            _glideY.Target = top;
            _glideH.Target = row.ActualHeight;
            _glideLoop.Start();
        }

        _glideVisible = true;
    }

    private bool OnGlideFrame(double dt)
    {
        _glideY.Step(dt);
        _glideH.Step(dt);
        ApplyGlide();
        if (_glideY.SettleIfClose(0.05, 0.5) & _glideH.SettleIfClose(0.05, 0.5))
        {
            ApplyGlide();
            return false;
        }

        return true;
    }

    private void ApplyGlide()
    {
        GlideShift.Y = _glideY.Value;
        GlideBar.Height = Math.Max(0, _glideH.Value);
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
        QueueGlide(snap: false); // pinned rows move; the highlight follows the entry
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
