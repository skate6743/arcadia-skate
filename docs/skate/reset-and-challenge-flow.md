# Reset and challenge flow

`MT_GameReset` is Skate's "transition to a new phase" message. Every phase change in the lobby — entering lobby, loading a challenge, starting a challenge, recycling after a map change — comes down to arcadia broadcasting an `MT_GameReset` (with a `Sk8ResetType` value) to every peer and orchestrating the surrounding events.

This doc walks through how arcadia coordinates resets, the two-phase challenge dance, and why some flows are Skate-1-specific vs Skate-2-specific.

Source:
- `src/server/Hosting/Lobby/Reset/ResetBroadcaster.cs` — the broadcast primitive.
- `src/server/Hosting/Lobby/Reset/ResetWatchdog.cs` — re-send timer if peers don't respond.
- `src/server/Hosting/Lobby/Challenge/ChallengeFlow.cs` — the entry point when a host hits "start challenge."
- `src/server/Hosting/Lobby/Challenge/Skate1ChallengeFlow.cs` / `Skate2ChallengeFlow.cs` — per-variant challenge orchestration.
- `src/server/Hosting/Lobby/Flow/*.cs` — JoinFinalize / MapChange / PostChallenge flows.

For wire format of `MT_GameReset`, see [app-layer.md](app-layer.md). For the FirstFrame barrier this drives, see [lockstep-and-relay.md](lockstep-and-relay.md).

## `Sk8ResetType` summary

```csharp
public enum Sk8ResetType
{
    LobbySkate    = 0,    // → enter free-skate lobby
    ChallengeLoad = 1,    // → load challenge assets (challenge phase 1)
    Challenge     = 2,    // → start challenge simulation (challenge phase 2)
}
```

The receiver's `OnReset` handler branches on the `resetType` and routes into the appropriate engine state:

- **LobbySkate** — enter / re-enter the free-skate world. Map is keyed by `mActivity` (the challenge key field; for free-skate this points at the default `OnlineFreeSkate` value).
- **ChallengeLoad** — start asset-loading for the challenge identified by `mActivity`. This is challenge **phase 1**.
- **Challenge** — actually start the challenge simulation. This is challenge **phase 2**; it resets the simulator (restarting the sim at frame 1), which produces the FirstFrame signal. **`ChallengeLoad` (phase 1) does NOT reset the sim** — the client routes it into asset-loading with no sim reset, which is exactly why arcadia never arms the FirstFrame barrier on phase 1. A `LobbySkate` reset *also* resets the simulator, so a FirstFrame follows every LobbySkate reset too — it is **not** unique to Challenge; arcadia arms the barrier on both LobbySkate and Challenge-phase-2.

> **`mActivity` is a Skate 2 reset-body field only.** `GameResetPacket.Build` writes an 8-byte activity value into the Skate 2 `MT_GameReset` body; the Skate 1 body carries only the slot table + `resetType` (53 bytes total, no activity). Skate 1's challenge/map selection rides on `MT_GameAttributes` instead. See [s1-vs-s2-differences.md](s1-vs-s2-differences.md#mt_gamereset-body).

## The broadcast primitive — `ResetBroadcaster.BroadcastLobbySkateAsync`

The canonical pattern for any LobbySkate reset:

1. **Snapshot eligible peers** (handshake done, past `JoinCompleteSent`).
2. **Optionally pre-broadcast `MT_GameAttributes`** if attributes have changed.
3. **Close the reset gate** (`ResetGate.Close`): one atomic bump of the lobby-wide `ResetEpoch`, then the WaitFirstFrame barrier arms on every eligible peer under its `RelayLock`.
4. **Resolve the activity key** (challenge key, or fallback).
5. **Build a per-peer `MT_GameReset` body** with that peer's slot table + reset type + activity, send to all peers in parallel (`reliable: true`), and **record each send's seq** (`ResetGate.RecordResetSent`) — the barrier's clear condition is keyed on it. The slot table is compacted at build time (`GameServerListing.CompactSlots`): slots are sticky while an epoch runs — a joiner arriving after a leave fills the freed slot — but the broadcast table must always be a dense prefix. Clients rebuild their peer array from this table, and an interior empty slot registers as a phantom player whose GameSyncs never arrive, stalling lockstep for everyone (trailing empty slots are harmless — every table has them below max capacity).
6. **Latch the joinable gate** by marking `Game.MarkCreatorResetSent()` when the creator has been sent their reset.
7. **Arm `ResetWatchdog`** for a re-send if the reset gets lost.

The gate close in step 3 happens **before** the wire send in step 5, and the epoch bump is a single atomic write — every relay decision after that instant sees the closed gate. GameSync-bearing relay sends additionally re-validate the gate at seq-allocation time inside the destination's `SendLock` (the same lock the reset's seq comes from), so a relay task in flight across the close cannot land a pre-reset GameSync at a seq above the reset. See [lockstep-and-relay.md](lockstep-and-relay.md) for the full gate mechanics.

## The two-phase challenge dance

Starting a challenge is a coordinated 4-step sequence involving every peer. Arcadia drives it.

### Trigger — `MT_GameRequest(StartGame)` from the host

The host client sends `MT_GameRequest` with `mRequest = StartGame (1)` to indicate "let's start a challenge." Arcadia's walker catches this and routes to `ChallengeFlow.MaybeStart`:

```csharp
public static void MaybeStart(LobbyUdpServer server, LobbySession source)
{
    // Single CAS against the lobby's startup gate
    if (!server.Game.TryStart()) return;

    bool fire;
    lock (server.ChallengeLock)
    {
        if (server.ChallengeAwaitingReady) fire = false;
        else
        {
            server.ChallengeReporters.Clear();
            server.ChallengeReadyFired = false;
            server.ChallengeAwaitingReady = true;
            fire = true;
        }
    }
    if (!fire) { server.Game.InProgress = false; return; }

    // Skate 1 only: arm the sync barrier synchronously to close a StartGame→async-arm race
    if (server.Variant == GameVariant.Skate1)
        ArmSyncBarrier(server, "Skate1-challenge-start(sync)");

    // Guarded fire-and-forget. A throw anywhere in the challenge-start path clears
    // InProgress + ChallengeAwaitingReady so a failed start can never strand the lobby
    // (hidden from matchmaking + rejecting every join). A normal return leaves
    // InProgress true — the challenge is live; the happy-path clear is PostChallengeFlow.
    _ = Task.Run(async () =>
    {
        try { await Phase1Async(server, server.CancellationToken); }
        catch (Exception) { ClearAwaitingReady(server); server.Game.InProgress = false; }
    });
}
```

The Skate 1 synchronous barrier arm (`ChallengeFlow.ArmSyncBarrier`) is critical: a `StartGame` + a trailing pre-reset `GameSync` could share one NetGameLink body. If arcadia armed the barrier on a `Task.Run` (async), the trailing GameSync would walk through the relay before the filter was set, poisoning the post-reset CommandQueue. The synchronous arm in `MaybeStart` runs under the same walker tick that observed `StartGame`, so any same-body trailing GameSyncs see the armed filter.

### Phase 1 — `MT_GameReset(ChallengeLoad)` + `MT_GameRequest(StartGame)`

`Skate2ChallengeFlow.Phase1Async`:

```csharp
public static async Task Phase1Async(LobbyUdpServer server, CancellationToken ct)
{
    long activity = HandshakeFlow.ResolveLobbyChallengeKey(server);
    byte[] startGameBody = GameRequestPacket.StartGame(server.Variant);

    var eligible = ResetBroadcaster.SnapshotEligible(server);

    // Per peer, in parallel:
    //   send MT_GameReset(ChallengeLoad, activity)
    //   send MT_GameRequest(StartGame)
}
```

Notice: **Phase 1 does NOT arm the WaitFirstFrame barrier on Skate 2.** In Skate 2, `Reset(ChallengeLoad)` is purely a *load assets* trigger; it doesn't reset the simulator. Only Phase 2 arms the barrier, because only Phase 2 resets the sim — the FirstFrame signal won't come until then.

After Phase 1, every peer is loading challenge assets. They emit no GameSyncs during this period (they're not in the lockstep loop). When each finishes loading, it emits an `MT_GameChallengeLoaded` (Skate 2 opcode `0x0D`) back to arcadia.

### Phase 2 trigger — `TrackChallengeLoadedReport`

`ChallengeFlow.TrackChallengeLoadedReport` is called by the `ChallengeLoadedHandler` for each peer's `MT_GameChallengeLoaded`. It accumulates peer UIDs into `ChallengeReporters`. When the count equals the eligible-peer count, Phase 2 fires:

```csharp
fire = !server.ChallengeReadyFired && eligibleCount > 0 && reportedCount >= eligibleCount;
if (fire) server.ChallengeReadyFired = true;
```

Single-shot CAS gate — Phase 2 only fires once.

### Phase 2 — `MT_GameReset(Challenge)`

`Skate2ChallengeFlow.Phase2Async`:

```csharp
// Close the gate BEFORE Reset(Challenge) hits the wire.
// Phase 2 IS the sim-reset trigger — FirstFrame will be the clearing signal.
int epoch = ResetGate.Close(server, eligible, "Skate2-challenge-phase2");

// Per peer: send MT_GameReset(Challenge, activity) reliable, then
// ResetGate.RecordResetSent(peerSession, epoch, seq)
```

So Phase 2 is the "this is the real reset, lockstep simulation begins now" boundary, and that's where the gate closes. Phase 2 also arms the `ResetWatchdog` (`allowDuringInProgress: true`, resend = `ResendPhase2Async`) — a lost Phase-2 reset that transport recovery somehow can't fix gets an app-level re-broadcast instead of wedging the lobby at the loading screen.

### Skate 1 challenge — single-phase

Skate 1's challenge flow is single-phase — there's no separate ChallengeLoad → Challenge handshake. `Skate1ChallengeFlow.StartAsync` re-closes the reset gate (`ResetGate.Close`), then sends **one** `MT_GameReset` per peer with `Sk8ResetType.ChallengeLoad` — i.e. **resetType = 1** (the arcadia send-label and the client both call this `Reset_Challenge`) — recording each send's seq. The sends are in parallel (`Task.Run` per peer + `Task.WhenAll`), like every reset broadcaster. There is no `MT_GameChallengeLoaded` round-trip — Skate 1 has no Phase 2. Its watchdog arms with `allowDuringInProgress: true`, since the challenge holds `InProgress` for its whole duration and a challenge-reset re-send must be allowed to fire inside it.

## Other flows that fire `MT_GameReset(LobbySkate)`

Several other server-side events broadcast `LobbySkate`:

| Flow | When | Code |
|---|---|---|
| `JoinFinalizeFlow` | New peer finished handshake; rebuild slot table for everyone | `Flow/JoinFinalizeFlow.FireAsync` |
| `MapChangeFlow` | Host requests a map change (`MT_GameRequestChange`) → announce, retarget attributes (ack-gated), reload (both variants) | `Flow/MapChangeFlow.RunAsync` |
| `PostChallengeFlow` | After challenge results display, return to free-skate | `Flow/PostChallengeFlow.RunAsync` |
| Debug force-reset keybind (`c`) | manual `MT_GameReset(LobbySkate)` re-broadcast; also releases the `InProgress` / challenge / map-change gates so it can abort a wedged flow | `LobbyUdpServer.ForceBroadcastGameResetAsync` |

All four go through `ResetBroadcaster.BroadcastLobbySkateAsync`, so the FirstFrame barrier + watchdog re-send arm uniformly.

> **Host-leave is *not* in this list — it does not fire a reset.** The Skate 2 host-leave dissolve (`HostLeaveFlow.BroadcastHostLeaveDissolveAsync`) sends `MT_GameRequest(LostConnection)` to the remaining peers; Skate 1 host-leave uses the normal `MT_GameRemovePlayer` + `PLAYER_LEFT` path. See [s1-vs-s2-differences.md](s1-vs-s2-differences.md#host-leave).

## The reset watchdog

`ResetWatchdog` (`Reset/ResetWatchdog.cs`) is the safety net. `Arm` snapshots every eligible peer's `GameSyncsReceived` as a baseline; `RunLoopAsync` then ticks every `TickMs` (= 50 ms) and runs a two-stage check:

1. **Resume** — every watched peer must climb to `baseline + ResumeGsThreshold` (= 6) GameSyncs *and* clear `WaitingForFirstFrameAfterReset`. If they all do, the lobby is "resumed." If they don't within `ResumeDeadlineMs` (= 12 000), the watchdog re-broadcasts the reset (`"never-resumed"`).
2. **Stay healthy** — after resume, a `PostResumeGraceMs` (= 1 000) grace absorbs the bursty ramp, then if the aggregate GameSync count stops advancing for `FrozenConfirmMsSkate1` (= 3 000) on Skate 1 / `FrozenConfirmMsSkate2` (= 1 500) on Skate 2, it re-broadcasts (`"resumed-then-froze"`). Once progress holds for `HealthyConfirmMs` (= 5 000) it disarms (`"healthy"`).

It re-broadcasts at most `MaxAttempts` (= 4) times, then gives up. It disarms early if the lobby drops below 2 watched peers (either phase). A watched peer that leaves mid-window is pruned from the baselines each tick and, post-resume, the aggregate progress sum is rebased to the survivors — the sum is taken over the watched set, so without the rebase a departure would read as a freeze and fire a spurious re-broadcast at healthy peers. The **liveness disarm** — a peer silent past `AliveWindowMs` (= 5 000) → disarm `peer-not-alive` — applies **only in the post-resume phase**. Pre-resume, a peer being app-quiet *is* the stall the watchdog exists to fix: a resetting/loading peer emits only heartbeat/ack frames, which don't refresh `LastAppMsgRxAt`, so a liveness check there would false-trip. Pre-resume therefore has **no** liveness disarm — only the `ResumeDeadlineMs` (12 s) never-resumed deadline drives the re-broadcast.

The `Game.InProgress` interaction is label-aware (`Arm`'s `allowDuringInProgress`): a **lobby**-reset watchdog (`allowDuringInProgress: false`) finding `InProgress` set at fire time disarms as `in-progress-superseded` — a challenge claimed the lobby, so the pending lobby re-send is obsolete and must not double-drive it. A **challenge**-reset watchdog (`allowDuringInProgress: true` — Skate 1 challenge, Skate 2 Phase 2) fires normally, because re-sending the challenge reset during its own challenge is the entire point. Suppression never burns an attempt. All constants are `public const` on `ResetWatchdog`.

The watchdog is purely a safety valve — it should rarely fire if the relay is healthy. It exists because the cost of a wedged lobby is "everyone has to re-join from FESL," which is bad UX.

## The `InProgress` gate and its guards

A challenge or map-change claims the lobby's `InProgress` gate (`GameServerListing.TryStart`, `EA/Session.cs`) for its whole duration. While the gate is held, the lobby is **hidden from every matchmaker** (the `ConnectionManager` matchmaking queries filter `!InProgress`) and **direct-GID `EGAM` joins are rejected** (`TheaterHandler.HandleEGAM` — closes the friend-invite/URL bypass). So nobody can join a lobby mid-match or mid-map-change. The gate is **not** `CanJoin` — `CanJoin` latches true once when the host's loading-flow reset is sent and never flips back; `InProgress` is the transient per-match gate.

The gate is released on the happy path by map-change's `finally` (bounded ~8 s) or, for a challenge, by `PostChallengeFlow` on the first peer's `MT_GameComplete`. Two guards clear it if a flow throws (a stuck gate would hide a *non-empty* lobby from matchmaking and reject all joins indefinitely):

- The challenge-start and Phase-2 `Task.Run`s in `ChallengeFlow` clear `InProgress` + `ChallengeAwaitingReady` if their flow throws.
- `PostChallengeFlow.RunAsync`'s `finally` clears `InProgress` even if the post-challenge sequence throws before its happy-path clear.

Two peer-departure guards complement these: a leaver who was the last peer Phase 2 was waiting on triggers a readiness re-check (`ChallengeFlow.OnPeerLeft` — fires Phase 2 for the survivors, or clears the gates if no eligible peers remain), and the post-challenge results collect skips peers that have left (`PostChallengeFlow.StillPresent`) instead of waiting out its ceiling on them. The manual `c` keybind releases the same gates immediately, then broadcasts the free-skate reset.

## Reset broadcasts are parallel — both variants

Every reset broadcaster in this codebase fans out **in parallel**: a `Task.Run` per eligible peer, then `await Task.WhenAll`. This holds for `ResetBroadcaster.BroadcastLobbySkateAsync`, `ResetBroadcaster.ResendLobbySkateAsync`, `Skate1ChallengeFlow.StartAsync`, and `Skate2ChallengeFlow.Phase1Async` / `Phase2Async`. There is **no** sequential per-peer stagger, no slowest-peer-first ordering, and no inter-send `Task.Delay` anywhere in the reset path.

The cross-peer hazard at a reset boundary — a fast peer emitting its post-reset FirstFrame GameSync before a slow peer has processed its own reset — is handled entirely by the **FirstFrame barrier** (sender-side pre-reset GameSync filter + destination-side `PendingForResetReleaseToDst` hold), not by spacing the sends. See [lockstep-and-relay.md](lockstep-and-relay.md).

## What gets armed in `ArmWaitFirstFrameBarrierLocked`

This is the key state change every reset triggers, always reached through `ResetGate.Close`. From the session record:

```csharp
session.WaitingForFirstFrameAfterReset       = true;
session.WaitingForFirstFrameAfterResetSetAt  = utcNow;
session.BarrierEpoch                         = epoch;  // the ResetGate epoch this barrier belongs to
session.ResetSeqSent                         = false;  // set by RecordResetSent once the reset has a seq
session.FirstFrameSrcSeqKnown                = false;
session.LockstepFrame                        = 0;   // new epoch: client recreates its CommandQueue at frame 1
// and unconditionally clears, on every call — stale pre-reset state that would
// otherwise drain into the rebuilt post-reset CommandQueue and poison it:
session.OrderedPendingRelayBodies.Clear();
session.PendingForResetReleaseToDst.Clear();
```

While the barrier is armed:
- Sender-side filter (`GameSyncHandler.HandleSkate1` / `HandleSkate2`): the peer's pre-reset GameSyncs are dropped from the relay (`consumeOnly = true`). The clear requires the epoch signal (S2 `FirstFrame` flag / S1 `mFrame ≤ 20`) **and** that the carrying datagram acked the recorded reset seq.
- Destination-side hold (`RelayPipeline`): bodies destined for this peer are parked in `PendingForResetReleaseToDst` until their FirstFrame clears the barrier.
- Send gate (`EncryptedSender.SendDataAsync`): GameSync-bearing relay sends re-validate epoch + barrier at seq allocation and are stripped on rejection.

See [lockstep-and-relay.md](lockstep-and-relay.md) for the FirstFrame barrier mechanics.

## See also

- [lockstep-and-relay.md](lockstep-and-relay.md) — the FirstFrame barrier + loading gate that resets drive.
- [s1-vs-s2-differences.md](s1-vs-s2-differences.md) — per-variant reset orchestration differences.
- [app-layer.md](app-layer.md) — `MT_GameReset` and `Sk8ResetType` wire format.
