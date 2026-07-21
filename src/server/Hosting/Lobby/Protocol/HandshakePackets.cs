using System.Buffers.Binary;
using System.Net;
using Arcadia.Hosting.Lobby.Wire;
using Arcadia.Storage;

// GameManager handshake packet builders.
namespace Arcadia.Hosting.Lobby.Protocol
{
    public static class HandshakePackets
    {
        public static byte[] BuildHostHello(
            ushort rosterSizeWire,
            IPAddress hostIp,
            ushort hostPort)
        {
            byte[] header = new byte[]
            {
                GameManagerPacketType.HostHello.ToWireByte(),       // [0]  opcode 0x82
                0x80, 0x14,                                         // [1..2]  Sint16 ver = 20
                0x80, 0x00, 0x00, 0x00,                             // [3..6]  game name len = 0
                0x80, 0x00,                                         // [7..8]  Sint16 ignored
                BiasedEncoding.Char((sbyte)GameManagerNetworkType.ClientServer), // [9]  netType
                BiasedEncoding.Char((sbyte)GameManagerVoipType.Mesh),            // [10] voipType
                0x80,                                               // [11] currentJoinMode = 0
                0x00, 0x00,                                         // [12..13] currentJoinFlags = 0
                0x00,                                               // [14] inProgress
                0x00,                                               // [15] ranked
                0x00,                                               // [16] joinInProgress
                0x00,                                               // [17] joinViaPresence
                0x80,                                               // [18] inviteStatus = 0
                0x00,                                               // [19] hostMigration
                0x00, 0x06, 0x01,                                   // [20..22] PlayerType[0]: cap=6, voip=1
                0x00, 0x00, 0x00,                                   // [23..25] PlayerType[1]: cap=0, voip=0
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,     // [26..33] xb360 nonce
                0x00, 0x00,                                         // [34..35] rosterElemSize (patched)
                0,                                                  // [36] hasHost
            };
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(34, 2), rosterSizeWire);
            return header;
        }

        public static byte[] BuildHostRosterElem(UdpSessionCache.ClientInfo info, int slotId, IPAddress hostIp, ushort hostPort)
        {
            byte[] nameBytes = RosterPlayer.EncodeName(info.Name);
            byte[] body = new byte[1 + RosterPlayer.FieldsSize(nameBytes.Length)];
            body[0] = GameManagerPacketType.HostRosterElem.ToWireByte();
            RosterPlayer.WriteFields(body.AsSpan(1), info, slotId, hostIp, hostPort);
            return body;
        }

        public static byte[] BuildPlayerJoinFullMesh(int joinerPlayerRef)
        {
            byte[] body = new byte[1 + 4];
            body[0] = GameManagerPacketType.PlayerJoinFullMesh.ToWireByte();
            BiasedEncoding.WriteSint32(body.AsSpan(1, 4), joinerPlayerRef);
            return body;
        }

        public static byte[] BuildPlayerJoinComplete(UdpSessionCache.ClientInfo info)
        {
            byte[] body = new byte[1 + 4];
            body[0] = GameManagerPacketType.JoinComplete.ToWireByte();
            BiasedEncoding.WriteSint32(body.AsSpan(1, 4), info.PlayerRef);
            return body;
        }

        public static byte[] BuildPlayerLeft(UdpSessionCache.ClientInfo leaver, byte reason)
        {
            byte[] body = new byte[1 + 4 + 1];
            body[0] = GameManagerPacketType.PlayerLeft.ToWireByte();
            BiasedEncoding.WriteSint32(body.AsSpan(1, 4), leaver.PlayerRef);
            BiasedEncoding.WriteChar(body.AsSpan(5, 1), unchecked((sbyte)reason));
            return body;
        }

        public static byte[] BuildVoipEnabledChange(int playerRef, bool enabled)
        {
            byte[] body = new byte[1 + 4 + 1];
            body[0] = GameManagerPacketType.VoipEnabledChange.ToWireByte();
            BiasedEncoding.WriteSint32(body.AsSpan(1, 4), playerRef);
            body[5] = enabled ? (byte)1 : (byte)0;
            return body;
        }
    }
}
