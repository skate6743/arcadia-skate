using System.Net;
using System.Text;
using Arcadia.Hosting.Lobby.Wire;
using Arcadia.Storage;

// Shared player record on the wire: HOST_HELLO host slot, HOST_ROSTER_ELEM, PLAYER_JOIN.
namespace Arcadia.Hosting.Lobby.Protocol
{
    public static class RosterPlayer
    {
        public const int NameMaxBytes = 128;
        // 0 = PLAYER (wire byte 1 decodes to OBSERVER, which the client host-scan skips)
        public const byte ActivePlayerType = 0;

        public static int FieldsSize(int nameLen)
            => 4 + 4 + 4 + 4 + nameLen + 8 + 1 + 8 + 1 + 1 + InternetAddressPair.Bytes + 1 + 8;

        public static byte[] EncodeName(string? name)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(name ?? string.Empty);
            return bytes.Length > NameMaxBytes ? bytes[..NameMaxBytes] : bytes;
        }

        public static int WriteFields(
            Span<byte> dst,
            UdpSessionCache.ClientInfo info,
            int slotId,
            IPAddress addr,
            ushort port)
        {
            byte[] nameBytes = EncodeName(info.Name);
            int off = 0;
            BiasedEncoding.WriteSint32(dst.Slice(off, 4), info.PlayerRef); off += 4;
            BiasedEncoding.WriteSint32(dst.Slice(off, 4), info.PlayerRef); off += 4;
            BiasedEncoding.WriteSint32(dst.Slice(off, 4), slotId);          off += 4;
            off += BiasedEncoding.WriteString(dst[off..], nameBytes);
            BiasedEncoding.WriteSint64(dst.Slice(off, 8), info.UID);        off += 8;
            BiasedEncoding.WriteChar(dst.Slice(off, 1), 0); off += 1;
            dst.Slice(off, 8).Clear(); off += 8;
            BiasedEncoding.WriteChar(dst.Slice(off, 1), 0); off += 1;
            BiasedEncoding.WriteChar(dst.Slice(off, 1), (sbyte)InternetAddressPair.SelectorInternetAddressPair); off += 1;
            InternetAddressPair.Write(dst.Slice(off, InternetAddressPair.Bytes), addr, port, addr, port); off += InternetAddressPair.Bytes;
            dst[off++] = ActivePlayerType;
            BiasedEncoding.WriteSint64(dst.Slice(off, 8), info.UID); off += 8;
            return off;
        }
    }
}
