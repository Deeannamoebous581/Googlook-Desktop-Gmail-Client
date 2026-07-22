using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Googlook.Services;

namespace Googlook.Controls;

/// <summary>
/// Draws a <see cref="VtScreen"/> snapshot cell-by-cell (background fills + glyph runs
/// + block cursor). A custom-drawn surface — rather than styled text inlines — is what
/// lets it show ANSI background colours and inverse video, not just foreground.
/// </summary>
public sealed class TerminalSurface : Control
{
    private static readonly IBrush PanelBg = new SolidColorBrush(Color.Parse("#0B0B0B"));
    private static readonly IBrush CursorBrush = new SolidColorBrush(Color.Parse("#8AB4F8"));
    private static readonly Dictionary<string, IBrush> BrushCache = new();

    private readonly Typeface _regular = new(new FontFamily("Cascadia Mono,Consolas,monospace"));
    private readonly Typeface _bold = new(new FontFamily("Cascadia Mono,Consolas,monospace"),
        FontStyle.Normal, FontWeight.Bold);
    private const double FontSize = 13;

    private VtScreen.Cell[][]? _cells;
    private int _cx, _cy;
    private bool _cursor;

    public double CellWidth { get; }
    public double CellHeight { get; }

    public TerminalSurface()
    {
        var m = Measure("M");
        CellWidth = m.Width > 0 ? m.Width : 8;
        CellHeight = m.Height > 0 ? m.Height : 16;
    }

    public void SetSnapshot(VtScreen.Cell[][] cells, int cx, int cy, bool cursorVisible)
    {
        _cells = cells; _cx = cx; _cy = cy; _cursor = cursorVisible;
        InvalidateVisual();
    }

    private FormattedText Measure(string s) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, _regular, FontSize, Brushes.White);

    private static IBrush BrushOf(string hex)
    {
        if (!BrushCache.TryGetValue(hex, out var b))
        {
            b = new SolidColorBrush(Color.Parse(hex));
            BrushCache[hex] = b;
        }
        return b;
    }

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(PanelBg, new Rect(Bounds.Size));
        var cells = _cells;
        if (cells is null) return;

        double cw = CellWidth, ch = CellHeight;

        for (int y = 0; y < cells.Length; y++)
        {
            var row = cells[y];
            int x = 0;
            while (x < row.Length)
            {
                var (fg, bg, bold) = Effective(row[x]);
                int start = x;
                var sb = new StringBuilder();
                while (x < row.Length)
                {
                    var (fg2, bg2, bold2) = Effective(row[x]);
                    if (fg2 != fg || bg2 != bg || bold2 != bold) break;
                    sb.Append(row[x].Ch == '\0' ? ' ' : row[x].Ch);
                    x++;
                }

                double px = start * cw, py = y * ch;
                if (bg is not null)
                    ctx.FillRectangle(BrushOf(bg), new Rect(px, py, (x - start) * cw, ch));

                var text = sb.ToString();
                if (text.Trim().Length > 0)
                {
                    var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                        bold ? _bold : _regular, FontSize, BrushOf(fg));
                    ctx.DrawText(ft, new Point(px, py));
                }
            }
        }

        // Block cursor
        if (_cursor && _cy < cells.Length && _cx < cells[_cy].Length)
        {
            double px = _cx * cw, py = _cy * ch;
            ctx.FillRectangle(CursorBrush, new Rect(px, py, cw, ch));
            var under = cells[_cy][_cx].Ch;
            if (under != ' ' && under != '\0')
            {
                var ft = new FormattedText(under.ToString(), CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, _regular, FontSize, PanelBg);
                ctx.DrawText(ft, new Point(px, py));
            }
        }
    }

    // Resolve a cell's effective (fg, bg, bold), applying inverse video.
    private static (string fg, string? bg, bool bold) Effective(VtScreen.Cell c)
    {
        string fg = c.Fg == 255 ? VtScreen.DefaultFg : VtScreen.Palette[c.Fg];
        string? bg = c.Bg == 255 ? null : VtScreen.Palette[c.Bg];
        if (c.Inverse)
        {
            var newBg = fg;                    // old fg becomes the fill
            var newFg = bg ?? "#0B0B0B";       // old bg (or panel) becomes the text
            return (newFg, newBg, c.Bold);
        }
        return (fg, bg, c.Bold);
    }
}
