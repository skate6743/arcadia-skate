using System.Buffers.Binary;
using Arcadia.EA;
using Arcadia.Hosting.Lobby.Protocol;
using Microsoft.Extensions.Logging;

// MT_GameRequest: variant-aware body size + per-mRequest dispatch.
namespace Arcadia.Hosting.Lobby.Walker.Handlers
{
    public static class GameRequestHandler
    {
        public static bool Handle(WalkContext ctx, out int msgLen, out bool consumeOnly)
        {
            msgLen = 0;
            consumeOnly = false;

            int mValueBytes = ctx.Server.Variant == GameVariant.Skate1 ? 1 : 8;
            int bodySize = 1 + 1 + mValueBytes;
            if (ctx.MsgOffset + bodySize > ctx.UserBodyLen) return false;

            Sk8GameRequest mRequest = (Sk8GameRequest)ctx.Work[ctx.PayloadBase + ctx.MsgOffset + 1];
            long mValue = ctx.Server.Variant == GameVariant.Skate1
                ? ctx.Work[ctx.PayloadBase + ctx.MsgOffset + 2]
                : BinaryPrimitives.ReadInt64BigEndian(ctx.Work.AsSpan(ctx.PayloadBase + ctx.MsgOffset + 2, 8));
            msgLen = bodySize;

            string pauseTag = mRequest == Sk8GameRequest.PauseResume
                ? (mValue != 0 ? " [PAUSE]" : " [RESUME]") : "";
            ctx.Logger.LogInformation(
                "LobbyUdp[{lobby}] APP-MSG GameRequest {ep} uid={uid} mRequest={req} mValue={val}{tag}",
                ctx.LobbyId, ctx.Ep, ctx.Session.PlayerInfo?.UID ?? 0, mRequest, mValue, pauseTag);

            if (mRequest == Sk8GameRequest.StartGame)
            {
                consumeOnly = true;
                ctx.Server.OnStartGameRequested(ctx.Session);
            }

            if (mRequest == Sk8GameRequest.ToggleSlotAccess && ctx.Server.Variant == GameVariant.Skate2)
            {
                consumeOnly = true;
                bool newIsPrivate = mValue != 0;
                ctx.Logger.LogInformation(
                    "LobbyUdp[{lobby}] HOST-ACTION ToggleSlotAccess from {ep} uid={uid} → is_private={priv}",
                    ctx.LobbyId, ctx.Ep, ctx.Session.PlayerInfo?.UID ?? 0, newIsPrivate);
                ctx.Server.OnAttributeUpdate(Sk8AttributeType.IsPrivate, newIsPrivate ? "true" : "false");
            }

            return true;
        }
    }
}
