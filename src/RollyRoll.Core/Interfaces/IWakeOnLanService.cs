namespace RollyRoll.Core.Interfaces;

/// <summary>
/// Sends Wake-on-LAN magic packets to power on PCs remotely.
/// Supports multi-subnet delivery via broadcast and directed packets.
/// </summary>
public interface IWakeOnLanService
{
    /// <summary>Send a WoL magic packet to a single MAC address.</summary>
    Task SendWakeOnLanAsync(string macAddress, CancellationToken ct = default);

    /// <summary>Send WoL magic packets to multiple MAC addresses.</summary>
    Task SendWakeOnLanAsync(IEnumerable<string> macAddresses, CancellationToken ct = default);

    /// <summary>Send WoL to all clients in a group.</summary>
    Task WakeGroupAsync(int groupId, CancellationToken ct = default);

    /// <summary>
    /// Send a directed WoL packet to a specific subnet broadcast address.
    /// Used for cross-subnet WoL when the client is on a different VLAN.
    /// </summary>
    Task SendDirectedWakeOnLanAsync(string macAddress, string subnetBroadcastAddress, CancellationToken ct = default);
}
