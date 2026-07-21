# Recipe pipeline

A **recipe** is Skate's character/skater data: appearance customization, gear, stats. It's a binary blob. On Skate 1 the packed Create-A-Character record is small — the client's upload buffer is 8272 bytes; Skate 2's blob size isn't measured here, so treat any specific KB figure as unverified. Every peer needs every other peer's recipe for the lobby to reach steady state.

**Both games run the same recipe pipeline** — `MT_GameRecipeRequest` / `MT_GameRecipeHead` / `MT_GameRecipeData` — only the opcode IDs differ (Skate 1 = 20/21/22, Skate 2 = 23/24/25). Skate 1 peers see each other's real skaters; `Head(size=0)` is only the genuine cache-miss fallback, not a Skate 1 policy.

This doc walks through how arcadia ingests recipe blobs from Plasma, caches them per-peer, and serves them to other peers on demand.

Source:
- `src/server/Hosting/Lobby/Recipe/RecipeService.cs` — the Head + N × Data emitter.
- `src/server/Hosting/Lobby/Recipe/RecipeAssembler.cs` — assembling incoming chunks back into a blob.
- `src/server/Hosting/Lobby/Walker/Handlers/RecipeHandlers.cs` — walker entry for incoming recipe messages.
- `src/server/Hosting/Lobby/Protocol/RecipePackets.cs` — wire builders (see [app-layer.md](app-layer.md)).
- `src/server/Storage/RecipeBlobStore.cs` — per-peer blob cache.

## Two paths: Plasma upload vs UDP peer-relay

Recipes reach arcadia by **two channels**:

1. **Plasma blob upload (TCP)** — when a peer connects to Fesl, it can upload its recipe via Plasma's `AddBlob` endpoint. Arcadia's `FeslHandler.HandleAddBlob` extracts the blob bytes and stores them in `RecipeBlobStore` keyed by `(uid, contentType)`, where `contentType` is the **wire** value from the `AddBlob` packet's `type` field: `RecipeBlobStore.CT_Recipe = 16`, `CT_Thumb = 17`. The client's internal enum is `CT_Recipe = 5` / `CT_Thumb = 6`, which it maps to the wire values `16`/`17` before upload; arcadia keys the store on the wire value.
2. **UDP peer-relay (CommUDP)** — when peer B requests peer A's recipe via `MT_GameRecipeRequest`, arcadia responds with `MT_GameRecipeHead` + N × `MT_GameRecipeData` chunks from the cached blob.

In practice, every peer's recipe should already be in the cache (uploaded at Fesl handshake) before any peer requests it.

## Recipe pipeline — the request → serve loop

The flow when peer B wants peer A's recipe:

```
peer B's client                       arcadia                       (peer A — passive)
        |                                |
        |  MT_GameRecipeRequest(peer=A's UID)  (sysFlag=App)
        |       ────────────────→        |
        |                                |
        |                                | look up Blobs[A.UID, CT_Recipe]
        |                                |
        |                                | if cached:
        |        ←──────  MT_GameRecipeHead(peer=A, size=N, crc)
        |        ←──────  MT_GameRecipeData(peer=A, idx=0, 1024 bytes)
        |        ←──────  MT_GameRecipeData(peer=A, idx=1, 1024 bytes)
        |              ...
        |        ←──────  MT_GameRecipeData(peer=A, idx=K, remainder)
        |                                |
        |                                | else (no blob):
        |        ←──────  MT_GameRecipeHead(peer=A, size=0, crc=0)
        |                                |
        |                                  → peer B falls back to default recipe
```

## `RecipeService.RespondAsync`

```csharp
public static async Task RespondAsync(LobbyUdpServer server, IPEndPoint ep, LobbySession session, long peerId, CancellationToken ct)
{
    var entry = server.Blobs.GetEntry(peerId, Storage.RecipeBlobStore.CT_Recipe);
    if (entry is null || entry.Value.Data.Length == 0)
    {
        // Fallback path — peer's blob isn't cached.
        // Emit Head(size=0) → receiver loads default recipe.
        byte[] fallback = RecipePackets.BuildHead(server.Variant, peerId, 0, 0);
        await EncryptedSender.SendSk8BodyAsync(server, ep, session, fallback,
            $"MT_GameRecipeHead(peer={peerId},size=0,fallback)", ct);
        return;
    }

    // Cached blob present — send Head + chunks.
    byte[] blob = entry.Value.Data;
    uint crc = entry.Value.Crc;
    byte[] head = RecipePackets.BuildHead(server.Variant, peerId, blob.Length, crc);
    await EncryptedSender.SendSk8BodyAsync(server, ep, session, head, ...);

    int offset = 0;
    int idx = 0;
    while (offset < blob.Length)
    {
        int take = Math.Min(RecipeChunkSize, blob.Length - offset);
        byte[] chunk = blob.AsSpan(offset, take).ToArray();
        byte[] data = RecipePackets.BuildData(server.Variant, peerId, idx, chunk);
        await EncryptedSender.SendSk8BodyAsync(server, ep, session, data, ...);
        offset += take;
        idx++;
    }
}
```

`RecipeChunkSize = 1024` — chunk size cap. Why 1024?

- ProtoTunnel envelope: ~12 bytes (counter + 2 tuple headers + preamble).
- CommUDP header: 8 bytes.
- NetGameLink envelope: 12 bytes (sysFlag + body + sync trailer + kindByte).
- MT_GameRecipeData header: 17 bytes (opcode + peerId + chunkLen + chunkIndex).
- Total non-payload: ~49 bytes.
- 1024 payload + 49 overhead = 1073 bytes. Under the typical 1272-byte CommUDP recv-element ceiling and under standard MTU (1500).

## Inbound: the Plasma upload

When a peer uploads its recipe via Fesl's blob endpoint:

1. `FeslHandler.HandleAddBlob` receives the `AddBlob` request; `ParseContentType` reads the `type` field, `ExtractBlobBytes` pulls the payload.
2. `ExtractBlobBytes` handles the known layouts — a chunked `data.[]=N` array, or a single `data`/`content` field — and `DecodeBlobField` percent-decodes (Plasma encodes `=` as `%3d`) then base64-decodes.
3. `_blobs.Put(uid, contentType, blob)` stashes the raw bytes in `RecipeBlobStore`.

Arcadia does **not** compute a CRC. `HandleAddBlob` calls `Put` with no CRC argument, so AddBlob-uploaded blobs are stored with `crc = 0`; the `MT_GameRecipeHead.crc` arcadia later serves is simply whatever the store holds (`0` for AddBlob blobs). Only the Skate 2 UDP-upload path stores a non-zero CRC — the client-declared value carried in the recipe `Head` (`RecipeAssembler.Crc`).

## Inbound: peer-uploaded recipe chunks (Skate 2 mid-session upload)

There's a less common path where Skate 2 *also* sends recipe chunks via UDP during the join sequence (for cases where the recipe wasn't on Plasma at Fesl handshake time, or for thumbs). The walker handles those via `RecipeHandlers.HandleHeadSkate2` and `RecipeHandlers.HandleDataSkate2` (`Walker/Handlers/RecipeHandlers.cs`) — both are **Skate 2-only**; the walker's dispatch guards them with `when ctx.Server.Variant == GameVariant.Skate2`.

`LobbyUdpServer.RecipeAssemblers` is a `ConcurrentDictionary<long, RecipeAssembler>` keyed by `peerId` alone; each `RecipeAssembler` holds one uploader's partial chunks. Chunks arrive identified by `chunkIndex`, are stored per-index, and when `ReceivedBytes` reaches the declared `Head.size` the assembled blob is `Put` into the blob store. Stale assemblers (no chunk for 30 s) are swept by `RunRecipeAssemblerSweepAsync`.

When this path is in play, the joiner's `PLAYER_JOIN` broadcast is held until its upload completes (`HandleDataSkate2` sets `PendingJoinBroadcast` only once the blob assembles), so every peer in the lobby gets the new peer's real recipe rather than a default.

## Skate 1 specifics

Skate 1 runs a **full, working recipe pipeline** — peers see each other's real skaters, not defaults. The Skate 1 client drives recipes through its `StateOnlineRecipe` state machine:

- **Upload** — the upload state machine packs the Create-A-Character record and fires a `Transaction_PostContentBlob(CT_Recipe, …)` — the Plasma blob-upload transaction arcadia receives as `blob/AddBlob`. `HandleAddBlob` then caches it in `RecipeBlobStore`.
- **Download** — the client builds a `Message_GameRecipeRequest` (`mPeerId = remote UID`) and sends it over UDP (op 20). It waits for `MT_GameRecipeHead`, computes the chunk count from `Head.mSize` (`mSize >> 10` — 1024-byte chunks), then collects the `MT_GameRecipeData` chunks. A `Head` with `size = 0` yields chunk count 0 → the client loads a default skater.

Arcadia's side is **variant-agnostic**: `RecipeHandlers.HandleRequest` (op 20 for Skate 1) has no variant guard, and `RecipeService.RespondAsync` serves the real `Head` + `Data` chunks whenever the peer's blob is in `RecipeBlobStore`. There is **no** "always default for Skate 1" branch anywhere in the server. `Head(size=0)` is emitted only on a genuine cache miss — the exact same fallback Skate 2 uses.

The one real Skate 1 limitation: arcadia does **not ingest UDP recipe uploads** for Skate 1 — `HandleHeadSkate2` / `HandleDataSkate2` are Skate 2-only. It doesn't need to: Skate 1 clients upload through the Plasma `blob/AddBlob` path above, so a peer's blob is already cached by the time anyone requests it.

## The `mRecipeMap` gate (Skate 2 client side)

On the receiving side, Skate 2's client maintains `mRecipeMap` — a `peerId → recipeBlob` dictionary. The `ApplyAttributes` gate (where the client transitions from "lobby setup" to "ready to start") checks that `mRecipeMap` has an entry for every peer in the roster.

If any peer's recipe is missing (no Head + Data sequence completed, no fallback fired), `ApplyAttributes` stalls and the client never reaches steady state. That's why the `Head(size=0)` fallback is critical — it satisfies the gate even when no real blob exists.

## Order-of-operations in JoinFinalizeFlow

`JoinFinalizeFlow.FireAsync` (`Flow/JoinFinalizeFlow.cs`) is the canonical join sequence on the server side. What it actually does, in order:

1. Clear `PendingJoinBroadcast`; set `PostJoinResetPending = true`.
2. `PlayerEventBroadcaster.BroadcastJoinAsync` — `HOST_ROSTER_ELEM` + `PLAYER_JOIN_FULL_MESH` for the new peer to each existing peer, and each existing peer back to the newcomer.
3. `ReleaseJoinGate` — the next queued joiner may now proceed.
4. A deferred task: wait for the **recipe mesh** (below; ≤20 s ceiling) + a 5 s settle, then — latest-wins debounced via `Game.LatestJoinFinalizationIndex` — `ResetBroadcaster.BroadcastLobbySkateAsync`; clear `PostJoinResetPending` in `finally`. If the mesh timed out, a detached 30 s watcher fires ONE recovery reset when the mesh finally completes (guards: joiner still present, still latest, `!Game.InProgress`).

The recipe request/serve is **not** sequenced inside `FireAsync`. It happens asynchronously: whenever a peer's client sends `MT_GameRecipeRequest`, the walker enqueues it and `ProcessTailAsync` drains it through `RecipeService.RespondAsync`. So the recipe exchange overlaps the join broadcast — it is not a strict step after it.

If `RespondAsync` ever served a `Head` with garbage data, the requester's recipe state machine would gate — which is why the cache must hold the real blob (or emit a clean `Head(size=0)`).

## The recipe-mesh gate

A naive gate — wait for `session.RecipeServed` then 5 s — is satisfied by the joiner's
**own-recipe self-echo** (the first thing every client requests after uploading), so the
actual cross-peer exchange isn't covered: the joiner's requests for existing peers'
recipes arrive 5–16 s after upload and routinely lose the race against the reset →
default/clone skaters and truck/wheel physics desync.

So the reset is gated on the **bidirectional, delivery-confirmed recipe mesh**:

- `RecipeService.RespondAsync` records every completed serve in
  `session.RecipeServeSeqByUid[peerUid] = highest outbound seq of the Head+Data serve`.
  `Head(size=0)` fallbacks are recorded too (they satisfy the client's default-loader
  gate; nothing further will arrive for that UID).
- `LobbySession.LastAckFromClient` tracks the client's cumulative CommUDP ack of our
  outbound seqs (w1 of every inbound DATA frame; kind=4 PING implies expected−1).
- A serve counts as **delivered** once `LastAckFromClient >= recorded seq`.
- Mesh complete ⇔ for every eligible existing peer P: P has been delivered the joiner's
  recipe AND the joiner has been delivered P's recipe.

Ceiling 20 s (`MeshWaitCeilingSeconds`) so a client that never requests can't deadlock the
join; on timeout the reset fires anyway and the late-recovery watcher covers stragglers.

Two related identity behaviors back the gate:

- **Sticky UIDs** (`ConnectionManager._stickyUids`): a returning player (same partition +
  onlineId) is re-issued the same UID. Client user-object matching is effectively
  name-keyed, so peers keep their old user object (and its old UID) across a rejoin —
  with fresh-per-login UIDs they would request + bind the rejoiner's recipe under the OLD
  UID while reset slot tables carried the NEW UID, so the recipe never bound.
- `RespondAsync`'s cache-miss log flags requests for UIDs that aren't any current
  player (`[requested UID is NOT a current player — stale-UID request]`).

## See also

- [app-layer.md](app-layer.md) — wire format of MT_GameRecipeRequest/Head/Data.
- [s1-vs-s2-differences.md](s1-vs-s2-differences.md) — variant-specific recipe pipeline notes.
- [lockstep-and-relay.md](lockstep-and-relay.md) — why recipes have to be complete before lockstep can begin.
