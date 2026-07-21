# Wire stack overview

EA's Plasma-era online stack is a layered protocol where each layer is a DirtySDK module with a stable wire contract. Skate 1 and Skate 2 use the same stack as every other Plasma-era title (Battlefield Bad Company 2, NFS Shift, Mercenaries 2, MOH Airborne, etc.) — what differs is only the **app-layer** at the very top.

If you're reading this to build a server for a different DirtySDK title, the lower layers (everything below `sysFlag`) are bit-for-bit reusable.

## The full stack

```
┌──────────────────────────────────────────────────────────────────────┐
│ TCP+SSL ─ Plasma (Fesl) + Theater                                    │  identity, auth, matchmaking,
│   - Fesl  port 18231 (Skate 1) / 18420 (Skate 2)                     │  game listing — HANDS OFF to UDP
│   - Theater port 18236 (Skate 1) / 18126 (Skate 2)                   │  after Theater EGEG announces
│                                                                       │  the UDP endpoint
├──────────────────────────────────────────────────────────────────────┤
│ UDP datagram                                                         │
│ └─ ProtoTunnel envelope                                              │  encryption + 16-channel mux
│    - 2-byte wire counter                                              │  (RC4-encrypted preamble +
│    - N × 2-byte tuple_header  = (length<<4)|tunnelIdx                │   variant-specific body)
│    - encrypted payloads, then clear payloads                          │
│    │                                                                  │
│    └─ CommUDP frame  (on the CommUDP tunnel idx, S1: 1 or 2, S2: 1)  │  reliable UDP — Kinds, seq/ack,
│       - 4-byte w0 = subCount<<28 | bareSeq                            │   bundled sub-packets, NACK
│       - 4-byte w1 = ack value                                         │
│       - payload (one or more sub-packets, bundled)                    │
│       │                                                               │
│       └─ NetGameLink body  (= each sub-packet)                       │  game framing
│          - 1-byte sysFlag    (0x00 = App, 0x01 = System)              │
│          - app body                                                   │
│          - 10-byte sync trailer (peer-echo, local-tick, jitter)       │
│          - 1-byte kind  (0x46 = body+sync, 0x40 = ack-only)           │
│          │                                                            │
│          ├─ sysFlag = 0x01 ─→ GameManager opcode  ────── COMMON ─────│  HELLO, HOST_HELLO,
│          │                                                            │  ROSTER_ELEM, ROSTER_ACK,
│          │                                                            │  PLAYER_JOIN_COMPLETE, etc.
│          │                                                            │  Wire byte = opcode + 0x80.
│          │                                                            │
│          └─ sysFlag = 0x00 ─→ Sk8::Net::Message ─── TITLE-SPECIFIC ─│  MT_GameSync, MT_GameReset,
│                                                                       │  MT_GameRecipe*, etc. Each
│                                                                       │  title defines its own table.
└──────────────────────────────────────────────────────────────────────┘
```

## Where common ends and title-specific begins

The exact split is at **one byte**: the first byte of every NetGameLink body, `sysFlag`.

Defined in `src/server/Hosting/Lobby/Wire/NetGameLink.cs`:

```csharp
public const byte SysFlagApp    = 0x00;
public const byte SysFlagSystem = 0x01;
```

- `SysFlagSystem` (0x01) → body[0] is a **GameManager opcode** — these are the DirtySDK common opcodes (HELLO/HOST_HELLO/ROSTER_ELEM/etc.) and identical across every Plasma title.
- `SysFlagApp` (0x00) → body[0] is a **title-specific message type** — for Skate this is the Sk8::Net::Message factory table (the `Sk8MessageType` enum in `Hosting/Lobby/Protocol/WireEnums.cs`).

The inbound walker enforces this split in `AppLayerWalker.Parse` — System frames detour to `SystemFrameDispatcher.TryHandle` first; everything else must be App or it's dropped.

## What's reusable for other titles

The following layers are **bit-for-bit identical** across every DirtySDK title:

| Layer | Doc | Title-agnostic? |
|---|---|---|
| Fesl (Plasma) frame format | — | Yes (handlers within differ per-title) |
| Theater frame format | — | Yes (handlers within differ per-title) |
| ProtoTunnel envelope | [prototunnel.md](prototunnel.md) | Yes (encrypted-tunnel set may vary per title/patch) |
| Arc4 / RC4 cipher | [prototunnel.md](prototunnel.md) | Yes |
| CommUDP frame | [commudp.md](commudp.md) | Yes |
| Bundled sub-packet layout | [commudp.md](commudp.md) | Yes |
| NetGameLink envelope (sysFlag/sync trailer/kind byte) | [netgamelink.md](netgamelink.md) | Yes |
| GameManager handshake (HELLO→...→JOIN_COMPLETE) | [gamemanager-handshake.md](gamemanager-handshake.md) | Yes |
| GameManager opcode bias (+0x80) | [gamemanager-handshake.md](gamemanager-handshake.md) | Yes |
| **App layer (the sysFlag=0x00 body)** | [../skate/app-layer.md](../skate/app-layer.md) | **No — title-specific** |

## Porting to another title

Everything except the App layer is DirtySDK and transfers as-is. To point this server at another Plasma-era title:

1. **Reuse the whole transport stack** — `Wire/`, `Send/`, and most of `Handshake/`. ProtoTunnel, CommUDP, NetGameLink, and the GameManager handshake are bit-for-bit identical across titles.
2. **Rewrite the App layer** — the `sysFlag=0x00` message table. Replace everything named `Sk8*` under `Protocol/` and the per-opcode handlers in `Walker/Handlers/`. [../skate/app-layer.md](../skate/app-layer.md) is the worked example.
3. **Swap the title identity** — FESL/Theater ports + hostname (`Ports.cs`, `AppSettings.cs`, `DnsHostedService.cs`), the ProtoTunnel EKey (title-specific; the default `"RELAYKEY"` won't work against a retail binary), and the FESL `hello` client/SKU/version strings (`FeslHandler`).
4. **Simplify the orchestration if the game isn't lockstep** — Skate's `Reset/`, `Challenge/`, and `Recipe/` flows exist because it's a lockstep game. A server-authoritative BF/NFS-style title keeps the relay loop and drops most of that.

**Finding a title's differences:** open the binary in IDA/Ghidra, find its `<Title>::Net::Message::Create` factory table (that's the App-layer opcode map), and trace one inbound packet — decrypt → CommUDP DATA → NetGameLink → branch on `sysFlag` → dispatch on `body[0]`. Most bytes line up with Skate; the deltas are the opcode table and a few game-specific fields. [Upstream arcadia](https://github.com/valters-tomsons/arcadia) has FESL/Theater handler patterns for several other titles.

## Inbound pipeline

A single UDP datagram is processed top-down in `LobbyUdpServer.HandleDatagramAsync`:

```csharp
private async Task HandleDatagramAsync(UdpReceiveResult result, CancellationToken ct)
{
    byte[] buf = result.Buffer;
    IPEndPoint ep = result.RemoteEndPoint;

    if (CommUdpFrame.LooksLikePlainControl(buf))
    {
        await HandlePlainControlAsync(ep, buf, ct);   // plain CONN/POKE/DISC before encryption
        return;
    }

    if (buf.Length >= ProtoTunnelCodec.CounterBytes + ProtoTunnelCodec.TupleHeaderBytes)
    {
        await HandleProtoTunnelAsync(ep, buf, ct);    // every encrypted datagram
        return;
    }
    ...
}
```

`LobbyUdpServer.HandleProtoTunnelAsync`:

1. Decrypts via `ProtoTunnelCodec.TryDecode` (advances the persistent RC4 stream).
2. Locates the first CommUDP tuple.
3. For each CommUDP tuple, reads the 8-byte header `w0|w1` and routes by Kind (Connect/ConnAck/Disconnect/PingRetransmitRequest/Data).
4. On DATA, optionally splits a bundled multi-sub-packet payload (`CommUdpFrame.TrySplitBundle`), then for each sub-packet:
5. `AppLayerWalker.Parse` reads the NetGameLink envelope, branches on `sysFlag`, and either dispatches a System frame or walks one or more Sk8 messages.

Each numbered step has a dedicated doc; the methods named above tell you where each layer's code lives.

## Outbound pipeline

Mirror of the inbound. `EncryptedSender.SendDataAsync` is the single point of truth for outbound ProtoTunnel-wrapped DATA frames. The build sequence:

1. Caller hands a NetGameLink body (sysFlag + body + sync trailer + kindByte) via `SendSk8BodyAsync` / `SendSystemBodyAsync` / `SendAckOnlyAsync`.
2. Under `session.SendLock`, allocate `serverSeq`, cache the frame for retransmit, ask `ProtoTunnelCodec.BuildData` (or `BuildDataBundle` for multi-sub-packet bundling) to wrap it into a ProtoTunnel frame, advance the RC4 send stream.
3. Send the bytes via the underlying `UdpClient` (outside the lock).
