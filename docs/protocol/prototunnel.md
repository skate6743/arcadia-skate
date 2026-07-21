# ProtoTunnel

ProtoTunnel is DirtySDK's encrypted tunnel layer. It sits directly on top of UDP and provides:

- **A 16-channel multiplexer** — multiple logical streams can ride one UDP socket; each is tagged by a 4-bit `tunnelIdx`.
- **An RC4-based encryption + authentication envelope** — each packet carries a 2-byte wire counter that drives a persistent RC4 stream. Wrong-key / out-of-order / replayed packets fail to decrypt to a valid tuple-header structure.
- **A 6-byte preamble** — a 5-byte session signature plus a 1-byte per-direction nonce. Encrypted in both Skate 1 and Skate 2.

Source: `src/server/Hosting/Lobby/Wire/ProtoTunnelCodec.cs`, `Arc4.cs`, `Arc4Stream.cs`.

## On-wire format

```
+0  [2 BE] wireCounter
+2  [2 BE] tuple_header_0   = (length << 4) | tunnelIdx       12-bit length, 4-bit idx
+4  [2 BE] tuple_header_1
... (one tuple_header per tunnel multiplexed into this packet)
... [N bytes] encrypted payloads (concatenated, in tuple-header order)
... [M bytes] clear payloads     (concatenated, after the encrypted block)
```

`tuple_header` encoding (decoded in `ProtoTunnelCodec.TryDecode`):

```csharp
ushort hdr = BinaryPrimitives.ReadUInt16BigEndian(work.AsSpan(hdrOff, TupleHeaderBytes));
int length = hdr >> 4;
int idx    = hdr & 0xF;
```

Constants (`ProtoTunnelCodec`):

```csharp
public const int CounterBytes       = 2;
public const int TupleHeaderBytes   = 2;
public const int PreambleTunnelIdx  = 7;
public const int PreambleLength     = 6;
public const int CommUdpTunnelIdx   = 1;
public const int Skate1AltCommUdpTunnelIdx = 2;
```

A typical Skate packet contains two tuples: idx 7 (preamble, always 6 bytes) and idx 1 (the CommUDP payload).

## Tunnel index assignments

The 4-bit `tunnelIdx` allows 16 channels per packet. EA assigned them as follows:

| Idx | Purpose | Skate 1 | Skate 2 |
|----:|---|:-:|:-:|
| 1 | Primary CommUDP (game traffic) | yes | yes |
| 2 | Alt CommUDP (rarely seen) | yes | no |
| 7 | Preamble (session envelope) | yes | yes |
| other | Unused for Skate | — | — |

`ProtoTunnelCodec.IsCommUdpTunnel`:

```csharp
public static bool IsCommUdpTunnel(GameVariant variant, int idx)
    => variant == GameVariant.Skate1
        ? (idx == CommUdpTunnelIdx || idx == Skate1AltCommUdpTunnelIdx)
        : (idx == CommUdpTunnelIdx);
```

The 6-byte preamble (idx 7) is **not** a CommUDP carrier; it's a session signature. The first 5 bytes are echoed from the client's initial CONNECT packet (`session.PreambleBytes`), and the 6th byte is a per-direction monotonic nonce (`session.ServerPreambleByte5`, incremented on every outbound by each `EncryptedSender` build path).

## Encryption

ProtoTunnel uses **RC4** (DirtySDK's `CryptArc4`) as the cipher. The implementation is `Wire/Arc4.cs` and the persistent-stream wrapper used per session is `Wire/Arc4Stream.cs`.

### Which bytes are encrypted

`ProtoTunnelCodec.IsEncryptedTunnel`:

```csharp
public static bool IsEncryptedTunnel(GameVariant variant, int idx)
    => variant == GameVariant.Skate1
        ? (idx == 1 || idx == 2 || idx == 7)
        : (idx == PreambleTunnelIdx);
```

| Variant | Encrypted tunnels |
|---|---|
| Skate 1 | **idx 1, 2, 7** — preamble *and* both CommUDP carriers (entire payload under RC4) |
| Skate 2 | **idx 7 only** — preamble only; CommUDP body is plaintext on the wire |

This is a meaningful design difference. Skate 1 RC4s every byte of every netGameLink body it sends; Skate 2 only RC4s the 10-byte preamble window (2 tuple headers + 6 preamble) and leaves the body in clear. See the `applyLen` computation in `ProtoTunnelCodec.BuildData`:

```csharp
int applyLen = variant == GameVariant.Skate1
    ? commudpPayloadLen + 2 * TupleHeaderBytes + PreambleLength
    : 10;
```

Skate 2's patched build dropped to idx-7-only as an optimization; an unpatched 1.00 Skate 2 build encrypted idx 1/2 like Skate 1. Arcadia matches the patched build.

The "encryption" mostly serves as a cheap authentication check: only a peer holding the correct `EKey` and at the right stream position can produce bytes that decrypt to a valid preamble + valid tuple-headers whose lengths sum to the wire length. The body content isn't sensitive.

### Arc4 (`Wire/Arc4.cs`)

Standard RC4. Constructed from a key via `Arc4.Init`:

```csharp
public void Init(ReadOnlySpan<byte> key, int iSchedule)
{
    for (int i = 0; i < 256; i++) _state[i] = (byte)i;
    _x = 0; _y = 0;

    int passes = iSchedule < 1 ? 1 : iSchedule;
    for (int pass = 0; pass < passes; pass++)
    {
        int j = 0;
        for (int i = 0; i < 256; i++)
        {
            j = (j + _state[i] + key[i % key.Length]) & 0xFF;
            byte tmp = _state[i];
            _state[i] = _state[j];
            _state[j] = tmp;
        }
    }
}
```

`iSchedule = 1` is what DirtySDK passes; this is standard single-pass KSA. `Arc4.Advance` consumes keystream without xor'ing; `Arc4.Apply` consumes keystream and xors into a buffer.

`Arc4.CopyStateTo` / `RestoreStateFrom` — atomic snapshot of the entire 256-byte permutation + (x, y). Used by `Arc4Stream.TakeSnapshot` / `RestoreSnapshot` to roll back if a partial decrypt fails mid-frame.

### Arc4Stream (`Wire/Arc4Stream.cs`)

A persistent RC4 stream that tracks position by the 16-bit wire counter and handles wraparound:

```csharp
public ushort LastWireCounter;
public uint   HighWord;
public uint   EffectiveCounter => ((uint)HighWord << 16) | LastWireCounter;
```

The "effective counter" is a virtual 32-bit position: low 16 bits from the wire, high 16 bits incremented every time the wire counter wraps from ~0xFFFF back to ~0x0000.

`TryAdvanceToCounter` is the core function — given a newly-received wire counter, decide how to move the RC4 state:

```csharp
public AdvanceResult TryAdvanceToCounter(ushort newCounter)
{
    int delta = (int)newCounter - (int)LastWireCounter;
    if (delta == 0)
        return AdvanceResult.Forward;

    if (delta > 0)
    {
        if (delta > WrapThreshold && HighWord > 0)
        {
            StaleAcrossWrapPackets++;
            return AdvanceResult.StaleAcrossWrap;
        }
        // Forward skip across one or more missed packets — capture pre-skip state
        // into OOP for potential later recovery of the missed seqs.
        if (delta > 1)
            SaveOopFromCurrentPrimary();
        _rc4.Advance(4 * delta);
        LastWireCounter = newCounter;
        return AdvanceResult.Forward;
    }

    // delta < 0
    if (delta > -WrapThreshold)
    {
        StaleInEpochPackets++;
        return AdvanceResult.StaleInEpoch;
    }

    // Forward across a wrap — also save OOP first, same reasoning.
    SaveOopFromCurrentPrimary();
    int advanceAmount = delta + 0x10000;
    _rc4.Advance(4 * advanceAmount);
    HighWord++;
    LastWireCounter = newCounter;
    return AdvanceResult.ForwardWrap;
}
```

Where `WrapThreshold = 32768` — half the 16-bit counter range. The "is this stale or did it wrap" decision is made by signed-delta comparison.

**Why `4 * delta` not `delta`**: the client RC4-advances by 4 bytes per wire-counter increment in steady state, because every CommUDP DATA payload it sends is at least 8 bytes (`HeaderBytes = 8`) and the counter increments by `bytesApplied >> 2` rounded up to the next slot of 4. See `Realign` below.

#### `Realign` — the per-packet position fix-up

After `Apply`-ing N bytes during decrypt, `Arc4Stream.Realign` advances the wire counter and possibly the high word:

```csharp
public void Realign(int bytesApplied)
{
    int slots = bytesApplied >> 2;
    int remainder = bytesApplied & 3;
    if (remainder != 0)
    {
        _rc4.Advance(4 - remainder);   // round up to next 4-byte slot
        slots++;
    }
    int newCounter = LastWireCounter + slots;
    if (newCounter > 0xFFFF) HighWord++;
    LastWireCounter = (ushort)newCounter;
}
```

This matches DirtySDK's contract that each wire-counter unit corresponds to exactly 4 bytes of RC4 keystream.

#### Snapshot / restore for atomic decode

`TryDecode` calls `TakeSnapshot` before any `Apply`. If the frame is malformed (e.g. tuple length runs past the end of the buffer), `RestoreSnapshot` rolls the RC4 state and counter back to before the packet, so the next packet still decrypts correctly. This is the documented stack-buffer rollback every DirtySDK ProtoTunnel decoder performs internally.

#### OOP state — secondary RC4 for wire-reordered packets

RC4 is a stream cipher: each keystream byte at position N is unique, and the state can only move forward. When a wire packet arrives with a counter behind the current primary state, primary can't decrypt it — its position has advanced past where that packet was encrypted.

To recover such packets without a CommUDP-level NACK roundtrip, `Arc4Stream` maintains a **secondary RC4 instance** (the "OOP" or out-of-order packet state) plus its own wire counter pair (`_oopWireCounter`, `_oopHighWord`). Whenever primary has to skip forward by more than one packet (= a missed packet or wire reorder), `SaveOopFromCurrentPrimary` copies primary's pre-skip RC4 state and counter into OOP first. OOP then lags behind primary, covering the range that was just skipped over.

Methods:

| Method | Purpose |
|---|---|
| `SaveOopFromCurrentPrimary` (private) | Capture primary's current state into OOP. Called from `TryAdvanceToCounter` when delta > 1 or on forward wrap. |
| `TryAdvanceOopToCounter(newCounter)` | Advance OOP forward to the target counter for a backwards-counter packet retry. Returns `false` if OOP isn't initialized, isn't in the same epoch as primary, delta is backwards, or would catch/pass primary. |
| `ApplyOop(span)` | RC4-decrypt bytes via OOP state (parallel to `Apply` for primary). |
| `RealignOop(bytesApplied)` | Advance OOP counter after decrypting a packet, mirroring `Realign`. |

`OopRecoveredPackets` counts every packet successfully recovered through OOP — exposed via `oopRec={N}` in TALLY log lines.

This mirrors DirtySDK ProtoTunnel's documented `CryptRecvOOPState` mechanism: same idea (lagging secondary RC4), same save trigger (only on primary-must-sync), same single-retry semantics. Each new save overwrites the previous OOP snapshot — OOP only covers the most recent skip range.

OOP recovery is a pure receive-side optimization. Nothing about what the server sends or what clients expect on the wire changes.

## Decode pipeline

`ProtoTunnelCodec.TryDecode` — the inbound entry point:

1. Read 2-byte wire counter.
2. `TakeSnapshot()`.
3. `TryAdvanceToCounter(counter)`. If primary returns:
   - `Forward` / `ForwardWrap` — proceed with normal decrypt via primary.
   - `StaleInEpoch` — try `TryAdvanceOopToCounter`. If OOP covers this counter, decrypt via the parallel `TryDecodeViaOop` path (uses `ApplyOop` / `RealignOop`). On success, increment `OopRecoveredPackets` and return. Otherwise drop.
   - `StaleAcrossWrap` — fall back to `TryDecodeFresh` (one-shot decrypt on a fresh RC4 keyed instance positioned at the inferred effective counter).
4. Walk tuple headers. For each tuple header (every 2 bytes after the counter), `Apply` 2 bytes to decrypt → read `length` and `idx`. Track total bytes applied for the later `Realign`.
5. After all tuple-headers, compute total encrypted-tunnel byte count → `Apply` that many bytes for the encrypted payloads.
6. `Realign(bytesApplied)`.
7. Build offsets into the decrypted `work` buffer for each tuple (preamble first, then payloads in order).

The "tuple sum mismatch" case — where the per-tuple length sum doesn't equal the remaining wire bytes — indicates a secondary tunnel; `TryDecode` flags it (`TupleSumMismatch`) and `LobbyUdpServer.HandleProtoTunnelAsync` drops the packet.

## Build pipeline

Four builders for the four shapes arcadia emits (all in `ProtoTunnelCodec.cs`):

| Builder | Used for | Carries |
|---|---|---|
| `BuildConnAck` | CommUDP CONNACK | 8-byte CommUDP payload (kind=2, ident) — `PlainAckOnlyLength` |
| `BuildPingRetransmit` | CommUDP kind=4 NACK upstream | 8-byte CommUDP payload (kind=4, expectedSeq) — `PlainAckOnlyLength` |
| `BuildData` | Single-sub-packet DATA | 8-byte CommUDP header + netGameLink body |
| `BuildDataBundle` | Multi-sub-packet bundled DATA | 8-byte header + concatenated sub-packets + 1-byte length tails |

Each builds the wire bytes plaintext, then `Apply`s the RC4 advance over the appropriate region (see `applyLen` discussion above), then `Realign`s. The output of each is an `OutboundFrame(packet, startCounter, nextCounter, wrapped)`.

## Things to know if you're porting this to another title

- The encrypted-tunnel set may differ. Each EA title (and sometimes each patch) decides which tunnel indices are RC4'd. If you don't know, find the inbound ProtoTunnel decrypt entry in the client binary and check which tuple indices it calls into the body-decrypt loop for vs. which it skips.
- The EKey may differ per title. Arcadia defaults to `"RELAYKEY"` (`LobbySettings.EKey`) — the actual EKey used by retail clients is something else, baked into the binary or negotiated. Check.
- The preamble length (6 bytes) and per-packet nonce semantics appear consistent across DirtySDK. The 5-byte session token comes from CONN; byte 5 is a counter.
- The 2-byte wire counter, 4-bytes-per-slot RC4 advance, and 32768 wrap threshold are DirtySDK invariants — same in every title.

## See also

- [stack-overview.md](stack-overview.md) — where ProtoTunnel sits in the stack.
- [commudp.md](commudp.md) — what's inside the `idx=1` payload.
