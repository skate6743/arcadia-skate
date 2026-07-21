using Arcadia.EA;

// MT_GameRequest builder. S1: 1B mRequest + 1B mValue; S2: 1B mRequest + 8B mValue.
namespace Arcadia.Hosting.Lobby.Protocol
{
    public static class GameRequestPacket
    {
        public const int Skate2BodySize = 1 + 1 + 8;
        public const int Skate1BodySize = 1 + 1 + 1;

        public static int BodySize(GameVariant variant)
            => variant == GameVariant.Skate1 ? Skate1BodySize : Skate2BodySize;

        public static byte[] Build(GameVariant variant, Sk8GameRequest request, long value)
        {
            if (variant == GameVariant.Skate1)
            {
                byte[] body = new byte[Skate1BodySize];
                body[0] = Sk8Opcodes.GameRequest(variant);
                body[1] = (byte)request;
                body[2] = (byte)value;
                return body;
            }
            else
            {
                byte[] body = new byte[Skate2BodySize];
                body[0] = Sk8Opcodes.GameRequest(variant);
                body[1] = (byte)request;
                for (int i = 0; i < 8; i++)
                    body[2 + i] = (byte)(value >> ((7 - i) * 8));
                return body;
            }
        }

        public static byte[] StartGame(GameVariant variant) => Build(variant, Sk8GameRequest.StartGame, 0);

        public static byte[] LostConnection(GameVariant variant)
            => Build(variant,
                variant == GameVariant.Skate1 ? (Sk8GameRequest)7 : Sk8GameRequest.LostConnection,
                0);

        // S1: mRequest=7, mValue=4 (KICKED_BY_GAME_HOST). S2: LostConnection(4), mValue unused.
        public static byte[] HostKick(GameVariant variant)
            => variant == GameVariant.Skate1
                ? Build(GameVariant.Skate1, (Sk8GameRequest)7, 4)
                : LostConnection(GameVariant.Skate2);
    }
}
