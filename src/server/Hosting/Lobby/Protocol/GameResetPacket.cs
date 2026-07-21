using System.Buffers.Binary;
using Arcadia.EA;

// MT_GameReset wire body. S1 52B (6 slots); S2 84B (8 slots, must stay 8 even at 6-cap).
namespace Arcadia.Hosting.Lobby.Protocol
{
    public static class GameResetPacket
    {
        public const int Skate2SlotCount = 8;
        public const int Skate1SlotCount = 6;

        public static byte[] Build(GameVariant variant, ReadOnlySpan<long> peerIdsBySlot, Sk8ResetType resetType, long activity)
        {
            if (variant == GameVariant.Skate1)
            {
                byte[] body = new byte[1 + Skate1SlotCount * 8 + 4];
                body[0] = Sk8Opcodes.GameReset(variant);
                WriteSlots(body.AsSpan(1, Skate1SlotCount * 8), peerIdsBySlot, Skate1SlotCount);
                BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(1 + Skate1SlotCount * 8, 4), (int)resetType);
                return body;
            }
            else
            {
                byte[] body = new byte[1 + Skate2SlotCount * 8 + 4 + 4 + 4 + 8];
                body[0] = Sk8Opcodes.GameReset(variant);
                WriteSlots(body.AsSpan(1, Skate2SlotCount * 8), peerIdsBySlot, Skate2SlotCount);
                BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(1 + Skate2SlotCount * 8, 4), (int)resetType);
                BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(1 + Skate2SlotCount * 8 + 4, 4), 1314481196);
                BinaryPrimitives.WriteInt64BigEndian(body.AsSpan(1 + Skate2SlotCount * 8 + 12, 8), activity);
                return body;
            }
        }

        private static void WriteSlots(Span<byte> dst, ReadOnlySpan<long> peerIdsBySlot, int slotCount)
        {
            for (int i = 0; i < dst.Length; i++)
                dst[i] = 0xFF;

            for (int slot = 0; slot < slotCount && slot < peerIdsBySlot.Length; slot++)
            {
                long uid = peerIdsBySlot[slot];
                if (uid == -1) continue;
                BinaryPrimitives.WriteInt64BigEndian(dst.Slice(slot * 8, 8), uid);
            }
        }
    }
}
