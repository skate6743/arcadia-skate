using System.Net;

namespace Arcadia.Storage;

// Maps an in-game UDP session to its authenticated player. The UDP HELLO carries no
// identity, so identity is resolved from the source IP registered at Theater hand-off.
// Multiple players can share one public IP (same NAT / CGNAT): each registers its own
// entry and each UDP endpoint claims a distinct one, so co-located players never collapse
// to a single identity. Entries are removed on logout, so the registry cannot grow without
// bound and a departed player's IP does not stay authorized forever.
public class UdpSessionCache
{
    public record ClientInfo(long UID, string Name, int PlayerRef, string Platform);

    private sealed class Claimable(ClientInfo info)
    {
        public readonly ClientInfo Info = info;
        public IPEndPoint? ClaimedBy;
    }

    private readonly Dictionary<IPAddress, List<Claimable>> _byIp = new();
    private readonly object _lock = new();

    public void Register(IPAddress clientIp, ClientInfo info)
    {
        lock (_lock)
        {
            if (!_byIp.TryGetValue(clientIp, out var list))
            {
                list = new List<Claimable>();
                _byIp[clientIp] = list;
            }
            list.RemoveAll(c => c.Info.UID == info.UID);
            list.Add(new Claimable(info));
        }
    }

    public void Register(string clientIp, ClientInfo info)
    {
        if (IPAddress.TryParse(clientIp, out var addr)) Register(addr, info);
    }

    public bool IsAuthorized(IPAddress clientIp)
    {
        lock (_lock) return _byIp.ContainsKey(clientIp);
    }

    // Bind a UDP endpoint to a player, claiming that identity so a second endpoint from the
    // same IP resolves to a different player. Idempotent for a given endpoint.
    public ClientInfo? Resolve(IPEndPoint ep)
    {
        lock (_lock)
        {
            if (!_byIp.TryGetValue(ep.Address, out var list) || list.Count == 0)
                return null;

            foreach (var c in list)
                if (Equals(c.ClaimedBy, ep)) return c.Info;

            foreach (var c in list)
                if (c.ClaimedBy is null) { c.ClaimedBy = ep; return c.Info; }

            // Every entry already claimed by another endpoint (not expected under correct
            // per-player registration) — return the last without minting a duplicate claim.
            return list[^1].Info;
        }
    }

    // Free the claim a closing UDP session held; the player stays registered for reconnect.
    public void ReleaseClaim(IPEndPoint ep)
    {
        lock (_lock)
        {
            if (!_byIp.TryGetValue(ep.Address, out var list)) return;
            foreach (var c in list)
                if (Equals(c.ClaimedBy, ep)) c.ClaimedBy = null;
        }
    }

    // Drop a player on logout; the IP is de-registered once its last player is gone.
    public void Remove(IPAddress clientIp, long uid)
    {
        lock (_lock)
        {
            if (!_byIp.TryGetValue(clientIp, out var list)) return;
            list.RemoveAll(c => c.Info.UID == uid);
            if (list.Count == 0) _byIp.Remove(clientIp);
        }
    }

    public void Remove(string clientIp, long uid)
    {
        if (IPAddress.TryParse(clientIp, out var addr)) Remove(addr, uid);
    }
}
