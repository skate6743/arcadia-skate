using System.Buffers.Binary;
using System.Text;
using Arcadia.EA;

// Server-originated Sk8 message builders.
namespace Arcadia.Hosting.Lobby.Protocol
{
    public static class Sk8MessagePackets
    {
        public enum TimerType
        {
            LockTime = 1,
            StartTime = 2,
            HideScreen = 3,
            PostEvent = 4,
        }

        public static byte[] BuildGameAttributeUpdate(Sk8AttributeType attribute, string value)
        {
            byte[] valBytes = Encoding.UTF8.GetBytes(value);
            if (valBytes.Length > Sk8MessageLayout.AttributeListMaxBytesPerSlot)
                valBytes = valBytes[..Sk8MessageLayout.AttributeListMaxBytesPerSlot];

            byte[] body = new byte[1 + 4 + 4 + valBytes.Length];
            body[0] = Sk8Opcodes.Skate2_GameAttributeUpdate;
            BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(1, 4), (int)attribute);
            BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(5, 4), (uint)valBytes.Length);
            valBytes.CopyTo(body.AsSpan(9));
            return body;
        }

        public static byte[] BuildGameTimer(GameVariant variant, TimerType timer, ulong time)
        {
            if (variant == GameVariant.Skate1)
            {
                byte[] body = new byte[1 + 20];
                body[0] = Sk8Opcodes.GameTimer(variant);
                int slot = timer switch
                {
                    TimerType.LockTime => 4,
                    TimerType.StartTime => 0,
                    _ => 0,
                };
                BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(1 + slot, 4), (uint)time);
                return body;
            }
            else
            {
                byte[] body = new byte[1 + 4 + 8];
                body[0] = Sk8Opcodes.GameTimer(variant);
                BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(1, 4), (int)timer);
                BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(5, 8), time);
                return body;
            }
        }

        public static byte[] BuildGameRemovePlayer(GameVariant variant, long userId, uint lastFrame)
        {
            byte[] body = new byte[1 + 8 + 4];
            body[0] = Sk8Opcodes.GameRemovePlayer(variant);
            BinaryPrimitives.WriteInt64BigEndian(body.AsSpan(1, 8), userId);
            BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(9, 4), lastFrame);
            return body;
        }

        public static byte[] BuildGameRequestChange(GameVariant variant, int challengeType, ulong challengeKey)
        {
            byte[] body = new byte[1 + 4 + 8];
            body[0] = Sk8Opcodes.GameRequestChange(variant);
            BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(1, 4), challengeType);
            BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(5, 8), challengeKey);
            return body;
        }

        public static byte[] BuildGameChange(GameVariant variant, int challengeType, ulong challengeKey, ulong changeTime, bool skate1Change = false)
        {
            int trailerBytes = variant == GameVariant.Skate1 ? 1 : 0;
            byte[] body = new byte[1 + 4 + 8 + 8 + trailerBytes];
            body[0] = Sk8Opcodes.GameChange(variant);
            BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(1, 4), challengeType);
            BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(5, 8), challengeKey);
            BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(13, 8), changeTime);
            if (variant == GameVariant.Skate1)
                body[21] = (byte)(skate1Change ? 1 : 0);
            return body;
        }

        public static byte[] BuildGameExitPostChallenge(GameVariant variant)
            => new[] { Sk8Opcodes.GameExitPostChallenge(variant) };

        public static byte[] BuildGameAllPlayersComplete(GameVariant variant)
            => new[] { Sk8Opcodes.GameAllPlayersComplete(variant) };

        public static byte[] BuildGameComplete(GameVariant variant)
            => new[] { Sk8Opcodes.GameComplete(variant) };

        public static byte[] BuildGameFinalResultsEmpty(GameVariant variant)
        {
            if (variant == GameVariant.Skate1)
            {
                byte[] body = new byte[1 + Sk8MessageLayout.FinalResultsSkate1FixedBodySize];
                body[0] = Sk8Opcodes.GameFinalResults(variant);
                BinaryPrimitives.WriteInt32BigEndian(
                    body.AsSpan(1 + Sk8MessageLayout.FinalResultsSkate1FixedSlots * Sk8MessageLayout.FinalResultsSkate1PerSlotBytes, 4), 0);
                return body;
            }
            else
            {
                byte[] body = new byte[1 + 4];
                body[0] = Sk8Opcodes.GameFinalResults(variant);
                BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(1, 4), 0);
                return body;
            }
        }

        public static byte[] BuildGameFinalResultsSkate1(
            IReadOnlyList<(long Uid, uint EventTime, int Score, int FinishReason, int Ranking, bool PlayersChoice)> rows)
        {
            const int slots = Sk8MessageLayout.FinalResultsSkate1FixedSlots;
            const int per = Sk8MessageLayout.FinalResultsSkate1PerSlotBytes;
            byte[] body = new byte[1 + Sk8MessageLayout.FinalResultsSkate1FixedBodySize];
            body[0] = Sk8Opcodes.GameFinalResults(GameVariant.Skate1);

            int n = rows.Count < slots ? rows.Count : slots;
            for (int i = 0; i < n; i++)
            {
                var r = rows[i];
                int o = 1 + i * per;
                BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(o, 4), r.FinishReason);
                BinaryPrimitives.WriteInt64BigEndian(body.AsSpan(o + 4, 8), r.Uid);
                BinaryPrimitives.WriteInt16BigEndian(body.AsSpan(o + 12, 2), (short)r.Ranking);
                BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(o + 14, 4), r.Score);
                BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(o + 18, 4), r.EventTime);
                BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(o + 22, 4), 0);
            }
            BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(1 + slots * per, 4), n);
            return body;
        }

        // cash/exp/wager/winnings stay 0; the client credits the local player's wallet/XP from them
        public static byte[] BuildGameFinalResultsSkate2(
            IReadOnlyList<(long Uid, uint EventTime, int Score, int FinishReason, int Ranking, bool PlayersChoice)> rows)
        {
            const int stride = Sk8MessageLayout.FinalResultsPlayerStride;
            int n = Math.Min(rows.Count, Sk8MessageLayout.FinalResultsSkate2MaxPlayers);
            byte[] body = new byte[1 + 4 + n * stride];
            body[0] = Sk8Opcodes.GameFinalResults(GameVariant.Skate2);
            BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(1, 4), n);
            int o = 5;
            for (int i = 0; i < n; i++)
            {
                var r = rows[i];
                BinaryPrimitives.WriteInt64BigEndian(body.AsSpan(o, 8), r.Uid);
                BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(o + 8, 4), r.EventTime);
                BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(o + 12, 4), r.Score);
                BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(o + 16, 4), r.FinishReason);
                BinaryPrimitives.WriteInt16BigEndian(body.AsSpan(o + 20, 2), (short)r.Ranking);
                body[o + 22] = r.PlayersChoice ? (byte)1 : (byte)0;
                o += stride;
            }
            return body;
        }

        public static byte[] BuildGameRequestReset(GameVariant variant, Sk8ResetType resetType)
        {
            byte[] body = new byte[1 + 4];
            body[0] = Sk8Opcodes.GameRequestReset(variant);
            BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(1, 4), (int)resetType);
            return body;
        }
    }
}
