// SPDX-License-Identifier: AGPL-3.0-only
/**
* dvmcli - headless Linux proof of concept for the DVM Desktop Dispatch Console.
*
* Offline self-test, run by CI on Linux ARM64 and x86-64. It checks that libvocoder loads, that
* IMBE encode/decode round-trips, that the receiver pulls the nine codewords out of an
* LDU frame and handles calls, that encrypted calls decrypt to exactly the clear audio,
* and that codeplugs and WAV files are read/written correctly. It does not touch the network.
*/

using System.Runtime.InteropServices;
using dvmconsole;
using fnecore.P25;

namespace dvmcli
{
    public static class SelfTest
    {
        private static int failures;

        private const ushort TestKeyId = 0x0561;
        private static readonly byte[] TestKey = Enumerable.Range(0, 32).Select(i => (byte)(0x10 + i * 7)).ToArray();
        private static readonly byte[] TestMi = { 0x3A, 0x91, 0x0C, 0x5E, 0xB2, 0x47, 0xD8, 0x13, 0x00 };

        private static void Check(bool ok, string what)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}: {what}");
            if (!ok)
            {
                failures++;
                // surfaces as an annotation on the GitHub Actions run
                if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
                    Console.WriteLine($"::error title=selftest::FAIL: {what}");
            }
        }

        public static int Run()
        {
            failures = 0;
            Console.WriteLine($"dvmcli self-test on {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture})");

            // 1. libvocoder loads and round-trips voice-like audio
            MBEEncoder encoder = new MBEEncoder(MBE_MODE.IMBE_88BIT);
            MBEDecoder decoder = new MBEDecoder(MBE_MODE.IMBE_88BIT);
            Check(true, "libvocoder loaded (encoder and decoder created)");

            const int frames = 90;                       // five superframes (LDU1 + LDU2 = 18 frames each)
            List<byte[]> codewords = new List<byte[]>();
            double outEnergy = 0;
            for (int f = 0; f < frames; f++)
            {
                short[] pcm = VoicedFrame(f);
                byte[] cw = new byte[P25VoiceReceiver.ImbeLength];
                encoder.encode(pcm, cw);
                codewords.Add(cw);

                short[] back = new short[P25VoiceReceiver.FrameSamples];
                decoder.decode(cw, back);
                if (f >= 10)
                    foreach (short s in back)
                        outEnergy += (double)s * s;
            }
            double rms = Math.Sqrt(outEnergy / ((frames - 10) * P25VoiceReceiver.FrameSamples));
            Check(rms > 100, $"IMBE round trip produces audio (output RMS {rms:F0})");
            Check(codewords.Select(c => Convert.ToHexString(c)).Distinct().Count() > 1, "encoder output varies frame to frame");

            // 2. codeword extraction from a network LDU frame
            byte[] ldu1 = BuildLduFrame(P25VoiceReceiver.Ldu1FirstRecord, codewords.Take(9).ToList());
            byte[] ldu2 = BuildLduFrame(P25VoiceReceiver.Ldu2FirstRecord, codewords.Skip(9).Take(9).ToList());
            bool ok1 = P25VoiceReceiver.TryExtractCodewords(ldu1, P25VoiceReceiver.Ldu1FirstRecord, out byte[][] got1);
            bool ok2 = P25VoiceReceiver.TryExtractCodewords(ldu2, P25VoiceReceiver.Ldu2FirstRecord, out byte[][] got2);
            Check(ok1 && Enumerable.Range(0, 9).All(i => got1[i].SequenceEqual(codewords[i])), "LDU1: all nine codewords extracted in order");
            Check(ok2 && Enumerable.Range(0, 9).All(i => got2[i].SequenceEqual(codewords[9 + i])), "LDU2: all nine codewords extracted in order");
            Check(!P25VoiceReceiver.TryExtractCodewords(ldu1, P25VoiceReceiver.Ldu2FirstRecord, out _), "LDU1 frame is rejected when LDU2 records are expected");
            byte[] corrupt = (byte[])ldu1.Clone();
            corrupt[P25VoiceReceiver.PayloadOffset + 53] = 0x00;
            Check(!P25VoiceReceiver.TryExtractCodewords(corrupt, P25VoiceReceiver.Ldu1FirstRecord, out _), "frame with a damaged record header is rejected");
            Check(!P25VoiceReceiver.TryExtractCodewords(new byte[100], P25VoiceReceiver.Ldu1FirstRecord, out _), "short frame is rejected");

            byte[] syncFrame = BuildLduFrame(P25VoiceReceiver.Ldu2FirstRecord, codewords.Skip(9).Take(9).ToList(), 0, P25Defines.P25_ALGO_AES, TestKeyId, TestMi);
            P25VoiceReceiver.ReadEncryptionSync(syncFrame, out byte sAlg, out ushort sKid, out byte[] sMi);
            Check(sAlg == P25Defines.P25_ALGO_AES && sKid == TestKeyId && sMi.SequenceEqual(TestMi) &&
                  P25VoiceReceiver.TryExtractCodewords(syncFrame, P25VoiceReceiver.Ldu2FirstRecord, out byte[][] syncCw) &&
                  Enumerable.Range(0, 9).All(i => syncCw[i].SequenceEqual(codewords[9 + i])),
                  "LDU2 encryption sync (algorithm, key ID, MI) read back without disturbing the voice");

            // 3. clear call on the followed talkgroup, traffic on others ignored
            using (P25VoiceReceiver rx = new P25VoiceReceiver(504))
            {
                List<short> audio = new List<short>();
                List<byte[]> fed = new List<byte[]>();
                List<string> status = new List<string>();
                rx.Audio += s => audio.AddRange(s);
                rx.Codeword += fed.Add;
                rx.Status += status.Add;

                rx.Handle(999, 1234, P25DUID.LDU1, 77, ldu1);           // other talkgroup
                Check(rx.CallsSeen == 0 && audio.Count == 0, "traffic on another talkgroup is ignored");

                foreach ((P25DUID duid, byte[] frame) in BuildCall(codewords, P25Defines.P25_ALGO_UNENCRYPT, 0, null, withHdu: true))
                    rx.Handle(504, 1234, duid, 42, frame);
                rx.Handle(504, 1234, P25DUID.TDU, 42, new byte[24]);

                Check(rx.CallsSeen == 1, "one call start detected");
                Check(rx.FramesDecoded == frames && rx.FramesMuted == 0, $"clear call: all {frames} voice frames played");
                Check(SameCodewords(fed, codewords), "clear call: decoder fed exactly the transmitted codewords, in order");
                Check(Rms(audio) > 100, $"clear call audio is not silent (RMS {Rms(audio):F0})");
                Check(status.Any(s => s.StartsWith("CALL START")) && status.Any(s => s.StartsWith("CALL END") && s.Contains("clear")), "call start and end reported");
            }

            // 4. late entry (no HDU): muted until the first LDU2 gives sync, then plays
            using (P25VoiceReceiver rx = new P25VoiceReceiver(504))
            {
                foreach ((P25DUID duid, byte[] frame) in BuildCall(codewords, P25Defines.P25_ALGO_UNENCRYPT, 0, null, withHdu: false))
                    rx.Handle(504, 1234, duid, 43, frame);
                Check(rx.FramesMuted == 18 && rx.FramesDecoded == frames - 18, "late-entry clear call plays from the second superframe");
            }

            // 5. encrypted call (AES-256) with the key: decrypts to exactly the clear audio
            KeyStore keys = new KeyStore();
            Check(keys.Store(P25Defines.P25_ALGO_AES, TestKeyId, TestKey), "AES key stored");
            Check(!keys.Store(0x99, 1, TestKey), "unsupported algorithm is refused");
            using (P25VoiceReceiver rx = new P25VoiceReceiver(500, keys))
            {
                List<short> audio = new List<short>();
                List<byte[]> fed = new List<byte[]>();
                List<string> status = new List<string>();
                int keyRequests = 0;
                rx.Audio += s => audio.AddRange(s);
                rx.Codeword += fed.Add;
                rx.Status += status.Add;
                rx.KeyNeeded += (_, _) => keyRequests++;

                List<(P25DUID, byte[])> call = BuildCall(codewords, P25Defines.P25_ALGO_AES, TestKeyId, TestKey, withHdu: true);
                Check(!call[0].Item2.Skip(P25VoiceReceiver.PayloadOffset + 10).Take(11).SequenceEqual(codewords[0]), "test call is really encrypted on the wire");

                foreach ((P25DUID duid, byte[] frame) in call)
                    rx.Handle(500, 1234, duid, 50, frame);
                rx.Handle(500, 1234, P25DUID.TDU, 50, new byte[24]);

                Check(rx.FramesDecoded == frames && rx.FramesMuted == 0 && keyRequests == 0, $"AES call: all {frames} frames decrypted, no key request needed");
                Check(SameCodewords(fed, codewords), "AES call: every codeword decrypts back to the clear original (MI cycled across 5 superframes)");
                Check(Rms(audio) > 100, $"decrypted call audio is not silent (RMS {Rms(audio):F0})");
                Check(status.Any(s => s.Contains("decrypting AES-256 key 0x0561")), "decryption reported");
            }

            // 6. encrypted call without the key: muted and the key is requested; key arriving mid-call unmutes
            KeyStore late = new KeyStore();
            using (P25VoiceReceiver rx = new P25VoiceReceiver(500, late))
            {
                List<(byte, ushort)> requested = new List<(byte, ushort)>();
                rx.KeyNeeded += (a, k) => requested.Add((a, k));

                List<(P25DUID, byte[])> call = BuildCall(codewords, P25Defines.P25_ALGO_AES, TestKeyId, TestKey, withHdu: true);
                for (int i = 0; i < 4; i++)                          // first two superframes, no key
                    rx.Handle(500, 1234, call[i].Item1, 51, call[i].Item2);
                Check(rx.FramesDecoded == 0 && rx.FramesMuted == 36, "AES call without a key is muted");
                Check(requested.Count == 1 && requested[0] == (P25Defines.P25_ALGO_AES, TestKeyId), "missing key requested once, with the right algorithm and key ID");

                late.Store(P25Defines.P25_ALGO_AES, TestKeyId, TestKey); // FNE answers
                for (int i = 4; i < call.Count; i++)
                    rx.Handle(500, 1234, call[i].Item1, 51, call[i].Item2);
                Check(rx.FramesDecoded > 0 && rx.FramesDecoded == frames - 36 - 18,
                      "key arriving mid-call unmutes from the next superframe after an LDU2");
            }

            // 7. transmit: frames built for the FNE round-trip through the receiver
            TransmitTests(codewords.Count);
            ToneTests();

            // 8. codeplug parsing (same settings as the console)
            string tmp = Path.Combine(Path.GetTempPath(), $"dvmcli-selftest-{Environment.ProcessId}");
            Directory.CreateDirectory(tmp);
            try
            {
                string cpPath = Path.Combine(tmp, "codeplug.yml");
                File.WriteAllText(cpPath, SampleCodeplug);
                Codeplug cp = Program.LoadCodeplug(cpPath);
                Codeplug.System sys = cp.Systems.FirstOrDefault();
                List<Codeplug.Channel> chans = cp.Zones?.FirstOrDefault()?.Channels;
                Codeplug.Channel ch = chans?.FirstOrDefault();
                Codeplug.Channel sec = chans?.Skip(1).FirstOrDefault();
                Check(sys != null && sys.Name == "Test FNE" && sys.PeerId == 9000001 && sys.Port == 62031, "codeplug system parsed");
                Check(ch != null && ch.Tgid == "504" && ch.GetChannelMode() == Codeplug.ChannelMode.P25 && ch.SelectableEncryption, "codeplug channel parsed (including dev-only keys)");
                Check(sec != null && sec.GetKeyRequestAlgoId() == P25Defines.P25_ALGO_AES && sec.GetKeyId() == TestKeyId, "encrypted channel's algorithm and key ID read from the codeplug");

                // 9. TUI: card grid, buttons and clicks (no terminal or network needed)
                TuiTests(cp, sys);

                // 10. audio device lookup (pw-dump parsing, --output / --input matching, startup report)
                AudioDeviceTests();

                // 11. microphone level meter
                MeterTests(cp, sys);

                // 8. WAV writer
                string wavPath = Path.Combine(tmp, "test.wav");
                using (WavSink wav = new WavSink(wavPath))
                    for (int i = 0; i < 50; i++)
                        wav.Write(VoicedFrame(i));
                byte[] wavBytes = File.ReadAllBytes(wavPath);
                uint dataLen = BitConverter.ToUInt32(wavBytes, 40);
                Check(wavBytes.Length == 44 + 50 * 320 && dataLen == 50 * 320 && BitConverter.ToUInt32(wavBytes, 24) == 8000,
                      "WAV file has a valid 8 kHz header and the right length");
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { /* ignore */ }
            }

            Console.WriteLine(failures == 0 ? "ALL TESTS PASSED" : $"{failures} TEST(S) FAILED");
            return failures == 0 ? 0 : 1;
        }

        // trimmed pw-dump output: two sinks, three sources, a playback stream and the "default" metadata
        private const string PwDumpFixture = """
[
  { "id": 34, "type": "PipeWire:Interface:Node", "info": { "props": {
      "media.class": "Audio/Sink", "object.serial": 34,
      "node.name": "alsa_output.pci-0000_0a_00.3.iec958-stereo",
      "node.description": "Family 17h (Models 00h-0fh) HD Audio Controller Digital Stereo (IEC958)" } } },
  { "id": 77, "type": "PipeWire:Interface:Node", "info": { "props": {
      "media.class": "Audio/Sink", "object.serial": 2077,
      "node.name": "alsa_output.usb-Plantronics_Savi_8200-00.analog-stereo",
      "node.description": "Plantronics Savi 8200 Office Series Analog Stereo" } } },
  { "id": 46, "type": "PipeWire:Interface:Node", "info": { "props": {
      "media.class": "Audio/Source", "object.serial": 46,
      "node.name": "alsa_input.pci-0000_0a_00.3.analog-stereo",
      "node.description": "Family 17h (Models 00h-0fh) HD Audio Controller Analog Stereo" } } },
  { "id": 73, "type": "PipeWire:Interface:Node", "info": { "props": {
      "media.class": "Audio/Source", "object.serial": 73,
      "node.name": "alsa_input.usb-Plantronics_Savi_8200-01.mono-fallback",
      "node.description": "Plantronics Savi 8200 Office Series Mono" } } },
  { "id": 75, "type": "PipeWire:Interface:Node", "info": { "props": {
      "media.class": "Audio/Source", "object.serial": 75,
      "node.name": "alsa_input.usb-USB_2.0_Camera-02.mono-fallback",
      "node.description": "USB 2.0 Camera Mono" } } },
  { "id": 102, "type": "PipeWire:Interface:Node", "info": { "props": {
      "media.class": "Stream/Output/Audio", "node.name": "Brave", "node.description": "Brave" } } },
  { "id": 31, "type": "PipeWire:Interface:Metadata", "props": { "metadata.name": "default" }, "metadata": [
      { "subject": 0, "key": "default.audio.sink", "type": "Spa:String:JSON",
        "value": { "name": "alsa_output.usb-Plantronics_Savi_8200-00.analog-stereo" } },
      { "subject": 0, "key": "default.audio.source", "type": "Spa:String:JSON",
        "value": "{\"name\":\"alsa_input.usb-Plantronics_Savi_8200-01.mono-fallback\"}" } ] }
]
""";

        private static short[] Sine(double amplitude, int n = 160, double hz = 1000)
        {
            short[] b = new short[n];
            for (int i = 0; i < n; i++)
                b[i] = (short)Math.Round(amplitude * Math.Sin(2 * Math.PI * hz * i / 8000.0));
            return b;
        }

        private static void MeterTests(Codeplug cp, Codeplug.System sys)
        {
            DateTime t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            LevelMeter m = new LevelMeter();
            Check(!m.Read(t0).Active, "mic meter: idle before any audio");

            m.Push(Sine(16384), t0);                         // -6 dBFS peak, about -9 dBFS RMS
            LevelMeter.Reading r = m.Read(t0);
            Check(r.Active && Math.Abs(r.LevelDb - -9.03) < 0.3 && Math.Abs(r.PeakDb - -6.02) < 0.3 && !r.Clipping,
                  $"mic meter: a -6 dBFS sine reads about -9 dB RMS with a -6 dB peak ({r.LevelDb:F1} / {r.PeakDb:F1})");

            m.Push(new short[160], t0.AddSeconds(0.5));       // silence: the level falls at 20 dB/s, the peak holds
            r = m.Read(t0.AddSeconds(0.5));
            Check(Math.Abs(r.LevelDb - -19.03) < 0.3 && Math.Abs(r.PeakDb - -6.02) < 0.3,
                  $"mic meter: level falls 10 dB in 0.5 s and the peak is held ({r.LevelDb:F1} / {r.PeakDb:F1})");

            m.Push(new short[160], t0.AddSeconds(3.0));       // -19 dB at 0.5 s reaches the -60 floor about 2 s later
            r = m.Read(t0.AddSeconds(3.0));
            Check(r.LevelDb <= LevelMeter.FloorDb + 0.01 && r.PeakDb <= LevelMeter.FloorDb + 0.01, "mic meter: the peak hold expires and silence reads as the floor");
            Check(!m.Read(t0.AddSeconds(3.5)).Active, "mic meter: goes idle when audio stops");

            short[] loud = Sine(32767);
            m.Push(loud, t0.AddSeconds(4));
            Check(m.Read(t0.AddSeconds(4)).Clipping, "mic meter: a full-scale block shows CLIP");
            m.Push(Sine(1000), t0.AddSeconds(6));
            Check(!m.Read(t0.AddSeconds(6)).Clipping, "mic meter: CLIP clears after the hold time");

            // in the TUI: the bottom edge of the title box
            Tui tui = new Tui(cp, sys, TimeSpan.FromSeconds(120), null) { NoAudio = true };
            Check(Tui.MeterCells(31) == 13 && Tui.MeterCells(20) == 0, "mic meter: bar width fits the title box, hidden when there's no room");
            string edge = tui.Render(120, 40).RowText(Tui.TitleHeight - 1);
            Check(edge.Contains("MIC off"), "TUI shows 'MIC off' while the microphone is closed");

            tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Char, 'v'));
            Check(tui.MicMonitor, "TUI v opens the mic for the meter");
            tui.MicLevel.Push(Sine(8192));                    // about -15 dB RMS
            edge = tui.Render(120, 40).RowText(Tui.TitleHeight - 1);
            Check(edge.Contains(" MIC ") && edge.Contains("█") && edge.Contains("dB") && !edge.Contains("MIC off"),
                  $"TUI meter shows the level ({edge.Trim()})");
            tui.MicLevel.Push(loud);
            edge = tui.Render(120, 40).RowText(Tui.TitleHeight - 1);
            Check(edge.Contains("CLIP"), "TUI meter shows CLIP on a full-scale input");

            int at = edge.IndexOf("MIC", StringComparison.Ordinal);
            tui.HandleInput(new InputEvent(InputKind.Click, X: at, Y: Tui.TitleHeight - 1));
            Check(!tui.MicMonitor && tui.Render(120, 40).RowText(Tui.TitleHeight - 1).Contains("MIC off"), "TUI click on the meter closes the mic");
        }

        private static void AudioDeviceTests()
        {
            AudioDeviceList list = AudioDeviceList.Parse(PwDumpFixture);
            Check(list.Outputs.Count == 2 && list.Inputs.Count == 3, "pw-dump: outputs and inputs found, streams left out");
            Check(list.DefaultOutput?.Id == 77 && list.DefaultInput?.Id == 73, "pw-dump: default output and input read from metadata (object and string forms)");

            AudioDevice savi = AudioDeviceList.Resolve(list.Outputs, "savi", "--output");
            Check(savi.Id == 77, "--output matches a unique part of the name, case ignored");
            Check(AudioDeviceList.Resolve(list.Outputs, "34", "--output").Id == 34 &&
                  AudioDeviceList.Resolve(list.Outputs, "2077", "--output").Id == 77, "--output matches an ID or a serial");
            Check(AudioDeviceList.Resolve(list.Inputs, "alsa_input.usb-USB_2.0_Camera-02.mono-fallback", "--input").Id == 75,
                  "--input matches an exact node name");

            string Error(Action a)
            {
                try { a(); return null; }
                catch (ArgumentException ex) { return ex.Message; }
            }
            string ambiguous = Error(() => AudioDeviceList.Resolve(list.Inputs, "mono", "--input"));
            Check(ambiguous != null && ambiguous.Contains("more than one") && ambiguous.Contains("USB 2.0 Camera Mono"),
                  "an ambiguous name is refused and the matches are listed");
            string missing = Error(() => AudioDeviceList.Resolve(list.Outputs, "HDMI", "--output"));
            Check(missing != null && missing.Contains("Plantronics Savi 8200 Office Series Analog Stereo"),
                  "an unknown name is refused and the outputs are listed");

            AudioSelection defaults = AudioSelection.Choose(null, null, () => list);
            Check(defaults.OutputTarget == null && defaults.InputTarget == null &&
                  defaults.OutputReport == "audio output: Plantronics Savi 8200 Office Series Analog Stereo (system default)" &&
                  defaults.InputReport == "audio input: Plantronics Savi 8200 Office Series Mono (system default)",
                  "no options: the system defaults are used and named in the log");

            AudioSelection chosen = AudioSelection.Choose("IEC958", "camera", () => list);
            Check(chosen.OutputTarget == "alsa_output.pci-0000_0a_00.3.iec958-stereo" &&
                  chosen.InputTarget == "alsa_input.usb-USB_2.0_Camera-02.mono-fallback" &&
                  chosen.OutputReport.EndsWith("(--output)") && chosen.InputReport.EndsWith("(--input)"),
                  "--output / --input: the chosen devices are targeted by node name and named in the log");

            AudioSelection noPw = AudioSelection.Choose(null, null, () => throw new InvalidOperationException("pw-dump not found"));
            Check(noPw.OutputTarget == null && noPw.OutputReport.Contains("PipeWire default") && noPw.OutputReport.Contains("pw-dump not found"),
                  "without pw-dump the defaults are still used and the log says why they can't be named");
        }

        private static void TuiTests(Codeplug cp, Codeplug.System sys)
        {
            // input parser
            InputParser parser = new InputParser();
            List<InputEvent> ev = parser.Feed(System.Text.Encoding.ASCII.GetBytes("\x1b[A\x1b[B\x1b[C\x1b[D \rq\x03"));
            Check(ev.Select(e => e.Key).SequenceEqual(new[] { InputKey.Up, InputKey.Down, InputKey.Right, InputKey.Left, InputKey.Space, InputKey.Enter, InputKey.Char, InputKey.CtrlC }) &&
                  ev[6].Char == 'q', "TUI input: arrows, Space, Enter, letters and Ctrl+C parsed");
            ev = parser.Feed(System.Text.Encoding.ASCII.GetBytes("\x1b[<0;12;5M\x1b[<0;12;5m\x1b[<64;3;4M\x1b[<65;3;4M\x1b[5~\x1b[6~"));
            Check(ev.Count == 5 && ev[0].Kind == InputKind.Click && ev[0].X == 11 && ev[0].Y == 4 && ev[1].Kind == InputKind.WheelUp &&
                  ev[2].Kind == InputKind.WheelDown && ev[3].Key == InputKey.PageUp && ev[4].Key == InputKey.PageDown, "TUI input: SGR mouse click (release ignored) and wheel parsed");
            Check(parser.Feed(new byte[] { 0x1b, (byte)'[', (byte)'<', (byte)'0', (byte)';' }).Count == 0 &&
                  parser.Feed(System.Text.Encoding.ASCII.GetBytes("7;2M")) is { Count: 1 } split && split[0].Kind == InputKind.Click && split[0].X == 6,
                  "TUI input: a mouse report split across reads is reassembled");
            ev = parser.Feed(new byte[] { 0x1b });
            Check(ev.Count == 1 && ev[0].Key == InputKey.Escape, "TUI input: a lone Esc is the Escape key");

            // screen buffer
            ScreenBuffer sb = new ScreenBuffer();
            sb.Begin(20, 3);
            int used = sb.Text(0, 0, "AES 🔒 x", Style.Plain);
            Check(used == 8 && sb.RowText(0).StartsWith("AES 🔒 x"), "TUI screen: the lock emoji takes two cells");
            sb.Text(0, 1, "abcdefghijklmnop", Style.Plain, 6);
            Check(sb.RowText(1).StartsWith("abcde… "), "TUI screen: long text is truncated with an ellipsis");
            bool hit = false;
            sb.OnClick(new Rect(2, 2, 4, 1), () => hit = true);
            sb.HitTest(3, 2)?.Invoke();
            Check(hit && sb.HitTest(6, 2) == null, "TUI screen: clicks map to the region under the pointer");
            Check(sb.Flush().Contains("🔒") && !sb.Flush().Contains("🔒"), "TUI screen: unchanged frames send nothing new");
            sb.Begin(10, 1);
            sb.Text(0, 0, "9511", Style.Plain);
            string plain = sb.Flush();
            Check(Style.Plain.Fg == Ansi.Default && Style.Plain.Bg == Ansi.Default && !plain.Contains("[0;30") && !plain.Contains(";40"),
                  "TUI screen: plain text uses the terminal's default colours (not black on black)");

            // grid geometry
            Tui.GridGeometry g = Tui.Layout(120, 40, 10);
            Check(g.Columns == 4 && g.CardWidth == 30 && g.TotalRows == 3 && g.LogHeight >= 6 && g.LogHeight <= 10, $"TUI grid: 4 cards across at 120 columns ({g.VisibleRows} rows visible, log {g.LogHeight})");
            Check(Tui.Layout(60, 40, 10).Columns == 2 && Tui.Layout(300, 40, 10).Columns == 4, "TUI grid: fewer columns on narrow terminals, never more than 4");

            // clicks on a rendered frame
            Tui tui = new Tui(cp, sys, TimeSpan.FromSeconds(120), null) { NoAudio = true };
            ScreenBuffer frame = tui.Render(120, 40);
            Check(frame.RowText(1).Contains("PTT") && frame.RowText(1).Contains("MUTE") && frame.RowText(1).Contains("EXIT"), "TUI title bar has PTT, MUTE and EXIT buttons");
            Check(frame.RowText(Tui.TitleHeight).Contains("Calling") && frame.RowText(Tui.TitleHeight).Contains("Secure"), "TUI cards for both channels drawn side by side");
            Check(frame.RowText(Tui.TitleHeight + 1).Contains("🔒"), "TUI secure card shows the lock");
            Check(frame.RowText(Tui.TitleHeight).StartsWith("┏") && frame.RowText(Tui.TitleHeight).Contains("▶ Calling") &&
                  frame.RowText(Tui.TitleHeight).Substring(30).TrimStart().StartsWith("╭"), "TUI focused card has a heavy border; others are thin");
            Tui.GridGeometry bottom = Tui.Layout(120, 60, 2);
            Check(bottom.LogHeight <= 10 && bottom.LogTop + bottom.LogHeight == 60 - Tui.FooterHeight, "TUI log is capped and sits just above the footer");
            Check(Enumerable.Range(0, 40).Any(y => frame.RowText(y).Contains("Recent calls")), "TUI recent-calls panel drawn beside the log");

            tui.HandleInput(new InputEvent(InputKind.Click, X: 30 + 5, Y: Tui.TitleHeight + 3));    // body of card 2
            Check(tui.Cursor == 1 && tui.IsSelected(1) && !tui.IsSelected(0), "TUI click on a card focuses it and starts listening");
            tui.Render(120, 40);
            string top = tui.Render(120, 40).RowText(Tui.TitleHeight);
            int toggle = top.IndexOf("[●]", StringComparison.Ordinal);
            Check(toggle > 30, "TUI listening card shows [●]");
            tui.HandleInput(new InputEvent(InputKind.Click, X: toggle + 1, Y: Tui.TitleHeight));
            Check(!tui.IsSelected(1), "TUI click on [●] stops listening");

            tui.Render(120, 40);
            string title = tui.Render(120, 40).RowText(1);
            tui.HandleInput(new InputEvent(InputKind.Click, X: title.IndexOf("MUTE", StringComparison.Ordinal), Y: 0));
            Check(tui.GlobalMute, "TUI MUTE button mutes all audio");
            tui.Render(120, 40);
            tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Left));
            tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Space));    // PTT while offline: refused, no crash
            tui.Render(120, 40);
            tui.HandleInput(new InputEvent(InputKind.Click, X: title.IndexOf("EXIT", StringComparison.Ordinal), Y: 2));
            Check(tui.Cursor == 0 && tui.QuitRequested, "TUI keyboard focus, offline PTT refusal and EXIT button");
            tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Char, '3'));    // offline: refused, no crash
            tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Char, 'c'));
            string titleRow = tui.Render(120, 40).RowText(1);
            Check(tui.MarkerOn && titleRow.Contains("T1") && titleRow.Contains("T5") && titleRow.Contains("MARK"),
                  "TUI tone buttons T1-T5 and MARK in the title bar; c toggles the marker");
            Check(titleRow.Contains("PAGE"), "TUI PAGE button in the title bar");
            tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Char, 'p'));
            foreach (char ch in "11534") tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Char, ch));   // digits go to the field, not alert tones
            tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Backspace));
            tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Char, '.'));
            tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Char, '4'));
            tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Tab));
            foreach (char ch in "99") tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Char, ch));
            string dialog = string.Join("\n", Enumerable.Range(0, 40).Select(y => tui.Render(120, 40).RowText(y)));
            Check(tui.PageDialogOpen && dialog.Contains("Tone (QC II)") && dialog.Contains("1153.4") && dialog.Contains("SEND"),
                  "TUI page dialog opens with p and takes typed tones");
            tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Enter));    // 99 Hz: refused, dialog stays open
            dialog = string.Join("\n", Enumerable.Range(0, 40).Select(y => tui.Render(120, 40).RowText(y)));
            Check(tui.PageDialogOpen && dialog.Contains("tone B must be"), "TUI page dialog refuses an out-of-range tone");
            tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Escape));
            Check(!tui.PageDialogOpen, "TUI page dialog closes with Esc");

            // digital page (P25 call alert)
            byte[] alert = CliPeer.BuildCallAlert(1234567, 9511);
            // P25 layout: octet 0 opcode, 1 MFID, 2-3 reserved, 4-6 target, 7-9 source, 10-11 CRC
            // (checked on the bytes: fnecore's TSBK Decode reads the payload one octet early)
            Check(alert.Length == P25Defines.P25_TSBK_LENGTH_BYTES && (alert[0] & 0x3F) == P25Defines.TSBK_IOSP_CALL_ALRT && alert[1] == 0 &&
                  alert[2] == 0 && alert[3] == 0 && (alert[4] << 16 | alert[5] << 8 | alert[6]) == 1234567 &&
                  (alert[7] << 16 | alert[8] << 8 | alert[9]) == 9511 && fnecore.EDAC.CRC.CheckCCITT162(alert, P25Defines.P25_TSBK_LENGTH_BYTES),
                  "digital page: call alert TSBK carries target and source RIDs with a valid CRC");
            tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Char, 'd'));
            foreach (char ch in "1234567") tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Char, ch));
            dialog = string.Join("\n", Enumerable.Range(0, 40).Select(y => tui.Render(120, 40).RowText(y)));
            Check(tui.PageDialogOpen && tui.PageDialogDigital && dialog.Contains("Radio ID") && dialog.Contains("1234567"),
                  "TUI d opens the page dialog in digital mode and takes a radio ID");
            tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Enter));    // offline: refused with a message
            dialog = string.Join("\n", Enumerable.Range(0, 40).Select(y => tui.Render(120, 40).RowText(y)));
            Check(tui.PageDialogOpen && dialog.Contains("not connected"), "TUI digital page refused while offline, dialog stays open");
            tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Left));
            Check(!tui.PageDialogDigital, "TUI arrows switch the page dialog between tone and digital");
            tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Escape));

            // radio check / inhibit / uninhibit
            byte[] check = CliPeer.BuildExtendedFunction(ExtendedFunction.CHECK, 9511, 1234567);
            byte[] inhibit = CliPeer.BuildExtendedFunction(ExtendedFunction.INHIBIT, P25Defines.WUID_FNE, 1234567);
            Check((check[0] & 0x3F) == P25Defines.TSBK_IOSP_EXT_FNCT && check[2] == 0x00 && check[3] == 0x00 &&
                  (check[4] << 16 | check[5] << 8 | check[6]) == 9511 && (check[7] << 16 | check[8] << 8 | check[9]) == 1234567 &&
                  inhibit[3] == 0x7F && (inhibit[4] << 16 | inhibit[5] << 8 | inhibit[6]) == P25Defines.WUID_FNE &&
                  fnecore.EDAC.CRC.CheckCCITT162(check, P25Defines.P25_TSBK_LENGTH_BYTES) && fnecore.EDAC.CRC.CheckCCITT162(inhibit, P25Defines.P25_TSBK_LENGTH_BYTES),
                  "radio check / inhibit TSBKs carry function, argument and target with valid CRCs");
            tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Char, 'r'));
            foreach (char ch in "7654321") tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Char, ch));
            tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Tab));
            dialog = string.Join("\n", Enumerable.Range(0, 40).Select(y => tui.Render(120, 40).RowText(y)));
            Check(tui.RadioDialogOpen && tui.RadioDialogAction == "Inhibit" && dialog.Contains("Radio command") && dialog.Contains("7654321") &&
                  dialog.Contains("Uninhibit"), "TUI r opens the radio command box: radio ID, then Check / Inhibit / Uninhibit");
            tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Enter));
            dialog = string.Join("\n", Enumerable.Range(0, 40).Select(y => tui.Render(120, 40).RowText(y)));
            Check(tui.RadioDialogOpen && dialog.Contains("press SEND again"), "TUI inhibit asks for confirmation first");
            tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Enter));
            dialog = string.Join("\n", Enumerable.Range(0, 40).Select(y => tui.Render(120, 40).RowText(y)));
            Check(tui.RadioDialogOpen && dialog.Contains("not connected"), "TUI radio command refused while offline");
            // a radio's check ack, as seen in an FNE log: tgt = the radio, arg = the console RID
            byte[] ack = CliPeer.BuildExtendedFunction(ExtendedFunction.CHECK_ACK, 99511, 9511);
            byte[] fecAck = new byte[P25Defines.P25_TSBK_FEC_LENGTH_BYTES];
            new fnecore.EDAC.Trellis().Encode12(ack, ref fecAck);
            byte[] tsduFrame = new byte[24 + P25Defines.P25_TSDU_FRAME_LENGTH_BYTES];
            byte[] rawTsdu = new byte[P25Defines.P25_TSDU_FRAME_LENGTH_BYTES];
            P25Interleaver.Encode(fecAck, ref rawTsdu, 114, 318);
            Buffer.BlockCopy(rawTsdu, 0, tsduFrame, 24, rawTsdu.Length);
            byte[] back = CliPeer.DecodeTsdu(tsduFrame);
            Check(back != null && back.SequenceEqual(ack), "network TSDU frame decodes back to its TSBK (trellis, interleave, CRC)");
            tui.consoleRidForTest = 99511;
            tui.OnP25Control(P25Defines.TSBK_IOSP_EXT_FNCT, 9511, 9511, back);
            string logRows = string.Join("\n", Enumerable.Range(0, 40).Select(y => tui.Render(120, 40).RowText(y)));
            Check(logRows.Contains("radio 9511 answered the radio check"), "radio check ack addressed to the radio (console RID in the argument) is reported");
            tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Escape));
            Check(!tui.RadioDialogOpen && tui.Render(120, 40).RowText(1).Contains("RADIO") && tui.Render(120, 40).RowText(1).Contains("T6"),
                  "TUI title bar has T6 and RADIO");

            // remembered settings
            string settingsFile = Path.Combine(Path.GetTempPath(), $"dvmcli-settings-{Environment.ProcessId}.json");
            try
            {
                Tui first = new Tui(cp, sys, TimeSpan.FromSeconds(120), null, settingsPath: settingsFile) { NoAudio = true };
                first.Render(120, 40);
                first.HandleInput(new InputEvent(InputKind.Key, InputKey.Right));      // focus Secure
                first.HandleInput(new InputEvent(InputKind.Key, InputKey.Enter));      // listen
                first.HandleInput(new InputEvent(InputKind.Key, InputKey.Char, 'm'));  // mute it
                first.HandleInput(new InputEvent(InputKind.Key, InputKey.Char, '+'));
                first.HandleInput(new InputEvent(InputKind.Key, InputKey.Char, 't'));  // talk permit off
                first.SaveSettings();
                string json = File.ReadAllText(settingsFile);

                Tui second = new Tui(cp, sys, TimeSpan.FromSeconds(120), null, settingsPath: settingsFile) { NoAudio = true };
                second.RestoreSettings();
                Check(second.Cursor == 1 && second.IsSelected(1) && !second.IsSelected(0) && !second.TalkPermit && json.Contains("1.25") &&
                      !json.Contains("password", StringComparison.OrdinalIgnoreCase),
                      "settings remembered: listening channels, focus, volume, mute, talk permit (no passwords in the file)");
            }
            finally
            {
                try { File.Delete(settingsFile); } catch { /* ignore */ }
            }
        }

        /// <summary>
        /// Captures what a transmitter would send to the FNE; headers come from a real (offline) CliPeer.
        /// </summary>
        private sealed class CaptureSink : IP25TrafficSink
        {
            private readonly CliPeer headers;
            public readonly List<(byte[] Frame, ushort Seq, uint Stream)> Voice = new();
            public readonly List<(uint Src, uint Dst, uint Stream, bool Grant)> Tdus = new();

            public CaptureSink(CliPeer headers) => this.headers = headers;
            public byte[] CreateP25Header(P25DUID duid, fnecore.RemoteCallData callData, fnecore.CryptoParams cryptoParams) =>
                headers.CreateP25Header(duid, callData, cryptoParams);
            public void SendP25Voice(byte[] frame, ushort packetSequence, uint streamId) => Voice.Add(((byte[])frame.Clone(), packetSequence, streamId));
            public void SendP25Tdu(uint srcId, uint dstId, uint streamId, bool grantDemand)
            {
                int count = grantDemand ? 1 : 4;   // as CliPeer does
                for (int i = 0; i < count; i++)
                    Tdus.Add((srcId, dstId, streamId, grantDemand));
            }
        }

        private static void TransmitTests(int frames)
        {
            const uint rid = 9000001, tg = 504, secureTg = 500;

            // expected codewords: the encoder is deterministic, so a fresh one gives the transmitter's output
            MBEEncoder reference = new MBEEncoder(MBE_MODE.IMBE_88BIT);
            List<short[]> pcm = Enumerable.Range(0, frames).Select(VoicedFrame).ToList();
            List<byte[]> expected = pcm.Select(p => { byte[] c = new byte[P25VoiceReceiver.ImbeLength]; reference.encode(p, c); return c; }).ToList();

            Codeplug.System offline = new Codeplug.System { Name = "Test", Identity = "TEST", Address = "127.0.0.1", Port = 62031, PeerId = rid, Rid = rid.ToString(), Password = "x" };
            CliPeer headerPeer = new CliPeer(offline, new KeyStore(), verbose: false);
            Check(headerPeer.ConsoleRid == rid, "peer reads the console RID from the codeplug");

            // clear call
            CaptureSink clear = new CaptureSink(headerPeer);
            P25Transmitter tx = new P25Transmitter(clear, new KeyStore(), rid, tg, P25Defines.P25_ALGO_UNENCRYPT, 0);
            Check(tx.Start(out string err1) && err1 == null, "clear transmit starts");
            foreach (short[] p in pcm) tx.PushAudio(p);
            for (int i = 0; i < 5; i++) tx.PushAudio(pcm[i]);   // partial superframe at the end, dropped
            uint stream = tx.StreamId;
            tx.Stop();

            Check(clear.Tdus.Count == 5 && clear.Tdus[0].Grant && clear.Tdus.Skip(1).All(t => !t.Grant) &&
                  clear.Tdus.All(t => t.Src == rid && t.Dst == tg && t.Stream == stream && stream != 0),
                  "call framing: one grant demand, then four end TDUs, all on the call's stream ID");
            Check(clear.Voice.Count == frames / 9 && clear.Voice.All(v => v.Stream == stream) &&
                  clear.Voice.Select(v => (int)v.Seq).SequenceEqual(Enumerable.Range(0, frames / 9)),
                  $"{frames / 9} LDUs sent with RTP sequence 0..{frames / 9 - 1}; partial superframe dropped");

            byte[] l1 = clear.Voice[0].Frame;
            int lcRecord = P25VoiceReceiver.PayloadOffset + P25VoiceReceiver.RecordOffsets[2];
            int tgRecord = P25VoiceReceiver.PayloadOffset + P25VoiceReceiver.RecordOffsets[3];
            int srcRecord = P25VoiceReceiver.PayloadOffset + P25VoiceReceiver.RecordOffsets[4];
            Check(l1[22] == (byte)P25DUID.LDU1 && clear.Voice[1].Frame[22] == (byte)P25DUID.LDU2 && l1[23] == 178 && l1.Length == P25FrameBuilder.FrameSize,
                  "LDU1/LDU2 headers carry the DUID and payload length");
            Check(l1[lcRecord + 1] == P25Defines.LC_GROUP && l1[lcRecord + 3] == 0x04 &&
                  ((l1[tgRecord + 1] << 16) | (l1[tgRecord + 2] << 8) | l1[tgRecord + 3]) == tg &&
                  ((l1[srcRecord + 1] << 16) | (l1[srcRecord + 2] << 8) | l1[srcRecord + 3]) == rid,
                  "link control: group call, priority 4, talkgroup and source in the voice records");
            Check(l1[P25VoiceReceiver.HduFlagOffset] == P25Defines.P25_FT_HDU_VALID && l1[P25VoiceReceiver.HduAlgOffset] == P25Defines.P25_ALGO_UNENCRYPT,
                  "clear call marked clear in the HDU parameters");

            List<byte[]> fed = RoundTrip(clear, tg, rid, new KeyStore(), out long muted);
            Check(muted == 0 && SameCodewords(fed, expected), "clear transmit decodes back to exactly the encoded codewords");

            // encrypted call (AES-256)
            KeyStore keys = new KeyStore();
            keys.Store(P25Defines.P25_ALGO_AES, TestKeyId, TestKey);
            CaptureSink secure = new CaptureSink(headerPeer);
            P25Transmitter stx = new P25Transmitter(secure, keys, rid, secureTg, P25Defines.P25_ALGO_AES, TestKeyId);
            Check(stx.Start(out string err2) && err2 == null && stx.Encrypted, "AES transmit starts with the key held");
            foreach (short[] p in pcm) stx.PushAudio(p);
            stx.Stop();

            byte[] s1 = secure.Voice[0].Frame;
            Check(s1[P25VoiceReceiver.HduFlagOffset] == P25Defines.P25_FT_HDU_VALID && s1[P25VoiceReceiver.HduAlgOffset] == P25Defines.P25_ALGO_AES &&
                  ((s1[P25VoiceReceiver.HduKeyIdOffset] << 8) | s1[P25VoiceReceiver.HduKeyIdOffset + 1]) == TestKeyId &&
                  s1.Skip(P25VoiceReceiver.HduMiOffset).Take(P25Defines.P25_MI_LENGTH).Any(b => b != 0) &&
                  s1[lcRecord + 3] == 0x44,
                  "AES call: HDU carries algorithm, key ID and a random MI; service options marked protected");
            Check(P25VoiceReceiver.TryExtractCodewords(s1, P25VoiceReceiver.Ldu1FirstRecord, out byte[][] wire) && !wire[0].SequenceEqual(expected[0]),
                  "AES call is encrypted on the wire");

            List<byte[]> secureFed = RoundTrip(secure, secureTg, rid, keys, out long secureMuted);
            Check(secureMuted == 0 && SameCodewords(secureFed, expected), "AES transmit decrypts back to exactly the encoded codewords");

            // refusals
            CaptureSink none = new CaptureSink(headerPeer);
            P25Transmitter nokey = new P25Transmitter(none, new KeyStore(), rid, secureTg, P25Defines.P25_ALGO_AES, TestKeyId);
            Check(!nokey.Start(out string err3) && err3.Contains("not transmitting in the clear") && none.Tdus.Count == 0,
                  "secure channel without its key refuses to transmit (nothing sent)");
            P25Transmitter norid = new P25Transmitter(none, new KeyStore(), 0, tg, P25Defines.P25_ALGO_UNENCRYPT, 0);
            Check(!norid.Start(out _) && none.Tdus.Count == 0, "transmit without a console RID is refused");
        }

        /// <summary>
        /// Plays captured transmit frames into a receiver and returns the codewords it would decode.
        /// </summary>
        private static void ToneTests()
        {
            const uint rid = 9000001;

            // plans match the Windows console's built-in tones
            TonePlan a1 = TonePlan.Alert(1), a2 = TonePlan.Alert(2), a3 = TonePlan.Alert(3), mk = TonePlan.Marker();
            int lead = TonePlan.LeadInMs / 20;
            Check(a1.Frames.Length % 9 == 0 && a1.Frames.Take(lead).All(f => f == null) &&
                  a1.Frames.Skip(lead).Take(250).All(f => f == 1000) && a1.Frames.Skip(lead + 250).All(f => f == null),
                  $"alert 1: 0.75 s silence, 5 s of 1000 Hz, 0.75 s silence ({a1.Frames.Length} frames, whole LDUs)");
            ushort?[] body2 = a2.Frames.Skip(lead).Take(250).ToArray();
            Check(body2.Take(12).All(f => f == 1500) && body2.Skip(13).Take(12).All(f => f == 800) && body2.All(f => f == 1500 || f == 800),
                  "alert 2 alternates 1500 / 800 Hz every 250 ms");
            Check(a3.Frames.Skip(lead).Take(250).Count(f => f == 1000) is >= 120 and <= 130 && a3.Frames.Skip(lead).Take(250).Contains(null),
                  "alert 3 pulses 1000 Hz on and off");
            Check(TonePlan.Alert(4).Frames.Contains((ushort)1000) && TonePlan.Alert(4).Frames.Contains((ushort)1500) &&
                  TonePlan.Alert(5).Frames.Contains((ushort)940) && TonePlan.Alert(5).Frames.Contains((ushort)1640),
                  "alerts 4 and 5 use their own frequency pairs");
            Check(mk.Frames.Count(f => f == 650) == 25 && mk.Frames.Length % 9 == 0, "channel marker is 0.5 s of 650 Hz");
            ushort?[] body6 = TonePlan.Alert(6).Frames.Skip(lead).Take(250).ToArray();
            Check(body6.Take(6).All(f => f == 460) && body6.Skip(7).Take(5).All(f => f == 860) && body6.All(f => f == 460 || f == 860),
                  "alert 6 warbles 460 / 860 Hz every 125 ms");
            short[] tpt = Tui.TalkPermitPcm();
            Check(tpt.Length == 400 && tpt[0] == 0 && tpt.Max() > 6000 && tpt.Max() < 6600, "talk permit tone: 50 ms of 1200 Hz at 20 % with fades");
            TonePlan pg = TonePlan.Page(1153.4, 1185.2);
            Check(pg.Frames.Take(lead).All(f => f == null) && pg.Frames.Skip(lead).Take(50).All(f => f == 1153) &&
                  pg.Frames.Skip(lead + 50).Take(150).All(f => f == 1185) && pg.Frames.Skip(lead + 200).All(f => f == null) && pg.Frames.Length % 9 == 0,
                  "QuickCall II page: tone A 1 s, tone B 3 s, with lead-in and tail");
            bool rejected = false;
            try { TonePlan.Page(100, 1000); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected, "page tones outside 250-3000 Hz are refused");

            // send alert 1 on two talkgroups at once: one clear, one AES
            Codeplug.System offline = new Codeplug.System { Name = "Test", Identity = "TEST", Address = "127.0.0.1", Port = 62031, PeerId = rid, Rid = rid.ToString(), Password = "x" };
            CliPeer headerPeer = new CliPeer(offline, new KeyStore(), verbose: false);
            KeyStore keys = new KeyStore();
            keys.Store(P25Defines.P25_ALGO_AES, TestKeyId, TestKey);
            CaptureSink clearSink = new CaptureSink(headerPeer), secureSink = new CaptureSink(headerPeer);
            P25Transmitter clearTx = new P25Transmitter(clearSink, keys, rid, 504, P25Defines.P25_ALGO_UNENCRYPT, 0);
            P25Transmitter secureTx = new P25Transmitter(secureSink, keys, rid, 500, P25Defines.P25_ALGO_AES, TestKeyId);
            Check(clearTx.Start(out _) && secureTx.Start(out _), "tone transmitters start on both talkgroups");
            int monitored = 0;
            ToneJob job = new ToneJob(a1, new List<(string, P25Transmitter)> { ("Calling", clearTx), ("Secure", secureTx) }, _ => monitored++);
            job.Start(realTime: false);

            int ldus = a1.Frames.Length / 9;
            Check(clearSink.Voice.Count == ldus && secureSink.Voice.Count == ldus && clearSink.Tdus.Count == 5 && secureSink.Tdus.Count == 5 &&
                  clearSink.Voice[0].Stream != secureSink.Voice[0].Stream && monitored == a1.Frames.Length,
                  $"alert sent as two separate calls ({ldus} LDUs each, grant and end TDUs, local monitor fed)");

            byte[] toneWord = new byte[P25VoiceReceiver.ImbeLength];
            MBEToneGenerator.IMBEEncodeSingleTone(1000, toneWord);
            List<byte[]> clearFed = RoundTrip(clearSink, 504, rid, keys, out long m1);
            List<byte[]> secureFed = RoundTrip(secureSink, 500, rid, keys, out long m2);
            Check(m1 == 0 && clearFed.Count == a1.Frames.Length && clearFed.Skip(lead).Take(250).All(c => c.SequenceEqual(toneWord)),
                  "clear alert decodes to the 1000 Hz IMBE tone codeword");
            Check(m2 == 0 && secureFed.Count == a1.Frames.Length && secureFed.Skip(lead).Take(250).All(c => c.SequenceEqual(toneWord)) &&
                  !secureSink.Voice[3].Frame.SequenceEqual(clearSink.Voice[3].Frame),
                  "AES alert is encrypted on the wire and decrypts to the same tone");
        }

        private static List<byte[]> RoundTrip(CaptureSink sent, uint tg, uint src, KeyStore keys, out long muted)
        {
            List<byte[]> fed = new List<byte[]>();
            using P25VoiceReceiver rx = new P25VoiceReceiver(tg, keys);
            rx.Codeword += fed.Add;
            foreach (var (frame, _, stream) in sent.Voice)
                rx.Handle(tg, src, (P25DUID)frame[22], stream, frame);
            rx.Handle(tg, src, P25DUID.TDU, sent.Voice[0].Stream, new byte[24]);
            muted = rx.FramesMuted;
            return fed;
        }

        private static bool SameCodewords(List<byte[]> got, List<byte[]> expected) =>
            got.Count == expected.Count && got.Zip(expected).All(p => p.First.SequenceEqual(p.Second));

        private static double Rms(List<short> samples) =>
            samples.Count == 0 ? 0 : Math.Sqrt(samples.Sum(v => (double)v * v) / samples.Count);

        /// <summary>
        /// Builds the LDU1/LDU2 frames of a call the way a radio would send it: when encrypted, each
        /// superframe is encrypted under the current MI, and its LDU2 carries the next MI (LFSR-cycled).
        /// </summary>
        private static List<(P25DUID, byte[])> BuildCall(List<byte[]> clear, byte alg, ushort kid, byte[] key, bool withHdu)
        {
            bool encrypted = alg != P25Defines.P25_ALGO_UNENCRYPT;
            byte[] mi = (byte[])TestMi.Clone();
            P25Crypto tx = null;
            if (encrypted)
            {
                tx = new P25Crypto();
                tx.SetKey(kid, alg, key);
                tx.Prepare(alg, kid, mi);
            }

            List<(P25DUID, byte[])> frames = new List<(P25DUID, byte[])>();
            for (int sf = 0; sf + 18 <= clear.Count; sf += 18)
            {
                List<byte[]> l1 = clear.Skip(sf).Take(9).Select(c => (byte[])c.Clone()).ToList();
                List<byte[]> l2 = clear.Skip(sf + 9).Take(9).Select(c => (byte[])c.Clone()).ToList();
                if (encrypted)
                {
                    foreach (byte[] c in l1) tx.Process(c, P25DUID.LDU1);
                    foreach (byte[] c in l2) tx.Process(c, P25DUID.LDU2);
                }

                byte[] hduMi = (byte[])mi.Clone();
                byte[] nextMi = (byte[])mi.Clone();
                if (encrypted)
                {
                    P25Crypto.CycleP25Lfsr(nextMi);
                    tx.Prepare(alg, kid, nextMi);
                }

                byte[] f1 = BuildLduFrame(P25VoiceReceiver.Ldu1FirstRecord, l1, P25VoiceReceiver.HduFrameLength);
                if (withHdu && sf == 0)
                {
                    f1[P25VoiceReceiver.HduFlagOffset] = P25Defines.P25_FT_HDU_VALID;
                    f1[P25VoiceReceiver.HduAlgOffset] = alg;
                    f1[P25VoiceReceiver.HduKeyIdOffset] = (byte)(kid >> 8);
                    f1[P25VoiceReceiver.HduKeyIdOffset + 1] = (byte)kid;
                    Array.Copy(hduMi, 0, f1, P25VoiceReceiver.HduMiOffset, P25Defines.P25_MI_LENGTH);
                }

                byte[] f2 = BuildLduFrame(P25VoiceReceiver.Ldu2FirstRecord, l2, 0, alg, kid, encrypted ? nextMi : new byte[P25Defines.P25_MI_LENGTH]);
                frames.Add((P25DUID.LDU1, f1));
                frames.Add((P25DUID.LDU2, f2));
                mi = nextMi;
            }
            return frames;
        }

        /// <summary>
        /// A voiced, speech-like test frame: 140 Hz fundamental with falling harmonics and a slow pitch glide.
        /// </summary>
        private static short[] VoicedFrame(int frame)
        {
            short[] pcm = new short[P25VoiceReceiver.FrameSamples];
            for (int i = 0; i < pcm.Length; i++)
            {
                double t = (frame * pcm.Length + i) / 8000.0;
                double f0 = 140 + 20 * Math.Sin(2 * Math.PI * 0.7 * t);
                double v = 0;
                for (int h = 1; h <= 20; h++)
                    v += Math.Sin(2 * Math.PI * f0 * h * t) / h;
                pcm[i] = (short)Math.Clamp(3000 * v, short.MinValue, short.MaxValue);
            }
            return pcm;
        }

        /// <summary>
        /// Builds a network frame with nine DFSI voice records, laid out as the console's receiver expects.
        /// For LDU2 frames, optionally fills the encryption sync (MI in records 0x6D-0x6F, algorithm and key ID in 0x70).
        /// </summary>
        private static byte[] BuildLduFrame(byte firstRecord, List<byte[]> nine, int length = 0, byte alg = 0, ushort kid = 0, byte[] mi = null)
        {
            byte[] frame = new byte[Math.Max(length, P25VoiceReceiver.PayloadOffset + P25VoiceReceiver.MinimumPayloadLength + 4)];
            for (int n = 0; n < 9; n++)
            {
                int record = P25VoiceReceiver.PayloadOffset + P25VoiceReceiver.RecordOffsets[n];
                frame[record] = (byte)(firstRecord + n);
                Buffer.BlockCopy(nine[n], 0, frame, record + P25VoiceReceiver.ImbeOffsetInRecord[n], P25VoiceReceiver.ImbeLength);
            }

            if (firstRecord == P25VoiceReceiver.Ldu2FirstRecord && alg != 0)
            {
                for (int i = 0; i < P25VoiceReceiver.MiRecordIndexes.Length; i++)
                {
                    int record = P25VoiceReceiver.PayloadOffset + P25VoiceReceiver.RecordOffsets[P25VoiceReceiver.MiRecordIndexes[i]];
                    Buffer.BlockCopy(mi, i * 3, frame, record + 1, 3);
                }
                int algRecord = P25VoiceReceiver.PayloadOffset + P25VoiceReceiver.RecordOffsets[P25VoiceReceiver.AlgRecordIndex];
                frame[algRecord + 1] = alg;
                frame[algRecord + 2] = (byte)(kid >> 8);
                frame[algRecord + 3] = (byte)kid;
            }
            return frame;
        }

        private const string SampleCodeplug = @"
systems:
  - name: ""Test FNE""
    identity: ""TEST CLI""
    address: ""127.0.0.1""
    port: 62031
    peerId: 9000001
    rid: ""9000001""
    password: ""PASSWORD""
    encrypted: false
zones:
  - name: ""Test""
    channels:
      - name: ""Calling""
        system: ""Test FNE""
        tgid: ""504""
        mode: ""p25""
        selectable_encryption: true
      - name: ""Secure""
        system: ""Test FNE""
        tgid: ""500""
        mode: ""p25""
        algo: ""aes""
        keyId: 0x561
";
    } // public static class SelfTest
}
