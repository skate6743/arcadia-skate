# App layer (Skate-specific)

This is the **title-specific** part of the stack — what rides on `sysFlag = 0x00` (App). For Skate, that's `Sk8::Net::Message` instances, identified by a 1-byte opcode at `body[0]`.

Everything in this doc is Skate-specific by design. The wire envelope (NetGameLink + sysFlag + sync trailer) is shared with every other DirtySDK title; the table of message opcodes and their bodies is not. If you're porting to another title, this doc is the model — find the equivalent factory table in that title's binary.

Source:
- `src/server/Hosting/Lobby/Protocol/WireEnums.cs` (Sk8MessageType enum)
- `src/server/Hosting/Lobby/Protocol/Sk8Opcodes.cs` (variant-specific opcode bytes)
- `src/server/Hosting/Lobby/Walker/AppLayerWalker.cs` (the per-frame walker)
- `src/server/Hosting/Lobby/Walker/Handlers/*` (per-opcode handlers)
- `src/server/Hosting/Lobby/Protocol/*Packet.cs` (per-message builders)

For the wire envelope above this layer, see [netgamelink.md](../protocol/netgamelink.md). For an end-to-end inbound packet example, see [stack-overview.md](../protocol/stack-overview.md).

## Variant-specific opcodes

Skate 1 and Skate 2 use the same set of message *kinds* but assign **different opcode bytes** to most of them. The mapping is in `Sk8Opcodes` (the per-kind accessors):

| Kind | Skate 1 byte | Skate 2 byte |
|---|:-:|:-:|
| `GameSync` | `0x04` | `0x01` |
| `GameReset` | `0x05` | `0x02` |
| `GameRequest` | `0x06` | `0x03` |
| `GameAttributes` | `0x07` | `0x04` |
| `GameComplete` | `0x08` | `0x06` |
| `GameResults` | `0x09` | `0x07` |
| `GameAllPlayersComplete` | `0x0A` | `0x08` |
| `GameFinalResults` | `0x0B` | `0x09` |
| `GameTimer` | `0x0C` | `0x0A` |
| `GameExitPostChallenge` | `0x0D` | `0x0B` |
| `GameLoadRequest` | `0x0E` | `0x0C` |
| `GameResetAttributes` | `0x0F` | `0x0E` |
| `GameRequestReset` | `0x10` | `0x0F` |
| `GameRequestChange` | `0x11` | `0x10` |
| `GameChange` | `0x12` | `0x12` |
| `GameRemovePlayer` | `0x13` | `0x13` |
| `GameRecipeRequest` | `0x14` | `0x17` |
| `GameRecipeHead` | `0x15` | `0x18` |
| `GameRecipeData` | `0x16` | `0x19` |

Skate-2-only kinds:

| Kind | Skate 2 byte |
|---|:-:|
| `GameAttributeUpdate` | `0x05` |
| `GameChallengeLoaded` | `0x0D` |
| `GameSyncPoint` | `0x11` |
| `GamePlayerAttributes` | `0x14` |
| `GameWager` | `0x15` |
| `GameProposal` | `0x16` |

Skate-1-only kinds:

| Kind | Skate 1 byte |
|---|:-:|
| `Broadcast` (wrapper) | `0x01` |
| `Broadcasted` (wrapper) | `0x02` |
| `GameLoadDone` | `0x03` |
| `GameQOSInfo` | `0x17` |

The full enum order (kind names) is the `Sk8Opcodes.Kind` enum. The decoder is `Sk8Opcodes.Decode(variant, op)` — it returns the kind or null for unrecognized bytes.

## Walker behavior

`AppLayerWalker.WalkAppMessages` iterates from `MsgOffset = 1` (skipping the sysFlag byte) until `MsgOffset >= UserBodyLen`. At each step:

1. `op = work[payloadBase + ctx.MsgOffset]`.
2. **Skate 1 Broadcast wrapper handling**: opcodes `0x01` (Broadcast) and `0x02` (Broadcasted) are wrapper opcodes that prefix the next message. `Broadcast` (`0x01`) is rewritten by arcadia as `Broadcasted` (`0x02` + 8-byte sender UID) before relay — Skate 1's client only consumes the unwrapped `Broadcasted` form.
3. Decode `kind = Sk8Opcodes.Decode(variant, op)`.
4. Dispatch to the appropriate handler in `Walker/Handlers/*`:
   - `GameSyncHandler.HandleSkate1` / `HandleSkate2`
   - `GameResetHandler.Handle`
   - `GameRequestHandler.Handle`
   - `AttributesHandlers.HandleGameAttributes` / `HandleGameResetAttributes` / `HandleGameAttributeUpdateSkate2`
   - `RecipeHandlers.HandleRequest` / `HandleHeadSkate2` / `HandleDataSkate2`
   - `ChallengeLoadedHandler.Handle`
   - `GameSyncPointHandler.Handle`
   - `GameChangeHandlers.*`
   - `ResultsHandlers.*`
   - `FixedFallbackHandler.Handle` — fallback using a fixed body-size lookup table.
5. Handler returns `msgLen` (how many bytes the whole message consumed) and `consumeOnly` (whether the message is dropped from the relay output or passed through).
6. Walker advances `ctx.MsgOffset += msgLen`.

Multiple messages in one body is the common case for outbound steady-state Skate traffic.

## Major message wire formats

### MT_GameSync (Skate 1)

Skate 1 wire (`GameSyncHandler.HandleSkate1`):

```
+0   [1]    opcode = 0x04
+1   [4 BE] mFrame                        simulation frame number
+5   [4 BE] mCheckSum                     desync-detection hash
+9   [1]    cmdCount                      number of input commands packed in this frame
+10  [...]  cmdCount × command record
            ┌─────────────────────────┐
            │ [1]  srcPlayer (slot)   │
            │ [1]  cmdType            │
            │ [1]  dataSize           │
            │ [N]  data (dataSize)    │
            └─────────────────────────┘
```

Parser at `GameSyncHandler.HandleSkate1`. A low `mFrame` (≤ `PostResetFrameWindow`, currently 20) is the post-reset "FirstFrame" barrier signal — see [lockstep-and-relay.md](lockstep-and-relay.md).

### MT_GameSync (Skate 2)

Skate 2 has a more compact header (`GameSyncMessage.TryParse`):

```
+0  [1]    opcode = 0x01
+1  [1]    flagByte                       bit field — see Sk8GameSyncFlag below
+2  [1]    checksum
+3  [1 or 2] dataSize                     1 byte by default, 2 bytes when flagByte has SizeFieldIsTwoBytes
+4  [N]   data (dataSize)
```

`Sk8GameSyncFlag` (`WireEnums.cs`):

```csharp
[Flags]
public enum Sk8GameSyncFlag : byte
{
    None                  = 0,
    PlayerSlotMask        = 0x0F,     // low nibble = player slot 0..15
    OnlyInput             = 0x20,
    SizeFieldIsTwoBytes   = 0x40,
    FirstFrame            = 0x80,     // post-reset barrier signal
}
```

`FirstFrame = 0x80` is the reset-barrier clear signal; `OnlyInput = 0x20` marks input-only frames (no sim state diff); `SizeFieldIsTwoBytes = 0x40` switches the size field width.

### MT_GameReset

Arcadia sends this to drive every peer's reset/phase transition — the canonical "transition to phase X" message. Built by `GameResetPacket.Build`:

**Skate 1 body** (53 bytes):

```
+0   [1]    opcode = 0x05
+1   [48]   6 × Int64 BE peerIdsBySlot   slot → uid; -1 (0xFF×8) for empty slots
+49  [4 BE] resetType                    Sk8ResetType enum
```

**Skate 2 body** (85 bytes):

```
+0   [1]    opcode = 0x02
+1   [64]   8 × Int64 BE peerIdsBySlot   slot → uid; -1 for empty
+65  [4 BE] resetType                    Sk8ResetType enum
+69  [4 BE] 1314481196                   mRandomSeed — arcadia hardcodes this constant (= 0x4E59642C)
+73  [4 BE] 0                            mSessionId — arcadia leaves this zero
+77  [8 BE] activity                     mActivityKey — drives challenge selection
```

`Sk8ResetType` (`WireEnums.cs`):

```csharp
public enum Sk8ResetType
{
    LobbySkate    = 0,
    ChallengeLoad = 1,    // challenge phase 1
    Challenge     = 2,    // challenge phase 2
}
```

Slot counts: `GameResetPacket.Skate1SlotCount = 6`, `Skate2SlotCount = 8`. Skate 2's table is always 8 slots wide even though the per-lobby cap is 6.

### MT_GameRequest

Variant-specific layouts (`GameRequestPacket.Build`):

**Skate 1** (3 bytes):

```
+0  [1]    opcode = 0x06
+1  [1]    mRequest (Sk8GameRequest)
+2  [1]    mValue                         (1 byte only — fits boolean / small int)
```

**Skate 2** (10 bytes):

```
+0  [1]    opcode = 0x03
+1  [1]    mRequest (Sk8GameRequest)
+2  [8 BE] mValue                         (64-bit value)
```

`Sk8GameRequest` (`WireEnums.cs`):

```csharp
public enum Sk8GameRequest : byte
{
    StartGame                    = 1,
    ToggleSlotAccess             = 3,
    LostConnection               = 4,      // S2; S1 uses request byte 7
    PauseResume                  = 5,
    ConvertPrivateSessionSk1     = 6,
    Heartbeat                    = 7,
}
```

> **LostConnection** is the universal "you got disconnected" / "kick this peer" signal. In Skate 2 it's `mRequest = 4`. In Skate 1 the request byte is **7** (see `GameRequestPacket.LostConnection`). This asymmetry matters when matching inner GameRequest bodies inside RelayRequest host-kicks (see [gamemanager-handshake.md](../protocol/gamemanager-handshake.md)#relay_request).
>
> Treat the `Sk8GameRequest` enum above as the **Skate 2** mapping. The `mRequest` byte values are not 1:1 across the two games — `LostConnection` alone is byte 4 in Skate 2 but byte 7 in Skate 1 — so do not assume a given byte means the same thing in both.

### MT_GameAttributes

Variant-specific slot count and trailer. Built by `GameAttributesPacket.Build`.

Wire (both variants):

```
+0    [1]   opcode               S1: 0x07, S2: 0x04
+1    [4 BE] len0
+5    [len0] string slot 0
+...   [4 BE] len1 + slot 1
+...   (repeating)
+...   [N]    lockTime trailer (S1 only — 8 bytes)
```

**Skate 2 slots** (9 total) — values from `GameAttributesPacket.BuildSkate2`:

| Slot | Field | Notes |
|---:|---|---|
| 0 | GameVersion | empty string |
| 1 | ChallengeType | e.g. `"OnlineFreeSkate"` |
| 2 | ChallengeKey | numeric string |
| 3 | PingSite | |
| 4 | IsPrivate | `"true"` / `"false"` |
| 5 | MaxPlayers | `"6"` |
| 6 | IsRanked | `"false"` |
| 7 | OverallSkill | `"0"` |
| 8 | ChallengeSkill | `"0"` |

**Skate 1 slots** (12 total) — `GameAttributesPacket.BuildSkate1`:

| Slot | Field | Notes |
|---:|---|---|
| 0 | GameVersion | `"#BAM"` |
| 1 | (constant) | `"OnlineFreeSkate"` |
| 2 | ChallengeType | |
| 3 | ChallengeKey | |
| 4 | (constant) | `"0"` |
| 5 | (constant) | `"0"` |
| 6 | PingSite | default `"arcadia"` |
| 7 | IsPrivate | `"false"` |
| 8 | MaxPlayers | `"6"` |
| 9 | (constant) | `"true"` |
| 10 | (constant) | `"false"` |
| 11 | (constant) | `"false"` |

Plus an 8-byte lockTime trailer at the end (`lockTimeTrailerBytes: 8` in `Pack`).

### MT_GameFinalResults

The end-of-challenge scoreboard. Arcadia is the authority: it collects each peer's `MT_GameResults` report after `MT_GameAllPlayersComplete`, aggregates them sorted by ranking, and broadcasts one populated `MT_GameFinalResults` (`Flow/PostChallengeFlow`). The client renders the results screen exclusively from these rows — an empty message means a blank board.

**Skate 1 body** (161 bytes, fixed) — `Sk8MessagePackets.BuildGameFinalResultsSkate1`:

```
+0    [1]     opcode = 0x0B
+1    [156]   6 × 26-byte slot
              ┌──────────────────────────┐
              │ [4 BE]  finishReason     │
              │ [8 BE]  uid              │
              │ [2 BE]  ranking          │
              │ [4 BE]  score            │
              │ [4 BE]  eventTime (u32)  │
              │ [4 BE]  0                │
              └──────────────────────────┘
+157  [4 BE]  playerCount               populated slot count
```

**Skate 2 body** (5 + N × 39 bytes) — `Sk8MessagePackets.BuildGameFinalResultsSkate2`:

```
+0   [1]     opcode = 0x09
+1   [4 BE]  playerCount               MUST be ≤ 8 — the client unpacks into a
                                       fixed 8-element array with no bounds check
+5   [...]   playerCount × 39-byte record
             ┌───────────────────────────┐
             │ [8 BE]  peerId (uid)      │  matched to the player list to attach names
             │ [4 BE]  eventTime (f32)   │  the board's time column
             │ [4 BE]  score             │
             │ [4 BE]  finishReason      │  1 = normal completion
             │ [2 BE]  ranking           │
             │ [1]     playersChoice     │  highlights the row; ranked → achievement
             │ [4 BE]  cash              │  ┐ the client credits the local player's
             │ [4 BE]  exp (f32)         │  │ wallet/XP from these fields directly —
             │ [4 BE]  wager             │  │ arcadia always sends 0
             │ [4 BE]  winnings          │  ┘
             └───────────────────────────┘
```

Note the 39-byte **wire** record is smaller than the client's 48-byte in-memory per-player struct — the wire stride constant is `Sk8MessageLayout.FinalResultsPlayerStride`. Rows are only emitted for peers still connected: the client maps `peerId` to its local player index and an unknown uid would index out of range.

### MT_GameRecipeRequest / Head / Data

Recipe = Skate's character/skater appearance data, uploaded to Plasma and chunk-served peer-to-peer through arcadia. (Skate 1's packed Create-A-Character blob is small — the client's upload buffer is 8272 bytes; Skate 2's blob size is not measured here, so treat any specific KB figure as unverified.) See [recipe-pipeline.md](recipe-pipeline.md) for the orchestration.

Wire format (`RecipePackets`):

**Request** (`size = 9`):

```
+0  [1]    opcode    S1: 0x14, S2: 0x17
+1  [8 BE] peerId    UID of the peer whose recipe is wanted
```

**Head** (`size = 17`):

```
+0  [1]    opcode    S1: 0x15, S2: 0x18
+1  [8 BE] peerId
+9  [4 BE] size      total blob size (`0` = no recipe → load default)
+13 [4 BE] crc       blob CRC32
```

**Data chunk** (`size = 17 + N`):

```
+0  [1]    opcode    S1: 0x16, S2: 0x19
+1  [8 BE] peerId
+9  [4 BE] chunkLen
+13 [4 BE] chunkIndex
+17 [N]    chunkBytes
```

Chunks are capped at ~1024 bytes each by arcadia (MTU-safe after ProtoTunnel + CommUDP + NetGameLink overhead). See `Recipe/RecipeService.cs`.

## Inbound dispatch by opcode

`AppLayerWalker.DispatchHandler` — the giant switch:

```csharp
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
    case Sk8Opcodes.Kind.GameChallengeLoaded when ctx.Server.Variant == GameVariant.Skate2:
        return ChallengeLoadedHandler.Handle(ctx, out msgLen, out consumeOnly);
    case Sk8Opcodes.Kind.GameSyncPoint when ctx.Server.Variant == GameVariant.Skate2:
        return GameSyncPointHandler.Handle(ctx, out msgLen, out consumeOnly);
    ...
    default:
        return FixedFallbackHandler.Handle(ctx, op, kind, out msgLen, out consumeOnly);
}
```

`FixedFallbackHandler.Handle` uses `Sk8MessageLayout.FixedBodySize` for opcodes with a known fixed length (the relay just passes them through). Unknown / truncated opcodes set `ctx.WalkOk = false` and the walker bails; the partial body (from offset 1) is then relayed via `ParkRelayBody` **only if** its first byte passes a safety check — non-zero and below the variant's opcode cap (`0x20` for Skate 1, `0x1A` for Skate 2). Otherwise the body is dropped (`AppLayerWalker.WalkAppMessages`).

## Outbound — building messages

The pattern across all the per-message builders (`Protocol/*Packet.cs`):

1. Allocate a `byte[]` of the exact body size.
2. Write the opcode byte (variant-aware via `Sk8Opcodes.X(variant)`).
3. Write the typed fields BE.
4. Hand to `EncryptedSender.SendSk8BodyAsync` which wraps it in a NetGameLink (`sysFlag=0x00`) and ships via the standard ProtoTunnel send path.

Example — `Sk8MessagePackets.BuildGameRequestReset`:

```csharp
public static byte[] BuildGameRequestReset(GameVariant variant, Sk8ResetType resetType)
{
    byte[] body = new byte[1 + 4];
    body[0] = Sk8Opcodes.GameRequestReset(variant);          // S1: 0x10, S2: 0x0F
    BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(1, 4), (int)resetType);
    return body;
}
```

## See also

- [stack-overview.md](../protocol/stack-overview.md) — where this layer fits.
- [netgamelink.md](../protocol/netgamelink.md) — the envelope these bodies ride inside.
- [gamemanager-handshake.md](../protocol/gamemanager-handshake.md) — the *System*-flag side (common across all DirtySDK titles).
- [Skate orchestration](README.md) — lockstep, resets, recipes, reliability.
- [Porting to another title](../protocol/stack-overview.md#porting-to-another-title) — how to find another title's factory table.
