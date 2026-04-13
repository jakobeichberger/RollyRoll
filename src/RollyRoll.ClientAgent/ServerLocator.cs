using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RollyRoll.ClientAgent;

/// <summary>
/// Auto-discovers the RollyRoll server using multiple strategies.
/// Discovery order:
///   1. UDP broadcast on port 5150 (same subnet)
///   2. DNS SRV lookup for _rollyroll._tcp (cross-subnet)
///   3. Last known server address from local config file (fallback)
/// Caches the result to avoid repeated discovery on every request.
/// </summary>
public class ServerLocator
{
    private readonly ILogger<ServerLocator> _logger;
    private readonly IConfiguration _configuration;
    private string? _cachedServerUrl;
    private DateTime _lastDiscovery = DateTime.MinValue;

    private static readonly TimeSpan CacheExpiry = TimeSpan.FromMinutes(30);
    private static readonly string LastKnownServerPath = Path.Combine(AppContext.BaseDirectory, "last_server.txt");

    private const int DiscoveryPort = 5150;
    private const int BroadcastTimeoutMs = 3000;

    public ServerLocator(ILogger<ServerLocator> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    /// <summary>
    /// Get the cached server URL, or null if not yet discovered.
    /// </summary>
    public string? GetCachedServerUrl() => _cachedServerUrl;

    /// <summary>
    /// Discover the RollyRoll server. Uses cache if available and not expired.
    /// </summary>
    public async Task<string> DiscoverAsync(CancellationToken ct = default)
    {
        // Return cached result if still valid
        if (_cachedServerUrl != null && DateTime.UtcNow - _lastDiscovery < CacheExpiry)
        {
            return _cachedServerUrl;
        }

        // Check configuration first (explicit override)
        var configuredUrl = _configuration.GetValue<string>("ServerUrl");
        if (!string.IsNullOrEmpty(configuredUrl))
        {
            _logger.LogInformation("Using configured server URL: {Url}", configuredUrl);
            CacheResult(configuredUrl);
            return configuredUrl;
        }

        // Strategy 1: UDP broadcast discovery
        var broadcastResult = await TryBroadcastDiscoveryAsync(ct);
        if (broadcastResult != null)
        {
            _logger.LogInformation("Server discovered via UDP broadcast: {Url}", broadcastResult);
            CacheResult(broadcastResult);
            return broadcastResult;
        }

        // Strategy 2: DNS SRV lookup
        var dnsResult = await TryDnsSrvDiscoveryAsync(ct);
        if (dnsResult != null)
        {
            _logger.LogInformation("Server discovered via DNS SRV: {Url}", dnsResult);
            CacheResult(dnsResult);
            return dnsResult;
        }

        // Strategy 3: Last known server address
        var lastKnown = await TryLastKnownServerAsync(ct);
        if (lastKnown != null)
        {
            _logger.LogInformation("Using last known server address: {Url}", lastKnown);
            CacheResult(lastKnown);
            return lastKnown;
        }

        throw new InvalidOperationException(
            "Unable to discover RollyRoll server. Tried: UDP broadcast, DNS SRV (_rollyroll._tcp), last known address. " +
            "Configure 'ServerUrl' in appsettings.json or ensure the server is reachable.");
    }

    /// <summary>
    /// Try to discover the server via UDP broadcast on port 5150.
    /// </summary>
    private async Task<string?> TryBroadcastDiscoveryAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogDebug("Attempting UDP broadcast discovery on port {Port}", DiscoveryPort);

            using var udpClient = new UdpClient();
            udpClient.EnableBroadcast = true;
            udpClient.Client.ReceiveTimeout = BroadcastTimeoutMs;

            var discoveryPayload = Encoding.UTF8.GetBytes("ROLLYROLL_DISCOVER");
            await udpClient.SendAsync(discoveryPayload, discoveryPayload.Length,
                new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(BroadcastTimeoutMs);

            try
            {
                var result = await udpClient.ReceiveAsync(timeoutCts.Token);
                var serverUrl = Encoding.UTF8.GetString(result.Buffer);

                if (Uri.TryCreate(serverUrl, UriKind.Absolute, out _))
                {
                    return serverUrl;
                }

                // Response might be just an IP — construct a URL
                return $"https://{result.RemoteEndPoint.Address}:5001";
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("UDP broadcast timed out after {Timeout}ms", BroadcastTimeoutMs);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "UDP broadcast discovery failed");
        }

        return null;
    }

    /// <summary>
    /// Try to discover the server via DNS SRV record lookup for _rollyroll._tcp.
    /// </summary>
    private async Task<string?> TryDnsSrvDiscoveryAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogDebug("Attempting DNS SRV lookup for _rollyroll._tcp");

            // Use nslookup to query SRV records (cross-platform compatible approach)
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "nslookup",
                Arguments = "-type=SRV _rollyroll._tcp",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null) return null;

            var output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            // Parse the SRV record response for host and port
            var lines = output.Split('\n');
            foreach (var line in lines)
            {
                if (line.Contains("svr hostname", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("target", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Split('=', StringSplitOptions.TrimEntries);
                    if (parts.Length >= 2)
                    {
                        var host = parts[1].TrimEnd('.');
                        return $"https://{host}:5001";
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DNS SRV discovery failed");
        }

        return null;
    }

    /// <summary>
    /// Try to use the last known server address saved from a previous successful discovery.
    /// Validates that the server is still reachable before returning.
    /// </summary>
    private async Task<string?> TryLastKnownServerAsync(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(LastKnownServerPath))
                return null;

            var serverUrl = (await File.ReadAllTextAsync(LastKnownServerPath, ct)).Trim();
            if (string.IsNullOrEmpty(serverUrl))
                return null;

            _logger.LogDebug("Testing last known server: {Url}", serverUrl);

            // Verify the server is reachable
            using var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(5)
            };

            var response = await httpClient.GetAsync($"{serverUrl}/api/health", ct);
            if (response.IsSuccessStatusCode)
            {
                return serverUrl;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Last known server check failed");
        }

        return null;
    }

    /// <summary>
    /// Cache the discovered server URL and persist it to disk for future use.
    /// </summary>
    private void CacheResult(string serverUrl)
    {
        _cachedServerUrl = serverUrl;
        _lastDiscovery = DateTime.UtcNow;

        // Persist to disk for fallback on next startup
        try
        {
            File.WriteAllText(LastKnownServerPath, serverUrl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist server URL to {Path}", LastKnownServerPath);
        }
    }
}
