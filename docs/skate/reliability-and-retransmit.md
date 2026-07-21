# Reliability and retransmit

CommUDP guarantees in-order, gap-free delivery of every DATA frame. The mechanism is client-driven: when the receiver detects a gap, it asks the sender to replay. This doc covers how arcadia plays both sides of that contract — as a sender (replaying lost frames to clients on demand, proactive piggy-back redundancy on every send, and a timer-based re-push of unacked one-shot control frames) and as a receiver (NACK'ing upstream when arcadia missed something, plus a secondary RC4 state for recovering wire-reordered packets that would otherwise drop at decrypt time).

For CommUDP wire format see [../protocol/commudp.md](../protocol/commudp.md). For ProtoTunnel see [../protocol/prototunnel.md](../protocol/prototunnel.md).

Source:
- `src/server/Hosting/Lobby/Reliability/PingRecovery.cs` — outbound catch-up on client kind=4.
- `src/server/Hosting/Lobby/Reliability/InboundAckTracker.cs` — receiver-side ack tracking + OOO gap detection.
- `src/server/Hosting/Lobby/Send/EncryptedSender.cs` — the send primitive, SentFrameCache, and outbound redundancy.
- `src/server/Hosting/Lobby/Wire/Arc4Stream.cs` — primary + OOP RC4 state for wire-reorder recovery.
- `src/server/Hosting/LobbySession.cs` — per-session counters (`ServerDataSeq`, `ClientAckSeq`, `SentFrameCache`, `ClientRequestedSeq`, `RedundancyMLimit`).

## The ~100ms hard law

The Skate client runs a CommUDP retransmit timer. On a connected link, measuring the time since the last packet it received, it re-solicits when:

> more than **~100 ms** have passed **and** it still has unacknowledged outbound data, **or** more than **~2500 ms** have passed, unconditionally.

When that fires it re-sends through the CommUDP path — a retransmit/re-solicit. These thresholds are the standard DirtySDK CommUDP keepalive values (`BUSY_KEEPALIVE = 100`, `IDLE_KEEPALIVE = 2500`), the same across every Plasma-era title.

**Arcadia's hard constraint**: while a client has data in flight it expects a response inside ~100ms, so every active session must receive an ack-bearing packet at least every ~100ms (minus expected RTT). Skip it and the client re-solicits; if recovery never comes, the client's ~10s game-network timeout eventually drops the session.

How arcadia satisfies this:

1. **Steady-state DATA traffic carries piggy-back acks** — every outbound `MT_GameSync` relay includes `w1 = session.ClientAckSeq` in its CommUDP header. As long as there's any traffic, the timer stays armed.
2. **`SendAckOnlyAsync` during quiet periods** — emits NetGameLink ack-only frames (no body, just sync trailer + `kindByte = 0x40`) to keep the timer fed when nothing else is flowing.

## SentFrameCache — the server-side replay cache

Every outbound DATA frame's body is cached by its allocated `serverSeq` in `session.SentFrameCache` — a `ConcurrentDictionary<uint, byte[]>` that keeps a sliding window of the most recent `SentFrameCacheCapacity` seqs. Each fresh send writes the body and `TryRemove`s the entry `SentFrameCacheCapacity` behind.

For replay: `SendDataAsync(explicitSeq: oldSeq)` (`EncryptedSender.cs`) — pass the seq the client asked for, skip the seq-allocation step, look up the cached body, re-send.

If the client asks for a seq that's been evicted, `PingRecovery` logs a warning — no clean recovery from this, the client will eventually time out. The cache size needs to be large enough that no realistic catch-up scenario exhausts it.

## Outbound redundancy — proactive piggy-back

Every fresh outbound DATA packet may carry a small number of recent unacked frames as redundancy, packed into the same CommUDP bundle. The receiver's CommUDP layer detects bundle sub-packets, ignores duplicates of seqs it already saw, and accepts any whose seq fills a gap — recovering a lost original **without needing a kind=4 NACK roundtrip**.

This mirrors DirtySDK's `commudp` redundancy mechanism (controlled per-session by the `'rlmt'` control selector — total bundle body byte limit). Arcadia uses 96 bytes (vs DirtySDK's documented default of 64) because Skate's typical relay body is ~46 bytes wrapped, so a bundle of `(main GameSync) + (one prior GameSync redundancy)` lands at ~93 bytes — needs slightly more than the default to fit.

Implementation in `EncryptedSender.SendDataAsync`:

- **Trigger condition**: fresh send (not a catch-up replay), main body ≤ 250 bytes, client has not yet ack'd seq `serverSeq - 1`. If client is keeping up tick-for-tick, no piggy-back fires — there's nothing unacked to add.
- **Walk**: from `serverSeq - 1` backward through SentFrameCache, accumulating bodies until either (a) total bundle size exceeds `RedundancyLimitBytes = 96`, (b) the adaptive sub-packet cap is hit, or (c) we cross the client's ack frontier.
- **Send**: if any redundancy entries collected, build a bundled CommUDP DATA via `BuildDataBundle`; otherwise the regular `BuildData` single-sub path.

### Adaptive sub-packet cap — `session.RedundancyMLimit`

Per-session integer that starts at 2 (= up to 1 redundancy entry per outbound packet) and self-tunes:

- If the walk reached the ack frontier (consumed every available candidate), reset to 2 — no growth needed because there's nothing more to add even at a higher cap.
- If the walk stopped early (budget or cap hit but more candidates existed), double it (cap at 15) — loss pressure suggests packing more redundancy next packet.

So under stable conditions, redundancy stays modest (1 sub-packet); under sustained loss, it expands automatically up to the wire limit. Mirrors DirtySDK CommUDP's documented `uMLimit` adaptive behavior — same progression pattern (2 → 4 → 8 → 15).

### Telemetry

`session.RedundancySubsSent` counts every sub-packet ever piggy-backed onto an outbound. Exposed via the `redSubs=` field in `TALLY` log lines (see `LobbyDiagnostics.cs`).

If this counter stays flat during steady-state gameplay (only grows during handshake / reset bursts), the peer is acking fast enough that no redundancy is needed — the mechanism is correctly dormant. If it grows continuously, the peer is consistently behind on acks (high RTT or loss).

## Proactive retransmit — server-push for one-shot control frames

Both mechanisms above depend on *more traffic flowing*: redundancy needs a fresh send to piggy-back onto, and the client's kind=4 NACK only fires once it receives a higher seq that exposes the gap. Neither covers a lost **one-shot control frame** — `MT_GameReset`, map-change, challenge-start, `MT_GameAttributes`, the GM player-events, host-leave/kick — sent into a quiet moment with nothing behind it: the client never sees a higher seq, never NACKs, and the action is lost forever (e.g. one peer starts a challenge while the other stays in the lobby; a `PLAYER_LEFT` that never lands leaves a ghost peer).

`LobbyUdpServer.MaybeProactiveRetransmitAsync`, run on every tick of the 25 ms delayed-ack loop, is the server-push backstop for exactly that case:

- **Tagging** — every must-deliver control frame is marked at send time (`reliable: true` on `SendDataAsync`, set by `ResetBroadcaster`, the challenge flows, `PlayerEventBroadcaster`, and host-leave/kick) and its seq recorded in `session.ReliableUnackedSeqs`. Relayed GameSyncs are **never** tagged — they stay on the kind=4 + redundancy path, since re-pushing them here would only duplicate that recovery and burn the client's 32-slot recv ring.
- **Trigger** — if the client's cumulative ack (`LastAckFromClient`) has been frozen for ≥ `ProactiveRetransmitStuckMs` (= 500 ms) while a tracked control seq is still unacked, the oldest such seqs are re-pushed from `SentFrameCache` (via `explicitSeq`, so no new seq is burned). Paced by `ProactiveRetransmitMinIntervalMs` (= 500 ms), capped at `ProactiveRetransmitWindowCap` (= 16) per pass.
- **Prune** — tracked seqs are dropped as `LastAckFromClient` advances past them (delivered).

500 ms lets the client's own ~100 ms CommUDP timer and the redundancy piggy-back try first; the server only steps in when a critical one-shot is genuinely stuck. Logged as `PROACTIVE-RETX(control)`. This is the arcadia-side stand-in for CommUDP's own sender-side retransmit timer — in EA's peer-to-peer model the *sender's* CommUDP retransmitted any unacked reliable frame end-to-end, so no server did this; arcadia *terminates* CommUDP per client and so must provide the server→client half of that reliability itself.

## Inbound kind=4 from client → `StartCatchUp`

When the client sends a `CommUdpKind.PingRetransmitRequest` (`w0 = 4`, `w1 = expectedSeq`), arcadia's UDP server records `session.ClientRequestedSeq = expectedSeq` and schedules `PingRecovery.StartCatchUp` for the session.

`PingRecovery.StartCatchUp`:

1. **Single-flight gate** — CAS on `session.CatchupInFlight`; back-to-back NACKs for the same range coalesce into one catch-up task.
2. **Compute window** — `toSeq = min(ServerDataSeq, fromSeq + CatchupWindowSize)`. `CatchupWindowSize = 16` — send at most 16 cached seqs per call.
3. **Bundle and send** — walks `[fromSeq, toSeq)`, packs consecutive cached bodies into bundles of up to `MaxBundleSubs = 16` sub-packets and `OversizeBoundaryBytes = 250` bytes per sub-packet.
4. **Cache miss handling** — if a seq has been evicted from `SentFrameCache`, an ack-only frame is synthesized in its place so the client's frontier can still advance past the eviction.

No throttle, no mode-switch, no per-seq retry cap on this path — every NACK kicks off catch-up immediately and bundles up to the cache-window's worth of cached bodies.

## Server-side NACK upstream — `SendPingRetransmitAsync`

The reverse direction: when arcadia detects an inbound seq gap from the client, it asks the client for the missing frames via its own kind=4. See `EncryptedSender.SendPingRetransmitAsync`:

```csharp
var built = ProtoTunnelCodec.BuildPingRetransmit(
    session.SendStream,
    session.PreambleBytes, session.ServerPreambleByte5,
    session.TunnelIdx, expectedSeq, server.Variant);
```

This matters when arcadia has an open out-of-order gap (`InboundAckTracker` is holding `ClientAckSeq` because seq N+1 hasn't arrived but N+2 has): the client only retransmits when *its own* receiver detects a gap — it has no way to know arcadia's receiver is missing N+1 — so arcadia proactively NACKs upstream to solicit the missing frame.

Rate-limited via `NackUpstreamIntervalMs = 50` to avoid spamming.

## OOP state — wire-reordered packet recovery

ProtoTunnel's persistent RC4 stream advances forward by wire counter. When a packet arrives with a counter behind the current state, primary RC4 can't decrypt it — the cipher is already past that position. By default such packets drop at decrypt time and the missing seq cascades into a CommUDP-level OOO gap, eventually triggering a NACK roundtrip.

To avoid the roundtrip for common wire-reorder cases, `Arc4Stream` maintains a **secondary RC4 instance** ("OOP state") that lags behind the primary. When primary has to skip forward by more than one packet (= the sender's stream jumped, meaning we missed something or saw reorder), the pre-skip primary state is captured into OOP first. A later backwards-counter packet can then retry through OOP — if its counter is within the captured range, OOP decrypts it.

This mirrors DirtySDK ProtoTunnel's documented `CryptRecvOOPState` mechanism (added in v15-era; functionally equivalent across implementations).

### When OOP is updated

`Arc4Stream.TryAdvanceToCounter` calls `SaveOopFromCurrentPrimary` BEFORE advancing primary, when:
- `delta > 1` (forward skip across at least one missed packet), OR
- forward wrap across the 16-bit counter boundary

Sequential advances (`delta == 1`) do NOT save — OOP keeps the most recent skip's snapshot. Each new save overwrites the previous, so OOP only covers the most recent skip range.

### When OOP is used

`ProtoTunnelCodec.TryDecode`: when primary returns `StaleInEpoch`, the code calls `recvStream.TryAdvanceOopToCounter(counter)`. OOP can advance forward to the target counter only if:

- OOP is initialized (= primary has done at least one skip),
- OOP and primary are in the same epoch (`HighWord` match),
- delta from OOP's position is positive (RC4 can't rewind), and
- the target counter does not catch up to or pass primary.

If those hold, `TryDecodeViaOop` runs the same tuple-header + payload decrypt as the primary path but via `ApplyOop` / `RealignOop` on the secondary state. On success the frame is returned and `OopRecoveredPackets` is incremented. If OOP also can't decode (counter outside its coverage, or genuinely malformed), the packet drops as before.

### Telemetry

| Counter | Meaning |
|---|---|
| `session.StaleInEpochPackets` | wire packets primary dropped due to backwards counter |
| `session.OopRecoveredPackets` | of those, how many OOP successfully recovered |
| `session.StaleAcrossWrapPackets` | packets requiring the across-wrap fresh decode path (separate from OOP) |

Exposed via `staleEpoch={N} oopRec={N} staleWrap={N}` fields in `TALLY` log lines.

Practical ratio interpretation:
- `oopRec / staleEpoch` close to 1.0 → most wire-reorder is being recovered; ack-only frames or near-current backwards counters dominate the residual.
- Lower ratio (say 30-50%) → some backwards-counter packets fall outside OOP coverage — either they're older than the most recent skip, or the wire reorder is more severe than single-skip recovery handles.

OOP recovery is a pure receive-side optimization; it doesn't change anything the clients send or expect on the wire.

## Counter summary

| Counter | Purpose |
|---|---|
| `ServerDataSeq` | next outbound seq to allocate |
| `ClientAckSeq` | last in-order seq received from client (piggy-back ack value) |
| `ClientRequestedSeq` | last seq the client kind=4-NACK'd us for (catch-up starting point) |
| `LastAckFromClient` | highest outbound seq the client has cumulatively ack'd — delivery confirmation for the recipe-mesh gate + proactive retransmit |
| `ReliableUnackedSeqs` | seqs of must-deliver control frames the proactive retransmit guards |
| `CatchupInFlight` | CAS gate so coalescing kind=4 storms don't spawn N catch-up tasks |
| `RedundancyMLimit` | adaptive sub-packet cap (`uMLimit` equivalent) — starts at 2, doubles under loss pressure |
| `RedundancySubsSent` | telemetry — total redundancy sub-packets piggy-backed |
| `StaleInEpochPackets` | telemetry — packets dropped at primary decrypt (before OOP retry) |
| `OopRecoveredPackets` | telemetry — packets recovered by OOP secondary state |
| `NacksSent` | telemetry — server-side kind=4 NACKs upstream |

## See also

- [../protocol/commudp.md](../protocol/commudp.md) — wire-level retransmit and bundling semantics.
- [../protocol/prototunnel.md](../protocol/prototunnel.md) — counter handling, RC4 stream, OOP state structure.
- [lockstep-and-relay.md](lockstep-and-relay.md) — how reliability events interact with the relay drain.
