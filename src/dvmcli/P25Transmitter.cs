// SPDX-License-Identifier: AGPL-3.0-only
/**
* dvmcli - headless Linux proof of concept for the DVM Desktop Dispatch Console.
*
* P25 transmit for one talkgroup: grant demand, IMBE-encoded voice in LDU1/LDU2
* superframes (encrypted when the channel is secure), and call-end TDUs.
*/

using System.Security.Cryptography;
using dvmconsole;
using fnecore;
using fnecore.P25;

namespace dvmcli
{
    /// <summary>
    /// Sends P25 traffic to the FNE; implemented by the FNE peer.
    /// </summary>
    public interface IP25TrafficSink : IP25HeaderFactory
    {
        /// <summary>Sends one LDU1/LDU2 frame.</summary>
        void SendP25Voice(byte[] frame, ushort packetSequence, uint streamId);

        /// <summary>Sends a TDU; a grant demand starts a call, plain TDUs end it.</summary>
        void SendP25Tdu(uint srcId, uint dstId, uint streamId, bool grantDemand);
    }

    /// <summary>
    /// Transmits voice on one P25 talkgroup.
    /// </summary>
    public sealed class P25Transmitter : IDisposable
    {
        /// <summary>Highest RTP sequence used for voice (the FNE reserves 65535 for call end).</summary>
        private const ushort MaxVoiceSequence = Constants.RtpCallEndSeq - 1;

        private readonly IP25TrafficSink sink;
        private readonly KeyStore keys;
        private readonly object sync = new object();
        private readonly byte[][] superframe = new byte[18][];

        private MBEEncoder encoder;
        private P25Crypto crypto;
        private byte[] txKey;     // P25Crypto keeps a reference to this array; wiped when the call ends
        private byte[] mi;
        private int n;
        private bool firstPacket;
        private ushort packetSequence;

        /// <summary>Source radio ID (the console RID).</summary>
        public uint SourceId { get; }

        /// <summary>Talkgroup.</summary>
        public uint Talkgroup { get; }

        /// <summary>Algorithm (0x80 = clear).</summary>
        public byte AlgorithmId { get; }

        /// <summary>Key ID (0 when clear).</summary>
        public ushort KeyId { get; }

        /// <summary>True between Start and Stop.</summary>
        public bool IsTransmitting { get; private set; }

        /// <summary>Stream ID of the current call.</summary>
        public uint StreamId { get; private set; }

        /// <summary>When the current call started.</summary>
        public DateTime StartedAt { get; private set; }

        /// <summary>Voice frames (20 ms each) sent in the current call.</summary>
        public int FramesSent { get; private set; }

        /// <summary>True when transmitting encrypted.</summary>
        public bool Encrypted => AlgorithmId != P25Defines.P25_ALGO_UNENCRYPT;

        /// <summary>
        /// Initializes a new instance of the <see cref="P25Transmitter"/> class.
        /// </summary>
        public P25Transmitter(IP25TrafficSink sink, KeyStore keys, uint sourceId, uint talkgroup, byte algorithmId, ushort keyId)
        {
            this.sink = sink;
            this.keys = keys;
            SourceId = sourceId;
            Talkgroup = talkgroup;
            AlgorithmId = algorithmId == 0 ? P25Defines.P25_ALGO_UNENCRYPT : algorithmId;
            KeyId = keyId;
        }

        /// <summary>
        /// Starts a call (sends the grant demand). Refuses to transmit a secure channel without its key.
        /// </summary>
        public bool Start(out string error)
        {
            lock (sync)
            {
                error = null;
                if (IsTransmitting)
                    return true;
                if (SourceId == 0)
                {
                    error = "the system has no valid 'rid' in the codeplug";
                    return false;
                }

                if (Encrypted)
                {
                    if (!KeyStore.IsSupported(AlgorithmId))
                    {
                        error = $"unsupported algorithm 0x{AlgorithmId:X2}";
                        return false;
                    }
                    if (!keys.TryGet(AlgorithmId, KeyId, out byte[] key))
                    {
                        error = $"no {KeyStore.AlgorithmName(AlgorithmId)} key 0x{KeyId:X4} yet; not transmitting in the clear on a secure channel";
                        return false;
                    }

                    // fresh random MI per call
                    mi = new byte[P25Defines.P25_MI_LENGTH];
                    do { RandomNumberGenerator.Fill(mi); } while (mi.All(b => b == 0));

                    // P25Crypto stores a reference to the key (it does not copy it) and regenerates the
                    // keystream from it on every Prepare, so the copy must stay intact until the call ends
                    txKey = key;
                    crypto = new P25Crypto();
                    crypto.SetKey(KeyId, AlgorithmId, txKey);
                    if (!crypto.Prepare(AlgorithmId, KeyId, mi))
                    {
                        crypto = null;
                        CryptographicOperations.ZeroMemory(txKey);
                        txKey = null;
                        error = $"could not use the {KeyStore.AlgorithmName(AlgorithmId)} key 0x{KeyId:X4}";
                        return false;
                    }
                }
                else
                {
                    crypto = null;
                    mi = new byte[P25Defines.P25_MI_LENGTH];
                }

                encoder = new MBEEncoder(MBE_MODE.IMBE_88BIT);
                StreamId = NewStreamId();
                n = 0;
                firstPacket = true;
                packetSequence = 0;
                FramesSent = 0;
                StartedAt = DateTime.Now;
                IsTransmitting = true;

                sink.SendP25Tdu(SourceId, Talkgroup, StreamId, grantDemand: true);
                return true;
            }
        }

        /// <summary>
        /// Encodes and queues one 20 ms block (160 samples at 8 kHz); sends an LDU every nine blocks.
        /// </summary>
        public void PushAudio(short[] samples) => PushFrame(samples, null);

        /// <summary>
        /// Queues one 20 ms frame: a generated tone (an IMBE tone codeword from the console's lookup table, as the
        /// Windows console sends alert tones), or encoded audio when <paramref name="toneHz"/> is null.
        /// </summary>
        public void PushFrame(short[] samples, ushort? toneHz)
        {
            lock (sync)
            {
                if (!IsTransmitting)
                    return;

                byte[] imbe = new byte[P25VoiceReceiver.ImbeLength];
                if (toneHz.HasValue)
                    MBEToneGenerator.IMBEEncodeSingleTone(toneHz.Value, imbe);
                else
                {
                    if (samples == null || samples.Length != P25VoiceReceiver.FrameSamples)
                        throw new ArgumentException("expected 160 samples");
                    encoder.encode(samples, imbe);
                }

                if (crypto != null)
                {
                    crypto.Process(imbe, n < 9 ? P25DUID.LDU1 : P25DUID.LDU2);

                    // after the last block of the superframe, move to the next MI; this LDU2 carries it
                    if (n == 17)
                    {
                        P25Crypto.CycleP25Lfsr(mi);
                        crypto.Prepare(AlgorithmId, KeyId, mi);
                    }
                }

                superframe[n] = imbe;
                FramesSent++;

                if (n == 8)
                    Send(P25FrameBuilder.BuildLdu1(sink, superframe.Take(9).ToArray(), SourceId, Talkgroup, CurrentCryptoParams()));
                else if (n == 17)
                    Send(P25FrameBuilder.BuildLdu2(sink, superframe.Skip(9).Take(9).ToArray(), SourceId, Talkgroup, CurrentCryptoParams()));

                n = (n + 1) % 18;
            }
        }

        /// <summary>
        /// Ends the call (sends the terminator TDUs). A partly filled superframe is dropped, as the console does.
        /// </summary>
        public void Stop()
        {
            lock (sync)
            {
                if (!IsTransmitting)
                    return;
                IsTransmitting = false;

                sink.SendP25Tdu(SourceId, Talkgroup, StreamId, grantDemand: false);

                crypto?.Reset();
                crypto = null;
                if (txKey != null)
                {
                    CryptographicOperations.ZeroMemory(txKey);
                    txKey = null;
                }
                if (mi != null)
                    CryptographicOperations.ZeroMemory(mi);
                encoder = null;
                StreamId = 0;
            }
        }

        private CryptoParams CurrentCryptoParams()
        {
            CryptoParams p = new CryptoParams { AlgoId = AlgorithmId, KeyId = KeyId };
            if (crypto != null)
                Array.Copy(mi, p.MI, P25Defines.P25_MI_LENGTH);
            return p;
        }

        private void Send(byte[] frame)
        {
            if (firstPacket)
            {
                firstPacket = false;
                packetSequence = 0;
            }
            else
                packetSequence = packetSequence >= MaxVoiceSequence ? (ushort)0 : (ushort)(packetSequence + 1);

            sink.SendP25Voice(frame, packetSequence, StreamId);
        }

        private static uint NewStreamId()
        {
            Span<byte> b = stackalloc byte[4];
            uint id;
            do { RandomNumberGenerator.Fill(b); id = BitConverter.ToUInt32(b); } while (id == 0);
            return id;
        }

        /// <summary>
        /// Ends any call in progress.
        /// </summary>
        public void Dispose() => Stop();
    } // public sealed class P25Transmitter
}
