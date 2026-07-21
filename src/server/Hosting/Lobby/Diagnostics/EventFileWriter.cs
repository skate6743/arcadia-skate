using System.Collections.Concurrent;

// Single owned writer thread for the forensic event files; a full queue drops writes.
namespace Arcadia.Hosting.Lobby.Diagnostics
{
    internal static class EventFileWriter
    {
        private static readonly BlockingCollection<(string Path, string Line)> _queue = new(boundedCapacity: 4096);

        static EventFileWriter()
        {
            var writer = new Thread(WriteLoop) { IsBackground = true, Name = "event-file-writer" };
            writer.Start();
        }

        public static void Enqueue(string path, string line) => _queue.TryAdd((path, line));

        private static void WriteLoop()
        {
            foreach (var (path, line) in _queue.GetConsumingEnumerable())
            {
                try
                {
                    Directory.CreateDirectory("logs");
                    File.AppendAllText(path, line);
                }
                catch { }
            }
        }
    }
}
