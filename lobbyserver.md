# Lobby UDP Server — Architecture + Per-Function Notes

This document holds every "why is this here" and IDA-verified rationale for the lobby UDP server. The code itself stays terse on purpose. When you read a file and see `// see lobbyserver.md > Foo > Bar`, the deep context lives in this doc under that heading.

---

## Top-level

The lobby UDP server is one component of arcadia. It does NOT touch FESL/Theater/Plasma/HTTP/Discord — those have their own modules. The lobby UDP server is solely responsible for the in-game peer-to-relay UDP traffic that flows after Theater hands off (EGEG → CommUDP CONNECT → ...).

Two important contrasts with the Skate 3 server (whose style inspired this rewrite):
1. Skate 3's relay is 122 lines because the Skate 3 game speaks modern relay-aware UDP — packets carry `receiverId` and the relay just looks it up and forwards. No crypto, no reliability, no game-layer parsing.
2. Skate 1/2 (arcadia) clients speak DirtySDK CommUDP + ProtoTunnel Arc4 + NetGameLink + Sk8 messages. The relay has to decrypt/encrypt/track/parse every byte. Inherent complexity is high.

The rewrite keeps the high inherent complexity but ditches the lambda-soup constructors and 2848-line god-files. Modules are small, focused, mostly static, and operate on `LobbyUdpServer` + `LobbySession` references passed by parameter.

## Platform separation (RPCN vs PSN)

FESL/Theater matchmaking side (not the UDP relay), kept here per request. Skate 1/2 players split into disjoint lobby pools by online platform: real-PS3 (PSN) players only matchmake with PS3, RPCS3 (RPCN) players only with RPCS3.

- **Detection** — `FeslHandler.DetectTicketPlatform` (from `HandleNuPs3Login`), mirroring the Skate 3 Blaze server's `Ps3LoginHandler.IsTicketValid`. Verify the NPTicket against `RpcnSigningKey` → `"RPCN"`; else against the real Skate PSN key `SkateSigningKey` → `"PSN"`; neither → reject. Platform comes from *which key validates*, not the ticket's SignatureIdentifier field (the old `Utils.GetOnlinePlatformName`, now removed). Side effect: arcadia now accepts real-PS3 tickets — it previously verified only `RpcnSigningKey` and rejected everything else, so it was RPCN-only in practice.
- **`SkateSigningKey`** (X=`a93f…`, Y=`9313…`) is copied verbatim from the Skate 3 Blaze server — Sony's NP-ticket ECDSA public key. Assumed shared across PS3 titles so it validates Skate 1/2 PSN tickets, but UNVERIFIED against a real Skate 1/2 PS3 ticket; if a real-PS3 player can't log in, this key is the first suspect.
- **Storage** — `PlasmaSession.OnlinePlatformId` (`"RPCN"`/`"PSN"`) set at login; copied to `GameServerListing.OnlinePlatform` when a lobby is created in `HandlePlayNow`.
- **Enforcement** — the five matchmaking queries (`FindQuickMatchJoinable`, `FindJoinableByChallengeKey`, `FindJoinableSkate1ByChallengeType`, `FindJoinableSkate2ByChallengeType`, `GetJoinablePartitionServers`) filter `x.OnlinePlatform == platform`, where `platform` is the caller's `OnlinePlatformId`. A player who finds no same-platform lobby creates one tagged with their own platform, so the pools stay disjoint automatically.
- **S2 fitTable create-gate + type-only branch** (`Handlers/PnowPolicy.cs` — pure per-variant planners returning `PnowPlan(Search, MayCreate, RankedExempt)`; `specificChallengeHunt` is the fitTable fact) — an S2 pnow whose raw `players.0.props.{fitTable-challenge_key}` contains no `"-1;"` field is a specific-challenge hunt: it may JOIN (by `challenge_type` via `FindJoinableSkate2ByChallengeType` when a type is detectable) but must NEVER create — the CREATE branch itself is gated on `!fitTableForbidsCreate`, independent of which search arm ran. **The specific-challenge pnow arrives as `sessionType=resetServer`** (field-verified packet 2026-07-17: resetServer + `{filter-challenge_type}=OwnTheSpot` + canonical `{pref-challenge_key}` + all-`0.5` fitTable), so resetServer is NOT exempt from the gate; the `"-1;"` fitTable content is the actual discriminator between resetServer flavors — present = create-own-lobby pnow (pre-existing flow, untouched: empty candidates → CREATE, `IsPrivate=true`), absent = hunt (join-or-NOSERVER). Two earlier gate versions leaked lobbies by exempting resetServer. `challenge_type` resolves via `PickClientPref` fallback hitting `{filter-challenge_type}`. Missing fitTable prop or S1 → chain untouched. NOSERVER tail logs the full decision inputs.
- **S1 matchmaking policy** (`PnowPolicy.Skate1`, 2026-07-18) — S1 findServer with a challenge_type: join the first joinable lobby of that TYPE (`FindJoinableSkate1ByChallengeType` — deliberately no IsPrivate filter, S1 has no in-game public/private toggle so private lobbies must stay matchable) else NOSERVER; NEVER creates. S1 resetServer WITH a challenge_type: ALWAYS creates with the resolved type+key (no key precondition). The old `!prefIsPrivate` branch condition is gone — a `filter-is_private` pnow artifact (same class as the 07-15 `filter-is_ranked` bug) could silently reroute S1 into join-or-create-by-key. The ranked NOSERVER short-circuit applies to both games (typed pnow + is_ranked=true → NOSERVER); the `!isQuickMatch` guard still exempts S1 quick-match pnows, whose `filter-is_ranked=true` is a client artifact, not a real pref. S1 typeless pnows (no challenge_type) quick-match — join the first available lobby else NOSERVER — for BOTH findServer AND resetServer (2026-07-18b: a typeless resetServer never creates; the reset empty-arm requires `isSkate2 || challengeTypePresent`, the S1 create arm requires `challengeTypePresent`, and `isQuickMatch` covers S1 typeless resetServer so ranked artifacts can't block it). Every added condition short-circuits true for `isSkate2`, so the S2 chain is bit-identical. The S1 2-player cap is enforced by `MAX-PLAYERS="2"` on create + `< MaxPlayers` in every matchmaking query + Theater EGAM `TryReserveJoiningSlot`.
- **NOT filtered** — direct-GID joins (`GetGameByGid`) and PSN-invite joins (`FindGameByMemberUid`): those are explicit friend/invite joins, not matchmaking, and cross-platform friend invites are implausible (separate friend networks). Revisit only if it comes up.

## File layout (final)

```
Hosting/
├── LobbyUdpServer.cs           # recv loop + dispatch + state container + background loops
├── LobbyUdpServerPool.cs       # per-game allocation, fan-out helpers
├── LobbySession.cs             # per-peer state, region-grouped
├── LobbySettings.cs            # config record
└── Lobby/
    ├── Wire/         (Arc4, Arc4Stream, BiasedEncoding, CommUdpFrame,
    │                  InternetAddressPair, NetGameLink, ProtoTunnelCodec)
    ├── Protocol/     (AppStage, GameAttributesPacket, GameRequestPacket, GameResetPacket,
    │                  GameSyncMessage, HandshakePackets, RecipePackets, RosterPlayer,
    │                  Sk8MessageLayout, Sk8MessagePackets, Sk8NetChallengeType,
    │                  Sk8Opcodes, Sk8XmlChallengeType, WireEnums)
    ├── Send/         (EncryptedSender, NetGameLinkEcho)
    ├── Reliability/  (InboundAckTracker, PingRecovery, PingEventLog)
    ├── Recipe/       (RecipeAssembler, RecipeService)
    ├── Handshake/    (HandshakeFlow, RosterBuilder)
    ├── Walker/
    │   ├── AppLayerWalker.cs   (orchestrator)
    │   ├── WalkContext.cs
    │   ├── SystemFrameDispatcher.cs
    │   └── Handlers/   (GameSyncHandler, RecipeHandlers, GameRequestHandler,
    │                     ChallengeLoadedHandler, AttributesHandlers, ResultsHandlers,
    │                     GameResetHandler, GameChangeHandlers, GameSyncPointHandler,
    │                     FixedFallbackHandler)
    ├── Relay/        (RelayPipeline, LoadingGate)
    ├── Reset/        (ResetBroadcaster, ResetWatchdog)
    ├── Challenge/    (ChallengeFlow, Skate1ChallengeFlow, Skate2ChallengeFlow)
    ├── Flow/         (JoinFinalizeFlow, MapChangeFlow, PostChallengeFlow, HostLeaveFlow)
    ├── PlayerEvents/ (PlayerEventBroadcaster)
    └── Diagnostics/  (LobbyDiagnostics, KickEventLog, DebugHooks)
```

## UDP session identity (`UdpSessionCache`)

The in-game UDP GameManager HELLO carries **no player id** (`SystemFrameDispatcher` — empty HELLO), so a UDP session's identity is resolved from its **source IP**, registered at Theater hand-off (`TheaterHandler.HandleEGAM` → `Register(theaterIp, ClientInfo{UID,NAME,PID,platform})`). Two consumers: the receive-loop authorization gate (`LobbyUdpServer.ReceiveLoop`: a datagram from an unknown EP with no session is dropped unless `UdpCache.IsAuthorized(ip)`), and identity binding (`OnHelloReceived`: `session.PlayerInfo ??= UdpCache.Resolve(ep)`).

**Same-IP correctness (NAT / CGNAT).** Multiple players can share one public IP — two consoles behind one home NAT, or unrelated players behind carrier-grade NAT. The cache therefore holds a **list of entries per IP**, and `Resolve(ep)` **claims** a distinct unclaimed entry per UDP endpoint (endpoints differ by NAT-assigned source port), so co-located players never collapse to one identity. Before this (single entry per IP, last-writer-wins) two same-IP players resolved to the SAME ClientInfo → duplicate UID in the roster/recipe-mesh → lobby wedge; it only escaped notice because Register→HELLO is near-sequential and the old `??=` snapshotted each session before the next overwrite. Resolve is idempotent per endpoint (a retransmitted HELLO returns the same claim). Residual: two same-IP players in the *same* lobby joining truly simultaneously with out-of-order HELLOs can swap identities (distinct, so no wedge — merely mismatched skaters); perfect matching is impossible server-side because the HELLO has no id and the NAT port isn't known at register time.

**Lifecycle (no leak, no stale auth).** `Register` upserts by UID (rejoin/re-EGAM replaces, never duplicates). `RemoveAndAnnounceLeaveAsync` calls `ReleaseClaim(ep)` (frees the claim for reconnect; player stays registered). FESL logout (`FeslHandler` → after `RemovePlasmaSession`) calls `Remove(ip, uid)`, which de-registers the IP once its last player is gone — so the registry can't grow unbounded and a departed player's IP doesn't stay authorized forever. All ops are serialized by one lock; `IsAuthorized` is the only per-datagram call and only on the pre-session path. Behavior locked by `tests/UdpSessionCacheTests.cs`.

## State ownership

Two state-bearing entities:

1. **`LobbyUdpServer`** — per-lobby. Owns: `Sessions` dict, `Game` reference, `Listener` socket, `EKey`, ResetWatchdog state, ChallengeFlow state, map-change + post-challenge gates, `_joinAdmissionGate`, EP processors.
2. **`LobbySession`** — per-peer. Owns: wire counters, ack/OOO tracking, recipe assembler reference, post-reset filter state, sent-frame cache, post-reset GameSync capture, per-peer diagnostics counters, two locks (`SendLock`, `RelayLock`).

Everything else is **static** — handlers, broadcasters, flow drivers, the watchdog tick loop. They receive `(LobbyUdpServer server, LobbySession session, ...)` as parameters and mutate state via direct field access on those references.

## Concurrency model

- **Receive loop** (`LobbyUdpServer.ReceiveLoop`): single thread, dispatches per-EP into unbounded `Channel<UdpReceiveResult>`.
- **Per-EP processor task** (`LobbyUdpServer.CreateEpProcessor`): single reader per EP — guarantees FIFO order across thread-pool scheduling. Different EPs run in parallel.
- **`SendLock`** (per-session): held across counter assignment + cache write + RC4 encrypt. UDP send fires OUTSIDE the lock.
- **`RelayLock`** (per-session): protects `OrderedPendingRelayBodies`, `PendingForResetReleaseToDst`, `WaitingForFirstFrameAfterReset` against the cross-thread race between the walker (per-EP-gated) and reset broadcasts (which fire on watchdog/deferred tasks WITHOUT acquiring the per-EP gate).
- **`_joinAdmissionGate`** (per-lobby SemaphoreSlim, 1 permit): at most one joiner in the race-prone roster/PLAYER_JOIN region at a time.

## Plasma TCP wing — threading model

Not lobby-UDP, but documented here because it shares the process and thread pool with the relay. Shaped by two 2026-07-16 incidents: (1) two FESL clients' fake-async TLS reads plus sync keepalive writes starved the 2-vCPU default pool and froze the UDP relay ~1s at a time (the "VPS stutter"); (2) switching writes to `WriteAsync` wedged ALL TCP read loops — BC's TlsStream serializes its default async ops on one internal gate, so the parked write held `_sendLock`, cross-connection pushes queued behind it, and the FESL-SILENT reaper then executed healthy connections at exactly 230s silence + ~10s teardown = "lost connection to EA Nation" at 240.0s.

- **Why BouncyCastle blocking mode at all** — retail Skate speaks SSLv3+RC4; `SslStream`/SChannel refuse both, hence `Ssl3TlsServer`+`Rc4TlsCrypto`. BC's blocking-mode `TlsServerProtocol` stream has no true async: default-Stream `ReadAsync`/`WriteAsync` serialize on one gate and run sync work on borrowed pool threads.
- **Dedicated reader thread per connection** — `EAConnection.ReadPump` (thread name `ea-rx <endpoint>`): blocking `Read`, framing, and multipacket assembly live on an owned thread; parsed Packets flow through an unbounded single-writer channel consumed by `ReceiveAsync`. Pool threads never park on TCP reads. Uniform for FESL/Theater/Messenger — one model, no flags.
- **Writes are deliberately sync** — `EAConnection.SendBinary`: sync `Write` under `_sendLock`. `WriteAsync` is FORBIDDEN on these streams (incident 2). A stalled client's TCP stall blocks one pool thread transiently; the `ThreadPool.SetMinThreads(32,32)` floor in Program.cs absorbs that. If write-stall pressure ever outgrows the floor, the fix is a per-connection writer thread + queue — never `WriteAsync`.
- **`IEAConnection.Transport`** — the RAW NetworkStream beneath the TLS stream. `Terminate` disposes Transport, not the TLS stream: a raw close unblocks a parked `Read` instantly, while a TLS-stream dispose would try to write close_notify to a possibly-dead peer and block. Side effect: reaper kicks tear down immediately instead of the old ~10s lag.
- **Handshakes run concurrently** — `Rc4TlsCrypto` is registered transient (the old global `_sslHandshakeSemaphore` existed to guard the shared singleton); the blocking `Accept` borrows a pool thread via `Task.Run` bounded by `SslHandshakeTimeout` (10s), and on timeout the raw stream is disposed to release the abandoned Accept. One slow or half-open connector can no longer serialize every login.
- **`EventFileWriter`** — single owned writer thread + bounded drop-on-full queue behind `PingEventLog`/`KickEventLog`. File appends must never run (or lock) on the datagram path: ping-events fire per kind=4 PING, which storms exactly when the network is already sick.

---

## LobbySession

Per-peer state container (`Hosting/LobbySession.cs`), region-grouped. Only the non-obvious fields are documented here. Two locks: `SendLock` (counter assignment + cache write + RC4 encrypt; UDP send fires outside it) and `RelayLock` (`OrderedPendingRelayBodies` / `PendingForResetReleaseToDst` / `WaitingForFirstFrameAfterReset` against the walker-vs-reset-broadcast race).

### CommUDP seq/ack
- `ServerDataSeq` — next outbound seq (starts 1). `ClientAckSeq` — our cumulative ack of the client's INBOUND stream; OOO arrivals queue in `ClientOooSeqs` (cap `ClientOooMaxSize = 4096`).
- `LastAckFromClient` — highest outbound seq the CLIENT has cumulatively acked (w1 of every inbound DATA; a kind=4 PING's expected-seq implies expected-1). Monotonic max, single-writer (serial inbound path). Consumed by the recipe-mesh gate (confirm a served recipe REACHED the client, not merely left our socket) and the proactive-retransmit sweep. `LastAckFromClientAdvancedAt` stamps the last increase — fresh during healthy flow, stale only when an outbound frame isn't getting through (the signal proactive retransmit keys on).

### WaitFirstFrame barrier
- `WaitingForFirstFrameAfterReset` (+ `…SetAt`) — pre-reset GameSync filter (see Reset > Barrier).
- `WaitFirstFrameForceClearSeconds = 15` — LAST-RESORT force-clear. PRIMARY clear is the peer's own first post-reset GameSync (mFrame ≤ `PostResetFrameWindow`); this timer only recovers a genuinely-lost FirstFrame, so it MUST exceed the longest legit reset→FirstFrame delay. Was 5s — SHORTER than the S1 challenge-start cutscene (~10s before the client emits FirstFrame) — so it fired mid-cutscene every challenge, prematurely dropping the barrier and releasing held bodies into the still-loading peer's not-ready ring → silent ring drops → post-reset CommandQueue gap → exact-frame lockstep wedge (the occasional "stall right after the cutscene"). 15s lets the real FirstFrame clear it first; non-challenge lost-FirstFrame recovery is unaffected (ResetWatchdog re-broadcasts at 12s first).
- `PostResetFrameWindow = 20` — post-reset epoch window. While armed, a GameSync with mFrame ≤ 20 is the new epoch (`SimController::Reset` restarts the sim at frame 1); the first such frame clears the filter and relays. mFrame above this while armed = old-epoch pre-reset frame, dropped. A lost/reordered frame 1 no longer wedges (clears on any of 1..20). S1 only; S2 uses the FirstFrame flag 0x80.
- `ArmWaitFirstFrameBarrierLocked(nowUtc)` — single source of truth for arming, used by every reset path; caller MUST hold RelayLock. Sets the flag + clears `PostResetGsCapture`, `OrderedPendingRelayBodies`, `PendingForResetReleaseToDst` (in-flight pre-reset bodies that would otherwise drain into the rebuilt post-reset CommandQueue and poison it).

### Retransmit / redundancy / reliable tracking
- `SentFrameCache` (cap `SentFrameCacheCapacity = 65536`) — seq → netGameLink bytes for catchup / redundancy / proactive replay. O(1) eviction (monotonic seq, one key in per fresh send).
- `RedundancyMLimit` — adaptive sub-packet cap for outbound redundancy: starts 2, doubles to ≤15 under loss pressure, resets to 2 when a walk consumes all candidates (budget `RedundancyLimitBytes = 96` lives in EncryptedSender).
- `ReliableUnackedSeqs` — must-deliver control seqs for proactive retransmit (see Reliability > Proactive retransmit).

### Recipe / relay / reset-gate
- `RecipeServeSeqByUid` — peer UID → highest outbound seq of the completed Head(+Data) serve of that peer's recipe to this session; delivery confirmed once `LastAckFromClient ≥ seq`. Head(size=0) fallbacks recorded too (the client's recipe gate is satisfied by its default-loader path, so nothing more will arrive). Guarded by RelayLock.
- `OrderedPendingRelayBodies` (cap `OrderedPendingRelayCapacity = 8192`) — parked relay bodies keyed by srcBareSeq, drained in order (see Relay > DrainAsync).
- `PendingForResetReleaseToDst` — bodies destined for THIS peer while it's pre-reset; released coalesced ≤800B on FirstFrame (see Relay > DrainResetGateRelease).

### Walker dedupe / OOO force-skip
- `AlreadyParsedBareSeqs` (+ order queue, cap `AlreadyParsedBareSeqCapacity = 4096`) — dedupe so a retransmitted datagram isn't re-walked; sync/ack-only frames return BEFORE the set add so a later body-bearing retransmit at the same seq isn't swallowed.
- `OooGapForceSkipAfterSeconds = 5` — an inbound OOO gap open this long force-skips to the lowest queued seq, then the peer is kicked (see LobbyUdpServer ProcessTail).

---

## Settings

`LobbySettings` (`Hosting/LobbySettings.cs`):
- `Enabled` — gate; allocator returns null when false.
- `ListenAddress` — bind interface (defaults `0.0.0.0`).
- `EKey` — RC4 key fed to ProtoTunnel; default `"RELAYKEY"`.
- `BasePort` / `MaxLobbies` — per-lobby port allocation range. Default 17000 + 1..500.
- `HostMigrationEnabled` — **DISABLED**, see below.

### HostMigration

Lite host-migration via UDP-only handshake replay would reuse init-time HOST_HELLO mid-session. Verified S2 crash: `OnHostHelloReceived @ 0x8248b3a0` end of function `mHostLink->GetRemoteAddress(mHostLink)` vcalls through `mHostPlayer->mGame->mHostLink`. `BreakHostConnection` inside `SwitchToHostMigrationMode` @ `0x8248d6b8` nulls that field, so any post-migration HOST_HELLO NULL-derefs.

The current behavior: host leaving → BOTH games dissolve the lobby (variant-aware LostConnection kick to remaining peers; S1 = mRequest 7, S2 = mRequest 4).

---

## Wire

### Arc4 (`Lobby/Wire/Arc4.cs`)

DirtySDK CryptArc4 implementation. Init/Advance/Apply mirror EA's symbols verified at:
- `CryptArc4Init @ 0x82ccb0a8`
- `CryptArc4Apply @ 0x82ccb148`
- `CryptArc4Advance @ 0x82ccb1b0`

`iSchedule` clamps to `max(1, iSchedule)`. `ProtoTunnelAlloc` passes 1.

**Atomic rollback** — `CopyStateTo` / `RestoreStateFrom` mirror EA's stack-buffer pattern in `sub_82ECA768 @ 0x82eca768`. The recv path copies the persistent RC4 state into a 256-byte stack buffer, works on the stack copy, and only copies back to the struct on successful decode.

### Arc4Stream (`Lobby/Wire/Arc4Stream.cs`)

Persistent RC4 keystream with **delta-advance** per packet. Mirrors EA's design verified in `sub_82ECA768` / `sub_82ECA080 @ 0x82eca080`:
- Tunnel struct holds persistent RC4 state at `+346` (`S[256] + x + y`).
- Tunnel struct holds persistent last-seen wire counter at `+86`.
- Per packet: `delta = packetCounter - last; CryptArc4Advance(state, 4 * delta)` — advance by SMALL delta (typically 1..50 wire-counter units).
- After successful decode, `sub_82ECA080` advances the counter by `bytesApplied / 4` and realigns RC4 to the next 4-byte slot boundary if `bytesApplied % 4 != 0`.
- Invariant maintained at every packet boundary: `rc4_position == 4 * last_wire_counter`.

**Why this exists vs. the obvious re-init-from-EKEY-and-advance design**:

The previous arcadia codec re-Init'd from EKEY and `Advance(4 * effectiveCounter)` on every packet. Per-packet cost was O(session_age × send_rate). At ~30 outbound packets/sec/peer × ~33 counter ticks/packet, effective counter reaches ~1M after 30 minutes — each packet's pre-Apply cost ballooned from ~100K iterations at session start to ~4.4M at 30 minutes. Verified in 3-stage log diff (HEALTHY.txt → BIT WORSE.txt → STALLED.txt): gs throughput collapsed from 66/s to 0/s while OS-level work matched the O(effectiveCounter) growth curve.

The persistent-stream design collapses per-packet cost to O(packet_size) regardless of session age.

**Wrap handling** (verified `sub_82ECA768` lines `0x82eca7d0+`):
- `delta == 0` → forward, no advance.
- `0 < delta < WrapThreshold (32768)` → forward, `Advance(4*delta)`.
- `delta > WrapThreshold` AND `HighWord > 0` → stale-across-wrap from previous epoch; persistent stream untouched, fallback to one-shot fresh-init+advance.
- `-WrapThreshold < delta < 0` → stale-in-epoch retransmit; silent drop (matches EA `return 0`).
- `delta <= -WrapThreshold` → forward wrap; `Advance(4 * (delta + 0x10000))`, `HighWord++`.

### BiasedEncoding (`Lobby/Wire/BiasedEncoding.cs`)

`Fesl::DecodingBuffer::Decode*` applies a sign bias on every signed read so wire byte 0x80 maps to signed zero. Encode side mirrors that. Verified:
- `DecodeChar @ 0x824851b8`
- `DecodeSint16 @ 0x82485320`
- `DecodeSint32 @ 0x82485410`
- `DecodeSint64 @ 0x82485498`

`DecodeString` reads a biased Sint32 length followed by raw bytes.

### CommUdpFrame (`Lobby/Wire/CommUdpFrame.cs`)

CommUDP wire kinds (verified `sub_82F0A390`):
- `Connect = 1`, `ConnAck = 2`, `Disconnect = 3`, `PingRetransmitRequest = 4`, `Poke = 5`.

Plain wire layout: `[4 BE kind/seq][4 BE ident/ack][optional payload]`.

ProtoTunnel-wrapped DATA frames drop the ident words and use encrypted 8-byte header `[4 BE seq][4 BE ack]`.

**Sub-packet bundling**: when bit 28+ of seq is set, the high nibble carries `(sub_count - 1)` and the low 28 bits carry the bare seq. Each sub-packet past the first has its 1-byte length appended at the tail of the payload, read backwards (verified `sub_82F0A390 @ 0x82f0a600+`).

`TrySplitBundle` iterates `END → BEGIN` of payload reading 1-byte length tails from the end, building the wire-order list of `(start, length)` pairs.

### InternetAddressPair (`Lobby/Wire/InternetAddressPair.cs`)

`Fesl::InternetAddressPair` on the wire is two back-to-back `InternetAddress` records (verified `InternetAddress::FromBuffer @ 0x824348f8`): `DecodeUint32 mAddr + DecodeUint16 mPort = 6 bytes raw BE`.

AddressType selector lives in the parent roster-elem field, immediately before the pair: `0 = InternetAddressPair`, `2 = XnaddrAddress`.

### NetGameLink (`Lobby/Wire/NetGameLink.cs`)

Inner CommUDP-payload wrapper. Each sub-packet: `[1 sysFlag][body][optional 10-byte sync trailer][1 kindByte]`. The kindByte's `0x40` bit signals the sync trailer is present; the `0x06` lower bits are the netGameLink kind (DATA bit).

Valid kindByte values arcadia produces:
- `0x40` — pure sync/keepalive frame, body empty
- `0x46` — DATA frame with body + sync trailer

Verified against `sub_82F0BA68 @ 0x82F0BA68` (composer) and the recv pump.

**Sync trailer** (10 bytes, verified `sub_82F0BA68` / `sub_82F0BC68`):
- `[0..4]` BE u32 — arcadia's estimate of peer's current tick (peer echoes back as next trailer's `[0..4]`)
- `[4..8]` BE u32 — arcadia's local tick — peer subtracts to get one-way latency, smoothed into ping/jitter
- `[8..10]` BE u16 — jitter estimate, informational (zero is fine)

### ProtoTunnelCodec (`Lobby/Wire/ProtoTunnelCodec.cs`)

Encoded-on-wire ProtoTunnel frame:
```
[2 BE counter] [N × 2 BE tuple_header] [encrypted payloads] [clear payloads]
tuple_header = (length << 4) | tunnelIdx   (12-bit length, 4-bit idx)
```

Encrypted tunnel idx sets per variant:
- **Skate 1**: 1, 2, 7 (verified historical)
- **Skate 2**: only 7 (preamble)

CommUDP tunnel idx sets per variant:
- **Skate 1**: 1 or 2
- **Skate 2**: 1 only

`TryDecode` uses `Arc4Stream.TryAdvanceToCounter` for the persistent path, and `TryDecodeFresh` (one-shot, untouched persistent stream) for the rare stale-across-wrap retransmit. `TakeSnapshot` / `RestoreSnapshot` rolls back both state and position on a malformed-frame partial-Apply.

`BuildConnAck` / `BuildData` / `BuildDataBundle` / `BuildPingRetransmit` emit the four shapes arcadia sends. Skate 1 encrypts the whole `[2 tuple_headers + preamble + payload]` block (sometimes longer with the body); Skate 2 encrypts only the 10-byte preamble block.

**Bundle layout** (verified receiver `sub_82F0A390 @ 0x82f0a600+`):
```
[body HIGHEST seq]                     ← offset 0, no length byte
[body HIGHEST-1 seq][len HIGHEST-1]
[body HIGHEST-2 seq][len HIGHEST-2]
...
[body LOWEST seq   ][len LOWEST]       ← end of payload
```

Receiver loop iterates `v14 = subCount → v14 < 0`, ASSIGNS effSeqs LOWEST → HIGHEST while READING the buffer END → BEGIN. So the LOWEST seq's body sits at the buffer end (just before its length byte) and the HIGHEST seq's body sits at offset 0 with its length implied by the residual.

**Constraints (all source-verified)**:
- subCount fits in 4 bits → max 16 sub-packets per bundle.
- each sub body length must fit in 1 byte → ≤ 250 (source caps at 250 in `sub_82F09A88` line `0x82f09bcc`).
- entries must be sorted strictly ASCENDING by seq.

**Why bundle vs. N separate frames**: ProtoTunnel decrypt at `sub_82F082B0 @ 0x82f0831c` rejects any packet whose 16-bit counter is < the receiver's tracked expected (signed delta < 0 → return 0, silent drop before CommUDP sees it). Sending N back-to-back means N counters, and one UDP-reorder kills every preceding sibling. Bundling makes it ONE counter for all sub-packets — no reorder risk.

---

## Protocol

### AppStage / EnvelopeKind

Per-peer handshake stage progression: `Idle → HelloReceived → HostHelloSent → HostRosterElemSent → RosterAckReceived → JoinCompleteSent → GameAttribsSent → [S2: GameRecipeRequestSent] / [S1: GameResetSent]`.

`EnvelopeKind`: `Plain` (CommUDP CONNECT/POKE/DISC before encryption) vs `ProtoTunnel` (encrypted).

### WireEnums

#### GameManagerPacketType (Fesl GM)

Handler index registered via `SetPacketHandler` in `GameManagerGameImpl` + `GameManagerHostedGameImpl` ctors. Wire byte = `(byte)Type + 0x80` because `SystemPacketEncoder` writes via `PutIntegerData` with the +0x80 char bias (verified `@ 0x8249b0b4`).

Full table (0..23) — see source. Relevant for arcadia:
- `Hello (0)` → wire `0x80`, `OnHelloReceived`
- `HostHello (2)` → wire `0x82`
- `HostRosterElem (3)` → wire `0x83`
- `RosterAck (4)` → wire `0x84`
- `PlayerJoin (5)` → wire `0x85`, `OnPlayerJoinReceived` (no `MakePlayerActive`)
- `PlayerJoinFullMesh (8)` → wire `0x88`, `OnPlayerJoinFullMeshReceived` — DOES call `MakePlayerActive`
- `JoinComplete (9)` → wire `0x89`, `OnJoinCompleteReceived`
- `PlayerLeft (10)` → wire `0x8A`
- `RelayRequest (13)` → wire `0x8D`, used for host-kick
- `VoipEnabledChange (20)` → wire `0x94`

#### Sk8MessageType

Verified `Sk8::Net::Message::Create` factory `@ 0x82a6d350` (Skate 2 IDA, confirmed live this session):
- 1=GameSync, 2=GameReset, 3=GameRequest, 4=GameAttributes, 5=GameAttributeUpdate, 6=GameComplete, 7=GameResults, 8=GameAllPlayersComplete, 9=GameFinalResults, 10=GameTimer, 11=GameExitPostChallenge, 12=GameLoadRequest, 13=GameChallengeLoaded, 14=GameResetAttributes, 15=GameRequestReset, 16=GameRequestChange, 17=GameSyncPoint, 18=GameChange, 19=GameRemovePlayer, 20=GamePlayerAttributes, 21=GameWager, 22=GameProposal, 23=GameRecipeRequest, 24=GameRecipeHead, 25=GameRecipeData.

The factory asserts `type==0 || type>=0x1A` → that's the WALKER-BAIL `firstByteCap = 0x1A` for Skate 2.

#### Skate 1 vs Skate 2 differences

Three Skate 1-only opcodes: Broadcast (1), Broadcasted (2), GameLoadDone (3), GameQOSInfo (23).
Three Skate 2-only: GameAttributeUpdate (5), GameChallengeLoaded (13), GameSyncPoint (17), GamePlayerAttributes (20), GameWager (21), GameProposal (22).

S1 indices for the rest shift by 3 vs S2 in the middle range — see `Sk8Opcodes.cs` for the full per-variant decode/encode tables. Per-variant `Sk8::Net::Message::Create` factories verified in IDA on both binaries.

#### Sk8AttributeType (9 slots)

Verified `GetAttributeValue` asserts `type < 9 @ 0x82a6a710` and `sOnlineAttributes` initializer `@ 0x82f795e0`:
- 0=GameVersion, 1=ChallengeType, 2=ChallengeKey, 3=PingSite, 4=IsPrivate, 5=MaxPlayers, 6=IsRanked, 7=OverallSkill, 8=ChallengeSkill.

#### Sk8ResetType (verified UpdateGameInfo branches)

- `LobbySkate = 0` — free skate (no challenge asset load)
- `ChallengeLoad = 1` — load challenge assets, transition to ChallengeLoadState
- `Challenge = 2` — start challenge proper, transition to ChallengeSkate

Skate 1's HandleLobbySkateReset `@ 0x82591eb0` uses `mResetType == 1` for challenge-start (no separate "load" phase). `Skate1ChallengeFlow` therefore sends `Sk8ResetType.ChallengeLoad` (=1) which means "Reset_Challenge" on Skate 1 — name is misleading; wire byte is correct.

#### Sk8GameRequest

**IMPORTANT**: The same `mRequest` byte means DIFFERENT things in Skate 1 vs Skate 2. Names below carry the Skate 2 semantic where they overlap; the Skate 1-only value is suffixed `Sk1`.

Skate 2 (`sk82_na_m.xex`):
- mRequest=1 StartGame — `EventSystem::HandleStartGame @ 0x8278de28` (host-only outbound)
- mRequest=3 ToggleSlotAccess — `FrontEndState_OnlineHubMenu::GoToToggleSlotAccess @ 0x8266c998`
- mRequest=4 LostConnection — `StateOnlineGame::OnMessage case 3 @ 0x8276b870` HUD kick path
- mRequest=5 PauseResume
- mRequest=7 Heartbeat — `SendCommands @ 0x825F5C00` fallback when SyncChecker dry

Skate 1 (`sk8_na_f.xex`):
- mRequest=6 ConvertPrivateSession — `StateOnlineGame::FilterImpl case MESSAGE_ConvertPrivateSession @ 0x8258f508`
- mRequest=7 LostConnection — `OnMessage case 6 mRequest==7 @ 0x82592648` kick path (NOT Heartbeat)
- mRequest=10 Heartbeat — `OnlineNetwork::SendCommands @ 0x825F5C00` fallback (NOT 7 like S2)

`GameRequestPacket.LostConnection(variant)` returns the right (mRequest, mValue) tuple for each variant.

### Sk8MessageLayout

Fixed body sizes after the leading op byte (returns -1 for variable-length or unknown):

**Skate 1**:
- GameRequest: 2 (1 mRequest + 1 mValue) — note S2 has 9
- GameResults: 16 (4 + 4 + 4 + 4)
- GameTimersData: 20 (5 × 4 BE u32s) — note S2 GameTimer is 12
- GameChange: 21 (4 + 8 + 8 + 1) — S1 has trailing mChange byte
- GameRemovePlayer: 12 (8 + 4)
- GameLoadRequest: 4
- GameRequestReset: 4
- GameRequestChange: 12 (4 + 8)

**Skate 2**:
- GameRequest: 9 (1 + 8)
- GameResults: 15
- GameTimer: 12 (4 timer + 8 time)
- GameChange: 20 (no trailing byte)
- GameRemovePlayer: 12
- GameLoadRequest: 4
- GameChallengeLoaded: 0
- GameRequestReset: 4
- GameRequestChange: 12
- GameSyncPoint: 0
- Skate2_GamePlayerAttributes: 12
- Skate2_GameWager: 16
- Skate2_GameProposal: 29

GameFinalResults:
- S1 (`Pack @ 0x82B10DE8`): 6 fixed-size slots (26 B each) + 4 mPlayerCount = 160 B
- S2 (`Pack @ 0x82a6cca0` / `Unpack @ 0x82a6cda0`, both re-verified 2026-07-02): 4 mPlayerCount + N × **39-byte wire records** (the old "48" here was the client's in-memory `PlayerStatsData` stride — 48 ≠ wire). `FinalResultsPlayerStride = 39`.

S2 per-player wire record (write/read order, all BE):

```
[8]  peerId          64-bit UID (same identity as mPlayerPeerIDs slots)
[4]  eventTime       f32 (raw bits relayed from that peer's MT_GameResults)
[4]  score           i32
[4]  finishReason    i32 (1 = normal completion)
[2]  ranking         i16
[1]  playersChoice   u8
[4]  cash            i32  ┐ wire order is cash, exp, wager, winnings —
[4]  exp             f32  │ NOT the PlayerStatsData member order
[4]  wager           i32  │ (wager, winnings, cash, exp). Arcadia sends
[4]  winnings        i32  ┘ all four as 0 — see PostChallengeFlow > S2 scoreboard.
```

mPlayerCount MUST be ≤ 8: `Unpack` writes count records into `mPlayerStats[8]` with NO bounds check — a larger count smashes client memory. `FinalResultsSkate2MaxPlayers = 8` caps the builder; lobby cap 6 keeps real rows under it anyway.

### GameSyncMessage (S2 only, S1 has different wire)

S2 GameSync wire (verified `Pack @ 0x82a6c528` / `Unpack @ 0x82a6c620`):
```
[flagByte][checksum][size (1 or 2 bytes)][data]
```

`flagByte` bits:
- `0x0F` PlayerSlotMask — routing key on receiver, indexes `mCommandQueueMap[mPlayer]`
- `0x20` OnlyInput
- `0x40` SizeFieldIsTwoBytes
- `0x80` FirstFrame — set on first emit after `SimController::Reset@0x828a2c68` sets `mNextGenFrame=1`

The receiver's slot index must match the sender's slot in the recipient's `mReset.mPlayerPeerIDs[]`. Diverging mappings cause MT_GameSync routing to the wrong CommandQueue → silent drop or wrong-character input.

### GameAttributes

Variant-specific slot counts + trailers:
- S2 (`Unpack @ 0x82a6aee0`): 9 strings, no trailer.
- S1 (`Pack @ 0x82B107B8`): 12 strings + 8-byte mLockTime trailer.
- S1 GameResetAttributes (`Pack @ 0x82B10A38`): 12 strings, NO trailer.

`SetupInitalGameInfo @ 0x82591650` reads mLockTime → `mLockRoomTime` / `mStartGameTime`. Missing trailer leaves those uninitialized — `Unpack` strictly reads 8 bytes after slot 12 and sets `bs->mError=1` if shorter.

Default values arcadia ships:
- S2 slot 0 `""` (game_version blank), slot 1 OnlineFreeSkate, slot 2 default key, slot 3 PingSite, slot 4 is_private true, slot 5 "6" (cap), slot 6 "false" (ranked), slot 7+8 "0".
- S1 slot 0 `"#BAM"` (game_version — bypasses version check), slot 7 `"false"` (is_private — observer-accept gate).

### GameReset

**Skate 1** body (52 B): 48 mPlayerPeerIDs (6 × 8) + 4 mResetType. Verified `Pack @ 0x82B10650`.

**Skate 2** body (84 B): 64 mPlayerPeerIDs (8 × 8) + 4 mResetType + 4 mRandomSeed + 4 mSessionId + 8 mActivity. Verified `Pack @ 0x82a6d528` (confirmed live this session).

**Slot count gotcha**: Skate 2 retail caps the lobby at 6 players (HOST_HELLO PlayerType[0] cap=6), but the `mPlayerPeerIDs[]` wire array is still 8 slots. Shrinking arcadia's slot count to 6 caused phantom UID=0 players in slots 6/7 from the trailing mResetType/randomSeed bytes being read as slot data.

Empty slots sentinelled with `0xFF...FF`; client treats anything `!= -1` as a player UID.

### GameRequest

S1: 1B mRequest + 1B mValue = 2B body (verified `Pack @ 0x82B10708`). Note `WriteBytes(bs, &mValue, 1u)` — mValue effectively `unsigned __int8`.

S2: 1B mRequest + 8B mValue = 9B body (verified `Pack @ 0x82a6c768`).

### Recipe

S1 used msg_type 20/21/22 for Request/Head/Data; S2 shifted to 23/24/25 (new GamePlayerAttributes/Wager/Proposal inserted at 20/21/22).

Bodies (after leading msg_type byte):
- Request: 8 bytes `mPeerId`
- Head: 8 mPeerId + 4 mSize + 4 mCRC = 16
- Data: 8 mPeerId + 4 mSize + 4 mChunk + mSize raw bytes

All multi-byte fields raw BE.

### HandshakePackets / RosterPlayer

HOST_HELLO byte layout (37 bytes) verified `OnHostHelloReceived @ 0x8248b3a0` decode order (confirmed live this session):

```
[0]      0x82 opcode
[1..2]   Sint16 ver = 20 (biased 0x8014)
[3..6]   Sint32 game-name length = 0 (biased)
[7..8]   Sint16 (ignored, biased 0x8000)
[9]      Char netType — biased (ClientServer=0 → 0x80)
[10]     Char voipType — biased (Mesh=1 → 0x81)
[11]     Char currentJoinMode = 0 (biased 0x80)
[12..13] Uint16 currentJoinFlags = 0
[14]     Uint8 inProgress
[15]     Uint8 ranked
[16]     Uint8 joinInProgress
[17]     Uint8 joinViaPresence
[18]     Char inviteStatus = 0 (biased 0x80)
[19]     Uint8 hostMigration
[20..22] PlayerType[0]: cap=6, voip=1
[23..25] PlayerType[1]: cap=0, voip=0
[26..33] xb360 nonce (8 raw bytes)
[34..35] Uint16 rosterElemSize (patched at send time)
[36]     Uint8 hasHost
```

**CRITICAL**: byte [10] (voipType) MUST be `0x81 (MESH)`. `0x80 (Disabled)` crashes host at lobby creation (StartupVoip skipped → NULL `mVoipRef`). MESH is safe because `hasHost=0` gates out every `MakeVoipConnection` callsite.

#### RosterPlayer (HOST_ROSTER_ELEM / PLAYER_JOIN body)

Verified `GameManagerPlayerImpl::FromBuffer @ 0x824971f0`:
```
Sint32 mPlayerRef       [biased]   ← also read by AddPlayerFromBuffer first
Sint32 mPlayerRef       [biased]   ← AddPlayerFromBuffer reads + FromBuffer overwrites with same value (wire carries it twice)
Sint32 mSlotId          [biased]
String name             [biased Sint32 len + UTF-8 bytes]
Sint64 mUserId          [biased]
Char   peerLink         [biased; 0 = none]
8 raw bytes             [nonce / secondary key — zero ok]
Char   peerAddress flag [biased; 0 = none]
Char   addressType      [biased; 0 = InternetAddressPair, 2 = Xnaddr]
InternetAddressPair (12 raw BE bytes when selector = 0)
Uint8  playerType       [raw; client does mPlayerType = (byte == 1): 0 → PLAYER (host-eligible), 1 → OBSERVER (host-scan skips)]  — see Host actions
Sint64 mUser            [biased]
```

#### HOST_KICK relay path

`Sk8::Net::KickPlayer @ 0x8278dfc0` is host-only — returns early unless `GetGameHostPeerID() == local`. Sends `Message_GameRequest(mRequest=LostConnection)` UNICAST to the target. The game frames this as a GM RelayRequest (sysFlag=System, wire `0x8D`).

Verified `OnRelayMessageReceived @ 0x8248e100` (confirmed live this session):
```
[pb+0]    sysFlag = 0x01 (System)
[pb+1]    opcode  = 0x8D (RelayRequest)
[pb+2..5] Sint32 destPlayerRef (biased)
[pb+6..9] Sint32 inner length (biased)
[pb+10..] inner Sk8 message (GameRequest: type byte + mRequest + mValue)
```

Arcadia drops every mid-session System frame at the walker (`SystemFrameDispatcher.TryHandle` returns false for anything other than HELLO / ROSTER_ACK / RelayRequest-with-LostConnection-inner), so the kick reached no one without server-side handling.

`SystemFrameDispatcher` decodes destRef + checks inner against `GameRequestPacket.LostConnection(variant)` first two bytes (variant-aware: S2 op3/mReq4, S1 op6/mReq7) and routes to `HostLeaveFlow.HandleHostKickAsync` which:
1. LostConnection→target so it shows the kicked chyron + leaves
2. PLAYER_LEFT→everyone else via `RemoveAndAnnounceLeaveAsync`

### Sk8NetChallengeType / Sk8XmlChallengeType

`Sk8::Net::ChallengeType` is the wire enum (variant-specific values) carried in MT_GameRequestChange, MT_GameChange, MT_GameAttributes slot 1.

`Sk8::Xml::ChallengeType` is the FE/Apt-side enum mapped in `FE::FrontEndManager::RegisterChallengeEnums @ 0x82611240` (the Apt-side `eChallengeTypes_*` registered for Flash UI).

The wire `challengeType` int in MT_GameRequestChange uses Xml enum values; the GameAttributes string slot 1 uses the matching name (e.g. wire int 9 → "OwnTheSpot", int 22 → "OnlineFreeSkate"). Without int→string mapping when handling a map-change, B-U-challenge_type stays pinned to creation-time value — breaks "Start Challenge" gate when navigating freeskate → spot battle.

Skate 1 has Gate / SpotRace / LastManStanding (S1-only online challenges); Skate 2 has OneUp / HallOfMeat (S2-only). Same slot count, totally renumbered.

---

## Send

### EncryptedSender (`Lobby/Send/EncryptedSender.cs`)

Single point of truth for outbound ProtoTunnel sends. Methods:
- `SendConnAckAsync` — preamble + CONNACK CommUDP
- `SendPingRetransmitAsync` — kind=4 NACK upstream
- `SendDataAsync(... uint? explicitSeq = null, bool dropOnWire = false)` — main DATA send. Fresh sends consume next `ServerDataSeq` and cache the netGameLink bytes. Catch-up replays pass `explicitSeq` to skip seq-burn + cache-write.
- `SendBundledDataAsync` — bundled DATA (single counter, N sub-packets) for catchup
- `SendSk8BodyAsync` / `SendSystemBodyAsync` — wrap an app body in NetGameLink then call SendDataAsync
- `SendAckOnlyAsync` — netGameLink ack-only frame

**`dropOnWire`** caches in `SentFrameCache` + advances `ServerDataSeq` but skips the actual UDP send. Used by:
- PhantomDropTester ('y' debug key)
- Relay backpressure (cache + advance + skip wire; peer's NACK replays from cache later)

**SentFrameCache eviction** — O(1): `ServerDataSeq` is monotonic and the cache grows by exactly one key per fresh send, so at most one key falls out of the `[serverSeq-cap+1, serverSeq]` retention window each fresh send → `TryRemove` directly. The previous `Keys.Where(k=>k<cutoff).ToList()` form ran under SendLock on every fresh send past cap and was the dominant per-packet cost in long sessions.

### NetGameLinkEcho (`Lobby/Send/NetGameLinkEcho.cs`)

Extrapolates peer's current tick for our next outbound trailer's echo slot.

With no inbound trailer yet, fall back to our own tick — the peer's smoother self-corrects once a real sample arrives.

---

## Reliability

### InboundAckTracker (`Lobby/Reliability/InboundAckTracker.cs`)

Tracks cumulative-ack frontier for inbound DATA from one peer. The frontier is what arcadia echoes back as the `ack` field on every outbound packet — matches the client's `CommUDPSendPacket` convention (`ack = highest contiguous inbound seq received`).

Out-of-order arrivals queue in `session.ClientOooSeqs`; when the missing seq lands, the frontier sweeps forward over any contiguous queued seqs.

Verdicts:
- **INIT** — first DATA, seeds frontier
- **ADVANCE** — `bareSeq == ack+1`, drains contiguous OOO entries
- **OOO** — future seq, queue in OOO set + stamp `OooGapOpenedAt`
- **PAYLOAD-PAIR** — duplicate of acked seq WITH payload (low-SendRate normal traffic)
- **DUPLICATE** — pure retransmit (no payload)

### PingRecovery (`Lobby/Reliability/PingRecovery.cs`)

Client → server catch-up: when peer sends kind=4 PING (`w1 = expected seq`), arcadia replays from `SentFrameCache` so a single round-trip closes multiple gaps.

The inverse direction (arcadia detected OOO on inbound) intentionally does nothing — withholding cumulative-ack TX lets the source's CommUDP retransmit timer naturally refill the gap; arcadia just emits a kind=4 NACK upstream to nudge it.

**CatchupWindowSize = 16** (one bundle per PING). Empirically smoothest on Skate 1 after the recv-ring batching fix.

**No throttle / mode / per-seq cap.** Single-flight per session (`CatchupInFlight` CAS) coalesces back-to-back NACKs for the same range; otherwise every NACK bundles + sends immediately. The RC4-era compensation logic (singleSubMode, `CatchupMinIntervalMs`/40ms cooldown, `MaxReplayAttemptsPerSeq`) was removed — every prior multi-minute stall traced to those throttles/modes, not to lack of batching. Pure 1:1 (one seq per NACK) was tried and FAILED for joins: a new peer needs ~50 consecutive handshake seqs and any loss drops it into a 200-400ms-per-seq spiral that never catches the still-arriving traffic; bundle catchup is the minimum that survives the join burst at VPN-grade latency.

**Bundle mode — CONSECUTIVE-SEQ INVARIANT**:

The bundle wire format only encodes `(subCount, highestSeq)` and infers all other sub-packet seqs as `effSeq[i] = highestSeq - subCount + i, i = 0..subCount` (verified IDA `sub_82F0A390 @ 0x82F0A620` v30 = `(w0 & 0xFFFFFFF) - (w0 >> 28)` = lowestSeq, increments per iteration; `sub_82F09F48` deliver function uses v30 as the seq for past/future/equal comparison).

So packing `(270, 274..277)` into a 5-entry bundle with `highestSeq=277` makes the receiver assign effSeqs `(273..277)`. seq=270 is NEVER delivered (would be 4 behind). Joiner re-PINGs forever; recovery wedges.

**Fix**: walk fromSeq..toSeq in seq order. Accumulate small consecutive bodies into `pendingBundle`. Whenever the run breaks (oversize body, empty body, cap-hit, end-of-window) FLUSH the pending bundle as a smaller bundle covering ONLY the consecutive prefix, then send singleton (or skip), then start fresh.

For 270..277 with oversize at 271/272/273 this produces:
- single-entry bundle [270] (consecutive)
- oversize singleton 271
- oversize singleton 272
- oversize singleton 273
- 4-entry bundle [274, 275, 276, 277] (consecutive)

Each bundle's effSeqs match what arcadia intended.

**Oversize boundary**: bodies > 250 B can't fit in the 1-byte length tail, so they're sent as standalone DATA at original seq (not in a bundle).

**Cache miss**: synthesize an empty ack-only fallback (11 B) so the receiver's expected-seq advances past the lost seq.

### PingEventLog (`Lobby/Reliability/PingEventLog.cs`)

Append-only file `logs/ping-events.txt` for PING-RX / CATCHUP-TX / BUNDLE-SENT / PHANTOM-DROP forensic events.

Gated on `DebugSettings.EnableFileLogging` (2026-07-15, threaded Pool → LobbyUdpServer ctor → PingEventLog). It previously wrote unconditionally — high-volume (100k+ lines over a few sessions), which clogs a VPS disk when file logging is meant to be off. `KickEventLog` stays unconditionally on: one line per kick is negligible and a missing kick line is itself a diagnostic signal.

### Proactive retransmit — reliable control delivery

Server-PUSH reliability for server-originated **one-shot control frames** that have no continuous stream behind them to expose a gap for the client's kind=4 pull-NACK: MT_GameReset (LobbySkate + both challenge phases + ResetWatchdog resends), GameAttributes / attribute-updates, challenge-start StartGame, the GM player-events (HOST_ROSTER_ELEM / PLAYER_JOIN_FULL_MESH / PLAYER_LEFT / VOIP_ENABLED_CHANGE) and host-leave/kick.

**Why it exists**: a relayed GameSync that's lost is always followed by a higher seq the client gap-detects on, so the client's own kind=4 NACK (PingRecovery) + outbound redundancy recover it. A one-shot control frame sent into a quiet window (e.g. a reset while the reset-gate has paused the GameSync stream to that peer) may have nothing behind it — the client never sees a higher seq, never NACKs, and the action is lost forever → that peer desyncs (starts a challenge alone, keeps a ghost player, etc.). The ResetWatchdog re-sends *resets* at 12s but is InProgress-suppressed during challenges and covers nothing else, so a faster, general backstop is needed.

**Mechanism** (tag-at-send, no body parsing):
- `EncryptedSender.SendDataAsync` takes a `reliable` flag; on a fresh reliable send it records the seq into `LobbySession.ReliableUnackedSeqs` (`SortedSet<uint>`, guarded by `SendLock`).
- `SendSystemBodyAsync` is inherently reliable (every caller is a GM control event); `SendSk8BodyAsync` takes `reliable` opt-in because it's shared with GameSync content — `RESET-HELD-RELEASE` / `RESEND-CAPTURED-GS` pass false. The relay fan-out, ack-only keepalives, catchup replays and recipe serves are all NOT reliable.
- The sweep (`LobbyUdpServer.MaybeProactiveRetransmitAsync`) runs in the 25ms DelayedAck loop. Under `SendLock` it prunes seqs the client has acked (`<= LastAckFromClient`) and, if any remain AND the client's cumulative ack has been frozen ≥ `ProactiveRetransmitStuckMs` (500ms) AND the last re-push was ≥ MinInterval (500ms) ago, snapshots up to `WindowCap` (16) seqs. The re-push `SendDataAsync` calls (explicitSeq — no seq burn, no re-cache) run OUTSIDE the lock. Log line `PROACTIVE-RETX(control)`.

**Relation to EA / CommUDP**: this is the arcadia-side stand-in for CommUDP's OWN sender-side retransmit timer (`sub_82ECE688 @ ~0x82ecebe8` — retransmit unacked reliable data if >0x64=100ms since last ack-bearing packet with unacked data, or >0x9C4=2500ms unconditionally). In EA's model reliability was end-to-end between the clients' CommUDP stacks, so the *sender's* CommUDP retransmitted any unacked reliable frame — control AND GameSync — automatically; there was no server push because no server terminated the reliable channel (relay forwarded, or P2P). Arcadia DOES terminate CommUDP per client and relays, so (a) it must provide the server→client half itself, and (b) it cannot naively re-push GameSyncs the way EA's timer did without overflowing the client's 32-slot recv ring — hence the control-only scope + 500ms (let the client's ~100ms CommUDP timer + redundancy try first). NOT a faithful copy of EA's mechanism; a pragmatic arcadia-specific backstop. *(IDA timer semantics are from prior notes — re-confirm `sub_82ECE688` before treating as gospel.)*

**Edge**: cumulative ack can stall behind an unrelated earlier GameSync gap, so a control seq the client already has (but can't cumulatively ack yet) may be re-pushed — harmless (client dedupes by seq), and it's pruned once the GameSync gap fills and the ack sweeps past it.

---

## Recipe

### RecipeAssembler (`Lobby/Recipe/RecipeAssembler.cs`)

Reassembles inbound Skate 2 MT_GameRecipeData chunks into one blob per uploader. Stale assemblers (no AddChunk for 30s) are swept by `RunRecipeAssemblerSweepAsync` so a partial upload can't leak memory or block `JoinBroadcasted`.

### RecipeService (`Lobby/Recipe/RecipeService.cs`)

Serves cached recipe blobs to peers that requested via MT_GameRecipeRequest. Emits Head + N × Data chunks, capped at 1024 bytes per chunk (MTU-safe after ProtoTunnel + CommUDP + NetGameLink overhead).

If the requested peer has no cached blob → fallback `Head(size=0)` which routes the requester to `StartLoadingDefaultRecipe` so it doesn't stall.

---

## Handshake

### HandshakeFlow (`Lobby/Handshake/HandshakeFlow.cs`)

Per-stage emit. `DriveAsync` chains back-to-back where appropriate (`HostHelloSent → HostRosterElemSent × N` and `JoinCompleteSent → GameAttribsSent → S2 recipe-request`). At `RosterAckReceived`, BEFORE PLAYER_JOIN_COMPLETE, it activates existing peers on the joiner creator-first via `PlayerEventBroadcaster.SendExistingPeerActivationsToNewcomerAsync` so the creator owns host actions (see Host actions).

Verified Skate 1 GameManager handlers match Skate 2 wire-byte-for-wire-byte:
- `OnPlayerJoinFullMeshReceived @ 0x82ac0e70` (S1) — same MakePlayerActive + Dispatch path as S2 `@ 0x8248c7e8`.
- `SystemPacketEncoder` ctor `@ 0x82acea78` (S1) — same +0x80 wire bias.

So `HandshakePackets.BuildHostRosterElem` / `BuildPlayerJoinFullMesh` / `BuildPlayerLeft` / `BuildVoipEnabledChange` are wire-compatible with both variants.

### Skate 1 vs Skate 2 final-handshake-step divergence

- **S2** at `GameAttribsSent`: emit MT_GameRecipeRequest, transition to `GameRecipeRequestSent`. Joiner uploads recipe via Data chunks → walker's RecipeData handler completes assembly → arms `PendingJoinBroadcast`.
- **S1** at `GameAttribsSent`: NO inline MT_GameReset send. Just arm `PendingJoinBroadcast` (the client self-loads its recipe, so the walker's RecipeData trigger never fires for S1). Transition to `GameResetSent`.

Both paths reach `PendingJoinBroadcast=true` → tail of HandleProtoTunnel fires `JoinFinalizeFlow.FireAsync`.

Previously S1 also sent an inline MT_GameReset here AND the deferred broadcast → joiner ran OnReset twice in quick succession (UI flicker + brief sim stall while CommandQueueMap re-aligned). Removing the inline emit fixed it.

### RosterBuilder (`Lobby/Handshake/RosterBuilder.cs`)

Roster snapshot frozen at HOST_HELLO send time so HOST_HELLO's `rosterElemSize` matches the exact set of HOST_ROSTER_ELEM packets that follow, even if other peers HELLO mid-emission.

`IsPeerReady` gate (`LobbyUdpServer.IsPeerReady`): a peer counts as ready when its UID is the host OR its session has reached `JoinCompleteSent`. Skipping not-yet-ready peers in the roster snapshot prevents phantom entries in the player list AND prevents phantom slots in `mPlayerPeerIDs[]` (whose GameSyncs would never arrive → every peer waits forever).

**Slot lifecycle** (`GameServerListing.AllocateSlot` / `ReleaseSlot` / `CompactSlots`): slots are sticky mid-epoch — `AllocateSlot` reuses the lowest freed slot, so a joiner arriving after a leave (before any reset) fills the gap and its roster view matches everyone's. Leaves only return the slot to the free pool; NO shifting happens at leave time (the in-epoch UI keeps the greyed slot; queues are removed via the `mLastFrame` machinery, and slots aren't re-read until the next epoch). `CompactSlots` runs at the top of `HandshakeFlow.BuildGameResetForSession` — the single choke point every reset flavor uses (LobbySkate, watchdog resend, both challenge flows) — reassigning slots 0..n-1 in current-slot order, so every broadcast table is a dense prefix. Why: the reset table is what clients rebuild their peer array from; an INTERIOR 0xFF slot after a leave ([A, FF, C]) materializes as a phantom player (empty-name entry in the player list) whose GameSyncs never arrive → post-reset lockstep stall for all survivors (observed S2 3-player → player 2 left → map change, 2026-07-18; trailing FF slots are harmless — every lobby below max capacity has them). Compaction covers the S1 shape too (host leaves 2P → survivor behind a slot-0 hole). Mid-handshake joiners keep their allocated slot through compaction (their entry is in `_slotByUid`), so an in-flight roster stays consistent with the next reset table.

`ResolveHostIp` — when bound on `0.0.0.0`, advertise something reachable (read `INT-IP` from `game.Data`, fall back to loopback). Roster elems carry this as the demangled address each remote peer will dial.

---

## Walker

### AppLayerWalker (`Lobby/Walker/AppLayerWalker.cs`)

Orchestrator for one CommUDP DATA payload's inner netGameLink body. Walks message-by-message, dispatching each to a static handler, building either:
- a copy of the source slice (hot path — no consumeOnly messages)
- a `relayBuilder` list that includes Skate 1 Broadcast → Broadcasted rewrite + omits consumeOnly messages

Final body parks into `session.OrderedPendingRelayBodies[srcBareSeq]` for the drain stage.

### WalkContext (`Lobby/Walker/WalkContext.cs`)

Mutable state passed to each per-message handler. Holds server/session refs, work buffer + offsets, `WalkOk` flag (set false on bail → break loop), `RewrapNextAsBroadcasted` (S1 wrapper state), `RelayBuilder` (lazy).

### SystemFrameDispatcher (`Lobby/Walker/SystemFrameDispatcher.cs`)

System frames (`sysFlag=0x01`):
- HELLO (`0x80`) — advances Stage → HelloReceived, populates PlayerInfo from UDP cache.
- ROSTER_ACK (`0x84`) — advances Stage → RosterAckReceived.
- RelayRequest (`0x8D`) with inner LostConnection — see HostKick path above.

Everything else is dropped (logged as WALKER-NO-PARK `reason=non-app-flag-or-undersized`).

### Per-Sk8 message handlers

#### GameSyncHandler

The post-reset stall protection. Two variants:

**Skate 1** — Wire (`Pack @ 0x82B10230`):
```
[4 BE mFrame][4 BE mCheckSum][1 cmdCount][N × [1 receiverID][1 mType][1 mDataSize][N data]]
```
No FirstFrame/OnlyInput/SizeFieldIsTwoBytes flag bits.

The S1 post-reset boundary signal is `mFrame == 1`. `SendCommands @ 0x825f5c00` writes the frame arg directly into mFrame after `SimController::Reset @ 0x827cdb98` sets `mNextGenFrame=1`. So `mFrame == 1` is the first post-reset emit.

If wire drops it: the 5s WAIT-FF-TIMEOUT force-clears so the filter doesn't stick forever.

**Skate 2** — uses `GameSyncMessage.TryParse` to read flag byte. `Sk8GameSyncFlag.FirstFrame` (`0x80`) is the post-reset marker.

**Why we filter** (verified end-to-end IDA on both binaries, live-confirmed S2 this session):

`OnlineNetwork::OnMessage case 2 @ 0x827a9c40` fires when MT_GameReset arrives:
1. `DoDestroyValues(mCommandQueueMap, mLocalQueue)` — destroys all per-peer CommandQueues + local queue (line `0x827a9da8`)
2. `mLocalQueue.mPtr = mCommandQueueMap.mBuffer.buffer[28]` (re-anchor)
3. `mResetInProgress = 1` (line `0x827a9db8`, stored as `mMissedSyncs.mBuffer.buffer[1013]`)

`OnMessage case 1` (GameSync arrival) when `mResetInProgress == 1`: `push_back` into `mMissedSyncs` instead of feeding `CommandQueue::AddCommands`.

`OnlineNetwork::OnReset @ 0x827a7438`:
1. `CleanUp(this)`
2. Loop 0..8 over PlayersBackEnd: alloc fresh CommandQueue per PT_Human, push into mCommandQueueMap. mArrival == mLocalID → mLocalQueue, else → `SyncChecker::AddPeer`.
3. Alloc fresh mAggregateQueue (peerID = -1)
4. If `mResetInProgress`: clear flag, replay `mMissedSyncs` through OnMessage case 1, destroy + clear mMissedSyncs.

`SimController::Reset @ 0x828a2c68` (live-confirmed this session):
- `mNextGenFrame = 1` (line `0x828a2ca4`)
- `Sk8::Simulation::Reset(randomSeed)`
- `ResetNetwork(this)`

`UpdateAggregate @ 0x827a9f18` only advances when every per-peer queue head matches the expected next frame.

**The race**: peer A finishes OnReset first. A's next GameSync (frame=1, FirstFrame=1) travels to B. If B's MT_GameReset hasn't arrived yet → A's GameSync routes to B's LIVE queue. Then B's MT_GameReset arrives, case 2 DESTROYS that queue including A's frame=1 entry. B's mMissedSyncs is empty during the impending OnReset. OnReset replays empty into rebuilt queues, B's queue for A is empty at frame=1 while B's local sim is at frame=1. UpdateAggregate stalls forever → SendCommands falls to Heartbeat (mRequest=7 S2 / mRequest=10 S1) → permanent heartbeat-only.

#### RecipeHandlers (consume only)

- HandleRequest (op 23 S2 / op 20 S1) — enqueue PendingRecipeRequests; tail of HandleProtoTunnel drains via RecipeService.
- HandleHeadSkate2 (op 24) — create/replace RecipeAssembler. Head(size=0) is the S1 default-recipe fallback; arcadia logs but doesn't create assembler.
- HandleDataSkate2 (op 25) — AddChunk → on completion `_blobs.Put` + arm PendingJoinBroadcast on the matching session.

#### GameRequestHandler

- StartGame (mRequest=1) — CONSUMED, kicks `ChallengeFlow.MaybeStart`.
- ToggleSlotAccess (mRequest=3, S2 only) — CONSUMED, emits server-side AttributeUpdate broadcast for IsPrivate state propagation. S2 client never sends a companion MT_GameAttributeUpdate; arcadia mirrors the state.
- PauseResume / LostConnection / Heartbeat / ConvertPrivateSessionSk1 — RELAYED verbatim.

S1 ConvertPrivateSession (mRequest=6) is intentionally not handled — Skate 1 lobbies are forced public at creation (FeslHandler), so there's no private→public transition for arcadia to track.

#### ChallengeLoadedHandler (S2 only)

Op 13, empty body. CONSUMED. Triggers `ChallengeFlow.TrackChallengeLoadedReport(uid)` which fires Phase 2 when all eligible peers report.

#### AttributesHandlers

- HandleGameAttributes — variant-aware (S1: 12 slots + 8B trailer; S2: 9 slots). RELAYED.
- HandleGameResetAttributes — variant-aware (S1: 12 slots no trailer; S2: 9 slots). RELAYED.
- HandleGameAttributeUpdateSkate2 — S2 only. Decode (attr, value) string. Triggers server-side mirror via `OnAttributeUpdate(IsPrivate, ...)` for state propagation.

#### ResultsHandlers

- HandleGameResults — variant-aware sizes (S1: 16 / S2: 15). Both variants capture (uid, eventTime-raw-bits, score, finishReason, ranking, playersChoice[S2]) into PeerGameResults for post-challenge aggregation.
- HandleGameComplete — triggers `OnGameCompleteFromPeer(uid)` → `PostChallengeFlow.RunAsync` (CAS gated). Beeps as audible confirmation.
- HandleGameFinalResultsSkate1 — 160 B fixed.
- HandleGameFinalResultsSkate2 — 4 B count + count × 48 B.

#### GameResetHandler

Just logging — server-emitted resets are what matter, peer-relayed resets are rare.

#### GameChangeHandlers

`HandleRequestChange` (op 16/17 by variant) — relayed + triggers `MapChangeFlow.RunAsync`.

#### GameSyncPointHandler (S2 only)

Op 17, empty body. RELAYED. Own The Spot scoring only; Spot Battle scores client-side with no UDP score message.

#### FixedFallbackHandler

Catch-all for fixed-size messages not handled explicitly (GameTimer, GameLoadRequest, GameRequestReset, GameChange, GameRemovePlayer, Skate1_GameLoadDone, Skate2 GamePlayerAttributes/Wager/Proposal, etc).

If `fixedLen < 0` for this variant's op, this hits WALKER-BAIL — anything outside the known op range for this variant (S1: ≥ 0x20 invalid, S2: ≥ 0x1A invalid).

### Skate 1 Broadcast → Broadcasted rewrite

S1 wraps every GameSync (and any message sent with destID HIDWORD=-1) in `Message_Broadcast` (op 1) when in client-server mode. Verified `OutboundMessageQueue::QueueMessage @ 0x825f38c0`.

The server is expected to rewrite `Broadcast → Broadcasted (op 2)` with the source peer's UID as `mFromID` before forwarding. Verified `InboundMessageQueue::Flush @ 0x825f3108` ONLY unwraps op==2 (`Broadcasted`): casts to `Message_Broadcasted`, replaces dispatched message with `mMessage`, uses `mFromID` as srcId.

Op 1 (Broadcast) falls through to `OnlineNetwork::OnMessage @ 0x825f6358` which has cases 4/5/0x13/0x17 only — no case 1, default returns silently. Inner GameSync is lost, receiver's SyncChecker starves (`mLocalChecksums` fill, `mFreeChecksums` depletes), SendCommands enters heartbeat-only branch → lockstep stalls.

`AppLayerWalker.WalkAppMessages` handles this inline (not in a separate handler file). When it sees op 1:
1. Activate `RelayBuilder` (backfill any preceding non-wrapper bytes from offset 1)
2. Arm `RewrapNextAsBroadcasted = true`
3. Step past the 1-byte wrapper

The inner message is parsed normally on the next iteration; on its emit branch the rewrap prepends `[02][8B sender.UID, BE]` before the inner bytes, producing a wire-correct Broadcasted.

Op 2 (Broadcasted) coming inbound from a client is unexpected (server-emitted only). Pass through verbatim — receiver's flush already handles op 2.

---

## Relay

### RelayPipeline (`Lobby/Relay/RelayPipeline.cs`)

Two top-level methods called from HandleProtoTunnel tail:

#### DrainAsync — coalescer

Pop entries from `OrderedPendingRelayBodies` in ascending srcBareSeq, batch up to `MaxRelayBatchBytes = 800`. Gate: skip if `firstEntry.Key > ClientAckSeq` (stoppedAtGate). Reorder check: drop if `firstEntry.Key <= LastRelayedSrcSeq` (DRAIN-DROP-REORDER). Batched body → `RelayAsync(combined, highestSrcSeq)`.

**Why 800B**: receiver's CommUDP recv ring is 32 slots (verified `Fesl::GameManagerParametersImpl::Init @ 0x82a4d7a8` → `SetGameNetworkQueueLength(32,32)`). Each delivered sub-packet consumes 1 ring slot. At Skate 1's ~50 GS/sec emit and ~85/sec arcadia outbound to a peer, sustained ingress already matches the joiner's drain rate; any catchup amplifies it past cap. Once ring full, `sub_82ECE248` returns 2 (silent drop, NO NACK, permanent wedge until drain catches up).

Receiver supports multi-message bodies natively (`Sk8::Net::InboundMessageQueue::OnReceive @ 0x825f3380` `do { ReadMessage(...) } while (mPos != mSize)`). So one CommUDP packet with N back-to-back Sk8 messages = ONE ring slot consumed = N Sk8 messages processed.

Budget 800 is well under receiver's element width (1272 B = `(1256 + 19) & 0x7FFC`, verified `CommUDPConstruct @ 0x82ecfad0`). Leaves headroom for ProtoTunnel envelope + CommUDP header + netGameLink trailer.

#### DrainResetGateReleaseAsync — release held bodies

Drains `PendingForResetReleaseToDst` the moment `WaitingForFirstFrameAfterReset` clears. Coalesces ≤800B per CommUDP frame for the same recv-ring reason.

The held backlog routinely reaches 30-40 bodies after a reset (logs: 36/37). One-packet-per-body would overrun the ring; ~2-3 ring slots per release after coalescing.

#### Release ordering — per-dst FIFO (2026-07-23)

Root cause of the semi-rare post-reset stall (the ~3s freeze rescued by a watchdog `resumed-then-froze` re-broadcast; log-proven `arcadia.639203603044666655.log`, and the 2026-07-18 4×resend GAVE-UP loop): the hold-vs-direct decision in `RelayAsync` read `WaitingForFirstFrameAfterReset` unsynchronized, and released bodies left the queue before their send. At a dst's FirstFrame clear a source body could either enqueue just after the drain had emptied the queue (strand-behind: OLDER frames released at a HIGHER seq than a newer direct-sent body — the proven case: release seq=458, direct srcSeq-6758 at seq=459, straggler srcSeq-6757 body at seq=460) or direct-send while a dequeued batch still awaited seq allocation (jump-the-queue). Client CommandQueues stamp arrivals in order (`mNextFrame`++ per add), so a permuted stream shifts input content by whole frames → checksum/lockstep wedge exactly one input-delay buffer (~1.6s) after flow-start. Wire signature at Info level: TWO `RESET-GATE-RELEASE` lines for one clear. Loss-independent — the proven session had zero OOO/NACK events; it is a pure thread-timing race, which is why it never correlated with network quality.

Invariant now enforced: per dst, bodies are delivered in enqueue order across the armed → flushing → direct transition. Mechanism, all in `RelayPipeline`:
- `RelayAsync` decides under the dst's `RelayLock`: `WaitingForFirstFrameAfterReset || PendingForResetReleaseToDst.Count > 0` ⇒ enqueue (behind any in-flight flush); else direct. Decision + enqueue are one critical section.
- `DrainResetGateReleaseAsync` peeks a ≤800B batch WITHOUT dequeuing — the backlog stays visible to concurrent deciders for the whole send — sends under a gate of `BarrierEpoch == generation && !WaitingForFirstFrameAfterReset`, and only dequeues the batch after the send returns a seq. If `BarrierEpoch` moved (re-arm), the wipe already retired the queue: abort without dequeuing (the generation guard is what stops the removal pass from eating NEW-epoch bodies enqueued after the wipe). The loop re-peeks until it observes empty under the lock, so stragglers enqueued mid-flush drain in order; once empty+unarmed is observed, direct sends cannot overtake anything.
- Single drainer per dst (only the dst's serial ProcessTail calls the drain), so peek/remove needs no further coordination. A send-level exception dequeues the batch anyway — retrying at a fresh seq could duplicate frames into the rebuilt CommandQueue, and a throwing socket means the peer is on its way out.

The arm-boundary direction (a decided-direct body racing a NEW reset's seq) was already covered by the `sendGate` at seq allocation; this section closes the clear-boundary direction. Together: no GameSync can be sequenced above a reset it predates, and no held GameSync can be sequenced above a frame that postdates it.

#### RelayAsync — fan-out

Per-peer fan-out with three filters:

1. **LoadingGate**: dst is loading (GameSyncsReceived==0 && !WaitFF). Strip GameSyncs from the body; if entire body was GS → suppress dst.
2. **RESET-GATE**: dst is pre-reset (WaitFF set). Enqueue body to `PendingForResetReleaseToDst` instead of forwarding.
3. **Schmitt backpressure**: `ServerDataSeq - PeerAckedOurSeq >= 32` → enter; `<= 8` → exit. Heartbeat-only body + backpressure → fully skip; GameSync-bearing body + backpressure → `dropOnWire=true` (cache + advance seq + skip wire; peer's NACK replays from cache later).

Otherwise: `EncryptedSender.SendDataAsync` with body wrapped in NetGameLink (sysFlag=App + trailer + kindByte 0x46).

### LoadingGate (`Lobby/Relay/LoadingGate.cs`)

Two helpers:
- `CountGameSyncsFast` — walk body, count GameSyncs (used by relay hot path when no dst is loading).
- `WalkRanges` — walk body, build per-message ranges (used when at least one dst is loading; needed for the strip).
- `TryBuildGameSyncStrippedBody` — produce copy of body with all GameSync messages elided. Returns null (fallback to original), empty array (suppress dst), or the stripped body.

Both walkers handle the S1 Broadcasted wrapper inline (skip past the 9-byte header and re-iterate on the inner op).

---

## Reset

### Barrier — `LobbySession.ArmWaitFirstFrameBarrierLocked`

Single source of truth for the FULL barrier-arming sequence used by EVERY reset path. Caller MUST hold `session.RelayLock`.

Fields cleared:
- `OrderedPendingRelayBodies` — in-flight pre-reset bodies parked at the walker; would otherwise drain into the relay at post-reset frames and poison the receiver's rebuilt CommandQueue.
- `PendingForResetReleaseToDst` — bodies destined for THIS peer that were held during a prior reset window. Without this clear, a stale pre-reset body from reset #1 could survive into reset #2's release pass and poison the post-reset queue.
- `PostResetGsCapture` — 'o' hotkey buffer; per-reset scope.
- `PostResetBatch{GsCount,FirstHeldAt,LastHeldAt}` — challenge post-reset coalescer counters; per-reset scope.

Clearing side:
- S2: walker sees `Sk8GameSyncFlag.FirstFrame` (0x80) → clear flag + log
- S1: walker sees `mFrame == 1` → clear flag + log
- Force-clear (5s): walker's in-branch WAIT-FF-TIMEOUT AND the heartbeat-reachable timer at the tail of HandleProtoTunnel.

The dual force-clear is needed because if a peer goes heartbeat-only after reset (SyncChecker starves before any GameSync reaches arcadia), `SendCommands @ 0x825f5c00` falls to Heartbeat-only and emits ZERO GameSyncs. The walker's GameSync-parse-branch clear never runs. Without the heartbeat-reachable timer, `PendingForResetReleaseToDst` would sit forever.

### ResetBroadcaster (`Lobby/Reset/ResetBroadcaster.cs`)

`BroadcastLobbySkateAsync(server, ct, resetAttributes = false)`:
1. Snapshot eligible peers (Stage ≥ JoinCompleteSent).
2. If `resetAttributes`: emit MT_GameAttributes(pre-reset) to every peer in parallel.
3. Arm WaitFirstFrame barrier on every peer (under each peer's RelayLock).
4. Send MT_GameReset(LobbySkate) to every peer in PARALLEL.
5. If host's session is in the eligible set, latch `Game.MarkCreatorResetSent()` (joinable gate).
6. Arm ResetWatchdog with `ResendLobbySkateAsync` as the retry function.

**Parallel sends, NOT staggered**: previous staggered design (slowest-first by GameSyncsReceived heuristic, 250ms gap) caused 50% of joins to deadlock because GameSyncsReceived measures cumulative emission count, NOT OnReset speed. Host fresh from OnlineHubMenu has gsRecv=0 but fast OnReset; joiner in solo LobbySkate has gsRecv=2000+ but slow OnReset on emulator. Heuristic flipped them, the "fast" peer was sent reset 250ms later, the "slow" peer finished OnReset first and emitted FirstFrame=1, which arcadia relayed to receiver still pre-reset, which landed in the LIVE queue. Then receiver's MT_GameReset arrived, case 2 destroyed that live queue including FirstFrame, mMissedSyncs was empty during the replay, receiver's queue for sender ended up empty at frame=1, UpdateAggregate stalls forever.

Parallel sends fix this because both peers enter `mResetInProgress=1` within ~RTT of each other. Whichever peer's FirstFrame=1 emit arrives first lands in the OTHER peer's mMissedSyncs (that peer is already in its own reset window). OnReset replays mMissedSyncs with frame=1, matching local sim. Walker's WaitFirstFrame filter handles the residual brief pre-reset → mid-reset window.

Stagger experiment was falsified by the controlled STALL/NO-STALL 1.5s pair (NO-STALL had the LARGER resume Δ yet recovered). Real root cause was the RESET-GATE-RELEASE ring-overrun, fixed by the 800B coalescer.

`ResendLobbySkateAsync` — LEAN re-send used by watchdog only. Same arm + parallel send pattern but does NOT re-arm the watchdog itself (the watchdog owns attempt bookkeeping).

`BroadcastSk8BodyAsync` — generic broadcast for any Sk8 app body, used by attribute updates / host-leave dissolve / post-challenge sequence / etc.

### ResetWatchdog (`Lobby/Reset/ResetWatchdog.cs`)

Post-reset re-convergence watchdog: detect heartbeat-only wedge after resume, re-broadcast the same reset up to 4× (≈94% recovery).

State lives on `LobbyUdpServer` fields (`Rw*`) so this module is fully static.

**Detection (50ms tick)**:
1. Baseline snapshot at arm: per-peer `GameSyncsReceived` stored in `RwBaselines` AND on `session.ResetWatchdogBaselineGameSyncs`.
2. "Resumed" = ALL watched peers have `!WaitingForFirstFrameAfterReset` AND `GameSyncsReceived >= baseline + 6`. **NOT a fixed timer** — separates legit cutscene (≈+1 from lone frame=1 emit) from real post-cutscene burst (≥+6 within one 60Hz tick).
3. If not resumed by `ResumeDeadlineMs (12000)` → fire retry (`never-resumed`).
4. Resumed → grace `PostResumeGraceMs (1000)` (absorb sub-frame gaps), then:
   - `FrozenConfirmMs` (S1: 3000ms, S2: 1500ms) no new GameSyncs → fire retry (`resumed-then-froze`)
   - **Pause-aware (2026-07-23)**: if any watched peer has `ClientPaused` set, the frozen check defers (`RwLastProgressAt` restarts; log `frozen check deferred`) instead of firing. `ClientPaused` tracks the S2 client's own `MT_GameRequest(PauseResume)` broadcasts (`GameRequestHandler`; S2-only — the S1 meaning of mRequest byte 5 is unverified). The S2 client announces every sim-pause window: challenge asset loads, and crucially the DeathRace-style race intro/countdown — PAUSE ~1s after challenge entry, RESUME ~7s later at "GO" (log-proven `arcadia.639203625567577581.log`). Without this, the intro's legit GS silence tripped `resumed-then-froze` at 2.5s and the resend armed an uncleariable barrier that consumed the entire post-GO input stream (already CommUDP-acked upstream ⇒ unrecoverable ⇒ permanent wedge). Since a paused sim also stalls the healthy-progress metric, `HealthyConfirmMs` still disarms 5s post-resume mid-pause — the intro then completes undisturbed.
   - `HealthyConfirmMs (5000ms)` sustained progress → disarm (`healthy`); must exceed FrozenConfirmMs so a wedge fires before healthy-disarm
5. Cap `MaxAttempts = 4`.
6. **Suppressed if `Game.InProgress`** — don't nuke an active challenge (post-challenge flow owns its own resets).
7. **Race guard**: a peer counts as "resumed" only once the walker has seen its GENUINE post-reset FirstFrame (which clears `WaitingForFirstFrameAfterReset`). A stale pre-reset GameSync that bumps `GameSyncsReceived` no longer spoofs "resumed".
8. **Watched-set reconciliation (each tick)**: a peer that left mid-window is pruned from `RwBaselines` and, post-resume, `RwLastGsSum` is rebased to the survivors' current sum. The progress metric is an aggregate sum over the watched set, so without the rebase a departure drops the sum by the leaver's entire lifetime GameSync count and reads as a multi-minute freeze → spurious `resumed-then-froze` reset fired at healthy survivors (observed: S2 3→2 voluntary leave inside the post-challenge window, 2026-07-17). `RwLastProgressAt` is deliberately NOT touched by the rebase — if the survivors really are wedged by the leave, frozen detection continues from genuine history instead of restarting.

**Audible markers** (Windows-only, fully best-effort): `(1200, 1200)` mid-double = DETECT/fire, `(300, 300)` low-double = suppressed (InProgress), `(900, 1500)` rising = healthy disarm, `(400, 650)` single low = gave up.

**Scope**: S1 + S2 LobbySkate path + S1 challenge path. S2 challenge (Phase 1/2) never calls Arm — challenge-start sets `InProgress=true` so the watchdog is structurally exempt.

### Player-leave re-sync (`MT_GameRemovePlayer.mLastFrame`)

A non-host leave mid-lockstep wedged the SURVIVORS until a manual `c` reset (S2 3-player → 2 after a peer PT-DISC'd; confirmed 2026-06-22). Root cause (IDA `sk82_na_m.xex`, fully traced): `MT_GameRemovePlayer` carries `mLastFrame`; the client (`OnlineNetwork::OnMessage` case 19) feeds it to `CommandQueue::OnRemove(frame)`, which KEEPS the departing peer's still-buffered input iff `frame == that queue's TAIL (newest) frame`, else `DoClear`s the whole queue (then `mRemove=1`). The survivors run ~1s behind (the lockstep input buffer), so their queue for the leaver still holds frames they haven't simulated. `mLastFrame` must be the leaver's LAST frame so the queue is kept → drained → the peer dropped via the `mWaitingForQueueRemoval` handshake in `OnlineNetwork::GetCommands` (which stalls when `>1 command queue` AND the aggregate isn't ready AND `!mWaitingForQueueRemoval`). We hardcoded `mLastFrame = uint.MaxValue` → never matches the tail → `DoClear` wipes the leaver's still-needed input → the removal handshake never resolves → permanent wedge. It is frame-EXACT or stall; there is no safe sentinel.

The frame IS recoverable: the client `CommandQueue` stamps each GameSync with `mNextFrame` (ctor sets it to 1, ++ per add), and queues are recreated on every `MT_GameReset` (case 2 destroys them). So the leaver's tail frame = the count of its GameSyncs since the current epoch — which the server tracks: `LobbySession.LockstepFrame` ++ per relayed (non-`consumeOnly`) S2 GameSync in `GameSyncHandler.HandleSkate2`, zeroed in `ArmWaitFirstFrameBarrierLocked` (every reset broadcast = the epoch boundary; matches the client recreating its queue at `mNextFrame=1`). On leave, `RemoveAndAnnounceLeaveAsync` sends `mLastFrame = removed.LockstepFrame` (S2). Clean in-place removal, no reset, and it works MID-CHALLENGE (the counter tracks whatever epoch is live — challenge resets zero it via the same barrier-arm). The `MT_GameRemovePlayer` is naturally ordered after the leaver's last relayed GameSync, so reliable in-order CommUDP delivery means each survivor has added frame N before it processes `OnRemove(N)` → tail matches → kept.

S1 keeps `uint.MaxValue` (unchanged): S1 is 2-player, so a leave drops to ONE survivor, and `OnlineNetwork::GetCommands` only stalls with `>1 command queue` — a solo survivor never wedges. The bug needs ≥2 survivors (S2 3+). The S2 host-leave dissolve path is separate.

---

## Challenge

### ChallengeFlow (`Lobby/Challenge/ChallengeFlow.cs`)

Entry: `MaybeStart(server, source)` — triggered when walker sees `MT_GameRequest(StartGame, mRequest=1)`.

1. CAS via `server.Game.TryStart()` — single Interlocked.CompareExchange against the lobby's startup gate. Back-to-back map-change + challenge presses produce exactly one accepted flow.
2. Set `ChallengeAwaitingReady = true` (or no-op if already set).
3. **Skate 1 sync barrier arm** (`ArmSyncBarrier`) — closes the StartGame→async-arm race.
4. Task.Run `Phase1Async`.

`TrackChallengeLoadedReport` fires Phase 2 once every eligible peer has reported; `OnPeerLeft` (called from `RemoveAndAnnounceLeaveAsync`) re-evaluates that readiness when a peer leaves mid-load, so a leaver who was the last pending reporter can't strand the survivors in ChallengeLoadState (see Flow interlocks matrix). `ForceStartAsync` ('r') claims the same `TryStart` gate as the in-game button.

#### Skate 1 sync barrier — why?

`MaybeStart` runs SYNCHRONOUSLY inside the walker under the source session's RelayLock on the very packet that carried `MT_GameRequest(mRequest=1)`. The Skate 1 client packs that StartGame request AND a pre-reset `Message_Broadcast(GameSync)` into the SAME netGameLink body. The pre-reset filter (`WaitingForFirstFrameAfterReset`) was previously armed only async in `Phase1Async` → between Task.Run dispatch and actual flag flip:

1. The StartGame consume completes
2. The trailing same-body GameSync gets parked in `OrderedPendingRelayBodies`
3. HandleProtoTunnel's drain coalescer fires
4. GameDataRelay's RESET-GATE held it for still-pre-reset destinations
5. When the dst emits its OWN post-reset FirstFrame=1, the held body is released
6. The pre-reset frame number (e.g. 0x30F=783) lands in the post-reset CommandQueue
7. `GetCommands @ 0x825f5e78` requires every per-peer queue head's frame to EXACTLY equal the requested frame; mismatch → break → return 0 → `SendCommands @ 0x825f5c00` falls back to Heartbeat (mRequest=10) → permanent stall

MT_GameReset is inert inside ChallengeSkate so the ResetWatchdog can't recover it.

**Fix** — `ArmSyncBarrier` arms the barrier for every eligible peer SYNCHRONOUSLY here, before the Task.Run, so the source filter is already on when the walker continues to the trailing GameSync in this same body (and every subsequent packet). Those are then stripped at parse time (consumeOnly = true) and never parked, drained, relayed, or held.

`Skate1StartChallengeAsync` still re-arms at the real send time (refreshes WAIT-FF 5s anchor); the flag stays continuously true between the two arms, so no leak window exists.

Scoped to Skate 1 — the verified scope of this defect; Skate 2's two-phase path is server-initiated, never consumed from a client packet co-bundled with a pre-reset GameSync.

### Skate1ChallengeFlow (`Lobby/Challenge/Skate1ChallengeFlow.cs`)

Single-phase: send `MT_GameReset(mResetType=Reset_Challenge=1, activity)` to every eligible peer in parallel.

Verified S1 `UpdateGameInfo @ 0x82591bc0`:
```
cmplwi cr6, r11, 1       ; mResetType vs 1
blt    cr6, free_skate   ; < 1 → GM_OnlineFreeSkate
bne    cr6, skip         ; > 1 → no-op (skips challenge setup)
; mResetType == 1: sets sGameInfo.gameMode = GM_OnlineChallenge + challengeKey from mChallengeKey
```

So S1 Reset_Challenge == 1.

`HandleLobbySkateReset @ 0x82591eb0` `cmpwi cr6,r11,1 / bne cr6, lobbyskate` — mResetType=1 path: CloseFEScreens → ResetGame(1) → StartMainChallenge(challengeKey) → TransitionTo(ChallengeSkate).

NIS cutscene plays as a substate of ChallengeSkate, not a separate reset (`ChallengeSkate::Event_Update else if (mLoadingNIS) @ 0x82590c10`; `mLoadingNIS` set ONLY in `FilterImpl case MESSAGE_NIS @ 0x8258f778`). Arcadia emits zero MESSAGE_NIS so the ResetWatchdog retry path doesn't replay the cutscene.

`ResendAsync` — targeted watchdog retry (2026-07-23): re-sends ONLY to peers whose barrier is still armed, with NO `ResetGate.Close` and NO `RecordResetSent` (same epoch — the original `ResetSeqToDst` stays the ack-causality anchor; if the original reset was genuinely lost, the resend is that client's first reset and its FirstFrame ack covers both seqs). Why: MT_GameReset is inert inside ChallengeSkate, so a peer whose barrier cleared has already processed the reset and a resend cannot help it — while the old full re-Close actively harmed it: the re-arm wiped its live-epoch `PendingForResetReleaseToDst` bodies (already CommUDP-acked upstream, so gone forever — the survivors' CommandQueues get an unfillable hole) and armed a barrier whose clear needs `mFrame ≤ 20`, unreachable mid-challenge, leaving only the 15s force-clear. All peers cleared ⇒ resend skipped entirely (`RESEND skipped` log): the wedge, whatever it is, is not reset loss.

### Skate2ChallengeFlow (`Lobby/Challenge/Skate2ChallengeFlow.cs`)

Two-phase:

**Phase1Async**:
1. NO barrier arm (Phase 1 is asset load only — see below).
2. Parallel `Task.Run` per peer: `BuildGameResetForSession(ChallengeLoad, activity)` → MT_GameReset + `StartGame` MT_GameRequest mirror.
3. Each peer transitions to ChallengeLoadState, asset-loads, emits `MT_GameChallengeLoaded` back to arcadia.

**Phase2Async** (fires when all reports received):
1. Arm WaitFirstFrame barrier INLINE under each peer's RelayLock.
2. Parallel `Task.Run` per peer: `BuildGameResetForSession(Challenge, activity)` → MT_GameReset.

`ResendPhase2Async` — targeted watchdog retry (2026-07-23), same shape as the S1 resend: still-armed peers only, NO `ResetGate.Close`, NO `RecordResetSent` (original `ResetSeqToDst` stays the ack-causality anchor), all-cleared ⇒ `phase2 RESEND skipped` log. Evidence: `arcadia.639203625567577581.log` — a `resumed-then-froze` misfire during a DeathRace intro re-Closed epoch 4 and resent Reset(Challenge); NO epoch-4 FirstFrame ever arrived (the in-challenge client runs no new SimController::Reset for it — inert, matching S1), so the re-armed barrier consumed the entire post-"GO" GameSync burst (visible as gsTotal bumps with zero clears) → both sims starved → permanent wedge the remaining attempts could not fix. A still-armed peer is the one case a resend helps: it is sitting in ChallengeLoadState, and Reset(Challenge) arriving there runs the normal phase-2 path → SimController::Reset → FirstFrame → clear.

#### Why Phase 2 ONLY arms the barrier

Verified `HandleLobbySkateReset @ 0x82776f90` (live-confirmed this session) branch dispatch:

- `mResetType == Reset_Challenge (=2)` → `CloseLoadingPopup` → `PopAllStates` → `ResetGame(mActiveModule, 1, 1, activity)` → `GameModule::ResetGame @ 0x8273a680` calls `SimController::Reset @ 0x828a2c68` UNCONDITIONALLY → `mNextGenFrame=1` → FirstFrame on next emit.
- `mResetType == ChallengeLoad (=1)` → `v4 = ChallengeLoadState`. **NO `ResetGame`, NO `SimController::Reset`, NO FirstFrame**.
- `mResetType == LobbySkate (=0)` → `ResetGame(mNewGameReset, !mIsFreeSkateHere, ...)` → SimController::Reset → FirstFrame (conditional on mNewGameReset).

Arming the barrier on Phase 1 ChallengeLoad would leave the flag unclearable via FirstFrame (it never gets emitted) and fall to the 5s force-clear, whose safety reasoning doesn't hold at challenge-entry. Phase 2 IS the SimController::Reset trigger, so FirstFrame is the natural clearing signal.

---

## Flow

### JoinFinalizeFlow (`Lobby/Flow/JoinFinalizeFlow.cs`)

Fires from the `PendingJoinBroadcast` block in the HandleProtoTunnel tail:

1. `BroadcastJoinAsync` — HOST_ROSTER_ELEM + PLAYER_JOIN_FULL_MESH to existing peers, and existing peers to the newcomer.
2. `BroadcastVoipDisabledForAllAsync` — auto-disable VoIP icons.
3. `ReleaseJoinGate` — race-prone region over, next joiner may proceed.
4. CAS `Game.LatestJoinFinalizationIndex` (latest-wins debounce). Detached task:
   - **Wait on the bidirectional RECIPE MESH** (`MeshWaitCeilingSeconds = 20`): every existing peer must have been served AND CommUDP-acked the joiner's recipe, AND the joiner served+acked every existing peer's. `ServeAcked(dst, uid)` = `dst.RecipeServeSeqByUid[uid]` present AND `dst.LastAckFromClient >= seq`.
   - `SettleSeconds = 5` settle (clients finish assembling/loading; catchup recovers tail loss).
   - Re-check the CAS; skip if a newer joiner finalization arrived (its reset covers us).
   - `BroadcastLobbySkateAsync` — the single MT_GameReset both peers receive.
   - If the mesh **timed out**, arm `RunLateRecoveryAsync` (`LateRecoveryWindowSeconds = 30`): watch for the mesh to complete, then fire ONE recovery reset so the late recipe applies at a fresh spawn (suppressed if a challenge/map flow grabbed InProgress).

Replaced the old `RecipeServed`-flag + flat-5s gate, which was satisfied by the joiner's own recipe self-echo and so didn't cover the cross-peer exchange at all — the reset routinely beat the joiner's request for existing peers' recipes (5-16s after upload), leaving default/clone skaters + physics desync.

### MapChangeFlow (`Lobby/Flow/MapChangeFlow.cs`)

Triggered when walker sees `MT_GameRequestChange`:
1. CAS `Game.TryStart()` (paired with ChallengeFlow's CAS so back-to-back presses produce exactly one accepted flow).
2. CAS `server.MapChangeInProgress`.
3. `WaitForJoinersAsync("MAP-CHANGE", 30s)` — block until every in-flight joiner is past their deferred post-handshake reset.
4. Update `Game.Data["B-U-challenge_key"]` + `B-U-challenge_type` (mapped via `Sk8NetChallengeType.ToName`).
5. Broadcast `MT_GameChange(type, key, changeTime=3s)`.
6. 3s delay (announcement window).
7. **S2**: broadcast `MT_GameAttributes(newKey, ...)` via `ResetBroadcaster.BroadcastSk8BodyAwaitAckAsync` — send reliable, then **block until every peer's `LastAckFromClient` reaches the GameAttributes seq** (delivery-confirmed), bounded by `GameAttributesAckWaitMs = 4000`. This is the ack-gate: the new map rides on GameAttributes, and CommUDP in-order delivery is not enough on a lossy link — a lost GameAttributes can be skipped (SentFrameCache eviction → synthesized ack-only) or dropped by the client's full 32-slot ring, so the reset would restart the sim in place (old map). Confirming delivery before the reset closes that window. On timeout it proceeds anyway (bounded, best-effort).
8. **S1**: plain-broadcast `MT_GameAttributes` then **immediately** broadcast `MT_GameChange(commit, t=0, mChange=1)` via `BroadcastSk8BodyAwaitAckAsync` + 5s delay. The pair MUST leave back-to-back (consecutive seqs, same instant — wire-proven May-18): the commit ARMS the pending change and the later reset EXECUTES it (RecycleGame); an unarmed reset is respawn-in-place. Any wait inserted between attribs and commit (the 07-19 ack-gate briefly sat there) lets the attribs re-baseline the client's current activity first, so the commit reads as no-change and never arms — user-verified broken 07-21, fixed same day. The ack-gate anchors on the COMMIT seq instead: acks are cumulative, so commit-acked ⟹ attribs delivered; lossy-link protection is preserved without splitting the pair.
9. `BroadcastLobbySkateAsync` (this is the actual reset that re-epochs).
10. Clear `Game.InProgress` + `MapChangeInProgress`.

> `BroadcastLobbySkateAsync(resetAttributes: true)` (post-challenge return-to-freeskate, `PostChallengeFlow` ×2) had the same GameAttributes-then-reset gap and is now ack-gated the same way (`SendReliableCapturingSeqsAsync` + `AwaitPeersAckedAsync`, bound `GameAttributesAckWaitMs`). Other send-then-reset flows are already gated by a stronger client signal: challenge Phase1→Phase2 waits for every peer's `GameChallengeLoaded` report; the join-finalize reset waits for the recipe-mesh delivery confirmation. Announce/commit/FinalResults are intentionally NOT gated (cosmetic timer / score-non-critical).

S1 routes real world reload ONLY through `MT_GameChange(mChange=1) → RecycleGame`. `MT_GameReset` hijacks into respawn-in-place. Skate 2 is opposite (Reset path). The S1 commit byte step is the variant divergence.

### PostChallengeFlow (`Lobby/Flow/PostChallengeFlow.cs`)

Fired from `OnGameCompleteFromPeer` CAS gate (`PostChallengeBroadcastInProgress`) on FIRST peer's MT_GameComplete report.

**Skate 2** path (populated scoreboard since 2026-07-02; mirrors the S1 recipe):
1. Broadcast AllPlayersComplete.
2. Collect every eligible peer's MT_GameResults (`Skate2ResultsCollectTimeout = 10s` ceiling, early-exits when all in). Log-proven (arcadia.639184348103875143.log, six S2 post-challenges): S2 clients emit GameResults spontaneously ~100–500ms after their own GameComplete — the flow fires on the FIRST GameComplete, so the data always used to arrive ~60ms AFTER the old code had already broadcast `FinalResults(empty)`. That ordering was the entire blank-scoreboard bug.
3. Build populated `BuildGameFinalResultsSkate2` sorted by client-reported ranking (empty fallback if nobody reported). Rows are filtered to peers still present in `Sessions` — see S2 scoreboard below.
4. Broadcast FinalResults.
5. `Skate2ResultsDwell = 4s` dwell (board display window).
6. Broadcast ExitPostChallenge.
7. 3s delay.
8. `BroadcastLobbySkateAsync(resetAttributes=true)`, clear gates, clear `PeerGameResults`.

**Why S2's per-round `PeerGameResults.Clear()` lives in `Skate2ChallengeFlow.Phase2Async`, NOT at the top of `RunSkate2Async`** (S1 keeps its flow-start clear): S2 peers pack GameComplete AND their GameResults into the SAME netGameLink body (log: 0.7ms apart, same walk). The walker stashes the row inline right after `OnGameCompleteFromPeer` fires the post-challenge `Task.Run` — so a flow-start clear races the first completer's own stash and can wipe it (missing row on the board for whoever finished first). Clearing at Phase 2 (the round's epoch) is race-free: rows can only accumulate after it. S1 is immune to the race — its clients emit GameResults only ~10s later, after AllPlayersComplete pulls them through SendPlayerResults — so its flow-start clear stays.

#### S2 scoreboard (consumer semantics, IDA-verified 2026-07-02)

`StateOnlineGame::OnMessage case 9 @ 0x8276b36c` (`sk82_na_m.xex`) is the ONLY consumer of MT_GameFinalResults. On arrival it fills `FrontEndState_LeaderBoard` directly from our rows — the board renders nothing else:

- `mNumPlayers = mPlayerCount` — count 0 = the old blank board.
- Per row: `PlayersBackEnd::GetPlayerIndexByPeerID(peerId)` maps the row's UID to the local player index (that's how names attach), then `SetPlayerData(idx, ranking, score, exp, …)` and `mPlayers[idx].time = eventTime`. **An unknown peerId resolves to index -1 and `mPlayers[-1].time = …` writes before the array** — why the builder rows are filtered to currently-present sessions.
- `playersChoice` row → `mOnlinePlayersChoice = idx` (highlight); on a ranked match the local player's playersChoice row also grants the Skater's Choice achievement.
- The column layout (`eResultsLBType`) is derived client-side from the active challenge type — nothing for arcadia to send.
- On the row whose peerId matches the local player: `finishReason == 1` sets "normal completion", **`cash > 0` → `cSignPostManager::AddCash(cash)` — the client credits its wallet straight from our packet — and `exp` feeds `SetPlayerProgressionPoints`**. That is why cash/exp/wager/winnings are hard-zeroed in the builder; populating them is a (deliberate, future) payout feature, not a display field.

`eventTime` is stored/relayed as raw f32 bits end-to-end (`PeerGameResults.EventTime` holds raw wire bytes for BOTH variants: S1 u32, S2 f32 bits) so no float round-trip touches the value.

ExitPostChallenge timing: S2 does NOT have S1's stash-insta-pop defect (the blank board stayed up ~3s with exit sent back-to-back), but the 4s dwell before exit is kept anyway — it matches S1's proven UX and guarantees the display window regardless of when the client opens the board.

**Skate 1** path (verified `sk8_na_f.xex`, dwell + results aggregation are S1-specific UX requirements):

1. Clear `PeerGameResults` (only this challenge counts).
2. Broadcast AllPlayersComplete — pulls every peer out of ChallengeSkate → WaitingForGameToFinish → SendPlayerResults, whose Enter (`ReportLocalPlayerResults @ 0x82592990`) broadcasts that peer's own `Message_GameResults` back to arcadia.
3. Collect every eligible peer's GameResults (20s ceiling; early-exits when all in).
4. Aggregate into ONE populated GameFinalResults sorted by ranking (the standings the EA authority server would have built). Empty fallback only if literally nobody reported.
5. Broadcast `MT_GameFinalResults(populated_rows)`.
6. **4s dwell** (`Skate1ResultsDwell`) — see below.
7. Broadcast ExitPostChallenge.
8. 1s delay.
9. `BroadcastLobbySkateAsync(resetAttributes=true)`.
10. `Game.InProgress = false; MarkJoinable()`.
11. Cast reset once immediately, then 1s delay, then once more (probes edge case where client reaches LobbySkate slightly later than first reset).
12. Clear `PeerGameResults`.

**Why the 4s dwell** (verified `sk8_na_f.xex` 2026-05-18):
- `StateOnlineGame::OnMessage @ 0x82592648` case 0xD stashes MT_GameExitPostChallenge into `mExitResultsDisplayMsg` UNCONDITIONALLY — no current-state gate — and the ptr field is never re-nulled by states that consume it.
- `DisplayFinalResults @ 0x82590fe8` leaves on the FIRST `Event_Update` where `mExitResultsDisplayMsg.mPtr != 0`; there is NO timer-gated exit, and Event_Exit does `PopHUDState` (tears the results screen down).

If ExitPostChallenge is sent back-to-back after FinalResults, it's already stashed by the time the client traverses ChallengeSkate → WaitingForGameToFinish → SendPlayerResults → DisplayFinalResults (a few frames). The results HUD is popped ~1 frame after it is pushed → S1 "shows literally nothing". Holding ExitPostChallenge for 4s lets the client sit in DisplayFinalResults and actually render.

**Why the 20s collect timeout** (proven by arcadia.639146976493493412.log 2026-05-18): in a 2P lobby the clients take ~10s AFTER AllPlayersComplete to traverse WaitingForGameToFinish → SendPlayerResults and broadcast GameResults (measured ~10.0s on two separate post-challenges, across VPN). Old 5s window timed out ~5s BEFORE real scores arrived → empty board every time; 1-player worked only because the solo client (not lockstep-coupled) reports fast. 20s = 2x observed traversal with margin.

### HostLeaveFlow (`Lobby/Flow/HostLeaveFlow.cs`)

- `BroadcastHostLeaveDissolveAsync(reason, ct)` — S2 only. Cast `MT_GameRequest(LostConnection)` to every remaining peer. Their `StateOnlineGame OnMessage case 3 mRequest==4` shows the LOSTCONNECTION chyron + leave-state flags. No PLAYER_LEFT follow-up — every recipient self-leaves on the kick.
- `HandleHostKickAsync(destRef, ct)` — invoked from the walker's host-kick detection. Matches `destRef` to a session, sends LostConnection unicast, then calls `RemoveAndAnnounceLeaveAsync` for normal PLAYER_LEFT broadcast to the remaining peers.

The host-leave dissolve is gated to Skate 2. A Skate 1 host leaving falls through to the normal MT_GameRemovePlayer + PLAYER_LEFT path (hostLeft=false), exactly like any other peer leaving.

---

## Flow interlocks — edge-case matrix

Audited end-to-end 2026-07-03 (every guard verified in code). The four 2026-07-03 entries are fixes from that audit; everything else predates it.

### Join ↔ flow
- **New join during a flow** — `TheaterHandler.HandleEGAM` rejects any non-host EGAM while `Game.InProgress` (closes the direct-GID/invite bypass); all four `ConnectionManager.Find*` matchmaker queries filter `!InProgress && CanJoin`. So no join can *start* during a map-change/challenge/post-challenge.
- **In-flight join blocks a flow** — `WaitForJoinersAsync(ctx, 30s)` in both MapChangeFlow and ChallengeFlow Phase1 waits on `Game.JoiningCount` (EGAM-reserved, pre-UDP) + every session with `!JoinBroadcasted || PostJoinResetPending` (mid-handshake through deferred reset). 30s ceiling > worst-case deferred-reset path (20s mesh ceiling + 5s settle).
- **Join vs join** — `_joinAdmissionGate` (1 permit, 30s backstop, released on finalize/removal) serializes the race-prone roster region; `LatestJoinFinalizationIndex` latest-wins debounces stacked deferred resets.
- **Stale joiner** — TCP teardown removes from `_joining` (`RemoveJoiningPlayer`); a pre-HELLO UDP ghost is bounded by the 30s silent-peer kick. `WaitForJoinersAsync` therefore can't be held >30s by a corpse.

### Flow ↔ flow
- **Map-change vs challenge-start** (and double-presses of either) — single `Game.TryStart()` CAS shared by both + `MapChangeInProgress` re-entry CAS + `ChallengeAwaitingReady` dedupe. Exactly one flow wins, the loser is logged + ignored.
- **'r' force challenge** — `ForceStartAsync` claims `TryStart` like the in-game path (2026-07-03; previously it bypassed the gate and could run a challenge concurrently with a map-change) and self-heals gates on throw.
- **Start/map-change during the post-challenge scoreboard** — `InProgress` stays held until post-challenge end, so `TryStart` fails.
- **Post-challenge entry** — `OnGameCompleteFromPeer` requires `Game.InProgress && MapChangeInProgress == 0` (2026-07-03; a stray or post-flow-duplicate MT_GameComplete previously fired the full scoreboard+reset sequence from freeskate or mid-map-change) + the `PostChallengeBroadcastInProgress` CAS for concurrent completers.

### Leave ↔ flow
- **Leave mid-lockstep** — `MT_GameRemovePlayer` with `mLastFrame = LockstepFrame` (S2, frame-exact in-place removal) / `uint.MaxValue` (S1 — solo survivor can't wedge).
- **Leave during S2 challenge-load (Phase1→Phase2 window)** — `ChallengeFlow.OnPeerLeft` from `RemoveAndAnnounceLeaveAsync` (2026-07-03): if the leaver was the last pending ChallengeLoaded reporter, Phase 2 fires for the survivors; if no eligible peers remain, gates clear. Previously the survivors sat in ChallengeLoadState indefinitely. Runs AFTER PLAYER_LEFT so survivors process the leave before the phase-2 reset.
- **Leave during results collect** — `StillPresent` prune in both variants' collect loops (2026-07-03): a peer who quits at challenge end no longer stalls the board the full 10/20s ceiling.
- **Leave during any broadcast sequence** — every broadcast step re-snapshots eligible peers (`SnapshotEligible`), so departed peers just drop out of later steps; scoreboard rows are additionally filtered to present sessions.
- **Host leave / host kick** — S2 dissolve vs S1 normal-leave split; kick via RelayRequest 0x8D. Kicked/dissolved peers exit through `RemoveAndAnnounceLeaveAsync`, so all the above applies to them too.

### Recovery ↔ flow
- **ResetWatchdog re-broadcast** — suppressed while `Game.InProgress` (can't stomp a live challenge); generation counter invalidates retries across re-arms.
- **Recipe late-recovery reset** — suppressed while `InProgress`, joiner-presence + latest-finalization guarded.
- **'c' force reset** — clears `ChallengeAwaitingReady` + `MapChangeInProgress` + `InProgress` BEFORE broadcasting (2026-07-03; previously aborting a wedged challenge with 'c' left the lobby unjoinable + rejecting START_GAME indefinitely).
- **Backstops** — every flow body clears its gates in catch/finally.

---

## Host actions

Who sees the in-lobby host-actions menu (switch map, start game, public/private toggle) is decided ENTIRELY client-side by `Sk8::Net::IsHost @ 0x826411a8` (gates `FrontEndState_OnlineHubMenu::PickCurrentState` / `OnMenuSelect` and `StateOnlineGame::FilterImpl`):

```
IsHost() = GetGameHostPeerID() == GetLocalUser()->GetUserId()
```

`Sk8::Net::GetGameHostPeerID @ 0x82641098` walks the game's active-player list and returns the 64-bit `GetUserId()` (player vtbl `+0x8`) of the **first player whose `GetPlayerType()` (player vtbl `+0x14`) == 0**; players with type ≠ 0 are skipped; `-1` if none. So the host is "the first PLAYER-type peer in activation order," and a client owns host actions iff that peer is itself.

- **Active-player order** = `MakePlayerActive @ 0x8248bfc0` PushTail order. Activation triggers: `OnPlayerJoinFullMeshReceived @ 0x8248c7e8` (a remote peer's PLAYER_JOIN_FULL_MESH) and `OnJoinCompleteReceived @ 0x8248baa0` → `MakePlayerActive(mLocalPlayer)` (the LOCAL player, on its own PLAYER_JOIN_COMPLETE). Roster elems do NOT auto-activate: `FromBuffer @ 0x824971f0` never writes `mPlayerState (+0x2C)`, and `AddPlayerFromBuffer @ 0x8248bee8` only activates when `mPlayerState == JOINED (6)`.
- **`mPlayerType` sources** — local player: Theater EGEG `PTYPE` tag, `TheaterEnterGameHostRequestResult` ctor `@ 0x82445288` does `mPlayerType = (PTYPE[0] == 'O')`. Roster peers: the playerType byte in RosterPlayer, `FromBuffer @ 0x824971f0` does `mPlayerType = (byte == 1)`. So `0` / no-PTYPE → PLAYER (type 0); `1` / `'O'` → OBSERVER (type 1).

### The bug (rewrite regression)

`RosterPlayer.ActivePlayerType` was `1`, so every HOST_ROSTER_ELEM / PLAYER_JOIN peer decoded to **OBSERVER (type 1)** and the host-scan skipped it. arcadia's EGEG sends no `PTYPE`, so each client's own local player was the ONLY PLAYER (type 0) it could see → `GetGameHostPeerID` returned the local user's UID on every client → **every player saw host actions**. (The `1 = active` note in RosterPlayer's wire table was the trap — wire byte 1 decodes to OBSERVER, not active.)

### The fix (two parts)

1. `RosterPlayer.ActivePlayerType = 0` → peers decode to PLAYER (type 0), so the host-scan actually considers them.
2. With all peers PLAYER, the host is the **first-activated** one. The joiner self-activates on its own PLAYER_JOIN_COMPLETE (mid-handshake), which would otherwise put it first. So existing peers — **host/creator first** (`Game.UID` sorted ahead) — are activated on the joiner BEFORE that: `HandshakeFlow` at `RosterAckReceived` calls `PlayerEventBroadcaster.SendExistingPeerActivationsToNewcomerAsync` (one PLAYER_JOIN_FULL_MESH per existing peer) before emitting PLAYER_JOIN_COMPLETE. The old post-recipe newcomer-direction send (BroadcastJoinAsync step 2) was removed — by then the peer can be `JOINED (6)` on the joiner and a repeat HOST_ROSTER_ELEM would duplicate it in `mActiveList`.

Result: the creator is the first PLAYER in every client's active list, so only the creator's `IsHost()` is true. Coverage for a peer that readies AFTER this joiner's roster snapshot is still met by that peer's own `BroadcastJoinAsync` (it announces itself to this joiner, appended after the creator).

---

## PlayerEvents

### PlayerEventBroadcaster (`Lobby/PlayerEvents/PlayerEventBroadcaster.cs`)

Mid-session broadcasts:

#### BroadcastJoinAsync

Tells existing peers (each in their own session) about the new joiner: `HOST_ROSTER_ELEM(joiner)` + `PLAYER_JOIN_FULL_MESH(joinerRef)`. The new joiner is activated AFTER the existing peers in each existing peer's active list, so it never displaces them as host.

The reverse direction (newcomer learns + activates existing peers) is NOT here — it runs during the joiner's own handshake: HOST_ROSTER_ELEM × N (RosterBuilder snapshot) puts existing peers in mPlayerList, then `SendExistingPeerActivationsToNewcomerAsync` activates them creator-first BEFORE the joiner's PLAYER_JOIN_COMPLETE (see Host actions). It was moved out of this post-recipe path because by then an existing peer can already be `JOINED (6)` on the joiner, and a repeat HOST_ROSTER_ELEM would re-fire `AddPlayerFromBuffer`'s `if (mPlayerState == 6) MakePlayerActive` → a DUPLICATE `mActiveList` PushTail.

**Why HOST_ROSTER_ELEM not PLAYER_JOIN**: Both feed `AddPlayerFromBuffer` which adds to mPlayerList, but PLAYER_JOIN's handler ALSO calls `Dispatch<OnPlayerJoin>` when `mGameState == ACTIVE`, which is then dispatched a SECOND time by the follow-up PLAYER_JOIN_FULL_MESH — host sees "X has joined" twice. HOST_ROSTER_ELEM doesn't dispatch.

**Roster-count safety**: host's `mRosterElemSize` was frozen at solo-join time (typically 1). Sending HOST_ROSTER_ELEM mid-session bumps mRosterElemCount past mRosterElemSize, so the `if (count == size) send ROSTER_ACK` branch in OnHostRosterElemReceived stays inert.

**PLAYER_JOIN_FULL_MESH (wire 0x88)** is the activation step — `OnPlayerJoinFullMeshReceived @ 0x8248c7e8` does `GetPlayerByRefInternal + MakePlayerActive + Dispatch`, which downstream broadcast/voip/dispatch gates need.

#### BroadcastVoipDisabledForAllAsync

One `VoipEnabledChange(playerRef=peer, enabled=0)` from each peer to every peer (incl. self — receiver tolerates self-target, just updates the speaker UI). After every join broadcast so newcomers see existing peers muted and existing peers see the newcomer muted.

#### BroadcastLeftAsync

`PLAYER_LEFT(leaverRef, reason=0)` to every remaining peer with `Stage ≥ JoinCompleteSent`. Called from `RemoveAndAnnounceLeaveAsync` after the MT_GameRemovePlayer broadcast (non-host-leave path).

---

## Diagnostics

### LobbyDiagnostics (`Lobby/Diagnostics/LobbyDiagnostics.cs`)

Two emitters:
- `RunSnapshotLoopAsync` — 1Hz unconditional dump of every session. Per-peer gs/appMsg rates, ackedBy/relayedTo dicts, OOO state, wait-FF state, etc.
- `MaybeEmitTally` — fires when AppMsgsReceived crosses every 200-message boundary per peer.

Snapshot-time alarms:
- **GS-RATE-DROP** — peer alive at network layer but emitted ZERO GameSyncs in the 1s window despite previously being an active emitter. Sim-side stall signature (UpdateAggregate not advancing).
- **WAIT-FF-STUCK** — `WaitingForFirstFrameAfterReset` set ≥ 30s. Sender either crashed mid-reset or we missed FirstFrame.
- **OOO-GAP-STUCK** — OOO gap open ≥ 5s but force-skip hasn't fired (force-skip runs on next inbound — a quiet peer doesn't trigger it).

### KickEventLog (`Lobby/Diagnostics/KickEventLog.cs`)

Append-only file `logs/kick-events.txt` — every auto-kick (`stuck-receiver`, `silent-peer`, `plain-DISC`, `PT-DISC`, `recovery-exhaustion(*)`, `host-kick`). Always-on regardless of log filters. A missed kick line itself is a signal — peer left through a path NOT covered by `RemoveAndAnnounceLeaveAsync`.

### DebugHooks (`Lobby/Diagnostics/DebugHooks.cs`)

- **PhantomDrop ('y')** — `ArmPhantomDrop` increments a counter. The next relay packet still encrypts + counter-burns + caches but skips the UDP send. Receiver sees a "real transit loss"; their next inbound arrives at expected+2, they NACK, SendCatchUp replays from cache.
- **ReplayLastN ('u')** — Re-send the most-recent N cached outbound frames at their ORIGINAL seqs. Receiver dedupes by seq so frames it already got are no-ops; if a single seq was lost and the recv frontier is stuck, the replay fills the gap.

---

## Top-level LobbyUdpServer

`LobbyUdpServer.cs` is the orchestrator. Roughly 700 lines covering:

- Constructor + Start/DisposeAsync
- `ReceiveLoop` — single-thread recv, whitelist via UdpSessionCache, dispatch per-EP into FIFO channel
- `CreateEpProcessor` — per-EP processor task with unbounded single-reader channel
- `HandleDatagramAsync` — top-level dispatch (plain control vs ProtoTunnel)
- `HandlePlainControlAsync` — CONN/POKE/DISC
- `HandleProtoTunnelAsync` — encrypted DATA pipeline:
  1. Get-or-create session, init streams from EKey
  2. PT decode w/ persistent stream
  3. Per-tuple dispatch (CONN+ACK, DISC, kind=4 PING, DATA)
  4. For DATA: split bundle if subCount>0, record each sub via InboundAckTracker + walk via AppLayerWalker
- `ProcessTailAsync` — post-walk steps:
  1. PING-driven catchup (PingRecovery.StartCatchUp)
  2. Handshake drive
  3. WAIT-FF-FORCECLEAR (5s timer, heartbeat-reachable)
  4. RESET-GATE-RELEASE (drain PendingForResetReleaseToDst)
  5. Recipe respond loop
  6. Relay drain coalescer (RelayPipeline.DrainAsync)
  7. JoinFinalize (if PendingJoinBroadcast set)
  8. VoIP-disable one-shot for own session
  9. NACK upstream + 5s OOO force-skip
  10. Recovery-exhaustion kick consume
  11. Diagnostics tally
- `OnHelloReceived` / `OnStartGameRequested` / `OnChallengeLoadedReport` / `OnMapChangeRequested` / `OnHostKickRequested` / `OnAttributeUpdate` / `OnGameCompleteFromPeer` / `OnGameResultsFromPeer` — callbacks invoked by walker handlers
- `IsPeerReady(uid)` — used by RosterBuilder + reset peerIDs slot allocation
- Per-lobby join-admission gate (`AcquireJoinGateIfJoinerAsync` / `ReleaseJoinGate`)
- `WaitForJoinersAsync(context, maxWaitSeconds)` — used by map-change + challenge-start
- `RemoveAndAnnounceLeaveAsync` — dispatches host-leave-dissolve (S2) vs normal MT_GameRemovePlayer + PLAYER_LEFT (S1 or non-host)
- Background loops (started in `Start`): ReceiveLoop, Diagnostics snapshot (1Hz), SilentPeerWatchdog, ResetWatchdog (50ms), RecipeAssemblerSweep (5s tick / 30s stale), DelayedAck (25ms tick / 80ms idle threshold). No StuckReceiver loop (removed).
  - **DelayedAck** also runs the proactive retransmit FIRST each tick (before the inbound-OOO and idle-ack skips) — a peer that lost a one-shot AND has an inbound gap is exactly the case the ack-only skip strands, so the re-push must run regardless of that state. See Reliability > Proactive retransmit.
  - **SilentPeerWatchdog** — 5s tick / 30s silence threshold (~12 missed CommUDP keepalives; LastPacketRxAt refreshes on ANY inbound datagram, so a sim-stalled-but-connected peer still counts alive). Was 90s/10s — a "never false-kick" overcorrection that left a crashed peer in the lobby 90-100s (the TCP FESL/Theater path can't tear down faster).
- Public Force* methods used by Pool (debug keybinds)

The cross-cutting `Rw*` ResetWatchdog fields, `ChallengeLock` + reporters + flags, `MapChangeInProgress`, `PostChallengeBroadcastInProgress`, `PeerGameResults` all live on `LobbyUdpServer` so the static modules can read/write them directly.

---

## Pool

`LobbyUdpServerPool` — DI singleton. `Allocate(GameServerListing game)` returns a fresh `LobbyUdpServer` bound to `(BasePort + LobbyId)` where LobbyId is round-robin via `Interlocked.Increment` with wrap-around. If the chosen slot is occupied, probes forward until free.

`ReleaseAsync(lobbyId)` — drops + disposes the server.

Force-all fan-outs (`ForceBroadcastGameResetAllAsync`, etc.) iterate every active lobby in parallel — used by debug keybinds in `DebugConsoleHostedService`.

---

## Debug keybinds

(Bound in `DebugConsoleHostedService`, not in the lobby UDP module. The 2026-06-21/22 release-prep purge removed everything else — p/e/b/k/o/u/y and DebugHooks.cs are gone.)

- **'c'** — `ForceBroadcastGameResetAll`: clears `ChallengeAwaitingReady` + `MapChangeInProgress` + `InProgress` then fires `BroadcastLobbySkateAsync` in every lobby. The gate-clear (2026-07-03) makes 'c' a true recovery key — aborting a wedged challenge/map-change no longer leaves the lobby unjoinable.
- **'r'** — `ForceBroadcastStartChallengeAll`: `ChallengeFlow.ForceStartAsync` per lobby — claims the `TryStart` gate like the in-game button, then drives the normal two-phase (S2) / single-phase (S1) challenge handshake.

---

## ResetGate (2026-07-16) — unified reset gating

> Supersedes the per-flow barrier-arm loops in Reset > Barrier above and the watchdog "suppressed if InProgress" note. Code: `Reset/ResetGate.cs`, `GameSyncHandler`, `RelayPipeline`, `EncryptedSender.SendDataAsync(sendGate)`.

**Invariant everything hangs on:** client CommUDP receive is strictly in-order by seq. A relayed GameSync is harmless at a seq BELOW the reset's seq for that dst (client processes it pre-reset, into the old epoch) and poisonous only ABOVE it. So enforcement lives at seq allocation (dst `SendLock` — the same lock the reset's seq comes from), not only at relay-decision time.

**Close** — `ResetGate.Close(server, eligible, reason)`; called by every reset flavor: LobbySkate (join-finalize / map-change / post-challenge / 'c'), watchdog resends, S1 challenge, S2 phase2, S1 `ArmSyncBarrier` pre-arm. One `Interlocked.Increment(ResetEpoch)` is the global mute point; then per-session `ArmWaitFirstFrameBarrierLocked(now, epoch)` (flag + `BarrierEpoch` + `ResetSeqSent=false` + `FirstFrameSrcSeqKnown=false` + queue wipes). Sends follow, `reliable:true`; each send's returned seq → `RecordResetSent` (under RelayLock, epoch-checked so a late record can't attach to a newer barrier; seq 0 = send no-op'd, barrier then only exits via force-clear/watchdog — intended).

**Clear** — epoch signal AND ack causality: S2 `hdr.FirstFrame` / S1 `mFrame<=20`, both `&& ResetSeqSent && DatagramAck >= ResetSeqToDst`. `tW1` is plumbed per-datagram into `WalkContext.DatagramAck` (bundle subs share it). The client cannot ack a reset before receiving it ⇒ a previous-epoch FirstFrame cannot clear the new barrier (watchdog resend = stacked resets = exactly the dangerous case; a FirstFrame is a bool, not a generation number). On clear: `FirstFrameSrcSeq = SrcBareSeq`.

**Straggler fence** — post-clear, a GS with srcSeq < `FirstFrameSrcSeq` ⇒ consumeOnly (`PreResetStragglerStrips`). The fence is the client's own seq space, NOT the ack field: CommUDP retransmits may re-stamp acks with current values, seqs never change. Covers: pre-reset GS lost upstream, retransmitted after the client processed the reset, arriving post-clear with a covering ack.

**Send gate** — `SendDataAsync(sendGate)` evaluated under dst SendLock BEFORE seq alloc; relay passes it only for GS-bearing bodies: `ResetEpoch == drainEpoch && !dst.WaitingForFirstFrameAfterReset`. Reject ⇒ returns 0 ⇒ `SendRelayBodyAsync` strips GSes (LoadingGate ranges) and sends the epoch-agnostic remainder (`GateRejectedRelays`). `DrainAsync` snapshots `drainEpoch` per batch; `RelayAsync` pre-strips whole stale batches (`StaleEpochGameSyncStrips`; unparseable stale body = dropped whole, STALE-EPOCH-DROP log).

**Release gate** — `DrainResetGateReleaseAsync` captures `BarrierEpoch` as its generation at peek; the release send gates on `generation unchanged && !waiting`, and bodies are dequeued only after their batch is sent (see Relay > Release ordering). A voided release leaves nothing to drop — the re-arm's wipe already retired the queue, and the generation guard keeps the drain from touching new-epoch bodies.

**Watchdog** — `Arm(..., allowDuringInProgress)`: false for LobbySkate flavors (fire-time `InProgress` ⇒ disarm `in-progress-superseded`, attempt NOT burned — previously every S1-challenge fire was suppressed AFTER burning an attempt ⇒ 4 no-ops ⇒ GAVE UP: that watchdog was dead code); true for S1-challenge and the NEW S2 phase2 arm (`ResendPhase2Async`). The fire-task InProgress suppression block is deleted. BOTH challenge retries are targeted as of 2026-07-23 — still-armed peers only, same epoch, no re-Close, skip-if-all-cleared (see Challenge > Skate1ChallengeFlow / Skate2ChallengeFlow; S2's Reset(Challenge) is log-proven inert mid-challenge, `arcadia.639203625567577581.log` epoch-4). Only the LobbySkate retry remains a full re-broadcast: a freeskate reset is processed wherever the client is, so re-epoching everyone is the proven recovery. The post-resume frozen check is also pause-aware (see ResetWatchdog).

**Deliberate non-changes:**
- S2 Phase 1 (ChallengeLoad) still never closes the gate — no `SimController::Reset` ⇒ no FirstFrame ⇒ the barrier would be unclearable (IDA: `GameModule::ResetGame` only on LobbySkate/Challenge).
- 15s force-clear retained as last resort; it clears WITHOUT setting the srcSeq fence. Never waits on the happy path — only fires for acked-but-never-FirstFramed (game-side wedge) or loss beyond transport recovery.
- Client-originated MT_GameReset relay-through (`GameResetHandler`) stays ungated, LOG-ONLY warning added. If it ever shows in real sessions: route through `ResetGate.Close` + per-dst `RecordResetSent` captured at relay-send time.
- Transport recovery unchanged and re-verified: every reset send is reliable:true → proactive retx (500ms, unbounded) + kind4 NACK catch-up.

**Seq-space note:** `DatagramAck`/`ResetSeqToDst` compare in the 24-bit bare-seq space (client echoes `serverSeq & 0xFFFFFF`) — same assumption the redundancy walk already makes; a session needs ~46h at 100pps to wrap.

**Counters:** TALLY emits `gateRej` / `staleGsStrips` / `stragglerStrips`. Healthy play: all ~0. Nonzero gateRej/staleGsStrips = the gate caught a real race (working as intended). Climbing stragglerStrips = lossy client retransmitting across resets (expected on bad links, harmless).
