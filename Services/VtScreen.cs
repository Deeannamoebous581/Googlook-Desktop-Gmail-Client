using System;
using System.Collections.Generic;

namespace Googlook.Services;

/// <summary>
/// A compact VT/ANSI screen-buffer emulator. It parses the console byte stream
/// (printables + the common escape sequences: cursor moves, erase, SGR colour,
/// scroll) into a grid of coloured cells with a cursor. Enough to render the Gemini
/// CLI's prompts, menus, and colour output legibly.
///
/// Scope note: covers the widely-used subset, not every sequence. Truecolour
/// (38;2;r;g;b) collapses to the default, and a few rare CSI ops are ignored rather
/// than mis-rendered. Feed() is thread-safe with Snapshot(); render off Snapshot().
/// </summary>
public sealed class VtScreen
{
    public struct Cell
    {
        public char Ch;
        public byte Fg;      // palette index, or 255 = default
        public byte Bg;      // palette index, or 255 = default
        public bool Bold;
        public bool Inverse;
    }

    /// <summary>16-colour ANSI palette (VS Code's dark values) for rendering.</summary>
    public static readonly string[] Palette =
    {
        "#000000","#CD3131","#0DBC79","#E5E510","#2472C8","#BC3FBC","#11A8CD","#E5E5E5",
        "#666666","#F14C4C","#23D18B","#F5F543","#3B8EEA","#D670D6","#29B8DB","#FFFFFF",
    };
    public const string DefaultFg = "#E6E6E6";

    public int Cols { get; private set; }
    public int Rows { get; private set; }

    private Cell[][] _grid = Array.Empty<Cell[]>();
    private int _cx, _cy;
    private int _savedX, _savedY;
    private int _scrollTop, _scrollBottom;
    private bool _cursorVisible = true;

    // current pen
    private byte _fg = 255, _bg = 255;
    private bool _bold, _inverse;

    // parser
    private enum State { Normal, Esc, EscConsume, Csi, Osc }
    private State _state = State.Normal;
    private readonly List<int> _params = new();
    private int _cur;
    private bool _hasCur;
    private bool _private;   // CSI '?' prefix

    private readonly object _lock = new();

    public VtScreen(int cols, int rows) => Resize(cols, rows);

    public void Resize(int cols, int rows)
    {
        lock (_lock)
        {
            cols = Math.Max(2, cols);
            rows = Math.Max(2, rows);
            var g = new Cell[rows][];
            for (int y = 0; y < rows; y++)
            {
                g[y] = new Cell[cols];
                for (int x = 0; x < cols; x++) g[y][x] = Blank();
                if (y < _grid.Length)
                    for (int x = 0; x < cols && x < _grid[y].Length; x++) g[y][x] = _grid[y][x];
            }
            _grid = g; Cols = cols; Rows = rows;
            _scrollTop = 0; _scrollBottom = rows - 1;
            _cx = Math.Min(_cx, cols - 1);
            _cy = Math.Min(_cy, rows - 1);
        }
    }

    public (Cell[][] cells, int cx, int cy, bool cursorVisible) Snapshot()
    {
        lock (_lock)
        {
            var copy = new Cell[Rows][];
            for (int y = 0; y < Rows; y++) { copy[y] = new Cell[Cols]; Array.Copy(_grid[y], copy[y], Cols); }
            return (copy, _cx, _cy, _cursorVisible);
        }
    }

    public void Feed(string s)
    {
        lock (_lock)
        {
            foreach (var ch in s) Step(ch);
        }
    }

    private Cell Blank() => new() { Ch = ' ', Fg = 255, Bg = 255 };

    private void Step(char ch)
    {
        switch (_state)
        {
            case State.Normal: Normal(ch); break;
            case State.Esc:    Esc(ch);    break;
            case State.EscConsume: _state = State.Normal; break; // swallow charset-set byte
            case State.Csi:    Csi(ch);    break;
            case State.Osc:    Osc(ch);    break;
        }
    }

    private void Normal(char ch)
    {
        switch (ch)
        {
            case '\x1b': _state = State.Esc; break;
            case '\n': LineFeed(); break;
            case '\r': _cx = 0; break;
            case '\t': _cx = Math.Min(Cols - 1, (_cx / 8 + 1) * 8); break;
            case '\b': _cx = Math.Max(0, _cx - 1); break;
            case '\a': break;
            default:
                if (ch >= ' ') Put(ch);
                break;
        }
    }

    private void Put(char ch)
    {
        if (_cx >= Cols) { _cx = 0; LineFeed(); }
        _grid[_cy][_cx] = new Cell { Ch = ch, Fg = _fg, Bg = _bg, Bold = _bold, Inverse = _inverse };
        _cx++;
    }

    private void LineFeed()
    {
        if (_cy == _scrollBottom) ScrollUp(1);
        else _cy = Math.Min(Rows - 1, _cy + 1);
    }

    private void ScrollUp(int n)
    {
        for (int i = 0; i < n; i++)
        {
            for (int y = _scrollTop; y < _scrollBottom; y++) _grid[y] = _grid[y + 1];
            _grid[_scrollBottom] = new Cell[Cols];
            for (int x = 0; x < Cols; x++) _grid[_scrollBottom][x] = Blank();
        }
    }

    private void Esc(char ch)
    {
        switch (ch)
        {
            case '[': _params.Clear(); _cur = 0; _hasCur = false; _private = false; _state = State.Csi; break;
            case ']': _state = State.Osc; break;
            case '7': _savedX = _cx; _savedY = _cy; _state = State.Normal; break;
            case '8': _cx = _savedX; _cy = _savedY; _state = State.Normal; break;
            case 'M': _cy = Math.Max(0, _cy - 1); _state = State.Normal; break;
            case '(': case ')': case '*': case '+': _state = State.EscConsume; break; // charset: eat next
            default: _state = State.Normal; break;
        }
    }

    private void Csi(char ch)
    {
        if (ch == '?') { _private = true; return; }
        if (ch >= '0' && ch <= '9') { _cur = _cur * 10 + (ch - '0'); _hasCur = true; return; }
        if (ch == ';') { _params.Add(_hasCur ? _cur : 0); _cur = 0; _hasCur = false; return; }
        if (ch >= ' ' && ch <= '/') return; // intermediate bytes — ignore
        _params.Add(_hasCur ? _cur : 0);    // final byte: flush last param
        Dispatch(ch);
        _state = State.Normal;
    }

    private int P1(int i, int def) { var v = i < _params.Count ? _params[i] : def; return v == 0 ? def : v; }

    private void Dispatch(char cmd)
    {
        switch (cmd)
        {
            case 'A': _cy = Math.Max(0, _cy - P1(0, 1)); break;
            case 'B': _cy = Math.Min(Rows - 1, _cy + P1(0, 1)); break;
            case 'C': _cx = Math.Min(Cols - 1, _cx + P1(0, 1)); break;
            case 'D': _cx = Math.Max(0, _cx - P1(0, 1)); break;
            case 'E': _cy = Math.Min(Rows - 1, _cy + P1(0, 1)); _cx = 0; break;
            case 'F': _cy = Math.Max(0, _cy - P1(0, 1)); _cx = 0; break;
            case 'G': _cx = Clamp(P1(0, 1) - 1, 0, Cols - 1); break;
            case 'd': _cy = Clamp(P1(0, 1) - 1, 0, Rows - 1); break;
            case 'H': case 'f':
                _cy = Clamp(P1(0, 1) - 1, 0, Rows - 1);
                _cx = Clamp(P1(1, 1) - 1, 0, Cols - 1);
                break;
            case 'J': EraseDisplay(_params.Count > 0 ? _params[0] : 0); break;
            case 'K': EraseLine(_params.Count > 0 ? _params[0] : 0); break;
            case 'X': EraseChars(P1(0, 1)); break;
            case 'm': Sgr(); break;
            case 'r':
                _scrollTop = Clamp(P1(0, 1) - 1, 0, Rows - 1);
                _scrollBottom = Clamp(P1(1, Rows) - 1, 0, Rows - 1);
                if (_scrollBottom < _scrollTop) _scrollBottom = _scrollTop;
                _cx = 0; _cy = _scrollTop;
                break;
            case 'S': ScrollUp(P1(0, 1)); break;
            case 'L': InsertLines(P1(0, 1)); break;
            case 'P': DeleteChars(P1(0, 1)); break;
            case 'h': case 'l': SetMode(cmd == 'h'); break;
            default: break; // ignore the rest gracefully
        }
    }

    private void SetMode(bool on)
    {
        if (!_private) return;
        foreach (var p in _params)
        {
            if (p == 25) _cursorVisible = on;                 // cursor visibility
            else if (p == 1049 || p == 47 || p == 1047)       // alt screen (approximate: clear)
            {
                EraseDisplay(2);
                _cx = 0; _cy = 0;
            }
        }
    }

    private void EraseDisplay(int mode)
    {
        if (mode == 0)      { EraseLine(0); for (int y = _cy + 1; y < Rows; y++) ClearRow(y); }
        else if (mode == 1) { for (int y = 0; y < _cy; y++) ClearRow(y); EraseLine(1); }
        else                { for (int y = 0; y < Rows; y++) ClearRow(y); }
    }

    private void EraseLine(int mode)
    {
        int from = mode == 1 ? 0 : (mode == 2 ? 0 : _cx);
        int to   = mode == 0 ? Cols - 1 : (mode == 2 ? Cols - 1 : _cx);
        for (int x = from; x <= to && x < Cols; x++) _grid[_cy][x] = Blank();
    }

    private void EraseChars(int n)
    {
        for (int x = _cx; x < _cx + n && x < Cols; x++) _grid[_cy][x] = Blank();
    }

    private void DeleteChars(int n)
    {
        for (int x = _cx; x < Cols; x++)
            _grid[_cy][x] = (x + n < Cols) ? _grid[_cy][x + n] : Blank();
    }

    private void InsertLines(int n)
    {
        for (int i = 0; i < n; i++)
        {
            for (int y = _scrollBottom; y > _cy; y--) _grid[y] = _grid[y - 1];
            _grid[_cy] = new Cell[Cols];
            for (int x = 0; x < Cols; x++) _grid[_cy][x] = Blank();
        }
    }

    private void ClearRow(int y) { for (int x = 0; x < Cols; x++) _grid[y][x] = Blank(); }

    private void Sgr()
    {
        if (_params.Count == 0) { ResetPen(); return; }
        for (int i = 0; i < _params.Count; i++)
        {
            int p = _params[i];
            switch (p)
            {
                case 0: ResetPen(); break;
                case 1: _bold = true; break;
                case 22: _bold = false; break;
                case 7: _inverse = true; break;
                case 27: _inverse = false; break;
                case 39: _fg = 255; break;
                case 49: _bg = 255; break;
                case 38: i = SkipExtended(i); break; // 38;5;n or 38;2;r;g;b → default
                case 48: i = SkipExtended(i); break;
                default:
                    if (p >= 30 && p <= 37) _fg = (byte)(p - 30);
                    else if (p >= 90 && p <= 97) _fg = (byte)(p - 90 + 8);
                    else if (p >= 40 && p <= 47) _bg = (byte)(p - 40);
                    else if (p >= 100 && p <= 107) _bg = (byte)(p - 100 + 8);
                    break;
            }
        }
    }

    // Handle 38/48 extended colour: map 5;n (n<16) to the palette, otherwise default.
    private int SkipExtended(int i)
    {
        if (i + 1 < _params.Count && _params[i + 1] == 5)
        {
            int n = i + 2 < _params.Count ? _params[i + 2] : 255;
            byte idx = (byte)(n < 16 ? n : 255);
            if (_params[i] == 38) _fg = idx; else _bg = idx;
            return i + 2;
        }
        if (i + 1 < _params.Count && _params[i + 1] == 2)
            return i + 4; // truecolour → leave default
        return i;
    }

    private void ResetPen() { _fg = 255; _bg = 255; _bold = false; _inverse = false; }

    private void Osc(char ch)
    {
        // OSC strings (window titles etc.) end at BEL or ST (ESC \). Ignore contents.
        if (ch == '\a') _state = State.Normal;
        else if (ch == '\\') _state = State.Normal;
    }

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
}
