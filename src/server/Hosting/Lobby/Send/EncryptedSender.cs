using System.Net;
using Arcadia.Hosting.Lobby.Wire;
using Microsoft.Extensions.Logging;

namespace Arcadia.Hosting.Lobby.Send
{
    public static class EncryptedSender
    {
        private const int MaxHexBytes = 256;

        public const int RedundancyLimitBytes = 96;

        public const int MaxRedundancySubs = 15;

        public static async Task SendConnAckAsync(LobbyUdpServer server, IPEndPoint ep, LobbySession session, uint ident, CancellationToken ct)
        {
            if (session.PreambleBytes is null || session.PreambleBytes.Length != ProtoTunnelCodec.PreambleLength)
                return;
            if (!session.SendStream.Initialized) session.SendStream.Init(server.EKey);

            byte[] packet;
            ushort startCounter;
            lock (session.SendLock)
            {
                var built = ProtoTunnelCodec.BuildConnAck(
                    session.SendStream,
                    session.PreambleBytes, session.ServerPreambleByte5,
                    session.TunnelIdx, ident, server.Variant);

                session.ServerPreambleByte5++;
                startCounter = built.StartCounter;
                packet = built.Packet;
            }

            server.Logger.LogInformation(
                "LobbyUdp[{lobby}] TX CONNACK to {ep} counter=0x{c:X4} ident=0x{id:X8}",
                server.LobbyId, ep, startCounter, ident);
            await server.Listener.SendAsync(packet, packet.Length, ep);
            session.LastOutboundAt = DateTimeOffset.UtcNow;
        }

        public static async Task SendPingRetransmitAsync(LobbyUdpServer server, IPEndPoint ep, LobbySession session, uint expectedSeq, CancellationToken ct)
        {
            if (session.PreambleBytes is null || session.PreambleBytes.Length != ProtoTunnelCodec.PreambleLength)
                return;
            if (!session.SendStream.Initialized) session.SendStream.Init(server.EKey);

            byte[] packet;
            ushort startCounter;
            lock (session.SendLock)
            {
                var built = ProtoTunnelCodec.BuildPingRetransmit(
                    session.SendStream,
                    session.PreambleBytes, session.ServerPreambleByte5,
                    session.TunnelIdx, expectedSeq, server.Variant);

                session.ServerPreambleByte5++;
                startCounter = built.StartCounter;
                packet = built.Packet;
            }

            server.Logger.LogInformation(
                "LobbyUdp[{lobby}] TX NACK-UPSTREAM (kind=4) to {ep} expectedSeq={seq} counter=0x{c:X4} nacksSent={n}",
                server.LobbyId, ep, expectedSeq, startCounter, session.NacksSent);
            await server.Listener.SendAsync(packet, packet.Length, ep);
            session.LastOutboundAt = DateTimeOffset.UtcNow;
        }

        public static async Task<uint> SendDataAsync(
            LobbyUdpServer server,
            IPEndPoint ep,
            LobbySession session,
            byte[] netGameLinkBody,
            string? label,
            CancellationToken ct,
            uint? explicitSeq = null,
            bool reliable = false,
            Func<bool>? sendGate = null)
        {
            if (session.PreambleBytes is null || session.PreambleBytes.Length != ProtoTunnelCodec.PreambleLength)
                return 0;
            if (!session.SendStream.Initialized) session.SendStream.Init(server.EKey);

            byte[] packet;
            ushort startCounter;
            uint serverSeq;
            uint ackValue;
            bool isFreshSend;
            bool wrapped;
            uint newServerHigh;
            ushort nextCounter;
            int redundancySubsIncluded = 0;

            lock (session.SendLock)
            {
                // Evaluated where the seq is allocated: a body that must not land after a
                // just-sent reset is rejected here or got an earlier seq — never a later one.
                if (sendGate is not null && !sendGate())
                    return 0;

                if (explicitSeq.HasValue)
                {
                    serverSeq = explicitSeq.Value;
                    isFreshSend = false;
                }
                else
                {
                    serverSeq = session.ServerDataSeq;
                    session.ServerDataSeq = serverSeq + 1;
                    isFreshSend = true;
                }

                if (isFreshSend)
                {
                    session.SentFrameCache[serverSeq] = netGameLinkBody;
                    if (serverSeq > (uint)LobbySession.SentFrameCacheCapacity)
                    {
                        uint staleKey = serverSeq - (uint)LobbySession.SentFrameCacheCapacity;
                        session.SentFrameCache.TryRemove(staleKey, out _);
                    }
                    if (reliable) session.ReliableUnackedSeqs.Add(serverSeq);
                }

                ackValue = session.ClientAckSeq;

                // "unacked" is vs LastAckFromClient (OUTBOUND), NOT ClientAckSeq (inbound).
                List<(uint Seq, byte[] Body)>? bundleEntries = null;
                bool exhaustedAllCandidates = false;
                bool stoppedAtLimit = false;
                if (isFreshSend && netGameLinkBody.Length > 0 && netGameLinkBody.Length <= 250
                    && session.LastAckFromClientInitialized && session.LastAckFromClient + 1 < serverSeq)
                {
                    int adaptiveCap = Math.Min(session.RedundancyMLimit - 1, MaxRedundancySubs);
                    int totalBundleSize = netGameLinkBody.Length;
                    List<(uint Seq, byte[] Body)> redundantCandidates = new List<(uint, byte[])>();
                    uint candidateSeq = serverSeq - 1;
                    while (candidateSeq > session.LastAckFromClient)
                    {
                        if (redundantCandidates.Count >= adaptiveCap)
                        {
                            stoppedAtLimit = true;
                            break;
                        }
                        if (!session.SentFrameCache.TryGetValue(candidateSeq, out byte[]? candidateBody))
                        {
                            exhaustedAllCandidates = true;
                            break;
                        }
                        if (candidateBody.Length == 0 || candidateBody.Length > 250)
                        {
                            stoppedAtLimit = true;
                            break;
                        }
                        int sizeWithLengthByte = candidateBody.Length + 1;
                        if (totalBundleSize + sizeWithLengthByte > RedundancyLimitBytes)
                        {
                            stoppedAtLimit = true;
                            break;
                        }
                        totalBundleSize += sizeWithLengthByte;
                        redundantCandidates.Add((candidateSeq, candidateBody));
                        if (candidateSeq == 0) break;
                        candidateSeq--;
                    }
                    if (candidateSeq <= session.LastAckFromClient) exhaustedAllCandidates = true;

                    if (redundantCandidates.Count > 0)
                    {
                        bundleEntries = new List<(uint, byte[])>(redundantCandidates.Count + 1);
                        for (int i = redundantCandidates.Count - 1; i >= 0; i--)
                            bundleEntries.Add(redundantCandidates[i]);
                        bundleEntries.Add((serverSeq, netGameLinkBody));
                        redundancySubsIncluded = redundantCandidates.Count;
                        Interlocked.Add(ref session.RedundancySubsSent, redundantCandidates.Count);
                    }

                    if (exhaustedAllCandidates)
                    {
                        session.RedundancyMLimit = 2;
                    }
                    else if (stoppedAtLimit)
                    {
                        int doubled = session.RedundancyMLimit * 2;
                        session.RedundancyMLimit = doubled > MaxRedundancySubs ? MaxRedundancySubs : doubled;
                    }
                }

                ProtoTunnelCodec.OutboundFrame built;
                if (bundleEntries != null)
                {
                    built = ProtoTunnelCodec.BuildDataBundle(
                        session.SendStream,
                        session.PreambleBytes, session.ServerPreambleByte5,
                        session.TunnelIdx, bundleEntries, ackValue, server.Variant);
                }
                else
                {
                    built = ProtoTunnelCodec.BuildData(
                        session.SendStream,
                        session.PreambleBytes, session.ServerPreambleByte5,
                        session.TunnelIdx, serverSeq, ackValue, netGameLinkBody, server.Variant);
                }

                session.ServerPreambleByte5++;
                startCounter = built.StartCounter;
                nextCounter = built.NextCounter;
                wrapped = built.ServerCounterWrapped;
                newServerHigh = session.SendStream.HighWord;
                packet = built.Packet;
            }

            if (wrapped)
            {
                server.Logger.LogInformation(
                    "LobbyUdp[{lobby}] PROTOTUNNEL-WRAP-OUT ep={ep} uid={uid} newHigh={hi} startCounter=0x{sc:X4} nextCounter=0x{nc:X4} seq={seq}",
                    server.LobbyId, ep, session.PlayerInfo?.UID ?? 0, newServerHigh, startCounter, nextCounter, serverSeq);
            }

            string? effectiveLabel = label;
            if (redundancySubsIncluded > 0 && effectiveLabel != null)
            {
                effectiveLabel = $"{label} redundancy={redundancySubsIncluded}";
            }

            if (effectiveLabel is null)
            {
                if (server.Logger.IsEnabled(LogLevel.Trace))
                {
                    string ackOnlyLabel = redundancySubsIncluded > 0
                        ? $"(ack-only) redundancy={redundancySubsIncluded}"
                        : "(ack-only)";
                    server.Logger.LogTrace(
                        "LobbyUdp[{lobby}] TX {label} to {ep} seq={seq} ack={ack} counter=0x{c:X4} bytes={len} stage={stage}",
                        server.LobbyId, ackOnlyLabel, ep, serverSeq, ackValue, startCounter, packet.Length, session.Stage);
                }
            }
            else if (server.Logger.IsEnabled(LogLevel.Trace))
            {
                server.Logger.LogTrace(
                    "LobbyUdp[{lobby}] TX {label} to {ep} seq={seq} ack={ack} counter=0x{c:X4} bytes={len} stage={stage}",
                    server.LobbyId, effectiveLabel, ep, serverSeq, ackValue, startCounter, packet.Length, session.Stage);
            }

            await server.Listener.SendAsync(packet, packet.Length, ep);
            session.LastOutboundAt = DateTimeOffset.UtcNow;
            return serverSeq;
        }

        public static async Task SendBundledDataAsync(
            LobbyUdpServer server,
            IPEndPoint ep,
            LobbySession session,
            IReadOnlyList<(uint Seq, byte[] Body)> entries,
            string label,
            CancellationToken ct)
        {
            if (session.PreambleBytes is null || session.PreambleBytes.Length != ProtoTunnelCodec.PreambleLength)
                return;
            if (entries.Count == 0) return;
            if (!session.SendStream.Initialized) session.SendStream.Init(server.EKey);

            byte[] packet;
            ushort startCounter;
            uint ackValue;
            bool wrapped;
            uint newServerHigh;
            ushort nextCounter;

            lock (session.SendLock)
            {
                ackValue = session.ClientAckSeq;

                var built = ProtoTunnelCodec.BuildDataBundle(
                    session.SendStream,
                    session.PreambleBytes, session.ServerPreambleByte5,
                    session.TunnelIdx, entries, ackValue, server.Variant);

                session.ServerPreambleByte5++;
                startCounter = built.StartCounter;
                nextCounter = built.NextCounter;
                wrapped = built.ServerCounterWrapped;
                newServerHigh = session.SendStream.HighWord;
                packet = built.Packet;
            }

            if (wrapped)
            {
                server.Logger.LogInformation(
                    "LobbyUdp[{lobby}] PROTOTUNNEL-WRAP-OUT (bundle) ep={ep} uid={uid} newHigh={hi} startCounter=0x{sc:X4} nextCounter=0x{nc:X4} subs={n}",
                    server.LobbyId, ep, session.PlayerInfo?.UID ?? 0, newServerHigh, startCounter, nextCounter, entries.Count);
            }

            server.Logger.LogInformation(
                "LobbyUdp[{lobby}] TX {label} to {ep} subs={n} fromSeq={fs} toSeq={ts} ack={ack} counter=0x{c:X4} bytes={len} stage={stage}",
                server.LobbyId, label, ep, entries.Count, entries[0].Seq, entries[entries.Count - 1].Seq,
                ackValue, startCounter, packet.Length, session.Stage);

            await server.Listener.SendAsync(packet, packet.Length, ep);
            session.LastOutboundAt = DateTimeOffset.UtcNow;
        }

        // GameSync content must pass reliable=false: the client recovers it via kind=4 NACK, and reliable re-push can overflow the 32-slot recv ring.
        public static Task<uint> SendSk8BodyAsync(LobbyUdpServer server, IPEndPoint ep, LobbySession session, byte[] appBody, string? label, CancellationToken ct, bool reliable = false, Func<bool>? sendGate = null)
        {
            uint nowTick = (uint)Environment.TickCount;
            uint echo = NetGameLinkEcho.Estimate(session, nowTick);
            byte[] frame = NetGameLink.BuildWithBody(NetGameLink.SysFlagApp, appBody, echo, nowTick);
            string? taggedLabel = label is null
                ? null
                : (server.Logger.IsEnabled(LogLevel.Trace) ? $"{label} hex={HexDump(appBody)}" : label);
            return SendDataAsync(server, ep, session, frame, taggedLabel, ct, reliable: reliable, sendGate: sendGate);
        }

        public static Task<uint> SendSystemBodyAsync(LobbyUdpServer server, IPEndPoint ep, LobbySession session, byte[] appBody, string label, CancellationToken ct)
        {
            uint nowTick = (uint)Environment.TickCount;
            uint echo = NetGameLinkEcho.Estimate(session, nowTick);
            byte[] frame = NetGameLink.BuildWithBody(NetGameLink.SysFlagSystem, appBody, echo, nowTick);
            string taggedLabel = server.Logger.IsEnabled(LogLevel.Trace)
                ? $"{label} hex={HexDump(appBody)}"
                : label;
            return SendDataAsync(server, ep, session, frame, taggedLabel, ct, reliable: true);
        }

        public static Task SendAckOnlyAsync(LobbyUdpServer server, IPEndPoint ep, LobbySession session, CancellationToken ct, string? label = null)
        {
            uint nowTick = (uint)Environment.TickCount;
            uint echo = NetGameLinkEcho.Estimate(session, nowTick);
            byte[] frame = NetGameLink.BuildAckOnly(echo, nowTick);
            return SendDataAsync(server, ep, session, frame, label, ct);
        }

        private static string HexDump(byte[] body)
        {
            int len = Math.Min(body.Length, MaxHexBytes);
            string hex = Convert.ToHexString(body.AsSpan(0, len));
            return body.Length > len ? $"{hex}…(+{body.Length - len})" : hex;
        }
    }
}
