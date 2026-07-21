using System.Buffers.Binary;
using Arcadia.EA;
using Arcadia.Hosting.Lobby.Protocol;
using Microsoft.Extensions.Logging;

// MT_GameReset relay-through logging.
namespace Arcadia.Hosting.Lobby.Walker.Handlers
{
    public static class GameResetHandler
    {
        public static bool Handle(WalkContext ctx, out int msgLen, out bool consumeOnly)
        {
            msgLen = 0;
            consumeOnly = false;

            int bodySize = ctx.Server.Variant == GameVariant.Skate1 ? 52 : 84;
            if (ctx.MsgOffset + 1 + bodySize > ctx.UserBodyLen) return false;
            msgLen = 1 + bodySize;

            int msgBase = ctx.PayloadBase + ctx.MsgOffset;
            int slotCount = ctx.Server.Variant == GameVariant.Skate1 ? 6 : 8;
            int resetTypeOffset = ctx.Server.Variant == GameVariant.Skate1 ? (1 + 48) : (1 + 64);
            Sk8ResetType resetType = (Sk8ResetType)BinaryPrimitives.ReadInt32BigEndian(
                ctx.Work.AsSpan(msgBase + resetTypeOffset, 4));

            System.Text.StringBuilder sb = new System.Text.StringBuilder("[");
            for (int i = 0; i < slotCount; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(BinaryPrimitives.ReadInt64BigEndian(ctx.Work.AsSpan(msgBase + 1 + i * 8, 8)));
            }
            sb.Append(']');

            if (ctx.Server.Variant == GameVariant.Skate1)
            {
                ctx.Logger.LogInformation(
                    "LobbyUdp[{lobby}] APP-MSG GameReset(Skate1) {ep} uid={uid} resetType={rt} peerIds={peers}",
                    ctx.LobbyId, ctx.Ep, ctx.Session.PlayerInfo?.UID ?? 0, resetType, sb.ToString());
            }
            else
            {
                long activity = BinaryPrimitives.ReadInt64BigEndian(
                    ctx.Work.AsSpan(msgBase + 1 + 64 + 12, 8));
                ctx.Logger.LogInformation(
                    "LobbyUdp[{lobby}] APP-MSG GameReset {ep} uid={uid} resetType={rt} activity=0x{act:X16} peerIds={peers}",
                    ctx.LobbyId, ctx.Ep, ctx.Session.PlayerInfo?.UID ?? 0, resetType, activity, sb.ToString());
            }
            ctx.Logger.LogWarning(
                "LobbyUdp[{lobby}] client-originated MT_GameReset relayed WITHOUT reset-gate protection uid={uid} resetType={rt}",
                ctx.LobbyId, ctx.Session.PlayerInfo?.UID ?? 0, resetType);
            return true;
        }
    }
}
