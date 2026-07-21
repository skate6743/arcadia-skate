using System.Buffers.Binary;
using Arcadia.EA;
using Arcadia.Hosting.Lobby.Protocol;

// Strip GameSyncs from relay bodies bound for not-yet-in-world (loading) peers.
namespace Arcadia.Hosting.Lobby.Relay
{
    public static class LoadingGate
    {
        // Hot count-only path: ranges stays null so the walk allocates nothing.
        public static int CountGameSyncsFast(byte[] body, GameVariant variant)
            => Walk(body, variant, ranges: null);

        // Same walk, additionally capturing each message's (offset, length, isGameSync).
        public static (int GameSyncCount, List<(int Offset, int Length, bool IsGameSync)> Msgs) WalkRanges(byte[] body, GameVariant variant)
        {
            var ranges = new List<(int Offset, int Length, bool IsGameSync)>();
            int gsCount = Walk(body, variant, ranges);
            return (gsCount, ranges);
        }

        // Single source of truth for the Sk8 message walk. Returns the GameSync count;
        // when ranges is non-null, appends one entry per parsed message.
        private static int Walk(byte[] body, GameVariant variant, List<(int Offset, int Length, bool IsGameSync)>? ranges)
        {
            int attrSlotCount = Sk8MessageLayout.AttributeSlotCount(variant);
            int attrTrailerBytes = variant == GameVariant.Skate1
                ? Sk8MessageLayout.AttributeListSkate1LockTimeTrailerBytes : 0;
            int resetBodySize = variant == GameVariant.Skate1 ? 52 : 84;
            int grBodySize = variant == GameVariant.Skate1 ? 2 : 9;

            int gsCount = 0;
            int off = 0;
            while (off < body.Length)
            {
                byte op = body[off];

                if (variant == GameVariant.Skate1 && op == Sk8Opcodes.Skate1_Broadcasted && off + 9 < body.Length)
                {
                    off += 9;
                    continue;
                }

                Sk8Opcodes.Kind? kind = Sk8Opcodes.Decode(variant, op);
                int msgLen;
                bool isGameSync = false;

                if (kind == Sk8Opcodes.Kind.GameSync && variant == GameVariant.Skate1)
                {
                    if (off + 1 + 9 > body.Length) break;
                    byte cmdCount = body[off + 1 + 8];
                    int cur = off + 1 + 9;
                    bool ok = true;
                    for (int c = 0; c < cmdCount; c++)
                    {
                        if (cur + 3 > body.Length) { ok = false; break; }
                        byte dataSize = body[cur + 2];
                        cur += 3 + dataSize;
                        if (cur > body.Length) { ok = false; break; }
                    }
                    if (!ok) break;
                    msgLen = cur - off;
                    gsCount++;
                    isGameSync = true;
                }
                else if (kind == Sk8Opcodes.Kind.GameSync)
                {
                    if (!GameSyncMessage.TryParse(body, off, out GameSyncHeader hdr)) break;
                    msgLen = hdr.TotalMessageBytes;
                    gsCount++;
                    isGameSync = true;
                }
                else if (kind == Sk8Opcodes.Kind.GameAttributes
                      || kind == Sk8Opcodes.Kind.GameResetAttributes)
                {
                    int probe = Sk8MessageLayout.ProbeAttributeListLength(body, off + 1, attrSlotCount, Sk8MessageLayout.AttributeListMaxBytesPerSlot);
                    if (probe < 0) break;
                    int trailer = (kind == Sk8Opcodes.Kind.GameAttributes) ? attrTrailerBytes : 0;
                    msgLen = 1 + probe + trailer;
                    if (off + msgLen > body.Length) break;
                }
                else if (kind == Sk8Opcodes.Kind.GameAttributeUpdate && variant == GameVariant.Skate2)
                {
                    if (off + 9 > body.Length) break;
                    int sLen = (int)BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(off + 5, 4));
                    if (off + 9 + sLen > body.Length) break;
                    msgLen = 9 + sLen;
                }
                else if (kind == Sk8Opcodes.Kind.GameFinalResults && variant == GameVariant.Skate1)
                {
                    msgLen = 1 + Sk8MessageLayout.FinalResultsSkate1FixedBodySize;
                    if (off + msgLen > body.Length) break;
                }
                else if (kind == Sk8Opcodes.Kind.GameFinalResults)
                {
                    if (off + 5 > body.Length) break;
                    int n = (int)BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(off + 1, 4));
                    msgLen = 5 + n * Sk8MessageLayout.FinalResultsPlayerStride;
                    if (off + msgLen > body.Length) break;
                }
                else if (kind == Sk8Opcodes.Kind.GameReset && off + 1 + resetBodySize <= body.Length)
                {
                    msgLen = 1 + resetBodySize;
                }
                else if (kind == Sk8Opcodes.Kind.GameRequest && off + 1 + grBodySize <= body.Length)
                {
                    msgLen = 1 + grBodySize;
                }
                else
                {
                    int fixedLen = Sk8MessageLayout.FixedBodySize(variant, op);
                    if (fixedLen < 0 || off + 1 + fixedLen > body.Length) break;
                    msgLen = 1 + fixedLen;
                }

                ranges?.Add((off, msgLen, isGameSync));
                off += msgLen;
            }
            return gsCount;
        }

        // Returns null = fallback to original (parse incomplete / no GameSync); empty = all-GameSync, suppress dst.
        public static byte[]? TryBuildGameSyncStrippedBody(byte[] body, IReadOnlyList<(int Offset, int Length, bool IsGameSync)> msgs)
        {
            if (msgs.Count == 0) return null;

            int lastEnd = msgs[msgs.Count - 1].Offset + msgs[msgs.Count - 1].Length;
            if (lastEnd != body.Length) return null;

            bool anyGameSync = false;
            int keepBytes = 0;
            foreach (var (_, len, isGs) in msgs)
            {
                if (isGs) anyGameSync = true;
                else keepBytes += len;
            }
            if (!anyGameSync) return null;
            if (keepBytes == 0) return Array.Empty<byte>();

            byte[] output = new byte[keepBytes];
            int off = 0;
            foreach (var (start, len, isGs) in msgs)
            {
                if (isGs) continue;
                Array.Copy(body, start, output, off, len);
                off += len;
            }
            return output;
        }
    }
}
