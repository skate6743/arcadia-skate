using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Arcadia.EA;
using Arcadia.Hosting.Lobby.Challenge;
using Arcadia.Hosting.Lobby.Diagnostics;
using Arcadia.Hosting.Lobby.Flow;
using Arcadia.Hosting.Lobby.Handshake;
using Arcadia.Hosting.Lobby.PlayerEvents;
using Arcadia.Hosting.Lobby.Protocol;
using Arcadia.Hosting.Lobby.Recipe;
using Arcadia.Hosting.Lobby.Relay;
using Arcadia.Hosting.Lobby.Reliability;
using Arcadia.Hosting.Lobby.Reset;
using Arcadia.Hosting.Lobby.Send;
using Arcadia.Hosting.Lobby.Walker;
using Arcadia.Hosting.Lobby.Wire;
using Arcadia.Storage;
using Microsoft.Extensions.Logging;

// Top-level lobby UDP server: owns the listener, sessions, ep processors, and background loops.
namespace Arcadia.Hosting
{
    public class LobbyUdpServer : IAsyncDisposable
    {
        // ===== Constants =====
        private const int NackUpstreamIntervalMs = 50;
        private const int JoinGateMaxHoldMs = 30_000;

        private const int ProactiveRetransmitStuckMs = 500;
        private const int ProactiveRetransmitMinIntervalMs = 500;
        private const int ProactiveRetransmitWindowCap = 16;

        // ===== Public properties (read by Pool / Theater / Fesl) =====
        public int Port => _port;
        public int LobbyId => Game.LobbyId;
        public long Gid => Game.GID;
        public GameVariant Variant => Game.Variant;
        public CancellationToken CancellationToken => _cts.Token;

        // ===== Shared state (read by Lobby/ modules) =====
        public readonly GameServerListing Game;
        public readonly ILogger Logger;
        public readonly UdpClient Listener;
        public readonly byte[] EKey;
        public readonly UdpSessionCache UdpCache;
        public readonly RecipeBlobStore Blobs;
        public readonly ConcurrentDictionary<IPEndPoint, LobbySession> Sessions = new ConcurrentDictionary<IPEndPoint, LobbySession>();
        public readonly ConcurrentDictionary<long, RecipeAssembler> RecipeAssemblers = new ConcurrentDictionary<long, RecipeAssembler>();
        public readonly PingEventLog PingEvents;
        public readonly LobbyDiagnostics Diagnostics;

        // ===== ResetWatchdog state =====
        public readonly object RwLock = new object();
        public bool RwArmed;
        public int RwGeneration;
        public int RwAttempts;
        public DateTimeOffset RwArmedAt;
        public bool RwResumed;
        public DateTimeOffset RwResumedAt;
        public DateTimeOffset RwLastProgressAt;
        public long RwLastGsSum;
        public string RwLabel = "";
        public Func<CancellationToken, Task>? RwReBroadcast;
        public Dictionary<long, long> RwBaselines = new Dictionary<long, long>();
        public bool RwRetryInFlight;
        public bool RwAllowDuringInProgress;

        // ===== ChallengeFlow state =====
        public readonly object ChallengeLock = new object();
        public readonly HashSet<long> ChallengeReporters = new HashSet<long>();
        public bool ChallengeAwaitingReady;
        public bool ChallengeReadyFired;

        // ===== Map-change + post-challenge in-flight gates =====
        public int MapChangeInProgress;
        public int PostChallengeBroadcastInProgress;

        // ===== Reset gate epoch (bumped by ResetGate.Close before any reset send) =====
        public int ResetEpoch;

        // ===== Post-challenge results aggregation (EventTime = raw wire bytes: S1 u32, S2 f32 bits) =====
        public readonly ConcurrentDictionary<long, (uint EventTime, int Score, int FinishReason, int Ranking, bool PlayersChoice)> PeerGameResults
            = new ConcurrentDictionary<long, (uint, int, int, int, bool)>();

        // ===== Private state =====
        private readonly int _port;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly ConcurrentDictionary<IPEndPoint, EpProcessor> _epProcessors = new ConcurrentDictionary<IPEndPoint, EpProcessor>();
        private readonly SemaphoreSlim _joinAdmissionGate = new SemaphoreSlim(1, 1);

        // Single-writer = recv loop, single-reader = runner task.
        private class EpProcessor
        {
            public required Channel<UdpReceiveResult> Channel;
            public Task? RunnerTask;
        }

        public LobbyUdpServer(
            ILogger logger,
            GameServerListing game,
            IPAddress bindAddress,
            int port,
            string ekey,
            UdpSessionCache udpCache,
            RecipeBlobStore blobs,
            bool pingEventFileLogging)
        {
            Logger = logger;
            Game = game;
            UdpCache = udpCache;
            Blobs = blobs;
            EKey = Encoding.ASCII.GetBytes(ekey);
            _port = port;

            Listener = new UdpClient(new IPEndPoint(bindAddress, port));
            Listener.Client.ReceiveBufferSize = 1 << 20;
            Listener.Client.SendBufferSize = 1 << 20;

            PingEvents = new PingEventLog(logger, pingEventFileLogging);
            Diagnostics = new LobbyDiagnostics(Sessions, Game.LobbyId, logger);
        }

        public void Start()
        {
            Logger.LogInformation(
                "LobbyUdp[{lobby}:{gid}] listening on port {port} variant={variant}",
                Game.LobbyId, Game.GID, _port, Game.Variant);
            ThreadPool.QueueUserWorkItem(_ => ReceiveLoop(_cts.Token));
            ThreadPool.QueueUserWorkItem(_ => _ = Diagnostics.RunSnapshotLoopAsync(_cts.Token));
            ThreadPool.QueueUserWorkItem(_ => _ = RunSilentPeerWatchdogAsync(_cts.Token));
            ThreadPool.QueueUserWorkItem(_ => _ = ResetWatchdog.RunLoopAsync(this, _cts.Token));
            ThreadPool.QueueUserWorkItem(_ => _ = RunRecipeAssemblerSweepAsync(_cts.Token));
            ThreadPool.QueueUserWorkItem(_ => _ = RunDelayedAckLoopAsync(_cts.Token));
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            Listener.Dispose();
            foreach (var kv in _epProcessors)
                kv.Value.Channel.Writer.TryComplete();
            await Task.CompletedTask;
        }

        // ===== Receive loop =====

        private async void ReceiveLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try { result = await Listener.ReceiveAsync(ct); }
                catch (OperationCanceledException) { return; }
                catch (Exception e)
                {
                    Logger.LogError(e, "LobbyUdp[{lobby}] receive error", Game.LobbyId);
                    continue;
                }

                bool hasSession = Sessions.TryGetValue(result.RemoteEndPoint, out LobbySession? preGateSession);
                if (hasSession)
                {
                    preGateSession!.LastPacketRxAt = DateTimeOffset.UtcNow;
                }
                else if (!UdpCache.IsAuthorized(result.RemoteEndPoint.Address))
                {
                    continue;
                }

                EpProcessor processor = _epProcessors.GetOrAdd(result.RemoteEndPoint, CreateEpProcessor);
                try
                {
                    await processor.Channel.Writer.WriteAsync(result, ct);
                }
                catch (ChannelClosedException) { }
            }
        }

        private EpProcessor CreateEpProcessor(IPEndPoint ep)
        {
            Channel<UdpReceiveResult> channel = Channel.CreateUnbounded<UdpReceiveResult>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
            });
            EpProcessor processor = new EpProcessor { Channel = channel };
            processor.RunnerTask = Task.Run(async () =>
            {
                CancellationToken ct = _cts.Token;
                try
                {
                    await foreach (UdpReceiveResult result in channel.Reader.ReadAllAsync(ct))
                    {
                        try { await HandleDatagramAsync(result, ct); }
                        catch (OperationCanceledException) { return; }
                        catch (Exception e)
                        {
                            Logger.LogError(e, "LobbyUdp[{lobby}] datagram handler error from {ep}", Game.LobbyId, ep);
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception e)
                {
                    Logger.LogError(e, "LobbyUdp[{lobby}] EP processor crashed for {ep}", Game.LobbyId, ep);
                }
            });
            return processor;
        }

        private async Task HandleDatagramAsync(UdpReceiveResult result, CancellationToken ct)
        {
            byte[] buf = result.Buffer;
            IPEndPoint ep = result.RemoteEndPoint;

            if (CommUdpFrame.LooksLikePlainControl(buf))
            {
                await HandlePlainControlAsync(ep, buf, ct);
                return;
            }

            if (buf.Length >= ProtoTunnelCodec.CounterBytes + ProtoTunnelCodec.TupleHeaderBytes)
            {
                await HandleProtoTunnelAsync(ep, buf, ct);
                return;
            }

            Logger.LogWarning("LobbyUdp[{lobby}] unrecognised packet from {ep} len={len}",
                Game.LobbyId, ep, buf.Length);
        }

        private async Task HandlePlainControlAsync(IPEndPoint ep, byte[] buf, CancellationToken ct)
        {
            CommUdpKind kind = (CommUdpKind)BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(0, 4));
            uint ident = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(4, 4));

            if (kind == CommUdpKind.Disconnect)
            {
                if (Sessions.TryRemove(ep, out LobbySession? removed))
                {
                    Logger.LogInformation("LobbyUdp[{lobby}] plain DISC: {ep}", Game.LobbyId, ep);
                    await RemoveAndAnnounceLeaveAsync(ep, removed, "plain-DISC", ct);
                }
                return;
            }

            if (kind != CommUdpKind.Connect && kind != CommUdpKind.Poke) return;

            LobbySession plainSession = Sessions.GetOrAdd(ep, _ => new LobbySession { Envelope = EnvelopeKind.Plain, ClientIdent = ident });
            plainSession.LastPacketRxAt = DateTimeOffset.UtcNow;

            byte[] reply = new byte[CommUdpFrame.PlainAckOnlyLength];
            BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(0, 4), (uint)CommUdpKind.ConnAck);
            BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(4, 4), ident);
            await Listener.SendAsync(reply, reply.Length, ep);
        }

        // ===== Per-encrypted-datagram pipeline =====

        private async Task HandleProtoTunnelAsync(IPEndPoint ep, byte[] buf, CancellationToken ct)
        {
            LobbySession session = Sessions.GetOrAdd(ep, _ =>
            {
                LobbySession s = new LobbySession { Envelope = EnvelopeKind.ProtoTunnel };
                s.RecvStream.Init(EKey);
                s.SendStream.Init(EKey);
                return s;
            });
            if (!session.RecvStream.Initialized) session.RecvStream.Init(EKey);
            if (!session.SendStream.Initialized) session.SendStream.Init(EKey);
            session.LastPacketRxAt = DateTimeOffset.UtcNow;

            ushort counter = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(0, 2));
            uint prevHighWord = session.RecvStream.HighWord;

            ProtoTunnelCodec.DecodedFrame? decoded = ProtoTunnelCodec.TryDecode(buf, EKey, session.RecvStream, Variant);
            if (decoded is null)
            {
                Logger.LogWarning("LobbyUdp[{lobby}] PT decode failed from {ep} len={len}", Game.LobbyId, ep, buf.Length);
                return;
            }

            if (decoded.WrapForwardOccurred)
            {
                Logger.LogInformation(
                    "LobbyUdp[{lobby}] PROTOTUNNEL-WRAP-IN ep={ep} uid={uid} newHigh={hi} wireCounter=0x{c:X4} effective=0x{ec:X8}",
                    Game.LobbyId, ep, session.PlayerInfo?.UID ?? 0,
                    session.RecvStream.HighWord, counter, decoded.EffectiveCounter);
            }

            if (decoded.TupleSumMismatch)
            {
                session.SecondaryTunnelPacketCount++;
                if (session.SecondaryTunnelPacketCount == 1)
                    Logger.LogInformation("LobbyUdp[{lobby}] secondary tunnel detected from {ep} (dropping)", Game.LobbyId, ep);
                return;
            }

            ProtoTunnelCodec.TunnelTuple? firstCommUdpTuple = null;
            foreach (ProtoTunnelCodec.TunnelTuple t in decoded.Tuples)
            {
                if (t.Idx == ProtoTunnelCodec.PreambleTunnelIdx && t.Length == ProtoTunnelCodec.PreambleLength) continue;
                if (!ProtoTunnelCodec.IsCommUdpTunnel(Variant, t.Idx)) continue;
                firstCommUdpTuple = t;
                break;
            }
            if (firstCommUdpTuple is null) return;

            ProtoTunnelCodec.TunnelTuple first = firstCommUdpTuple.Value;
            if (first.Length < CommUdpFrame.HeaderBytes || first.Offset + first.Length > decoded.Work.Length) return;

            uint w0 = BinaryPrimitives.ReadUInt32BigEndian(decoded.Work.AsSpan(first.Offset, 4));
            uint w1 = BinaryPrimitives.ReadUInt32BigEndian(decoded.Work.AsSpan(first.Offset + 4, 4));

            session.TunnelIdx = first.Idx;
            if ((CommUdpKind)w0 == CommUdpKind.Connect) session.ClientIdent = w1;
            if (session.ClientIdent == 0 && (CommUdpKind)w0 != CommUdpKind.Connect) session.ClientIdent = w1;
            if (decoded.Preamble is not null) session.PreambleBytes = decoded.Preamble;

            bool sawDataOrPing = false;
            bool sessionAlive = true;

            foreach (ProtoTunnelCodec.TunnelTuple t in decoded.Tuples)
            {
                if (t.Idx == ProtoTunnelCodec.PreambleTunnelIdx && t.Length == ProtoTunnelCodec.PreambleLength) continue;
                if (!ProtoTunnelCodec.IsCommUdpTunnel(Variant, t.Idx)) continue;
                if (!sessionAlive) break;
                if (t.Length < CommUdpFrame.HeaderBytes || t.Offset + t.Length > decoded.Work.Length) continue;

                uint tW0 = BinaryPrimitives.ReadUInt32BigEndian(decoded.Work.AsSpan(t.Offset, 4));
                uint tW1 = BinaryPrimitives.ReadUInt32BigEndian(decoded.Work.AsSpan(t.Offset + 4, 4));

                if (t.Length == 12 && (CommUdpKind)tW0 is CommUdpKind.Connect or CommUdpKind.Poke)
                {
                    Logger.LogInformation(
                        "LobbyUdp[{lobby}] RX {kind} from {ep} ident=0x{id:X8} counter=0x{c:X4}",
                        Game.LobbyId, (CommUdpKind)tW0, ep, tW1, counter);
                    await EncryptedSender.SendConnAckAsync(this, ep, session, tW1, ct);
                }
                else if (t.Length == CommUdpFrame.PlainAckOnlyLength && (CommUdpKind)tW0 == CommUdpKind.Disconnect)
                {
                    if (Sessions.TryRemove(ep, out LobbySession? removed))
                    {
                        Logger.LogInformation("LobbyUdp[{lobby}] PT DISC: {ep}", Game.LobbyId, ep);
                        await RemoveAndAnnounceLeaveAsync(ep, removed, "PT-DISC", ct);
                    }
                    sessionAlive = false;
                }
                else if (t.Length == CommUdpFrame.PlainAckOnlyLength && (CommUdpKind)tW0 == CommUdpKind.PingRetransmitRequest)
                {
                    session.ClientRequestedSeq = tW1;
                    // kind=4 carries expected-next: everything below it is acked.
                    if (tW1 > 0 && tW1 - 1 > session.LastAckFromClient)
                    {
                        session.LastAckFromClient = tW1 - 1;
                        session.LastAckFromClientAdvancedAt = DateTimeOffset.UtcNow;
                    }
                    if (tW1 > 0) session.LastAckFromClientInitialized = true;
                    sawDataOrPing = true;

                    uint lag = session.ServerDataSeq > tW1 ? session.ServerDataSeq - tW1 : 0u;
                    PingEvents.Append(string.Format(CultureInfo.InvariantCulture,
                        "[{0:O}] PING-RX lobby={1} ep={2} uid={3} expected-seq={4} ourServerDataSeq={5} lag={6}\n",
                        DateTimeOffset.UtcNow, Game.LobbyId, ep,
                        session.PlayerInfo?.UID ?? 0, tW1, session.ServerDataSeq, lag));
                }
                else if (t.Length >= CommUdpFrame.HeaderBytes)
                {
                    if (!session.ServerSeqInitialized)
                    {
                        session.ServerDataSeq = tW1 + 1;
                        session.ServerSeqInitialized = true;
                    }

                    // Monotonic max: reordered datagrams may carry an older ack.
                    if (tW1 > session.LastAckFromClient)
                    {
                        session.LastAckFromClient = tW1;
                        session.LastAckFromClientAdvancedAt = DateTimeOffset.UtcNow;
                    }
                    session.LastAckFromClientInitialized = true;

                    uint bareSeq = CommUdpFrame.BareSeq(tW0);
                    int subCount = CommUdpFrame.SubCount(tW0);
                    int payloadLen = t.Length - CommUdpFrame.HeaderBytes;
                    int payloadBase = t.Offset + CommUdpFrame.HeaderBytes;

                    // advance+park must be atomic vs the drain, or a late-parked body is reorder-dropped and wedges
                    lock (session.RelayLock)
                    {
                        if (subCount == 0)
                        {
                            InboundAckTracker.RecordInboundSeq(session, bareSeq, ep, Logger, Game.LobbyId, hasAppPayload: payloadLen >= 3);
                            if (payloadLen >= 3)
                                AppLayerWalker.Parse(this, ep, session, decoded.Work, payloadBase, payloadLen, bareSeq, tW1);
                        }
                        else
                        {
                            if (CommUdpFrame.TrySplitBundle(decoded.Work.AsSpan(payloadBase, payloadLen), subCount, out List<(int Start, int Length)> subs))
                            {
                                for (int i = 0; i < subs.Count; i++)
                                {
                                    uint effectiveSeq = bareSeq - (uint)i;
                                    var (subStart, subLen) = subs[i];
                                    InboundAckTracker.RecordInboundSeq(session, effectiveSeq, ep, Logger, Game.LobbyId, hasAppPayload: subLen >= 3);
                                    if (subLen >= 3)
                                        AppLayerWalker.Parse(this, ep, session, decoded.Work, payloadBase + subStart, subLen, effectiveSeq, tW1);
                                }
                            }
                            else
                            {
                                Logger.LogWarning(
                                    "LobbyUdp[{lobby}] malformed bundle from {ep} subCount={s} payloadLen={p}",
                                    Game.LobbyId, ep, subCount, payloadLen);
                            }
                        }
                    }
                    sawDataOrPing = true;
                }
            }

            if (!sessionAlive) return;

            await ProcessTailAsync(ep, session, sawDataOrPing, ct);
        }

        private async Task ProcessTailAsync(IPEndPoint ep, LobbySession session, bool sawDataOrPing, CancellationToken ct)
        {
            if (session.ClientRequestedSeq > 0
                && session.ClientRequestedSeq < session.ServerDataSeq)
            {
                PingRecovery.StartCatchUp(this, ep, session, ct);
                if (session.Stage == AppStage.GameRecipeRequestSent)
                    sawDataOrPing = false;
            }

            bool holdAckForGap = session.ClientOooSeqs.Count > 0 && !HandshakeFlow.NeedsDrive(session);

            if (sawDataOrPing && !holdAckForGap)
            {
                if (HandshakeFlow.NeedsDrive(session))
                {
                    await AcquireJoinGateIfJoinerAsync(session, ct);
                    await HandshakeFlow.DriveAsync(this, ep, session, ct);
                }
            }

            if (session.WaitingForFirstFrameAfterReset
                && session.WaitingForFirstFrameAfterResetSetAt is { } ffSetAt
                && (DateTimeOffset.UtcNow - ffSetAt).TotalSeconds >= LobbySession.WaitFirstFrameForceClearSeconds)
            {
                bool forceCleared = false;
                lock (session.RelayLock)
                {
                    if (session.WaitingForFirstFrameAfterReset
                        && session.WaitingForFirstFrameAfterResetSetAt is { } ffSetAt2
                        && (DateTimeOffset.UtcNow - ffSetAt2).TotalSeconds >= LobbySession.WaitFirstFrameForceClearSeconds)
                    {
                        session.WaitingForFirstFrameAfterReset = false;
                        session.WaitingForFirstFrameAfterResetSetAt = null;
                        forceCleared = true;
                    }
                }
                if (forceCleared)
                {
                    Logger.LogWarning(
                        "LobbyUdp[{lobby}] WAIT-FF-FORCECLEAR(timer) ep={ep} uid={uid} stage={stage} — frame=1 GameSync not seen {s}s after reset",
                        Game.LobbyId, ep, session.PlayerInfo?.UID ?? 0, session.Stage,
                        LobbySession.WaitFirstFrameForceClearSeconds);
                }
            }

            if (!session.WaitingForFirstFrameAfterReset
                && session.PendingForResetReleaseToDst.Count > 0)
            {
                await RelayPipeline.DrainResetGateReleaseAsync(this, ep, session, ct);
            }

            while (session.PendingRecipeRequests.TryDequeue(out long requestedPeer))
            {
                await RecipeService.RespondAsync(this, ep, session, requestedPeer, ct);
            }

            await RelayPipeline.DrainAsync(this, ep, session, ct);

            if (session.PendingJoinBroadcast)
            {
                await JoinFinalizeFlow.FireAsync(this, ep, session, ct);
            }

            if (!session.VoipDisableBroadcasted && session.Stage >= AppStage.JoinCompleteSent)
            {
                session.VoipDisableBroadcasted = true;
                await PlayerEventBroadcaster.BroadcastVoipDisabledForAllAsync(this, ct);
            }

            if (session.ClientOooSeqs.Count > 0 && session.ClientAckInitialized)
            {
                uint firstMissing = session.ClientAckSeq + 1;
                DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
                double sinceLastNackMs = session.LastNackUpstreamAt.HasValue
                    ? (nowUtc - session.LastNackUpstreamAt.Value).TotalMilliseconds
                    : double.MaxValue;
                if (sinceLastNackMs >= NackUpstreamIntervalMs)
                {
                    session.LastNackUpstreamAt = nowUtc;
                    session.LastNackedSeq = firstMissing;
                    session.NacksSent++;
                    await EncryptedSender.SendPingRetransmitAsync(this, ep, session, firstMissing, ct);
                }

                if (session.OooGapOpenedAt.HasValue
                    && session.OooGapHeadAtOpen == firstMissing
                    && (nowUtc - session.OooGapOpenedAt.Value).TotalSeconds >= LobbySession.OooGapForceSkipAfterSeconds
                    && session.ClientOooSeqs.Count > 0)
                {
                    uint lowestOoo = session.ClientOooSeqs.Min;
                    uint skippedFrom = session.ClientAckSeq;
                    session.ClientAckSeq = lowestOoo;
                    session.ClientOooSeqs.Remove(lowestOoo);
                    int drained = 1;
                    while (session.ClientOooSeqs.Count > 0
                        && session.ClientOooSeqs.Min == session.ClientAckSeq + 1)
                    {
                        uint next = session.ClientOooSeqs.Min;
                        session.ClientOooSeqs.Remove(next);
                        session.ClientAckSeq = next;
                        drained++;
                    }
                    session.OooGapForceSkips++;

                    Interlocked.Exchange(ref session.RecoveryExhaustionForceSkipPending, 1);
                    if (session.ClientOooSeqs.Count == 0)
                    {
                        session.OooGapOpenedAt = null;
                        session.OooGapHeadAtOpen = 0;
                    }
                    else
                    {
                        session.OooGapOpenedAt = nowUtc;
                        session.OooGapHeadAtOpen = session.ClientAckSeq + 1;
                    }
                    Logger.LogWarning(
                        "LobbyUdp[{lobby}] OOO-FORCE-SKIP ep={ep} uid={uid} skippedFromAck={from} newAck={na} drained={d}",
                        Game.LobbyId, ep, session.PlayerInfo?.UID ?? 0,
                        skippedFrom, session.ClientAckSeq, drained);
                }
            }

            if (Interlocked.Exchange(ref session.RecoveryExhaustionForceSkipPending, 0) == 1
                && session.PlayerInfo is not null
                && session.Stage >= AppStage.JoinCompleteSent)
            {
                Logger.LogWarning(
                    "LobbyUdp[{lobby}] OOO-FORCE-SKIP-KICK ep={ep} uid={uid}",
                    Game.LobbyId, ep, session.PlayerInfo.UID);
                if (Sessions.TryRemove(ep, out LobbySession? removed))
                {
                    await RemoveAndAnnounceLeaveAsync(ep, removed, "ooo-force-skip", ct);
                }
                return;
            }

            Diagnostics.MaybeEmitTally(ep, session);
        }

        // ===== Walker callbacks (invoked from Walker/Handlers/* via the server reference) =====

        public void OnHelloReceived(IPEndPoint ep, LobbySession session)
        {
            session.PlayerInfo ??= UdpCache.Resolve(ep);
            if (session.PlayerInfo is not null)
                OnPeerJoinedLobby(session);
        }

        public void OnStartGameRequested(LobbySession source) => ChallengeFlow.MaybeStart(this, source);

        public void OnChallengeLoadedReport(long uid) => ChallengeFlow.TrackChallengeLoadedReport(this, uid);

        public void OnMapChangeRequested(int reqType, ulong reqKey)
            => _ = MapChangeFlow.RunAsync(this, reqType, reqKey, _cts.Token);

        public void OnHostKickRequested(int destRef)
            => _ = HostLeaveFlow.HandleHostKickAsync(this, destRef, _cts.Token);

        public void OnAttributeUpdate(Sk8AttributeType attr, string value)
        {
            if (attr != Sk8AttributeType.IsPrivate) return;

            string v = value.Trim();
            bool isPrivate = v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1";
            bool prior = Game.IsPrivate;
            Game.IsPrivate = isPrivate;
            Game.Data["B-U-is_private"] = isPrivate ? "true" : "false";
            Logger.LogInformation(
                "LobbyUdp[{lobby}] HOST-ACTION is_private {prior}→{now} (raw=\"{raw}\") gid={gid}",
                Game.LobbyId, prior, isPrivate, value, Game.GID);

            if (Variant == GameVariant.Skate2)
            {
                byte[] body = Sk8MessagePackets.BuildGameAttributeUpdate(Sk8AttributeType.IsPrivate,
                    isPrivate ? "true" : "false");
                _ = ResetBroadcaster.BroadcastSk8BodyAsync(this, body,
                    $"MT_GameAttributeUpdate(IsPrivate={isPrivate},host-action)", _cts.Token);
            }
        }

        public void OnGameCompleteFromPeer(long uid)
        {
            // only a live challenge may enter post-challenge; a stray/late GameComplete would fire scoreboard+reset
            if (!Game.InProgress || Volatile.Read(ref MapChangeInProgress) != 0) return;
            if (Interlocked.CompareExchange(ref PostChallengeBroadcastInProgress, 1, 0) == 0)
            {
                _ = PostChallengeFlow.RunAsync(this, _cts.Token);
            }
        }

        public void OnGameResultsFromPeer(long uid, uint eventTime, int score, int finishReason, int ranking, bool playersChoice)
            => PeerGameResults[uid] = (eventTime, score, finishReason, ranking, playersChoice);

        // ===== Peer-ready + roster helpers =====

        public bool IsPeerReady(long uid)
        {
            if (uid == Game.UID) return true;
            foreach (LobbySession s in Sessions.Values)
                if (s.PlayerInfo?.UID == uid && s.Stage >= AppStage.JoinCompleteSent)
                    return true;
            return false;
        }

        private void OnPeerJoinedLobby(LobbySession session)
        {
            if (session.PlayerInfo is null) return;

            if (!Game.HasConnectedPlayer(session.PlayerInfo.UID))
            {
                while (Game.TryPromoteJoining(out PlasmaSession? promoted))
                    if (promoted?.UID == session.PlayerInfo.UID) break;
            }
        }

        // ===== Per-lobby join-admission gate =====

        private async Task AcquireJoinGateIfJoinerAsync(LobbySession session, CancellationToken ct)
        {
            Storage.UdpSessionCache.ClientInfo? info = session.PlayerInfo;
            if (info is null || info.UID == Game.UID) return;

            if (Interlocked.CompareExchange(ref session.JoinGateState, 1, 0) != 0) return;

            try
            {
                await _joinAdmissionGate.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Exchange(ref session.JoinGateState, 3);
                throw;
            }

            Interlocked.Exchange(ref session.JoinGateState, 2);
            Logger.LogInformation(
                "LobbyUdp[{lobby}] join-gate ACQUIRED uid={uid} ({name})",
                Game.LobbyId, info.UID, info.Name);

            _ = Task.Run(async () =>
            {
                try { await Task.Delay(JoinGateMaxHoldMs, ct); }
                catch (OperationCanceledException) { return; }
                ReleaseJoinGate(session, "30s-backstop");
            });
        }

        public void ReleaseJoinGate(LobbySession session, string reason)
        {
            if (Interlocked.CompareExchange(ref session.JoinGateState, 3, 2) != 2) return;
            _joinAdmissionGate.Release();
            Logger.LogInformation(
                "LobbyUdp[{lobby}] join-gate RELEASED ({reason}) uid={uid}",
                Game.LobbyId, reason, session.PlayerInfo?.UID ?? 0);
        }

        // ===== Joiner-wait helper used by map-change + challenge-start =====

        public async Task WaitForJoinersAsync(string context, int maxWaitSeconds, CancellationToken ct)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(maxWaitSeconds);
            int loggedPending = -1;
            while (true)
            {
                int pending = Game.JoiningCount;
                foreach (LobbySession s in Sessions.Values)
                {
                    if (s.PlayerInfo is not null && s.PlayerInfo.UID == Game.UID) continue;
                    if (!s.JoinBroadcasted || s.PostJoinResetPending) pending++;
                }
                if (pending == 0) return;

                if (pending != loggedPending)
                {
                    Logger.LogInformation(
                        "LobbyUdp[{lobby}] {ctx}: waiting for {n} joiner(s) to finish handshake",
                        Game.LobbyId, context, pending);
                    loggedPending = pending;
                }

                if (DateTime.UtcNow >= deadline)
                {
                    Logger.LogWarning(
                        "LobbyUdp[{lobby}] {ctx}: joiner-wait timed out after {s}s with {n} still pending",
                        Game.LobbyId, context, maxWaitSeconds, pending);
                    return;
                }

                try { await Task.Delay(200, ct); }
                catch (OperationCanceledException) { return; }
            }
        }

        // ===== Session removal + leave broadcast =====

        public async Task RemoveAndAnnounceLeaveAsync(IPEndPoint ep, LobbySession removed, string reason, CancellationToken ct)
        {
            ReleaseJoinGate(removed, $"session-removed:{reason}");
            UdpCache.ReleaseClaim(ep);

            TimeSpan ago = DateTimeOffset.UtcNow - removed.LastPacketRxAt;
            double elapsed = (DateTimeOffset.UtcNow - removed.SessionStart).TotalSeconds;
            string kickLine = string.Format(CultureInfo.InvariantCulture,
                "[{0:O}] KICK lobby={1} ep={2} uid={3} name={4} reason={5} stage={6} elapsedSec={7:F1} lastRxAgoMs={8} appMsgs={9} gs={10} retx={11} oooOpen={12} oooClose={13} cmiss={14} nacks={15} clientAck={16} sendSeq={17} oooSize={18}\n",
                DateTimeOffset.UtcNow, Game.LobbyId, ep,
                removed.PlayerInfo?.UID ?? 0,
                removed.PlayerInfo?.Name ?? "(none)",
                reason, removed.Stage, elapsed, (long)ago.TotalMilliseconds,
                removed.AppMsgsReceived, removed.GameSyncsReceived, removed.RetransmitsSeen,
                removed.OooGapOpens, removed.OooGapCloses, removed.CatchupCacheMisses,
                removed.NacksSent, removed.ClientAckSeq, removed.ServerDataSeq,
                removed.ClientOooSeqs.Count);
            KickEventLog.Append(kickLine, Logger);

            Diagnostics.LogSessionClose(ep, removed, reason);

            bool hostLeft = removed.PlayerInfo is not null
                           && removed.PlayerInfo.UID == Game.UID;

            if (removed.PlayerInfo is not null)
            {
                try
                {
                    if (hostLeft)
                    {
                        await HostLeaveFlow.BroadcastHostLeaveDissolveAsync(this, reason, ct);
                    }
                    else
                    {
                        // S2: leaver's last lockstep frame lets peers drain his queue and drop him cleanly, no reset
                        uint lastFrame = Variant == GameVariant.Skate2 ? removed.LockstepFrame : uint.MaxValue;
                        byte[] body = Sk8MessagePackets.BuildGameRemovePlayer(
                            Variant, removed.PlayerInfo.UID, lastFrame);
                        await ResetBroadcaster.BroadcastSk8BodyAsync(this, body,
                            $"MT_GameRemovePlayer(uid={removed.PlayerInfo.UID},mLastFrame={lastFrame},reason={reason})", ct);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception e)
                {
                    Logger.LogWarning(e,
                        "LobbyUdp[{lobby}] {reason} pre-leave broadcast failed",
                        Game.LobbyId, reason);
                }
                Game.ReleaseSlot(removed.PlayerInfo.UID);
            }

            if (_epProcessors.TryRemove(ep, out EpProcessor? removedProcessor))
            {
                removedProcessor.Channel.Writer.TryComplete();
            }

            try { await Task.Delay(200, ct); }
            catch (OperationCanceledException) { return; }

            if (removed.PlayerInfo is not null && !hostLeft)
            {
                try { await PlayerEventBroadcaster.BroadcastLeftAsync(this, removed.PlayerInfo, ct); }
                catch (OperationCanceledException) { return; }
                catch (Exception e)
                {
                    Logger.LogWarning(e,
                        "LobbyUdp[{lobby}] {reason} post-RemovePlayer PLAYER_LEFT for {ep} failed",
                        Game.LobbyId, reason, ep);
                }
            }

            ChallengeFlow.OnPeerLeft(this);
        }

        // ===== Background loops =====

        private async Task RunDelayedAckLoopAsync(CancellationToken ct)
        {
            TimeSpan tickInterval = TimeSpan.FromMilliseconds(25);
            TimeSpan threshold = TimeSpan.FromMilliseconds(LobbySession.DelayedAckIdleThresholdMs);
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(tickInterval, ct); }
                catch (OperationCanceledException) { return; }

                DateTimeOffset now = DateTimeOffset.UtcNow;
                foreach (var kv in Sessions)
                {
                    IPEndPoint ep = kv.Key;
                    LobbySession session = kv.Value;
                    if (session.Stage < AppStage.JoinCompleteSent) continue;
                    if (session.PlayerInfo is null) continue;
                    if (session.Envelope != EnvelopeKind.ProtoTunnel) continue;
                    if (session.PreambleBytes is null) continue;

                    // runs FIRST: the OOO/idle skips below would otherwise strand a lost one-shot
                    try { await MaybeProactiveRetransmitAsync(ep, session, now, ct); }
                    catch (OperationCanceledException) { return; }
                    catch (Exception e)
                    {
                        Logger.LogWarning(e,
                            "LobbyUdp[{lobby}] proactive-retransmit to {ep} uid={uid} failed",
                            Game.LobbyId, ep, session.PlayerInfo?.UID ?? 0);
                    }

                    if (session.ClientOooSeqs.Count > 0) continue;
                    if (now - session.LastOutboundAt < threshold) continue;

                    try
                    {
                        await EncryptedSender.SendAckOnlyAsync(this, ep, session, ct);
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception e)
                    {
                        Logger.LogWarning(e,
                            "LobbyUdp[{lobby}] delayed-ack to {ep} uid={uid} failed",
                            Game.LobbyId, ep, session.PlayerInfo?.UID ?? 0);
                    }
                }
            }
        }

        private async Task MaybeProactiveRetransmitAsync(IPEndPoint ep, LobbySession session, DateTimeOffset now, CancellationToken ct)
        {
            if (!session.LastAckFromClientInitialized) return;

            // snapshot under SendLock; re-push outside the lock
            List<uint>? toResend = null;
            lock (session.SendLock)
            {
                uint lastAck = session.LastAckFromClient;
                while (session.ReliableUnackedSeqs.Count > 0 && session.ReliableUnackedSeqs.Min <= lastAck)
                    session.ReliableUnackedSeqs.Remove(session.ReliableUnackedSeqs.Min);

                if (session.ReliableUnackedSeqs.Count > 0
                    && (now - session.LastAckFromClientAdvancedAt).TotalMilliseconds >= ProactiveRetransmitStuckMs
                    && (now - session.LastProactiveRetransmitAt).TotalMilliseconds >= ProactiveRetransmitMinIntervalMs)
                {
                    session.LastProactiveRetransmitAt = now;
                    toResend = new List<uint>(Math.Min(session.ReliableUnackedSeqs.Count, ProactiveRetransmitWindowCap));
                    foreach (uint seq in session.ReliableUnackedSeqs)
                    {
                        toResend.Add(seq);
                        if (toResend.Count >= ProactiveRetransmitWindowCap) break;
                    }
                }
            }

            if (toResend is null) return;

            int resent = 0;
            foreach (uint seq in toResend)
            {
                if (!session.SentFrameCache.TryGetValue(seq, out byte[]? body)) continue;
                await EncryptedSender.SendDataAsync(this, ep, session, body, null, ct, explicitSeq: seq);
                resent++;
            }

            if (resent > 0)
            {
                Logger.LogWarning(
                    "LobbyUdp[{lobby}] PROACTIVE-RETX(control) ep={ep} uid={uid} resent={n} seqs=[{seqs}] lastAck={ack} sendSeq={ss} stuckMs={ms:F0}",
                    Game.LobbyId, ep, session.PlayerInfo?.UID ?? 0, resent, string.Join(",", toResend),
                    session.LastAckFromClient, session.ServerDataSeq, (now - session.LastAckFromClientAdvancedAt).TotalMilliseconds);
            }
        }

        private async Task RunRecipeAssemblerSweepAsync(CancellationToken ct)
        {
            const int sweepIntervalSec = 5;
            const int staleThresholdSec = 30;

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(sweepIntervalSec), ct);

                    DateTimeOffset now = DateTimeOffset.UtcNow;
                    foreach (var kv in RecipeAssemblers)
                    {
                        double ageSec = (now - kv.Value.LastChunkAt).TotalSeconds;
                        if (ageSec >= staleThresholdSec)
                        {
                            if (RecipeAssemblers.TryRemove(kv.Key, out RecipeAssembler? dropped))
                            {
                                double totalAgeSec = (now - dropped.StartedAt).TotalSeconds;
                                Logger.LogWarning(
                                    "LobbyUdp[{lobby}] STALE-RECIPE-ASSEMBLER peer={peer} startedAgoSec={startAgo:F1} lastChunkAgoSec={chunkAgo:F1} received={r}/{total}",
                                    Game.LobbyId, kv.Key, totalAgeSec, ageSec, dropped.ReceivedBytes, dropped.DeclaredSize);
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                Logger.LogError(e, "LobbyUdp[{lobby}] recipe-assembler sweeper error", Game.LobbyId);
            }
        }

        private async Task RunSilentPeerWatchdogAsync(CancellationToken ct)
        {
            TimeSpan checkInterval = TimeSpan.FromSeconds(5);
            TimeSpan silenceThreshold = TimeSpan.FromSeconds(30);

            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(checkInterval, ct); }
                catch (OperationCanceledException) { return; }

                DateTimeOffset now = DateTimeOffset.UtcNow;

                foreach (var kv in Sessions)
                {
                    IPEndPoint ep = kv.Key;
                    LobbySession session = kv.Value;

                    TimeSpan ago = now - session.LastPacketRxAt;
                    if (ago < silenceThreshold) continue;

                    Logger.LogWarning(
                        "LobbyUdp[{lobby}] SILENT-PEER kick {ep} uid={uid} silentFor={s}s",
                        Game.LobbyId, ep, session.PlayerInfo?.UID ?? 0, (int)ago.TotalSeconds);

                    if (Sessions.TryRemove(ep, out LobbySession? removed))
                    {
                        await RemoveAndAnnounceLeaveAsync(ep, removed, "silent-peer", ct);
                    }
                }
            }
        }

    }
}
