// SPDX-License-Identifier: AGPL-3.0-only
/**
* dvmcli - headless Linux proof of concept for the DVM Desktop Dispatch Console.
*
* Connects to an FNE master as a console peer (using fnecore) and forwards
* P25 traffic to a P25VoiceReceiver. DMR, NXDN and analog traffic are ignored.
*/

using System.Net;
using dvmconsole;
using fnecore;
using fnecore.DMR;
using fnecore.NXDN;
using fnecore.P25;
using fnecore.P25.KMM;

namespace dvmcli
{
    /// <summary>
    /// FNE console peer for the CLI.
    /// </summary>
    public sealed class CliPeer : fnecore.FneSystemBase, IP25TrafficSink
    {
        private readonly KeyStore keys;
        private readonly bool verbose;
        private readonly uint consoleRid;
        private readonly object keyLock = new object();
        private readonly List<(byte alg, ushort kid)> pendingKeyRequests = new List<(byte, ushort)>();

        /// <summary>Raised with a status line for the user.</summary>
        public event Action<string> Status;

        /// <summary>Raised on the network thread for every P25 frame from the FNE (all talkgroups).</summary>
        public event Action<P25DataReceivedEvent> P25Frame;

        /// <summary>True once the master has accepted our login.</summary>
        public bool Connected { get; private set; }

        /// <summary>The console RID from the codeplug (0 if missing); used as the source of key requests and transmissions.</summary>
        public uint ConsoleRid => consoleRid;

        /// <summary>
        /// Initializes a new instance of the <see cref="CliPeer"/> class.
        /// </summary>
        public CliPeer(Codeplug.System system, KeyStore keys, bool verbose)
            : base(CreatePeer(system), verbose ? LogLevel.DEBUG : LogLevel.INFO)
        {
            this.keys = keys;
            this.verbose = verbose;

            // key requests identify the requesting radio; the console uses its configured RID
            uint.TryParse(system.Rid, out consoleRid);

            fne.Logger = (LogLevel level, string message) =>
            {
                // fnecore levels are flags (INFO 0, WARNING 1, ERROR 2, DEBUG 4, FATAL 8): show all but DEBUG unless verbose
                if (level != LogLevel.DEBUG || verbose)
                    Status?.Invoke($"fne {level}: {message}");
            };
        }

        /// <summary>
        /// Builds the fnecore peer from a codeplug system entry.
        /// </summary>
        private static FnePeer CreatePeer(Codeplug.System system)
        {
            if (string.IsNullOrWhiteSpace(system.Address))
                throw new ArgumentException($"system '{system.Name}' has no address");

            IPAddress address;
            if (!IPAddress.TryParse(system.Address, out address))
            {
                IPAddress[] found = Dns.GetHostAddresses(system.Address);
                address = found.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? found.FirstOrDefault()
                    ?? throw new ArgumentException($"could not resolve '{system.Address}'");
            }

            string networkKey = system.Encrypted ? system.PresharedKey : null;
            FnePeer peer = new FnePeer("DVMCLI", system.PeerId, new IPEndPoint(address, system.Port), networkKey);
            if (!string.IsNullOrWhiteSpace(system.KmfPresharedKey))
                peer.SetKMFPresharedKey(system.KmfPresharedKey);

            string identity = string.IsNullOrWhiteSpace(system.Identity) ? system.PeerId.ToString() : system.Identity;

            peer.Passphrase = system.Password;
            peer.Information = new PeerInformation
            {
                Details = new PeerDetails
                {
                    ConventionalPeer = true,
                    PeerClass = PeerConnectionClass.PEER_CONN_CLASS_CONSOLE,
                    Software = "DVMCLI_R00A01",
                    Identity = identity
                }
            };
            peer.PingTime = 5;

            return peer;
        }

        /*
        ** P25
        */

        /// <inheritdoc/>
        protected override bool P25DataValidate(uint peerId, uint srcId, uint dstId, CallType callType, P25DUID duid, FrameType frameType, uint streamId, byte[] message) => true;

        /// <inheritdoc/>
        protected override void P25DataPreprocess(object sender, P25DataReceivedEvent e) { }

        /// <inheritdoc/>
        protected override void P25DataReceived(object sender, P25DataReceivedEvent e)
        {
            try
            {
                if (verbose)
                    Status?.Invoke($"p25 {e.DUID} src {e.SrcId} tg {e.DstId} stream {e.StreamId} ({e.Data.Length} bytes)");
                P25Frame?.Invoke(e);
            }
            catch (Exception ex)
            {
                Status?.Invoke($"error handling P25 frame: {ex.Message}");
            }
        }

        /*
        ** Peer / keys
        */

        /// <inheritdoc/>
        protected override bool PeerIgnored(uint peerId, uint srcId, uint dstId, byte slot, CallType callType, FrameType frameType, DMRDataType dataType, uint streamId) => false;

        /// <inheritdoc/>
        protected override void PeerConnected(object sender, PeerConnectedEvent e)
        {
            Connected = true;
            Status?.Invoke($"connected to FNE as peer {e.PeerId}");

            // send any key requests made before the login completed
            List<(byte alg, ushort kid)> pending;
            lock (keyLock)
            {
                pending = new List<(byte, ushort)>(pendingKeyRequests);
                pendingKeyRequests.Clear();
            }
            foreach ((byte alg, ushort kid) in pending)
                RequestKey(alg, kid);
        }

        /// <summary>
        /// Asks the FNE for a traffic key. Requests made before login are queued and sent on connect.
        /// </summary>
        public void RequestKey(byte algId, ushort keyId)
        {
            if (!KeyStore.IsSupported(algId))
            {
                Status?.Invoke($"cannot request a key for unsupported algorithm 0x{algId:X2}");
                return;
            }

            if (!Connected)
            {
                lock (keyLock)
                {
                    if (!pendingKeyRequests.Contains((algId, keyId)))
                        pendingKeyRequests.Add((algId, keyId));
                }
                return;
            }

            if (consoleRid == 0)
            {
                Status?.Invoke($"cannot request key 0x{keyId:X4}: the system has no valid 'rid' in the codeplug (the FNE needs it to answer)");
                return;
            }

            try
            {
                fne.SendMasterKeyRequest(algId, keyId, consoleRid);
                Status?.Invoke($"requested {KeyStore.AlgorithmName(algId)} key 0x{keyId:X4} from the FNE (as RID {consoleRid})");
            }
            catch (Exception ex)
            {
                Status?.Invoke($"key request for 0x{keyId:X4} failed: {ex.Message}");
            }
        }

        /// <inheritdoc/>
        protected override void KeyResponse(object sender, KeyResponseEvent e)
        {
            KeysetItem keyset = e?.KmmKey?.KeysetItem;
            if (keyset?.Keys == null || keyset.Keys.Count == 0)
            {
                Status?.Invoke("FNE sent a key response with no keys (key not in its container, or this peer is not allowed keys)");
                return;
            }

            foreach (KeyItem key in keyset.Keys)
            {
                if (key == null)
                    continue;

                byte[] material = key.GetKey();
                bool stored = keys.Store(keyset.AlgId, key.KeyId, material);
                if (material != null)
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(material);

                Status?.Invoke(stored
                    ? $"received {KeyStore.AlgorithmName(keyset.AlgId)} key 0x{key.KeyId:X4} from the FNE"
                    : $"ignored key 0x{key.KeyId:X4} with unsupported algorithm {KeyStore.AlgorithmName(keyset.AlgId)}");
            }
        }

        /*
        ** Transmit (IP25TrafficSink)
        */

        /// <inheritdoc/>
        public byte[] CreateP25Header(P25DUID duid, RemoteCallData callData, CryptoParams cryptoParams)
        {
            byte[] frame = new byte[P25FrameBuilder.FrameSize];
            CreateP25MessageHdr((byte)duid, callData, ref frame, cryptoParams);
            return frame;
        }

        /// <inheritdoc/>
        public void SendP25Voice(byte[] frame, ushort packetSequence, uint streamId)
        {
            fne.SendMasterTraffic(FneBase.CreateOpcode(Constants.NET_FUNC_PROTOCOL, Constants.NET_PROTOCOL_SUBFUNC_P25),
                                  frame, packetSequence, streamId);
        }

        /// <inheritdoc/>
        public void SendP25Tdu(uint srcId, uint dstId, uint streamId, bool grantDemand)
        {
            RemoteCallData callData = new RemoteCallData
            {
                SrcId = srcId,
                DstId = dstId,
                LCO = P25Defines.LC_GROUP,
                TxStreamID = streamId
            };

            // one grant demand to start; the console sends four TDUs to make sure the call end lands
            int count = grantDemand ? 1 : 4;
            for (int i = 0; i < count; i++)
                SendP25TDU(callData, grantDemand);
        }

        /*
        ** Not used by the proof of concept
        */

        /// <inheritdoc/>
        protected override bool DMRDataValidate(uint peerId, uint srcId, uint dstId, byte slot, CallType callType, FrameType frameType, DMRDataType dataType, uint streamId, byte[] message) => true;

        /// <inheritdoc/>
        protected override void DMRDataReceived(object sender, DMRDataReceivedEvent e) { }

        /// <inheritdoc/>
        protected override bool NXDNDataValidate(uint peerId, uint srcId, uint dstId, CallType callType, NXDNMessageType messageType, FrameType frameType, uint streamId, byte[] message) => true;

        /// <inheritdoc/>
        protected override void NXDNDataReceived(object sender, NXDNDataReceivedEvent e) { }
    } // public sealed class CliPeer
}
