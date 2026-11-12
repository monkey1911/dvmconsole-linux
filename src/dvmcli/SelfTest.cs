// SPDX-License-Identifier: AGPL-3.0-only
/**
* dvmcli - headless Linux proof of concept for the DVM Desktop Dispatch Console.
*
* Offline self-test, run by CI on Linux ARM64. It checks that libvocoder loads, that
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
