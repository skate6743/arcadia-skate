using Arcadia.EA;

// Variant-aware Sk8 message opcode resolution + fixed body sizes.
namespace Arcadia.Hosting.Lobby.Protocol
{
    public static class Sk8Opcodes
    {
        public static byte GameSync(GameVariant v)               => v == GameVariant.Skate1 ? (byte)4  : (byte)1;
        public static byte GameReset(GameVariant v)              => v == GameVariant.Skate1 ? (byte)5  : (byte)2;
        public static byte GameRequest(GameVariant v)            => v == GameVariant.Skate1 ? (byte)6  : (byte)3;
        public static byte GameAttributes(GameVariant v)         => v == GameVariant.Skate1 ? (byte)7  : (byte)4;
        public static byte GameComplete(GameVariant v)           => v == GameVariant.Skate1 ? (byte)8  : (byte)6;
        public static byte GameResults(GameVariant v)            => v == GameVariant.Skate1 ? (byte)9  : (byte)7;
        public static byte GameAllPlayersComplete(GameVariant v) => v == GameVariant.Skate1 ? (byte)10 : (byte)8;
        public static byte GameFinalResults(GameVariant v)       => v == GameVariant.Skate1 ? (byte)11 : (byte)9;
        public static byte GameTimer(GameVariant v)              => v == GameVariant.Skate1 ? (byte)12 : (byte)10;
        public static byte GameExitPostChallenge(GameVariant v)  => v == GameVariant.Skate1 ? (byte)13 : (byte)11;
        public static byte GameLoadRequest(GameVariant v)        => v == GameVariant.Skate1 ? (byte)14 : (byte)12;
        public static byte GameResetAttributes(GameVariant v)    => v == GameVariant.Skate1 ? (byte)15 : (byte)14;
        public static byte GameRequestReset(GameVariant v)       => v == GameVariant.Skate1 ? (byte)16 : (byte)15;
        public static byte GameRequestChange(GameVariant v)      => v == GameVariant.Skate1 ? (byte)17 : (byte)16;
        public static byte GameChange(GameVariant v)             => 18;
        public static byte GameRemovePlayer(GameVariant v)       => 19;
        public static byte GameRecipeRequest(GameVariant v)      => v == GameVariant.Skate1 ? (byte)20 : (byte)23;
        public static byte GameRecipeHead(GameVariant v)         => v == GameVariant.Skate1 ? (byte)21 : (byte)24;
        public static byte GameRecipeData(GameVariant v)         => v == GameVariant.Skate1 ? (byte)22 : (byte)25;

        public const byte Skate2_GameAttributeUpdate = 5;
        public const byte Skate2_GameChallengeLoaded = 13;
        public const byte Skate2_GameSyncPoint       = 17;
        public const byte Skate2_GamePlayerAttributes = 20;
        public const byte Skate2_GameWager           = 21;
        public const byte Skate2_GameProposal        = 22;

        public const byte Skate1_Broadcast    = 1;
        public const byte Skate1_Broadcasted  = 2;
        public const byte Skate1_GameLoadDone = 3;
        public const byte Skate1_GameQOSInfo  = 23;

        public enum Kind
        {
            GameSync, GameReset, GameRequest, GameAttributes,
            GameAttributeUpdate, GameComplete, GameResults, GameAllPlayersComplete,
            GameFinalResults, GameTimer, GameExitPostChallenge, GameLoadRequest,
            GameChallengeLoaded, GameResetAttributes, GameRequestReset,
            GameRequestChange, GameSyncPoint, GameChange, GameRemovePlayer,
            GameRecipeRequest, GameRecipeHead, GameRecipeData,
            Skate1_Broadcast, Skate1_Broadcasted, Skate1_GameLoadDone, Skate1_GameQOSInfo,
            Skate2_GamePlayerAttributes, Skate2_GameWager, Skate2_GameProposal,
        }

        public static Kind? Decode(GameVariant v, byte op)
            => v == GameVariant.Skate1 ? DecodeSkate1(op) : DecodeSkate2(op);

        private static Kind? DecodeSkate1(byte op) => op switch
        {
            1  => Kind.Skate1_Broadcast,
            2  => Kind.Skate1_Broadcasted,
            3  => Kind.Skate1_GameLoadDone,
            4  => Kind.GameSync,
            5  => Kind.GameReset,
            6  => Kind.GameRequest,
            7  => Kind.GameAttributes,
            8  => Kind.GameComplete,
            9  => Kind.GameResults,
            10 => Kind.GameAllPlayersComplete,
            11 => Kind.GameFinalResults,
            12 => Kind.GameTimer,
            13 => Kind.GameExitPostChallenge,
            14 => Kind.GameLoadRequest,
            15 => Kind.GameResetAttributes,
            16 => Kind.GameRequestReset,
            17 => Kind.GameRequestChange,
            18 => Kind.GameChange,
            19 => Kind.GameRemovePlayer,
            20 => Kind.GameRecipeRequest,
            21 => Kind.GameRecipeHead,
            22 => Kind.GameRecipeData,
            23 => Kind.Skate1_GameQOSInfo,
            _  => null,
        };

        private static Kind? DecodeSkate2(byte op) => op switch
        {
            1  => Kind.GameSync,
            2  => Kind.GameReset,
            3  => Kind.GameRequest,
            4  => Kind.GameAttributes,
            5  => Kind.GameAttributeUpdate,
            6  => Kind.GameComplete,
            7  => Kind.GameResults,
            8  => Kind.GameAllPlayersComplete,
            9  => Kind.GameFinalResults,
            10 => Kind.GameTimer,
            11 => Kind.GameExitPostChallenge,
            12 => Kind.GameLoadRequest,
            13 => Kind.GameChallengeLoaded,
            14 => Kind.GameResetAttributes,
            15 => Kind.GameRequestReset,
            16 => Kind.GameRequestChange,
            17 => Kind.GameSyncPoint,
            18 => Kind.GameChange,
            19 => Kind.GameRemovePlayer,
            20 => Kind.Skate2_GamePlayerAttributes,
            21 => Kind.Skate2_GameWager,
            22 => Kind.Skate2_GameProposal,
            23 => Kind.GameRecipeRequest,
            24 => Kind.GameRecipeHead,
            25 => Kind.GameRecipeData,
            _  => null,
        };

        // Fixed body size after the leading op byte. -1 = variable or unknown.
        public static int FixedBodySize(GameVariant v, Kind kind)
            => v == GameVariant.Skate1 ? FixedBodySizeSkate1(kind) : FixedBodySizeSkate2(kind);

        private static int FixedBodySizeSkate1(Kind kind) => kind switch
        {
            Kind.GameRequest                => 2,
            Kind.GameComplete               => 0,
            Kind.GameResults                => 16,
            Kind.GameAllPlayersComplete     => 0,
            Kind.GameTimer                  => 20,
            Kind.GameExitPostChallenge      => 0,
            Kind.GameLoadRequest            => 4,
            Kind.GameRequestReset           => 4,
            Kind.GameRequestChange          => 12,
            Kind.GameChange                 => 21,
            Kind.GameRemovePlayer           => 12,
            Kind.Skate1_GameLoadDone        => 0,
            _ => -1,
        };

        private static int FixedBodySizeSkate2(Kind kind) => kind switch
        {
            Kind.GameRequest                => 9,
            Kind.GameComplete               => 0,
            Kind.GameResults                => 15,
            Kind.GameAllPlayersComplete     => 0,
            Kind.GameTimer                  => 12,
            Kind.GameExitPostChallenge      => 0,
            Kind.GameLoadRequest            => 4,
            Kind.GameChallengeLoaded        => 0,
            Kind.GameRequestReset           => 4,
            Kind.GameRequestChange          => 12,
            Kind.GameSyncPoint              => 0,
            Kind.GameChange                 => 20,
            Kind.GameRemovePlayer           => 12,
            Kind.Skate2_GamePlayerAttributes => 12,
            Kind.Skate2_GameWager           => 16,
            Kind.Skate2_GameProposal        => 29,
            _ => -1,
        };
    }
}
