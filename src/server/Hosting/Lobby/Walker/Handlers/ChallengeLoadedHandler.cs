using Microsoft.Extensions.Logging;

// MT_GameChallengeLoaded (Skate 2 only, op 13): CONSUMED; drives Phase 2 of the challenge handshake.
namespace Arcadia.Hosting.Lobby.Walker.Handlers
{
    public static class ChallengeLoadedHandler
    {
        public static bool Handle(WalkContext ctx, out int msgLen, out bool consumeOnly)
        {
            msgLen = 0;
            consumeOnly = false;

            if (ctx.MsgOffset + 1 > ctx.UserBodyLen) return false;

            msgLen = 1;
            consumeOnly = true;
            ctx.Logger.LogInformation(
                "LobbyUdp[{lobby}] APP-MSG GameChallengeLoaded {ep} uid={uid}",
                ctx.LobbyId, ctx.Ep, ctx.Session.PlayerInfo?.UID ?? 0);
            if (ctx.Session.PlayerInfo is not null)
                ctx.Server.OnChallengeLoadedReport(ctx.Session.PlayerInfo.UID);
            return true;
        }
    }
}
