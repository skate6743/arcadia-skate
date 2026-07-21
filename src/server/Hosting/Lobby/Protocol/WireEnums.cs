// Fesl GameManager + Sk8 net enums.
namespace Arcadia.Hosting.Lobby.Protocol
{
    public enum GameManagerPacketType : byte
    {
        Hello = 0,
        Goodbye = 1,
        HostHello = 2,
        HostRosterElem = 3,
        RosterAck = 4,
        PlayerJoin = 5,
        PlayerJoinQuery = 6,
        PlayerJoinQueryResult = 7,
        PlayerJoinFullMesh = 8,
        JoinComplete = 9,
        PlayerLeft = 10,
        ConnectionChange = 11,
        ConnectionChange2 = 12,
        RelayRequest = 13,
        RelayMessage = 14,
        HostPropertyChange = 15,
        StartGameRequest = 16,
        StartGameReady = 17,
        StartGame = 18,
        EndGame = 19,
        VoipEnabledChange = 20,
        VoipReceiverChange = 21,
        HostMigrationComplete = 22,
        PlayerPlayGroupChange = 23,
    }

    public static class GameManagerPacketTypeExtensions
    {
        public const byte WireBias = 0x80;
        public static byte ToWireByte(this GameManagerPacketType t) => (byte)((byte)t + WireBias);
    }

    public enum Sk8MessageType : byte
    {
        GameSync = 1,
        GameReset = 2,
        GameRequest = 3,
        GameAttributes = 4,
        GameAttributeUpdate = 5,
        GameComplete = 6,
        GameResults = 7,
        GameAllPlayersComplete = 8,
        GameFinalResults = 9,
        GameTimer = 10,
        GameExitPostChallenge = 11,
        GameLoadRequest = 12,
        GameChallengeLoaded = 13,
        GameResetAttributes = 14,
        GameRequestReset = 15,
        GameRequestChange = 16,
        GameSyncPoint = 17,
        GameChange = 18,
        GameRemovePlayer = 19,

        Skate2_GamePlayerAttributes = 20,
        Skate2_GameWager = 21,
        Skate2_GameProposal = 22,

        GameRecipeRequest_Skate2 = 23,
        GameRecipeHead_Skate2 = 24,
        GameRecipeData_Skate2 = 25,

        GameRecipeRequest_Skate1 = 20,
        GameRecipeHead_Skate1 = 21,
        GameRecipeData_Skate1 = 22,
    }

    public enum Sk8AttributeType
    {
        GameVersion = 0,
        ChallengeType = 1,
        ChallengeKey = 2,
        PingSite = 3,
        IsPrivate = 4,
        MaxPlayers = 5,
        IsRanked = 6,
        OverallSkill = 7,
        ChallengeSkill = 8,
    }

    public enum Sk8ResetType
    {
        LobbySkate = 0,
        ChallengeLoad = 1,
        Challenge = 2,
    }

    // mRequest semantics differ across S1/S2: same byte, different meaning.
    public enum Sk8GameRequest : byte
    {
        StartGame = 1,
        ToggleSlotAccess = 3,
        LostConnection = 4,
        PauseResume = 5,
        ConvertPrivateSessionSk1 = 6,
        Heartbeat = 7,
    }

    public enum GameManagerNetworkType : byte
    {
        ClientServer = 0,
        PeerToPeer = 1,
    }

    public enum GameManagerVoipType : byte
    {
        Disabled = 0,
        Mesh = 1,
        ClientServer = 2,
    }

    [Flags]
    public enum Sk8GameSyncFlag : byte
    {
        None = 0,
        PlayerSlotMask = 0x0F,
        OnlyInput = 0x20,
        SizeFieldIsTwoBytes = 0x40,
        FirstFrame = 0x80,
    }
}
