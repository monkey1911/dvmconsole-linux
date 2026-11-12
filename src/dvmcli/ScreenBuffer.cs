// SPDX-License-Identifier: AGPL-3.0-only
/**
* dvmcli - headless Linux proof of concept for the DVM Desktop Dispatch Console.
*
* A cell-based screen buffer for the TUI. Everything drawn can register a clickable
* region, so a mouse click maps straight to the button or card under the pointer.
* Frames are sent as a diff against the previous frame, inside synchronized output.
*/

using System.Text;

namespace dvmcli
{
    /// <summary>The 16 ANSI colors, plus the terminal default.</summary>
    public enum Ansi : sbyte
    {
        Default = -1,
        Black = 0, Red, Green, Yellow, Blue, Magenta, Cyan, White,
        Grey, BrightRed, BrightGreen, BrightYellow, BrightBlue, BrightMagenta, BrightCyan, BrightWhite
    }

    /// <summary>A cell style.</summary>
    public readonly record struct Style(Ansi Fg = Ansi.Default, Ansi Bg = Ansi.Default, bool Bold = false)
    {
        public static readonly Style Plain = new Style();
        public static Style Of(Ansi fg, Ansi bg = Ansi.Default, bool bold = false) => new Style(fg, bg, bold);
    }

    /// <summary>A rectangle in cells.</summary>
    public readonly record struct Rect(int X, int Y, int W, int H)
    {
        public bool Contains(int x, int y) => x >= X && x < X + W && y >= Y && y < Y + H;
    }

    /// <summary>
    /// Screen buffer with clickable regions.
    /// </summary>
    public sealed class ScreenBuffer
    {
        private struct Cell
        {
            public string Text;     // one grapheme; null = continuation of a wide cell
            public Style Style;
        }

        private Cell[] cells;
        private Cell[] previous;
        private readonly List<(Rect Area, Action Action)> regions = new List<(Rect, Action)>();

        /// <summary>Width in columns.</summary>
        public int Width { get; private set; }

        /// <summary>Height in rows.</summary>
        public int Height { get; private set; }

        /// <summary>
        /// Clears the buffer (and the clickable regions) for a new frame of the given size.
        /// </summary>
        public void Begin(int width, int height)
        {
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            if (width != Width || height != Height)
            {
                Width = width;
                Height = height;
                cells = new Cell[width * height];
                previous = null;    // size changed: next frame is a full redraw
            }

            for (int i = 0; i < cells.Length; i++)
                cells[i] = new Cell { Text = " ", Style = Style.Plain };
            regions.Clear();
        }

        /// <summary>
        /// Display width of a code point: 2 for emoji and East Asian wide characters, otherwise 1.
        /// </summary>
        public static int RuneWidth(Rune r)
        {
            int v = r.Value;
            if (v < 0x1100)
                return 1;
            if ((v >= 0x1100 && v <= 0x115F) || (v >= 0x2E80 && v <= 0xA4CF) || (v >= 0xAC00 && v <= 0xD7A3) ||
                (v >= 0xF900 && v <= 0xFAFF) || (v >= 0xFE30 && v <= 0xFE4F) || (v >= 0xFF00 && v <= 0xFF60) ||
                (v >= 0xFFE0 && v <= 0xFFE6) || (v >= 0x1F300 && v <= 0x1F64F) || (v >= 0x1F900 && v <= 0x1F9FF) ||
                (v >= 0x1F680 && v <= 0x1F6FF) || (v >= 0x1FA70 && v <= 0x1FAFF) || (v >= 0x20000 && v <= 0x3FFFD))
                return 2;
            return 1;
        }

        /// <summary>Display width of a string.</summary>
        public static int TextWidth(string s)
        {
            int w = 0;
            foreach (Rune r in (s ?? string.Empty).EnumerateRunes())
                w += RuneWidth(r);
            return w;
        }

        /// <summary>
        /// Draws text at (x, y), clipped to <paramref name="maxWidth"/> cells (and the screen); text that doesn't fit
        /// ends with '…'. Returns the number of cells used.
        /// </summary>
        public int Text(int x, int y, string s, Style style, int maxWidth = int.MaxValue)
        {
            if (y < 0 || y >= Height || string.IsNullOrEmpty(s))
                return 0;
            int limit = Math.Min(maxWidth, Width - x);
            if (limit <= 0)
                return 0;

            bool truncate = TextWidth(s) > limit;
            int budget = truncate ? limit - 1 : limit;
            int col = x;
            foreach (Rune r in s.EnumerateRunes())
            {
                if (r.Value < 0x20)
                    continue;
                int w = RuneWidth(r);
                if (col + w > x + budget)
                    break;
                Set(col, y, r.ToString(), style);
                if (w == 2)
                    Set(col + 1, y, null, style);
                col += w;
            }
            if (truncate && col < x + limit)
                Set(col++, y, "…", style);
            return col - x;
        }

        /// <summary>Text right-aligned so that it ends just before column <paramref name="right"/>.</summary>
        public int TextRight(int right, int y, string s, Style style) => Text(right - TextWidth(s), y, s, style);

        /// <summary>Fills a rectangle with a background style.</summary>
        public void Fill(Rect r, Style style)
        {
            for (int y = r.Y; y < r.Y + r.H; y++)
                for (int x = r.X; x < r.X + r.W; x++)
                    Set(x, y, " ", style);
        }

        /// <summary>Draws a rounded box border.</summary>
        public void Box(Rect r, Style style)
        {
            if (r.W < 2 || r.H < 2)
                return;
            int right = r.X + r.W - 1, bottom = r.Y + r.H - 1;
            for (int x = r.X + 1; x < right; x++)
            {
                Set(x, r.Y, "─", style);
                Set(x, bottom, "─", style);
            }
            for (int y = r.Y + 1; y < bottom; y++)
            {
                Set(r.X, y, "│", style);
                Set(right, y, "│", style);
            }
            Set(r.X, r.Y, "╭", style);
            Set(right, r.Y, "╮", style);
            Set(r.X, bottom, "╰", style);
            Set(right, bottom, "╯", style);
        }

        /// <summary>
        /// Registers a clickable region; when regions overlap, the one added last wins.
        /// </summary>
        public void OnClick(Rect area, Action action) => regions.Add((area, action));

        /// <summary>Finds the action for a click at (x, y), if any.</summary>
        public Action HitTest(int x, int y)
        {
            for (int i = regions.Count - 1; i >= 0; i--)
                if (regions[i].Area.Contains(x, y))
                    return regions[i].Action;
            return null;
        }

        /// <summary>The text of one row (for tests).</summary>
        public string RowText(int y)
        {
            StringBuilder sb = new StringBuilder();
            for (int x = 0; x < Width; x++)
            {
                string t = cells[y * Width + x].Text;
                if (t != null)
                    sb.Append(t);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Produces the escape sequence that updates the terminal from the previous frame to this one.
        /// </summary>
        public string Flush()
        {
            StringBuilder sb = new StringBuilder("\x1b[?2026h");
            bool full = previous == null;
            if (full)
                sb.Append("\x1b[0m\x1b[2J");

            Style? current = null;
            for (int y = 0; y < Height; y++)
            {
                int expectX = -1;
                for (int x = 0; x < Width; x++)
                {
                    int i = y * Width + x;
                    Cell c = cells[i];
                    if (c.Text == null)
                        continue;   // drawn with the wide cell before it
                    if (!full && previous[i].Text == c.Text && previous[i].Style == c.Style &&
                        (x + 1 >= Width || previous[i + 1].Text == cells[i + 1].Text))
                        continue;

                    if (expectX != x)
                        sb.Append("\x1b[").Append(y + 1).Append(';').Append(x + 1).Append('H');
                    if (current != c.Style)
                    {
                        sb.Append(Sgr(c.Style));
                        current = c.Style;
                    }
                    sb.Append(c.Text);
                    expectX = x + (x + 1 < Width && cells[i + 1].Text == null ? 2 : 1);
                }
            }

            sb.Append("\x1b[0m\x1b[?2026l");
            previous = (Cell[])cells.Clone();
            return sb.ToString();
        }

        /// <summary>Forces the next Flush to redraw everything.</summary>
        public void Invalidate() => previous = null;

        private void Set(int x, int y, string text, Style style)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height)
                return;
            int i = y * Width + x;

            // overwriting half of a wide character: blank the other half
            if (cells[i].Text == null && x > 0 && text != null)
                cells[i - 1].Text = " ";
            if (text != null && x + 1 < Width && cells[i + 1].Text == null && RuneWidth(Rune.GetRuneAt(cells[i].Text ?? " ", 0)) == 2)
                cells[i + 1].Text = " ";

            cells[i] = new Cell { Text = text, Style = style };
        }

        private static string Sgr(Style s)
        {
            StringBuilder sb = new StringBuilder("\x1b[0");
            if (s.Bold)
                sb.Append(";1");
            if (s.Fg != Ansi.Default)
                sb.Append(';').Append((int)s.Fg < 8 ? 30 + (int)s.Fg : 90 + (int)s.Fg - 8);
            if (s.Bg != Ansi.Default)
                sb.Append(';').Append((int)s.Bg < 8 ? 40 + (int)s.Bg : 100 + (int)s.Bg - 8);
            return sb.Append('m').ToString();
        }
    } // public sealed class ScreenBuffer
}
