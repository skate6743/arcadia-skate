using System.Buffers.Binary;
using System.Text;
using Arcadia.EA;

// MT_GameAttributes wire body builder.
namespace Arcadia.Hosting.Lobby.Protocol
{
    public static class GameAttributesPacket
    {
        private const int MaxAttributeBytes = 63;

        public const string Skate2GameVersion = "";
        public const string Skate1GameVersion = "#BAM";
        public const string DefaultChallengeType = "OnlineFreeSkate";
        public const string Skate2DefaultChallengeKey = "29899526234398379";
        public const string Skate1DefaultChallengeKey = "-9019507031372527534";

        public static byte[] Build(GameVariant variant, string? challengeType, string? challengeKey, string? pingSite, string? isPrivate = null)
            => variant == GameVariant.Skate1
                ? BuildSkate1(challengeType, challengeKey, pingSite, isPrivate)
                : BuildSkate2(challengeType, challengeKey, pingSite, isPrivate);

        private static byte[] BuildSkate2(string? challengeType, string? challengeKey, string? pingSite, string? isPrivate)
        {
            string[] values =
            {
                Skate2GameVersion,
                Coalesce(challengeType, DefaultChallengeType),
                Coalesce(challengeKey, Skate2DefaultChallengeKey),
                pingSite ?? string.Empty,
                Coalesce(isPrivate, "true"),
                "6",
                "false",
                "0",
                "0",
            };
            return Pack(Sk8Opcodes.GameAttributes(GameVariant.Skate2), values, MaxAttributeBytes + 1, lockTimeTrailerBytes: 0);
        }

        private static byte[] BuildSkate1(string? challengeType, string? challengeKey, string? pingSite, string? isPrivate)
        {
            string[] attrs =
            {
                Skate1GameVersion,
                "OnlineFreeSkate",
                Coalesce(challengeType, DefaultChallengeType),
                Coalesce(challengeKey, Skate1DefaultChallengeKey),
                "0",
                "0",
                "arcadia",
                Coalesce(isPrivate, "false"),
                "6",
                "true",
                "false",
                "false",
            };
            return Pack(Sk8Opcodes.GameAttributes(GameVariant.Skate1), attrs, MaxAttributeBytes, lockTimeTrailerBytes: 8);
        }

        private static byte[] Pack(byte typeByte, string[] values, int maxBytes, int lockTimeTrailerBytes)
        {
            byte[][] raw = new byte[values.Length][];
            int total = 1;
            for (int i = 0; i < values.Length; i++)
            {
                byte[] b = Encoding.UTF8.GetBytes(values[i]);
                if (b.Length > maxBytes) b = b[..maxBytes];
                raw[i] = b;
                total += 4 + b.Length;
            }
            total += lockTimeTrailerBytes;

            byte[] body = new byte[total];
            int off = 0;
            body[off++] = typeByte;
            foreach (byte[] b in raw)
            {
                BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(off, 4), (uint)b.Length);
                off += 4;
                b.CopyTo(body.AsSpan(off));
                off += b.Length;
            }
            return body;
        }

        private static string Coalesce(string? value, string fallback)
            => string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
