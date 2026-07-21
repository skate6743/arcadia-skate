using System.Buffers.Binary;

// Sk8 GameSync header parse (Skate 2 only; Skate 1 has a different wire format).
namespace Arcadia.Hosting.Lobby.Protocol
{
    public readonly record struct GameSyncHeader(
        Sk8GameSyncFlag Flag,
        byte Checksum,
        int DataSize,
        int TotalMessageBytes,
        int Player,
        bool FirstFrame,
        bool OnlyInput);

    public static class GameSyncMessage
    {
        public static bool TryParse(ReadOnlySpan<byte> buf, int offset, out GameSyncHeader header)
        {
            header = default;
            if (offset + 3 > buf.Length) return false;

            byte flagByte = buf[offset + 1];
            byte checksum = buf[offset + 2];
            int sizeFieldBytes = (flagByte & (byte)Sk8GameSyncFlag.SizeFieldIsTwoBytes) != 0 ? 2 : 1;

            if (offset + 2 + sizeFieldBytes >= buf.Length) return false;

            int dataSize = sizeFieldBytes == 2
                ? BinaryPrimitives.ReadUInt16BigEndian(buf.Slice(offset + 3, 2))
                : buf[offset + 3];

            int totalMessageBytes = 1 + 1 + 1 + sizeFieldBytes + dataSize;
            if (offset + totalMessageBytes > buf.Length) return false;

            header = new GameSyncHeader(
                (Sk8GameSyncFlag)flagByte,
                checksum,
                dataSize,
                totalMessageBytes,
                flagByte & (byte)Sk8GameSyncFlag.PlayerSlotMask,
                (flagByte & (byte)Sk8GameSyncFlag.FirstFrame) != 0,
                (flagByte & (byte)Sk8GameSyncFlag.OnlyInput) != 0);
            return true;
        }
    }
}
