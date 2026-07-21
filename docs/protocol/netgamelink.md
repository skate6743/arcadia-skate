# NetGameLink

NetGameLink is the game-framing layer that wraps each application body inside a CommUDP sub-packet. It does two jobs:

1. **Tags the body** as either a common GameManager opcode or a title-specific message — via the 1-byte `sysFlag` header.
2. **Carries ping/jitter telemetry** between peers — via a 10-byte sync trailer.

It's the layer where "common DirtySDK protocol" and "title-specific protocol" diverge. Every game-state byte in Skate travels through here.

Source: `src/server/Hosting/Lobby/Wire/NetGameLink.cs`.

## On-wire format

A complete NetGameLink frame inside one CommUDP sub-packet:

```
+0     [1] sysFlag         0x00 = App (title-specific body), 0x01 = System (GameManager opcode)
+1     [N] body            opcode byte + payload (the actual message)
+1+N   [10] sync trailer   (only present when kindByte has SyncPresent bit)
+11+N  [1] kindByte        0x46 = body+sync, 0x40 = ack-only (no body)
```

For ack-only frames (heartbeat with no body), the layout collapses to:

```
+0  [10] sync trailer
+10 [1]  kindByte = 0x40
```

The walker reads the `kindByte` from the END of the payload and uses its `SyncPresent` bit to compute body length.

## Constants

Defined in `NetGameLink.cs`:

```csharp
public const byte SysFlagApp        = 0x00;
public const byte SysFlagSystem     = 0x01;
public const int  SyncTrailerBytes  = 10;
public const byte KindByteAckOnly   = 0x40;
public const byte KindByteWithBody  = 0x46;
public const byte SyncPresentBit    = 0x40;
```

## sysFlag — the common/title split

The first byte of every NetGameLink body is the most important byte in the entire app-layer protocol — it decides which dispatcher consumes the body.

| sysFlag | body[0] is | Dispatcher | Examples |
|:-:|---|---|---|
| `0x01` | GameManager opcode (wire byte = `opcode + 0x80`) | `SystemFrameDispatcher.TryHandle` | HELLO (0x80), ROSTER_ACK (0x84), RelayRequest (0x8D) |
| `0x00` | Title-specific message type (raw byte, no bias) | `AppLayerWalker.WalkAppMessages` → per-opcode handlers | (Skate) MT_GameSync (0x04 S1 / 0x01 S2), MT_GameReset (0x05 S1 / 0x02 S2) |

The split is enforced in `AppLayerWalker.Parse`:

```csharp
// System frame? (HELLO / ROSTER_ACK / RelayRequest+LostConnection host-kick.)
if (SystemFrameDispatcher.TryHandle(server, ep, session,
        work, payloadBase, userBodyLen, srcBareSeq, systemFlag, opcodeByte))
{
    return;
}

if (systemFlag != NetGameLink.SysFlagApp || userBodyLen < 2)
    return;

// App frame — walk Sk8 messages...
```

So a body whose sysFlag isn't `App` (0x00) and isn't a recognized System opcode is dropped.

## Sync trailer

When `kindByte & SyncPresentBit` is set, a 10-byte trailer sits immediately before the kindByte. Written by `NetGameLink.WriteSyncTrailer`:

```csharp
public static void WriteSyncTrailer(Span<byte> dst, uint peerEchoEstimate, uint nowTick)
{
    BinaryPrimitives.WriteUInt32BigEndian(dst[..4], peerEchoEstimate);
    BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(4, 4), nowTick);
    BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(8, 2), 0);
}
```

Trailer fields:

| Offset | Type | Field | Purpose |
|---:|---|---|---|
| 0 | BE u32 | `peerEchoEstimate` | Arcadia's estimate of the peer's current tick — the peer echoes this back in its next outbound trailer as `[0..4]`. The peer subtracts to compute one-way latency. |
| 4 | BE u32 | `nowTick` | Arcadia's local tick (Environment.TickCount). The peer reads this to update its echo estimate. |
| 8 | BE u16 | `jitter` | Jitter estimate. Informational; arcadia writes 0. |

Reader half (`NetGameLink.ReadPeerSendTick`):

```csharp
public static uint ReadPeerSendTick(ReadOnlySpan<byte> trailer)
    => BinaryPrimitives.ReadUInt32BigEndian(trailer.Slice(4, 4));
```

And usage in the inbound walker (`AppLayerWalker.Parse`):

```csharp
if (syncSize == NetGameLink.SyncTrailerBytes && payloadLen >= 11)
{
    int syncBase = payloadBase + userBodyLen;
    session.LastPeerSendTick = NetGameLink.ReadPeerSendTick(
        work.AsSpan(syncBase, NetGameLink.SyncTrailerBytes));
    session.LocalTickAtLastPeerSend = (uint)Environment.TickCount;
}
```

These two timestamps drive the per-session latency estimate that arcadia uses to seed its own outbound `peerEchoEstimate` (via `NetGameLinkEcho.Estimate`).

## kindByte semantics

`kindByte` is the trailing byte of the NetGameLink frame. Its bits encode what's present:

- bit 6 (`0x40`) — sync trailer present
- bit 1, 2 (`0x06`) — body present

Concrete values in use:

| Value | Meaning | Builder |
|:-:|---|---|
| `0x40` | sync trailer present, no body (ack-only / heartbeat) | `NetGameLink.BuildAckOnly` |
| `0x46` | sync trailer present + body | `NetGameLink.BuildWithBody` |

The walker decodes via `SyncPresentBit`:

```csharp
byte typeByte = work[payloadBase + payloadLen - 1];
int syncSize = (typeByte & NetGameLink.SyncPresentBit) != 0 ? NetGameLink.SyncTrailerBytes : 0;
int userBodyLen = payloadLen - 1 - syncSize;
```

(`AppLayerWalker.Parse`.)

`userBodyLen` is the byte count from `sysFlag` through the end of the body — i.e. it includes the sysFlag byte itself. The walker then reads `work[payloadBase]` as sysFlag and starts walking message opcodes from `payloadBase + 1`.

## Builders

`NetGameLink.BuildWithBody` — the workhorse for any body-bearing frame:

```csharp
public static byte[] BuildWithBody(byte sysFlag, ReadOnlySpan<byte> body, uint peerEchoEstimate, uint nowTick)
{
    byte[] frame = new byte[1 + body.Length + SyncTrailerBytes + 1];
    frame[0] = sysFlag;
    body.CopyTo(frame.AsSpan(1));
    int syncOff = 1 + body.Length;
    WriteSyncTrailer(frame.AsSpan(syncOff, SyncTrailerBytes), peerEchoEstimate, nowTick);
    frame[syncOff + SyncTrailerBytes] = KindByteWithBody;
    return frame;
}
```

Total length = `1 + body.Length + 10 + 1` = `body.Length + 12`.

`NetGameLink.BuildAckOnly` — sync trailer + 0x40 trailer byte, 11 bytes total. Used by `EncryptedSender.SendAckOnlyAsync` for keep-alive ack frames during periods with no body traffic.

## Walker entry point

Inbound frames are decoded by `AppLayerWalker.Parse`:

1. Read trailing `kindByte` — derive `syncSize` and `userBodyLen`.
2. If sync trailer present, update `session.LastPeerSendTick`.
3. If `userBodyLen < 2` (ack-only or empty), return.
4. Add `srcBareSeq` to the dedupe set (so a retransmit of the same seq is silently swallowed).
5. Read `sysFlag = work[payloadBase]` and `opcodeByte = work[payloadBase + 1]`.
6. Try `SystemFrameDispatcher.TryHandle` (only fires if `sysFlag == 0x01`).
7. If not consumed, require `sysFlag == 0x00`, then call `WalkAppMessages` which loops over each Sk8 message in the body advancing `ctx.MsgOffset += msgLen`.

The walker loop allows **multiple title-specific messages packed back-to-back in one NetGameLink body** — arcadia uses this aggressively to coalesce outbound Sk8 traffic into fewer CommUDP packets, so the client processes more game messages per received datagram.

## Practical notes for porting

- **The sysFlag/kindByte/sync-trailer triple is DirtySDK-universal.** Every Plasma-era title uses the same envelope. Battlefield, NFS, Burnout — same bytes.
- **What changes per title is what's *inside* the body** — specifically the table that maps `body[0]` (when sysFlag=0x00) to a message class. See [../skate/app-layer.md](../skate/app-layer.md) for Skate's table, and [stack-overview.md](stack-overview.md#porting-to-another-title) for how to find another title's.
- **The GameManager opcode table (sysFlag=0x01) does not change.** It's identical across all DirtySDK titles, just with different opcodes ignored by different games. See [gamemanager-handshake.md](gamemanager-handshake.md).
- **Heartbeat-ack cadence matters.** The ack-only frame (`kindByte = 0x40`) keeps the client's CommUDP retransmit timer happy — see [commudp.md](commudp.md) for the ~100 ms / 2500 ms thresholds. Skip it and a client with unacked data starts re-soliciting.

## See also

- [stack-overview.md](stack-overview.md) — where NetGameLink fits.
- [commudp.md](commudp.md) — the layer below (each NetGameLink is one CommUDP sub-packet).
- [gamemanager-handshake.md](gamemanager-handshake.md) — what System-flag bodies look like (the common DirtySDK part).
- [../skate/app-layer.md](../skate/app-layer.md) — what App-flag bodies look like in Skate.
