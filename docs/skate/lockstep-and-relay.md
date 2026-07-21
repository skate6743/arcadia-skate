# Lockstep and the relay

Skate's online netcode is **lockstep**: every peer in a lobby simulates the entire game world deterministically, advancing one frame at a time only after it has received every other peer's input + checksum for that frame. There is no server-authoritative simulation — arcadia is purely a relay between peers.

This doc explains how that lockstep model interacts with arcadia's relay loop, the post-reset FirstFrame barrier, and the loading-gate filter that prevents lockstep from wedging during transient state mismatches.

Source:
- `src/server/Hosting/Lobby/Walker/Handlers/GameSyncHandler.cs`
- `src/server/Hosting/Lobby/Relay/RelayPipeline.cs`
- `src/server/Hosting/Lobby/Relay/LoadingGate.cs`
- `src/server/Hosting/LobbySession.cs` (`WaitingForFirstFrameAfterReset`, `ArmWaitFirstFrameBarrierLocked`, `PendingForResetReleaseToDst`, `OrderedPendingRelayBodies`)

For wire formats of MT_GameSync see [app-layer.md](app-layer.md).

## The lockstep model in one paragraph

Each peer's client runs a `SyncChecker` + `CommandQueue` pair. Every frame, the client:

1. Reads local input for frame N.
2. Computes a `mCheckSum` over local sim state at frame N.
3. Emits an `MT_GameSync` containing `{mFrame=N, mCheckSum, cmdCount, inputs[]}`.
4. **Waits** until it has received frame N's GameSync from every other peer.
5. Advances simulation one tick, applying everyone's frame-N inputs in deterministic order.
6. Compares each peer's checksum against its own; mismatch ⇒ desync ⇒ disconnect (eventually).

Lockstep means the slowest peer dictates the simulation rate. The relay's job is to keep every peer's queue of inbound GameSyncs full enough that nobody starves waiting.

## What arcadia actually relays

`RelayPipeline.RelayAsync` — the fan-out path for any Sk8 message a peer emits:

1. Snapshot eligible destination peers (everyone who's past `JoinCompleteSent` and isn't the sender themselves).
2. Walk the body to count GameSyncs (and produce per-message ranges if any peer is in the "loading" state).
3. For each destination peer, apply per-destination filters in order:
   - **Loading gate**: if the destination peer is still loading (no GameSync emitted yet AND not waiting for a post-reset FirstFrame), strip GameSyncs from the relayed body. Lockstep frames sent to a peer that hasn't started its simulation will desync it immediately.
   - **Reset gate**: if the destination peer is in `WaitingForFirstFrameAfterReset`, hold the body in `PendingForResetReleaseToDst` instead of forwarding. Released atomically once the destination's own FirstFrame clears.
4. Send the resulting per-destination body via `EncryptedSender.SendDataAsync`.

The full inbound path for a GameSync is:

```
ProtoTunnel decrypt
  → CommUDP DATA
  → NetGameLink unwrap (sysFlag=0x00)
  → AppLayerWalker.WalkAppMessages
    → GameSyncHandler.HandleSkate1 / HandleSkate2  (see below)
    → ParkRelayBody (appends body to session.OrderedPendingRelayBodies)
  → tail of HandleProtoTunnelAsync
    → RelayPipeline.DrainAsync (coalesces parked bodies and fans out)
```

## The FirstFrame post-reset barrier

The hardest problem in lockstep over a dedicated relay is **what happens at reset boundaries** — when arcadia broadcasts an `MT_GameReset` to all peers to transition them into a new lobby phase (FreeSkate, ChallengeLoad, Challenge).

The race: arcadia sends `MT_GameReset` to peer A and peer B simultaneously. Peer A processes it quickly and emits its first post-reset GameSync (`mFrame == 1`). Peer B is still draining queued frames from the *pre-reset* epoch. If arcadia naively forwards A's mFrame=1 to B, B's CommandQueue sees a frame from the new epoch while still expecting frames from the old, and the lockstep gates wedge permanently.

### Detection — `ResetGate.Close`

Every reset flavor closes the gate through one entry point (`ResetGate.Close` in `Reset/ResetGate.cs`) before any reset hits the wire:

```csharp
int epoch = ResetGate.Close(server, eligible, "LobbySkate");
// per peer, after the reliable send:
ResetGate.RecordResetSent(peerSession, epoch, seq);
```

`Close` atomically bumps the lobby-wide `LobbyUdpServer.ResetEpoch`, then arms every eligible session under its `RelayLock` (`LobbySession.ArmWaitFirstFrameBarrierLocked`: flag + epoch stamp + pending-queue wipe). `RecordResetSent` stores the CommUDP seq the reset occupied for that peer — the clear condition below is keyed on it.

The load-bearing ordering fact: the client's CommUDP receive is strictly in-order, so a relayed GameSync is only dangerous if it lands at a **higher** seq than the reset. Anything that beat the reset to a seq is processed by the client *before* the reset, into the old epoch it belongs to — harmless. The gate therefore enforces at seq-allocation time (see the send gate below), not just at relay-decision time.

### Sender-side filter — `GameSyncHandler.HandleSkate1` / `HandleSkate2`

When peer A's first post-reset GameSync arrives, the handler clears the filter on the epoch signal — `mFrame <= PostResetFrameWindow` (= 20) on Skate 1, or the `FirstFrame` flag bit on Skate 2 — **AND** an ack condition: the datagram carrying that GameSync must have acked the seq the reset was sent in (`DatagramAck >= ResetSeqToDst`).

```csharp
// GameSyncHandler.HandleSkate2 (Skate 1 identical with mFrame <= 20 in place of the flag)
bool ackCoversReset = ctx.Session.ResetSeqSent && ctx.DatagramAck >= ctx.Session.ResetSeqToDst;
if (hdr.FirstFrame && ackCoversReset)
{
    // release the filter; remember the clearing frame's source seq
    ctx.Session.WaitingForFirstFrameAfterReset = false;
    ctx.Session.FirstFrameSrcSeq = ctx.SrcBareSeq;
    ctx.Session.FirstFrameSrcSeqKnown = true;
}
else { /* force-clear timeout, else consumeOnly = true */ }
```

The ack condition is what makes the clear epoch-safe: a FirstFrame is only a boolean, so with two resets close together (a watchdog re-send *is* a second reset) a FirstFrame emitted for epoch A could arrive after the gate re-closed for epoch B. The client cannot have acked reset B before receiving it, so `ackCoversReset` rejects the stale clear. On Skate 1 the same condition upgrades the `mFrame <= 20` window from a guess to "only consulted once the client provably holds the reset" — a young previous epoch can no longer false-clear.

After the clear, one residual filter remains: a GameSync whose source seq is **below** `FirstFrameSrcSeq` is a client-retransmitted pre-reset frame and is consumed, not relayed. The fence is the client's own seq space rather than the ack field because a retransmit may re-stamp its ack with a current value, but its seq never changes.

The window semantics on Skate 1 are unchanged: the clear triggers on any `mFrame <= 20`, not strictly `mFrame == 1`, so a lost or wire-reordered frame 1 doesn't strand the barrier. A genuine pre-reset frame sits far higher (hundreds/thousands) after any real play.

### Destination-side hold — `PendingForResetReleaseToDst`

There is also a destination-side hold — applied for **both variants** (`RelayAsync` does this with no variant check). While destination peer B is still pre-reset, any inbound message destined for B is parked rather than forwarded:

```csharp
if (otherSession.WaitingForFirstFrameAfterReset)
{
    lock (otherSession.RelayLock)
    {
        otherSession.PendingForResetReleaseToDst.Enqueue(dstBody);
    }
    continue;
}
```

The hold is drained by `RelayPipeline.DrainResetGateReleaseAsync` when B's own FirstFrame clears its barrier — at which point the held bodies are coalesced into ≤800B batches and shipped. The release send carries a gate evaluated at seq allocation: if the lobby re-armed between the release check and the send, the held bodies belong to the epoch the re-arm just wiped, so the release voids instead of delivering them after the new reset.

### The send gate — enforcement at seq allocation

Relay decisions race reset broadcasts: a send task that already passed the "is the destination waiting?" check can reach the socket *after* a reset grabbed its seq, which is exactly the poisonous ordering. `EncryptedSender.SendDataAsync` therefore accepts a `sendGate` callback evaluated **inside the destination's `SendLock`, before the seq is allocated** — the same lock the reset serialized through. GameSync-bearing relay bodies pass a gate of "the reset epoch is still the one this batch was drained under, and the destination is not barrier-armed" (`RelayPipeline.SendRelayBodyAsync`). A rejected body is re-sent with its GameSyncs stripped (`LoadingGate.TryBuildGameSyncStrippedBody`) so epoch-agnostic messages still deliver; the pre-reset GameSyncs are expendable by design — the reset wipes the lockstep state they belonged to.

`RelayPipeline.DrainAsync` snapshots `ResetEpoch` per batch, and `RelayPipeline.RelayAsync` strips GameSyncs from any batch whose epoch went stale between drain and fan-out.

### Safety valve

If `WaitingForFirstFrameAfterReset` doesn't clear within `WaitFirstFrameForceClearSeconds` (= 15s), arcadia force-clears the flag and accepts whatever's arriving. This prevents a permanent stall if a peer's reset processing got lost on the wire. 15s is long enough to outlast the ~10s Skate 1 challenge-start cutscene, during which the peer is loading and emits no FirstFrame; a shorter timeout would fire mid-cutscene and drop the barrier prematurely.

## The Loading gate (peer never reached the lockstep loop yet)

Different problem: a peer's client has joined the lobby but is still loading map assets, hasn't started its simulation, has emitted zero GameSyncs. Forwarding lockstep frames to it during this window would put frames into its CommandQueue before it's set up to consume them, causing immediate desync.

Detection (`RelayPipeline.RelayAsync`):

```csharp
bool loading = otherSession.GameSyncsReceived == 0
            && !otherSession.WaitingForFirstFrameAfterReset;
```

- `GameSyncsReceived == 0` — peer has not yet emitted any GameSync (so hasn't entered the lockstep loop).
- `&& !WaitingForFirstFrameAfterReset` — distinguishes "loading for the first time" from "post-reset waiting for FirstFrame." A post-reset peer might also have `GameSyncsReceived == 0` for the new epoch, but that's a different filter.

`LoadingGate.WalkRanges` walks the inbound body, identifies which messages are GameSyncs (variant-specific opcodes), and returns ranges. Then `LoadingGate.TryBuildGameSyncStrippedBody` constructs a new body with GameSyncs excluded. The destination peer gets all the non-GameSync messages (resets, requests, attribute changes) but no lockstep frames until it's emitted its own first GameSync.

## Drain coalescer

The drain stage (`RelayPipeline.DrainAsync`) is the bridge between "messages get parked into `OrderedPendingRelayBodies` as they arrive" and "outbound packets get sent to peers." It coalesces multiple parked bodies into single outbound CommUDP packets (capped at `MaxRelayBatchBytes = 800` bytes) to minimize ProtoTunnel + CommUDP overhead and stay clear of the client's fixed-size game-network receive queue (see *Why bundling at this layer matters* below).

Two important constraints:

1. **Sequence ordering** — only forwards bodies whose `srcSeq <= ClientAckSeq`. Bodies for seqs ahead of the client's ack hold at the gate — the source peer hasn't actually acknowledged them yet, so forwarding would be premature.
2. **No reorder** — if a parked body's seq is `<= LastRelayedSrcSeq`, drop it. The drain forwards in monotone order.

The `MaxRelayBatchBytes = 800` constant is conservative — well under the 1272-byte CommUDP receive-element size, leaves headroom for ProtoTunnel envelope + CommUDP header + NetGameLink trailer.

## Why bundling at this layer matters

Skate's clients have a **fixed-size game-network receive queue** of **32 entries** in each direction, on both games. The size is set two ways, either of which alone yields 32: the game-network params are initialized with a queue length of 32, and the manager's initialize path applies a `≤ 0 → 32` fallback as a belt-and-suspenders default.

The per-entry accounting: each ring slot is 1272 bytes, **one CommUDP sub-packet consumes one slot** (a bundled datagram of K subs takes K slots), a full ring is a **silent drop** (no NACK from the ring writer; recovery comes later via the receiver's gap-detection NACK), and the drain is dual — a reactive per-write callback on the socket thread plus a per-Idle pump on the game thread, with a 4 KB NetGameLink buffer between ring and sim. Fewer, fuller CommUDP packets therefore consume fewer slots per message delivered, which is exactly why arcadia coalesces at two layers:

- Drain coalesces parked bodies into one CommUDP DATA payload via concatenation (multiple Sk8 messages in one NetGameLink body).
- `EncryptedSender.SendBundledDataAsync` bundles multiple CommUDP sub-packets into one ProtoTunnel frame (multiple netGameLink bodies in one ProtoTunnel packet).

These work together: a single outbound UDP datagram carries one ProtoTunnel envelope wrapping one CommUDP bundle of N sub-packets, each sub-packet a NetGameLink body that can itself hold M back-to-back Sk8 messages — so one datagram can deliver N×M game messages.

## Counters in `LobbySession`

| Counter | Purpose | Set in |
|---|---|---|
| `GameSyncsReceived` | how many GameSyncs the peer has emitted (loading gate input) | `GameSyncHandler.HandleSkate1` / `HandleSkate2` |
| `WaitingForFirstFrameAfterReset` | post-reset filter armed | armed in `LobbySession.ArmWaitFirstFrameBarrierLocked` (via `ResetGate.Close`), cleared in `GameSyncHandler.HandleSkate1` / `HandleSkate2` |
| `WaitingForFirstFrameAfterResetSetAt` | for safety-valve timeout | same as above |
| `BarrierEpoch` / `ResetSeqToDst` / `ResetSeqSent` | which reset this barrier belongs to + the seq it rode in | `ResetGate.Close` / `ResetGate.RecordResetSent` |
| `FirstFrameSrcSeq` (+`Known`) | source-seq fence: frames below it are pre-reset retransmits | `GameSyncHandler` on clear |
| `GateRejectedRelays` / `StaleEpochGameSyncStrips` / `PreResetStragglerStrips` | send-gate + epoch-strip + straggler-fence diagnostics | `RelayPipeline` / `GameSyncHandler` |
| `OrderedPendingRelayBodies` | parked bodies awaiting drain | `AppLayerWalker.ParkRelayBody` |
| `PendingForResetReleaseToDst` | held bodies for a peer in pre-reset state | `RelayPipeline.RelayAsync` |

## See also

- [reset-and-challenge-flow.md](reset-and-challenge-flow.md) — when `MT_GameReset` is broadcast and what the orchestration looks like.
- [reliability-and-retransmit.md](reliability-and-retransmit.md) — the CommUDP-level retransmit machinery feeding this layer.
- [app-layer.md](app-layer.md) — MT_GameSync wire format.
- [../protocol/commudp.md](../protocol/commudp.md) — CommUDP bundling and the sub-packet wire format.
