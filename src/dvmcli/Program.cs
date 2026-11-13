// SPDX-License-Identifier: AGPL-3.0-only
/**
* dvmcli - headless Linux proof of concept for the DVM Desktop Dispatch Console.
*
*   dvmcli list     --codeplug FILE
*   dvmcli listen   --codeplug FILE (--channel NAME | --tg TGID) [--system NAME]
*                   [--wav FILE] [--no-play] [--player CMD] [--verbose]
*   dvmcli selftest
*/

using System.Reflection;
using dvmconsole;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace dvmcli
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                PrintUsage();
                return args.Length == 0 ? 2 : 0;
            }

            if (args[0] is "--version" or "version")
            {
                Console.WriteLine($"dvmcli {Assembly.GetExecutingAssembly().GetName().Version}");
                return 0;
            }

            try
            {
                Options opt = Options.Parse(args.Skip(1).ToArray());
                return args[0] switch
                {
                    "list" => List(opt),
                    "listen" => Listen(opt),
                    "tui" => RunTui(opt),
                    "selftest" => SelfTest.Run(),
                    _ => Usage($"unknown command '{args[0]}'")
                };
            }
            catch (ArgumentException ex)
            {
                return Usage(ex.Message);
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                Console.Error.WriteLine($"error: could not load libvocoder ({ex.Message}). It must be next to dvmcli.");
                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return 1;
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine("dvmcli - DVM console proof of concept for Linux");
            Console.WriteLine();
            Console.WriteLine("usage:");
            Console.WriteLine("  dvmcli list     --codeplug FILE");
            Console.WriteLine("  dvmcli listen   --codeplug FILE (--channel NAME | --tg TGID) [--system NAME]");
            Console.WriteLine("                  [--wav FILE] [--no-play] [--player CMD] [--verbose]");
            Console.WriteLine("  dvmcli tui      --codeplug FILE [--system NAME] [--tx-limit SECONDS] [--log FILE]");
            Console.WriteLine("                  [--marker-interval SECONDS] [--settings FILE | --no-settings]");
            Console.WriteLine("  dvmcli selftest");
            Console.WriteLine();
            Console.WriteLine("listen follows one P25 talkgroup and plays clear calls through PipeWire (pw-play).");
            Console.WriteLine("Press Ctrl+C to stop.");
        }

        private static int Usage(string message)
        {
            Console.Error.WriteLine($"error: {message}");
            Console.Error.WriteLine("run 'dvmcli --help' for usage");
            return 2;
        }

        /// <summary>
        /// Loads a codeplug the same way the console does.
        /// </summary>
        public static Codeplug LoadCodeplug(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("--codeplug is required");

            path = Path.GetFullPath(path);
            if (!File.Exists(path))
                throw new ArgumentException($"codeplug not found: {path}");

            IDeserializer deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();

            Codeplug codeplug = deserializer.Deserialize<Codeplug>(File.ReadAllText(path))
                ?? throw new ArgumentException($"codeplug is empty: {path}");
            codeplug.SourcePath = path;

            if (codeplug.Systems == null || codeplug.Systems.Count == 0)
                throw new ArgumentException("codeplug has no systems");

            return codeplug;
        }

        private static IEnumerable<Codeplug.Channel> AllChannels(Codeplug cp) =>
            (cp.Zones ?? new List<Codeplug.Zone>()).SelectMany(z => z.Channels ?? new List<Codeplug.Channel>());

        private static int List(Options opt)
        {
            Codeplug cp = LoadCodeplug(opt.Codeplug);

            Console.WriteLine("Systems:");
            foreach (Codeplug.System s in cp.Systems)
                Console.WriteLine($"  {s.Name}: {s.Address}:{s.Port} peer {s.PeerId} ({s.Identity})");

            Console.WriteLine();
            Console.WriteLine($"{"Channel",-24} {"TG",8}  {"Mode",-6} {"Crypto",-8} System");
            foreach (Codeplug.Channel c in AllChannels(cp))
            {
                string crypto = string.IsNullOrWhiteSpace(c.Algo) || c.Algo.Equals("none", StringComparison.OrdinalIgnoreCase) ? "clear" : c.Algo;
                string note = c.GetChannelMode() == Codeplug.ChannelMode.P25 ? string.Empty : "  (not P25; not supported yet)";
                Console.WriteLine($"{c.Name,-24} {c.Tgid,8}  {c.Mode,-6} {crypto,-8} {c.System}{note}");
            }

            return 0;
        }

        private static int Listen(Options opt)
        {
            Codeplug cp = LoadCodeplug(opt.Codeplug);

            Codeplug.Channel channel = null;
            if (!string.IsNullOrWhiteSpace(opt.Channel))
            {
                channel = AllChannels(cp).FirstOrDefault(c => string.Equals(c.Name, opt.Channel, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"no channel named '{opt.Channel}' (run 'dvmcli list')");
            }

            uint tg;
            if (opt.Tg != null)
            {
                if (!uint.TryParse(opt.Tg, out tg))
                    throw new ArgumentException($"--tg must be a number, got '{opt.Tg}'");
            }
            else if (channel != null)
            {
                if (channel.GetChannelMode() != Codeplug.ChannelMode.P25)
                    throw new ArgumentException($"channel '{channel.Name}' is {channel.Mode}; this proof of concept only supports P25");
                if (!uint.TryParse(channel.Tgid, out tg))
                    throw new ArgumentException($"channel '{channel.Name}' has an invalid tgid '{channel.Tgid}'");
            }
            else
                throw new ArgumentException("give --channel NAME or --tg TGID");

            string systemName = opt.SystemName ?? channel?.System;
            Codeplug.System system = systemName == null
                ? cp.Systems[0]
                : cp.Systems.FirstOrDefault(s => string.Equals(s.Name, systemName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"no system named '{systemName}'");

            // the channel's configured key, requested from the FNE as soon as we're logged in
            bool channelEncrypted = channel != null && !string.IsNullOrWhiteSpace(channel.Algo) &&
                                    !channel.Algo.Equals("none", StringComparison.OrdinalIgnoreCase);
            byte configuredAlg = channelEncrypted ? channel.GetKeyRequestAlgoId() : (byte)0;
            ushort configuredKeyId = channelEncrypted ? channel.GetKeyId() : (ushort)0;
            if (channelEncrypted)
                Say($"'{channel.Name}' uses {KeyStore.AlgorithmName(configuredAlg)} key 0x{configuredKeyId:X4}; it will be requested from the FNE");

            List<IAudioSink> sinks = new List<IAudioSink>();
            try
            {
                if (!opt.NoPlay)
                {
                    PipeWireSink pw = new PipeWireSink(opt.Player);
                    sinks.Add(pw);
                    Say($"audio: playing through {pw.Command}");
                }
                if (!string.IsNullOrWhiteSpace(opt.Wav))
                {
                    sinks.Add(new WavSink(opt.Wav));
                    Say($"audio: recording to {Path.GetFullPath(opt.Wav)}");
                }
                if (sinks.Count == 0)
                    Say("audio: none (--no-play without --wav); showing call activity only");

                KeyStore keys = new KeyStore();
                using P25VoiceReceiver receiver = new P25VoiceReceiver(tg, keys);
                receiver.Status += Say;
                receiver.Audio += samples =>
                {
                    ApplyGain(samples, opt.Gain);
                    foreach (IAudioSink sink in sinks)
                        sink.Write(samples);
                };

                CliPeer peer = new CliPeer(system, keys, opt.Verbose);
                peer.Status += Say;
                peer.P25Frame += e => receiver.Handle(e.DstId, e.SrcId, e.DUID, e.StreamId, e.Data);

                // a call using a key we don't hold yet asks the FNE for it
                receiver.KeyNeeded += (alg, kid) => peer.RequestKey(alg, kid);
                if (channelEncrypted && KeyStore.IsSupported(configuredAlg))
                    peer.RequestKey(configuredAlg, configuredKeyId);   // queued until login completes

                using ManualResetEventSlim stop = new ManualResetEventSlim(false);
                Console.CancelKeyPress += (s, e) => { e.Cancel = true; stop.Set(); };
                AppDomain.CurrentDomain.ProcessExit += (s, e) => stop.Set();

                Say($"connecting to {system.Name} ({system.Address}:{system.Port}) as peer {system.PeerId}, following tg {tg}{(channel != null ? $" ({channel.Name})" : string.Empty)}");
                peer.Start();

                DateTime started = DateTime.Now;
                bool warned = false;
                while (!stop.Wait(1000))
                {
                    if (!peer.Connected && !warned && (DateTime.Now - started).TotalSeconds > 20)
                    {
                        warned = true;
                        Say("still not connected after 20 s: check the address, port, peer ID, password, and that NetBird is up");
                    }
                }

                Say($"stopping ({receiver.CallsSeen} calls, {receiver.FramesDecoded} voice frames decoded)");
                peer.Stop();
                return 0;
            }
            finally
            {
                foreach (IAudioSink sink in sinks)
                    sink.Dispose();
            }
        }

        private static int RunTui(Options opt)
        {
            Codeplug cp = LoadCodeplug(opt.Codeplug);
            Codeplug.System system = opt.SystemName == null
                ? cp.Systems[0]
                : cp.Systems.FirstOrDefault(s => string.Equals(s.Name, opt.SystemName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"no system named '{opt.SystemName}'");

            if (PipeWireSink.FindPlayer() == null)
                Console.Error.WriteLine("warning: pw-play not found; you'll see activity but hear nothing");
            if (PipeWireCapture.FindRecorder() == null)
                Console.Error.WriteLine("warning: pw-record not found; transmit won't work");

            string logFile = string.IsNullOrWhiteSpace(opt.LogFile) ? null : Path.GetFullPath(opt.LogFile);
            return new Tui(cp, system, TimeSpan.FromSeconds(opt.TxLimitSeconds), logFile, TimeSpan.FromSeconds(opt.MarkerSeconds),
                           opt.NoSettings ? null : opt.SettingsFile ?? AppSettings.DefaultPath).Run();
        }

        /// <summary>
        /// Applies a linear gain with clipping.
        /// </summary>
        public static void ApplyGain(short[] samples, double gain)
        {
            if (gain == 1.0)
                return;
            for (int i = 0; i < samples.Length; i++)
                samples[i] = (short)Math.Clamp(samples[i] * gain, short.MinValue, short.MaxValue);
        }

        private static readonly object sayLock = new object();

        private static void Say(string message)
        {
            lock (sayLock)
                Console.WriteLine($"{DateTime.Now:HH:mm:ss} {message}");
        }

        /// <summary>
        /// Command-line options.
        /// </summary>
        private sealed class Options
        {
            public string Codeplug, Channel, Tg, SystemName, Wav, Player, LogFile;
            public bool NoPlay, Verbose;
            public double Gain = 1.0;
            public int TxLimitSeconds = 120;
            public int MarkerSeconds = 10;
            public string SettingsFile;
            public bool NoSettings;

            public static Options Parse(string[] args)
            {
                Options o = new Options();
                for (int i = 0; i < args.Length; i++)
                {
                    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
                    switch (args[i])
                    {
                        case "--codeplug": case "-c": o.Codeplug = Next(); break;
                        case "--channel": o.Channel = Next(); break;
                        case "--tg": o.Tg = Next(); break;
                        case "--system": o.SystemName = Next(); break;
                        case "--wav": o.Wav = Next(); break;
                        case "--player": o.Player = Next(); break;
                        case "--gain":
                            if (!double.TryParse(Next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out o.Gain) || o.Gain <= 0 || o.Gain > 20)
                                throw new ArgumentException("--gain must be a number between 0 and 20");
                            break;
                        case "--no-play": o.NoPlay = true; break;
                        case "--verbose": case "-v": o.Verbose = true; break;
                        case "--log": o.LogFile = Next(); break;
                        case "--settings":
                            o.SettingsFile = Next();
                            break;
                        case "--no-settings":
                            o.NoSettings = true;
                            break;
                        case "--marker-interval":
                            if (!int.TryParse(Next(), out o.MarkerSeconds) || o.MarkerSeconds < 5 || o.MarkerSeconds > 600)
                                throw new ArgumentException("--marker-interval must be between 5 and 600 seconds");
                            break;
                        case "--tx-limit":
                            if (!int.TryParse(Next(), out o.TxLimitSeconds) || o.TxLimitSeconds < 5 || o.TxLimitSeconds > 900)
                                throw new ArgumentException("--tx-limit must be between 5 and 900 seconds");
                            break;
                        default: throw new ArgumentException($"unknown option '{args[i]}'");
                    }
                }
                return o;
            }
        }
    } // public static class Program
}
