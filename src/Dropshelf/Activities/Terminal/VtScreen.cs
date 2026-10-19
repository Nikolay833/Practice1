using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Dropshelf.Activities.Terminal;

/// <summary>One character cell: a code point (0 = blank, -1 = the right half of a wide character) and its colours.</summary>
internal struct VtCell
{
    public int Rune;
    public int Fore;   // -1 = default; otherwise 0xRRGGBB
    public int Back;   // -1 = none
    public byte Flags; // 1 bold, 2 dim, 4 inverse, 8 underline

    public static VtCell Blank => new() { Rune = 0, Fore = -1, Back = -1 };
}

/// <summary>
/// A small VT / xterm screen: the text grid a terminal shows, fed with the pseudo console's output. It understands what
/// ConPTY and interactive tools send: cursor movement and positioning, erasing, inserting and deleting characters and lines,
/// scroll regions, colours (16, 256 and true colour), bold / dim / inverse, the alternate screen, saved cursor, wide
/// characters, and the queries a program may ask (cursor position, device attributes), whose answers go to
/// <see cref="Reply"/>. Lines that scroll off the top are kept (up to 2000) for scrolling back.
/// </summary>
internal sealed class VtScreen
{
    private const int MaxScrollback = 2000;

    private enum State { Ground, Escape, EscapeIntermediate, Csi, Osc, OscEscape, Dcs, DcsEscape }

    private static readonly int[] BasicColors =
    {
        0x2E2E2E, 0xFF6B6B, 0x7EE787, 0xF2CC60, 0x79B8FF, 0xD2A8FF, 0x76E3EA, 0xD4D4D4,
        0x8B949E, 0xFF8E8E, 0xA5F0A5, 0xFFE08A, 0xA5D6FF, 0xE2C5FF, 0xA0F0F5, 0xFFFFFF,
    };

    private VtCell[][] _main;
    private VtCell[][]? _alternate;
    private readonly List<VtCell[]> _scrollback = new();
    private State _state = State.Ground;
    private readonly StringBuilder _params = new();
    private char _private;
    private char _intermediate;
    private int _top, _bottom;   // scroll region (inclusive)
    private bool _wrapPending;
    private int _savedX, _savedY;
    private VtCell _pen = VtCell.Blank;
    private VtCell _savedPen = VtCell.Blank;
    private char _highSurrogate;

    public VtScreen(int columns, int rows)
    {
        Columns = Math.Max(1, columns);
        Rows = Math.Max(1, rows);
        _main = NewGrid(Columns, Rows);
        _bottom = Rows - 1;
    }

    public int Columns { get; private set; }
    public int Rows { get; private set; }
    public int CursorX { get; private set; }
    public int CursorY { get; private set; }
    public bool CursorVisible { get; private set; } = true;
    public bool CursorKeysApplication { get; private set; }
    public bool BracketedPaste { get; private set; }
    public bool AlternateScreen => _alternate != null;

    /// <summary>The visible grid (rows of cells).</summary>
    public VtCell[][] Grid => _alternate ?? _main;

    public IReadOnlyList<VtCell[]> Scrollback => _scrollback;

    /// <summary>Answers to the program's queries (to be written back to its input).</summary>
    public event Action<string>? Reply;

    /// <summary>Default text colour (light grey).</summary>
    public static int DefaultFore => 0xD4D4D4;

    // ---- Size ----------------------------------------------------------------------------------------

    public void Resize(int columns, int rows)
    {
        columns = Math.Max(1, columns);
        rows = Math.Max(1, rows);
        if (columns == Columns && rows == Rows)
        {
            return;
        }

        // Keep the bottom of the screen (where the prompt is) when it gets shorter.
        int shift = Math.Max(0, CursorY - (rows - 1));
        _main = Fit(_main, columns, rows, shift, keepScrollback: true);
        if (_alternate != null)
        {
            _alternate = Fit(_alternate, columns, rows, shift, keepScrollback: false);
        }

        Columns = columns;
        Rows = rows;
        CursorY = Math.Clamp(CursorY - shift, 0, rows - 1);
        CursorX = Math.Clamp(CursorX, 0, columns - 1);
        _top = 0;
        _bottom = rows - 1;
        _wrapPending = false;
    }

    private VtCell[][] Fit(VtCell[][] grid, int columns, int rows, int shift, bool keepScrollback)
    {
        if (keepScrollback)
        {
            for (int i = 0; i < shift && i < grid.Length; i++)
            {
                PushScrollback(grid[i]);
            }
        }

        var fitted = NewGrid(columns, rows);
        for (int y = 0; y < rows; y++)
        {
            int from = y + shift;
            if (from >= grid.Length)
            {
                break;
            }

            Array.Copy(grid[from], fitted[y], Math.Min(columns, grid[from].Length));
        }

        return fitted;
    }

    private static VtCell[][] NewGrid(int columns, int rows)
    {
        var grid = new VtCell[rows][];
        for (int y = 0; y < rows; y++)
        {
            grid[y] = NewLine(columns);
        }

        return grid;
    }

    private static VtCell[] NewLine(int columns)
    {
        var line = new VtCell[columns];
        Array.Fill(line, VtCell.Blank);
        return line;
    }

    private void PushScrollback(VtCell[] line)
    {
        _scrollback.Add(line);
        if (_scrollback.Count > MaxScrollback)
        {
            _scrollback.RemoveRange(0, _scrollback.Count - MaxScrollback);
        }
    }

    // ---- Feeding ------------------------------------------------------------------------------------

    public void Feed(string text)
    {
        foreach (char c in text)
        {
            Step(c);
        }
    }

    private void Step(char c)
    {
        switch (_state)
        {
            case State.Ground:
                Ground(c);
                break;
            case State.Escape:
                EscapeChar(c);
                break;
            case State.EscapeIntermediate:
                _state = State.Ground; // the character set chosen (ESC ( B ...) is ignored
                break;
            case State.Csi:
                CsiChar(c);
                break;
            case State.Osc:
                if (c == '\a')
                {
                    _state = State.Ground; // window title and the like: ignored
                }
                else if (c == '\x1b')
                {
                    _state = State.OscEscape;
                }

                break;
            case State.OscEscape:
            case State.DcsEscape:
                _state = State.Ground; // ESC \ ends the string
                break;
            case State.Dcs:
                if (c == '\x1b')
                {
                    _state = State.DcsEscape;
                }

                break;
        }
    }

    private void Ground(char c)
    {
        switch (c)
        {
            case '\x1b':
                _state = State.Escape;
                return;
            case '\r':
                CursorX = 0;
                _wrapPending = false;
                return;
            case '\n':
            case '\v':
            case '\f':
                LineFeed();
                return;
            case '\b':
                if (CursorX > 0)
                {
                    CursorX--;
                }

                _wrapPending = false;
                return;
            case '\t':
                CursorX = Math.Min(Columns - 1, (CursorX / 8 + 1) * 8);
                return;
            case '\a':
                return;
        }

        if (c < ' ')
        {
            return;
        }

        if (char.IsHighSurrogate(c))
        {
            _highSurrogate = c;
            return;
        }

        int rune = c;
        if (char.IsLowSurrogate(c))
        {
            rune = _highSurrogate != 0 ? char.ConvertToUtf32(_highSurrogate, c) : 0xFFFD; // a lone half: replacement character
            _highSurrogate = '\0';
        }

        Print(rune);
    }

    private void Print(int rune)
    {
        int width = IsWide(rune) ? 2 : 1;
        if (_wrapPending)
        {
            CursorX = 0;
            LineFeed();
            _wrapPending = false;
        }

        if (width == 2 && CursorX == Columns - 1)
        {
            Grid[CursorY][CursorX] = WithPen(0);
            CursorX = 0;
            LineFeed();
        }

        VtCell[] line = Grid[CursorY];
        line[CursorX] = WithPen(rune);
        if (width == 2 && CursorX + 1 < Columns)
        {
            line[CursorX + 1] = WithPen(-1);
        }

        CursorX += width;
        if (CursorX >= Columns)
        {
            CursorX = Columns - 1;
            _wrapPending = true;
        }
    }

    private VtCell WithPen(int rune)
    {
        VtCell cell = _pen;
        cell.Rune = rune;
        return cell;
    }

    private void LineFeed()
    {
        if (CursorY == _bottom)
        {
            ScrollUp(1);
        }
        else if (CursorY < Rows - 1)
        {
            CursorY++;
        }
    }

    private void ReverseIndex()
    {
        if (CursorY == _top)
        {
            ScrollDown(1);
        }
        else if (CursorY > 0)
        {
            CursorY--;
        }
    }

    private void ScrollUp(int count)
    {
        VtCell[][] grid = Grid;
        for (int i = 0; i < count; i++)
        {
            if (_top == 0 && _alternate == null)
            {
                PushScrollback(grid[0]);
            }

            for (int y = _top; y < _bottom; y++)
            {
                grid[y] = grid[y + 1];
            }

            grid[_bottom] = BlankLine();
        }
    }

    private void ScrollDown(int count)
    {
        VtCell[][] grid = Grid;
        for (int i = 0; i < count; i++)
        {
            for (int y = _bottom; y > _top; y--)
            {
                grid[y] = grid[y - 1];
            }

            grid[_top] = BlankLine();
        }
    }

    /// <summary>A blank line in the current background colour (erasing keeps the background, like xterm).</summary>
    private VtCell[] BlankLine()
    {
        VtCell[] line = NewLine(Columns);
        if (_pen.Back != -1)
        {
            for (int x = 0; x < line.Length; x++)
            {
                line[x].Back = _pen.Back;
            }
        }

        return line;
    }

    private VtCell Erased => new() { Rune = 0, Fore = -1, Back = _pen.Back };

    // ---- Escape sequences ------------------------------------------------------------------------------

    private void EscapeChar(char c)
    {
        _state = State.Ground;
        switch (c)
        {
            case '[':
                _params.Clear();
                _private = '\0';
                _intermediate = '\0';
                _state = State.Csi;
                break;
            case ']':
                _state = State.Osc;
                break;
            case 'P':
                _state = State.Dcs;
                break;
            case '(':
            case ')':
            case '*':
            case '+':
                _state = State.EscapeIntermediate;
                break;
            case '7':
                SaveCursor();
                break;
            case '8':
                RestoreCursor();
                break;
            case 'D':
                LineFeed();
                break;
            case 'E':
                CursorX = 0;
                LineFeed();
                break;
            case 'M':
                ReverseIndex();
                break;
            case 'c':
                Reset();
                break;
        }
    }

    private void CsiChar(char c)
    {
        if (c >= '0' && c <= '9' || c == ';' || c == ':')
        {
            _params.Append(c == ':' ? ';' : c);
            return;
        }

        if (c is '?' or '>' or '=' or '<' && _params.Length == 0)
        {
            _private = c;
            return;
        }

        if (c >= ' ' && c <= '/')
        {
            _intermediate = c;
            return;
        }

        _state = State.Ground;
        if (c >= '@' && c <= '~')
        {
            try
            {
                Csi(c, ParseParams());
            }
            catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException)
            {
                // A malformed sequence: ignore it.
            }
        }
    }

    private List<int> ParseParams()
    {
        var list = new List<int>();
        if (_params.Length == 0)
        {
            return list;
        }

        foreach (string part in _params.ToString().Split(';'))
        {
            list.Add(part.Length == 0 ? 0 : int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int v) ? Math.Min(v, 99999) : 0);
        }

        return list;
    }

    private static int P(List<int> p, int index, int fallback) => index < p.Count && p[index] != 0 ? p[index] : fallback;

    private void Csi(char final, List<int> p)
    {
        if (_intermediate != '\0')
        {
            return; // cursor shape (CSI n SP q) and similar: ignored
        }

        if (_private == '?')
        {
            if (final is 'h' or 'l')
            {
                foreach (int mode in p)
                {
                    PrivateMode(mode, final == 'h');
                }
            }

            return;
        }

        if (_private != '\0')
        {
            if (_private == '>' && final == 'c')
            {
                Reply?.Invoke("\x1b[>0;10;1c");
            }

            return;
        }

        int n = P(p, 0, 1);
        switch (final)
        {
            case 'A':
                CursorY = Math.Max(CursorY < _top ? 0 : _top, CursorY - n);
                break;
            case 'B':
                CursorY = Math.Min(CursorY > _bottom ? Rows - 1 : _bottom, CursorY + n);
                break;
            case 'C':
                CursorX = Math.Min(Columns - 1, CursorX + n);
                break;
            case 'D':
                CursorX = Math.Max(0, CursorX - n);
                break;
            case 'E':
                CursorX = 0;
                CursorY = Math.Min(Rows - 1, CursorY + n);
                break;
            case 'F':
                CursorX = 0;
                CursorY = Math.Max(0, CursorY - n);
                break;
            case 'G':
            case '`':
                CursorX = Math.Clamp(n - 1, 0, Columns - 1);
                break;
            case 'd':
                CursorY = Math.Clamp(n - 1, 0, Rows - 1);
                break;
            case 'H':
            case 'f':
                CursorY = Math.Clamp(P(p, 0, 1) - 1, 0, Rows - 1);
                CursorX = Math.Clamp(P(p, 1, 1) - 1, 0, Columns - 1);
                break;
            case 'J':
                EraseDisplay(p.Count > 0 ? p[0] : 0);
                break;
            case 'K':
                EraseLine(p.Count > 0 ? p[0] : 0);
                break;
            case 'L':
                InsertLines(n);
                break;
            case 'M':
                DeleteLines(n);
                break;
            case '@':
                InsertChars(n);
                break;
            case 'P':
                DeleteChars(n);
                break;
            case 'X':
                for (int x = CursorX; x < Math.Min(Columns, CursorX + n); x++)
                {
                    Grid[CursorY][x] = Erased;
                }

                break;
            case 'S':
                ScrollUp(n);
                break;
            case 'T':
                ScrollDown(n);
                break;
            case 'r':
                _top = Math.Clamp(P(p, 0, 1) - 1, 0, Rows - 1);
                _bottom = Math.Clamp(P(p, 1, Rows) - 1, 0, Rows - 1);
                if (_bottom <= _top)
                {
                    _top = 0;
                    _bottom = Rows - 1;
                }

                CursorX = 0;
                CursorY = 0;
                break;
            case 'm':
                Sgr(p);
                break;
            case 's':
                SaveCursor();
                break;
            case 'u':
                RestoreCursor();
                break;
            case 'n':
                if (p.Count > 0 && p[0] == 6)
                {
                    Reply?.Invoke($"\x1b[{CursorY + 1};{CursorX + 1}R");
                }
                else if (p.Count > 0 && p[0] == 5)
                {
                    Reply?.Invoke("\x1b[0n");
                }

                break;
            case 'c':
                Reply?.Invoke("\x1b[?1;0c");
                break;
        }

        if (final is not ('m' or 'n' or 'c' or 's'))
        {
            _wrapPending = false;
        }
    }

    private void PrivateMode(int mode, bool on)
    {
        switch (mode)
        {
            case 1:
                CursorKeysApplication = on;
                break;
            case 25:
                CursorVisible = on;
                break;
            case 47:
            case 1047:
            case 1049:
                if (on && _alternate == null)
                {
                    if (mode == 1049)
                    {
                        SaveCursor();
                    }

                    _alternate = NewGrid(Columns, Rows);
                    CursorX = CursorY = 0;
                }
                else if (!on && _alternate != null)
                {
                    _alternate = null;
                    if (mode == 1049)
                    {
                        RestoreCursor();
                    }
                }

                _wrapPending = false;
                break;
            case 2004:
                BracketedPaste = on;
                break;
        }
    }

    private void SaveCursor()
    {
        _savedX = CursorX;
        _savedY = CursorY;
        _savedPen = _pen;
    }

    private void RestoreCursor()
    {
        CursorX = Math.Clamp(_savedX, 0, Columns - 1);
        CursorY = Math.Clamp(_savedY, 0, Rows - 1);
        _pen = _savedPen;
        _wrapPending = false;
    }

    private void Reset()
    {
        _main = NewGrid(Columns, Rows);
        _alternate = null;
        _pen = VtCell.Blank;
        CursorX = CursorY = 0;
        _top = 0;
        _bottom = Rows - 1;
        CursorVisible = true;
        _wrapPending = false;
    }

    private void EraseDisplay(int mode)
    {
        VtCell[][] grid = Grid;
        switch (mode)
        {
            case 0:
                EraseLine(0);
                for (int y = CursorY + 1; y < Rows; y++)
                {
                    grid[y] = BlankLine();
                }

                break;
            case 1:
                EraseLine(1);
                for (int y = 0; y < CursorY; y++)
                {
                    grid[y] = BlankLine();
                }

                break;
            case 2:
                for (int y = 0; y < Rows; y++)
                {
                    grid[y] = BlankLine();
                }

                break;
            case 3:
                _scrollback.Clear();
                break;
        }
    }

    private void EraseLine(int mode)
    {
        VtCell[] line = Grid[CursorY];
        int from = mode == 0 ? CursorX : 0;
        int to = mode == 1 ? CursorX : Columns - 1;
        for (int x = from; x <= to && x < Columns; x++)
        {
            line[x] = Erased;
        }
    }

    private void InsertLines(int n)
    {
        if (CursorY < _top || CursorY > _bottom)
        {
            return;
        }

        int saved = _top;
        _top = CursorY;
        ScrollDown(Math.Min(n, _bottom - CursorY + 1));
        _top = saved;
        CursorX = 0;
    }

    private void DeleteLines(int n)
    {
        if (CursorY < _top || CursorY > _bottom)
        {
            return;
        }

        int saved = _top;
        _top = CursorY;
        VtCell[][] grid = Grid;
        for (int i = 0; i < Math.Min(n, _bottom - CursorY + 1); i++)
        {
            for (int y = _top; y < _bottom; y++)
            {
                grid[y] = grid[y + 1];
            }

            grid[_bottom] = BlankLine();
        }

        _top = saved;
        CursorX = 0;
    }

    private void InsertChars(int n)
    {
        VtCell[] line = Grid[CursorY];
        n = Math.Min(n, Columns - CursorX);
        Array.Copy(line, CursorX, line, CursorX + n, Columns - CursorX - n);
        for (int x = CursorX; x < CursorX + n; x++)
        {
            line[x] = Erased;
        }
    }

    private void DeleteChars(int n)
    {
        VtCell[] line = Grid[CursorY];
        n = Math.Min(n, Columns - CursorX);
        Array.Copy(line, CursorX + n, line, CursorX, Columns - CursorX - n);
        for (int x = Columns - n; x < Columns; x++)
        {
            line[x] = Erased;
        }
    }

    // ---- Colours ------------------------------------------------------------------------------------------

    private void Sgr(List<int> p)
    {
        if (p.Count == 0)
        {
            _pen = VtCell.Blank;
            return;
        }

        for (int i = 0; i < p.Count; i++)
        {
            int code = p[i];
            switch (code)
            {
                case 0:
                    _pen = VtCell.Blank;
                    break;
                case 1:
                    _pen.Flags |= 1;
                    break;
                case 2:
                    _pen.Flags |= 2;
                    break;
                case 4:
                    _pen.Flags |= 8;
                    break;
                case 7:
                    _pen.Flags |= 4;
                    break;
                case 22:
                    _pen.Flags &= unchecked((byte)~3);
                    break;
                case 24:
                    _pen.Flags &= unchecked((byte)~8);
                    break;
                case 27:
                    _pen.Flags &= unchecked((byte)~4);
                    break;
                case >= 30 and <= 37:
                    _pen.Fore = BasicColors[code - 30];
                    break;
                case >= 90 and <= 97:
                    _pen.Fore = BasicColors[code - 90 + 8];
                    break;
                case 39:
                    _pen.Fore = -1;
                    break;
                case >= 40 and <= 47:
                    _pen.Back = BasicColors[code - 40];
                    break;
                case >= 100 and <= 107:
                    _pen.Back = BasicColors[code - 100 + 8];
                    break;
                case 49:
                    _pen.Back = -1;
                    break;
                case 38:
                case 48:
                    int color = ExtendedColor(p, ref i);
                    if (color >= 0)
                    {
                        if (code == 38)
                        {
                            _pen.Fore = color;
                        }
                        else
                        {
                            _pen.Back = color;
                        }
                    }

                    break;
            }
        }
    }

    /// <summary>38;5;n (256 colours) or 38;2;r;g;b (true colour). Advances <paramref name="i"/> past what it used.</summary>
    private static int ExtendedColor(List<int> p, ref int i)
    {
        if (i + 1 >= p.Count)
        {
            return -1;
        }

        if (p[i + 1] == 5 && i + 2 < p.Count)
        {
            int n = Math.Clamp(p[i + 2], 0, 255);
            i += 2;
            if (n < 16)
            {
                return BasicColors[n];
            }

            if (n < 232)
            {
                n -= 16;
                int r = n / 36, g = n / 6 % 6, b = n % 6;
                static int Level(int v) => v == 0 ? 0 : 55 + v * 40;
                return Level(r) << 16 | Level(g) << 8 | Level(b);
            }

            int gray = 8 + (n - 232) * 10;
            return gray << 16 | gray << 8 | gray;
        }

        if (p[i + 1] == 2 && i + 4 < p.Count)
        {
            int color = Math.Clamp(p[i + 2], 0, 255) << 16 | Math.Clamp(p[i + 3], 0, 255) << 8 | Math.Clamp(p[i + 4], 0, 255);
            i += 4;
            return color;
        }

        return -1;
    }

    /// <summary>East Asian wide characters and emoji take two cells (a simple table that covers the common ranges).</summary>
    private static bool IsWide(int r) =>
        r >= 0x1100 && (r <= 0x115F || r is >= 0x2E80 and <= 0xA4CF and not 0x303F || r is >= 0xAC00 and <= 0xD7A3
                        || r is >= 0xF900 and <= 0xFAFF || r is >= 0xFE30 and <= 0xFE4F || r is >= 0xFF00 and <= 0xFF60
                        || r is >= 0xFFE0 and <= 0xFFE6 || r is >= 0x1F300 and <= 0x1F64F || r is >= 0x1F900 and <= 0x1FAFF
                        || r is >= 0x20000 and <= 0x3FFFD);

    // ---- Text out ----------------------------------------------------------------------------------------

    /// <summary>The text of a line (for copying), without trailing blanks.</summary>
    public static string TextOf(VtCell[] line, int from = 0, int to = int.MaxValue)
    {
        var sb = new StringBuilder();
        for (int x = Math.Max(0, from); x < line.Length && x <= to; x++)
        {
            int rune = line[x].Rune;
            if (rune == -1)
            {
                continue;
            }

            sb.Append(rune == 0 ? " " : char.ConvertFromUtf32(rune));
        }

        return sb.ToString().TrimEnd();
    }
}
