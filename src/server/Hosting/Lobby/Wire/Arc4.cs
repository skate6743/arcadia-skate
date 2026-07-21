// DirtySDK CryptArc4 (RC4).
namespace Arcadia.Hosting.Lobby.Wire
{
    public class Arc4
    {
        private readonly byte[] _state = new byte[256];
        private int _x;
        private int _y;

        public void Init(ReadOnlySpan<byte> key, int iSchedule)
        {
            if (key.Length == 0)
                throw new ArgumentException("key must be non-empty", nameof(key));

            for (int i = 0; i < 256; i++)
                _state[i] = (byte)i;
            _x = 0;
            _y = 0;

            int passes = iSchedule < 1 ? 1 : iSchedule;
            for (int pass = 0; pass < passes; pass++)
            {
                int j = 0;
                for (int i = 0; i < 256; i++)
                {
                    j = (j + _state[i] + key[i % key.Length]) & 0xFF;
                    byte tmp = _state[i];
                    _state[i] = _state[j];
                    _state[j] = tmp;
                }
            }
        }

        public void Advance(int length)
        {
            for (int k = 0; k < length; k++)
            {
                _x = (_x + 1) & 0xFF;
                _y = (_y + _state[_x]) & 0xFF;
                byte tmp = _state[_x];
                _state[_x] = _state[_y];
                _state[_y] = tmp;
            }
        }

        public void Apply(Span<byte> buf)
        {
            for (int k = 0; k < buf.Length; k++)
            {
                _x = (_x + 1) & 0xFF;
                _y = (_y + _state[_x]) & 0xFF;
                byte tmp = _state[_x];
                _state[_x] = _state[_y];
                _state[_y] = tmp;
                buf[k] ^= _state[(_state[_x] + _state[_y]) & 0xFF];
            }
        }

        public void CopyStateTo(Span<byte> destState, out int outX, out int outY)
        {
            if (destState.Length < 256)
                throw new ArgumentException("destState must be at least 256 bytes", nameof(destState));
            _state.AsSpan(0, 256).CopyTo(destState);
            outX = _x;
            outY = _y;
        }

        public void RestoreStateFrom(ReadOnlySpan<byte> srcState, int srcX, int srcY)
        {
            if (srcState.Length < 256)
                throw new ArgumentException("srcState must be at least 256 bytes", nameof(srcState));
            srcState[..256].CopyTo(_state);
            _x = srcX;
            _y = srcY;
        }
    }
}
