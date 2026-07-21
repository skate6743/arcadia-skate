# CommUDP

CommUDP is DirtySDK's reliable-UDP transport. It rides inside a ProtoTunnel tuple (idx 1, sometimes idx 2 on Skate 1) and provides:

- **Connection lifecycle**: CONNECT / CONNACK / DISCONNECT.
- **Reliable delivery**: sequence numbers, cumulative ACKs in the same packet, client-driven retransmit-request NACKs (kind=4 PingRetransmitRequest).
- **Bundled sub-packet payloads** — N application bodies can ride in a single CommUDP DATA frame, sharing one sequence number.

Source: `src/server/Hosting/Lobby/Wire/CommUdpFrame.cs`.

## Wire format — kinds

CommUDP frames are categorized by a 4-byte big-endian "kind" at the start of the payload. Defined in `CommUdpFrame.cs`:

```csharp
public enum CommUdpKind : uint
{
    Connect              = 1,
    ConnAck              = 2,
    Disconnect           = 3,
    PingRetransmitRequest = 4,
    Poke                 = 5,
}
```

The defined kinds are 1–5. (`LooksLikePlainControl` accepts `1 ≤ kind ≤ 6` — one wider than the enum — so a stray kind=6 datagram would be *routed* as plain control, but `LobbyUdpServer.HandlePlainControlAsync` only acts on Connect/Poke/Disconnect and silently ignores anything else.)

## Wire format — plain control vs encrypted

CommUDP frames come in two flavors on the wire:

- **Plain** — 12 bytes total, sent before ProtoTunnel encryption is set up. Used for CONNECT / POKE / DISCONNECT during initial handshake.
- **Encrypted** — wrapped inside a ProtoTunnel envelope (see [prototunnel.md](prototunnel.md)). All steady-state traffic.

Detected by length and kind range (`CommUdpFrame.LooksLikePlainControl`):

```csharp
public static bool LooksLikePlainControl(byte[] buf)
{
    if (buf.Length != PlainConnLength)
        return false;
    uint kind = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(0, 4));
    return kind >= 1 && kind <= 6;
}
```

`PlainConnLength = 12`.

The dispatcher (`LobbyUdpServer.HandleDatagramAsync`) routes by this check — exactly 12 bytes with kind ∈ [1,6] is plain; everything larger goes through ProtoTunnel.

### Plain CONN / CONNACK / DISC layout

```
+0  [4 BE] kind  (1=Connect, 2=ConnAck, 3=Disconnect, 5=Poke)
+4  [4 BE] ident (client-chosen 32-bit identifier; server echoes it back)
+8  [4 BE] tail  (varies — present for CONN/POKE; padded/zero in some shapes)
```

Total 12 bytes. The server's plain reply uses `PlainAckOnlyLength = 8` — just `kind|ident` with no tail. See `LobbyUdpServer.HandlePlainControlAsync` for the reply construction.

### Encrypted DATA frame layout

Inside the ProtoTunnel idx=1 (or 2) payload, after decrypt:

```
+0  [4 BE] w0  = (subCount << 28) | (metaType << 24) | (bareSeq & 0x00FFFFFF)
+4  [4 BE] w1  = ack value (the sender's view of the last seq it received)
+8  [...]  netGameLink body (one or more sub-packets — see "Bundled sub-packets")
```

The 8-byte header is `HeaderBytes = 8`.

`w0` encoding (`CommUdpFrame.cs`):

```csharp
public const uint BareSeqMask    = 0x00FFFFFFu;   // bits 0-23  — actual seq (24 bits)
public const int  MetaTypeShift  = 24;            // bits 24-27 — metadata-type nibble
public const uint MetaTypeMask   = 0xFu;
public const int  SubCountShift  = 28;            // bits 28-31 — extra-sub-packet count

public static uint BareSeq(uint seqWord)   => seqWord & BareSeqMask;
public static int  SubCount(uint seqWord)  => (int)((seqWord >> SubCountShift) & 0xF);
public static uint MetaType(uint seqWord)  => (seqWord >> MetaTypeShift) & MetaTypeMask;
```

- **Bare seq** — 24 bits. The sequence number of the *highest* sub-packet in this frame.
- **MetaType** — 4 bits. DirtySDK CommUDP supports per-packet metadata; `metaType = 1` adds an 8-byte metadata block after the seq+ack header (`MetaType1HeaderExtraBytes = 8`). Skate-era clients send `metaType = 0`; arcadia masks seq to 24 bits so an outbound seq value that grew above 16M would not leak into this nibble.
- **subCount** — 4 bits. Number of *additional* sub-packets beyond the first (so subCount=0 means one sub-packet; subCount=3 means four). Max 16 sub-packets per frame.

> The "subCount" in the high nibble is encoded as count-minus-one. A frame with 4 sub-packets has subCount=3. See `ProtoTunnelCodec.BuildDataBundle`: `int subCount = entries.Count - 1;`.

### Encrypted ACK-only / NACK frame layout

For CONNACK in encrypted form, the kind/ident pair occupies bytes 0..7:

```
+0  [4 BE] kind   = 2 (ConnAck) or 4 (PingRetransmitRequest)
+4  [4 BE] ident  (echoed from CONN) or expectedSeq (for kind=4)
```

Total 8 bytes — `PlainAckOnlyLength = 8` is also reused here for the encrypted-form payload length.

`ProtoTunnelCodec.BuildConnAck` and `ProtoTunnelCodec.BuildPingRetransmit` construct these.

## Bundled sub-packets

A single CommUDP DATA frame can carry multiple application bodies (each one its own NetGameLink). This is critical for retransmit correctness — see "Why bundling matters" below.

### Layout

```
+0       [body of seq = HIGHEST]                          ← no length tail; implied
         [body of seq = HIGHEST-1]
         [1-byte length of HIGHEST-1]
         [body of seq = HIGHEST-2]
         [1-byte length of HIGHEST-2]
         ...
         [body of seq = LOWEST]
         [1-byte length of LOWEST]                        ← at end of payload
```

The receiver assigns effective seqs as `highestSeq - subCount .. highestSeq`. It iterates LOWEST → HIGHEST while reading the buffer END → BEGIN. So:

- The **lowest-seq body sits at the END** of the payload (with its 1-byte length immediately after).
- The **highest-seq body sits at offset 0** with its length implied by what's left after consuming all the others.

### Length-tail encoding — 1 vs 2 byte

DirtySDK CommUDP supports two sub-packet length encodings, read from the end of the bundle payload backwards:

| Last byte (B0) | Encoding | Resulting size |
|:-:|---|---|
| `0x00`–`0xFA` | 1 byte | `B0` (0–250) |
| `0xFB`–`0xFF` | 2 bytes (B0 + previous byte B1) | `251 + B1 + ((B0 - 0xFB) << 8)` (251–1530) |

Arcadia's outbound bundler emits only the 1-byte form (capped at 250-byte sub-packets), but the inbound splitter handles both — a clean Skate client may send 2-byte-encoded sub-packets for larger payloads (e.g. recipe data chunks).

### Splitter

`CommUdpFrame.TrySplitBundle`:

```csharp
public static bool TrySplitBundle(
    ReadOnlySpan<byte> payload,
    int subCount,
    out List<(int Start, int Length)> subPackets)
{
    subPackets = new List<(int, int)>(subCount + 1);
    int end = payload.Length;
    for (int i = 0; i < subCount; i++)
    {
        if (end < 1) return false;
        end -= 1;
        int last = payload[end];
        int len;
        if (last > 0xFA)
        {
            // 2-byte encoding: size = 251 + prev + ((last - 0xFB) << 8)
            if (end < 1) return false;
            end -= 1;
            int prev = payload[end];
            len = 251 + prev + ((last - 0xFB) << 8);
        }
        else
        {
            len = last;
        }
        int start = end - len;
        if (start < 0) return false;
        subPackets.Add((start, len));
        end = start;
    }
    subPackets.Add((0, end));          // highest-seq body — fills the remainder
    subPackets.Reverse();
    return true;
}
```

The closing `subPackets.Reverse()` flips the end→begin scan, so `subPackets[0]` is the highest-seq body (offset 0) and the last entry is the lowest-seq body. The caller (`LobbyUdpServer.HandleProtoTunnelAsync`) then assigns `effectiveSeq = bareSeq - i` to entry `i`: entry 0 gets `bareSeq` (the highest), entry `i` gets `bareSeq - i`.

### Constraints

- subCount fits in 4 bits → **max 16 sub-packets per frame**.
- Outbound builder uses the 1-byte length tail → **≤ 250 bytes per outbound sub-packet** (`ProtoTunnelCodec.BuildDataBundle`).
- Inbound splitter handles both encodings → **≤ 1530 bytes per inbound sub-packet**.
- Entries must be sorted **strictly ascending by seq** with no duplicates (outbound builder enforces).

### Builder

`ProtoTunnelCodec.BuildDataBundle` is the outbound bundler. Builds the payload back-to-front: starts with the highest-seq body at offset 0 (no length tail), then appends each lower-seq body+length-tail.

## Why bundling matters — the wire-counter trap

ProtoTunnel's decrypt-side rejects packets whose 16-bit counter is < the receiver's tracked expected counter (signed delta < 0 → silent drop, before CommUDP sees it). See `Arc4Stream.TryAdvanceToCounter` → `StaleInEpoch`.

If a server sends N sub-packets as N separate ProtoTunnel frames, that's N separate wire counters. One reordered UDP packet on the path silently kills every preceding sibling — even though CommUDP itself would tolerate the reorder via NACK.

Bundling makes it **ONE counter for all sub-packets** — no reorder risk. The N sub-packets share both the ProtoTunnel counter (for decrypt-stream alignment) and a single CommUDP DATA-frame's sequence space (via the `subCount` field).

This is also why arcadia coalesces multiple Sk8 message bodies into one netGameLink body where possible — same logic at a different layer.

## Sequence space, ACKs, and retransmits

CommUDP is a **client-driven retransmit** protocol: the receiver tells the sender what it's missing via kind=4 PingRetransmitRequest, the sender replays from a cache.

### Sequence numbers

- 24-bit bare seq (`w0 & BareSeqMask`). Monotonically increasing per sender.
- `session.ServerDataSeq` (in `LobbySession`) is arcadia's outbound seq counter, allocated under `SendLock` in `EncryptedSender.SendDataAsync`:

```csharp
serverSeq = session.ServerDataSeq;
session.ServerDataSeq = serverSeq + 1;
```

- For bundled frames, the seq in `w0` is the **highest** seq in the bundle.

### Cumulative ACK

Every outbound DATA frame carries `w1 = session.ClientAckSeq` (set in `EncryptedSender.SendDataAsync`) — arcadia's view of the highest in-order seq it has received from the client. This is piggy-backed; arcadia doesn't need to send dedicated ACK frames in steady state, since every DATA already carries one.

For periods with nothing to send, arcadia emits `EncryptedSender.SendAckOnlyAsync` — a NetGameLink ack-only frame (no app body, just sync trailer + `kindByte = 0x40`) carrying the current ack value. This is what keeps the client's retransmit timer happy.

### The retransmit / keepalive timer (client side)

The Skate client runs a CommUDP retransmit timer. On a connected link, measuring the time since the last packet it received, it re-solicits through the CommUDP send path when:

> more than **~100 ms** have passed **and** it still has unacknowledged outbound data, **or** more than **~2500 ms** have passed, unconditionally.

These are the standard DirtySDK CommUDP keepalive values (`BUSY_KEEPALIVE = 100`, `IDLE_KEEPALIVE = 2500`), the same across every Plasma-era title. The re-solicit is a retransmit, not a specific kind=4 NACK.

The practical rule for arcadia: while a client has data in flight it expects to hear back inside ~100 ms, so arcadia must keep every active client fed with an ack-bearing packet — `SendAckOnlyAsync` exists for exactly that (heartbeat ack frames during quiet periods).

> **Keep an ack-bearing packet flowing to every active client well inside ~100 ms − RTT whenever it may have unacked data, or it starts re-soliciting.**

See [../skate/reliability-and-retransmit.md](../skate/reliability-and-retransmit.md#the-100ms-hard-law) for how arcadia satisfies this hard law.

### kind=4 PingRetransmitRequest

When a client detects a gap (it received seq N+2 but is still waiting on N+1), it emits a CommUDP frame with `kind = 4` and `w1 = expectedSeq` (the seq it's waiting for). The server replays from `session.SentFrameCache[seq]`.

Arcadia also emits its own kind=4 upstream — when it has an open out-of-order gap from a peer, it sends `kind=4` with `w1 = firstMissingSeq` via `EncryptedSender.SendPingRetransmitAsync` to solicit the missing bytes from that peer. See [../skate/reliability-and-retransmit.md](../skate/reliability-and-retransmit.md) for the OOO-gap orchestration around it.

### Outbound redundancy — proactive loss recovery

To reduce kind=4 NACK roundtrips for normal wire loss, arcadia's send path piggy-backs recent unacked frames as bundle sub-packets onto fresh outbound DATA packets. The receiver's CommUDP layer detects bundle entries, ignores duplicates, and accepts any whose seq fills a gap — recovering a lost original without needing to NACK.

This mirrors DirtySDK's `commudp` redundancy mechanism (per-session, controlled by `'rlmt'` selector for budget, file-scope `uMLimit` adaptive sub-packet cap):

- **Per-packet budget**: `RedundancyLimitBytes = 96` (vs DirtySDK default 64) — total bundle body cap. Skate's typical wrapped GameSync is ~46 bytes, so `main + 1 redundancy = 93 bytes` fits in 96 but not 64.
- **Adaptive sub-cap**: `session.RedundancyMLimit` starts at 2 (one redundancy entry max). Doubles up to `MaxRedundancySubs = 15` when budget/cap was hit but more candidates existed (= loss pressure). Resets to 2 when all candidates were consumed (= peer is keeping up).
- **Trigger**: fresh send only (not explicit-seq replays), at least one frame unacked, main body ≤ 250 bytes.

Implementation in `EncryptedSender.SendDataAsync`. See [../skate/reliability-and-retransmit.md](../skate/reliability-and-retransmit.md) for the full mechanism walkthrough and telemetry.

### SentFrameCache

`session.SentFrameCache` is a dictionary `uint seq → byte[] body` that retains every outbound DATA payload for retransmit. Capped at `SentFrameCacheCapacity` (a constant in `LobbySession`); on overflow, the oldest seq is evicted (in `EncryptedSender.SendDataAsync`).

For explicit-seq retransmits (where the client asks for a specific seq), `EncryptedSender.SendDataAsync(explicitSeq: ...)` bypasses the seq-allocation and uses the cached body.

## CommUDP-level state at the LobbySession

| Field | Purpose |
|---|---|
| `ServerDataSeq` | next outbound seq to allocate |
| `ClientAckSeq` | last in-order seq received from client (piggy-back ack value) |
| `SentFrameCache` | seq → cached body for retransmit |
| `RedundancyMLimit` | adaptive sub-packet cap for outbound redundancy |
| `RedundancySubsSent` | telemetry counter for redundancy piggy-backs |
| `RecvStream` / `SendStream` | per-direction Arc4Stream (ProtoTunnel side, not strictly CommUDP) |
| `LastOutboundAt` | clock for heartbeat-ack scheduling |

## See also

- [prototunnel.md](prototunnel.md) — the encryption envelope sitting outside CommUDP.
- [netgamelink.md](netgamelink.md) — what's inside the CommUDP DATA payload (each sub-packet).
- [../skate/reliability-and-retransmit.md](../skate/reliability-and-retransmit.md) — Skate-specific retransmit and NACK orchestration.
