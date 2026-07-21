using System.Buffers.Binary;
using Arcadia.EA;
using Arcadia.Hosting.Lobby.Recipe;
using Microsoft.Extensions.Logging;

// MT_GameRecipeRequest/Head/Data: all CONSUMED server-side, never relayed.
namespace Arcadia.Hosting.Lobby.Walker.Handlers
{
    public static class RecipeHandlers
    {
        public static bool HandleRequest(WalkContext ctx, out int msgLen, out bool consumeOnly)
        {
            msgLen = 0;
            consumeOnly = false;

            if (ctx.MsgOffset + 9 > ctx.UserBodyLen) return false;

            long peerId = BinaryPrimitives.ReadInt64BigEndian(
                ctx.Work.AsSpan(ctx.PayloadBase + ctx.MsgOffset + 1, 8));
            ctx.Logger.LogInformation("LobbyUdp[{lobby}] MT_GameRecipeRequest from {ep} peer={peer}",
                ctx.LobbyId, ctx.Ep, peerId);
            ctx.Session.PendingRecipeRequests.Enqueue(peerId);
            msgLen = 9;
            consumeOnly = true;
            return true;
        }

        public static bool HandleHeadSkate2(WalkContext ctx, out int msgLen, out bool consumeOnly)
        {
            msgLen = 0;
            consumeOnly = false;

            if (ctx.MsgOffset + 17 > ctx.UserBodyLen) return false;

            long peerId = BinaryPrimitives.ReadInt64BigEndian(
                ctx.Work.AsSpan(ctx.PayloadBase + ctx.MsgOffset + 1, 8));
            uint size = BinaryPrimitives.ReadUInt32BigEndian(
                ctx.Work.AsSpan(ctx.PayloadBase + ctx.MsgOffset + 9, 4));
            uint crc = BinaryPrimitives.ReadUInt32BigEndian(
                ctx.Work.AsSpan(ctx.PayloadBase + ctx.MsgOffset + 13, 4));

            if (size > 0)
            {
                if (ctx.Server.RecipeAssemblers.TryGetValue(peerId, out RecipeAssembler? existing))
                {
                    if (existing.DeclaredSize == (int)size && existing.Crc == crc)
                    {
                        ctx.Logger.LogInformation(
                            "LobbyUdp[{lobby}] recipe upload Head duplicate ignored peer={peer} size={size} crc=0x{crc:X8} (already in progress, {bytes}/{total} bytes)",
                            ctx.LobbyId, peerId, size, crc, existing.ReceivedBytes, existing.DeclaredSize);
                    }
                    else
                    {
                        ctx.Logger.LogWarning(
                            "LobbyUdp[{lobby}] recipe upload Head REPLACES in-progress assembler peer={peer} oldSize={oldSize} newSize={newSize}",
                            ctx.LobbyId, peerId, existing.DeclaredSize, size);
                        ctx.Server.RecipeAssemblers[peerId] = new RecipeAssembler((int)size, crc);
                    }
                }
                else
                {
                    ctx.Server.RecipeAssemblers[peerId] = new RecipeAssembler((int)size, crc);
                    ctx.Logger.LogInformation(
                        "LobbyUdp[{lobby}] recipe upload start peer={peer} size={size} crc=0x{crc:X8}",
                        ctx.LobbyId, peerId, size, crc);
                }
            }
            else
            {
                ctx.Logger.LogInformation(
                    "LobbyUdp[{lobby}] recipe upload Head(size=0) peer={peer} crc=0x{crc:X8} (fallback path)",
                    ctx.LobbyId, peerId, crc);
            }
            msgLen = 17;
            consumeOnly = true;
            return true;
        }

        public static bool HandleDataSkate2(WalkContext ctx, out int msgLen, out bool consumeOnly)
        {
            msgLen = 0;
            consumeOnly = false;

            if (ctx.MsgOffset + 17 > ctx.UserBodyLen) return false;

            long peerId = BinaryPrimitives.ReadInt64BigEndian(
                ctx.Work.AsSpan(ctx.PayloadBase + ctx.MsgOffset + 1, 8));
            uint chunkLen = BinaryPrimitives.ReadUInt32BigEndian(
                ctx.Work.AsSpan(ctx.PayloadBase + ctx.MsgOffset + 9, 4));
            uint chunkIndex = BinaryPrimitives.ReadUInt32BigEndian(
                ctx.Work.AsSpan(ctx.PayloadBase + ctx.MsgOffset + 13, 4));

            if (chunkLen == 0 || ctx.MsgOffset + 17 + (int)chunkLen > ctx.UserBodyLen)
                return false;

            if (ctx.Server.RecipeAssemblers.TryGetValue(peerId, out RecipeAssembler? asm))
            {
                byte[] chunk = ctx.Work.AsSpan(ctx.PayloadBase + ctx.MsgOffset + 17, (int)chunkLen).ToArray();
                if (asm.AddChunk((int)chunkIndex, chunk))
                {
                    byte[]? blob = asm.Build();
                    if (blob is not null)
                    {
                        ctx.Server.Blobs.Put(peerId, Storage.RecipeBlobStore.CT_Recipe, blob, asm.Crc);
                        ctx.Server.RecipeAssemblers.TryRemove(peerId, out _);
                        ctx.Logger.LogInformation(
                            "LobbyUdp[{lobby}] recipe upload complete peer={peer} size={size}",
                            ctx.LobbyId, peerId, blob.Length);
                        if (!ctx.Session.JoinBroadcasted && ctx.Session.PlayerInfo is not null && peerId == ctx.Session.PlayerInfo.UID)
                        {
                            ctx.Session.JoinBroadcasted = true;
                            ctx.Session.PendingJoinBroadcast = true;
                        }
                    }
                }
            }
            else
            {
                ctx.Logger.LogWarning(
                    "LobbyUdp[{lobby}] recipe Data without assembler peer={peer} chunkIndex={idx} chunkLen={n} ep={ep} — chunk dropped",
                    ctx.LobbyId, peerId, chunkIndex, chunkLen, ctx.Ep);
            }

            msgLen = 17 + (int)chunkLen;
            consumeOnly = true;
            return true;
        }
    }
}
