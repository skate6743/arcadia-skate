using Microsoft.Extensions.Logging;

// Append-only forensic record of every auto-kick (logs/kick-events.txt).
namespace Arcadia.Hosting.Lobby.Diagnostics
{
    public static class KickEventLog
    {
        private const string FilePath = "logs/kick-events.txt";

        public static void Append(string line, ILogger? logger = null)
        {
            EventFileWriter.Enqueue(FilePath, line);
        }
    }
}
