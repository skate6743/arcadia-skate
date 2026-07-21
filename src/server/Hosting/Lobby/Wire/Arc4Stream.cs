// Persistent RC4 stream; a secondary "OOP" state recovers wire-reordered packets.
namespace Arcadia.Hosting.Lobby.Wire
{
    public class Arc4Stream
    {
        public const int WrapThreshold = 32768;

        private readonly Arc4 _rc4 = new Arc4();

        private readonly byte[] _snapState = new byte[256];
        private int _snapX;
        private int _snapY;
        private ushort _snapWireCounter;
        private uint _snapHighWord;
        private bool _initialized;

        private readonly Arc4 _oopRc4 = new Arc4();
        private readonly byte[] _oopCopyBuf = new byte[256];
        private ushort _oopWireCounter;
        private uint _oopHighWord;
        private bool _oopInitialized;

        public ushort LastWireCounter;
        public uint HighWord;
        public long StaleInEpochPackets;
        public long StaleAcrossWrapPackets;
        public long OopRecoveredPackets;

        public bool Initialized => _initialized;
        public uint EffectiveCounter => ((uint)HighWord << 16) | LastWireCounter;

        public void Init(ReadOnlySpan<byte> key)
        {
            _rc4.Init(key, 1);
            _oopRc4.Init(key, 1);
            LastWireCounter = 0;
            HighWord = 0;
            _oopWireCounter = 0;
            _oopHighWord = 0;
            _oopInitialized = false;
            _initialized = true;
        }

        private void SaveOopFromCurrentPrimary()
        {
            _rc4.CopyStateTo(_oopCopyBuf, out int x, out int y);
            _oopRc4.RestoreStateFrom(_oopCopyBuf, x, y);
            _oopWireCounter = LastWireCounter;
            _oopHighWord = HighWord;
            _oopInitialized = true;
        }

        public void Advance(int bytes) => _rc4.Advance(bytes);
        public void Apply(Span<byte> data) => _rc4.Apply(data);

        public void TakeSnapshot()
        {
            _rc4.CopyStateTo(_snapState, out _snapX, out _snapY);
            _snapWireCounter = LastWireCounter;
            _snapHighWord = HighWord;
        }

        public void RestoreSnapshot()
        {
            _rc4.RestoreStateFrom(_snapState, _snapX, _snapY);
            LastWireCounter = _snapWireCounter;
            HighWord = _snapHighWord;
        }

        public enum AdvanceResult
        {
            Forward,
            ForwardWrap,
            StaleInEpoch,
            StaleAcrossWrap,
        }

        public AdvanceResult TryAdvanceToCounter(ushort newCounter)
        {
            int delta = (int)newCounter - (int)LastWireCounter;
            if (delta == 0)
                return AdvanceResult.Forward;

            if (delta > 0)
            {
                if (delta > WrapThreshold && HighWord > 0)
                {
                    StaleAcrossWrapPackets++;
                    return AdvanceResult.StaleAcrossWrap;
                }
                if (delta > 1)
                    SaveOopFromCurrentPrimary();
                _rc4.Advance(4 * delta);
                LastWireCounter = newCounter;
                return AdvanceResult.Forward;
            }

            if (delta > -WrapThreshold)
            {
                StaleInEpochPackets++;
                return AdvanceResult.StaleInEpoch;
            }

            SaveOopFromCurrentPrimary();
            int advanceAmount = delta + 0x10000;
            _rc4.Advance(4 * advanceAmount);
            HighWord++;
            LastWireCounter = newCounter;
            return AdvanceResult.ForwardWrap;
        }

        public bool TryAdvanceOopToCounter(ushort newCounter)
        {
            if (!_oopInitialized) return false;
            if (_oopHighWord != HighWord) return false;
            int delta = (int)newCounter - (int)_oopWireCounter;
            if (delta <= 0) return false;
            int primaryDelta = (int)LastWireCounter - (int)_oopWireCounter;
            if (primaryDelta <= 0) return false;
            if (delta >= primaryDelta) return false;
            _oopRc4.Advance(4 * delta);
            _oopWireCounter = newCounter;
            return true;
        }

        public void ApplyOop(Span<byte> data) => _oopRc4.Apply(data);

        public void RealignOop(int bytesApplied)
        {
            int slots = bytesApplied >> 2;
            int remainder = bytesApplied & 3;
            if (remainder != 0)
            {
                _oopRc4.Advance(4 - remainder);
                slots++;
            }
            int newCounter = _oopWireCounter + slots;
            if (newCounter > 0xFFFF)
                _oopHighWord++;
            _oopWireCounter = (ushort)newCounter;
        }

        public void Realign(int bytesApplied)
        {
            int slots = bytesApplied >> 2;
            int remainder = bytesApplied & 3;
            if (remainder != 0)
            {
                _rc4.Advance(4 - remainder);
                slots++;
            }
            int newCounter = LastWireCounter + slots;
            if (newCounter > 0xFFFF)
                HighWord++;
            LastWireCounter = (ushort)newCounter;
        }
    }
}
