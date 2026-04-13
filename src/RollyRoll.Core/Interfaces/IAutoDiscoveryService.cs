namespace RollyRoll.Core.Interfaces;

/// <summary>
/// Handles automatic server-client discovery without manual configuration.
/// Server broadcasts presence; clients find the server automatically.
/// Supports same-subnet (UDP broadcast), cross-subnet (DNS SRV), and last-known fallback.
/// </summary>
public interface IAutoDiscoveryService
{
    /// <summary>Start broadcasting server presence on the network.</summary>
    Task StartBroadcastingAsync(CancellationToken ct = default);

    /// <summary>Stop broadcasting.</summary>
    Task StopBroadcastingAsync(CancellationToken ct = default);

    /// <summary>
    /// Discover the RollyRoll server from a client.
    /// Tries: UDP broadcast -> DNS SRV (_rollyroll._tcp) -> last known server IP.
    /// </summary>
    Task<ServerEndpoint?> DiscoverServerAsync(CancellationToken ct = default);

    /// <summary>Register a DNS SRV record for cross-subnet discovery (optional).</summary>
    Task RegisterDnsSrvRecordAsync(string serverHostname, int port, CancellationToken ct = default);
}

public class ServerEndpoint
{
    public string Address { get; set; } = string.Empty;
    public int Port { get; set; }
    public string ServerName { get; set; } = string.Empty;
    public string DiscoveryMethod { get; set; } = string.Empty; // "broadcast", "dns-srv", "last-known"
}
