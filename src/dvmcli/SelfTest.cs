// SPDX-License-Identifier: AGPL-3.0-only
/**
* dvmcli - headless Linux proof of concept for the DVM Desktop Dispatch Console.
*
* Offline self-test, run by CI on Linux ARM64. It checks that libvocoder loads, that
* IMBE encode/decode round-trips, that the receiver pulls the nine codewords out of an
* LDU frame and handles calls, and that codeplugs and WAV files are read/written correctly.
* It does not touch the network.
*/

using System.Runtime.InteropServices;
using dvmconsole;
using fnecore.P25;

namespace dvmcli
{
    public static class SelfTest
    {
        private static int failures;

        private static void Check(bool ok, string what)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}: {what}");
            if (!ok)
                failures++;
        }

        public static int Run()
        {
            failures = 0;
            Console.WriteLine($"dvmcli self-test on {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture})");

            // 1. libvocoder loads and round-trips voice-like audio
            MBEEncoder encoder = new MBEEncoder(MBE_MODE.IMBE_88BIT);
            MBEDecoder decoder = new MBEDecoder(MBE_MODE.IMBE_88BIT);
            Check(true, "libvocoder loaded (encoder and decoder created)");

            const int frames = 90;
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

            // 3. call handling: one clear call on the followed talkgroup, traffic on others ignored
            using (P25VoiceReceiver rx = new P25VoiceReceiver(504))
            {
                int blocks = 0;
                double rxEnergy = 0;
                List<string> status = new List<string>();
                rx.Audio += s => { blocks++; foreach (short v in s) rxEnergy += (double)v * v; };
                rx.Status += status.Add;

                rx.Handle(999, 1234, P25DUID.LDU1, 77, ldu1);           // other talkgroup
                Check(rx.CallsSeen == 0 && blocks == 0, "traffic on another talkgroup is ignored");

                for (int i = 0; i + 18 <= codewords.Count; i += 18)
                {
                    rx.Handle(504, 1234, P25DUID.LDU1, 42, BuildLduFrame(P25VoiceReceiver.Ldu1FirstRecord, codewords.Skip(i).Take(9).ToList()));
                    rx.Handle(504, 1234, P25DUID.LDU2, 42, BuildLduFrame(P25VoiceReceiver.Ldu2FirstRecord, codewords.Skip(i + 9).Take(9).ToList()));
                }
                rx.Handle(504, 1234, P25DUID.TDU, 42, new byte[24]);

                int expected = (codewords.Count / 18) * 18;
                Check(rx.CallsSeen == 1, "one call start detected");
                Check(blocks == expected && rx.FramesDecoded == expected, $"{expected} voice frames decoded ({blocks} audio blocks)");
                Check(Math.Sqrt(rxEnergy / Math.Max(1, blocks * 160)) > 100, "decoded call audio is not silent");
                Check(status.Any(s => s.StartsWith("CALL START")) && status.Any(s => s.StartsWith("CALL END")), "call start and end reported");
            }

            // 4. encrypted call (HDU parameters in the first LDU1) is detected and muted
            using (P25VoiceReceiver rx = new P25VoiceReceiver(500))
            {
                int blocks = 0;
                List<string> status = new List<string>();
                rx.Audio += _ => blocks++;
                rx.Status += status.Add;

                byte[] enc = BuildLduFrame(P25VoiceReceiver.Ldu1FirstRecord, codewords.Take(9).ToList(), 193);
                enc[180] = P25Defines.P25_FT_HDU_VALID;
                enc[181] = 0x84;                                      // AES-256
                enc[182] = 0x05; enc[183] = 0x61;                     // key 0x0561
                rx.Handle(500, 1234, P25DUID.LDU1, 9, enc);
                rx.Handle(500, 1234, P25DUID.LDU2, 9, ldu2);
                Check(blocks == 0 && status.Any(s => s.Contains("encrypted") && s.Contains("0x0561")), "encrypted call is detected and muted");
            }

            // 5. codeplug parsing (same settings as the console)
            string tmp = Path.Combine(Path.GetTempPath(), $"dvmcli-selftest-{Environment.ProcessId}");
            Directory.CreateDirectory(tmp);
            try
            {
                string cpPath = Path.Combine(tmp, "codeplug.yml");
                File.WriteAllText(cpPath, SampleCodeplug);
                Codeplug cp = Program.LoadCodeplug(cpPath);
                Codeplug.System sys = cp.Systems.FirstOrDefault();
                Codeplug.Channel ch = cp.Zones?.FirstOrDefault()?.Channels?.FirstOrDefault();
                Check(sys != null && sys.Name == "Test FNE" && sys.PeerId == 9000001 && sys.Port == 62031, "codeplug system parsed");
                Check(ch != null && ch.Tgid == "504" && ch.GetChannelMode() == Codeplug.ChannelMode.P25 && ch.SelectableEncryption, "codeplug channel parsed (including dev-only keys)");

                // 6. WAV writer
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
        /// </summary>
        private static byte[] BuildLduFrame(byte firstRecord, List<byte[]> nine, int length = 0)
        {
            byte[] frame = new byte[Math.Max(length, P25VoiceReceiver.PayloadOffset + P25VoiceReceiver.MinimumPayloadLength + 4)];
            for (int n = 0; n < 9; n++)
            {
                int record = P25VoiceReceiver.PayloadOffset + P25VoiceReceiver.RecordOffsets[n];
                frame[record] = (byte)(firstRecord + n);
                Buffer.BlockCopy(nine[n], 0, frame, record + P25VoiceReceiver.ImbeOffsetInRecord[n], P25VoiceReceiver.ImbeLength);
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
";
    } // public static class SelfTest
}
