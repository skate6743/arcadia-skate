using Arcadia.Handlers;
using Xunit;

namespace tests;

public class PnowPolicyTests
{
    // Literal transcription of the pre-refactor HandlePlayNow branch chain (2026-07-18):
    // search-arm if/else ladder, isQuickMatch ranked exemption, and the create gate.
    private static (PnowSearch Search, bool MayCreate, bool RankedExempt) LegacyChain(
        bool isSkate2, bool isResetServer, bool typePresent, bool hunt, bool hasKey)
    {
        PnowSearch search;
        if (isResetServer && !hunt && (isSkate2 || typePresent)) search = PnowSearch.None;
        else if (!typePresent) search = PnowSearch.QuickMatch;
        else if (!isSkate2 && !isResetServer) search = PnowSearch.Skate1ByType;
        else if (hunt && typePresent) search = PnowSearch.Skate2ByType;
        else if (hasKey) search = PnowSearch.ByChallengeKey;
        else search = PnowSearch.QuickMatch;

        bool rankedExempt = !typePresent && (!isResetServer || !isSkate2);
        bool mayCreate = (!isSkate2 && isResetServer && typePresent)
                      || (isSkate2 && !hunt && hasKey);

        return (search, mayCreate, rankedExempt);
    }

    [Fact]
    public void Skate1FindServerNeverCreates()
    {
        // S1 findServer must never create a lobby — only resetServer does. Skate1 ignores
        // challenge_key entirely (not a param), so a key can't route findServer into a create.
        Assert.False(PnowPolicy.Skate1(isResetServer: false, typePresent: false).MayCreate);
        Assert.False(PnowPolicy.Skate1(isResetServer: false, typePresent: true).MayCreate);
        // resetServer + type is the only S1 create path.
        Assert.True(PnowPolicy.Skate1(isResetServer: true, typePresent: true).MayCreate);
    }

    public static IEnumerable<object[]> AllInputCombinations()
    {
        bool[] bits = [false, true];
        return from isSkate2 in bits
               from isResetServer in bits
               from typePresent in bits
               from hunt in bits
               from hasKey in bits
               where isSkate2 || !hunt // Skate 1 has no hunt parameter.
               select new object[] { isSkate2, isResetServer, typePresent, hunt, hasKey };
    }

    [Theory]
    [MemberData(nameof(AllInputCombinations))]
    public void PlanMatchesLegacyChain(bool isSkate2, bool isResetServer, bool typePresent, bool hunt, bool hasKey)
    {
        var plan = isSkate2
            ? PnowPolicy.Skate2(isResetServer, typePresent, hunt, hasKey)
            : PnowPolicy.Skate1(isResetServer, typePresent);

        var expected = LegacyChain(isSkate2, isResetServer, typePresent, hunt, hasKey);

        Assert.Equal(expected.Search, plan.Search);
        Assert.Equal(expected.MayCreate, plan.MayCreate);
        Assert.Equal(expected.RankedExempt, plan.RankedExempt);
    }
}
