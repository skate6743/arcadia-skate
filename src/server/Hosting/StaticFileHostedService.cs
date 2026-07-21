using System.Net;
using System.Text.Json;
using Arcadia.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Arcadia.Hosting;

public class StaticFileHostedService(IOptions<FileServerSettings> settings, IOptions<Skate2Settings> skate2Settings, ILogger<StaticFileHostedService> logger, UdpSessionCache udpSessionCache, ConnectionManager connectionManager) : IHostedService
{
    private readonly ILogger<StaticFileHostedService> _logger = logger;
    private readonly FileServerSettings _settings = settings.Value;
    private readonly Skate2Settings _skate2 = skate2Settings.Value;
    private readonly UdpSessionCache _udpSessionCache = udpSessionCache;
    private readonly ConnectionManager _connectionManager = connectionManager;
    private readonly HttpListener _httpListener = new();

    private string _absoluteRootPath = string.Empty;

    private static readonly JsonSerializerOptions StatsJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_settings.EnableCdn)
        {
            return Task.CompletedTask;
        }

        _absoluteRootPath = Path.GetFullPath(_settings.ContentRoot);
        if(!_absoluteRootPath.EndsWith('/'))
        {
            _absoluteRootPath += '/';
        }

        try
        {
            string prefix = $"http://*:{_settings.Port}/";
            _httpListener.Prefixes.Add(prefix);
            _httpListener.Start();

            Task.Run(() => StartListening(cancellationToken), cancellationToken);

            _logger.LogInformation("File server '{prefix}' listening at root path: {path}", prefix, _absoluteRootPath);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "CDN host failed to start: {}", e.Message);
        }

        return Task.CompletedTask;
    }

    private async Task StartListening(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _httpListener.GetContextAsync();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                if (cancellationToken.IsCancellationRequested) return;
                _logger.LogError(e, "File server accept failed, listener stopped");
                return;
            }
            _ = Task.Run(() => HandleRequestAsync(context), cancellationToken);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        try
        {
            await HandleRequestCoreAsync(context);
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "File server request failed: {path}", context.Request?.RawUrl);
            try { context.Response.Abort(); } catch { }
        }
    }

    private async Task HandleRequestCoreAsync(HttpListenerContext context)
    {
        var requestPath = context.Request?.Url?.AbsolutePath.TrimStart('/');
        if (string.IsNullOrWhiteSpace(requestPath))
        {
            context.Response.StatusCode = (int)HttpStatusCode.NotFound;
            context.Response.Close();
            return;
        }

        if (requestPath.EndsWith("serverstats", StringComparison.OrdinalIgnoreCase))
        {
            await ServeServerStatsAsync(context);
            return;
        }

        if (requestPath.EndsWith("RemoteConfig.xml", StringComparison.OrdinalIgnoreCase))
        {
            await ServeRemoteConfigAsync(context, requestPath);
            return;
        }

        if (requestPath.EndsWith("GetProfile", StringComparison.OrdinalIgnoreCase))
        {
            await ServeGetProfileAsync(context, requestPath);
            return;
        }

        if (requestPath.EndsWith("GetOverallRanks", StringComparison.OrdinalIgnoreCase))
        {
            await ServeGetOverallRanksAsync(context, requestPath);
            return;
        }

        if (requestPath.EndsWith("GetBestOverallRank", StringComparison.OrdinalIgnoreCase))
        {
            await ServeGetBestOverallRankAsync(context, requestPath);
            return;
        }

        var filePath = GetFullPath(requestPath);
        var contentType = GetContentType(filePath);

        if (filePath is not null && contentType is not null && File.Exists(filePath))
        {
            byte[] fileBytes = await File.ReadAllBytesAsync(filePath);
            context.Response.ContentType = contentType;
            context.Response.ContentLength64 = fileBytes.Length;
            await context.Response.OutputStream.WriteAsync(fileBytes);
        }
        else
        {
            _logger.LogTrace("Returning 404 for request: {reqPath}", requestPath);
            context.Response.StatusCode = (int)HttpStatusCode.NotFound;
        }

        context.Response.Close();
    }

    private async Task ServeServerStatsAsync(HttpListenerContext context)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(_connectionManager.GetStats(), StatsJsonOptions);
        context.Response.Headers["Cache-Control"] = "no-cache";
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private async Task ServeRemoteConfigAsync(HttpListenerContext context, string requestPath)
    {
        string body =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<Config>\n" +
            "  <GamePlaySettings>\n" +
            $"  <SendRate>{_skate2.LockstepInputRateHz}</SendRate>\n" +
            "  </GamePlaySettings>\n" +
            "</Config>\n";

        var bytes = System.Text.Encoding.UTF8.GetBytes(body);
        _logger.LogInformation(
            "Served RemoteConfig.xml ({len} bytes) to {ep} for path '{path}'",
            bytes.Length, context.Request?.RemoteEndPoint, requestPath);

        // Content-Type MUST be text/xml, not application/xml, or the game silently falls back to defaults.
        context.Response.Headers["Server"] = "Microsoft-IIS/6.0";
        context.Response.Headers["X-Powered-By"] = "ASP.NET";
        context.Response.Headers["Accept-Ranges"] = "bytes";
        context.Response.Headers["Cache-Control"] = "no-cache";
        context.Response.ContentType = "text/xml";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private async Task ServeGetProfileAsync(HttpListenerContext context, string requestPath)
    {
        var userIdParam = context.Request?.QueryString["userId"];
        long userId = long.TryParse(userIdParam, out var parsed) ? parsed : 1000000000001L;
        var fesl = _connectionManager.FindSessionByUID(userId);
        string userName = fesl?.NAME ?? "Custom Server";
        string lookupSource = fesl is not null ? "fesl" : (userIdParam is not null ? "queryOnly" : "default");

        var body =
            "<?xml version=\"1.0\"?>\n" +
            "<ProfileInfo>\n" +
            $"  <userId>{userId}</userId>\n" +
            $"  <userName>{System.Security.SecurityElement.Escape(userName)}</userName>\n" +
            "  <totalFileSize>0</totalFileSize>\n" +
            "  <totalSpotSize>0</totalSpotSize>\n" +
            "  <videosSharing>0</videosSharing>\n" +
            "  <photosSharing>0</photosSharing>\n" +
            "  <spotsSharing>0</spotsSharing>\n" +
            "  <userMotto />\n" +
            "</ProfileInfo>\n";

        var bytes = System.Text.Encoding.UTF8.GetBytes(body);
        _logger.LogInformation(
            "Served GetProfile.xml ({len} bytes) to {ep} for path '{path}' — userId={uid} userName='{name}' (source={src})",
            bytes.Length, context.Request?.RemoteEndPoint, requestPath,
            userId, userName, lookupSource);

        context.Response.Headers["Server"] = "Microsoft-IIS/6.0";
        context.Response.Headers["X-Powered-By"] = "ASP.NET";
        context.Response.Headers["Accept-Ranges"] = "bytes";
        context.Response.Headers["Cache-Control"] = "no-cache";
        context.Response.ContentType = "text/xml";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private async Task ServeGetOverallRanksAsync(HttpListenerContext context, string requestPath)
    {
        const string body =
            "<?xml version=\"1.0\"?>\n" +
            "<ProfileOverallRankInfo>\n" +
            "  <userId>0</userId>\n" +
            "</ProfileOverallRankInfo>\n";

        var bytes = System.Text.Encoding.UTF8.GetBytes(body);
        _logger.LogInformation(
            "Served GetOverallRanks ({len} bytes) to {ep} for path '{path}'",
            bytes.Length, context.Request?.RemoteEndPoint, requestPath);

        context.Response.Headers["Server"] = "Microsoft-IIS/6.0";
        context.Response.Headers["X-Powered-By"] = "ASP.NET";
        context.Response.Headers["Accept-Ranges"] = "bytes";
        context.Response.Headers["Cache-Control"] = "no-cache";
        context.Response.ContentType = "text/xml";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private async Task ServeGetBestOverallRankAsync(HttpListenerContext context, string requestPath)
    {
        const string body =
            "<?xml version=\"1.0\"?>\n" +
            "<IntegerContainer>\n" +
            "  <value>0</value>\n" +
            "</IntegerContainer>\n";

        var bytes = System.Text.Encoding.UTF8.GetBytes(body);
        _logger.LogInformation(
            "Served GetBestOverallRank ({len} bytes) to {ep} for path '{path}'",
            bytes.Length, context.Request?.RemoteEndPoint, requestPath);

        context.Response.Headers["Server"] = "Microsoft-IIS/6.0";
        context.Response.Headers["X-Powered-By"] = "ASP.NET";
        context.Response.Headers["Accept-Ranges"] = "bytes";
        context.Response.Headers["Cache-Control"] = "no-cache";
        context.Response.ContentType = "text/xml";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private string? GetFullPath(string requestPath)
    {
        var filePath = Path.GetFullPath(Path.Combine(_absoluteRootPath, requestPath));
        var isValid = filePath.StartsWith(_absoluteRootPath, StringComparison.OrdinalIgnoreCase);
        return isValid ? filePath : null;
    }

    private static string? GetContentType(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".xml" => "application/xml",
            _ => null
        };
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _httpListener?.Stop();
        return Task.CompletedTask;
    }
}