using System.Net;
using Arcadia.Storage;
using Xunit;

namespace tests;

public class UdpSessionCacheTests
{
    private static readonly IPAddress IpA = IPAddress.Parse("203.0.113.10");
    private static readonly IPAddress SharedIp = IPAddress.Parse("198.51.100.7");

    private static UdpSessionCache.ClientInfo Info(long uid, string name)
        => new(uid, name, PlayerRef: (int)uid, Platform: "RPCN");

    [Fact]
    public void DistinctIp_ResolvesToTheRegisteredPlayer()
    {
        var cache = new UdpSessionCache();
        cache.Register(IpA, Info(1, "alice"));

        Assert.True(cache.IsAuthorized(IpA));
        var resolved = cache.Resolve(new IPEndPoint(IpA, 10000));
        Assert.NotNull(resolved);
        Assert.Equal(1, resolved!.UID);
    }

    [Fact]
    public void Resolve_IsIdempotentPerEndpoint()
    {
        var cache = new UdpSessionCache();
        cache.Register(IpA, Info(1, "alice"));
        var ep = new IPEndPoint(IpA, 10000);

        var first = cache.Resolve(ep);
        var second = cache.Resolve(ep);
        Assert.Equal(first!.UID, second!.UID);
    }

    [Fact]
    public void TwoPlayersSameIp_ResolveToDistinctIdentities()
    {
        var cache = new UdpSessionCache();
        cache.Register(SharedIp, Info(1, "alice"));
        cache.Register(SharedIp, Info(2, "bob"));

        var a = cache.Resolve(new IPEndPoint(SharedIp, 1076));
        var b = cache.Resolve(new IPEndPoint(SharedIp, 3200));

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotEqual(a!.UID, b!.UID);   // the core fix: never the same identity
    }

    [Fact]
    public void ReRegisterSameUid_DoesNotDuplicateOrLeak()
    {
        var cache = new UdpSessionCache();
        cache.Register(IpA, Info(1, "alice"));
        cache.Register(IpA, Info(1, "alice"));   // rejoin / re-EGAM

        cache.Remove(IpA, 1);
        Assert.False(cache.IsAuthorized(IpA));   // one Remove clears it → no duplicate lingered
    }

    [Fact]
    public void Remove_DropsIpOnlyWhenLastPlayerGone()
    {
        var cache = new UdpSessionCache();
        cache.Register(SharedIp, Info(1, "alice"));
        cache.Register(SharedIp, Info(2, "bob"));

        cache.Remove(SharedIp, 1);
        Assert.True(cache.IsAuthorized(SharedIp));    // bob still there
        var still = cache.Resolve(new IPEndPoint(SharedIp, 3200));
        Assert.Equal(2, still!.UID);

        cache.Remove(SharedIp, 2);
        Assert.False(cache.IsAuthorized(SharedIp));   // now de-registered
    }

    [Fact]
    public void ReleaseClaim_AllowsReconnectToRebind()
    {
        var cache = new UdpSessionCache();
        cache.Register(IpA, Info(1, "alice"));

        var ep1 = new IPEndPoint(IpA, 10000);
        Assert.Equal(1, cache.Resolve(ep1)!.UID);

        cache.ReleaseClaim(ep1);                       // session closed
        var ep2 = new IPEndPoint(IpA, 20000);          // reconnect, new NAT port
        Assert.Equal(1, cache.Resolve(ep2)!.UID);      // rebinds cleanly
    }

    [Fact]
    public void UnregisteredIp_IsNotAuthorizedAndResolvesNull()
    {
        var cache = new UdpSessionCache();
        Assert.False(cache.IsAuthorized(IpA));
        Assert.Null(cache.Resolve(new IPEndPoint(IpA, 10000)));
    }
}
