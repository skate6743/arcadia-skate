using System.Buffers.Binary;
using System.Net;

// Two back-to-back InternetAddress records: 4 BE addr + 2 BE port each.
namespace Arcadia.Hosting.Lobby.Wire
{
    public static class InternetAddressPair
    {
        public const int Bytes = 12;
        public const byte SelectorInternetAddressPair = 0;

        public static void Write(Span<byte> dst, IPAddress internalIp, ushort internalPort, IPAddress externalIp, ushort externalPort)
        {
            if (dst.Length < Bytes)
                throw new ArgumentException($"need {Bytes} bytes", nameof(dst));

            WriteOne(dst[..6], internalIp, internalPort);
            WriteOne(dst.Slice(6, 6), externalIp, externalPort);
        }

        private static void WriteOne(Span<byte> dst, IPAddress addr, ushort port)
        {
            addr.GetAddressBytes().CopyTo(dst);
            BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(4, 2), port);
        }
    }
}
