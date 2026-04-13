using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RollyRoll.Core.Interfaces;
using RollyRoll.Core.Models;
using RollyRoll.Infrastructure.Data;

namespace RollyRoll.Infrastructure.Services;

/// <summary>
/// Manages client registration, approval, and inventory. Clients are auto-discovered
/// via PXE boot (MAC + boot type) or ClientAgent heartbeat (full hardware inventory).
/// MAC address is the stable identity; hostname and IP may change over time.
/// </summary>
public class ClientDiscoveryService : IClientDiscoveryService
{
    private readonly ILogger<ClientDiscoveryService> _logger;
    private readonly RollyRollDbContext _db;

    /// <summary>
    /// Heartbeat timeout threshold. A client is considered offline if no heartbeat
    /// has been received within this duration.
    /// </summary>
    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Initializes a new instance of the <see cref="ClientDiscoveryService"/> class.
    /// </summary>
    /// <param name="logger">Logger for client discovery operations.</param>
    /// <param name="db">Database context for persisting client records.</param>
    public ClientDiscoveryService(ILogger<ClientDiscoveryService> logger, RollyRollDbContext db)
    {
        _logger = logger;
        _db = db;
    }

    /// <inheritdoc />
    public async Task<Client> RegisterFromPxeBootAsync(
        string macAddress,
        BootType bootType,
        string? ipAddress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(macAddress);
        var normalizedMac = NormalizeMacAddress(macAddress);

        var client = await _db.Clients.FirstOrDefaultAsync(c => c.MacAddress == normalizedMac, ct);

        if (client is not null)
        {
            // Update existing client with PXE boot info
            client.BootType = bootType;
            client.LastSeen = DateTime.UtcNow;
            client.IsOnline = true;

            if (!string.IsNullOrEmpty(ipAddress))
                client.IpAddress = ipAddress;

            _logger.LogInformation(
                "Existing client updated from PXE boot: {Mac}, BootType={BootType}, IP={IP}",
                normalizedMac, bootType, ipAddress ?? client.IpAddress);
        }
        else
        {
            // Register new client discovered via PXE boot
            client = new Client
            {
                MacAddress = normalizedMac,
                BootType = bootType,
                IpAddress = ipAddress ?? string.Empty,
                IsOnline = true,
                LastSeen = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                IsApproved = false // Pending admin approval
            };

            _db.Clients.Add(client);

            _logger.LogInformation(
                "New client discovered via PXE boot: {Mac}, BootType={BootType}, IP={IP}",
                normalizedMac, bootType, ipAddress ?? "Unknown");
        }

        await _db.SaveChangesAsync(ct);
        return client;
    }

    /// <inheritdoc />
    public async Task<Client> RegisterFromAgentAsync(
        string macAddress,
        string hostname,
        string ipAddress,
        string osVersion,
        string? hardwareModel = null,
        string? serialNumber = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(macAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);
        ArgumentException.ThrowIfNullOrWhiteSpace(ipAddress);

        var normalizedMac = NormalizeMacAddress(macAddress);

        var client = await _db.Clients.FirstOrDefaultAsync(c => c.MacAddress == normalizedMac, ct);

        if (client is not null)
        {
            // Update existing client with heartbeat data
            client.Hostname = hostname;
            client.IpAddress = ipAddress;
            client.OsVersion = osVersion;
            client.IsOnline = true;
            client.LastSeen = DateTime.UtcNow;

            if (!string.IsNullOrEmpty(hardwareModel))
                client.HardwareModel = hardwareModel;

            if (!string.IsNullOrEmpty(serialNumber))
                client.SerialNumber = serialNumber;

            _logger.LogDebug(
                "Client heartbeat received: {Hostname} ({Mac}), IP={IP}, OS={OS}",
                hostname, normalizedMac, ipAddress, osVersion);
        }
        else
        {
            // Register new client discovered via agent heartbeat
            client = new Client
            {
                MacAddress = normalizedMac,
                Hostname = hostname,
                IpAddress = ipAddress,
                OsVersion = osVersion,
                HardwareModel = hardwareModel ?? string.Empty,
                SerialNumber = serialNumber ?? string.Empty,
                IsOnline = true,
                LastSeen = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                IsApproved = false
            };

            _db.Clients.Add(client);

            _logger.LogInformation(
                "New client discovered via agent heartbeat: {Hostname} ({Mac}), IP={IP}, OS={OS}",
                hostname, normalizedMac, ipAddress, osVersion);
        }

        await _db.SaveChangesAsync(ct);
        return client;
    }

    /// <inheritdoc />
    public async Task ApproveClientAsync(int clientId, CancellationToken ct = default)
    {
        var client = await _db.Clients.FindAsync([clientId], ct)
            ?? throw new InvalidOperationException($"Client {clientId} not found.");

        if (client.IsApproved)
        {
            _logger.LogWarning("Client {Hostname} ({Mac}) is already approved.", client.Hostname, client.MacAddress);
            return;
        }

        client.IsApproved = true;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Client approved: {Hostname} ({Mac}), Id={ClientId}",
            client.Hostname, client.MacAddress, clientId);
    }

    /// <inheritdoc />
    public async Task<List<Client>> GetAllClientsAsync(CancellationToken ct = default)
    {
        return await _db.Clients
            .Include(c => c.Group)
            .OrderBy(c => c.Hostname)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<Client?> GetClientByMacAsync(string macAddress, CancellationToken ct = default)
    {
        var normalizedMac = NormalizeMacAddress(macAddress);

        return await _db.Clients
            .Include(c => c.Group)
            .FirstOrDefaultAsync(c => c.MacAddress == normalizedMac, ct);
    }

    /// <inheritdoc />
    public async Task<Client?> GetClientByIdAsync(int id, CancellationToken ct = default)
    {
        return await _db.Clients
            .Include(c => c.Group)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    /// <inheritdoc />
    public async Task<List<Client>> GetClientsByGroupAsync(int groupId, CancellationToken ct = default)
    {
        return await _db.Clients
            .Where(c => c.GroupId == groupId)
            .OrderBy(c => c.Hostname)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task MoveClientToGroupAsync(int clientId, int? groupId, CancellationToken ct = default)
    {
        var client = await _db.Clients.FindAsync([clientId], ct)
            ?? throw new InvalidOperationException($"Client {clientId} not found.");

        if (groupId.HasValue)
        {
            var groupExists = await _db.ClientGroups.AnyAsync(g => g.Id == groupId.Value, ct);
            if (!groupExists)
                throw new InvalidOperationException($"Group {groupId} not found.");
        }

        var previousGroupId = client.GroupId;
        client.GroupId = groupId;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Client {Hostname} ({Mac}) moved from group {PreviousGroup} to group {NewGroup}.",
            client.Hostname, client.MacAddress,
            previousGroupId?.ToString() ?? "None",
            groupId?.ToString() ?? "None");
    }

    /// <inheritdoc />
    public async Task DeleteClientAsync(int clientId, CancellationToken ct = default)
    {
        var client = await _db.Clients.FindAsync([clientId], ct)
            ?? throw new InvalidOperationException($"Client {clientId} not found.");

        _db.Clients.Remove(client);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Client deleted: {Hostname} ({Mac}), Id={ClientId}",
            client.Hostname, client.MacAddress, clientId);
    }

    /// <inheritdoc />
    public async Task UpdateOnlineStatusAsync(CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow - HeartbeatTimeout;

        var staleClients = await _db.Clients
            .Where(c => c.IsOnline && (c.LastSeen == null || c.LastSeen < cutoff))
            .ToListAsync(ct);

        if (staleClients.Count == 0)
            return;

        foreach (var client in staleClients)
        {
            client.IsOnline = false;
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Marked {Count} clients as offline (no heartbeat since {Cutoff:O}).",
            staleClients.Count, cutoff);
    }

    /// <inheritdoc />
    public async Task<List<Client>> SearchClientsAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return await GetAllClientsAsync(ct);

        var normalizedQuery = query.Trim().ToLowerInvariant();

        return await _db.Clients
            .Include(c => c.Group)
            .Where(c => c.Hostname.ToLower().Contains(normalizedQuery)
                     || c.MacAddress.ToLower().Contains(normalizedQuery)
                     || c.IpAddress.Contains(normalizedQuery)
                     || c.SerialNumber.ToLower().Contains(normalizedQuery))
            .OrderBy(c => c.Hostname)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Normalizes a MAC address to the standard colon-separated uppercase format (AA:BB:CC:DD:EE:FF).
    /// </summary>
    private static string NormalizeMacAddress(string mac)
    {
        var cleaned = mac.Replace(":", "").Replace("-", "").Replace(".", "").ToUpperInvariant();

        if (cleaned.Length != 12)
            throw new ArgumentException($"Invalid MAC address: {mac}");

        return string.Join(":",
            Enumerable.Range(0, 6).Select(i => cleaned.Substring(i * 2, 2)));
    }
}
