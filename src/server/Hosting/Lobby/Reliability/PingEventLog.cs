using Arcadia.Hosting.Lobby.Diagnostics;
using Microsoft.Extensions.Logging;

// Append-only forensic log for PING + catchup + phantom-drop events.
namespace Arcadia.Hosting.Lobby.Reliability
{
    public class PingEventLog
    {
        private const string FilePath = "logs/ping-events.txt";

        private readonly bool _enabled;

        public PingEventLog(ILogger logger, bool enabled)
        {
            _enabled = enabled;
        }

        public void Append(string line)
        {
            if (!_enabled) return;
            EventFileWriter.Enqueue(FilePath, line);
        }
    }
}
