# Documentation

Wire-format references and design docs for the dedicated Skate 1 / Skate 2 lobby server.

## Folder layout

```
docs/
├── README.md                          ← you are here
├── protocol/                          ← the reusable DirtySDK stack (transfers to any Plasma-era title)
│   ├── stack-overview.md
│   ├── prototunnel.md
│   ├── commudp.md
│   ├── netgamelink.md
│   └── gamemanager-handshake.md
└── skate/                             ← everything Skate-specific
    ├── README.md
    ├── app-layer.md
    ├── lockstep-and-relay.md
    ├── reset-and-challenge-flow.md
    ├── recipe-pipeline.md
    ├── s1-vs-s2-differences.md
    └── reliability-and-retransmit.md
```

## Reading order

**The reusable DirtySDK stack** — the same in every Plasma-era title:

1. **[protocol/stack-overview.md](protocol/stack-overview.md)** — the full wire stack diagram, and where every other doc fits. Ends with a short guide to porting this server to another title.
2. **[protocol/prototunnel.md](protocol/prototunnel.md)** — the encrypted outer envelope every packet rides in.
3. **[protocol/commudp.md](protocol/commudp.md)** — the reliable-UDP transport layer inside ProtoTunnel.
4. **[protocol/netgamelink.md](protocol/netgamelink.md)** — the per-message envelope inside CommUDP. This is where common (GameManager) and title-specific (Skate) split, via the `sysFlag` byte.
5. **[protocol/gamemanager-handshake.md](protocol/gamemanager-handshake.md)** — the common DirtySDK opcodes (HELLO / HOST_HELLO / ROSTER_ELEM / ROSTER_ACK / PLAYER_JOIN_COMPLETE / etc.). Identical across every Plasma-era title.

**Everything Skate-specific** — the app-layer messages, then the server orchestration on top of the wire layer:

6. **[skate/app-layer.md](skate/app-layer.md)** — Skate's title-specific message table (MT_GameSync, MT_GameReset, MT_GameRequest, MT_GameRecipe*, MT_GameAttributes, etc.). The `sysFlag=0x00` layer.
7. **[skate/lockstep-and-relay.md](skate/lockstep-and-relay.md)** — how arcadia coordinates Skate's lockstep simulation across peers.
8. **[skate/reset-and-challenge-flow.md](skate/reset-and-challenge-flow.md)** — phase transitions, challenge Phase 1 / Phase 2 dance.
9. **[skate/recipe-pipeline.md](skate/recipe-pipeline.md)** — Skate's character-data blob system (both variants).
10. **[skate/s1-vs-s2-differences.md](skate/s1-vs-s2-differences.md)** — variant-specific behavior summary.
11. **[skate/reliability-and-retransmit.md](skate/reliability-and-retransmit.md)** — how arcadia keeps the CommUDP retransmit timer happy.

## How docs are written

Every doc cites the **file and method (or symbol)** in `src/server/` where a behavior lives — e.g. `EncryptedSender.SendDataAsync`, `WireEnums.cs` (Sk8MessageType). No line numbers: they drift, and a method or symbol name is something you can `grep` to find the current implementation. Wire formats are pulled directly from the constants and builder code, not from external notes or memory.

Where docs describe client-side behavior (constants enforced inside the game's network code, decision logic in the client's CommUDP / ProtoTunnel modules, etc.), those claims describe only the *behavior*, not specific function offsets — an offset is only meaningful if you have the same disassembly loaded yourself. Treat client-behavior claims as accurate-as-of-the-revisions-we-tested-against, not authoritative for every retail revision.

## See also

- The top-level [README.md](../README.md) for setup / configuration / supported games.
