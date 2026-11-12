// SPDX-License-Identifier: AGPL-3.0-only
/**
* dvmcli - headless Linux proof of concept for the DVM Desktop Dispatch Console.
*
* Follows one P25 talkgroup: tracks calls, pulls the nine IMBE codewords out of each
* LDU1/LDU2 network frame, decrypts them when the call is encrypted and the key is
* known, and decodes them to 8 kHz PCM with libvocoder.
*/

using System.Security.Cryptography;
using dvmconsole;
using fnecore.P25;

namespace dvmcli
{
    /// <summary>
    /// Receives P25 voice for a single talkgroup and produces decoded PCM.
    /// </summary>
    public sealed class P25VoiceReceiver : IDisposable
    {
        /// <summary>Length of one IMBE codeword (88 bits).</summary>
        public const int ImbeLength = 11;

        /// <summary>Samples per decoded IMBE frame (20 ms at 8 kHz).</summary>
        public const int FrameSamples = 160;

        /// <summary>Offset of the DFSI voice records in a P25 network frame.</summary>
        public const int PayloadOffset = 24;

        /// <summary>
        /// Start of each of the nine DFSI voice records within the payload.
        /// </summary>
        public static readonly int[] RecordOffsets = { 0, 22, 36, 53, 70, 87, 104, 121, 138 };

        /// <summary>
        /// Position of the IMBE codeword inside each record (record start + this = codeword start).
        /// </summary>
        public static readonly int[] ImbeOffsetInRecord = { 10, 1, 5, 5, 5, 5, 5, 5, 4 };

        /// <summary>Record type of the first LDU1 record (IMBE voice 1); the nine records are consecutive.</summary>
        public const byte Ldu1FirstRecord = 0x62;

        /// <summary>Record type of the first LDU2 record (IMBE voice 10); the nine records are consecutive.</summary>
        public const byte Ldu2FirstRecord = 0x6B;

        /// <summary>Smallest payload that holds all nine records.</summary>
        public const int MinimumPayloadLength = 154;

        /// <summary>
        /// LDU2 records 0x6D, 0x6E and 0x6F (record indexes 2-4) each carry three bytes of the next
        /// message indicator, right after the record type byte.
        /// </summary>
        public static readonly int[] MiRecordIndexes = { 2, 3, 4 };

        /// <summary>LDU2 record 0x70 (index 5) carries the algorithm ID and key ID after the record type byte.</summary>
        public const int AlgRecordIndex = 5;

        /// <summary>Offsets in an LDU1 network frame of the appended HDU parameters.</summary>
        public const int HduFlagOffset = 180, HduAlgOffset = 181, HduKeyIdOffset = 182, HduMiOffset = 184, HduFrameLength = 193;

        private readonly uint talkgroup;
        private readonly KeyStore keys;
        private readonly object sync = new object();
        private readonly P25ReceiveCrypto crypto = new P25ReceiveCrypto();
        private MBEDecoder decoder;

        private uint streamId;
        private uint sourceId;
        private DateTime callStarted;
        private int callFrames;
        private int callMuted;

        private byte algId;
        private ushort keyId;
        private readonly byte[] mi = new byte[P25Defines.P25_MI_LENGTH];
        private bool keyRequested;
        private bool decryptAnnounced;
        private bool keyErrorAnnounced;

        /// <summary>Raised with each decoded 20 ms block of PCM.</summary>
        public event Action<short[]> Audio;

        /// <summary>Raised with a status line for the user (call start/end, warnings).</summary>
        public event Action<string> Status;

        /// <summary>Raised once per call when an encrypted call needs a key the store doesn't have.</summary>
        public event Action<byte, ushort> KeyNeeded;

        /// <summary>
        /// Raised with a copy of each IMBE codeword (decrypted if the call is encrypted) just before it is decoded.
        /// Decoded audio itself is not bit-exact between runs (IMBE synthesizes unvoiced sound from random noise),
        /// so this is the deterministic point to test against.
        /// </summary>
        public event Action<byte[]> Codeword;

        /// <summary>Number of IMBE frames decoded since start.</summary>
        public long FramesDecoded { get; private set; }

        /// <summary>Number of IMBE frames muted (encrypted without a usable key, or before sync).</summary>
        public long FramesMuted { get; private set; }

        /// <summary>Number of calls seen on the talkgroup.</summary>
        public int CallsSeen { get; private set; }

        /// <summary>True while a call is in progress.</summary>
        public bool InCall => streamId != 0;

        /// <summary>Source radio of the current (or last) call.</summary>
        public uint CurrentSource => sourceId;

        /// <summary>Algorithm of the current call (0 = not known yet, 0x80 = clear).</summary>
        public byte CurrentAlgorithm => algId;

        /// <summary>Key ID of the current call.</summary>
        public ushort CurrentKeyId => keyId;

        /// <summary>When the current call started.</summary>
        public DateTime CallStartedAt => callStarted;

        /// <summary>True if any voice in the current call has been muted (no key, or before sync).</summary>
        public bool CallHasMuted => callMuted > 0;

        /// <summary>Raised when a call starts (source radio ID).</summary>
        public event Action<uint> CallStarted;

        /// <summary>Raised when a call ends: source, duration in seconds, frames played, frames muted, crypto description.</summary>
        public event Action<uint, double, int, int, string> CallEnded;

        /// <summary>
        /// Initializes a new instance of the <see cref="P25VoiceReceiver"/> class.
        /// </summary>
        /// <param name="talkgroup">Talkgroup ID to follow.</param>
        /// <param name="keys">Traffic key store (may be shared with the FNE peer).</param>
        public P25VoiceReceiver(uint talkgroup, KeyStore keys = null)
        {
            this.talkgroup = talkgroup;
            this.keys = keys ?? new KeyStore();
            decoder = new MBEDecoder(MBE_MODE.IMBE_88BIT);
        }

        /// <summary>
        /// Handles one P25 network frame.
        /// </summary>
        /// <param name="dstId">Destination (talkgroup) ID.</param>
        /// <param name="srcId">Source radio ID.</param>
        /// <param name="duid">P25 data unit ID.</param>
        /// <param name="streamId">Network stream ID of the call.</param>
        /// <param name="frame">Complete network frame (header + DFSI records).</param>
        public void Handle(uint dstId, uint srcId, P25DUID duid, uint streamId, byte[] frame)
        {
            if (dstId != talkgroup)
                return;
            if (duid == P25DUID.TSDU || duid == P25DUID.PDU)
                return;

            lock (sync)
            {
                if (duid == P25DUID.TDU || duid == P25DUID.TDULC)
                {
                    if (this.streamId != 0 && streamId == this.streamId)
                        EndCall();
                    return;
                }

                if (streamId != this.streamId)
                {
                    if (this.streamId != 0)
                        EndCall();
                    StartCall(srcId, streamId);
                }

                // HDU parameters ride along with an LDU1 at the start of a call
                if (duid == P25DUID.LDU1 && frame.Length >= HduFrameLength && frame[HduFlagOffset] == P25Defines.P25_FT_HDU_VALID)
                {
                    algId = frame[HduAlgOffset];
                    keyId = (ushort)((frame[HduKeyIdOffset] << 8) | frame[HduKeyIdOffset + 1]);
                    Array.Copy(frame, HduMiOffset, mi, 0, P25Defines.P25_MI_LENGTH);
                    Prepare();
                }

                if (duid != P25DUID.LDU1 && duid != P25DUID.LDU2)
                    return;

                byte first = duid == P25DUID.LDU1 ? Ldu1FirstRecord : Ldu2FirstRecord;
                if (!TryExtractCodewords(frame, first, out byte[][] codewords))
                {
                    Status?.Invoke($"ignored a malformed {duid} frame ({frame.Length} bytes)");
                    return;
                }

                foreach (byte[] imbe in codewords)
                {
                    // decrypts in place; false = not ready (no key, or no sync yet on a late entry)
                    if (!crypto.Process(imbe, duid))
                    {
                        FramesMuted++;
                        callMuted++;
                        continue;
                    }

                    Codeword?.Invoke((byte[])imbe.Clone());

                    short[] samples = new short[FrameSamples];
                    decoder.decode(imbe, samples);
                    FramesDecoded++;
                    callFrames++;
                    Audio?.Invoke(samples);
                }

                // LDU2 carries the encryption sync for the next superframe
                if (duid == P25DUID.LDU2)
                {
                    ReadEncryptionSync(frame, out byte nextAlg, out ushort nextKeyId, out byte[] nextMi);

                    // 0x00 is not a P25 voice algorithm: keep what the HDU said, or treat the call as clear
                    if (nextAlg == 0)
                    {
                        nextAlg = algId != 0 ? algId : P25Defines.P25_ALGO_UNENCRYPT;
                        nextKeyId = keyId;
                    }

                    algId = nextAlg;
                    keyId = nextKeyId;
                    Array.Copy(nextMi, mi, P25Defines.P25_MI_LENGTH);
                    Prepare();
                }
            }
        }

        /// <summary>
        /// Reads the algorithm ID, key ID and next message indicator from an LDU2 network frame.
        /// </summary>
        public static void ReadEncryptionSync(byte[] frame, out byte alg, out ushort kid, out byte[] nextMi)
        {
            nextMi = new byte[P25Defines.P25_MI_LENGTH];
            for (int i = 0; i < MiRecordIndexes.Length; i++)
            {
                int record = PayloadOffset + RecordOffsets[MiRecordIndexes[i]];
                Buffer.BlockCopy(frame, record + 1, nextMi, i * 3, 3);
            }

            int algRecord = PayloadOffset + RecordOffsets[AlgRecordIndex];
            alg = frame[algRecord + 1];
            kid = (ushort)((frame[algRecord + 2] << 8) | frame[algRecord + 3]);
        }

        /// <summary>
        /// Extracts the nine IMBE codewords from a P25 LDU network frame.
        /// </summary>
        /// <param name="frame">Complete network frame.</param>
        /// <param name="firstRecord">Expected type of the first record (0x62 for LDU1, 0x6B for LDU2).</param>
        /// <param name="codewords">Nine 11-byte codewords, in order.</param>
        /// <returns>True if all nine records were present and well formed.</returns>
        public static bool TryExtractCodewords(byte[] frame, byte firstRecord, out byte[][] codewords)
        {
            codewords = null;
            if (frame == null || frame.Length < PayloadOffset + MinimumPayloadLength)
                return false;

            byte[][] result = new byte[RecordOffsets.Length][];
            for (int n = 0; n < RecordOffsets.Length; n++)
            {
                int record = PayloadOffset + RecordOffsets[n];
                if (frame[record] != (byte)(firstRecord + n))
                    return false;

                result[n] = new byte[ImbeLength];
                Buffer.BlockCopy(frame, record + ImbeOffsetInRecord[n], result[n], 0, ImbeLength);
            }

            codewords = result;
            return true;
        }

        /// <summary>
        /// Sets up receive crypto for the current algorithm, key ID and MI.
        /// </summary>
        private void Prepare()
        {
            if (algId == 0)
                return; // not known yet (late entry before the first LDU2)

            bool encrypted = algId != P25Defines.P25_ALGO_UNENCRYPT;
            byte[] key = null;

            if (encrypted && !keys.TryGet(algId, keyId, out key))
            {
                crypto.Reset();
                if (!keyRequested)
                {
                    keyRequested = true;
                    Status?.Invoke($"call from {sourceId} is {KeyStore.AlgorithmName(algId)} key 0x{keyId:X4}; no key yet, " +
                                   "requesting it from the FNE (muted until it arrives)");
                    KeyNeeded?.Invoke(algId, keyId);
                }
                return;
            }

            try
            {
                crypto.Prepare(algId, keyId, mi, key);
                if (encrypted && !decryptAnnounced)
                {
                    decryptAnnounced = true;
                    Status?.Invoke($"decrypting {KeyStore.AlgorithmName(algId)} key 0x{keyId:X4}");
                }
            }
            catch (CryptographicException)
            {
                crypto.Reset();
                if (!keyErrorAnnounced)
                {
                    keyErrorAnnounced = true;
                    Status?.Invoke($"the {KeyStore.AlgorithmName(algId)} key 0x{keyId:X4} could not be used; call muted");
                }
            }
            finally
            {
                if (key != null)
                    CryptographicOperations.ZeroMemory(key);
            }
        }

        private void StartCall(uint srcId, uint newStreamId)
        {
            streamId = newStreamId;
            sourceId = srcId;
            callStarted = DateTime.Now;
            callFrames = 0;
            callMuted = 0;
            CallsSeen++;

            crypto.Reset();
            algId = 0;
            keyId = 0;
            Array.Clear(mi);
            keyRequested = false;
            decryptAnnounced = false;
            keyErrorAnnounced = false;

            // fresh decoder state per call, as a radio would start
            decoder = new MBEDecoder(MBE_MODE.IMBE_88BIT);

            Status?.Invoke($"CALL START  src {srcId}  tg {talkgroup}  stream {newStreamId}");
            CallStarted?.Invoke(srcId);
        }

        private void EndCall()
        {
            double secs = (DateTime.Now - callStarted).TotalSeconds;
            string crypt = algId == 0 || algId == P25Defines.P25_ALGO_UNENCRYPT ? "clear" : $"{KeyStore.AlgorithmName(algId)} key 0x{keyId:X4}";
            string muted = callMuted > 0 ? $", {callMuted} muted" : string.Empty;
            Status?.Invoke($"CALL END    src {sourceId}  tg {talkgroup}  {secs:F1} s, {crypt}, {callFrames} voice frames played{muted}");
            streamId = 0;
            CallEnded?.Invoke(sourceId, secs, callFrames, callMuted, crypt);
        }

        /// <summary>
        /// Releases crypto state and the decoder.
        /// </summary>
        public void Dispose()
        {
            crypto.Reset();
            decoder = null;
        }
    } // public sealed class P25VoiceReceiver
}
