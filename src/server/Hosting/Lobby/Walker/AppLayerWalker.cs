using System.Net;
using Arcadia.EA;
using Arcadia.Hosting.Lobby.Protocol;
using Arcadia.Hosting.Lobby.Walker.Handlers;
using Arcadia.Hosting.Lobby.Wire;
using Microsoft.Extensions.Logging;

// Orchestrates the per-CommUDP-DATA-payload walk over inner Sk8 messages.
namespace Arcadia.Hosting.Lobby.Walker
{
    public static class AppLayerWalker
    {
        public static void Parse(
            LobbyUdpServer server, IPEndPoint ep, LobbySession session,
            byte[] work, int payloadBase, int payloadLen, uint srcBareSeq, uint datagramAck)
        {
            if (session.AlreadyParsedBareSeqs.Contains(srcBareSeq))
            {
                return;
            }

            byte typeByte = work[payloadBase + payloadLen - 1];
            int syncSize = (typeByte & NetGameLink.SyncPresentBit) != 0 ? NetGameLink.SyncTrailerBytes : 0;
            int userBodyLen = payloadLen - 1 - syncSize;

            if (syncSize == NetGameLink.SyncTrailerBytes && payloadLen >= 11)
            {
                int syncBase = payloadBase + userBodyLen;
                session.LastPeerSendTick = NetGameLink.ReadPeerSendTick(
                    work.AsSpan(syncBase, NetGameLink.SyncTrailerBytes));
                session.LocalTickAtLastPeerSend = (uint)Environment.TickCount;
            }

            // Return before dedupe-set add so a later body-bearing retransmit at this seq isn't swallowed.
            if (userBodyLen < 2) return;

            session.AlreadyParsedBareSeqs.Add(srcBareSeq);
            session.AlreadyParsedBareSeqOrder.Enqueue(srcBareSeq);
            if (session.AlreadyParsedBareSeqOrder.Count > LobbySession.AlreadyParsedBareSeqCapacity)
            {
                uint evicted = session.AlreadyParsedBareSeqOrder.Dequeue();
                session.AlreadyParsedBareSeqs.Remove(evicted);
            }

            byte systemFlag = work[payloadBase];
            byte opcodeByte = work[payloadBase + 1];
            session.AppMsgsReceived++;
            session.LastAppMsgRxAt = DateTimeOffset.UtcNow;

            if (SystemFrameDispatcher.TryHandle(server, ep, session,
                    work, payloadBase, userBodyLen, srcBareSeq, systemFlag, opcodeByte))
            {
                return;
            }

            if (systemFlag != NetGameLink.SysFlagApp || userBodyLen < 2)
                return;

            // Walk under RelayLock so the filter check + park insertion is atomic vs reset arms / dict clears.
            lock (session.RelayLock)
            {
                WalkAppMessages(server, ep, session, work, payloadBase, userBodyLen, srcBareSeq, datagramAck);
            }
        }

        private static void WalkAppMessages(
            LobbyUdpServer server, IPEndPoint ep, LobbySession session,
            byte[] work, int payloadBase, int userBodyLen, uint srcBareSeq, uint datagramAck)
        {
            WalkContext ctx = new WalkContext
            {
                Server = server,
                Session = session,
                Ep = ep,
                Work = work,
                PayloadBase = payloadBase,
                UserBodyLen = userBodyLen,
                SrcBareSeq = srcBareSeq,
                DatagramAck = datagramAck,
            };

            while (ctx.MsgOffset < ctx.UserBodyLen && ctx.WalkOk)
            {
                byte op = work[payloadBase + ctx.MsgOffset];

                if (server.Variant == GameVariant.Skate1
                    && op == Sk8Opcodes.Skate1_Broadcast
                    && ctx.MsgOffset + 1 < ctx.UserBodyLen)
                {
                    if (ctx.RelayBuilder is null)
                    {
                        ctx.RelayBuilder = new List<byte>(ctx.UserBodyLen + 8);
                        for (int b = 1; b < ctx.MsgOffset; b++)
                            ctx.RelayBuilder.Add(work[payloadBase + b]);
                    }
                    ctx.RewrapNextAsBroadcasted = true;
                    ctx.MsgOffset += 1;
                    continue;
                }
                if (server.Variant == GameVariant.Skate1
                    && op == Sk8Opcodes.Skate1_Broadcasted
                    && ctx.MsgOffset + 9 < ctx.UserBodyLen)
                {
                    ctx.MsgOffset += 9;
                    continue;
                }

                Sk8Opcodes.Kind? kind = Sk8Opcodes.Decode(server.Variant, op);
                int msgLen;
                bool consumeOnly;
                bool ok = DispatchHandler(ctx, op, kind, out msgLen, out consumeOnly);
                if (!ok) { ctx.WalkOk = false; break; }

                if (!consumeOnly)
                {
                    if (ctx.RelayBuilder is not null)
                    {
                        if (ctx.RewrapNextAsBroadcasted)
                        {
                            ctx.RelayBuilder.Add(Sk8Opcodes.Skate1_Broadcasted);
                            ulong srcUid = (ulong)(session.PlayerInfo?.UID ?? 0L);
                            for (int b = 7; b >= 0; b--)
                                ctx.RelayBuilder.Add((byte)((srcUid >> (b * 8)) & 0xFF));
                        }
                        for (int i = 0; i < msgLen; i++)
                            ctx.RelayBuilder.Add(work[payloadBase + ctx.MsgOffset + i]);
                    }
                }
                else if (ctx.RelayBuilder is null)
                {
                    ctx.RelayBuilder = new List<byte>(ctx.UserBodyLen);
                    for (int b = 1; b < ctx.MsgOffset; b++)
                        ctx.RelayBuilder.Add(work[payloadBase + b]);
                }
                ctx.RewrapNextAsBroadcasted = false;

                ctx.MsgOffset += msgLen;
            }

            if (!ctx.WalkOk)
            {
                byte firstByteCap = server.Variant == GameVariant.Skate1 ? (byte)0x20 : (byte)0x1A;
                byte[] relayBody = work.AsSpan(payloadBase + 1, userBodyLen - 1).ToArray();
                if (relayBody.Length == 0 || relayBody[0] == 0x00 || relayBody[0] >= firstByteCap)
                {
                    server.Logger.LogWarning(
                        "LobbyUdp[{lobby}] DROPPING unsafe relay from {ep} uid={uid} variant={variant}: first byte=0x{mt:X2} (cap=0x{cap:X2})",
                        server.LobbyId, ep, session.PlayerInfo?.UID ?? 0, server.Variant,
                        relayBody.Length == 0 ? (byte)0 : relayBody[0], firstByteCap);
                }
                else
                {
                    ParkRelayBody(server, session, srcBareSeq, relayBody);
                }
                return;
            }

            if (ctx.RelayBuilder is null)
            {
                int bodyLen = userBodyLen - 1;
                if (bodyLen > 0)
                {
                    byte[] relayBody = new byte[bodyLen];
                    Array.Copy(work, payloadBase + 1, relayBody, 0, bodyLen);
                    ParkRelayBody(server, session, srcBareSeq, relayBody);
                }
            }
            else if (ctx.RelayBuilder.Count > 0)
            {
                byte[] rb = ctx.RelayBuilder.ToArray();
                ParkRelayBody(server, session, srcBareSeq, rb);
            }
        }

        private static bool DispatchHandler(WalkContext ctx, byte op, Sk8Opcodes.Kind? kind,
            out int msgLen, out bool consumeOnly)
        {
            msgLen = 0;
            consumeOnly = false;

            switch (kind)
            {
                case Sk8Opcodes.Kind.GameRecipeRequest:
                    return RecipeHandlers.HandleRequest(ctx, out msgLen, out consumeOnly);

                case Sk8Opcodes.Kind.GameRecipeHead when ctx.Server.Variant == GameVariant.Skate2:
                    return RecipeHandlers.HandleHeadSkate2(ctx, out msgLen, out consumeOnly);

                case Sk8Opcodes.Kind.GameRecipeData when ctx.Server.Variant == GameVariant.Skate2:
                    return RecipeHandlers.HandleDataSkate2(ctx, out msgLen, out consumeOnly);

                case Sk8Opcodes.Kind.GameSync when ctx.Server.Variant == GameVariant.Skate1:
                    return GameSyncHandler.HandleSkate1(ctx, out msgLen, out consumeOnly);

                case Sk8Opcodes.Kind.GameSync:
                    return GameSyncHandler.HandleSkate2(ctx, out msgLen, out consumeOnly);

                case Sk8Opcodes.Kind.GameRequest:
                    return GameRequestHandler.Handle(ctx, out msgLen, out consumeOnly);

                case Sk8Opcodes.Kind.GameChallengeLoaded when ctx.Server.Variant == GameVariant.Skate2:
                    return ChallengeLoadedHandler.Handle(ctx, out msgLen, out consumeOnly);

                case Sk8Opcodes.Kind.GameSyncPoint when ctx.Server.Variant == GameVariant.Skate2:
                    return GameSyncPointHandler.Handle(ctx, out msgLen, out consumeOnly);

                case Sk8Opcodes.Kind.GameReset:
                    return GameResetHandler.Handle(ctx, out msgLen, out consumeOnly);

                case Sk8Opcodes.Kind.GameAttributes:
                    return AttributesHandlers.HandleGameAttributes(ctx, out msgLen, out consumeOnly);

                case Sk8Opcodes.Kind.GameResetAttributes:
                    return AttributesHandlers.HandleGameResetAttributes(ctx, out msgLen, out consumeOnly);

                case Sk8Opcodes.Kind.GameAttributeUpdate when ctx.Server.Variant == GameVariant.Skate2:
                    return AttributesHandlers.HandleGameAttributeUpdateSkate2(ctx, out msgLen, out consumeOnly);

                case Sk8Opcodes.Kind.GameRequestChange:
                    return GameChangeHandlers.HandleRequestChange(ctx, out msgLen, out consumeOnly);

                case Sk8Opcodes.Kind.GameFinalResults when ctx.Server.Variant == GameVariant.Skate1:
                    return ResultsHandlers.HandleGameFinalResultsSkate1(ctx, out msgLen, out consumeOnly);

                case Sk8Opcodes.Kind.GameFinalResults:
                    return ResultsHandlers.HandleGameFinalResultsSkate2(ctx, out msgLen, out consumeOnly);

                case Sk8Opcodes.Kind.GameResults:
                    return ResultsHandlers.HandleGameResults(ctx, out msgLen, out consumeOnly);

                case Sk8Opcodes.Kind.GameComplete:
                    return ResultsHandlers.HandleGameComplete(ctx, out msgLen, out consumeOnly);

                default:
                    return FixedFallbackHandler.Handle(ctx, op, kind, out msgLen, out consumeOnly);
            }
        }

        private static void ParkRelayBody(LobbyUdpServer server, LobbySession session, uint srcBareSeq, byte[] body)
        {
            session.OrderedPendingRelayBodies[srcBareSeq] = body;

            while (session.OrderedPendingRelayBodies.Count > LobbySession.OrderedPendingRelayCapacity)
            {
                uint oldest = session.OrderedPendingRelayBodies.First().Key;
                session.OrderedPendingRelayBodies.Remove(oldest);
                session.OrderedPendingRelayDrops++;
                if ((session.OrderedPendingRelayDrops & 0xFF) == 1)
                {
                    server.Logger.LogWarning(
                        "LobbyUdp[{lobby}] PARK-CAP-DROP uid={uid} droppedSrcSeq={k} totalDrops={t}",
                        server.LobbyId, session.PlayerInfo?.UID ?? 0, oldest, session.OrderedPendingRelayDrops);
                }
            }
        }

    }
}
