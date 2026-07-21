using System.Collections.Concurrent;
using System.Threading;

namespace Arcadia.EA;

public enum GameVariant
{
    Skate1,
    Skate2,
}

public class PlasmaSession
{
    public IEAConnection? FeslConnection { get; set; }
    public IEAConnection? TheaterConnection { get; set; }
    public IEAConnection? MessengerConnection { get; set; }

    public int PID { get; set; }

    public required long UID { get; init; }
    public required string NAME { get; init; }
    public required string LKEY { get; init; }

    public required string ClientString { get; init; }
    public required string PartitionId { get; init; }

    public string? OnlinePlatformId { get; set; }
}

public class GameServerListing
{
    public IEAConnection? TheaterConnection { get; set; }
    public ConcurrentDictionary<string, string> Data { get; init; } = new();

    public required string PartitionId { get; init; }
    public required string Platform { get; init; }
    public string? OnlinePlatform { get; init; }

    public long UID { get; set; }
    public long GID { get; init; }
    public int LID { get; init; }

    public int LobbyId { get; set; }
    public int UdpPort { get; set; }
    public GameVariant Variant { get; set; } = GameVariant.Skate2;

    public bool IsPrivate { get; set; }
    public string ChallengeKey { get; set; } = string.Empty;

    public string UGID { get; init; } = string.Empty;
    public string SECRET { get; init; } = string.Empty;
    public string EKEY { get; init; } = string.Empty;
    public string NAME { get; init; } = string.Empty;

    private readonly ConcurrentQueue<PlasmaSession> _joining = new();
    private readonly ConcurrentDictionary<long, PlasmaSession> _connected = new();

    private int _nextPid;

    // All reads/writes via Interlocked.
    public long LatestJoinFinalizationIndex;

    private readonly object _slotLock = new();
    private readonly Dictionary<long, int> _slotByUid = new();
    private readonly SortedSet<int> _freeSlots = new();
    private int _nextSlot;

    public IEnumerable<PlasmaSession> ConnectedPlayers => _connected.Values;
    public int ConnectedCount => _connected.Count;
    public int JoiningCount => _joining.Count;

    private int _creatorResetSentGate;

    public int MaxPlayers =>
        Data.TryGetValue("MAX-PLAYERS", out var mp)
        && int.TryParse(mp, out var v) && v > 0
            ? v
            : (Variant == GameVariant.Skate2 ? 6 : 2);

    private int _inProgressGate;

    public bool InProgress
    {
        get => Volatile.Read(ref _inProgressGate) != 0;
        set => Interlocked.Exchange(ref _inProgressGate, value ? 1 : 0);
    }

    // Winner owns the gate and must clear InProgress when the flow ends.
    public bool TryStart() => Interlocked.CompareExchange(ref _inProgressGate, 1, 0) == 0;

    public bool CanJoin => Volatile.Read(ref _creatorResetSentGate) != 0;

    public void MarkCreatorResetSent() => Interlocked.Exchange(ref _creatorResetSentGate, 1);

    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;

    public bool IsEmpty => _connected.IsEmpty && _joining.IsEmpty;
    public bool IsHostedBy(PlasmaSession session) => UID == session.UID;

    public int AllocateJoinerPid() => Interlocked.Increment(ref _nextPid);

    private readonly object _slotReservationLock = new();

    // Only path that adds to _joining; any other enqueue path bypasses the capacity cap.
    public bool TryReserveJoiningSlot(PlasmaSession session, int maxPlayers)
    {
        lock (_slotReservationLock)
        {
            if (ConnectedCount + JoiningCount >= maxPlayers) return false;
            _joining.Enqueue(session);
            return true;
        }
    }

    public bool TryPromoteJoining(out PlasmaSession? session)
    {
        lock (_slotReservationLock)
        {
            if (_joining.TryDequeue(out session))
            {
                _connected.TryAdd(session.UID, session);
                return true;
            }
            return false;
        }
    }

    public int RemoveJoiningPlayer(long uid)
    {
        lock (_slotReservationLock)
        {
            if (_joining.IsEmpty) return 0;

            var snapshot = new List<PlasmaSession>();
            while (_joining.TryDequeue(out var s)) snapshot.Add(s);

            var removed = 0;
            foreach (var s in snapshot)
            {
                if (s.UID == uid) { removed++; continue; }
                _joining.Enqueue(s);
            }
            return removed;
        }
    }

    public bool RemoveConnectedPlayer(long uid) => _connected.TryRemove(uid, out _);
    public PlasmaSession? FindConnectedByPid(int pid) => _connected.Values.FirstOrDefault(p => p.PID == pid);
    public bool HasConnectedPlayer(long uid) => _connected.ContainsKey(uid);

    public int AllocateSlot(long uid)
    {
        lock (_slotLock)
        {
            if (_slotByUid.TryGetValue(uid, out var existing)) return existing;

            int slot;
            if (_freeSlots.Count > 0)
            {
                slot = _freeSlots.Min;
                _freeSlots.Remove(slot);
            }
            else
            {
                slot = _nextSlot++;
            }
            _slotByUid[uid] = slot;
            return slot;
        }
    }

    public void ReleaseSlot(long uid)
    {
        lock (_slotLock)
        {
            if (_slotByUid.Remove(uid, out var s))
            {
                _freeSlots.Add(s);
            }
        }
    }

    public void CompactSlots()
    {
        lock (_slotLock)
        {
            if (_freeSlots.Count == 0) return;

            var ordered = new List<KeyValuePair<long, int>>(_slotByUid);
            ordered.Sort((a, b) => a.Value.CompareTo(b.Value));

            _slotByUid.Clear();
            for (int i = 0; i < ordered.Count; i++)
                _slotByUid[ordered[i].Key] = i;

            _freeSlots.Clear();
            _nextSlot = ordered.Count;
        }
    }
}
