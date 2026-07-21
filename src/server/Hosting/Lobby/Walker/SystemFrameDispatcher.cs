using System.Buffers.Binary;
using System.Net;
using Arcadia.Hosting.Lobby.Protocol;
using Arcadia.Hosting.Lobby.Wire;
using Microsoft.Extensions.Logging;

// Handles netGameLink System-flag (0x01) frames: HELLO, ROSTER_ACK, RelayRequest host-kick.
namespace Arcadia.Hosting.Lobby.Walker
{
    public static class SystemFrameDispatcher
    {
        public static bool TryHandle(LobbyUdpServer server, IPEndPoint ep, LobbySession session,
            byte[] work, int payloadBase, int userBodyLen, uint srcBareSeq,
            byte systemFlag, byte opcodeByte)
        {
            if (systemFlag != NetGameLink.SysFlagSystem)
                return false;

            if (opcodeByte == GameManagerPacketType.Hello.ToWireByte() && session.Stage == AppStage.Idle)
            {
                session.Stage = AppStage.HelloReceived;
                server.OnHelloReceived(ep, session);
                server.Logger.LogInformation(
                    "LobbyUdp[{lobby}] HELLO from {ep} player={name} uid={uid}",
                    server.LobbyId, ep, session.PlayerInfo?.Name ?? "(unknown)", session.PlayerInfo?.UID ?? 0);
                return true;
            }

            if (opcodeByte == GameManagerPacketType.RosterAck.ToWireByte() && session.Stage == AppStage.HostRosterElemSent)
            {
                session.Stage = AppStage.RosterAckReceived;
                server.Logger.LogInformation("LobbyUdp[{lobby}] ROSTER_ACK from {ep}", server.LobbyId, ep);
                return true;
            }

            if (opcodeByte == GameManagerPacketType.RelayRequest.ToWireByte()
                && session.PlayerInfo is not null
                && userBodyLen >= 2 + 4 + 4 + 2)
            {
                int destRef = (int)(BinaryPrimitives.ReadUInt32BigEndian(
                    work.AsSpan(payloadBase + 2, 4)) - BiasedEncoding.Sint32Bias);
                int innerLen = (int)(BinaryPrimitives.ReadUInt32BigEndian(
                    work.AsSpan(payloadBase + 6, 4)) - BiasedEncoding.Sint32Bias);
                int innerBase = payloadBase + 10;
                if (innerLen >= 2 && innerBase + innerLen <= payloadBase + userBodyLen)
                {
                    byte[] lc = GameRequestPacket.LostConnection(server.Variant);
                    if (work[innerBase] == lc[0] && work[innerBase + 1] == lc[1])
                    {
                        server.Logger.LogInformation(
                            "LobbyUdp[{lobby}] HOST-KICK ep={ep} hostUid={uid} → destRef={dref}",
                            server.LobbyId, ep, session.PlayerInfo.UID, destRef);
                        server.OnHostKickRequested(destRef);
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
