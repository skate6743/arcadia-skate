using System.Buffers.Binary;

// netGameLink frame inside a CommUDP payload: [1 sysFlag][body][optional 10B sync trailer][1 kindByte].
namespace Arcadia.Hosting.Lobby.Wire
{
    public static class NetGameLink
    {
        public const byte SysFlagApp = 0x00;
        public const byte SysFlagSystem = 0x01;

        public const int SyncTrailerBytes = 10;

        public const byte KindByteAckOnly = 0x40;
        public const byte KindByteWithBody = 0x46;
        public const byte SyncPresentBit = 0x40;

        public static int FrameSize(int bodyLength, bool includeSync)
            => 1 + bodyLength + (includeSync ? SyncTrailerBytes : 0) + 1;

        public static byte[] BuildAckOnly(uint peerEchoEstimate, uint nowTick)
        {
            byte[] frame = new byte[1 + SyncTrailerBytes];
            WriteSyncTrailer(frame.AsSpan(0, SyncTrailerBytes), peerEchoEstimate, nowTick);
            frame[SyncTrailerBytes] = KindByteAckOnly;
            return frame;
        }

        public static byte[] BuildWithBody(byte sysFlag, ReadOnlySpan<byte> body, uint peerEchoEstimate, uint nowTick)
        {
            byte[] frame = new byte[1 + body.Length + SyncTrailerBytes + 1];
            frame[0] = sysFlag;
            body.CopyTo(frame.AsSpan(1));
            int syncOff = 1 + body.Length;
            WriteSyncTrailer(frame.AsSpan(syncOff, SyncTrailerBytes), peerEchoEstimate, nowTick);
            frame[syncOff + SyncTrailerBytes] = KindByteWithBody;
            return frame;
        }

        public static void WriteSyncTrailer(Span<byte> dst, uint peerEchoEstimate, uint nowTick)
        {
            BinaryPrimitives.WriteUInt32BigEndian(dst[..4], peerEchoEstimate);
            BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(4, 4), nowTick);
            BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(8, 2), 0);
        }

        public static uint ReadPeerSendTick(ReadOnlySpan<byte> trailer)
            => BinaryPrimitives.ReadUInt32BigEndian(trailer.Slice(4, 4));
    }
}
