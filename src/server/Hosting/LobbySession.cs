using System.Collections.Concurrent;
using Arcadia.Hosting.Lobby.Protocol;
using Arcadia.Hosting.Lobby.Wire;
using Arcadia.Storage;

// Per-peer state container.
namespace Arcadia.Hosting
{
    public class LobbySession
    {
        // ===== Identity / handshake =====
        public EnvelopeKind Envelope;
        public AppStage Stage = AppStage.Idle;
        public UdpSessionCache.ClientInfo? PlayerInfo;
        public DateTimeOffset SessionStart = DateTimeOffset.UtcNow;

        // ===== ProtoTunnel transport =====
        public uint ClientIdent;
        public int TunnelIdx;
        public byte[]? PreambleBytes;
        public byte ServerPreambleByte5;
        public readonly Arc4Stream RecvStream = new Arc4Stream();
        public readonly Arc4Stream SendStream = new Arc4Stream();

        public long StaleAcrossWrapPackets => RecvStream.StaleAcrossWrapPackets;
        public long StaleInEpochPackets => RecvStream.StaleInEpochPackets;
        public long OopRecoveredPackets => RecvStream.OopRecoveredPackets;

        // ===== CommUDP seq/ack =====
        public uint ServerDataSeq = 1;
        public bool ServerSeqInitialized;

        public uint ClientAckSeq;
        public bool ClientAckInitialized;
        public readonly SortedSet<uint> ClientOooSeqs = new SortedSet<uint>();
        public const int ClientOooMaxSize = 4096;

        public uint LastAckFromClient;
        public bool LastAckFromClientInitialized;
        public DateTimeOffset LastAckFromClientAdvancedAt = DateTimeOffset.UtcNow;
        public DateTimeOffset LastProactiveRetransmitAt;

        public uint LastRelayedSrcSeq;
        public bool LastRelayedSrcSeqInitialized;

        // ===== Recovery (PING-driven retransmit) =====
        public uint ClientRequestedSeq;

        // ===== Active gap recovery (kind=4 NACK upstream) =====
        public DateTimeOffset? LastNackUpstreamAt;
        public uint LastNackedSeq;
        public long NacksSent;

        // ===== OOO-gap force-skip =====
        public DateTimeOffset? OooGapOpenedAt;
        public uint OooGapHeadAtOpen;
        public long OooGapForceSkips;
        public const int OooGapForceSkipAfterSeconds = 5;

        // ===== WaitFirstFrame barrier =====
        public DateTimeOffset? WaitingForFirstFrameAfterResetSetAt;
        // must outlast the ~10s S1 challenge cutscene, or it force-clears the barrier mid-load
        public const int WaitFirstFrameForceClearSeconds = 15;
        public bool WaitingForFirstFrameAfterReset;

        public const uint PostResetFrameWindow = 20;

        public uint LockstepFrame;

        public int BarrierEpoch;
        public uint ResetSeqToDst;
        public bool ResetSeqSent;
        public uint FirstFrameSrcSeq;
        public bool FirstFrameSrcSeqKnown;

        // caller must hold this.RelayLock
        public void ArmWaitFirstFrameBarrierLocked(DateTimeOffset nowUtc, int epoch)
        {
            WaitingForFirstFrameAfterReset = true;
            WaitingForFirstFrameAfterResetSetAt = nowUtc;
            BarrierEpoch = epoch;
            ResetSeqSent = false;
            ResetSeqToDst = 0;
            FirstFrameSrcSeqKnown = false;
            FirstFrameSrcSeq = 0;
            OrderedPendingRelayBodies.Clear();
            PendingForResetReleaseToDst.Clear();
            LockstepFrame = 0;   // client recreates its CommandQueue (mNextFrame=1) on every MT_GameReset
        }

        // ===== Retransmit cache =====
        public readonly ConcurrentDictionary<uint, byte[]> SentFrameCache = new ConcurrentDictionary<uint, byte[]>();
        public const int SentFrameCacheCapacity = 65536;

        // ===== Reliable (must-deliver) outbound tracking =====
        public readonly SortedSet<uint> ReliableUnackedSeqs = new SortedSet<uint>();

        // ===== Outbound redundancy =====
        public long RedundancySubsSent;
        public int RedundancyMLimit = 2;

        public long ResetWatchdogBaselineGameSyncs;

        // ===== Walker dedupe =====
        public readonly HashSet<uint> AlreadyParsedBareSeqs = new HashSet<uint>();
        public readonly Queue<uint> AlreadyParsedBareSeqOrder = new Queue<uint>();
        public const int AlreadyParsedBareSeqCapacity = 4096;

        // ===== Roster / handshake builder =====
        public List<(UdpSessionCache.ClientInfo Info, int Slot)>? FrozenRoster;
        public int RosterEmittedCount;

        // ===== Recipe upload + fan-out =====
        public readonly Queue<long> PendingRecipeRequests = new Queue<long>();

        public readonly Dictionary<long, uint> RecipeServeSeqByUid = new Dictionary<long, uint>();
        public readonly SortedDictionary<uint, byte[]> OrderedPendingRelayBodies = new SortedDictionary<uint, byte[]>();
        public const int OrderedPendingRelayCapacity = 8192;
        public long OrderedPendingRelayDrops;

        public readonly Queue<byte[]> PendingForResetReleaseToDst = new Queue<byte[]>();
        public bool JoinBroadcasted;
        public bool PendingJoinBroadcast;
        public bool PostJoinResetPending;

        public int JoinGateState;

        public bool VoipDisableBroadcasted;

        // ===== Loading-gate (relay strip for not-yet-in-world peers) =====
        public bool LoadingGateLogged;

        // ===== netGameLink sync trailer extrapolation =====
        public uint LastPeerSendTick;
        public uint LocalTickAtLastPeerSend;

        // ===== Outbound pacing =====
        public DateTimeOffset LastOutboundAt = DateTimeOffset.UtcNow;
        public const int DelayedAckIdleThresholdMs = 80;

        // ===== Diagnostics =====
        public long AppMsgsReceived;
        public long GameSyncsReceived;
        public long RetransmitsSeen;
        public long OooGapOpens;
        public long OooGapCloses;
        public long CatchupCacheMisses;
        public long GateRejectedRelays;
        public long StaleEpochGameSyncStrips;
        public long PreResetStragglerStrips;
        public int SecondaryTunnelPacketCount;
        public bool FirstRelayLogged;
        public DateTimeOffset LastAppMsgRxAt;
        public DateTimeOffset LastPacketRxAt = DateTimeOffset.UtcNow;

        public long LastTallyEmitMsgCount;
        public long LastTallyGameSyncs;
        public long LastTallyRetx;
        public long LastSnapshotGameSyncs;
        public long LastSnapshotAppMsgs;

        // ===== Concurrency =====
        public readonly object SendLock = new object();
        public readonly object RelayLock = new object();
        public int CatchupInFlight;

        public int RecoveryExhaustionForceSkipPending;
    }
}
