namespace Arcadia.Handlers;

public enum PnowSearch
{
    None,
    QuickMatch,
    Skate1ByType,
    Skate2ByType,
    ByChallengeKey,
}

public readonly record struct PnowPlan(PnowSearch Search, bool MayCreate, bool RankedExempt);

// PlayNow matchmaking policy, one pure function per variant.
public static class PnowPolicy
{
    public static PnowPlan Skate1(bool isResetServer, bool typePresent)
    {
        if (!typePresent) return new(PnowSearch.QuickMatch, MayCreate: false, RankedExempt: true);
        if (isResetServer) return new(PnowSearch.None, MayCreate: true, RankedExempt: false);
        return new(PnowSearch.Skate1ByType, MayCreate: false, RankedExempt: false);
    }

    public static PnowPlan Skate2(bool isResetServer, bool typePresent, bool specificChallengeHunt, bool hasChallengeKey)
    {
        if (specificChallengeHunt)
        {
            return typePresent
                ? new(PnowSearch.Skate2ByType, MayCreate: false, RankedExempt: false)
                : new(PnowSearch.QuickMatch, MayCreate: false, RankedExempt: !isResetServer);
        }

        if (isResetServer) return new(PnowSearch.None, MayCreate: hasChallengeKey, RankedExempt: false);
        if (!typePresent) return new(PnowSearch.QuickMatch, MayCreate: hasChallengeKey, RankedExempt: true);

        return hasChallengeKey
            ? new(PnowSearch.ByChallengeKey, MayCreate: true, RankedExempt: false)
            : new(PnowSearch.QuickMatch, MayCreate: false, RankedExempt: false);
    }
}
