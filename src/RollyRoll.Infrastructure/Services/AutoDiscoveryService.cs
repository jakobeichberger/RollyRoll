using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RollyRoll.Core.Interfaces;

namespace RollyRoll.Infrastructure.Services;

/// <summary>
/// Handles automatic server-client discovery without manual configuration.
/// The server periodically broadcasts its presence via UDP on port 5150.
/// Clients discover the server through a three-tier fallback:
/// 1. UDP broadcast (same subnet) — instant, zero-config.
/// 2. DNS SRV record (_rollyroll._tcp) — cross-subnet, requires DNS admin setup.
/// 3. Last-known server IP — fallback if broadcast and DNS both fail.
/// </summary>
public class AutoDiscoveryService : IAutoDiscoveryService
{
    private readonly ILogger<AutoDiscoveryService> _logger;

    /// <summary>UDP port used for discovery broadcasts.</summary>
    private const int DiscoveryPort = 5150;

    /// <summary>Magic header to identify RollyRoll discovery packets.</summary>
    private const string DiscoveryMagic = "ROLLYROLL-DISCOVER";

    /// <summary>Magic header for server announcement broadcasts.</summary>
    private const string AnnounceMagic = "ROLLYROLL-SERVER";

    /// <summary>DNS SRV record service name for cross-subnet discovery.</summary>
    private const string DnsSrvServiceName = "_rollyroll._tcp";

    /// <summary>Interval between server presence broadcasts.</summary>
    private static readonly TimeSpan BroadcastInterval = TimeSpan.FromSeconds(30);

    /// <summary>Timeout for waiting for a broadcast response from the server.</summary>
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(5);

    /// <summary>File path for caching the last-known server IP for fallback.</summary>
    private const string LastKnownServerFile = "last_known_server.json";

    private CancellationTokenSource? _broadcastCts;
    private Task? _broadcastTask;
    private UdpClient? _broadcastListener;

    /// <summary>
    /// Initializes a new instance of the <see cref="AutoDiscoveryService"/> class.
    /// </summary>
    /// <param name="logger">Logger for discovery operations.</param>
    public AutoDiscoveryService(ILogger<AutoDiscoveryService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartBroadcastingAsync(CancellationToken ct = default)
    {
        if (_broadcastTask is not null)
        {
            _logger.LogWarning("Discovery broadcasting is already running.");
            return Task.CompletedTask;
        }

        _broadcastCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _broadcastTask = RunBroadcastLoopAsync(_broadcastCts.Token);

        _logger.LogInformation("Server discovery broadcasting started on UDP port {Port}.", DiscoveryPort);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopBroadcastingAsync(CancellationToken ct = default)
    {
        if (_broadcastCts is null || _broadcastTask is null)
        {
            _logger.LogWarning("Discovery broadcasting is not running.");
            return;
        }

        _logger.LogInformation("Stopping server discovery broadcasting.");

        await _broadcastCts.CancelAsync();

        try
        {
            // Give the broadcast loop time to exit gracefully
            await _broadcastTask.WaitAsync(TimeSpan.FromSeconds(5), ct);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Broadcast loop did not stop within timeout.");
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation propagates
        }
        finally
        {
            _broadcastListener?.Dispose();
            _broadcastListener = null;
            _broadcastCts.Dispose();
            _broadcastCts = null;
            _broadcastTask = null;
        }

        _logger.LogInformation("Server discovery broadcasting stopped.");
    }

    /// <inheritdoc />
    public async Task<ServerEndpoint?> DiscoverServerAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Starting server discovery...");

        // Tier 1: UDP broadcast (same subnet)
        var endpoint = await DiscoverViaBroadcastAsync(ct);
        if (endpoint is not null)
        {
            _logger.LogInformation("Server discovered via broadcast: {Address}:{Port}", endpoint.Address, endpoint.Port);
            await SaveLastKnownServerAsync(endpoint);
            return endpoint;
        }

        // Tier 2: DNS SRV record (cross-subnet)
        endpoint = await DiscoverViaDnsSrvAsync(ct);
        if (endpoint is not null)
        {
            _logger.LogInformation("Server discovered via DNS SRV: {Address}:{Port}", endpoint.Address, endpoint.Port);
            await SaveLastKnownServerAsync(endpoint);
            return endpoint;
        }

        // Tier 3: Last-known server IP (fallback)
        endpoint = await LoadLastKnownServerAsync();
        if (endpoint is not null)
        {
            _logger.LogInformation("Using last-known server: {Address}:{Port}", endpoint.Address, endpoint.Port);
            return endpoint;
        }

        _logger.LogWarning("Server discovery failed. No server found via broadcast, DNS SRV, or last-known IP.");
        return null;
    }

    /// <inheritdoc />
    public async Task RegisterDnsSrvRecordAsync(string serverHostname, int port, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverHostname);

        _logger.LogInformation(
            "Registering DNS SRV record: {Service} -> {Host}:{Port}",
            DnsSrvServiceName, serverHostname, port);

        // DNS SRV registration typically requires nsupdate or Active Directory DNS integration.
        // This method prepares the record details; actual registration depends on the DNS infrastructure.
        // For Active Directory environments, this could use the DnsClient API or invoke dnscmd.exe.

        var srvRecord = new
        {
            Service = DnsSrvServiceName,
            Target = serverHostname,
            Port = port,
            Priority = 0,
            Weight = 100
        };

        _logger.LogInformation(
            "DNS SRV record prepared: {Service} IN SRV {Priority} {Weight} {Port} {Target}",
            srvRecord.Service, srvRecord.Priority, srvRecord.Weight, srvRecord.Port, srvRecord.Target);

        // In a production environment, this would execute:
        // nsupdate or dnscmd /RecordAdd <zone> _rollyroll._tcp SRV <priority> <weight> <port> <target>
        await Task.CompletedTask;
    }

    /// <summary>
    /// Runs the continuous broadcast loop that announces server presence on the network.
    /// Sends periodic announcements and responds to client discovery requests.
    /// </summary>
    private async Task RunBroadcastLoopAsync(CancellationToken ct)
    {
        try
        {
            _broadcastListener = new UdpClient(DiscoveryPort);
            _broadcastListener.EnableBroadcast = true;

            // Run announcement and listener in parallel
            var announceTask = RunPeriodicAnnouncementAsync(ct);
            var listenTask = RunDiscoveryListenerAsync(ct);

            await Task.WhenAll(announceTask, listenTask);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Discovery broadcast loop encountered an error.");
        }
    }

    /// <summary>
    /// Periodically sends UDP broadcast packets announcing the server's presence.
    /// </summary>
    private async Task RunPeriodicAnnouncementAsync(CancellationToken ct)
    {
        using var sender = new UdpClient();
        sender.EnableBroadcast = true;

        var serverInfo = BuildServerAnnouncement();
        var payload = Encoding.UTF8.GetBytes(serverInfo);
        var broadcastEndpoint = new IPEndPoint(IPAddress.Broadcast, DiscoveryPort);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await sender.SendAsync(payload, payload.Length, broadcastEndpoint);
                _logger.LogDebug("Server presence broadcast sent.");
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.NetworkDown)
            {
                _logger.LogWarning("Network is down; skipping broadcast cycle.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send server presence broadcast.");
            }

            await Task.Delay(BroadcastInterval, ct);
        }
    }

    /// <summary>
    /// Listens for client discovery requests and responds with server information.
    /// </summary>
    private async Task RunDiscoveryListenerAsync(CancellationToken ct)
    {
        if (_broadcastListener is null) return;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _broadcastListener.ReceiveAsync(ct);
                var message = Encoding.UTF8.GetString(result.Buffer);

                if (message.StartsWith(DiscoveryMagic, StringComparison.Ordinal))
                {
                    _logger.LogInformation(
                        "Discovery request received from {Remote}.",
                        result.RemoteEndPoint);

                    // Respond directly to the requesting client
                    var response = Encoding.UTF8.GetBytes(BuildServerAnnouncement());
                    using var responder = new UdpClient();
                    await responder.SendAsync(response, response.Length, result.RemoteEndPoint);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in discovery listener.");
            }
        }
    }

    /// <summary>
    /// Attempts to discover the server by sending a UDP broadcast and waiting for a response.
    /// </summary>
    private async Task<ServerEndpoint?> DiscoverViaBroadcastAsync(CancellationToken ct)
    {
        try
        {
            using var client = new UdpClient();
            client.EnableBroadcast = true;

            var request = Encoding.UTF8.GetBytes(DiscoveryMagic);
            await client.SendAsync(request, request.Length, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(DiscoveryTimeout);

            try
            {
                var result = await client.ReceiveAsync(timeoutCts.Token);
                var response = Encoding.UTF8.GetString(result.Buffer);

                if (response.StartsWith(AnnounceMagic, StringComparison.Ordinal))
                {
                    return ParseServerAnnouncement(response, result.RemoteEndPoint);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Broadcast discovery timed out after {Timeout}s.", DiscoveryTimeout.TotalSeconds);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Broadcast discovery failed.");
        }

        return null;
    }

    /// <summary>
    /// Attempts to discover the server via DNS SRV record lookup for _rollyroll._tcp.
    /// </summary>
    private async Task<ServerEndpoint?> DiscoverViaDnsSrvAsync(CancellationToken ct)
    {
        try
        {
            // Use system DNS resolver to look up the SRV record.
            // In production, this would use a DNS client library (e.g., DnsClient.NET)
            // to query: _rollyroll._tcp.<domain> IN SRV
            var hostEntry = await Dns.GetHostEntryAsync(DnsSrvServiceName, ct);

            if (hostEntry.AddressList.Length > 0)
            {
                var address = hostEntry.AddressList[0];

                return new ServerEndpoint
                {
                    Address = address.ToString(),
                    Port = 5000, // Default; SRV record would provide the actual port
                    ServerName = hostEntry.HostName,
                    DiscoveryMethod = "dns-srv"
                };
            }
        }
        catch (SocketException)
        {
            _logger.LogDebug("DNS SRV lookup for {Service} failed (record not found).", DnsSrvServiceName);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DNS SRV discovery failed.");
        }

        return null;
    }

    /// <summary>
    /// Builds a server announcement payload string containing the server's identity and endpoints.
    /// </summary>
    private static string BuildServerAnnouncement()
    {
        var hostname = Dns.GetHostName();
        return $"{AnnounceMagic}|{hostname}|5000";
    }

    /// <summary>
    /// Parses a server announcement payload into a <see cref="ServerEndpoint"/>.
    /// </summary>
    private static ServerEndpoint? ParseServerAnnouncement(string announcement, IPEndPoint remoteEndpoint)
    {
        // Format: "ROLLYROLL-SERVER|<hostname>|<port>"
        var parts = announcement.Split('|');
        if (parts.Length < 3) return null;

        return new ServerEndpoint
        {
            Address = remoteEndpoint.Address.ToString(),
            Port = int.TryParse(parts[2], out var port) ? port : 5000,
            ServerName = parts[1],
            DiscoveryMethod = "broadcast"
        };
    }

    /// <summary>
    /// Persists the server endpoint to disk so clients can fall back to it on next startup.
    /// </summary>
    private async Task SaveLastKnownServerAsync(ServerEndpoint endpoint)
    {
        try
        {
            var json = JsonSerializer.Serialize(endpoint, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(LastKnownServerFile, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save last-known server endpoint.");
        }
    }

    /// <summary>
    /// Loads the previously saved server endpoint from disk for last-known fallback.
    /// </summary>
    private static async Task<ServerEndpoint?> LoadLastKnownServerAsync()
    {
        try
        {
            if (!File.Exists(LastKnownServerFile))
                return null;

            var json = await File.ReadAllTextAsync(LastKnownServerFile);
            var endpoint = JsonSerializer.Deserialize<ServerEndpoint>(json);

            if (endpoint is not null)
                endpoint.DiscoveryMethod = "last-known";

            return endpoint;
        }
        catch
        {
            return null;
        }
    }
}
