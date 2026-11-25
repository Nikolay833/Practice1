using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Dropshelf.Services;

namespace Dropshelf.Activities.Terminal;

/// <summary>An island element that needs the keyboard when clicked (the island then lets go of its no-activate style).</summary>
internal interface IWantsKeyboard
{
}

/// <summary>
/// Draws the terminal: monospaced light grey text straight on the (transparent) island, colours where programs use them, a
/// soft block cursor, and a selection. Click to type; every key goes to the shell as a terminal would send it (Esc, Ctrl+C,
/// arrows, Tab, function keys). Drag to select and copy; Ctrl+V, Shift+Insert or a right-click paste. The wheel scrolls
/// back through earlier output.
/// </summary>
internal sealed class TerminalScreen : FrameworkElement, IWantsKeyboard
{
    private const double FontSize = 13;
    private const double Pad = 2;

    private static readonly Brush HitBrush = Frozen(Color.FromArgb(0x01, 0, 0, 0));   // invisible, but takes the mouse
    private static readonly Brush SelectionBrush = Frozen(Color.FromArgb(0x55, 0x4A, 0x4A, 0x4F));

    private readonly TerminalSession _session;
    private readonly Typeface _typeface;
    private readonly Typeface _bold;
    private readonly Dictionary<int, Brush> _brushes = new();
    private readonly double _cellWidth;
    private readonly double _cellHeight;
    private int _scrollOffset;              // lines scrolled back from the bottom
    private (int Row, int Col)? _selStart;
    private (int Row, int Col)? _selEnd;

    // Drawing: one retained visual per row plus an overlay (selection, cursor) on top. A repaint only redraws the rows whose
    // cells changed since they were last drawn, so a spinner or a typed character costs one row, not the whole screen, and
    // nothing at all is drawn while the island is closed.
    private readonly VisualCollection _visuals;
    private readonly List<DrawingVisual> _rows = new();
    private readonly List<VtCell[]?> _drawn = new();   // what each row visual currently shows
    private readonly DrawingVisual _overlay = new();
    private bool _paintQueued;
    private bool _paused;

    /// <summary>Nothing is drawn while paused; un-pausing draws whatever changed meanwhile.</summary>
    public bool Paused
    {
        get => _paused;
        set
        {
            if (_paused == value)
            {
                return;
            }

            _paused = value;
            if (!value)
            {
                Redraw();
            }
        }
    }
    private long _lastPaint;
    private DispatcherTimer? _paintTimer;
    private const int MinPaintMs = 33;   // at most ~30 paints a second: every paint re-sends the whole layered window

    public TerminalScreen(TerminalSession session)
    {
        _session = session;
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.IBeam;
        var family = new FontFamily("Cascadia Mono, Cascadia Code, Consolas, Courier New");
        _typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _bold = new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var probe = Text("M", -1, false);
        _cellWidth = Math.Max(4, probe.WidthIncludingTrailingWhitespace);
        _cellHeight = Math.Ceiling(FontSize * 1.32);

        _visuals = new VisualCollection(this) { _overlay };
        _session.Changed += OnChanged;
        Loaded += (_, _) =>
        {
            _dpiScale = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            ForgetRows();
            Redraw();
        };
        IsKeyboardFocusWithinChanged += (_, _) => Redraw();
        IsVisibleChanged += (_, _) => Redraw(); // catch up on output that arrived while the island was closed
    }

    protected override int VisualChildrenCount => _visuals.Count;

    protected override Visual GetVisualChild(int index) => _visuals[index];

    private double _dpiScale = 1;

    private void OnChanged()
    {
        _scrollOffset = 0;
        Redraw();
    }

    // ---- Size --------------------------------------------------------------------------------------------

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        ForgetRows();
        Redraw();
        int columns = Math.Max(20, (int)((ActualWidth - 2 * Pad) / _cellWidth));
        int rows = Math.Max(4, (int)((ActualHeight - 2 * Pad) / _cellHeight));
        if (ActualWidth < 40 || ActualHeight < 40)
        {
            return;
        }

        if (!_session.IsRunning && !_session.HasEnded)
        {
            _session.EnsureStarted(columns, rows); // the first time the terminal is on screen
        }
        else if (columns != _session.Screen.Columns || rows != _session.Screen.Rows)
        {
            _session.Resize(columns, rows);
        }
    }

    // ---- Drawing -------------------------------------------------------------------------------------------

    /// <summary>Only the invisible hit area: the text lives in the row visuals, which <see cref="Paint"/> keeps up to date.</summary>
    protected override void OnRender(DrawingContext dc) => dc.DrawRectangle(HitBrush, null, new Rect(RenderSize));

    /// <summary>Asks for one repaint at render priority; any number of changes before it is drawn cost a single paint.</summary>
    private void Redraw()
    {
        if (_paintQueued)
        {
            return;
        }

        _paintQueued = true;
        long wait = MinPaintMs - (Environment.TickCount64 - _lastPaint);
        if (wait <= 0)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(Paint));
            return;
        }

        // Output is streaming in (claude writing, a spinner): wait for the rest of the frame and draw it all at once.
        if (_paintTimer == null)
        {
            _paintTimer = new DispatcherTimer(DispatcherPriority.Render);
            _paintTimer.Tick += (_, _) =>
            {
                _paintTimer.Stop();
                Paint();
            };
        }

        _paintTimer.Interval = TimeSpan.FromMilliseconds(wait);
        _paintTimer.Start();
    }

    /// <summary>Every row is drawn again on the next paint (size or DPI changed).</summary>
    private void ForgetRows()
    {
        for (int i = 0; i < _drawn.Count; i++)
        {
            _drawn[i] = null;
        }
    }

    private void Paint()
    {
        _paintQueued = false;
        _lastPaint = Environment.TickCount64;
        if (!IsVisible || _paused)
        {
            return; // the island is closed: draw nothing now; opening it (IsVisibleChanged, Paused) repaints
        }

        VtScreen screen = _session.Screen;
        List<VtCell[]> lines = VisibleLines(screen);
        while (_rows.Count < lines.Count)
        {
            var visual = new DrawingVisual();
            _rows.Add(visual);
            _drawn.Add(null);
            _visuals.Insert(_visuals.Count - 1, visual); // rows stay under the overlay
        }

        while (_rows.Count > lines.Count)
        {
            _visuals.Remove(_rows[^1]);
            _rows.RemoveAt(_rows.Count - 1);
            _drawn.RemoveAt(_drawn.Count - 1);
        }

        int width = screen.Columns;
        for (int row = 0; row < lines.Count; row++)
        {
            ReadOnlySpan<VtCell> cells = lines[row].AsSpan(0, Math.Min(lines[row].Length, width));
            VtCell[]? drawn = _drawn[row];
            if (drawn != null && drawn.Length == cells.Length
                && MemoryMarshal.AsBytes(cells).SequenceEqual(MemoryMarshal.AsBytes(drawn.AsSpan())))
            {
                continue; // unchanged since it was drawn
            }

            using (DrawingContext dc = _rows[row].RenderOpen())
            {
                DrawLine(dc, lines[row], row);
            }

            _drawn[row] = cells.ToArray();
        }

        using DrawingContext overlay = _overlay.RenderOpen();
        DrawOverlay(overlay, screen);
    }

    private void DrawOverlay(DrawingContext dc, VtScreen screen)
    {
        DrawSelection(dc);

        if (_scrollOffset == 0 && screen.CursorVisible && !_session.HasEnded)
        {
            var cell = new Rect(Pad + screen.CursorX * _cellWidth, Pad + screen.CursorY * _cellHeight, _cellWidth, _cellHeight);
            if (IsKeyboardFocusWithin)
            {
                dc.DrawRectangle(Brush(VtScreen.DefaultFore, 0.55), null, cell);
            }
            else
            {
                dc.DrawRectangle(null, new Pen(Brush(VtScreen.DefaultFore, 0.5), 1), new Rect(cell.X + 0.5, cell.Y + 0.5, cell.Width - 1, cell.Height - 1));
            }
        }
    }

    /// <summary>The screen, or (scrolled back) the end of the scrollback followed by the top of the screen.</summary>
    private List<VtCell[]> VisibleLines(VtScreen screen)
    {
        var lines = new List<VtCell[]>(screen.Rows);
        IReadOnlyList<VtCell[]> back = screen.Scrollback;
        _scrollOffset = Math.Clamp(_scrollOffset, 0, screen.AlternateScreen ? 0 : back.Count);
        int first = back.Count - _scrollOffset;
        for (int row = 0; row < screen.Rows; row++)
        {
            int index = first + row;
            lines.Add(index < back.Count ? back[index] : screen.Grid[index - back.Count]);
        }

        return lines;
    }

    /// <summary>Draws a line as runs of cells with the same look; a wide character is drawn on its own at its column.</summary>
    private void DrawLine(DrawingContext dc, VtCell[] line, int row)
    {
        double y = Pad + row * _cellHeight;
        int x = 0;
        int width = Math.Min(line.Length, _session.Screen.Columns);
        while (x < width)
        {
            VtCell first = line[x];
            if (first.Rune == -1)
            {
                x++; // the right half of a wide character drawn just before
                continue;
            }

            int start = x;
            var text = new System.Text.StringBuilder();
            bool wide = x + 1 < width && line[x + 1].Rune == -1;
            if (wide || first.Rune > 0xFFFF)
            {
                text.Append(RuneText(first.Rune));
                x += wide ? 2 : 1;
            }
            else
            {
                while (x < width)
                {
                    VtCell cell = line[x];
                    bool special = cell.Rune == -1 || cell.Rune > 0xFFFF || x + 1 < width && line[x + 1].Rune == -1;
                    if (x > start && (special || cell.Fore != first.Fore || cell.Back != first.Back || cell.Flags != first.Flags))
                    {
                        break;
                    }

                    text.Append(cell.Rune <= 0 ? ' ' : (char)cell.Rune);
                    x++;
                }
            }

            // Monochrome on purpose: every program's colours are ignored and all text is the same dark grey. A coloured or
            // inverse background (a selected menu item, a highlighted line) becomes a faint grey band so it still shows.
            bool highlighted = (first.Flags & 4) != 0 || first.Back != -1;
            int fore = VtScreen.DefaultFore;

            double left = Pad + start * _cellWidth;
            if (highlighted)
            {
                dc.DrawRectangle(Brush(VtScreen.DefaultFore, 0.16), null, new Rect(left, y, (x - start) * _cellWidth, _cellHeight));
            }

            string run = text.ToString();
            if (run.Trim().Length == 0)
            {
                continue;
            }

            double opacity = (first.Flags & 2) != 0 ? 0.6 : 1;
            FormattedText formatted = Text(run, fore, (first.Flags & 1) != 0, opacity);
            if ((first.Flags & 8) != 0)
            {
                formatted.SetTextDecorations(TextDecorations.Underline);
            }

            dc.DrawText(formatted, new Point(left, y + (_cellHeight - formatted.Height) / 2));
        }
    }

    private void DrawSelection(DrawingContext dc)
    {
        if (Ordered() is not { } sel)
        {
            return;
        }

        for (int row = sel.From.Row; row <= sel.To.Row; row++)
        {
            int from = row == sel.From.Row ? sel.From.Col : 0;
            int to = row == sel.To.Row ? sel.To.Col : _session.Screen.Columns - 1;
            dc.DrawRectangle(SelectionBrush, null, new Rect(Pad + from * _cellWidth, Pad + row * _cellHeight, (to - from + 1) * _cellWidth, _cellHeight));
        }
    }

    private static string RuneText(int rune)
    {
        if (rune <= 0 || rune > 0x10FFFF || rune is >= 0xD800 and <= 0xDFFF)
        {
            return " ";
        }

        return char.ConvertFromUtf32(rune);
    }

    private FormattedText Text(string text, int color, bool bold, double opacity = 1) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, bold ? _bold : _typeface, FontSize,
            Brush(color == -1 ? VtScreen.DefaultFore : color, opacity), _dpiScale);

    private Brush Brush(int rgb, double opacity = 1)
    {
        int key = rgb ^ (int)(opacity * 100) << 24;
        if (!_brushes.TryGetValue(key, out Brush? brush))
        {
            var color = Color.FromArgb((byte)Math.Round(255 * opacity), (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            brush = Frozen(color);
            _brushes[key] = brush;
        }

        return brush;
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // ---- Keyboard ------------------------------------------------------------------------------------------

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        ModifierKeys mods = Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(ModifierKeys.Control), shift = mods.HasFlag(ModifierKeys.Shift), alt = mods.HasFlag(ModifierKeys.Alt);

        // Copy, paste and scrolling stay with the terminal window, like Windows Terminal.
        if ((ctrl && key == Key.V) || (shift && key == Key.Insert))
        {
            Paste();
            e.Handled = true;
            return;
        }

        if (ctrl && key == Key.C && Ordered() != null)
        {
            CopySelection();
            e.Handled = true;
            return;
        }

        if (shift && key is Key.PageUp or Key.PageDown)
        {
            Scroll(key == Key.PageUp ? _session.Screen.Rows - 1 : -(_session.Screen.Rows - 1));
            e.Handled = true;
            return;
        }

        string? send = Translate(key, ctrl, shift, alt);
        if (send != null)
        {
            ClearSelection();
            _scrollOffset = 0;
            _session.Send(send);
            e.Handled = true;
        }
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);
        if (!string.IsNullOrEmpty(e.Text) && e.Text[0] >= ' ')
        {
            ClearSelection();
            _scrollOffset = 0;
            _session.Send(e.Text);
            e.Handled = true;
        }
    }

    /// <summary>The bytes a VT terminal sends for a key; null for keys that type text (they arrive as text input).</summary>
    private string? Translate(Key key, bool ctrl, bool shift, bool alt)
    {
        string mod = (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0) is var m and > 0 ? $";{m + 1}" : string.Empty;
        bool app = _session.Screen.CursorKeysApplication && mod.Length == 0;
        string Arrow(char c) => app ? $"\x1bO{c}" : mod.Length > 0 ? $"\x1b[1{mod}{c}" : $"\x1b[{c}";
        string Tilde(int n) => $"\x1b[{n}{mod}~";

        switch (key)
        {
            case Key.Enter:
                return alt ? "\x1b\r" : "\r";
            case Key.Back:
                return ctrl ? "\x08" : alt ? "\x1b\x7f" : "\x7f";
            case Key.Tab:
                return shift ? "\x1b[Z" : "\t";
            case Key.Escape:
                return "\x1b";
            case Key.Up:
                return Arrow('A');
            case Key.Down:
                return Arrow('B');
            case Key.Right:
                return Arrow('C');
            case Key.Left:
                return Arrow('D');
            case Key.Home:
                return Arrow('H');
            case Key.End:
                return Arrow('F');
            case Key.Insert:
                return Tilde(2);
            case Key.Delete:
                return Tilde(3);
            case Key.PageUp:
                return Tilde(5);
            case Key.PageDown:
                return Tilde(6);
            case >= Key.F1 and <= Key.F4:
                return mod.Length > 0 ? $"\x1b[1{mod}{"PQRS"[key - Key.F1]}" : $"\x1bO{"PQRS"[key - Key.F1]}";
            case >= Key.F5 and <= Key.F12:
                return Tilde(new[] { 15, 17, 18, 19, 20, 21, 23, 24 }[key - Key.F5]);
            case Key.Space when ctrl:
                return "\0";
        }

        if (ctrl && !alt && key >= Key.A && key <= Key.Z)
        {
            return ((char)(key - Key.A + 1)).ToString(); // Ctrl+A .. Ctrl+Z (Ctrl+C interrupts)
        }

        if (ctrl && !alt)
        {
            return key switch
            {
                Key.OemOpenBrackets => "\x1b",
                Key.OemBackslash or Key.Oem5 => "\x1c",
                Key.OemCloseBrackets => "\x1d",
                _ => null,
            };
        }

        if (alt && !ctrl && key >= Key.A && key <= Key.Z)
        {
            char letter = (char)('a' + (key - Key.A));
            return "\x1b" + (shift ? char.ToUpperInvariant(letter) : letter);
        }

        return null;
    }

    private void Paste()
    {
        try
        {
            if (!System.Windows.Clipboard.ContainsText())
            {
                return;
            }

            string text = System.Windows.Clipboard.GetText().Replace("\r\n", "\r").Replace('\n', '\r');
            if (_session.Screen.BracketedPaste)
            {
                text = "\x1b[200~" + text + "\x1b[201~";
            }

            _scrollOffset = 0;
            _session.Send(text);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or System.Runtime.InteropServices.ExternalException)
        {
            Log.Info($"Terminal paste failed: {ex.Message}");
        }
    }

    // ---- Mouse: focus, selection, scrolling --------------------------------------------------------------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        Keyboard.Focus(this);
        _selStart = _selEnd = CellAt(e.GetPosition(this));
        CaptureMouse();
        Redraw();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsMouseCaptured && _selStart != null)
        {
            _selEnd = CellAt(e.GetPosition(this));
            Redraw();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!IsMouseCaptured)
        {
            return;
        }

        ReleaseMouseCapture();
        if (_selStart == _selEnd)
        {
            ClearSelection(); // a plain click: just focus
        }
        else
        {
            CopySelection(clear: false); // selecting copies, like Windows Terminal's "copy on select"
        }
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        Focus();
        Keyboard.Focus(this);
        if (Ordered() != null)
        {
            CopySelection();
        }
        else
        {
            Paste();
        }

        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        int lines = e.Delta > 0 ? 3 : -3;
        if (_session.Screen.AlternateScreen)
        {
            // Full-screen programs (less, vim) scroll themselves.
            for (int i = 0; i < Math.Abs(lines); i++)
            {
                _session.Send(lines > 0 ? "\x1b[A" : "\x1b[B");
            }
        }
        else
        {
            Scroll(lines);
        }

        e.Handled = true;
    }

    private void Scroll(int lines)
    {
        _scrollOffset = Math.Max(0, _scrollOffset + lines);
        ClearSelection();
        Redraw();
    }

    private (int Row, int Col) CellAt(Point p) =>
        (Math.Clamp((int)((p.Y - Pad) / _cellHeight), 0, _session.Screen.Rows - 1),
         Math.Clamp((int)((p.X - Pad) / _cellWidth), 0, _session.Screen.Columns - 1));

    private ((int Row, int Col) From, (int Row, int Col) To)? Ordered()
    {
        if (_selStart is not { } a || _selEnd is not { } b || a == b)
        {
            return null;
        }

        return a.Row < b.Row || a.Row == b.Row && a.Col <= b.Col ? (a, b) : (b, a);
    }

    private void CopySelection(bool clear = true)
    {
        if (Ordered() is not { } sel)
        {
            return;
        }

        List<VtCell[]> lines = VisibleLines(_session.Screen);
        var text = new System.Text.StringBuilder();
        for (int row = sel.From.Row; row <= sel.To.Row && row < lines.Count; row++)
        {
            int from = row == sel.From.Row ? sel.From.Col : 0;
            int to = row == sel.To.Row ? sel.To.Col : int.MaxValue;
            text.Append(VtScreen.TextOf(lines[row], from, to));
            if (row < sel.To.Row)
            {
                text.Append("\r\n");
            }
        }

        try
        {
            System.Windows.Clipboard.SetText(text.ToString());
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or System.Runtime.InteropServices.ExternalException)
        {
            Log.Info($"Terminal copy failed: {ex.Message}");
        }

        if (clear)
        {
            ClearSelection();
        }
    }

    private void ClearSelection()
    {
        if (_selStart != null)
        {
            _selStart = _selEnd = null;
            Redraw();
        }
    }
}
