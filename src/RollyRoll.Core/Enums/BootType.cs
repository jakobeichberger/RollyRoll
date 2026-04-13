namespace RollyRoll.Core.Models;

/// <summary>
/// Firmware type detected during PXE boot via DHCP Option 93.
/// Determines partitioning (GPT vs MBR) and boot file (ipxe.efi vs undionly.kpxe).
/// </summary>
public enum BootType
{
    /// <summary>Unknown — not yet detected.</summary>
    Unknown,

    /// <summary>Legacy BIOS. Partitioning: MBR. Boot file: undionly.kpxe.</summary>
    LegacyBIOS,

    /// <summary>UEFI 64-bit. Partitioning: GPT+ESP. Boot file: ipxe.efi.</summary>
    UEFI,

    /// <summary>UEFI 32-bit. Partitioning: GPT+ESP. Boot file: ipxe32.efi.</summary>
    UEFI32
}
