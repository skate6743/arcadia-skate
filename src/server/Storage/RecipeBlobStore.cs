using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Arcadia.Storage;

// Uploaded Skate recipe blobs, keyed by (UID, contentType), for the UDP recipe-serve pipeline.
public class RecipeBlobStore(ILogger<RecipeBlobStore> logger)
{
    // Wire value (AddBlob's `type` field), not the internal 5/6; store keys must match this or recipe lookups miss.
    public const int CT_Recipe = 16;

    public readonly record struct BlobEntry(byte[] Data, uint Crc);

    private readonly ILogger<RecipeBlobStore> _logger = logger;
    private readonly ConcurrentDictionary<(long Uid, int ContentType), BlobEntry> _blobs = new();

    public void Put(long uid, int contentType, byte[] data, uint crc = 0)
    {
        _blobs[(uid, contentType)] = new BlobEntry(data, crc);
        _logger.LogInformation("Recipe blob stored uid={Uid} type={Type} size={Size} crc=0x{Crc:X8}", uid, contentType, data.Length, crc);
    }

    public byte[]? Get(long uid, int contentType)
    {
        return _blobs.TryGetValue((uid, contentType), out var v) ? v.Data : null;
    }

    public BlobEntry? GetEntry(long uid, int contentType)
    {
        return _blobs.TryGetValue((uid, contentType), out var v) ? v : null;
    }

    public void Delete(long uid, int contentType)
    {
        _blobs.TryRemove((uid, contentType), out _);
        _logger.LogInformation("Recipe blob deleted uid={Uid} type={Type}", uid, contentType);
    }
}
