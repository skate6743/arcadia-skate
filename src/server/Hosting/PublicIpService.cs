using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Resolves the public IPv4 the server advertises to clients.
namespace Arcadia.Hosting
{
    public interface IPublicIpProvider
    {
        string CurrentAddress { get; }
    }

    public class PublicIpService : IPublicIpProvider, IHostedService, IDisposable
    {
        private static readonly string[] ProbeEndpoints =
        {
            "https://api.ipify.org",
            "https://ifconfig.me/ip",
            "https://icanhazip.com",
        };

        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);

        private readonly ILogger<PublicIpService> _logger;
        private readonly IOptions<ArcadiaSettings> _settings;
        private readonly HttpClient _http;
        private string _current = string.Empty;

        public string CurrentAddress => _current;

        public PublicIpService(ILogger<PublicIpService> logger, IOptions<ArcadiaSettings> settings)
        {
            _logger = logger;
            _settings = settings;
            _http = new HttpClient { Timeout = ProbeTimeout };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("arcadia-skate/1.0 (+public-ip-probe)");
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            string configured = _settings.Value.PublicAddress ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(configured))
            {
                _current = configured.Trim();
                _logger.LogInformation(
                    "PublicIp: using explicit override {ip} from ArcadiaSettings.PublicAddress",
                    _current);
                return;
            }

            foreach (string endpoint in ProbeEndpoints)
            {
                try
                {
                    string raw = await _http.GetStringAsync(endpoint, cancellationToken);
                    string trimmed = raw.Trim();
                    if (IPAddress.TryParse(trimmed, out IPAddress? parsed)
                        && parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        _current = trimmed;
                        _logger.LogInformation("PublicIp: detected {ip} via {endpoint}", _current, endpoint);
                        return;
                    }
                    _logger.LogWarning(
                        "PublicIp: {endpoint} returned non-IPv4 body (len={len}); trying next",
                        endpoint, trimmed.Length);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "PublicIp: probe {endpoint} failed; trying next", endpoint);
                }
            }

            _logger.LogError(
                "PublicIp: every probe in the fallback chain failed and no explicit " +
                "ArcadiaSettings.PublicAddress is configured. FESL/Theater responses will " +
                "advertise an empty IP and clients will be unable to reach the UDP relay. " +
                "Set ArcadiaSettings.PublicAddress in appsettings.json as a workaround.");
            _current = string.Empty;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose() => _http.Dispose();
    }
}
