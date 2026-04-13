using RollyRoll.Core.Models;

namespace RollyRoll.Core.Interfaces;

/// <summary>
/// Manages client registration, approval, and inventory.
/// Clients are auto-discovered via PXE boot or ClientAgent broadcast.
/// </summary>
public interface IClientDiscoveryService
{
    /// <summary>Register or update a client from PXE boot information.</summary>
    Task<Client> RegisterFromPxeBootAsync(string macAddress, BootType bootType, string? ipAddress = null, CancellationToken ct = default);

    /// <summary>Register or update a client from ClientAgent heartbeat.</summary>
    Task<Client> RegisterFromAgentAsync(string macAddress, string hostname, string ipAddress, string osVersion, string? hardwareModel = null, string? serialNumber = null, CancellationToken ct = default);

    /// <summary>Approve a pending client.</summary>
    Task ApproveClientAsync(int clientId, CancellationToken ct = default);

    /// <summary>Get all clients.</summary>
    Task<List<Client>> GetAllClientsAsync(CancellationToken ct = default);

    /// <summary>Get a client by MAC address.</summary>
    Task<Client?> GetClientByMacAsync(string macAddress, CancellationToken ct = default);

    /// <summary>Get a client by ID.</summary>
    Task<Client?> GetClientByIdAsync(int id, CancellationToken ct = default);

    /// <summary>Get all clients in a group.</summary>
    Task<List<Client>> GetClientsByGroupAsync(int groupId, CancellationToken ct = default);

    /// <summary>Move a client to a group.</summary>
    Task MoveClientToGroupAsync(int clientId, int? groupId, CancellationToken ct = default);

    /// <summary>Delete a client from the inventory.</summary>
    Task DeleteClientAsync(int clientId, CancellationToken ct = default);

    /// <summary>Update online status for all clients based on heartbeat timeout.</summary>
    Task UpdateOnlineStatusAsync(CancellationToken ct = default);

    /// <summary>Search clients by hostname, MAC, IP, or serial number.</summary>
    Task<List<Client>> SearchClientsAsync(string query, CancellationToken ct = default);
}
