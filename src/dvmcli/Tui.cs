// SPDX-License-Identifier: AGPL-3.0-only
/**
* dvmcli - headless Linux proof of concept for the DVM Desktop Dispatch Console.
*
* Terminal UI: one system's channels as a grid of cards (up to four across), live
* receive on selected channels, toggle push-to-talk, alert tones 1-5 and the channel
* marker on every listening channel, and mouse support: buttons in the title bar,
* click a card to listen, wheel to scroll.
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
        private const int MaxBottomHeight = 10;   // log + recent calls: 8 lines plus the border
        private const int MaxLogLines = 500;
        private const int MaxRecentCalls = 100;
        private const int MinSplitWidth = 100;    // below this the recent-calls panel is hidden

        private readonly Codeplug.System system;
        private readonly TimeSpan txLimit;
        private readonly string logFile;
        private readonly KeyStore keys = new KeyStore();
        private readonly List<Chan> chans = new List<Chan>();
        private readonly object uiLock = new object();
        private readonly LinkedList<string> log = new LinkedList<string>();
        private readonly LinkedList<(string Time, string Chan, string Who, string Detail, bool Tx)> recent = new();
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

        // tones: alert 1-5 and the periodic channel marker, sent on every listening channel
        private readonly TimeSpan markerInterval;
        private volatile ToneJob toneJob;
        private HashSet<Chan> toneChans = new HashSet<Chan>();
        private bool markerOn;
        private DateTime nextMarker;

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
        public Tui(Codeplug codeplug, Codeplug.System system, TimeSpan txLimit, string logFile, TimeSpan? markerInterval = null)
        {
            this.markerInterval = markerInterval ?? TimeSpan.FromSeconds(10);   // the Windows console's hold timer
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
            peer.P25Control += OnP25Control;

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
                CancelTones(wait: true);
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
                            AddRecent(c.Cfg.Name, c.ActiveSrc.ToString(), "not listening", tx: false);
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
                AddRecent(c.Cfg.Name, src.ToString(), $"{secs:F0} s, {crypt}{(muted > 0 ? ", partly muted" : string.Empty)}", tx: false);
            };
            rx.Audio += samples =>
            {
                PipeWireSink sink = c.Sink;
                if (sink == null || c.Muted || globalMute || (txChan == c && tx?.IsTransmitting == true) || toneChans.Contains(c))
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
            if (toneJob != null)
            {
                CancelTones();
                return;
            }
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
            {
                AddLog($"{txChan?.Cfg.Name}: TX end, {secs:F1} s, {t.FramesSent} frames{(reason != null ? $" ({reason})" : string.Empty)}");
                AddRecent(txChan?.Cfg.Name, $"{t.SourceId}", $"{secs:F0} s, {(t.Encrypted ? KeyStore.AlgorithmName(t.AlgorithmId) : "clear")}", tx: true);
            }

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

            // channel marker: a beep on every idle listening channel, every markerInterval
            if (markerOn && DateTime.Now >= nextMarker)
            {
                nextMarker = DateTime.Now + markerInterval;
                if (toneJob == null && !Transmitting && peer?.Connected == true)
                {
                    List<Chan> idle = TxCapableListening().Where(c => !(c.Rx?.InCall ?? false) && !c.Active).ToList();
                    if (idle.Count > 0)
                        SendTone(TonePlan.Marker(), idle, quiet: true);
                }
            }
        }

        /*
        ** Tones
        */

        private List<Chan> TxCapableListening() =>
            chans.Where(c => c.Selected && c.IsP25 && !c.Cfg.RxOnly).ToList();

        /// <summary>
        /// Sends alert tone 1-5 on every listening channel (internal for the self-test).
        /// </summary>
        internal void SendAlert(int number) => SendManual(TonePlan.Alert(number));

        /// <summary>
        /// Sends a QuickCall II page on every listening channel (internal for the self-test).
        /// </summary>
        internal void SendPage(double aHz, double bHz) => SendManual(TonePlan.Page(aHz, bHz));

        private void SendManual(TonePlan plan)
        {
            if (toneJob != null)
            {
                AddLog($"a tone is already being sent ({toneJob.Plan.Name}); Esc stops it");
                return;
            }
            if (Transmitting)
            {
                AddLog("stop transmitting before sending a tone");
                return;
            }
            if (peer == null || !peer.Connected)
            {
                AddLog("not connected to the FNE yet");
                return;
            }

            List<Chan> targets = TxCapableListening();
            if (targets.Count == 0)
            {
                AddLog("no listening channels to send the tone on (click a card to listen)");
                return;
            }
            SendTone(plan, targets, quiet: false);
        }

        /*
        ** Page dialog: QuickCall II tone page (on listening channels) or digital page (P25 call alert to one radio)
        */

        private sealed class PageDialog
        {
            public bool Digital;
            public string A = string.Empty, B = string.Empty, Rid = string.Empty;
            public int Field;           // tone mode: 0 = A, 1 = B
            public string Error;
        }

        private PageDialog pageDialog;
        private string lastPageA = string.Empty, lastPageB = string.Empty, lastPageRid = string.Empty;

        /// <summary>True while the page dialog is open (self-test hook).</summary>
        internal bool PageDialogOpen => pageDialog != null;

        /// <summary>True when the open page dialog is in digital (call alert) mode (self-test hook).</summary>
        internal bool PageDialogDigital => pageDialog?.Digital == true;

        private void OpenPage(bool digital = false)
        {
            // the last page's values are filled in
            pageDialog = new PageDialog { Digital = digital, A = lastPageA, B = lastPageB, Rid = lastPageRid };
        }

        private void PageInput(InputEvent e)
        {
            PageDialog d = pageDialog;
            ref string field = ref (d.Digital ? ref d.Rid : ref (d.Field == 0 ? ref d.A : ref d.B));
            switch (e.Key)
            {
                case InputKey.Escape:
                    pageDialog = null;
                    break;
                case InputKey.Left:
                case InputKey.Right:
                    d.Digital = !d.Digital;
                    d.Error = null;
                    break;
                case InputKey.Tab:
                case InputKey.Down:
                case InputKey.Up:
                    if (!d.Digital)
                        d.Field = 1 - d.Field;
                    break;
                case InputKey.Backspace:
                    if (field.Length > 0)
                        field = field.Substring(0, field.Length - 1);
                    d.Error = null;
                    break;
                case InputKey.Enter:
                    if (!d.Digital && d.Field == 0 && string.IsNullOrEmpty(d.B))
                        d.Field = 1;
                    else
                        SubmitPage();
                    break;
                case InputKey.CtrlC:
                    quit = true;
                    break;
                case InputKey.Char when char.IsDigit(e.Char) && field.Length < (d.Digital ? 8 : 7):
                case InputKey.Char when e.Char == '.' && !d.Digital && field.Length < 7:
                    field += e.Char;
                    d.Error = null;
                    break;
            }
        }

        private void SubmitPage()
        {
            PageDialog d = pageDialog;
            if (d == null)
                return;

            if (d.Digital)
            {
                if (!uint.TryParse(d.Rid, out uint rid) || rid == 0 || rid > 0xFFFFFF)
                {
                    d.Error = "enter a radio ID (1-16777215)";
                    return;
                }
                string error = "not connected to the FNE yet";
                if (peer == null || !peer.SendCallAlert(rid, out error))
                {
                    d.Error = error;
                    return;
                }
                lastPageRid = d.Rid;
                pageDialog = null;
                AddLog($"digital page (call alert) sent to {rid}");
                AddRecent("call alert", rid.ToString(), "digital page", tx: true);
                return;
            }

            bool okA = double.TryParse(d.A, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double a);
            bool okB = double.TryParse(d.B, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double b);
            if (!okA || a < TonePlan.MinPageHz || a > TonePlan.MaxPageHz)
            {
                d.Field = 0;
                d.Error = $"tone A must be {TonePlan.MinPageHz:F0}-{TonePlan.MaxPageHz:F0} Hz";
                return;
            }
            if (!okB || b < TonePlan.MinPageHz || b > TonePlan.MaxPageHz)
            {
                d.Field = 1;
                d.Error = $"tone B must be {TonePlan.MinPageHz:F0}-{TonePlan.MaxPageHz:F0} Hz";
                return;
            }

            lastPageA = d.A;
            lastPageB = d.B;
            pageDialog = null;
            SendPage(a, b);
        }

        private void OnP25Control(byte lco, uint src, uint dst)
        {
            switch (lco)
            {
                case P25Defines.TSBK_IOSP_CALL_ALRT:
                    AddLog($"call alert (page) from {src}");
                    AddRecent("call alert", src.ToString(), "paged this console", tx: false);
                    break;
                case P25Defines.TSBK_IOSP_ACK_RSP:
                    AddLog($"acknowledged by {src}");
                    break;
            }
        }

        private void DrawPageDialog(int width, int height)
        {
            PageDialog d = pageDialog;
            if (d == null)
                return;

            // swallow clicks outside the dialog
            screen.OnClick(new Rect(0, 0, width, height), () => { });

            int w = Math.Min(54, width - 4), h = 12;
            Rect r = new Rect((width - w) / 2, Math.Max(0, (height - h) / 2), w, h);
            Style bg = Style.Of(Ansi.BrightWhite, Ansi.Black);
            screen.Fill(r, bg);
            screen.Box(r, Style.Of(Ansi.BrightYellow, Ansi.Black, bold: true), heavy: true);
            screen.Text(r.X + 2, r.Y, " Page ", Style.Of(Ansi.BrightYellow, Ansi.Black, bold: true));
            int ix = r.X + 3, iw = w - 6;

            // mode tabs
            Style on = Style.Of(Ansi.Black, Ansi.BrightYellow, bold: true), off = Style.Of(Ansi.White, Ansi.Grey);
            Rect toneTab = new Rect(ix, r.Y + 2, 16, 1), digTab = new Rect(ix + 17, r.Y + 2, 11, 1);
            screen.Fill(toneTab, d.Digital ? off : on);
            screen.Text(toneTab.X + 1, toneTab.Y, "Tone (QC II)", d.Digital ? off : on);
            screen.OnClick(toneTab, () => { pageDialog.Digital = false; pageDialog.Error = null; });
            screen.Fill(digTab, d.Digital ? on : off);
            screen.Text(digTab.X + 1, digTab.Y, "Digital", d.Digital ? on : off);
            screen.OnClick(digTab, () => { pageDialog.Digital = true; pageDialog.Error = null; });

            Style info = Style.Of(Ansi.White, Ansi.Black);
            if (d.Digital)
            {
                screen.Text(ix, r.Y + 4, $"P25 call alert from RID {peer?.ConsoleRid ?? 0} to one radio", info, iw);
                DrawField(ix, r.Y + 6, "Radio ID", d.Rid, true, null, () => { });
            }
            else
            {
                List<string> names = TxCapableListening().Select(c => c.Cfg.Name).ToList();
                screen.Text(ix, r.Y + 4, names.Count > 0 ? $"on: {string.Join(", ", names)}" : "no listening channels: click a card first",
                            names.Count > 0 ? info : Style.Of(Ansi.Yellow, Ansi.Black), iw);
                DrawField(ix, r.Y + 5, "Tone A (Hz)", d.A, d.Field == 0, "1 s", () => pageDialog.Field = 0);
                DrawField(ix, r.Y + 6, "Tone B (Hz)", d.B, d.Field == 1, "3 s", () => pageDialog.Field = 1);
            }

            if (d.Error != null)
                screen.Text(ix, r.Y + 8, d.Error, Style.Of(Ansi.BrightRed, Ansi.Black, bold: true), iw);
            else
                screen.Text(ix, r.Y + 8, d.Digital ? "←/→: mode  Enter: send  Esc: cancel" : "←/→: mode  Tab: A/B  Enter: send  Esc: cancel",
                            Style.Of(Ansi.Grey, Ansi.Black), iw);

            Rect send = new Rect(ix, r.Y + 9, 10, 1), cancel = new Rect(ix + 12, r.Y + 9, 10, 1);
            screen.Fill(send, on);
            screen.Text(send.X + 3, send.Y, "SEND", on);
            screen.OnClick(send, SubmitPage);
            Style cancelStyle = Style.Of(Ansi.Black, Ansi.White, bold: true);
            screen.Fill(cancel, cancelStyle);
            screen.Text(cancel.X + 2, cancel.Y, "CANCEL", cancelStyle);
            screen.OnClick(cancel, () => pageDialog = null);
        }

        private void DrawField(int x, int y, string label, string value, bool active, string suffix, Action focus)
        {
            screen.Text(x, y, label, Style.Of(active ? Ansi.BrightWhite : Ansi.White, Ansi.Black, bold: active), 12);
            Rect box = new Rect(x + 13, y, 12, 1);
            Style boxStyle = active ? Style.Of(Ansi.Black, Ansi.BrightWhite, bold: true) : Style.Of(Ansi.White, Ansi.Grey);
            screen.Fill(box, boxStyle);
            screen.Text(box.X + 1, y, value + (active ? "▏" : string.Empty), boxStyle, box.W - 1);
            if (suffix != null)
                screen.Text(box.X + box.W + 2, y, suffix, Style.Of(Ansi.Grey, Ansi.Black));
            screen.OnClick(new Rect(x, y, 30, 1), focus);
        }

        private void SendTone(TonePlan plan, List<Chan> targets, bool quiet)
        {
            List<(string, P25Transmitter)> started = new List<(string, P25Transmitter)>();
            HashSet<Chan> sending = new HashSet<Chan>();
            foreach (Chan c in targets)
            {
                P25Transmitter t = new P25Transmitter(peer, keys, peer.ConsoleRid, c.Tg, c.TxAlg, c.KeyId);
                if (t.Start(out string error))
                {
                    started.Add((c.Cfg.Name, t));
                    sending.Add(c);
                }
                else
                {
                    AddLog($"{c.Cfg.Name}: no {plan.Name}: {error}");
                    if (c.Encrypted && KeyStore.IsSupported(c.KeyRequestAlg))
                        peer.RequestKey(c.KeyRequestAlg, c.KeyId);
                }
            }
            if (started.Count == 0)
                return;

            // monitor locally on one channel's speaker (not every one), unless muted
            Chan monitorChan = sending.FirstOrDefault(c => c.Sink != null && !c.Muted);
            Action<short[]> monitor = monitorChan == null ? null : pcm =>
            {
                if (!globalMute && !monitorChan.Muted)
                    monitorChan.Sink?.Write(pcm);
            };

            ToneJob job = new ToneJob(plan, started, monitor);
            job.Finished += j =>
            {
                if (!quiet || j.Cancelled)
                    AddLog($"{plan.Name} {(j.Cancelled ? "stopped" : "sent")} on {string.Join(", ", j.Channels)}");
                if (!quiet)
                    foreach (string name in j.Channels)
                        AddRecent(name, $"{peer?.ConsoleRid}", plan.Name, tx: true);
                toneChans = new HashSet<Chan>();
                toneJob = null;
            };

            toneChans = sending;
            toneJob = job;
            if (!quiet)
                AddLog($"sending {plan.Name} on {string.Join(", ", started.Select(t => t.Item1))}");
            job.Start();
        }

        private void CancelTones(bool wait = false)
        {
            ToneJob job = toneJob;
            if (job == null)
                return;
            job.Cancel();
            for (int i = 0; wait && job.Running && i < 50; i++)
                Thread.Sleep(20);
        }

        private void ToggleMarker()
        {
            markerOn = !markerOn;
            nextMarker = DateTime.Now;     // first beep straight away
            AddLog(markerOn ? $"channel marker on: a {TonePlan.MarkerHz:F0} Hz beep every {markerInterval.TotalSeconds:F0} s on idle listening channels"
                            : "channel marker off");
        }

        /// <summary>Channel marker on (self-test hook).</summary>
        internal bool MarkerOn => markerOn;

        /*
        ** Input
        */

        /// <summary>
        /// Handles one key or mouse event (internal for the self-test).
        /// </summary>
        internal void HandleInput(InputEvent e)
        {
            if (pageDialog != null && e.Kind == InputKind.Key)
            {
                PageInput(e);
                return;
            }

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
                case InputKey.Escape: StopTx(null); CancelTones(); break;
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
                        case >= '1' and <= '5': SendAlert(e.Char - '0'); break;
                        case 'c': case 'C': ToggleMarker(); break;
                        case 'p': case 'P': OpenPage(); break;
                        case 'd': case 'D': OpenPage(digital: true); break;
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
        /// wide, and as many card rows as fit while leaving room for the log (capped at <see cref="MaxBottomHeight"/> rows).
        /// </summary>
        public static GridGeometry Layout(int width, int height, int cardCount)
        {
            int cols = Math.Clamp(width / MinCardWidth, 1, MaxColumns);
            int cardW = width / cols;
            int totalRows = (cardCount + cols - 1) / cols;
            int avail = Math.Max(0, height - TitleHeight - FooterHeight);
            int visible = Math.Clamp((avail - MinLogHeight) / CardHeight, 1, Math.Max(1, totalRows));
            int gridH = Math.Min(visible * CardHeight, avail);
            int logH = Math.Min(avail - gridH, MaxBottomHeight);
            if (logH < 3)
                logH = 0;
            // the log sits just above the footer; any spare rows stay between it and the cards
            return new GridGeometry(cols, cardW, visible, totalRows, TitleHeight, TitleHeight + avail - logH, logH);
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
            {
                if (width >= MinSplitWidth)
                {
                    int callsW = Math.Min(width / 2, 64);
                    DrawRecent(new Rect(0, g.LogTop, callsW, g.LogHeight));
                    DrawLog(new Rect(callsW, g.LogTop, width - callsW, g.LogHeight));
                }
                else
                    DrawLog(new Rect(0, g.LogTop, width, g.LogHeight));
            }
            DrawFooter(width, height);
            DrawPageDialog(width, height);
            return screen;
        }

        private void DrawTitle(int width)
        {
            // buttons: three rows tall so they're easy to hit
            const int pttW = 10, muteW = 9, exitW = 8, toneW = 5, markW = 8;
            bool tones = width >= 110;
            const int pageW = 8;
            int toneBlock = tones ? 5 * (toneW + 1) + pageW + 1 + markW + 2 : 0;
            int x = width - (pttW + muteW + exitW + 2) - toneBlock;

            if (tones)
            {
                ToneJob job = toneJob;
                for (int n = 1; n <= 5; n++)
                {
                    int number = n;
                    bool playing = job != null && job.Plan.Name == $"alert {n}";
                    Button(new Rect(x, 0, toneW, TitleHeight), $"T{n}",
                           playing ? Style.Of(Ansi.Black, Ansi.BrightRed, bold: true) : Style.Of(Ansi.Black, Ansi.BrightCyan, bold: true),
                           () => { if (playing) CancelTones(); else SendAlert(number); });
                    x += toneW + 1;
                }
                bool paging = job != null && job.Plan.Name.StartsWith("page ", StringComparison.Ordinal);
                Button(new Rect(x, 0, pageW, TitleHeight), "PAGE",
                       paging ? Style.Of(Ansi.Black, Ansi.BrightRed, bold: true)
                       : pageDialog != null ? Style.Of(Ansi.Black, Ansi.BrightYellow, bold: true) : Style.Of(Ansi.Black, Ansi.BrightGreen, bold: true),
                       () => { if (paging) CancelTones(); else OpenPage(); });
                x += pageW + 1;
                bool beeping = job != null && job.Plan.Name == "marker";
                Button(new Rect(x, 0, markW, TitleHeight), markerOn ? "● MARK" : "MARK",
                       beeping ? Style.Of(Ansi.Black, Ansi.BrightRed, bold: true)
                       : markerOn ? Style.Of(Ansi.Black, Ansi.BrightYellow, bold: true) : Style.Of(Ansi.Black, Ansi.White, bold: true), ToggleMarker);
                x += markW + 2;
            }

            bool txOn = Transmitting;
            Button(new Rect(x, 0, pttW, TitleHeight), txOn ? "● TX" : "PTT",
                   txOn ? Style.Of(Ansi.Black, Ansi.BrightRed, bold: true) : Style.Of(Ansi.Black, Ansi.BrightBlue, bold: true), ToggleTx);
            x += pttW + 1;
            Button(new Rect(x, 0, muteW, TitleHeight), globalMute ? "MUTED" : "MUTE",
                   globalMute ? Style.Of(Ansi.Black, Ansi.BrightYellow, bold: true) : Style.Of(Ansi.Black, Ansi.White, bold: true), ToggleGlobalMute);
            x += muteW + 1;
            Button(new Rect(x, 0, exitW, TitleHeight), "EXIT", Style.Of(Ansi.Black, Ansi.BrightMagenta, bold: true), () => quit = true);

            // system box
            int boxW = width - (pttW + muteW + exitW + 3) - toneBlock;
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
            bool txHere = (Transmitting && txChan == c) || toneChans.Contains(c);
            P25VoiceReceiver rx = c.Rx;
            bool rxHere = c.Selected && rx != null && rx.InCall;

            Style border = txHere ? Style.Of(Ansi.BrightRed, bold: true)
                : rxHere ? Style.Of(Ansi.BrightGreen, bold: true)
                : focused ? Style.Of(Ansi.BrightCyan, bold: true)
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
            screen.Box(box, border, heavy: focused);
            int ix = box.X + 2, iw = box.W - 4, right = box.X + box.W - 2;

            // title and listen toggle
            string toggle = c.Selected ? "[●]" : "[ ]";
            int toggleX = right - 3;
            Style nameStyle = !c.IsP25 ? dim : focused ? Style.Of(Ansi.Black, Ansi.BrightCyan, bold: true)
                : c.Selected ? Style.Of(Ansi.BrightWhite, bold: true) : Style.Of(Ansi.White);
            screen.Text(ix, box.Y, focused ? $" ▶ {c.Cfg.Name} " : $" {c.Cfg.Name} ", nameStyle, toggleX - ix - 1);
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
            ToneJob job = toneJob;
            if (txHere && job != null && toneChans.Contains(c))
            {
                Style ts = Style.Of(Ansi.BrightWhite, Ansi.Red, bold: true);
                screen.Fill(new Rect(x, y, w, 1), ts);
                screen.Text(x, y, $" ♪ TX {job.Plan.Name}", ts, w);
                return;
            }
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

        private void DrawRecent(Rect r)
        {
            screen.Box(r, Style.Of(Ansi.Grey));
            screen.Text(r.X + 2, r.Y, " Recent calls ", Style.Of(Ansi.White, bold: true));

            List<(string Time, string Chan, string Who, string Detail, bool Tx)> rows;
            lock (uiLock)
                rows = recent.Take(r.H - 2).ToList();    // newest first
            if (rows.Count == 0)
                screen.Text(r.X + 2, r.Y + 1, "none yet", Style.Of(Ansi.Grey), r.W - 4);

            int x = r.X + 2, w = r.W - 4;
            for (int i = 0; i < rows.Count; i++)
            {
                var call = rows[i];
                int y = r.Y + 1 + i, used = 0;
                used += screen.Text(x, y, $"{call.Time} ", Style.Of(Ansi.Grey), w);
                used += screen.Text(x + used, y, call.Tx ? "TX " : "RX ", call.Tx ? Style.Of(Ansi.BrightRed, bold: true) : Style.Of(Ansi.BrightGreen, bold: true), w - used);
                used += screen.Text(x + used, y, $"{call.Chan,-10} ", Style.Of(Ansi.BrightWhite), w - used);
                used += screen.Text(x + used, y, $"{call.Who,-8} ", Style.Plain, w - used);
                screen.Text(x + used, y, call.Detail, Style.Of(Ansi.Grey), w - used);
            }
        }

        private void AddRecent(string chan, string who, string detail, bool tx)
        {
            lock (uiLock)
            {
                recent.AddFirst(($"{DateTime.Now:HH:mm}", chan ?? "?", who, detail, tx));
                while (recent.Count > MaxRecentCalls)
                    recent.RemoveLast();
            }
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
            ToneJob job = toneJob;
            if (job != null)
            {
                Style s = Style.Of(Ansi.BrightWhite, Ansi.Red, bold: true);
                screen.Fill(new Rect(0, y, width, 1), s);
                screen.Text(1, y, $"♪ SENDING {job.Plan.Name.ToUpperInvariant()} on {string.Join(", ", job.Channels)}   (Esc, Space or the button to stop)", s, width - 2);
            }
            else if (Transmitting)
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

            screen.Text(1, y + 1, "click card: listen  [●]: stop  arrows: move  Space: PTT  1-5: alert  p/d: tone/digital page  c: marker  m/M: mute card/all  +/-: vol  k: key  q: quit",
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
