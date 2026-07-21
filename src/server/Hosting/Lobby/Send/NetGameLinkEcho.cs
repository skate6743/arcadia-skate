// netGameLink sync trailer echo extrapolation.
namespace Arcadia.Hosting.Lobby.Send
{
    public static class NetGameLinkEcho
    {
        public static uint Estimate(LobbySession session, uint nowTick)
        {
            if (session.LocalTickAtLastPeerSend == 0) return nowTick;
            unchecked { return session.LastPeerSendTick + (nowTick - session.LocalTickAtLastPeerSend); }
        }
    }
}
