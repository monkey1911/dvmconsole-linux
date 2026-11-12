// SPDX-License-Identifier: AGPL-3.0-only
/**
* dvmcli - headless Linux proof of concept for the DVM Desktop Dispatch Console.
*
* Terminal UI: one system's channels as a grid of cards (up to four across), live
* receive on selected channels, toggle push-to-talk, and mouse support: PTT, MUTE
* and EXIT buttons in the title bar, click a card to listen, wheel to scroll.
*/

using dvmconsole;
using fnecore;
using fnecore.P25;

namespace dvmcli
{
    /// <summary>
    /// Terminal dispatch console.
    /// </summary>
    public sealed class Tui
    {
        private sealed class Chan
        {
            public Codeplug.Channel Cfg;
            public string Zone;
            public uint Tg;
            public bool IsP25;
            public bool Encrypted;
            public byte TxAlg;
            public byte KeyRequestAlg;
            public ushort KeyId;

            public volatile bool Selected;
            public volatile bool Muted;
            public double Gain = 1.0;
            public P25VoiceReceiver Rx;
            public PipeWireSink Sink;

            // activity seen on channels that aren't selected
            public bool Active;
            public uint ActiveSrc;
            public uint ActiveStream;
            public string LastCall = string.Empty;
        }

        /// <summary>Grid geometry for a terminal size.</summary>
        public readonly record struct GridGeometry(int Columns, int CardWidth, int VisibleRows, int TotalRows, int GridTop, int LogTop, int LogHeight);

        public const int TitleHeight = 3;
        public const int FooterHeight = 2;
        public const int CardHeight = 7;
        public const int MinCardWidth = 24;
        public const int MaxColumns = 4;
        private const int MinLogHeight = 6;
        private const int MaxLogLines = 500;

        private readonly Codeplug.System system;
        private readonly TimeSpan txLimit;
        private readonly string logFile;
        private readonly KeyStore keys = new KeyStore();
        private readonly List<Chan> chans = new List<Chan>();
        private readonly object uiLock = new object();
        private readonly LinkedList<string> log = new LinkedList<string>();
        private readonly ScreenBuffer screen = new ScreenBuffer();

        private CliPeer peer;
        private int cursor;
        private int scrollRow;
        private int logOffset;
        private bool followCursor = true;
        private volatile bool quit;
        private volatile bool globalMute;

        private P25Transmitter tx;
        private Chan txChan;
        private PipeWireCapture capture;
        private volatile string pendingTxStop;

        /// <summary>Self-test hook: don't start audio players when selecting channels.</summary>
        internal bool NoAudio { get; set; }

        /// <summary>Index of the focused card (self-test hook).</summary>
        internal int Cursor => cursor;

        /// <summary>Whether channel <paramref name="i"/> is selected (self-test hook).</summary>
        internal bool IsSelected(int i) => chans[i].Selected;

        /// <summary>Global mute (self-test hook).</summary>
        internal bool GlobalMute => globalMute;

        /// <summary>Set when the user asks to quit (self-test hook).</summary>
        internal bool QuitRequested => quit;

        /// <summary>
        /// Initializes a new instance of the <see cref="Tui"/> class.
        /// </summary>
        public Tui(Codeplug codeplug, Codeplug.System system, TimeSpan txLimit, string logFile)
        {
            this.system = system;
            this.txLimit = txLimit;
            this.logFile = logFile;

            foreach (Codeplug.Zone zone in codeplug.Zones ?? new List<Codeplug.Zone>())
                foreach (Codeplug.Channel c in zone.Channels ?? new List<Codeplug.Channel>())
                {
                    if (!string.IsNullOrWhiteSpace(c.System) && !string.Equals(c.System, system.Name, StringComparison.OrdinalIgnoreCase))
                        continue;

                    bool p25 = c.GetChannelMode() == Codeplug.ChannelMode.P25;
                    bool enc = p25 && !string.IsNullOrWhiteSpace(c.Algo) && !c.Algo.Equals("none", StringComparison.OrdinalIgnoreCase);
                    uint.TryParse(c.Tgid, out uint tg);
                    chans.Add(new Chan
                    {
                        Cfg = c,
                        Zone = zone.Name,
                        Tg = tg,
                        IsP25 = p25 && tg != 0,
                        Encrypted = enc,
                        TxAlg = enc ? c.GetAlgoId() : P25Defines.P25_ALGO_UNENCRYPT,
                        KeyRequestAlg = enc ? c.GetKeyRequestAlgoId() : (byte)0,
                        KeyId = enc ? c.GetKeyId() : (ushort)0
                    });
                }

            if (chans.Count == 0)
                throw new ArgumentException($"no channels for system '{system.Name}' in the codeplug");
        }

        /// <summary>
        /// Runs until the user quits.
        /// </summary>
        public int Run()
        {
            if (!Terminal.IsTerminal())
                throw new InvalidOperationException("the TUI needs an interactive terminal (use 'listen' for scripts)");

            peer = new CliPeer(system, keys, verbose: false);
            peer.Status += AddLog;
            peer.P25Frame += OnFrame;

            AddLog($"connecting to {system.Name} ({system.Address}:{system.Port}) as peer {system.PeerId}");
            peer.Start();

            Terminal term = new Terminal();
            try
            {
                while (!quit)
                {
                    while (term.TryRead(out InputEvent e))
                        HandleInput(e);

                    Housekeeping();
                    (int w, int h) = term.Size;
                    Render(w, h);
                    term.Write(screen.Flush());
                    Thread.Sleep(50);
                }
            }
            finally
            {
                term.Dispose();
                StopTx("console closed");
                foreach (Chan c in chans)
                    Deselect(c, quiet: true);
                peer.Stop();
            }

            Console.WriteLine("dvmcli: stopped");
            return 0;
        }

        /*
        ** Network
        */

        private void OnFrame(P25DataReceivedEvent e)
        {
            List<Chan> matching;
            lock (uiLock)
                matching = chans.Where(c => c.IsP25 && c.Tg == e.DstId).ToList();

            bool terminator = e.DUID == P25DUID.TDU || e.DUID == P25DUID.TDULC;
            foreach (Chan c in matching)
            {
                P25VoiceReceiver rx = c.Rx;
                if (c.Selected && rx != null)
                {
                    rx.Handle(e.DstId, e.SrcId, e.DUID, e.StreamId, e.Data);
                    continue;
                }

                // not selected: just show that the channel is busy
                lock (uiLock)
                {
                    if (terminator)
                    {
                        if (c.Active && e.StreamId == c.ActiveStream)
                        {
                            c.Active = false;
                            c.LastCall = $"{c.ActiveSrc} at {DateTime.Now:HH:mm}";
                        }
                    }
                    else if (e.DUID == P25DUID.LDU1 || e.DUID == P25DUID.LDU2 || e.DUID == P25DUID.HDU)
                    {
                        c.Active = true;
                        c.ActiveSrc = e.SrcId;
                        c.ActiveStream = e.StreamId;
                    }
                }
            }
        }

        /*
        ** Channels
        */

        private void Select(Chan c)
        {
            if (c.Selected)
                return;
            if (!c.IsP25)
            {
                AddLog($"{c.Cfg.Name}: {c.Cfg.Mode} channels aren't supported yet");
                return;
            }

            P25VoiceReceiver rx = new P25VoiceReceiver(c.Tg, keys);
            rx.Status += s => { if (!s.StartsWith("CALL ")) AddLog($"{c.Cfg.Name}: {s}"); };
            rx.KeyNeeded += (alg, kid) => peer?.RequestKey(alg, kid);
            rx.CallStarted += src => AddLog($"{c.Cfg.Name}: RX {src}");
            rx.CallEnded += (src, secs, played, muted, crypt) =>
            {
                c.LastCall = $"{src} at {DateTime.Now:HH:mm} ({secs:F0} s)";
                AddLog($"{c.Cfg.Name}: end {src}, {secs:F1} s, {crypt}{(muted > 0 ? $", {muted} frames muted" : string.Empty)}");
            };
            rx.Audio += samples =>
            {
                PipeWireSink sink = c.Sink;
                if (sink == null || c.Muted || globalMute || (txChan == c && tx?.IsTransmitting == true))
                    return;
                Program.ApplyGain(samples, c.Gain);
                sink.Write(samples);
            };

            if (!NoAudio)
            {
                try
                {
                    PipeWireSink sink = new PipeWireSink();
                    sink.Failed += msg => AddLog($"{c.Cfg.Name}: {msg}");
                    c.Sink = sink;
                }
                catch (Exception ex)
                {
                    AddLog($"{c.Cfg.Name}: no audio output ({ex.Message})");
                }
            }

            c.Rx = rx;
            c.Active = false;
            c.Selected = true;
            AddLog($"{c.Cfg.Name}: listening (tg {c.Tg})");

            if (c.Encrypted && KeyStore.IsSupported(c.KeyRequestAlg) && !keys.TryGet(c.KeyRequestAlg, c.KeyId, out _))
                peer?.RequestKey(c.KeyRequestAlg, c.KeyId);
        }

        private void Deselect(Chan c, bool quiet = false)
        {
            if (!c.Selected)
                return;
            if (txChan == c)
                StopTx("channel deselected");

            c.Selected = false;
            P25VoiceReceiver rx = c.Rx;
            PipeWireSink sink = c.Sink;
            c.Rx = null;
            c.Sink = null;
            rx?.Dispose();
            sink?.Dispose();
            if (!quiet)
                AddLog($"{c.Cfg.Name}: stopped listening");
        }

        private void ToggleSelect(Chan c)
        {
            if (c.Selected) Deselect(c); else Select(c);
        }

        private void ToggleMute(Chan c)
        {
            c.Muted = !c.Muted;
            AddLog($"{c.Cfg.Name}: {(c.Muted ? "muted" : "unmuted")}");
        }

        private void ToggleGlobalMute()
        {
            globalMute = !globalMute;
            AddLog(globalMute ? "all receive audio muted" : "receive audio unmuted");
        }

        private static void Louder(Chan c) => c.Gain = Math.Min(8.0, c.Gain * 1.25);

        private static void Quieter(Chan c) => c.Gain = Math.Max(0.25, c.Gain / 1.25);

        private void RequestKey(Chan c)
        {
            if (c.Encrypted && KeyStore.IsSupported(c.KeyRequestAlg))
                peer?.RequestKey(c.KeyRequestAlg, c.KeyId);
            else
                AddLog($"{c.Cfg.Name}: clear channel, no key needed");
        }

        /*
        ** Transmit
        */

        private void ToggleTx()
        {
            if (tx != null && tx.IsTransmitting)
            {
                StopTx(null);
                return;
            }

            Chan c = chans[cursor];
            if (!c.IsP25)
            {
                AddLog($"{c.Cfg.Name}: can't transmit on a {c.Cfg.Mode} channel");
                return;
            }
            if (c.Cfg.RxOnly)
            {
                AddLog($"{c.Cfg.Name}: receive-only channel");
                return;
            }
            if (peer == null || !peer.Connected)
            {
                AddLog("not connected to the FNE yet");
                return;
            }

            Select(c);

            P25Transmitter t = new P25Transmitter(peer, keys, peer.ConsoleRid, c.Tg, c.TxAlg, c.KeyId);
            if (!t.Start(out string error))
            {
                AddLog($"{c.Cfg.Name}: not transmitting: {error}");
                if (c.Encrypted && KeyStore.IsSupported(c.KeyRequestAlg))
                    peer.RequestKey(c.KeyRequestAlg, c.KeyId);
                return;
            }

            try
            {
                PipeWireCapture cap = new PipeWireCapture();
                cap.Block += t.PushAudio;
                cap.Failed += msg => pendingTxStop = $"microphone: {msg}";
                capture = cap;
            }
            catch (Exception ex)
            {
                t.Stop();
                AddLog($"{c.Cfg.Name}: no microphone ({ex.Message})");
                return;
            }

            tx = t;
            txChan = c;
            AddLog($"{c.Cfg.Name}: TX start as {peer.ConsoleRid}{(t.Encrypted ? $" ({KeyStore.AlgorithmName(t.AlgorithmId)} key 0x{t.KeyId:X4})" : " (clear)")}");
        }

        private void StopTx(string reason)
        {
            P25Transmitter t = tx;
            if (t == null)
                return;

            PipeWireCapture cap = capture;
            capture = null;
            cap?.Dispose();

            double secs = (DateTime.Now - t.StartedAt).TotalSeconds;
            bool was = t.IsTransmitting;
            t.Stop();
            if (was)
                AddLog($"{txChan?.Cfg.Name}: TX end, {secs:F1} s, {t.FramesSent} frames{(reason != null ? $" ({reason})" : string.Empty)}");

            tx = null;
            txChan = null;
        }

        private bool Transmitting => tx != null && tx.IsTransmitting;

        private void Housekeeping()
        {
            string stop = pendingTxStop;
            if (stop != null)
            {
                pendingTxStop = null;
                StopTx(stop);
            }

            if (Transmitting && DateTime.Now - tx.StartedAt > txLimit)
                StopTx($"{txLimit.TotalSeconds:F0} s transmit limit reached");
        }

        /*
        ** Input
        */

        /// <summary>
        /// Handles one key or mouse event (internal for the self-test).
        /// </summary>
        internal void HandleInput(InputEvent e)
        {
            GridGeometry g = Layout(screen.Width, screen.Height, chans.Count);
            switch (e.Kind)
            {
                case InputKind.Click:
                    screen.HitTest(e.X, e.Y)?.Invoke();
                    return;

                case InputKind.WheelUp:
                case InputKind.WheelDown:
                {
                    int dir = e.Kind == InputKind.WheelUp ? -1 : 1;
                    if (g.LogHeight > 0 && e.Y >= g.LogTop)
                        logOffset = Math.Max(0, logOffset - dir * 3);
                    else
                    {
                        scrollRow += dir;
                        followCursor = false;
                    }
                    return;
                }
            }

            Chan c = chans[cursor];
            int page = Math.Max(1, g.VisibleRows) * g.Columns;
            switch (e.Key)
            {
                case InputKey.Left: MoveCursor(-1); break;
                case InputKey.Right: MoveCursor(1); break;
                case InputKey.Tab: MoveCursor(1); break;
                case InputKey.Up: MoveCursor(-g.Columns); break;
                case InputKey.Down: MoveCursor(g.Columns); break;
                case InputKey.PageUp: MoveCursor(-page); break;
                case InputKey.PageDown: MoveCursor(page); break;
                case InputKey.Home: MoveCursor(-chans.Count); break;
                case InputKey.End: MoveCursor(chans.Count); break;
                case InputKey.Enter: ToggleSelect(c); break;
                case InputKey.Space: ToggleTx(); break;
                case InputKey.Escape: StopTx(null); break;
                case InputKey.CtrlC: quit = true; break;
                case InputKey.Char:
                    switch (e.Char)
                    {
                        case 'q': case 'Q': quit = true; break;
                        case 'm': ToggleMute(c); break;
                        case 'M': ToggleGlobalMute(); break;
                        case '+': case '=': Louder(c); break;
                        case '-': case '_': Quieter(c); break;
                        case 'k': case 'K': RequestKey(c); break;
                    }
                    break;
            }
        }

        private void MoveCursor(int delta)
        {
            cursor = Math.Clamp(cursor + delta, 0, chans.Count - 1);
            followCursor = true;
        }

        private void Focus(int index)
        {
            cursor = index;
            followCursor = true;
        }

        /*
        ** Layout and rendering
        */

        /// <summary>
        /// Works out the card grid for a terminal size: up to four cards across, at least <see cref="MinCardWidth"/>
        /// wide, and as many card rows as fit while leaving room for the log.
        /// </summary>
        public static GridGeometry Layout(int width, int height, int cardCount)
        {
            int cols = Math.Clamp(width / MinCardWidth, 1, MaxColumns);
            int cardW = width / cols;
            int totalRows = (cardCount + cols - 1) / cols;
            int avail = Math.Max(0, height - TitleHeight - FooterHeight);
            int visible = Math.Clamp((avail - MinLogHeight) / CardHeight, 1, Math.Max(1, totalRows));
            int gridH = Math.Min(visible * CardHeight, avail);
            int logH = avail - gridH;
            if (logH < 3)
                logH = 0;
            return new GridGeometry(cols, cardW, visible, totalRows, TitleHeight, TitleHeight + gridH, logH);
        }

        /// <summary>
        /// Draws one frame into the screen buffer (and registers the clickable regions).
        /// </summary>
        internal ScreenBuffer Render(int width, int height)
        {
            screen.Begin(width, height);
            if (width < 40 || height < TitleHeight + FooterHeight + CardHeight)
            {
                screen.Text(0, 0, "dvmcli: terminal too small", Style.Of(Ansi.Yellow, bold: true));
                screen.Text(0, 1, "make the window bigger, or press q to quit", Style.Plain);
                return screen;
            }

            GridGeometry g = Layout(width, height, chans.Count);
            DrawTitle(width);

            // scrolling: keep the focused card in view after keyboard moves; the wheel scrolls freely
            int maxScroll = Math.Max(0, g.TotalRows - g.VisibleRows);
            if (followCursor)
            {
                int row = cursor / g.Columns;
                if (row < scrollRow) scrollRow = row;
                if (row >= scrollRow + g.VisibleRows) scrollRow = row - g.VisibleRows + 1;
            }
            scrollRow = Math.Clamp(scrollRow, 0, maxScroll);

            for (int vr = 0; vr < g.VisibleRows; vr++)
                for (int col = 0; col < g.Columns; col++)
                {
                    int idx = (scrollRow + vr) * g.Columns + col;
                    if (idx >= chans.Count)
                        break;
                    DrawCard(idx, new Rect(col * g.CardWidth, g.GridTop + vr * CardHeight, g.CardWidth, CardHeight));
                }

            if (maxScroll > 0)
            {
                string more = $" {scrollRow + 1}-{Math.Min(g.TotalRows, scrollRow + g.VisibleRows)} of {g.TotalRows} rows ";
                screen.TextRight(width - 1, g.LogTop - 1, more, Style.Of(Ansi.Grey));
            }

            if (g.LogHeight > 0)
                DrawLog(new Rect(0, g.LogTop, width, g.LogHeight));
            DrawFooter(width, height);
            return screen;
        }

        private void DrawTitle(int width)
        {
            // buttons: three rows tall so they're easy to hit
            const int pttW = 10, muteW = 9, exitW = 8;
            int x = width - (pttW + muteW + exitW + 2);

            bool txOn = Transmitting;
            Button(new Rect(x, 0, pttW, TitleHeight), txOn ? "● TX" : "PTT",
                   txOn ? Style.Of(Ansi.BrightWhite, Ansi.Red, bold: true) : Style.Of(Ansi.BrightWhite, Ansi.Blue, bold: true), ToggleTx);
            x += pttW + 1;
            Button(new Rect(x, 0, muteW, TitleHeight), globalMute ? "MUTED" : "MUTE",
                   globalMute ? Style.Of(Ansi.Black, Ansi.Yellow, bold: true) : Style.Of(Ansi.BrightWhite, Ansi.Grey, bold: true), ToggleGlobalMute);
            x += muteW + 1;
            Button(new Rect(x, 0, exitW, TitleHeight), "EXIT", Style.Of(Ansi.BrightWhite, Ansi.Magenta, bold: true), () => quit = true);

            // system box
            int boxW = width - (pttW + muteW + exitW + 3);
            Style frame = Style.Of(Ansi.Cyan);
            screen.Box(new Rect(0, 0, boxW, TitleHeight), frame);
            screen.Text(2, 0, " dvmcli ", Style.Of(Ansi.BrightCyan, bold: true));

            int tx0 = 2, inner = boxW - 4;
            bool connected = peer?.Connected == true;
            int used = screen.Text(tx0, 1, system.Name, Style.Of(Ansi.BrightWhite, bold: true), inner);
            used += screen.Text(tx0 + used, 1, connected ? "  ● connected" : "  ○ connecting…",
                                connected ? Style.Of(Ansi.BrightGreen) : Style.Of(Ansi.Yellow), inner - used);
            screen.Text(tx0 + used, 1, $"  peer {system.PeerId}  RID {peer?.ConsoleRid ?? 0}  keys {keys.Count}", Style.Of(Ansi.White), inner - used);
        }

        private void Button(Rect r, string label, Style style, Action action)
        {
            screen.Fill(r, style);
            int lw = ScreenBuffer.TextWidth(label);
            screen.Text(r.X + Math.Max(0, (r.W - lw) / 2), r.Y + r.H / 2, label, style, r.W);
            screen.OnClick(r, action);
        }

        private void DrawCard(int idx, Rect r)
        {
            Chan c = chans[idx];
            bool focused = idx == cursor;
            bool txHere = Transmitting && txChan == c;
            P25VoiceReceiver rx = c.Rx;
            bool rxHere = c.Selected && rx != null && rx.InCall;

            Style border = txHere ? Style.Of(Ansi.BrightRed, bold: true)
                : rxHere ? Style.Of(Ansi.BrightGreen, bold: true)
                : focused ? Style.Of(Ansi.BrightWhite, bold: true)
                : c.Selected ? Style.Of(Ansi.Green)
                : Style.Of(Ansi.Grey);
            Style dim = Style.Of(Ansi.Grey);

            // whole card: focus it and start listening
            screen.OnClick(r, () =>
            {
                Focus(idx);
                if (!c.Selected) Select(c);
            });

            Rect box = new Rect(r.X, r.Y, r.W - 1, r.H);    // one column gap between cards
            screen.Box(box, border);
            int ix = box.X + 2, iw = box.W - 4, right = box.X + box.W - 2;

            // title and listen toggle
            string toggle = c.Selected ? "[●]" : "[ ]";
            int toggleX = right - 3;
            screen.Text(box.X + 1, box.Y, focused ? "▶" : "─", border);
            Style nameStyle = !c.IsP25 ? dim : focused || c.Selected ? Style.Of(Ansi.BrightWhite, bold: true) : Style.Of(Ansi.White);
            screen.Text(ix, box.Y, $" {c.Cfg.Name} ", nameStyle, toggleX - ix - 1);
            screen.Text(toggleX, box.Y, toggle, c.Selected ? Style.Of(Ansi.BrightGreen, bold: true) : dim);
            screen.OnClick(new Rect(toggleX - 1, box.Y, 5, 1), () =>
            {
                Focus(idx);
                ToggleSelect(c);
            });

            int y = box.Y + 1;
            if (!c.IsP25)
            {
                screen.Text(ix, y, c.Tg != 0 ? $"TG {c.Tg}" : $"TG {c.Cfg.Tgid}", dim, iw);
                screen.Text(ix, y + 1, $"{c.Cfg.Mode} · not supported yet", dim, iw);
                return;
            }

            // line 1: talkgroup and crypto
            screen.Text(ix, y, $"TG {c.Tg}", Style.Of(Ansi.BrightWhite), iw);
            string crypt = c.Encrypted ? $"{KeyStore.AlgorithmName(c.TxAlg)} 🔒" : "clear";
            screen.TextRight(right, y, crypt, c.Encrypted ? Style.Of(Ansi.BrightYellow) : dim);

            // line 2: key
            if (c.Encrypted)
            {
                bool have = keys.TryGet(c.KeyRequestAlg, c.KeyId, out _);
                screen.Text(ix, y + 1, have ? $"key ✓ {c.KeyId:X4}" : $"key – {c.KeyId:X4}  (k: request)",
                            have ? Style.Of(Ansi.Green) : Style.Of(Ansi.Yellow), iw);
            }
            else
                screen.Text(ix, y + 1, c.Cfg.RxOnly ? "receive only" : "no encryption", dim, iw);

            // line 3: status
            DrawStatus(c, rx, txHere, rxHere, ix, y + 2, iw);

            // line 4: last call
            if (!string.IsNullOrEmpty(c.LastCall))
                screen.Text(ix, y + 3, $"last {c.LastCall}", dim, iw);

            // line 5: volume and mute, clickable
            int vx = ix;
            vx += screen.Text(vx, y + 4, $"vol {c.Gain:0.##}x ", c.Selected ? Style.Of(Ansi.White) : dim, iw);
            vx = SmallButton(vx, y + 4, "[-]", dim, () => { Focus(idx); Quieter(c); }, ix + iw);
            vx = SmallButton(vx, y + 4, "[+]", dim, () => { Focus(idx); Louder(c); }, ix + iw);
            SmallButton(vx + 1, y + 4, c.Muted ? "[MUTED]" : "[mute]",
                        c.Muted ? Style.Of(Ansi.Black, Ansi.Yellow, bold: true) : dim, () => { Focus(idx); ToggleMute(c); }, ix + iw);
        }

        private int SmallButton(int x, int y, string label, Style style, Action action, int limit)
        {
            int w = ScreenBuffer.TextWidth(label);
            if (x + w > limit)
                return x;
            screen.Text(x, y, label, style);
            screen.OnClick(new Rect(x, y, w, 1), action);
            return x + w;
        }

        private void DrawStatus(Chan c, P25VoiceReceiver rx, bool txHere, bool rxHere, int x, int y, int w)
        {
            if (txHere)
            {
                TimeSpan held = DateTime.Now - tx.StartedAt;
                Style s = Style.Of(Ansi.BrightWhite, Ansi.Red, bold: true);
                screen.Fill(new Rect(x, y, w, 1), s);
                screen.Text(x, y, $" ● TX {peer?.ConsoleRid}  {held:mm\\:ss}", s, w);
                return;
            }

            if (rxHere)
            {
                byte alg = rx.CurrentAlgorithm;
                bool enc = alg != 0 && alg != P25Defines.P25_ALGO_UNENCRYPT;
                bool noKey = enc && rx.CallHasMuted && !keys.TryGet(alg, rx.CurrentKeyId, out _);
                Style s = noKey ? Style.Of(Ansi.Black, Ansi.Yellow, bold: true) : Style.Of(Ansi.Black, Ansi.BrightGreen, bold: true);
                screen.Fill(new Rect(x, y, w, 1), s);
                string text = $" RX {rx.CurrentSource}{(enc ? " 🔒" : string.Empty)}{(noKey ? " no key" : string.Empty)}{(c.Muted || globalMute ? " (muted)" : string.Empty)}";
                screen.Text(x, y, text, s, w);
                return;
            }

            if (!c.Selected && c.Active)
                screen.Text(x, y, $"busy {c.ActiveSrc}", Style.Of(Ansi.Cyan), w);
            else if (c.Selected)
                screen.Text(x, y, c.Muted || globalMute ? "listening (muted)" : "listening", c.Muted || globalMute ? Style.Of(Ansi.Yellow) : Style.Of(Ansi.Green), w);
            else
                screen.Text(x, y, "click to listen", Style.Of(Ansi.Grey), w);
        }

        private void DrawLog(Rect r)
        {
            screen.Box(r, Style.Of(Ansi.Grey));
            screen.Text(r.X + 2, r.Y, " Log ", Style.Of(Ansi.White, bold: true));

            int rows = r.H - 2;
            List<string> lines;
            lock (uiLock)
            {
                logOffset = Math.Clamp(logOffset, 0, Math.Max(0, log.Count - rows));
                lines = log.Reverse().Skip(logOffset).Take(rows).Reverse().ToList();
            }
            if (logOffset > 0)
                screen.TextRight(r.X + r.W - 2, r.Y, $" ↑ {logOffset} newer below ", Style.Of(Ansi.Yellow));

            for (int i = 0; i < lines.Count; i++)
            {
                string l = lines[i];
                Style s = l.Contains("TX ") ? Style.Of(Ansi.BrightRed)
                    : l.Contains(": RX ") ? Style.Of(Ansi.BrightGreen)
                    : l.Contains("error", StringComparison.OrdinalIgnoreCase) || l.Contains("not transmitting") ? Style.Of(Ansi.Yellow)
                    : Style.Plain;
                screen.Text(r.X + 2, r.Y + 1 + i, l, s, r.W - 4);
            }
        }

        private void DrawFooter(int width, int height)
        {
            int y = height - FooterHeight;
            if (Transmitting)
            {
                TimeSpan held = DateTime.Now - tx.StartedAt;
                Style s = Style.Of(Ansi.BrightWhite, Ansi.Red, bold: true);
                screen.Fill(new Rect(0, y, width, 1), s);
                screen.Text(1, y, $"● TRANSMITTING  {txChan?.Cfg.Name}  tg {tx.Talkgroup}  {(tx.Encrypted ? KeyStore.AlgorithmName(tx.AlgorithmId) : "clear")}  " +
                                  $"{held:mm\\:ss} / {txLimit:mm\\:ss}   (Space, Esc or PTT to stop)", s, width - 2);
            }
            else
            {
                Chan c = chans[cursor];
                string target = c.IsP25 ? $"PTT → {c.Cfg.Name} (tg {c.Tg}, {(c.Encrypted ? KeyStore.AlgorithmName(c.TxAlg) : "clear")})" : $"{c.Cfg.Name}: no transmit on {c.Cfg.Mode}";
                screen.Text(1, y, target, Style.Of(Ansi.White), width - 2);
            }

            screen.Text(1, y + 1, "click card: listen  [●]: stop  arrows: move  Enter: listen  Space: PTT  m/M: mute card/all  +/-: vol  k: key  q: quit",
                        Style.Of(Ansi.Grey), width - 2);
        }

        /*
        ** Log
        */

        private void AddLog(string message)
        {
            message = (message ?? string.Empty).Replace("(DVMCLI) ", string.Empty);
            string line = $"{DateTime.Now:HH:mm:ss} {message}";
            lock (uiLock)
            {
                log.AddLast(line);
                while (log.Count > MaxLogLines)
                    log.RemoveFirst();
                if (logOffset > 0)
                    logOffset++;    // scrolled back: keep looking at the same lines
            }

            if (logFile != null)
            {
                try { File.AppendAllText(logFile, line + Environment.NewLine); }
                catch { /* ignore */ }
            }
        }
    } // public sealed class Tui
}
