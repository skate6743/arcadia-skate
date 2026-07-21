// Per-peer handshake stage progression.
namespace Arcadia.Hosting.Lobby.Protocol
{
    public enum AppStage
    {
        Idle,
        HelloReceived,
        HostHelloSent,
        HostRosterElemSent,
        RosterAckReceived,
        JoinCompleteSent,
        GameAttribsSent,
        GameResetSent,
        GameRecipeRequestSent,
    }

    public enum EnvelopeKind
    {
        Plain,
        ProtoTunnel,
    }
}
