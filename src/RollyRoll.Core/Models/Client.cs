namespace RollyRoll.Core.Models;

/// <summary>
/// Represents a managed PC in the fleet. Identity is MAC-based (stable across IP/hostname changes).
/// </summary>
public class Client
{
    public int Id { get; set; }

    /// <summary>Primary MAC address — the stable identity for this client.</summary>
    public string MacAddress { get; set; } = string.Empty;

    /// <summary>Current hostname (auto-updated by ClientAgent heartbeats).</summary>
    public string Hostname { get; set; } = string.Empty;

    /// <summary>Current IP address (auto-updated by ClientAgent heartbeats).</summary>
    public string IpAddress { get; set; } = string.Empty;

    /// <summary>Additional MAC addresses (wireless, secondary NICs).</summary>
    public List<string> AdditionalMacAddresses { get; set; } = [];

    /// <summary>UEFI or Legacy BIOS — detected during PXE boot via DHCP Option 93.</summary>
    public BootType BootType { get; set; }

    /// <summary>Operating system version reported by ClientAgent.</summary>
    public string OsVersion { get; set; } = string.Empty;

    /// <summary>Manufacturer and model from SMBIOS/DMI data.</summary>
    public string HardwareModel { get; set; } = string.Empty;

    /// <summary>Serial number from SMBIOS/DMI data.</summary>
    public string SerialNumber { get; set; } = string.Empty;

    /// <summary>Whether this client is currently online (heartbeat within threshold).</summary>
    public bool IsOnline { get; set; }

    /// <summary>Last time the ClientAgent sent a heartbeat.</summary>
    public DateTime? LastSeen { get; set; }

    /// <summary>When this client was first discovered (PXE boot or agent registration).</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Whether this client has been approved by an admin (vs auto-discovered pending).</summary>
    public bool IsApproved { get; set; }

    // Navigation properties
    public int? GroupId { get; set; }
    public ClientGroup? Group { get; set; }

    public List<ScheduledTask> ScheduledTasks { get; set; } = [];
    public List<RecoverySnapshot> RecoverySnapshots { get; set; } = [];
}
