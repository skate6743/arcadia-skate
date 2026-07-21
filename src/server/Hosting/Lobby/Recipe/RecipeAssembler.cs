// Reassembles inbound Skate 2 MT_GameRecipeData chunks into one blob per uploader.
namespace Arcadia.Hosting.Lobby.Recipe
{
    public class RecipeAssembler
    {
        public int DeclaredSize { get; }
        public uint Crc { get; }
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset LastChunkAt { get; private set; } = DateTimeOffset.UtcNow;

        public int ReceivedBytes => _receivedBytes;

        private readonly Dictionary<int, byte[]> _chunks = new Dictionary<int, byte[]>();
        private int _receivedBytes;

        public RecipeAssembler(int declaredSize, uint crc)
        {
            DeclaredSize = declaredSize;
            Crc = crc;
        }

        public bool AddChunk(int index, byte[] chunk)
        {
            if (_chunks.ContainsKey(index))
                return _receivedBytes >= DeclaredSize;
            _chunks[index] = chunk;
            _receivedBytes += chunk.Length;
            LastChunkAt = DateTimeOffset.UtcNow;
            return _receivedBytes >= DeclaredSize;
        }

        public byte[]? Build()
        {
            if (_receivedBytes < DeclaredSize) return null;

            byte[] blob = new byte[DeclaredSize];
            int off = 0;
            foreach (var kv in _chunks.OrderBy(x => x.Key))
            {
                int take = Math.Min(kv.Value.Length, DeclaredSize - off);
                if (take <= 0) break;
                kv.Value.AsSpan(0, take).CopyTo(blob.AsSpan(off));
                off += take;
            }
            return blob;
        }
    }
}
