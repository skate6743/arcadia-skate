# Skate-specific documentation

This folder covers **Skate's title-specific layer** — both its wire messages ([app-layer.md](app-layer.md)) and how arcadia orchestrates them on top of the generic DirtySDK wire stack. For the generic stack underneath, see [../protocol/](../protocol/) first.

## What's in here

| Doc | Purpose |
|---|---|
| [app-layer.md](app-layer.md) | Skate's message table — the `sysFlag=0x00` layer (MT_GameSync, MT_GameReset, MT_GameRequest, MT_GameRecipe*, MT_GameAttributes, etc.). The wire format every doc below references. |
| [lockstep-and-relay.md](lockstep-and-relay.md) | How Skate's lockstep simulation works and how arcadia relays GameSync frames between peers without wedging. Covers the FirstFrame barrier and the loading-gate filter. |
| [reset-and-challenge-flow.md](reset-and-challenge-flow.md) | When and how `MT_GameReset` gets broadcast: free-skate entry, the two-phase challenge dance, map-change and post-challenge flows. |
| [recipe-pipeline.md](recipe-pipeline.md) | Skate's character-data blob system: client upload, per-peer cache, on-demand serve via `MT_GameRecipeHead` + `MT_GameRecipeData` chunks. |
| [s1-vs-s2-differences.md](s1-vs-s2-differences.md) | Reference table of every behavioral split between Skate 1 and Skate 2 in this codebase. Useful when porting a feature between variants. |
| [reliability-and-retransmit.md](reliability-and-retransmit.md) | How arcadia keeps CommUDP's ~100ms retransmit timer happy: SentFrameCache + replay, outbound redundancy piggy-back, proactive control-frame retransmit, kind=4 PingRetransmit handling, server-side NACK upstream, OOP-state recovery for wire-reordered packets. |

## Suggested reading order

Read [../protocol/stack-overview.md](../protocol/stack-overview.md) first if you haven't — every doc here assumes you know what `sysFlag` is and where a `MT_GameSync` lives in the stack.

Then:

1. **[app-layer.md](app-layer.md)** — the Skate message table. Learn the vocabulary (MT_GameSync, MT_GameReset, recipes, attributes) before the docs that orchestrate them.
2. **[lockstep-and-relay.md](lockstep-and-relay.md)** — the core mental model. The FirstFrame barrier is the single trickiest piece of arcadia and showing up to other docs without understanding it will be confusing.
3. **[reset-and-challenge-flow.md](reset-and-challenge-flow.md)** — when the barrier gets armed and what triggers it.
4. **[reliability-and-retransmit.md](reliability-and-retransmit.md)** — the CommUDP-level machinery that keeps everything fed.
5. **[recipe-pipeline.md](recipe-pipeline.md)** — character data, only really relevant to Skate-specific lobby setup.
6. **[s1-vs-s2-differences.md](s1-vs-s2-differences.md)** — keep as a reference; you'll come back to it whenever you cross-check a behavior between variants.

## What "Skate-specific" means here

These docs cover behavior that's **specific to Skate's gameplay model** (lockstep, peer-to-peer recipe sharing, multi-phase challenge transitions). If you're porting this server to another DirtySDK title:

- A non-lockstep title (BF, NFS, most server-authoritative shooters) would not need most of [lockstep-and-relay.md](lockstep-and-relay.md) — the FirstFrame barrier exists because of lockstep.
- Most titles do not have a recipe pipeline; [recipe-pipeline.md](recipe-pipeline.md) is Skate's solution to a problem that may not exist for your title.
- The reset/challenge orchestration model is Skate-specific; another game's phase machine would need a parallel design.
- **The reliability and retransmit material is mostly portable** — DirtySDK CommUDP works the same way in every title, just the cadence requirements may differ.

See [../protocol/stack-overview.md](../protocol/stack-overview.md#porting-to-another-title) for what's reusable vs needs rewriting.

## Where the claims come from

Behavioral claims about the Skate client (what triggers a desync, why a barrier matters, what the client does on a given message) are grounded in observation of the actual game running against this server and in cross-referenced behavior between Skate 1 and Skate 2. The file/method citations are all reproducible from this repo alone — every wire format and every server-side decision is in the C# source. The client-side claims are accurate-as-of-the-revisions-we-tested-against; if you observe different behavior on a different patch, that's worth raising.
