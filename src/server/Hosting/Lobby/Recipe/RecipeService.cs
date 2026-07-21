using System.Net;
using Arcadia.Hosting.Lobby.Protocol;
using Arcadia.Hosting.Lobby.Send;
using Microsoft.Extensions.Logging;

// Serves cached recipe blobs to peers that requested via MT_GameRecipeRequest.
namespace Arcadia.Hosting.Lobby.Recipe
{
    public static class RecipeService
    {
        private const int RecipeChunkSize = 1024;

        public static async Task RespondAsync(LobbyUdpServer server, IPEndPoint ep, LobbySession session, long peerId, CancellationToken ct)
        {
            var entry = server.Blobs.GetEntry(peerId, Storage.RecipeBlobStore.CT_Recipe);
            if (entry is null || entry.Value.Data.Length == 0)
            {
                bool isCurrentPlayer = server.Game.HasConnectedPlayer(peerId);
                foreach (var kv in server.Sessions)
                {
                    if (kv.Value.PlayerInfo?.UID == peerId) { isCurrentPlayer = true; break; }
                }
                server.Logger.LogWarning(
                    "LobbyUdp[{lobby}] 0x17 request for peer={peer} from {ep} has no cached blob — sending Head(size=0){staleHint}",
                    server.LobbyId, peerId, ep,
                    isCurrentPlayer ? "" : " [requested UID is NOT a current player — stale-UID request]");
                byte[] fallback = RecipePackets.BuildHead(server.Variant, peerId, 0, 0);
                uint fallbackSeq = await EncryptedSender.SendSk8BodyAsync(server, ep, session, fallback,
                    $"MT_GameRecipeHead(peer={peerId},size=0,fallback)", ct);
                if (fallbackSeq != 0)
                {
                    lock (session.RelayLock)
                        session.RecipeServeSeqByUid[peerId] = fallbackSeq;
                }
                return;
            }

            byte[] blob = entry.Value.Data;
            uint crc = entry.Value.Crc;
            byte[] head = RecipePackets.BuildHead(server.Variant, peerId, blob.Length, crc);
            uint maxSeq = await EncryptedSender.SendSk8BodyAsync(server, ep, session, head,
                $"MT_GameRecipeHead(peer={peerId},size={blob.Length},crc=0x{crc:X8})", ct);
            bool allSent = maxSeq != 0;

            int offset = 0;
            int idx = 0;
            while (offset < blob.Length)
            {
                int take = Math.Min(RecipeChunkSize, blob.Length - offset);
                byte[] chunk = blob.AsSpan(offset, take).ToArray();
                byte[] data = RecipePackets.BuildData(server.Variant, peerId, idx, chunk);
                uint seq = await EncryptedSender.SendSk8BodyAsync(server, ep, session, data,
                    $"MT_GameRecipeData(peer={peerId},chunk={idx})", ct);
                if (seq == 0) allSent = false;
                else if (seq > maxSeq) maxSeq = seq;
                offset += take;
                idx++;
            }

            if (allSent)
            {
                lock (session.RelayLock)
                    session.RecipeServeSeqByUid[peerId] = maxSeq;
            }
            else
            {
                server.Logger.LogWarning(
                    "LobbyUdp[{lobby}] recipe serve peer={peer} to {ep} incomplete (a frame was not sendable) — not recorded for the mesh gate",
                    server.LobbyId, peerId, ep);
            }
        }
    }
}
