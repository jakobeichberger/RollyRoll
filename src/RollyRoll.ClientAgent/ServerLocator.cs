using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RollyRoll.ClientAgent;

/// <summary>
/// Auto-discovers the RollyRoll server without manual configuration.
/// Discovery order: UDP broadcast (port 5150) -> DNS SRV (_rollyroll._tcp) -> last-known IP from config.
/// Caches the result and re-discovers if the server becomes unreachable.
/// </summary>
public class ServerLocator
{
    private readonly ILogger<ServerLocator> _logger;
    private string? _cachedServerUrl;
    private readonly string _configPath;

    private const int DiscoveryPort = 5150;
    private const int BroadcastTimeoutMs = 3000;

    public ServerLocator(ILogger<ServerLocator> logger)
    {
        _logger = logger;
        _configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "RollyRoll", "server.json");
    }

    /// <summary>Get the server base URL, discovering it if necessary.</summary>
    public async Task<string> GetServerUrlAsync(CancellationToken ct = default)
    {
        if (_cachedServerUrl != null && await IsServerReachableAsync(_cachedServerUrl, ct))
            return _cachedServerUrl;

        // Step 1: UDP broadcast discovery
        var broadcastResult = await DiscoverViaBroadcastAsync(ct);
        if (broadcastResult != null)
        {
            _cachedServerUrl = broadcastResult;
            await SaveLastKnownAsync(broadcastResult);
            _logger.LogInformation("Server found via broadcast: {Url}", broadcastResult);
            return broadcastResult;
        }

        // Step 2: DNS SRV lookup
        var dnsResult = await DiscoverViaDnsSrvAsync(ct);
        if (dnsResult != null)
        {
            _cachedServerUrl = dnsResult;
            await SaveLastKnownAsync(dnsResult);
            _logger.LogInformation("Server found via DNS SRV: {Url}", dnsResult);
            return dnsResult;
        }

        // Step 3: Last known address
        var lastKnown = await LoadLastKnownAsync();
        if (lastKnown != null && await IsServerReachableAsync(lastKnown, ct))
        {
            _cachedServerUrl = lastKnown;
            _logger.LogInformation("Server found via last-known: {Url}", lastKnown);
            return lastKnown;
        }

        throw new InvalidOperationException("Cannot discover RollyRoll server. Ensure server is running on the network.");
    }

    private async Task<string?> DiscoverViaBroadcastAsync(CancellationToken ct)
    {
        try
        {
            using var client = new UdpClient();
            client.EnableBroadcast = true;
            client.Client.ReceiveTimeout = BroadcastTimeoutMs;

            var request = Encoding.UTF8.GetBytes("ROLLYROLL_DISCOVER");
            await client.SendAsync(request, request.Length, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));

            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(BroadcastTimeoutMs);

            var result = await client.ReceiveAsync(cts.Token);
            var response = Encoding.UTF8.GetString(result.Buffer);

            if (response.StartsWith("ROLLYROLL_SERVER:"))
            {
                return response["ROLLYROLL_SERVER:".Length..];
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException)
        {
            _logger.LogDebug("Broadcast discovery: no response");
        }
        return null;
    }

    private async Task<string?> DiscoverViaDnsSrvAsync(CancellationToken ct)
    {
        try
        {
            // Try to resolve _rollyroll._tcp DNS SRV record
            var hostEntry = await Dns.GetHostEntryAsync("_rollyroll._tcp", ct);
            if (hostEntry.AddressList.Length > 0)
            {
                var ip = hostEntry.AddressList[0];
                return $"https://{ip}:443";
            }
        }
        catch
        {
            _logger.LogDebug("DNS SRV discovery: no record found");
        }
        return null;
    }

    private async Task<bool> IsServerReachableAsync(string url, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true });
            http.Timeout = TimeSpan.FromSeconds(5);
            var response = await http.GetAsync($"{url}/health", ct);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private async Task SaveLastKnownAsync(string url)
    {
        var dir = Path.GetDirectoryName(_configPath)!;
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(_configPath, JsonSerializer.Serialize(new { ServerUrl = url }));
    }

    private async Task<string?> LoadLastKnownAsync()
    {
        if (!File.Exists(_configPath)) return null;
        var json = await File.ReadAllTextAsync(_configPath);
        var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("ServerUrl", out var prop) ? prop.GetString() : null;
    }
}
