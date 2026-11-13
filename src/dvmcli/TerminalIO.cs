// SPDX-License-Identifier: AGPL-3.0-only
/**
* dvmcli - headless Linux proof of concept for the DVM Desktop Dispatch Console.
*
* Raw terminal I/O for the TUI: raw mode (via termios), the alternate screen, xterm SGR
* mouse reporting, window size (via ioctl), and a parser for keys, clicks and the wheel.
* Deliberately avoids System.Console input APIs, which would fight raw mode.
*/

using System.Runtime.InteropServices;
using System.Text;

namespace dvmcli
{
    /// <summary>Kinds of input event.</summary>
    public enum InputKind { Key, Click, WheelUp, WheelDown }

    /// <summary>Named keys.</summary>
    public enum InputKey { None, Up, Down, Left, Right, Enter, Space, Escape, PageUp, PageDown, Home, End, Tab, Char, CtrlC, Backspace }

    /// <summary>
    /// One input event. Mouse coordinates are zero-based (column, row).
    /// </summary>
    public readonly record struct InputEvent(InputKind Kind, InputKey Key = InputKey.None, char Char = '\0', int X = 0, int Y = 0);

    /// <summary>
    /// Parses raw terminal input bytes into events.
    /// </summary>
    public sealed class InputParser
    {
        private readonly List<byte> pending = new List<byte>();

        /// <summary>
        /// Feeds bytes read from the terminal; returns the complete events. A lone ESC at the end of a
        /// chunk is treated as the Escape key (terminals send escape sequences in a single write).
        /// </summary>
        public List<InputEvent> Feed(ReadOnlySpan<byte> data)
        {
            foreach (byte b in data)
                pending.Add(b);

            List<InputEvent> events = new List<InputEvent>();
            int i = 0;
            while (i < pending.Count)
            {
                byte b = pending[i];
                if (b == 0x1B)
                {
                    if (i + 1 >= pending.Count)
                    {
                        events.Add(new InputEvent(InputKind.Key, InputKey.Escape));
                        i++;
                        continue;
                    }

                    int used = TryParseEscape(i, events);
                    if (used == 0)
                        break;          // incomplete sequence: wait for more bytes
                    i += used;
                    continue;
                }

                i++;
                switch (b)
                {
                    case 0x03: events.Add(new InputEvent(InputKind.Key, InputKey.CtrlC)); break;
                    case 0x0D:
                    case 0x0A: events.Add(new InputEvent(InputKind.Key, InputKey.Enter)); break;
                    case 0x09: events.Add(new InputEvent(InputKind.Key, InputKey.Tab)); break;
                    case 0x20: events.Add(new InputEvent(InputKind.Key, InputKey.Space)); break;
                    case 0x7F:
                    case 0x08: events.Add(new InputEvent(InputKind.Key, InputKey.Backspace)); break;
                    default:
                        if (b >= 0x21 && b < 0x7F)
                            events.Add(new InputEvent(InputKind.Key, InputKey.Char, (char)b));
                        break;  // other bytes (UTF-8 text, controls) are ignored
                }
            }

            pending.RemoveRange(0, i);
            if (pending.Count > 64)
                pending.Clear();    // garbage; don't grow forever
            return events;
        }

        /// <summary>
        /// Parses an escape sequence starting at <paramref name="start"/>; returns bytes used, or 0 if incomplete.
        /// </summary>
        private int TryParseEscape(int start, List<InputEvent> events)
        {
            byte kind = pending[start + 1];
            if (kind != (byte)'[' && kind != (byte)'O')
            {
                // ESC followed by something else: Escape, then let the next byte parse normally
                events.Add(new InputEvent(InputKind.Key, InputKey.Escape));
                return 1;
            }

            // find the final byte (0x40-0x7E) of the CSI / SS3 sequence
            int end = -1;
            for (int j = start + 2; j < pending.Count; j++)
            {
                byte c = pending[j];
                if (c >= 0x40 && c <= 0x7E)
                {
                    end = j;
                    break;
                }
            }
            if (end < 0)
                return pending.Count - start > 32 ? pending.Count - start : 0;

            string body = Encoding.ASCII.GetString(pending.GetRange(start + 2, end - start - 2).ToArray());
            char final = (char)pending[end];
            int used = end - start + 1;

            // SGR mouse: ESC [ < b ; x ; y (M = press, m = release)
            if (kind == (byte)'[' && body.StartsWith('<') && (final == 'M' || final == 'm'))
            {
                string[] parts = body.Substring(1).Split(';');
                if (parts.Length == 3 && int.TryParse(parts[0], out int btn) && int.TryParse(parts[1], out int x) && int.TryParse(parts[2], out int y) && final == 'M')
                {
                    if ((btn & 64) != 0)
                        events.Add(new InputEvent((btn & 1) == 0 ? InputKind.WheelUp : InputKind.WheelDown, X: x - 1, Y: y - 1));
                    else if ((btn & 3) == 0 && (btn & 32) == 0)
                        events.Add(new InputEvent(InputKind.Click, X: x - 1, Y: y - 1));    // left button press (not drag)
                }
                return used;
            }

            InputKey key = final switch
            {
                'A' => InputKey.Up,
                'B' => InputKey.Down,
                'C' => InputKey.Right,
                'D' => InputKey.Left,
                'H' => InputKey.Home,
                'F' => InputKey.End,
                '~' => body switch
                {
                    "1" or "7" => InputKey.Home,
                    "4" or "8" => InputKey.End,
                    "5" => InputKey.PageUp,
                    "6" => InputKey.PageDown,
                    _ => InputKey.None
                },
                _ => InputKey.None
            };
            if (key != InputKey.None)
                events.Add(new InputEvent(InputKind.Key, key));
            return used;
        }
    } // public sealed class InputParser

    /// <summary>
    /// Owns the terminal while the TUI runs.
    /// </summary>
    public sealed class Terminal : IDisposable
    {
        private const string AltScreenOn = "\x1b[?1049h", AltScreenOff = "\x1b[?1049l";
        private const string CursorHide = "\x1b[?25l", CursorShow = "\x1b[?25h";
        private const string MouseOn = "\x1b[?1000h\x1b[?1006h", MouseOff = "\x1b[?1006l\x1b[?1000l";

        private readonly byte[] savedTermios = new byte[TermiosSize];
        private bool haveTermios;
        private readonly Thread reader;
        private readonly System.Collections.Concurrent.ConcurrentQueue<InputEvent> events = new();
        private volatile bool closed;

        /// <summary>
        /// Switches the terminal to raw mode, the alternate screen and mouse reporting.
        /// </summary>
        public Terminal()
        {
            if (!IsTerminal() || TcGetAttr(0, savedTermios) != 0)
                throw new InvalidOperationException("the TUI needs an interactive terminal");
            haveTermios = true;

            byte[] raw = (byte[])savedTermios.Clone();
            CfMakeRaw(raw);
            if (TcSetAttr(0, TCSANOW, raw) != 0)
                throw new InvalidOperationException("the terminal could not be put in raw mode");

            Write(AltScreenOn + CursorHide + MouseOn + "\x1b[2J");
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Restore();

            reader = new Thread(ReadLoop) { IsBackground = true, Name = "terminal input" };
            reader.Start();
        }

        /// <summary>Takes the next input event, if any.</summary>
        public bool TryRead(out InputEvent e) => events.TryDequeue(out e);

        /// <summary>Current size in columns and rows.</summary>
        public (int Width, int Height) Size
        {
            get
            {
                WinSize ws = default;
                if (Ioctl(1, TIOCGWINSZ, ref ws) == 0 && ws.Cols > 0 && ws.Rows > 0)
                    return (ws.Cols, ws.Rows);
                return (100, 30);
            }
        }

        /// <summary>Writes a frame (UTF-8) to the terminal.</summary>
        public void Write(string s)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(s);
            int off = 0;
            while (off < bytes.Length)
            {
                nint n = SysWrite(1, bytes, off, bytes.Length - off);
                if (n < 0)
                {
                    if (Marshal.GetLastPInvokeError() == EINTR)
                        continue;
                    return;
                }
                off += (int)n;
            }
        }

        private void ReadLoop()
        {
            InputParser parser = new InputParser();
            byte[] buf = new byte[256];
            while (!closed)
            {
                // read fd 0 directly: .NET's console stream would run terminal input through its own line editor
                nint n = SysRead(0, buf, buf.Length);
                if (n < 0 && Marshal.GetLastPInvokeError() == EINTR)
                    continue;   // SIGWINCH, SIGCHLD...
                if (n <= 0)
                    return;
                foreach (InputEvent e in parser.Feed(buf.AsSpan(0, (int)n)))
                    events.Enqueue(e);
            }
        }

        private int restored;

        private void Restore()
        {
            if (Interlocked.Exchange(ref restored, 1) == 1)
                return;
            try { Write(MouseOff + CursorShow + "\x1b[0m" + AltScreenOff); } catch { /* ignore */ }
            if (haveTermios)
                TcSetAttr(0, TCSANOW, savedTermios);
        }

        /// <summary>
        /// Puts the terminal back the way it was.
        /// </summary>
        public void Dispose()
        {
            closed = true;
            Restore();
        }

        /// <summary>True when stdin and stdout are both terminals.</summary>
        public static bool IsTerminal() => IsATty(0) == 1 && IsATty(1) == 1;

        // struct termios is treated as opaque (60 bytes on glibc; generous buffer) and only touched by libc
        private const int TermiosSize = 256;
        private const int TCSANOW = 0;

        private const int EINTR = 4;

        [DllImport("libc", EntryPoint = "read", SetLastError = true)]
        private static extern nint SysRead(int fd, byte[] buf, nint count);

        [DllImport("libc", EntryPoint = "write", SetLastError = true)]
        private static extern unsafe nint SysWriteRaw(int fd, byte* buf, nint count);

        private static unsafe nint SysWrite(int fd, byte[] buf, int offset, int count)
        {
            fixed (byte* p = buf)
                return SysWriteRaw(fd, p + offset, count);
        }

        [DllImport("libc", EntryPoint = "isatty")]
        private static extern int IsATty(int fd);

        [DllImport("libc", EntryPoint = "tcgetattr", SetLastError = true)]
        private static extern int TcGetAttr(int fd, byte[] termios);

        [DllImport("libc", EntryPoint = "tcsetattr", SetLastError = true)]
        private static extern int TcSetAttr(int fd, int optionalActions, byte[] termios);

        [DllImport("libc", EntryPoint = "cfmakeraw")]
        private static extern void CfMakeRaw(byte[] termios);

        [StructLayout(LayoutKind.Sequential)]
        private struct WinSize
        {
            public ushort Rows, Cols, XPixel, YPixel;
        }

        private const ulong TIOCGWINSZ = 0x5413;   // Linux, all architectures

        [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
        private static extern int Ioctl(int fd, ulong request, ref WinSize ws);
    } // public sealed class Terminal
}
