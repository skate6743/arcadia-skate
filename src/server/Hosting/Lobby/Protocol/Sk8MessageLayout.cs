using System.Buffers.Binary;
using Arcadia.EA;

// Variant-aware fixed-body / attribute-list / final-results sizes.
namespace Arcadia.Hosting.Lobby.Protocol
{
    public static class Sk8MessageLayout
    {
        // wire record stride, distinct from the client's 48-byte in-memory PlayerStatsData
        public const int FinalResultsPlayerStride = 39;
        public const int FinalResultsSkate2MaxPlayers = 8;
        public const int FinalResultsSkate1FixedSlots = 6;
        public const int FinalResultsSkate1PerSlotBytes = 26;
        public const int FinalResultsSkate1FixedBodySize = FinalResultsSkate1FixedSlots * FinalResultsSkate1PerSlotBytes + 4;

        public const int AttributeListSlotsSkate2 = 9;
        public const int AttributeListSlotsSkate1 = 12;
        public const int AttributeListMaxBytesPerSlot = 64;
        public const int AttributeListSkate1LockTimeTrailerBytes = 8;

        public static int FixedBodySize(GameVariant variant, byte op)
        {
            Sk8Opcodes.Kind? kind = Sk8Opcodes.Decode(variant, op);
            if (kind is null)
                return -1;
            return Sk8Opcodes.FixedBodySize(variant, kind.Value);
        }

        public static int ProbeAttributeListLength(ReadOnlySpan<byte> buf, int firstStringOffset, int slotCount, int maxSlotBytes)
        {
            int off = firstStringOffset;
            for (int i = 0; i < slotCount; i++)
            {
                if (off + 4 > buf.Length) return -1;
                int len = (int)BinaryPrimitives.ReadUInt32BigEndian(buf.Slice(off, 4));
                if (len < 0 || len > maxSlotBytes) return -1;
                off += 4 + len;
                if (off > buf.Length) return -1;
            }
            return off - firstStringOffset;
        }

        public static int AttributeSlotCount(GameVariant variant)
            => variant == GameVariant.Skate1 ? AttributeListSlotsSkate1 : AttributeListSlotsSkate2;
    }
}
