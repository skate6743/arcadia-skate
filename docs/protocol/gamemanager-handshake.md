# GameManager handshake

The DirtySDK `GameManager` module defines a small, fixed set of opcodes that every Plasma-era title uses for lobby setup, roster management, and player events. This is the **most portable layer in the whole stack** — the opcode table, wire-byte bias, and canonical sequence are identical between Skate 1, Skate 2, Battlefield Bad Company 2, NFS Shift, Burnout Paradise, Mercenaries 2, MOH Airborne, etc.

If you're building a server for a different DirtySDK title, this doc is the one you want to read first.

All GameManager frames ride **NetGameLink** with `sysFlag = 0x01` (System). See [netgamelink.md](netgamelink.md) for the envelope.

Source:
- `src/server/Hosting/Lobby/Protocol/WireEnums.cs` (opcode enum + bias)
- `src/server/Hosting/Lobby/Protocol/HandshakePackets.cs` (packet builders)
- `src/server/Hosting/Lobby/Protocol/RosterPlayer.cs` (the shared player-record struct)
- `src/server/Hosting/Lobby/Protocol/AppStage.cs` (per-peer handshake stage machine)
- `src/server/Hosting/Lobby/Handshake/HandshakeFlow.cs` (the per-stage driver)
- `src/server/Hosting/Lobby/Walker/SystemFrameDispatcher.cs` (inbound System dispatcher)

## Opcode table

```csharp
public enum GameManagerPacketType : byte
{
    Hello                   = 0,
    Goodbye                 = 1,
    HostHello               = 2,
    HostRosterElem          = 3,
    RosterAck               = 4,
    PlayerJoin              = 5,
    PlayerJoinQuery         = 6,
    PlayerJoinQueryResult   = 7,
    PlayerJoinFullMesh      = 8,
    JoinComplete            = 9,
    PlayerLeft              = 10,
    ConnectionChange        = 11,
    ConnectionChange2       = 12,
    RelayRequest            = 13,
    RelayMessage            = 14,
    HostPropertyChange      = 15,
    StartGameRequest        = 16,
    StartGameReady          = 17,
    StartGame               = 18,
    EndGame                 = 19,
    VoipEnabledChange       = 20,
    VoipReceiverChange      = 21,
    HostMigrationComplete   = 22,
    PlayerPlayGroupChange   = 23,
}
```

(`WireEnums.cs`, `GameManagerPacketType`.)

## Wire-byte bias

The on-wire opcode byte is **handler index + 0x80**. Defined at `WireEnums.cs` (`GameManagerPacketTypeExtensions`):

```csharp
public const byte WireBias = 0x80;
public static byte ToWireByte(this GameManagerPacketType t) => (byte)((byte)t + WireBias);
```

Why: the client's `SystemPacketEncoder` writes opcodes via `PutIntegerData` with the +0x80 char bias. This is a DirtySDK invariant, not Skate-specific — every Plasma-era title applies the same bias on the wire.

So:
- `Hello` (handler 0) → wire byte `0x80`
- `HostHello` (handler 2) → wire byte `0x82`
- `RosterAck` (handler 4) → wire byte `0x84`
- `JoinComplete` (handler 9) → wire byte `0x89`
- `RelayRequest` (handler 13) → wire byte `0x8D`
- `PlayerJoinFullMesh` (handler 8) → wire byte `0x88`

When reading inbound (e.g. `SystemFrameDispatcher.TryHandle`):

```csharp
if (opcodeByte == GameManagerPacketType.Hello.ToWireByte() && session.Stage == AppStage.Idle)
```

## Sign-bias encoding (BiasedEncoding)

Numeric fields in System frames use **biased encoding** — signed integers are stored as `value + 2^(N-1)` so they're always non-negative on the wire. Defined in `Wire/BiasedEncoding.cs`:

```csharp
public const byte   CharBias    = 0x80;
public const ushort Sint16Bias  = 0x8000;
public const uint   Sint32Bias  = 0x80000000u;
public const ulong  Sint64Bias  = 0x8000000000000000UL;

public static void WriteSint32(Span<byte> dst, int value)
    => BinaryPrimitives.WriteUInt32BigEndian(dst, unchecked((uint)value) + Sint32Bias);
```

So a `Sint32(0)` on the wire is `0x80000000`, `Sint32(1)` is `0x80000001`, `Sint32(-1)` is `0x7FFFFFFF`.

**This bias applies inside System bodies and inside Fesl opcodes — but NOT inside title-specific Sk8::Net::Message bodies.** Mixing them up causes silent identity mismatches.

String fields are written as `Sint32 length` followed by raw bytes (`BiasedEncoding.WriteString`).

## Canonical handshake sequence

```
client                          server
  |                                |
  |  HELLO (0x80) →                |    AppStage: Idle → HelloReceived
  |                                |
  |                ← HOST_HELLO (0x82)   AppStage: HostHelloSent
  |                ← HOST_ROSTER_ELEM (0x83)   ×N  (one per player)
  |                                |
  |  ROSTER_ACK (0x84) →           |    AppStage: HostRosterElemSent → RosterAckReceived
  |                                |
  |                ← PLAYER_JOIN_COMPLETE (0x89)   AppStage: JoinCompleteSent
  |                                |
  |        (sysFlag flips to App, title-specific bootstrap begins)
  |                                |
```

Stage machine: `AppStage` (`Idle → HelloReceived → HostHelloSent → HostRosterElemSent → RosterAckReceived → JoinCompleteSent → GameAttribsSent → [S1: GameResetSent / S2: GameRecipeRequestSent]`).

Driver: `HandshakeFlow.DriveAsync` — runs each tick while `HandshakeFlow.NeedsDrive(session)` is true.

Cross-confirmed: this sequence is the same in Battlefield Bad Company 2 custom-server logs.

## Packet shapes

### HELLO (client → server)

Client sends a HELLO when it first wants to handshake. Wire byte `0x80`. Arcadia's dispatcher doesn't parse the body — only the opcode byte matters for the state advance:

```csharp
// SystemFrameDispatcher.TryHandle
if (opcodeByte == GameManagerPacketType.Hello.ToWireByte() && session.Stage == AppStage.Idle)
{
    session.Stage = AppStage.HelloReceived;
    server.OnHelloReceived(ep, session);
    ...
}
```

### HOST_HELLO (server → client)

Wire byte `0x82`. Built by `HandshakePackets.BuildHostHello`. The full byte map:

```
+0   [1]    opcode = 0x82
+1   [2 BE] 0x8014                         Sint16 ver = 20
+3   [4 BE] 0x80000000                     Sint32 game name length = 0
+7   [2 BE] 0x8000                         Sint16 ignored
+9   [1]    BiasedChar(netType)            0x80 = ClientServer, 0x81 = PeerToPeer
+10  [1]    0x81                           constant (arcadia always sends this)
+11  [1]    0x80                           currentJoinMode = 0
+12  [2]    0x00 0x00                      currentJoinFlags = 0
+14  [1]    0x00                           inProgress
+15  [1]    0x00                           ranked
+16  [1]    0x00                           joinInProgress
+17  [1]    0x00                           joinViaPresence
+18  [1]    0x80                           inviteStatus = 0
+19  [1]    0x00                           hostMigration
+20  [3]    0x00 0x06 0x01                 PlayerType[0]: cap=6
+23  [3]    0x00 0x00 0x00                 PlayerType[1]: cap=0
+26  [8]    0x00 × 8                       xb360 nonce (zero for PS3)
+34  [2 BE] rosterSizeWire                 number of roster elems that follow
+36  [1]    0x00                           hasHost  (0 = host slot embedded in roster instead)
```

Total: 37 bytes.

Important values (from `HandshakeFlow.DriveAsync` → `HandshakePackets.BuildHostHello`):

- **Byte 9 (`netType`)**: `0x80` = ClientServer. Arcadia always sends `0x80` here.
- **Byte 36 (`hasHost`)**: `0x00`. The host's own roster element is included in the upcoming HOST_ROSTER_ELEM stream rather than embedded inline in HOST_HELLO.

### HOST_ROSTER_ELEM (server → client) × N

Wire byte `0x83`. Built by `HandshakePackets.BuildHostRosterElem`. One emitted per player in the lobby; arcadia advances `session.RosterEmittedCount` after each send and transitions to `HostRosterElemSent` after the last one (`HandshakeFlow.DriveAsync`).

Body layout = `[opcode] [RosterPlayer.WriteFields(...)]`. The RosterPlayer field set is documented below — same as PLAYER_JOIN.

### ROSTER_ACK (client → server)

Wire byte `0x84`. Client confirms it processed all the roster elements. Arcadia's dispatcher only checks the opcode byte:

```csharp
// SystemFrameDispatcher.TryHandle
if (opcodeByte == GameManagerPacketType.RosterAck.ToWireByte() && session.Stage == AppStage.HostRosterElemSent)
{
    session.Stage = AppStage.RosterAckReceived;
    ...
}
```

### PLAYER_JOIN_COMPLETE (server → client)

Wire byte `0x89` (handler 9 = JoinComplete). Built by `HandshakePackets.BuildPlayerJoinComplete`:

```
+0  [1]    opcode = 0x89
+1  [4 BE] Sint32(info.PlayerRef)
```

Total: 5 bytes. After arcadia sends this, the handshake transitions to `JoinCompleteSent` and the next stage emits title-specific bytes (Skate sends `MT_GameAttributes` with `sysFlag = 0x00`).

### PLAYER_JOIN (server → client) — late arrival

Wire byte `0x85`. Built by `HandshakePackets.BuildPlayerJoin`. Announces a new player to peers already in the lobby:

```
+0   [1]    opcode = 0x85
+1   [4 BE] Sint32(joiner.PlayerRef)
+5   [...]  RosterPlayer fields (see below)
```

> Note: `PLAYER_JOIN` (0x85) does **not** activate the joiner in the receiver's roster — `PLAYER_JOIN_FULL_MESH` (0x88) does. For full-mesh activation post-join, follow up with `PLAYER_JOIN_FULL_MESH`.

### PLAYER_JOIN_FULL_MESH (server → client)

Wire byte `0x88`. Built by `HandshakePackets.BuildPlayerJoinFullMesh`:

```
+0  [1]    opcode = 0x88
+1  [4 BE] Sint32(joinerPlayerRef)
```

Total: 5 bytes. Activates the peer in the receiver's roster.

### PLAYER_LEFT (server → client)

Wire byte `0x8A`. Built by `HandshakePackets.BuildPlayerLeft`:

```
+0  [1]    opcode = 0x8A
+1  [4 BE] Sint32(leaver.PlayerRef)
+5  [1]    BiasedChar(reason)        0x80 = normal, other codes per title
```

Total: 6 bytes.

### RELAY_REQUEST (client → server) — host-kick

Wire byte `0x8D`. Used by the host client to request the server kick a specific peer. Detected in `SystemFrameDispatcher.TryHandle`:

```csharp
if (opcodeByte == GameManagerPacketType.RelayRequest.ToWireByte()
    && session.PlayerInfo is not null
    && userBodyLen >= 2 + 4 + 4 + 2)
{
    int destRef  = (int)(BinaryPrimitives.ReadUInt32BigEndian(work.AsSpan(payloadBase + 2, 4)) - BiasedEncoding.Sint32Bias);
    int innerLen = (int)(BinaryPrimitives.ReadUInt32BigEndian(work.AsSpan(payloadBase + 6, 4)) - BiasedEncoding.Sint32Bias);
    int innerBase = payloadBase + 10;
    if (innerLen >= 2 && innerBase + innerLen <= payloadBase + userBodyLen)
    {
        byte[] lc = GameRequestPacket.LostConnection(server.Variant);
        if (work[innerBase] == lc[0] && work[innerBase + 1] == lc[1])
        {
            // host-kick recognized → fire HandleHostKickAsync
            server.OnHostKickRequested(destRef);
        }
    }
}
```

Wire layout:

```
+0  [1]    sysFlag = 0x01 (System)
+1  [1]    opcode  = 0x8D (RelayRequest)
+2  [4 BE] Sint32(destRef)            target player's playerRef (biased)
+6  [4 BE] Sint32(innerLen)           inner-body length (biased)
+10 [...]  inner body (a title-specific MT_GameRequest with LostConnection mRequest)
```

(Offsets are from the NetGameLink body start — matching the `payloadBase + N` indexing in the dispatcher code above — so `+0` is the sysFlag and `+1` the opcode, unlike the opcode-relative diagrams elsewhere in this doc.)

This is how Skate's "host kicks player" surfaces on the wire. See [../skate/s1-vs-s2-differences.md](../skate/s1-vs-s2-differences.md) for the variant-specific inner GameRequest format.

## RosterPlayer wire layout

Shared by HOST_ROSTER_ELEM and PLAYER_JOIN bodies. Built by `RosterPlayer.WriteFields`:

```
+0     [4 BE] Sint32(playerRef)                       primary ref
+4     [4 BE] Sint32(playerRef)                       repeated
+8     [4 BE] Sint32(slotId)                          slot index in roster
+12    [4 BE] Sint32(nameLen) + [nameLen] name bytes  string
+...   [8 BE] Sint64(uid)                             64-bit user id (PSN account id)
+...   [1]    BiasedChar(0)                           reserved
+...   [8]    0x00 × 8                                reserved
+...   [1]    BiasedChar(0)                           reserved
+...   [1]    BiasedChar(0)                           InternetAddressPair selector (0 = pair, 2 = xnaddr)
+...   [12]   InternetAddressPair                     2 × (4-byte IP + 2-byte port)  — internal + external
+...   [1]    0x00                                    ActivePlayerType (0 = PLAYER; wire 1 decodes to OBSERVER, skipped by client host-scan)
+...   [8 BE] Sint64(uid)                             repeated
```

Total fields-size = `4 + 4 + 4 + (4 + nameLen) + 8 + 1 + 8 + 1 + 1 + 12 + 1 + 8` = `56 + nameLen` (per `RosterPlayer.FieldsSize`).

### InternetAddressPair

`Wire/InternetAddressPair.cs` (`InternetAddressPair.Write`):

```csharp
public const int Bytes = 12;

public static void Write(Span<byte> dst, IPAddress internalIp, ushort internalPort, IPAddress externalIp, ushort externalPort)
{
    WriteOne(dst[..6], internalIp, internalPort);
    WriteOne(dst.Slice(6, 6), externalIp, externalPort);
}

private static void WriteOne(Span<byte> dst, IPAddress addr, ushort port)
{
    addr.GetAddressBytes().CopyTo(dst);                             // 4 bytes IPv4
    BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(4, 2), port);   // 2 bytes BE port
}
```

12 bytes total: `[4 IPv4][2 port][4 IPv4][2 port]`. First pair is the "internal" (LAN) address, second is "external" (WAN).

Selector byte preceding it (per `RosterPlayer.WriteFields`): `0` = `SelectorInternetAddressPair` (the layout above), `2` = `SelectorXnaddr` (the Xbox 360 packed network address — unused on PS3).

## Inbound dispatch summary

```csharp
// AppLayerWalker.Parse → SystemFrameDispatcher.TryHandle
if (systemFlag != NetGameLink.SysFlagSystem) return false;

// HELLO from new peer  →  arcadia builds HOST_HELLO + HOST_ROSTER_ELEMs in response
if (opcodeByte == 0x80 && stage == Idle) → advance to HelloReceived;

// ROSTER_ACK from peer  →  arcadia builds PLAYER_JOIN_COMPLETE in response
if (opcodeByte == 0x84 && stage == HostRosterElemSent) → advance to RosterAckReceived;

// RELAY_REQUEST + inner LostConnection  →  host-kick
if (opcodeByte == 0x8D) → parse destRef + inner body → fire host-kick handler;
```

Other System opcodes (Goodbye, ConnectionChange, EndGame, etc.) are accepted on the wire but currently not dispatched by arcadia — they don't fire in normal Skate flow.

## Porting checklist

If you're adapting this to BF / NFS / Etc:

1. **The opcode table doesn't change.** Use the same enum, same +0x80 bias.
2. **The handshake order doesn't change.** HELLO→HOST_HELLO→ROSTER_ELEM*→ROSTER_ACK→JOIN_COMPLETE is the canonical sequence everywhere.
3. **HOST_HELLO byte map may need per-title tweaks** for the few title-relevant fields (player-type caps, netType for CS-vs-Mesh games). The skeleton is the same.
4. **RosterPlayer fields are DirtySDK-standard.** Watch for title-specific extensions appended at the end — Skate doesn't add any but some titles do.
5. **The InternetAddressPair format** (and the selector byte) is the same. Always populate both internal + external; some clients pick one based on the demangler.
6. **`netType=ClientServer` is the safe default** for dedicated-server lobby topologies (we don't want the client building peer-to-peer connections to other clients when we're the relay).

## See also

- [netgamelink.md](netgamelink.md) — the envelope these bodies ride inside.
- [stack-overview.md](stack-overview.md) — where this layer sits.
- [../skate/app-layer.md](../skate/app-layer.md) — what the *App-flag* bodies look like (Skate's title-specific layer).
- [stack-overview.md](stack-overview.md#porting-to-another-title) — adapting this server to other DirtySDK titles.
