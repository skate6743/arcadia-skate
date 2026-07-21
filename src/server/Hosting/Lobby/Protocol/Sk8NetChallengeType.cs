using Arcadia.EA;

// Wire-enum int to name for MT_GameRequestChange / GameChange / GameAttributes slot 1.
// S1 and S2 reuse the same slot with different values.
namespace Arcadia.Hosting.Lobby.Protocol
{
    public static class Sk8NetChallengeType
    {
        public static string? ToName(GameVariant variant, int wireType) => variant switch
        {
            GameVariant.Skate1 => ToNameSkate1(wireType),
            GameVariant.Skate2 => ToNameSkate2(wireType),
            _ => null,
        };

        private static string? ToNameSkate1(int wireType) => wireType switch
        {
            0 => "SKATE",
            1 => "BestTrick",
            2 => "OwnTheSpot",
            3 => "Lap",
            4 => "Gate",
            5 => "Jam",
            6 => "SpotRace",
            7 => "DeathRace",
            8 => "LastManStanding",
            9 => "OnlineFreeSkate",
            _ => null,
        };

        private static string? ToNameSkate2(int wireType) => wireType switch
        {
            0 => "OwnTheSpot",
            1 => "Slap",
            2 => "BestTrick",
            3 => "Jam",
            4 => "OneUp",
            5 => "SKATE",
            6 => "DeathRace",
            7 => "HallOfMeat",
            8 => "OnlineFreeSkate",
            _ => null,
        };
    }
}
