// SPDX-License-Identifier: AGPL-3.0-only
/**
* dvmcli - headless Linux proof of concept for the DVM Desktop Dispatch Console.
*
* Terminal UI: one system, its P25 channels, live receive on selected channels,
* and Space-bar toggle push-to-talk on the highlighted channel.
*/

using dvmconsole;
using fnecore;
using fnecore.P25;
using Spectre.Console;
using Spectre.Console.Rendering;

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

        private readonly Codeplug codeplug;
        private readonly Codeplug.System system;
        private readonly TimeSpan txLimit;
        private readonly string logFile;
        private readonly KeyStore keys = new KeyStore();
        private readonly List<Chan> chans = new List<Chan>();
        private readonly object uiLock = new object();
        private readonly LinkedList<string> log = new LinkedList<string>();
        private const int MaxLogLines = 500;

        private CliPeer peer;
        private int cursor;
        private volatile bool quit;

        private P25Transmitter tx;
        private Chan txChan;
        private PipeWireCapture capture;
        private volatile string pendingTxStop;

        /// <summary>
        /// Initializes a new instance of the <see cref="Tui"/> class.
        /// </summary>
        public Tui(Codeplug codeplug, Codeplug.System system, TimeSpan txLimit, string logFile)
        {
            this.codeplug = codeplug;
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
            peer = new CliPeer(system, keys, verbose: false);
            peer.Status += AddLog;
            peer.P25Frame += OnFrame;
            keys.KeyAdded += (_, _) => { };   // key column re-reads the store on each render

            Console.CancelKeyPress += (s, e) => { e.Cancel = true; quit = true; };
            AddLog($"connecting to {system.Name} ({system.Address}:{system.Port}) as peer {system.PeerId}");
            peer.Start();

            Layout layout = new Layout("root").SplitRows(
                new Layout("header").Size(3),
                new Layout("main").SplitColumns(
                    new Layout("channels").Ratio(3),
                    new Layout("log").Ratio(2)),
                new Layout("footer").Size(4));

            try
            {
                AnsiConsole.Live(layout)
                    .AutoClear(true)
                    .Overflow(VerticalOverflow.Crop)
                    .Start(ctx =>
                    {
                        while (!quit)
                        {
                            while (Console.KeyAvailable)
                                HandleKey(Console.ReadKey(intercept: true));

                            Housekeeping();
                            Render(layout);
                            ctx.Refresh();
                            Thread.Sleep(80);
                        }
                    });
            }
            finally
            {
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
            rx.KeyNeeded += (alg, kid) => peer.RequestKey(alg, kid);
            rx.CallStarted += src => AddLog($"{c.Cfg.Name}: RX {src}");
            rx.CallEnded += (src, secs, played, muted, crypt) =>
            {
                c.LastCall = $"{src} at {DateTime.Now:HH:mm} ({secs:F0} s)";
                AddLog($"{c.Cfg.Name}: end {src}, {secs:F1} s, {crypt}{(muted > 0 ? $", {muted} frames muted" : string.Empty)}");
            };
            rx.Audio += samples =>
            {
                PipeWireSink sink = c.Sink;
                if (sink == null || c.Muted || (txChan == c && tx?.IsTransmitting == true))
                    return;
                Program.ApplyGain(samples, c.Gain);
                sink.Write(samples);
            };

            try
            {
                c.Sink = new PipeWireSink();
            }
            catch (Exception ex)
            {
                AddLog($"{c.Cfg.Name}: no audio output ({ex.Message})");
            }

            c.Rx = rx;
            c.Active = false;
            c.Selected = true;
            AddLog($"{c.Cfg.Name}: selected (tg {c.Tg})");

            if (c.Encrypted && KeyStore.IsSupported(c.KeyRequestAlg) && !keys.TryGet(c.KeyRequestAlg, c.KeyId, out _))
                peer.RequestKey(c.KeyRequestAlg, c.KeyId);
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
                AddLog($"{c.Cfg.Name}: deselected");
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
            if (!peer.Connected)
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

        private void Housekeeping()
        {
            string stop = pendingTxStop;
            if (stop != null)
            {
                pendingTxStop = null;
                StopTx(stop);
            }

            if (tx != null && tx.IsTransmitting && DateTime.Now - tx.StartedAt > txLimit)
                StopTx($"{txLimit.TotalSeconds:F0} s transmit limit reached");
        }

        /*
        ** Input
        */

        private void HandleKey(ConsoleKeyInfo k)
        {
            Chan c = chans[cursor];
            switch (k.Key)
            {
                case ConsoleKey.UpArrow:
                    cursor = Math.Max(0, cursor - 1);
                    break;
                case ConsoleKey.DownArrow:
                    cursor = Math.Min(chans.Count - 1, cursor + 1);
                    break;
                case ConsoleKey.PageUp:
                    cursor = Math.Max(0, cursor - 10);
                    break;
                case ConsoleKey.PageDown:
                    cursor = Math.Min(chans.Count - 1, cursor + 10);
                    break;
                case ConsoleKey.Home:
                    cursor = 0;
                    break;
                case ConsoleKey.End:
                    cursor = chans.Count - 1;
                    break;
                case ConsoleKey.Enter:
                    if (c.Selected) Deselect(c); else Select(c);
                    break;
                case ConsoleKey.Spacebar:
                    ToggleTx();
                    break;
                case ConsoleKey.Escape:
                    StopTx(null);
                    break;
                default:
                    switch (char.ToLowerInvariant(k.KeyChar))
                    {
                        case 'q':
                            quit = true;
                            break;
                        case 'm':
                            c.Muted = !c.Muted;
                            AddLog($"{c.Cfg.Name}: {(c.Muted ? "muted" : "unmuted")}");
                            break;
                        case '+':
                        case '=':
                            c.Gain = Math.Min(8.0, c.Gain * 1.25);
                            break;
                        case '-':
                            c.Gain = Math.Max(0.25, c.Gain / 1.25);
                            break;
                        case 'k':
                            if (c.Encrypted && KeyStore.IsSupported(c.KeyRequestAlg))
                                peer.RequestKey(c.KeyRequestAlg, c.KeyId);
                            else
                                AddLog($"{c.Cfg.Name}: clear channel, no key needed");
                            break;
                    }
                    break;
            }
        }

        /*
        ** Rendering
        */

        private void Render(Layout layout)
        {
            string conn = peer.Connected ? "[green]● connected[/]" : "[yellow]○ connecting…[/]";
            layout["header"].Update(new Panel(new Markup(
                $"[bold]dvmcli[/]  {Esc(system.Name)} ({Esc(system.Address)}:{system.Port})  peer {system.PeerId}  RID {peer.ConsoleRid}  {conn}  keys held: {keys.Count}"))
            { Expand = true, Border = BoxBorder.Rounded });

            layout["channels"].Update(new Panel(ChannelTable()) { Expand = true, Header = new PanelHeader(" Channels ") });

            int logRows = Math.Max(3, Console.WindowHeight - 3 - 4 - 2);
            List<string> lines;
            lock (uiLock)
                lines = log.Skip(Math.Max(0, log.Count - logRows)).ToList();
            IRenderable logBody = lines.Count == 0 ? new Markup("[grey]no activity yet[/]") : new Rows(lines.Select(l => new Markup(Esc(l))));
            layout["log"].Update(new Panel(logBody) { Expand = true, Header = new PanelHeader(" Log ") });

            string txLine;
            P25Transmitter t = tx;
            if (t != null && t.IsTransmitting)
            {
                TimeSpan held = DateTime.Now - t.StartedAt;
                txLine = $"[bold white on red] ● TRANSMITTING [/] [bold]{Esc(txChan?.Cfg.Name ?? "?")}[/] tg {t.Talkgroup} " +
                         $"{(t.Encrypted ? Esc(KeyStore.AlgorithmName(t.AlgorithmId)) : "clear")}  {held:mm\\:ss} / {txLimit:mm\\:ss}   [grey](Space or Esc to stop)[/]";
            }
            else
                txLine = "[grey]not transmitting[/]";

            layout["footer"].Update(new Panel(new Rows(
                new Markup(txLine),
                new Markup("[grey]↑↓ move  Enter select/deselect  Space PTT toggle  m mute  +/- volume  k request key  q quit[/]")))
            { Expand = true });
        }

        private IRenderable ChannelTable()
        {
            Table table = new Table { Border = TableBorder.Simple, Expand = true };
            table.AddColumn(new TableColumn(string.Empty).Width(1));
            table.AddColumn("Channel");
            table.AddColumn(new TableColumn("TG").RightAligned());
            table.AddColumn("Crypto");
            table.AddColumn("Key");
            table.AddColumn("Status");
            table.AddColumn("Last call");
            table.AddColumn(new TableColumn("Vol").RightAligned());

            // keep the cursor in view
            int visible = Math.Max(1, Console.WindowHeight - 3 - 4 - 6);
            int first = Math.Clamp(cursor - visible / 2, 0, Math.Max(0, chans.Count - visible));

            for (int i = first; i < Math.Min(chans.Count, first + visible); i++)
            {
                Chan c = chans[i];
                bool here = i == cursor;
                string name = Esc(c.Cfg.Name);
                if (!c.IsP25) name = $"[grey]{name} ({Esc(c.Cfg.Mode)})[/]";
                else if (c.Selected) name = $"[bold]{name}[/]";
                if (here) name = $"[reverse]{name}[/]";

                string crypto = !c.IsP25 ? string.Empty : c.Encrypted ? Esc(KeyStore.AlgorithmName(c.TxAlg)) : "[grey]clear[/]";
                string key = !c.Encrypted ? string.Empty
                    : keys.TryGet(c.KeyRequestAlg, c.KeyId, out _) ? $"[green]✓ {c.KeyId:X4}[/]" : $"[yellow]– {c.KeyId:X4}[/]";

                table.AddRow(
                    new Markup(here ? "[bold]▶[/]" : " "),
                    new Markup(name),
                    new Markup(c.Tg != 0 ? c.Tg.ToString() : "?"),
                    new Markup(crypto),
                    new Markup(key),
                    new Markup(StatusCell(c)),
                    new Markup(Esc(c.LastCall)),
                    new Markup(c.Selected ? $"{c.Gain:0.##}x" : string.Empty));
            }

            return table;
        }

        private string StatusCell(Chan c)
        {
            P25Transmitter t = tx;
            if (t != null && t.IsTransmitting && txChan == c)
                return "[bold white on red] TX [/]";

            P25VoiceReceiver rx = c.Rx;
            if (c.Selected && rx != null && rx.InCall)
            {
                byte alg = rx.CurrentAlgorithm;
                bool enc = alg != 0 && alg != P25Defines.P25_ALGO_UNENCRYPT;
                string lockMark = enc ? " 🔒" : string.Empty;
                string noKey = enc && rx.CallHasMuted && !keys.TryGet(alg, rx.CurrentKeyId, out _) ? " [yellow]no key[/]" : string.Empty;
                return $"[bold black on green] RX {rx.CurrentSource} [/]{lockMark}{noKey}{(c.Muted ? " [grey](muted)[/]" : string.Empty)}";
            }

            if (!c.Selected && c.Active)
                return $"[grey]busy {c.ActiveSrc}[/]";

            if (c.Selected)
                return c.Muted ? "[grey]listening (muted)[/]" : "[green]listening[/]";

            return string.Empty;
        }

        /*
        ** Log
        */

        private void AddLog(string message)
        {
            string line = $"{DateTime.Now:HH:mm:ss} {message}";
            lock (uiLock)
            {
                log.AddLast(line);
                while (log.Count > MaxLogLines)
                    log.RemoveFirst();
            }

            if (logFile != null)
            {
                try { File.AppendAllText(logFile, line + Environment.NewLine); }
                catch { /* ignore */ }
            }
        }

        private static string Esc(string s) => Markup.Escape(s ?? string.Empty);
    } // public sealed class Tui
}
