using Arcadia.Hosting.Lobby.Protocol;
using Microsoft.Extensions.Logging;

// Fallback for fixed-size Sk8 messages not handled explicitly.
namespace Arcadia.Hosting.Lobby.Walker.Handlers
{
    public static class FixedFallbackHandler
    {
        public static bool Handle(WalkContext ctx, byte op, Sk8Opcodes.Kind? kind, out int msgLen, out bool consumeOnly)
        {
            msgLen = 0;
            consumeOnly = false;

            int fixedLen = Sk8MessageLayout.FixedBodySize(ctx.Server.Variant, op);
            if (fixedLen < 0 || ctx.MsgOffset + 1 + fixedLen > ctx.UserBodyLen)
            {
                ctx.Logger.LogWarning(
                    "LobbyUdp[{lobby}] WALKER-BAIL ep={ep} uid={uid} variant={variant} srcSeq={seq} reason={reason} op=0x{op:X2} kind={kind} fixedLen={fl}",
                    ctx.LobbyId, ctx.Ep, ctx.Session.PlayerInfo?.UID ?? 0, ctx.Server.Variant, ctx.SrcBareSeq,
                    fixedLen < 0 ? "unknown-opcode" : "truncated",
                    op, kind?.ToString() ?? "(unknown)", fixedLen);
                return false;
            }
            msgLen = 1 + fixedLen;

            string kindName = kind?.ToString() ?? $"unknown(0x{op:X2})";
            ctx.Logger.LogInformation(
                "LobbyUdp[{lobby}] APP-MSG {kind} {ep} uid={uid} bodyLen={n}",
                ctx.LobbyId, kindName, ctx.Ep, ctx.Session.PlayerInfo?.UID ?? 0, fixedLen);
            return true;
        }
    }
}
