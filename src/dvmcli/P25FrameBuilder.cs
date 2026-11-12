// SPDX-License-Identifier: AGPL-3.0-only
/**
* dvmcli - headless Linux proof of concept for the DVM Desktop Dispatch Console.
*
* Builds P25 LDU1/LDU2 network frames for transmit: a header from fnecore, then the nine
* DFSI voice records in the layout P25VoiceReceiver reads (and dvmconsole uses).
*/

using fnecore;
using fnecore.P25;

namespace dvmcli
{
    /// <summary>
    /// Creates network frame headers; implemented by the FNE peer (wraps fnecore's CreateP25MessageHdr).
    /// </summary>
    public interface IP25HeaderFactory
    {
        /// <summary>Returns a new frame buffer with the P25 network header (and HDU parameters) filled in.</summary>
        byte[] CreateP25Header(P25DUID duid, RemoteCallData callData, CryptoParams cryptoParams);
    }

    /// <summary>
    /// Builds P25 LDU1/LDU2 network frames.
    /// </summary>
    public static class P25FrameBuilder
    {
        /// <summary>Size of the frame buffer fnecore uses for P25 voice.</summary>
        public const int FrameSize = 200;

        /// <summary>Length of each of the nine DFSI voice records.</summary>
        public static readonly int[] RecordLengths = { 22, 14, 17, 17, 17, 17, 17, 17, 16 };

        private const byte ServiceOptionsPriority4 = 0x04;
        private const byte ServiceOptionsProtected = 0x40;

        /// <summary>
        /// Builds an LDU1 frame (voice 1-9 plus link control).
        /// </summary>
        public static byte[] BuildLdu1(IP25HeaderFactory headers, byte[][] nine, uint srcId, uint dstId, CryptoParams crypto)
        {
            CheckCodewords(nine);
            bool encrypted = crypto.AlgoId != P25Defines.P25_ALGO_UNENCRYPT;
            byte[] frame = headers.CreateP25Header(P25DUID.LDU1, CallData(srcId, dstId), crypto);

            for (int n = 0; n < 9; n++)
            {
                int record = WriteRecord(frame, n, (byte)(P25VoiceReceiver.Ldu1FirstRecord + n), nine[n]);
                switch (n)
                {
                    case 2: // link control: LCO, MFId, service options
                        frame[record + 1] = P25Defines.LC_GROUP;
                        frame[record + 2] = 0;
                        frame[record + 3] = (byte)(ServiceOptionsPriority4 | (encrypted ? ServiceOptionsProtected : 0));
                        break;
                    case 3: // talkgroup
                        Write24(frame, record + 1, dstId);
                        break;
                    case 4: // source
                        Write24(frame, record + 1, srcId);
                        break;
                }
            }

            frame[23] = (byte)(P25VoiceReceiver.PayloadOffset + P25VoiceReceiver.MinimumPayloadLength);
            return frame;
        }

        /// <summary>
        /// Builds an LDU2 frame (voice 10-18 plus encryption sync for the next superframe).
        /// </summary>
        public static byte[] BuildLdu2(IP25HeaderFactory headers, byte[][] nine, uint srcId, uint dstId, CryptoParams crypto)
        {
            CheckCodewords(nine);
            byte[] frame = headers.CreateP25Header(P25DUID.LDU2, CallData(srcId, dstId), crypto);

            for (int n = 0; n < 9; n++)
            {
                int record = WriteRecord(frame, n, (byte)(P25VoiceReceiver.Ldu2FirstRecord + n), nine[n]);
                int miIndex = Array.IndexOf(P25VoiceReceiver.MiRecordIndexes, n);
                if (miIndex >= 0)
                    Buffer.BlockCopy(crypto.MI, miIndex * 3, frame, record + 1, 3);
                else if (n == P25VoiceReceiver.AlgRecordIndex)
                {
                    frame[record + 1] = crypto.AlgoId;
                    frame[record + 2] = (byte)(crypto.KeyId >> 8);
                    frame[record + 3] = (byte)crypto.KeyId;
                }
            }

            frame[23] = (byte)(P25VoiceReceiver.PayloadOffset + P25VoiceReceiver.MinimumPayloadLength);
            return frame;
        }

        private static RemoteCallData CallData(uint srcId, uint dstId) =>
            new RemoteCallData { SrcId = srcId, DstId = dstId, LCO = P25Defines.LC_GROUP };

        private static int WriteRecord(byte[] frame, int n, byte type, byte[] imbe)
        {
            int record = P25VoiceReceiver.PayloadOffset + P25VoiceReceiver.RecordOffsets[n];
            Array.Clear(frame, record, RecordLengths[n]);
            frame[record] = type;
            Buffer.BlockCopy(imbe, 0, frame, record + P25VoiceReceiver.ImbeOffsetInRecord[n], P25VoiceReceiver.ImbeLength);
            return record;
        }

        private static void Write24(byte[] frame, int offset, uint value)
        {
            frame[offset] = (byte)(value >> 16);
            frame[offset + 1] = (byte)(value >> 8);
            frame[offset + 2] = (byte)value;
        }

        private static void CheckCodewords(byte[][] nine)
        {
            if (nine == null || nine.Length != 9 || nine.Any(c => c == null || c.Length != P25VoiceReceiver.ImbeLength))
                throw new ArgumentException("an LDU needs nine 11-byte IMBE codewords");
        }
    } // public static class P25FrameBuilder
}
