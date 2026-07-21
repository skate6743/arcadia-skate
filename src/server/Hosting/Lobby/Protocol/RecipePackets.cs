using System.Buffers.Binary;
using Arcadia.EA;

// MT_GameRecipeRequest/Head/Data builders. S1 opcodes 20/21/22; S2 23/24/25.
namespace Arcadia.Hosting.Lobby.Protocol
{
    public static class RecipePackets
    {
        public static byte RequestOpcode(GameVariant variant)
            => variant == GameVariant.Skate1 ? (byte)Sk8MessageType.GameRecipeRequest_Skate1 : (byte)Sk8MessageType.GameRecipeRequest_Skate2;

        public static byte HeadOpcode(GameVariant variant)
            => variant == GameVariant.Skate1 ? (byte)Sk8MessageType.GameRecipeHead_Skate1 : (byte)Sk8MessageType.GameRecipeHead_Skate2;

        public static byte DataOpcode(GameVariant variant)
            => variant == GameVariant.Skate1 ? (byte)Sk8MessageType.GameRecipeData_Skate1 : (byte)Sk8MessageType.GameRecipeData_Skate2;

        public static byte[] BuildRequest(GameVariant variant, long peerId)
        {
            byte[] body = new byte[1 + 8];
            body[0] = RequestOpcode(variant);
            BinaryPrimitives.WriteInt64BigEndian(body.AsSpan(1, 8), peerId);
            return body;
        }

        public static byte[] BuildHead(GameVariant variant, long peerId, int size, uint crc)
        {
            byte[] body = new byte[1 + 8 + 4 + 4];
            body[0] = HeadOpcode(variant);
            BinaryPrimitives.WriteInt64BigEndian(body.AsSpan(1, 8), peerId);
            BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(9, 4), unchecked((uint)size));
            BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(13, 4), crc);
            return body;
        }

        public static byte[] BuildData(GameVariant variant, long peerId, int chunkIndex, ReadOnlySpan<byte> chunk)
        {
            byte[] body = new byte[1 + 8 + 4 + 4 + chunk.Length];
            body[0] = DataOpcode(variant);
            BinaryPrimitives.WriteInt64BigEndian(body.AsSpan(1, 8), peerId);
            BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(9, 4), unchecked((uint)chunk.Length));
            BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(13, 4), unchecked((uint)chunkIndex));
            chunk.CopyTo(body.AsSpan(17));
            return body;
        }
    }
}
