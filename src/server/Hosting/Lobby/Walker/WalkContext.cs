using System.Net;
using Microsoft.Extensions.Logging;

// Mutable context passed to each per-message walker handler.
namespace Arcadia.Hosting.Lobby.Walker
{
    public class WalkContext
    {
        public LobbyUdpServer Server = null!;
        public LobbySession Session = null!;
        public IPEndPoint Ep = null!;
        public byte[] Work = null!;
        public int PayloadBase;
        public int UserBodyLen;
        public uint SrcBareSeq;
        public uint DatagramAck;

        public int MsgOffset = 1;          // skip the leading sysFlag
        public bool WalkOk = true;
        public bool RewrapNextAsBroadcasted;
        public List<byte>? RelayBuilder;

        public ILogger Logger => Server.Logger;
        public int LobbyId => Server.LobbyId;
    }
}
