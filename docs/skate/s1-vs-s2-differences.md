# Skate 1 vs Skate 2 differences

A reference table of every behavioral split between Skate 1 and Skate 2 in this codebase, with the file + symbol where each is gated. Useful when adding a feature: search this doc for the comparable feature in the other variant first.

The variant is `server.Variant` (`GameVariant` enum in `EA/Session.cs`).

## FESL / Theater identity

| Concern | Skate 1 | Skate 2 | Source |
|---|---|---|---|
| FESL port | 18231 | 18420 | `EA/Ports.cs` (`FeslGamePort`) |
| Theater port | 18236 | 18126 | `EA/Ports.cs` (`TheaterGamePort`) |
| FESL hostname | `skate-ps3.fesl.ea.com` | `skate2-ps3.fesl.ea.com` | `Hosting/DnsHostedService.cs` |

## ProtoTunnel encryption

| Concern | Skate 1 | Skate 2 | Source |
|---|---|---|---|
| Encrypted tunnel set | idx 1, 2, 7 | idx 7 only | `ProtoTunnelCodec.IsEncryptedTunnel` |
| CommUDP idx set | idx 1 or 2 | idx 1 only | `ProtoTunnelCodec.IsCommUdpTunnel` |
| ConnAck `applyLen` | 18 bytes (covers CommUDP header) | 10 bytes (preamble only) | `ProtoTunnelCodec.BuildConnAck` |
| Data `applyLen` | full payload | 10 bytes | `ProtoTunnelCodec.BuildData` / `BuildDataBundle` |

See [../protocol/prototunnel.md](../protocol/prototunnel.md) for full context.

## Sk8::Net::Message opcodes

Per-message wire byte differs:

| Kind | Skate 1 | Skate 2 |
|---|:-:|:-:|
| GameSync | `0x04` | `0x01` |
| GameReset | `0x05` | `0x02` |
| GameRequest | `0x06` | `0x03` |
| GameAttributes | `0x07` | `0x04` |
| GameResetAttributes | `0x0F` | `0x0E` |
| GameRequestReset | `0x10` | `0x0F` |
| GameRecipeRequest | `0x14` | `0x17` |
| GameRecipeHead | `0x15` | `0x18` |
| GameRecipeData | `0x16` | `0x19` |

Full table at [app-layer.md](app-layer.md#variant-specific-opcodes). Mapping function: `Sk8Opcodes` (`GameSync`/`GameReset`/… accessors + `Decode`).

## Wire-layout differences

### MT_GameReset body

`GameResetPacket.Build`:

| | Skate 1 | Skate 2 |
|---|---|---|
| Slot count | 6 | 8 |
| Slot-table bytes | 48 | 64 |
| Total body | 53 (opcode + 48 + 4 resetType) | 85 (opcode + 64 + 4 resetType + 4 const + 4 unused + 8 activity) |
| Has `mActivity` field | no | yes (drives challenge selection) |

The Skate 2 slot table is always 8 wide even though the per-lobby cap is 6.

### MT_GameRequest body

`GameRequestPacket.Build`:

| | Skate 1 | Skate 2 |
|---|---|---|
| Total body | 3 bytes | 10 bytes |
| `mValue` width | 1 byte | 8 bytes BE |

### MT_GameAttributes slots

`GameAttributesPacket.Build`:

| | Skate 1 | Skate 2 |
|---|---|---|
| Slot count | 12 | 9 |
| LockTime trailer | 8 bytes | 0 bytes |
| Slot 0 (`GameVersion`) | `"#BAM"` | `""` (empty) |
| Max bytes per attribute field | 63 | 64 |

See [app-layer.md](app-layer.md) for the per-slot field meanings.

### Sk8GameRequest.LostConnection encoding

`GameRequestPacket.LostConnection`:

```csharp
public static byte[] LostConnection(GameVariant variant)
    => Build(variant,
        variant == GameVariant.Skate1 ? (Sk8GameRequest)7 : Sk8GameRequest.LostConnection,
        0);
```

- Skate 2: `mRequest = 4` (`Sk8GameRequest.LostConnection`)
- Skate 1: `mRequest = 7` (Skate 1's encoding overlaps with `Heartbeat` on the enum, but functions as LostConnection)

## Variant-gated behavior

### Walker dispatch

The big walker switch (`AppLayerWalker.DispatchHandler`) has variant gates on opcodes that only exist in one game:

- `GameRecipeHead` / `GameRecipeData` inbound handlers — Skate 2 only (`RecipeHandlers.HandleHeadSkate2` / `HandleDataSkate2`); Skate 1 clients don't upload recipes over UDP (they use the Plasma `blob/AddBlob` channel — see [recipe-pipeline.md](recipe-pipeline.md)).
- `GameChallengeLoaded` — Skate 2 only.
- `GameSyncPoint` — Skate 2 only (Own The Spot challenge scoring).
- `GameAttributeUpdate` — Skate 2 only.
- `GameFinalResults` — both, but with variant-specific handler logic.

### Skate 1 Broadcast wrapper

Skate 1 wraps some messages in a `Broadcast` (0x01) / `Broadcasted` (0x02) wrapper. Inbound `Broadcast` opcode triggers a relay-time rewrite to `Broadcasted` + 8-byte sender UID. See `AppLayerWalker.WalkAppMessages`.

Skate 2 has no equivalent — all peer messages are unwrapped to begin with.

### Loading gate parse format

`LoadingGate` (`CountGameSyncsFast` / `WalkRanges`) walks the body to identify GameSyncs. The per-variant message size table (`attrTrailerBytes`, `resetBodySize`, `grBodySize`) differs:

```csharp
int attrTrailerBytes = variant == GameVariant.Skate1
    ? Sk8MessageLayout.AttributeListSkate1LockTimeTrailerBytes  // = 8
    : 0;
int resetBodySize = variant == GameVariant.Skate1 ? 52 : 84;
int grBodySize    = variant == GameVariant.Skate1 ? 2 : 9;
```

(Note: `resetBodySize` here is measured **after** the opcode byte, so 52/84 = the body sizes minus 1.)

## Per-lobby cap

`GameServerListing.MaxPlayers` (`EA/Session.cs`):

```csharp
// Per-lobby player cap, variant-correct.
//   Skate 1 = 2 (3+ player lockstep is an S1 client-architecture limit — parked)
//   Skate 2 = 6
public int MaxPlayers => ...
    : (Variant == GameVariant.Skate2 ? 6 : 2);
```

## Challenge flow

| Concern | Skate 1 | Skate 2 |
|---|---|---|
| Number of phases | 1 (single `Reset(ChallengeLoad)`, resetType 1) | 2 (`Reset(ChallengeLoad)` + `Reset(Challenge)`) |
| Pre-arms WaitFirstFrame sync barrier | yes (`ChallengeFlow.MaybeStart` → `ArmSyncBarrier`) | only Phase 2 (`Skate2ChallengeFlow.Phase2Async`) |
| `MT_GameChallengeLoaded` ready signal | no — single-phase | yes — peers report when assets loaded |
| Reset broadcast | parallel (`Task.WhenAll`) | parallel (`Task.WhenAll`) — no stagger |

See [reset-and-challenge-flow.md](reset-and-challenge-flow.md) for full details.

## Map change

| | Skate 1 | Skate 2 |
|---|---|---|
| Real world reload triggered by | `MT_GameChange(mChange=1)` → `RecycleGame` | `MT_GameReset` |
| `MT_GameReset` semantics | respawn-in-place | reload world |

Branch by variant in `MapChangeFlow.RunAsync`.

## Host leave

| Behavior | Skate 1 | Skate 2 |
|---|---|---|
| When creator leaves | dissolve lobby: `MT_GameRequest(mRequest=7, mValue=4)` to remaining peers — mValue 4 = KICKED_BY_GAME_HOST, so clients show the "host has kicked you" dialog rather than silently dumping to menus | dissolve lobby: `MT_GameRequest(mRequest=4, LostConnection)` to remaining peers |

Both games dissolve (no `RemovePlayer` / `PLAYER_LEFT` on the host-leave path); `GameRequestPacket.HostKick(variant)` picks the wire tuple — `(7, 4)` on Skate 1, and on Skate 2 it delegates to `LostConnection` (`(4, 0)`; S2 has no reason value in play). The same tuple serves the host-initiated 0x8D kick, so kick and host-leave read identically to an S1 client by design. Gate in `LobbyUdpServer.RemoveAndAnnounceLeaveAsync`: `hostLeft` (leaver UID == `Game.UID`).

## Recipe pipeline

| | Skate 1 | Skate 2 |
|---|---|---|
| Client → arcadia upload path | Plasma `blob/AddBlob` (FESL / TCP) | UDP `MT_GameRecipeHead` + `MT_GameRecipeData` |
| Inbound UDP upload handlers | none — Skate 1 doesn't upload over UDP | `RecipeHandlers.HandleHeadSkate2` / `HandleDataSkate2` |
| `MT_GameRecipeRequest` handling | `RecipeHandlers.HandleRequest` — variant-agnostic | same |
| arcadia → client serving | `RecipeService.RespondAsync` — variant-agnostic | same |
| Blob cache key | `(peerId, contentType)` | same |

`RecipeService.RespondAsync` is the same code for both variants — it serves a real cached blob if one exists for the peer, otherwise `MT_GameRecipeHead(size=0)`. The variant only selects the opcode (`BuildHead` / `BuildData`). See [recipe-pipeline.md](recipe-pipeline.md).

## CommUDP / ProtoTunnel reliability

Both variants run the same shared DirtySDK CommUDP module, so the reliability machinery (Kinds, seq/ack, bundled sub-packets, the retransmit timer) is common. What differs is a few game-set parameters:

- **Send cadence** — Skate 1 has no server-tunable send rate: the client's sync-command cadence is autonomous (its internal `SpeedControl`) and capped by a hardcoded `kMaxSyncCommands`. Arcadia cannot pace Skate 1's lockstep from the server. Skate 2's internal send pacing was not characterised.
- **Game-network receive queue** — **32 slots in both games**, each ~1272 bytes (CommUDP `maxPacketSize` 1256 plus header). Both games set it explicitly at params-init (a queue length of 32) and also carry a `≤ 0 → 32` fallback in their GameManager init as belt-and-suspenders. The same init also sets a 10-second game-network timeout in both games.
- **Retransmit timer** — the ~100 ms / ~2500 ms CommUDP retransmit thresholds are the standard DirtySDK keepalive values; both games run the same module. See [reliability-and-retransmit.md](reliability-and-retransmit.md).

## See also

- [../protocol/prototunnel.md](../protocol/prototunnel.md) — variant differences in the encryption layer.
- [app-layer.md](app-layer.md) — full opcode table per variant.
- [reset-and-challenge-flow.md](reset-and-challenge-flow.md) — per-variant challenge orchestration.
- [reliability-and-retransmit.md](reliability-and-retransmit.md) — CommUDP reliability.
