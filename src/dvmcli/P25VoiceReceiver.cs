// SPDX-License-Identifier: AGPL-3.0-only
/**
* dvmcli - headless Linux proof of concept for the DVM Desktop Dispatch Console.
*
* Follows one P25 talkgroup: tracks calls, pulls the nine IMBE codewords out of each
* LDU1/LDU2 network frame, and decodes them to 8 kHz PCM with libvocoder.
*/

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

        private readonly uint talkgroup;
        private readonly object sync = new object();
        private MBEDecoder decoder;

        private uint streamId;
        private uint sourceId;
        private DateTime callStarted;
        private int callFrames;
        private bool callEncrypted;

        /// <summary>Raised with each decoded 20 ms block of PCM.</summary>
        public event Action<short[]> Audio;

        /// <summary>Raised with a status line for the user (call start/end, warnings).</summary>
        public event Action<string> Status;

        /// <summary>Number of IMBE frames decoded since start.</summary>
        public long FramesDecoded { get; private set; }

        /// <summary>Number of calls seen on the talkgroup.</summary>
        public int CallsSeen { get; private set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="P25VoiceReceiver"/> class.
        /// </summary>
        /// <param name="talkgroup">Talkgroup ID to follow.</param>
        public P25VoiceReceiver(uint talkgroup)
        {
            this.talkgroup = talkgroup;
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
                bool terminator = duid == P25DUID.TDU || duid == P25DUID.TDULC;

                if (terminator)
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

                // HDU parameters ride along with the first LDU1 of a call
                if (duid == P25DUID.LDU1 && frame.Length >= 193 && frame[180] == P25Defines.P25_FT_HDU_VALID)
                {
                    byte algId = frame[181];
                    if (algId != P25Defines.P25_ALGO_UNENCRYPT && !callEncrypted)
                    {
                        callEncrypted = true;
                        ushort keyId = (ushort)((frame[182] << 8) | frame[183]);
                        Status?.Invoke($"call from {sourceId} is encrypted (algorithm 0x{algId:X2}, key 0x{keyId:X4}); " +
                                       "this proof of concept only decodes clear calls, so it will be muted");
                    }
                }

                if (duid != P25DUID.LDU1 && duid != P25DUID.LDU2)
                    return;
                if (callEncrypted)
                    return;

                byte first = duid == P25DUID.LDU1 ? Ldu1FirstRecord : Ldu2FirstRecord;
                if (!TryExtractCodewords(frame, first, out byte[][] codewords))
                {
                    Status?.Invoke($"ignored a malformed {duid} frame ({frame.Length} bytes)");
                    return;
                }

                foreach (byte[] imbe in codewords)
                {
                    short[] samples = new short[FrameSamples];
                    decoder.decode(imbe, samples);
                    FramesDecoded++;
                    callFrames++;
                    Audio?.Invoke(samples);
                }
            }
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

        private void StartCall(uint srcId, uint newStreamId)
        {
            streamId = newStreamId;
            sourceId = srcId;
            callStarted = DateTime.Now;
            callFrames = 0;
            callEncrypted = false;
            CallsSeen++;

            // fresh decoder state per call, as a radio would start
            decoder = new MBEDecoder(MBE_MODE.IMBE_88BIT);

            Status?.Invoke($"CALL START  src {srcId}  tg {talkgroup}  stream {newStreamId}");
        }

        private void EndCall()
        {
            double secs = (DateTime.Now - callStarted).TotalSeconds;
            Status?.Invoke($"CALL END    src {sourceId}  tg {talkgroup}  {secs:F1} s, {callFrames} voice frames" +
                           (callEncrypted ? " (encrypted, muted)" : string.Empty));
            streamId = 0;
        }

        /// <summary>
        /// Releases the decoder.
        /// </summary>
        public void Dispose()
        {
            decoder = null;
        }
    } // public sealed class P25VoiceReceiver
}
