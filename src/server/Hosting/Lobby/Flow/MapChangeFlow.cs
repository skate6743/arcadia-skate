using Arcadia.EA;
using Arcadia.Hosting.Lobby.Protocol;
using Arcadia.Hosting.Lobby.Reset;
using Microsoft.Extensions.Logging;

// Map-change flow: MT_GameChange announcement, MT_GameAttributes(newKey), then MT_GameReset(LobbySkate).
namespace Arcadia.Hosting.Lobby.Flow
{
    public static class MapChangeFlow
    {
        public static async Task RunAsync(LobbyUdpServer server, int challengeType, ulong newKey, CancellationToken ct)
        {
            if (!server.Game.TryStart())
            {
                server.Logger.LogInformation(
                    "LobbyUdp[{lobby}] MAP-CHANGE rejected (another flow has the startup gate) type={t} key=0x{k:X16}",
                    server.LobbyId, challengeType, newKey);
                return;
            }

            if (Interlocked.CompareExchange(ref server.MapChangeInProgress, 1, 0) != 0)
            {
                server.Game.InProgress = false;
                server.Logger.LogInformation(
                    "LobbyUdp[{lobby}] MAP-CHANGE ignored (already in progress) type={t} key=0x{k:X16}",
                    server.LobbyId, challengeType, newKey);
                return;
            }

            await server.WaitForJoinersAsync("MAP-CHANGE", 30, ct);

            try
            {
                server.Logger.LogInformation(
                    "LobbyUdp[{lobby}] MAP-CHANGE start type={t} key=0x{k:X16}",
                    server.LobbyId, challengeType, newKey);

                string newKeyStr = newKey.ToString(System.Globalization.CultureInfo.InvariantCulture);
                server.Game.Data["B-U-challenge_key"] = newKeyStr;
                server.Game.ChallengeKey = newKeyStr;

                string? mappedTypeName = Sk8NetChallengeType.ToName(server.Variant, challengeType);
                if (mappedTypeName is not null)
                    server.Game.Data["B-U-challenge_type"] = mappedTypeName;

                const ulong changeTimeSeconds = 3UL;
                byte[] changeBody = Sk8MessagePackets.BuildGameChange(server.Variant, challengeType, newKey, changeTimeSeconds);
                await ResetBroadcaster.BroadcastSk8BodyAsync(server, changeBody,
                    $"MT_GameChange(type={challengeType},key=0x{newKey:X16},t={changeTimeSeconds})", ct);

                try { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
                catch (OperationCanceledException) { return; }

                server.Game.Data.TryGetValue("B-U-challenge_type", out string? typeStr);
                server.Game.Data.TryGetValue("B-U-ping_site", out string? pingSite);
                server.Game.Data.TryGetValue("B-U-is_private", out string? isPrivateStr);
                byte[] attribsBody = GameAttributesPacket.Build(server.Variant, typeStr, newKeyStr, pingSite, isPrivateStr);

                if (server.Variant == GameVariant.Skate1)
                {
                    // S1: attribs+commit must leave back-to-back (a gap un-arms the commit);
                    // gate on the commit instead — cumulative acks cover the attribs too.
                    await ResetBroadcaster.BroadcastSk8BodyAsync(server, attribsBody,
                        $"MT_GameAttributes(key=0x{newKey:X16},map-change)", ct);

                    byte[] commitBody = Sk8MessagePackets.BuildGameChange(server.Variant, challengeType, newKey, 0UL, skate1Change: true);
                    await ResetBroadcaster.BroadcastSk8BodyAwaitAckAsync(server, commitBody,
                        $"MT_GameChange(commit,type={challengeType},key=0x{newKey:X16})", ResetBroadcaster.GameAttributesAckWaitMs, ct);

                    try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
                    catch (OperationCanceledException) { return; }
                }
                else
                {
                    await ResetBroadcaster.BroadcastSk8BodyAwaitAckAsync(server, attribsBody,
                        $"MT_GameAttributes(key=0x{newKey:X16},map-change)", ResetBroadcaster.GameAttributesAckWaitMs, ct);
                }

                await ResetBroadcaster.BroadcastLobbySkateAsync(server, ct);
                server.Game.InProgress = false;

                server.Logger.LogInformation(
                    "LobbyUdp[{lobby}] MAP-CHANGE complete type={t} key=0x{k:X16}",
                    server.LobbyId, challengeType, newKey);
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                server.Logger.LogWarning(e,
                    "LobbyUdp[{lobby}] MAP-CHANGE failed type={t} key=0x{k:X16}",
                    server.LobbyId, challengeType, newKey);
            }
            finally
            {
                server.Game.InProgress = false;
                Interlocked.Exchange(ref server.MapChangeInProgress, 0);
            }
        }
    }
}
