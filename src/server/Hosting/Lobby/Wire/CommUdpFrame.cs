using System.Buffers.Binary;

namespace Arcadia.Hosting.Lobby.Wire
{
    public enum CommUdpKind : uint
    {
        Connect = 1,
        ConnAck = 2,
        Disconnect = 3,
        PingRetransmitRequest = 4,
        Poke = 5,
    }

    public static class CommUdpFrame
    {
        public const int HeaderBytes = 8;
        public const int PlainConnLength = 12;
        public const int PlainAckOnlyLength = 8;

        // w0 bit layout: bits 0-23 = seq, bits 24-27 = metaType, bits 28-31 = extra sub-packet count.
        public const uint BareSeqMask = 0x00FFFFFFu;
        public const int SubCountShift = 28;
        public const int MetaTypeShift = 24;
        public const uint MetaTypeMask = 0xFu;
        public const int MetaType1HeaderExtraBytes = 8;

        public static uint BareSeq(uint seqWord) => seqWord & BareSeqMask;
        public static int SubCount(uint seqWord) => (int)((seqWord >> SubCountShift) & 0xF);
        public static uint MetaType(uint seqWord) => (seqWord >> MetaTypeShift) & MetaTypeMask;

        public static bool LooksLikePlainControl(byte[] buf)
        {
            if (buf.Length != PlainConnLength)
                return false;
            uint kind = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(0, 4));
            return kind >= 1 && kind <= 6;
        }

        // Length tails read from the END backwards.
        // 0x00..0xFA = 1-byte size; 0xFB..0xFF = 2-byte size = 251 + prev + ((last - 0xFB) << 8).
        public static bool TrySplitBundle(
            ReadOnlySpan<byte> payload,
            int subCount,
            out List<(int Start, int Length)> subPackets)
        {
            subPackets = new List<(int, int)>(subCount + 1);
            int end = payload.Length;
            for (int i = 0; i < subCount; i++)
            {
                if (end < 1) return false;
                end -= 1;
                int last = payload[end];
                int len;
                if (last > 0xFA)
                {
                    if (end < 1) return false;
                    end -= 1;
                    int prev = payload[end];
                    len = 251 + prev + ((last - 0xFB) << 8);
                }
                else
                {
                    len = last;
                }
                int start = end - len;
                if (start < 0) return false;
                subPackets.Add((start, len));
                end = start;
            }
            subPackets.Add((0, end));
            subPackets.Reverse();
            return true;
        }
    }
}
