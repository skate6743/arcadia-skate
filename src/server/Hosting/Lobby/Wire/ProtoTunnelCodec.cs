using System.Buffers.Binary;
using Arcadia.EA;

// ProtoTunnel encrypted frame codec: [2 BE counter][N×2 BE tuple headers][encrypted payloads][clear payloads].
namespace Arcadia.Hosting.Lobby.Wire
{
    public static class ProtoTunnelCodec
    {
        public const int CounterBytes = 2;
        public const int TupleHeaderBytes = 2;
        public const int PreambleTunnelIdx = 7;
        public const int PreambleLength = 6;
        public const int CommUdpTunnelIdx = 1;
        public const int Skate1AltCommUdpTunnelIdx = 2;

        public static bool IsEncryptedTunnel(GameVariant variant, int idx)
            => variant == GameVariant.Skate1
                ? (idx == 1 || idx == 2 || idx == 7)
                : (idx == PreambleTunnelIdx);

        public static bool IsCommUdpTunnel(GameVariant variant, int idx)
            => variant == GameVariant.Skate1
                ? (idx == CommUdpTunnelIdx || idx == Skate1AltCommUdpTunnelIdx)
                : (idx == CommUdpTunnelIdx);

        public readonly record struct TunnelTuple(int Idx, int Length, int Offset);

        public class DecodedFrame
        {
            public ushort WireCounter;
            public uint EffectiveCounter;
            public required byte[] Work;
            public required IReadOnlyList<TunnelTuple> Tuples;
            public byte[]? Preamble;
            public bool TupleSumMismatch;
            public bool WrapForwardOccurred;
            public bool DecodedFromPreviousEpoch;
        }

        public static DecodedFrame? TryDecode(
            ReadOnlySpan<byte> wire,
            ReadOnlySpan<byte> ekey,
            Arc4Stream recvStream,
            GameVariant variant)
        {
            if (wire.Length < CounterBytes + TupleHeaderBytes)
                return null;

            ushort counter = BinaryPrimitives.ReadUInt16BigEndian(wire[..CounterBytes]);
            uint prevHighWord = recvStream.HighWord;

            recvStream.TakeSnapshot();

            Arc4Stream.AdvanceResult advanceResult = recvStream.TryAdvanceToCounter(counter);

            if (advanceResult == Arc4Stream.AdvanceResult.StaleInEpoch)
            {
                if (recvStream.TryAdvanceOopToCounter(counter))
                {
                    DecodedFrame? oopFrame = TryDecodeViaOop(wire, recvStream, variant, counter);
                    if (oopFrame != null)
                    {
                        recvStream.OopRecoveredPackets++;
                        return oopFrame;
                    }
                }
                return null;
            }

            if (advanceResult == Arc4Stream.AdvanceResult.StaleAcrossWrap)
            {
                uint fallbackEffective = ((uint)(prevHighWord - 1) << 16) | counter;
                return TryDecodeFresh(wire, ekey, fallbackEffective, variant, fromPreviousEpoch: true);
            }

            byte[] work = wire[CounterBytes..].ToArray();
            List<TunnelTuple> tuples = new List<TunnelTuple>();
            int wireOff = CounterBytes;
            int hdrOff = 0;
            int bytesApplied = 0;

            while (wireOff < wire.Length)
            {
                if (hdrOff + TupleHeaderBytes > work.Length)
                {
                    recvStream.RestoreSnapshot();
                    return null;
                }
                recvStream.Apply(work.AsSpan(hdrOff, TupleHeaderBytes));
                bytesApplied += TupleHeaderBytes;
                ushort hdr = BinaryPrimitives.ReadUInt16BigEndian(work.AsSpan(hdrOff, TupleHeaderBytes));
                int length = hdr >> 4;
                int idx = hdr & 0xF;
                tuples.Add(new TunnelTuple(idx, length, 0));
                wireOff += length + TupleHeaderBytes;
                hdrOff += TupleHeaderBytes;
                if (tuples.Count > 32)
                {
                    recvStream.RestoreSnapshot();
                    return null;
                }
            }

            bool sumMismatch = wireOff != wire.Length;

            int encTotal = 0;
            foreach (TunnelTuple t in tuples)
                if (IsEncryptedTunnel(variant, t.Idx))
                    encTotal += t.Length;

            if (encTotal > 0)
            {
                if (hdrOff + encTotal > work.Length)
                {
                    recvStream.RestoreSnapshot();
                    return null;
                }
                recvStream.Apply(work.AsSpan(hdrOff, encTotal));
                bytesApplied += encTotal;
            }

            recvStream.Realign(bytesApplied);

            byte[]? preamble = null;
            bool hasPreamble = false;
            foreach (TunnelTuple t in tuples)
            {
                if (t.Idx == PreambleTunnelIdx && t.Length == PreambleLength)
                {
                    preamble = work.AsSpan(hdrOff, PreambleLength).ToArray();
                    hasPreamble = true;
                    break;
                }
            }

            int preambleBytes = hasPreamble ? PreambleLength : 0;
            int scanOff = hdrOff + preambleBytes;
            List<TunnelTuple> withOffsets = new List<TunnelTuple>(tuples.Count);
            foreach (TunnelTuple t in tuples)
            {
                if (t.Idx == PreambleTunnelIdx && t.Length == PreambleLength)
                {
                    withOffsets.Add(t with { Offset = hdrOff });
                }
                else
                {
                    withOffsets.Add(t with { Offset = scanOff });
                    scanOff += t.Length;
                }
            }

            return new DecodedFrame
            {
                WireCounter = counter,
                EffectiveCounter = recvStream.EffectiveCounter,
                Work = work,
                Tuples = withOffsets,
                Preamble = preamble,
                TupleSumMismatch = sumMismatch,
                WrapForwardOccurred = advanceResult == Arc4Stream.AdvanceResult.ForwardWrap,
                DecodedFromPreviousEpoch = false,
            };
        }

        private static DecodedFrame? TryDecodeViaOop(
            ReadOnlySpan<byte> wire,
            Arc4Stream recvStream,
            GameVariant variant,
            ushort counter)
        {
            byte[] work = wire[CounterBytes..].ToArray();
            List<TunnelTuple> tuples = new List<TunnelTuple>();
            int wireOff = CounterBytes;
            int hdrOff = 0;
            int bytesApplied = 0;

            while (wireOff < wire.Length)
            {
                if (hdrOff + TupleHeaderBytes > work.Length)
                    return null;
                recvStream.ApplyOop(work.AsSpan(hdrOff, TupleHeaderBytes));
                bytesApplied += TupleHeaderBytes;
                ushort hdr = BinaryPrimitives.ReadUInt16BigEndian(work.AsSpan(hdrOff, TupleHeaderBytes));
                int length = hdr >> 4;
                int idx = hdr & 0xF;
                tuples.Add(new TunnelTuple(idx, length, 0));
                wireOff += length + TupleHeaderBytes;
                hdrOff += TupleHeaderBytes;
                if (tuples.Count > 32)
                    return null;
            }

            bool sumMismatch = wireOff != wire.Length;

            int encTotal = 0;
            foreach (TunnelTuple t in tuples)
                if (IsEncryptedTunnel(variant, t.Idx))
                    encTotal += t.Length;

            if (encTotal > 0)
            {
                if (hdrOff + encTotal > work.Length)
                    return null;
                recvStream.ApplyOop(work.AsSpan(hdrOff, encTotal));
                bytesApplied += encTotal;
            }

            recvStream.RealignOop(bytesApplied);

            byte[]? preamble = null;
            bool hasPreamble = false;
            foreach (TunnelTuple t in tuples)
            {
                if (t.Idx == PreambleTunnelIdx && t.Length == PreambleLength)
                {
                    preamble = work.AsSpan(hdrOff, PreambleLength).ToArray();
                    hasPreamble = true;
                    break;
                }
            }

            int preambleBytes = hasPreamble ? PreambleLength : 0;
            int scanOff = hdrOff + preambleBytes;
            List<TunnelTuple> withOffsets = new List<TunnelTuple>(tuples.Count);
            foreach (TunnelTuple t in tuples)
            {
                if (t.Idx == PreambleTunnelIdx && t.Length == PreambleLength)
                {
                    withOffsets.Add(t with { Offset = hdrOff });
                }
                else
                {
                    withOffsets.Add(t with { Offset = scanOff });
                    scanOff += t.Length;
                }
            }

            uint oopEffectiveCounter = ((uint)recvStream.HighWord << 16) | counter;

            return new DecodedFrame
            {
                WireCounter = counter,
                EffectiveCounter = oopEffectiveCounter,
                Work = work,
                Tuples = withOffsets,
                Preamble = preamble,
                TupleSumMismatch = sumMismatch,
                WrapForwardOccurred = false,
                DecodedFromPreviousEpoch = false,
            };
        }

        private static DecodedFrame? TryDecodeFresh(
            ReadOnlySpan<byte> wire,
            ReadOnlySpan<byte> ekey,
            uint effectiveCounter,
            GameVariant variant,
            bool fromPreviousEpoch)
        {
            if (wire.Length < CounterBytes + TupleHeaderBytes)
                return null;

            byte[] work = wire[CounterBytes..].ToArray();
            ushort counter = BinaryPrimitives.ReadUInt16BigEndian(wire[..CounterBytes]);

            Arc4 rc4 = new Arc4();
            rc4.Init(ekey, 1);
            rc4.Advance((int)(4 * effectiveCounter));

            List<TunnelTuple> tuples = new List<TunnelTuple>();
            int v16 = CounterBytes;
            int hdrOff = 0;
            while (v16 < wire.Length)
            {
                if (hdrOff + TupleHeaderBytes > work.Length)
                    return null;
                rc4.Apply(work.AsSpan(hdrOff, TupleHeaderBytes));
                ushort hdr = BinaryPrimitives.ReadUInt16BigEndian(work.AsSpan(hdrOff, TupleHeaderBytes));
                int length = hdr >> 4;
                int idx = hdr & 0xF;
                tuples.Add(new TunnelTuple(idx, length, 0));
                v16 += length + TupleHeaderBytes;
                hdrOff += TupleHeaderBytes;
                if (tuples.Count > 32)
                    return null;
            }

            bool sumMismatch = v16 != wire.Length;

            int encTotal = 0;
            foreach (TunnelTuple t in tuples)
                if (IsEncryptedTunnel(variant, t.Idx))
                    encTotal += t.Length;

            if (encTotal > 0)
            {
                if (hdrOff + encTotal > work.Length)
                    return null;
                rc4.Apply(work.AsSpan(hdrOff, encTotal));
            }

            byte[]? preamble = null;
            bool hasPreamble = false;
            foreach (TunnelTuple t in tuples)
            {
                if (t.Idx == PreambleTunnelIdx && t.Length == PreambleLength)
                {
                    preamble = work.AsSpan(hdrOff, PreambleLength).ToArray();
                    hasPreamble = true;
                    break;
                }
            }

            int preambleBytes = hasPreamble ? PreambleLength : 0;
            int scanOff = hdrOff + preambleBytes;
            List<TunnelTuple> withOffsets = new List<TunnelTuple>(tuples.Count);
            foreach (TunnelTuple t in tuples)
            {
                if (t.Idx == PreambleTunnelIdx && t.Length == PreambleLength)
                {
                    withOffsets.Add(t with { Offset = hdrOff });
                }
                else
                {
                    withOffsets.Add(t with { Offset = scanOff });
                    scanOff += t.Length;
                }
            }

            return new DecodedFrame
            {
                WireCounter = counter,
                EffectiveCounter = effectiveCounter,
                Work = work,
                Tuples = withOffsets,
                Preamble = preamble,
                TupleSumMismatch = sumMismatch,
                WrapForwardOccurred = false,
                DecodedFromPreviousEpoch = fromPreviousEpoch,
            };
        }

        public readonly record struct OutboundFrame(
            byte[] Packet,
            ushort StartCounter,
            ushort NextCounter,
            bool ServerCounterWrapped);

        public static OutboundFrame BuildConnAck(
            Arc4Stream sendStream,
            ReadOnlySpan<byte> preamble,
            byte preambleByte5,
            int tunnelIdx,
            uint clientIdent,
            GameVariant variant)
        {
            if (preamble.Length != PreambleLength)
                throw new ArgumentException("preamble must be 6 bytes", nameof(preamble));

            const int payloadLen = CommUdpFrame.PlainAckOnlyLength;
            byte[] packet = new byte[CounterBytes + 2 * TupleHeaderBytes + PreambleLength + payloadLen];

            ushort startCounter = sendStream.LastWireCounter;
            uint startHigh = sendStream.HighWord;

            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0, 2), startCounter);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), (ushort)((PreambleLength << 4) | PreambleTunnelIdx));
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4, 2), (ushort)((payloadLen << 4) | (tunnelIdx & 0xF)));
            preamble.CopyTo(packet.AsSpan(6, PreambleLength));
            packet[11] = preambleByte5;
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(12, 4), (uint)CommUdpKind.ConnAck);
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(16, 4), clientIdent);

            int applyLen = variant == GameVariant.Skate1 ? 18 : 10;
            sendStream.Apply(packet.AsSpan(2, applyLen));
            sendStream.Realign(applyLen);

            ushort nextCounter = sendStream.LastWireCounter;
            bool wrapped = sendStream.HighWord != startHigh;
            return new OutboundFrame(packet, startCounter, nextCounter, wrapped);
        }

        public static OutboundFrame BuildData(
            Arc4Stream sendStream,
            ReadOnlySpan<byte> preamble,
            byte preambleByte5,
            int tunnelIdx,
            uint serverSeq,
            uint ackValue,
            ReadOnlySpan<byte> netGameLinkBody,
            GameVariant variant)
        {
            if (preamble.Length != PreambleLength)
                throw new ArgumentException("preamble must be 6 bytes", nameof(preamble));

            int commudpPayloadLen = CommUdpFrame.HeaderBytes + netGameLinkBody.Length;
            int packetLen = CounterBytes + 2 * TupleHeaderBytes + PreambleLength + commudpPayloadLen;
            byte[] packet = new byte[packetLen];

            ushort startCounter = sendStream.LastWireCounter;
            uint startHigh = sendStream.HighWord;

            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0, 2), startCounter);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), (ushort)((PreambleLength << 4) | PreambleTunnelIdx));
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4, 2), (ushort)((commudpPayloadLen << 4) | (tunnelIdx & 0xF)));
            preamble.CopyTo(packet.AsSpan(6, PreambleLength));
            packet[11] = preambleByte5;
            // Mask seq to 24 bits; bits 24-27 = metaType, 28-31 = subCount (both 0 here).
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(12, 4), serverSeq & CommUdpFrame.BareSeqMask);
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(16, 4), ackValue);
            netGameLinkBody.CopyTo(packet.AsSpan(20));

            int applyLen = variant == GameVariant.Skate1
                ? commudpPayloadLen + 2 * TupleHeaderBytes + PreambleLength
                : 10;
            sendStream.Apply(packet.AsSpan(2, applyLen));
            sendStream.Realign(applyLen);

            ushort nextCounter = sendStream.LastWireCounter;
            bool wrapped = sendStream.HighWord != startHigh;
            return new OutboundFrame(packet, startCounter, nextCounter, wrapped);
        }

        public static OutboundFrame BuildDataBundle(
            Arc4Stream sendStream,
            ReadOnlySpan<byte> preamble,
            byte preambleByte5,
            int tunnelIdx,
            IReadOnlyList<(uint Seq, byte[] Body)> entries,
            uint ackValue,
            GameVariant variant)
        {
            if (preamble.Length != PreambleLength)
                throw new ArgumentException("preamble must be 6 bytes", nameof(preamble));
            if (entries.Count == 0)
                throw new ArgumentException("bundle must have at least one entry", nameof(entries));
            if (entries.Count > 16)
                throw new ArgumentException("bundle subCount fits in 4 bits, max 16 entries", nameof(entries));

            for (int i = 0; i < entries.Count; i++)
            {
                int len = entries[i].Body?.Length ?? 0;
                if (len == 0 || len > 250)
                    throw new ArgumentException($"bundle entry {i} body length {len} out of range (1..250)", nameof(entries));
                if (i > 0 && entries[i].Seq <= entries[i - 1].Seq)
                    throw new ArgumentException("bundle entries must be ascending by seq with no duplicates", nameof(entries));
            }

            int subCount = entries.Count - 1;
            uint highestSeq = entries[entries.Count - 1].Seq;

            int bundleSize = entries[entries.Count - 1].Body.Length;
            for (int i = entries.Count - 2; i >= 0; i--)
                bundleSize += entries[i].Body.Length + 1;

            int commudpPayloadLen = CommUdpFrame.HeaderBytes + bundleSize;
            int packetLen = CounterBytes + 2 * TupleHeaderBytes + PreambleLength + commudpPayloadLen;
            byte[] packet = new byte[packetLen];

            ushort startCounter = sendStream.LastWireCounter;
            uint startHigh = sendStream.HighWord;

            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0, 2), startCounter);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), (ushort)((PreambleLength << 4) | PreambleTunnelIdx));
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4, 2), (ushort)((commudpPayloadLen << 4) | (tunnelIdx & 0xF)));
            preamble.CopyTo(packet.AsSpan(6, PreambleLength));
            packet[11] = preambleByte5;

            uint w0 = ((uint)subCount << 28) | (highestSeq & CommUdpFrame.BareSeqMask);
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(12, 4), w0);
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(16, 4), ackValue);

            int bundleOff = 20;
            byte[] highestBody = entries[entries.Count - 1].Body;
            highestBody.CopyTo(packet.AsSpan(bundleOff, highestBody.Length));
            bundleOff += highestBody.Length;

            for (int i = entries.Count - 2; i >= 0; i--)
            {
                byte[] body = entries[i].Body;
                body.CopyTo(packet.AsSpan(bundleOff, body.Length));
                bundleOff += body.Length;
                packet[bundleOff] = (byte)body.Length;
                bundleOff++;
            }

            int applyLen = variant == GameVariant.Skate1
                ? commudpPayloadLen + 2 * TupleHeaderBytes + PreambleLength
                : 10;
            sendStream.Apply(packet.AsSpan(2, applyLen));
            sendStream.Realign(applyLen);

            ushort nextCounter = sendStream.LastWireCounter;
            bool wrapped = sendStream.HighWord != startHigh;
            return new OutboundFrame(packet, startCounter, nextCounter, wrapped);
        }

        public static OutboundFrame BuildPingRetransmit(
            Arc4Stream sendStream,
            ReadOnlySpan<byte> preamble,
            byte preambleByte5,
            int tunnelIdx,
            uint expectedSeq,
            GameVariant variant)
        {
            if (preamble.Length != PreambleLength)
                throw new ArgumentException("preamble must be 6 bytes", nameof(preamble));

            const int payloadLen = CommUdpFrame.PlainAckOnlyLength;
            byte[] packet = new byte[CounterBytes + 2 * TupleHeaderBytes + PreambleLength + payloadLen];

            ushort startCounter = sendStream.LastWireCounter;
            uint startHigh = sendStream.HighWord;

            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0, 2), startCounter);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), (ushort)((PreambleLength << 4) | PreambleTunnelIdx));
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4, 2), (ushort)((payloadLen << 4) | (tunnelIdx & 0xF)));
            preamble.CopyTo(packet.AsSpan(6, PreambleLength));
            packet[11] = preambleByte5;
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(12, 4), (uint)CommUdpKind.PingRetransmitRequest);
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(16, 4), expectedSeq);

            int applyLen = variant == GameVariant.Skate1 ? 18 : 10;
            sendStream.Apply(packet.AsSpan(2, applyLen));
            sendStream.Realign(applyLen);

            ushort nextCounter = sendStream.LastWireCounter;
            bool wrapped = sendStream.HighWord != startHigh;
            return new OutboundFrame(packet, startCounter, nextCounter, wrapped);
        }
    }
}
