using System.Text;
using Arcadia.EA;
using Arcadia.Storage;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Arcadia.Hosting;

public class DiscordHostedService(DiscordSocketClient client, ILogger<DiscordHostedService> logger, ConnectionManager sharedCache, IOptions<DiscordSettings> config) : BackgroundService
{
    private const string messageIdFile = "./messageId";

    private static readonly TimeSpan PeriodicUpdateInterval = TimeSpan.FromSeconds(10);
    private static readonly StringBuilder PlayersStringBuilder = new();

    private readonly DiscordSocketClient _client = client;
    private readonly ILogger<DiscordHostedService> _logger = logger;
    private readonly ConnectionManager _sharedCache = sharedCache;
    private readonly IOptions<DiscordSettings> _config = config;

    private readonly List<(IMessageChannel Channel, ulong StatusId)> _channelStatus = [];
    private readonly Dictionary<ulong, List<(long GID, ulong MessageId)>> _channelsGameMessage = [];

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_config.Value.EnableBot)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_config.Value.BotToken))
        {
            _logger.LogWarning("Discord bot not configured!");
            return;
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _client.Log += x =>
            {
                _logger.LogDebug("Discord.NET: {msg}", x.ToString());
                return Task.CompletedTask;
            };
        }

        _client.Ready += () =>
        {
            _logger.LogInformation("Discord bot connected & ready!");
            return Task.CompletedTask;
        };

        await _client.LoginAsync(TokenType.Bot, _config.Value.BotToken);
        await _client.StartAsync();

        await base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await GracefulShutdown();
        await _client.StopAsync();
        await _client.LogoutAsync();
        await base.StopAsync(cancellationToken);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return Task.Run(async () => {
            while (_client.ConnectionState != ConnectionState.Connected)
            {
                await Task.Delay(1000, stoppingToken);
            }

            await InitializeChannels();

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (_client.ConnectionState != ConnectionState.Connected)
                    {
                        _logger.LogWarning("Discord connection lost!");
                        await Task.Delay(5000, stoppingToken);
                        continue;
                    }

                    await UpdateGameStatus();
                    await Task.Delay(PeriodicUpdateInterval, stoppingToken);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Failed to update status!");
                }
            }
        }, stoppingToken);
    }

    private async Task GracefulShutdown()
    {
        foreach (var (channel, statusId) in _channelStatus)
        {
            try
            {
                await channel.ModifyMessageAsync(statusId, x =>
                {
                    x.Content = "Server offline!";
                    x.Embeds = null;
                });

                var gameMessages = _channelsGameMessage.GetValueOrDefault(channel.Id);
                if (gameMessages is null) continue;

                foreach (var (_, gameMessageId) in gameMessages)
                {
                    await channel.DeleteMessageAsync(gameMessageId);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to notify channel about shutdown!");
            }
        }

        _channelStatus.Clear();
        _channelsGameMessage.Clear();
    }

    private async Task InitializeChannels()
    {
        var channels = _config.Value.Channels;
        var cachedIds = await GetCachedStatusMessageIds();
        var flushCache = false;

        foreach (var channelId in channels)
        {
            if (await _client.GetChannelAsync(channelId) is not IMessageChannel channel)
            {
                _logger.LogError("Failed to open channel: {channelId}", channelId);
                continue;
            }

            var cacheHit = cachedIds.TryGetValue(channel.Id, out var messageId);
            var statusMsg = cacheHit ? await channel.GetMessageAsync(messageId) : await channel.SendMessageAsync("Initializing status...");
            if (statusMsg is null)
            {
                if (cacheHit)
                {
                    _logger.LogWarning("Cached message no longer exists, must manually remove cache line {channelId}:{messageId}", channelId, messageId);
                }

                _logger.LogError("Failed to acquire status message in channel:{channelId}. Messages will not be posted here.", channelId);
                continue;
            }
            else if (!cacheHit)
            {
                flushCache = true;
                cachedIds.Add(channelId, statusMsg.Id);
                _logger.LogInformation("New status created, msgId:{messageId}, chId:{channelId}", statusMsg.Id, channelId);
            }

            await foreach (var batch in channel.GetMessagesAsync())
            {
                foreach (var message in batch)
                {
                    if (message.Id != statusMsg.Id) await message.DeleteAsync();
                }
            }

            _channelStatus.Add((channel, statusMsg.Id));
        }

        if (flushCache)
        {
            await File.WriteAllLinesAsync(messageIdFile, cachedIds.Select(x => $"{x.Key}:{x.Value}"));
        }
    }

    private async Task<Dictionary<ulong, ulong>> GetCachedStatusMessageIds()
    {
        try
        {
            var text = await File.ReadAllLinesAsync(messageIdFile);
            var dict = new Dictionary<ulong, ulong>(text.Length);
            foreach (var line in text)
            {
                var parts = line.Split(':');
                dict.Add(ulong.Parse(parts[0]), ulong.Parse(parts[1]));
            }

            return dict;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to read messageId cache file.");
            return [];
        }
    }

    private async Task UpdateGameStatus()
    {
        var content = BuildGameStatusContent();

        foreach (var (channel, statusId) in _channelStatus)
        {
            try
            {
                await channel.ModifyMessageAsync(statusId, x =>
                {
                    x.Content = "\n";
                    x.Embed = new EmbedBuilder()
                            .WithTitle("Arcadia")
                            .WithDescription(content.StatusMessage)
                            .Build();
                });
            }
            catch (HttpException e)
            {
                _logger.LogError(e, "Failed to update channel status message, reason: {message}", e.Message);
            }

            var gameMessagesInChannel = _channelsGameMessage.GetValueOrDefault(channel.Id);
            if (gameMessagesInChannel is null)
            {
                gameMessagesInChannel = new(content.Games.Length);
                _channelsGameMessage.Add(channel.Id, gameMessagesInChannel);
            }
            else
            {
                var gidsToRemove = new List<long>(3);
                foreach (var postedGame in gameMessagesInChannel)
                {
                    try
                    {
                        if (content.Games.Any(x => x.GID == postedGame.GID)) continue;
                        _logger.LogDebug("Removing game listing, GID:{GID}", postedGame.GID);

                        gidsToRemove.Add(postedGame.GID);
                        await channel.DeleteMessageAsync(postedGame.MessageId);
                    }
                    catch (HttpException e)
                    {
                        _logger.LogError(e, "Failed to delete game server message, reason: {message}", e.Message);
                    }
                }

                gameMessagesInChannel.RemoveAll(x => gidsToRemove.Contains(x.GID));
            }

            try
            {
                for (var i = 0; i < content.Games.Length; i++)
                {
                    var game = content.Games[i];
                    var postedMsg = gameMessagesInChannel.FirstOrDefault(x => game.GID == x.GID);
                    if (postedMsg == default)
                    {
                        var gameMessage = await channel.SendMessageAsync("\n", embed: game.Embed);
                        gameMessagesInChannel.Add((game.GID, gameMessage.Id));
                        _logger.LogDebug("Server listing added, GID:{GID}", game.GID);
                    }
                    else
                    {
                        await channel.ModifyMessageAsync(postedMsg.MessageId, x =>
                        {
                            x.Content = "\n";
                            x.Embed = game.Embed;
                        });

                        _logger.LogDebug("Server listing updated, GID:{GID}", game.GID);
                    }
                }
            }
            catch (HttpException e)
            {
                _logger.LogError(e, "Failed to update game server messages, reason: {message}", e.Message);
            }
        }
    }

    private (string StatusMessage, (long GID, Embed Embed)[] Games) BuildGameStatusContent()
    {
        var hosts = _sharedCache.GetAllServersInternal();

        var gidEmbeds = new (long GID, Embed Embed)[hosts.Length];
        for (var i = 0; i < hosts.Length; i++)
        {
            var server = hosts[i];
            if (!server.CanJoin)
            {
                gidEmbeds[i].GID = 0;
                continue;
            }

            try
            {
                gidEmbeds[i] = BuildSkateStatus(server);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to build server status embedding: {Message}", e.Message);
            }
        }

        var embeds = gidEmbeds.Where(x => x.GID != 0).ToArray();

        const string statusFormat = "**{0}** ongoing game{1}";
        var gameCount = embeds.Length;
        var statusEnd = gameCount == 0 ? "s. 😞" : gameCount > 1 ? "s! 🔥" : "! ⭐";
        var statusMsg = string.Format(statusFormat, gameCount, statusEnd);

        return (statusMsg, embeds);
    }

    private static string GetPlayerCountString(GameServerListing server)
    {
        var maxPlayers = server.Data["MAX-PLAYERS"];

        if (server.ConnectedCount == 0)
        {
            return $"0/{maxPlayers}";
        }

        PlayersStringBuilder.Clear();
        PlayersStringBuilder
            .Append(server.ConnectedCount)
            .Append('/')
            .Append(maxPlayers)
            .Append(" | ")
            .AppendJoin(", ", server.ConnectedPlayers.Select(x => x.NAME));

        return PlayersStringBuilder.ToString();
    }

    private static (long GID, Embed Embed) BuildSkateStatus(GameServerListing server)
    {
        var eb = new EmbedBuilder()
            .WithTitle($"**{server.NAME}**")
            .AddField("Players", GetPlayerCountString(server))
            .WithTimestamp(server.StartedAt);

        return (server.GID, eb.Build());
    }
}
