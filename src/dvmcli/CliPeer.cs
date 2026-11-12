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

namespace dvmcli
{
    /// <summary>
    /// FNE console peer for the CLI.
    /// </summary>
    public sealed class CliPeer : fnecore.FneSystemBase
    {
        private readonly P25VoiceReceiver receiver;
        private readonly bool verbose;

        /// <summary>Raised with a status line for the user.</summary>
        public event Action<string> Status;

        /// <summary>True once the master has accepted our login.</summary>
        public bool Connected { get; private set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="CliPeer"/> class.
        /// </summary>
        public CliPeer(Codeplug.System system, P25VoiceReceiver receiver, bool verbose)
            : base(CreatePeer(system), verbose ? LogLevel.DEBUG : LogLevel.INFO)
        {
            this.receiver = receiver;
            this.verbose = verbose;

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
                receiver.Handle(e.DstId, e.SrcId, e.DUID, e.StreamId, e.Data);
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
        }

        /// <inheritdoc/>
        protected override void KeyResponse(object sender, KeyResponseEvent e)
        {
            Status?.Invoke("received a key response (encrypted calls are not decoded in this proof of concept)");
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
