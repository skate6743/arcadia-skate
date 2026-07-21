using System.Buffers.Binary;

// Sign-biased encoding: wire 0x80 maps to signed 0.
namespace Arcadia.Hosting.Lobby.Wire
{
    public static class BiasedEncoding
    {
        public const byte CharBias = 0x80;
        public const ushort Sint16Bias = 0x8000;
        public const uint Sint32Bias = 0x80000000u;
        public const ulong Sint64Bias = 0x8000000000000000UL;

        public static byte Char(sbyte value) => (byte)(value + CharBias);

        public static void WriteChar(Span<byte> dst, sbyte value)
            => dst[0] = Char(value);

        public static void WriteSint16(Span<byte> dst, short value)
            => BinaryPrimitives.WriteUInt16BigEndian(dst, (ushort)(unchecked((ushort)value) + Sint16Bias));

        public static void WriteSint32(Span<byte> dst, int value)
            => BinaryPrimitives.WriteUInt32BigEndian(dst, unchecked((uint)value) + Sint32Bias);

        public static void WriteSint64(Span<byte> dst, long value)
            => BinaryPrimitives.WriteUInt64BigEndian(dst, unchecked((ulong)value) + Sint64Bias);

        public static int WriteString(Span<byte> dst, ReadOnlySpan<byte> bytes)
        {
            WriteSint32(dst, bytes.Length);
            bytes.CopyTo(dst[4..]);
            return 4 + bytes.Length;
        }
    }
}
