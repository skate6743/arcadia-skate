using Microsoft.Extensions.Logging;

// MT_GameSyncPoint (Skate 2 only, op 17, empty body). Relayed (Own The Spot scoring).
namespace Arcadia.Hosting.Lobby.Walker.Handlers
{
    public static class GameSyncPointHandler
    {
        public static bool Handle(WalkContext ctx, out int msgLen, out bool consumeOnly)
        {
            msgLen = 1;
            consumeOnly = false;
            ctx.Logger.LogInformation(
                "LobbyUdp[{lobby}] APP-MSG GameSyncPoint {ep} uid={uid}",
                ctx.LobbyId, ctx.Ep, ctx.Session.PlayerInfo?.UID ?? 0);
            return true;
        }
    }
}
