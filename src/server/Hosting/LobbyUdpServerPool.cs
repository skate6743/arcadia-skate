using System.Collections.Concurrent;
using System.Net;
using Arcadia.EA;
using Arcadia.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Allocates per-lobby UDP servers on (BasePort + LobbyId) with LobbyId in [1, MaxLobbies].
namespace Arcadia.Hosting
{
    public class LobbyUdpServerPool : IAsyncDisposable
    {
        private readonly ILogger<LobbyUdpServerPool> _logger;
        private readonly ILoggerFactory _loggerFactory;
        private readonly LobbySettings _settings;
        private readonly DebugSettings _debugSettings;
        private readonly UdpSessionCache _udpCache;
        private readonly RecipeBlobStore _blobs;

        private readonly ConcurrentDictionary<int, LobbyUdpServer> _byLobbyId = new ConcurrentDictionary<int, LobbyUdpServer>();
        private int _cursor;

        public LobbyUdpServerPool(
            ILogger<LobbyUdpServerPool> logger,
            ILoggerFactory loggerFactory,
            IOptions<LobbySettings> settings,
            IOptions<DebugSettings> debugSettings,
            UdpSessionCache udpCache,
            RecipeBlobStore blobs)
        {
            _logger = logger;
            _loggerFactory = loggerFactory;
            _settings = settings.Value;
            _debugSettings = debugSettings.Value;
            _udpCache = udpCache;
            _blobs = blobs;
        }

        public LobbyUdpServer? Allocate(GameServerListing game)
        {
            if (!_settings.Enabled)
            {
                _logger.LogInformation("LobbyUdpServerPool disabled — skipping allocate for GID={gid}", game.GID);
                return null;
            }

            IPAddress bindAddress = IPAddress.Parse(_settings.ListenAddress);
            int basePort = _settings.BasePort;
            int maxLobbies = _settings.MaxLobbies;

            for (int attempt = 0; attempt < maxLobbies; attempt++)
            {
                int next = Interlocked.Increment(ref _cursor);
                int lobbyId = ((next - 1) % maxLobbies) + 1;
                if (_byLobbyId.ContainsKey(lobbyId)) continue;

                int port = basePort + lobbyId;
                try
                {
                    game.LobbyId = lobbyId;
                    game.UdpPort = port;

                    ILogger<LobbyUdpServer> serverLogger = _loggerFactory.CreateLogger<LobbyUdpServer>();
                    LobbyUdpServer server = new LobbyUdpServer(
                        serverLogger, game, bindAddress, port, _settings.EKey, _udpCache, _blobs,
                        _debugSettings.EnableFileLogging);

                    if (!_byLobbyId.TryAdd(lobbyId, server))
                    {
                        server.DisposeAsync().AsTask().Wait();
                        continue;
                    }

                    server.Start();

                    _logger.LogInformation(
                        "LobbyUdpServerPool allocated lobbyId={lobbyId} port={port} for GID={gid} variant={variant}",
                        lobbyId, port, game.GID, game.Variant);
                    return server;
                }
                catch (Exception e)
                {
                    _logger.LogWarning(e, "LobbyUdpServerPool lobbyId={lobbyId} port={port} bind failed, probing next",
                        lobbyId, port);
                }
            }

            _logger.LogError("LobbyUdpServerPool exhausted — no free ports for GID={gid}", game.GID);
            return null;
        }

        public async Task ReleaseAsync(int lobbyId)
        {
            if (_byLobbyId.TryRemove(lobbyId, out LobbyUdpServer? server))
            {
                _logger.LogInformation("LobbyUdpServerPool releasing lobbyId={lobbyId} port={port}",
                    lobbyId, server.Port);
                await server.DisposeAsync();
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var (_, server) in _byLobbyId)
            {
                try { await server.DisposeAsync(); } catch { }
            }
            _byLobbyId.Clear();
        }
    }
}
