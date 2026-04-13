using RollyRoll.Core.Models;

namespace RollyRoll.Core.Interfaces;

/// <summary>
/// Manages PXE boot infrastructure: TFTP server for iPXE delivery and DHCP proxy for boot file selection.
/// </summary>
public interface IPxeService
{
    /// <summary>Start the TFTP server and DHCP proxy listener.</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>Stop the PXE services.</summary>
    Task StopAsync(CancellationToken ct = default);

    /// <summary>Whether the PXE services are currently running.</summary>
    bool IsRunning { get; }

    /// <summary>
    /// Generate an iPXE boot script for a specific client MAC.
    /// Decides whether to boot into WinPE (if task pending) or local disk.
    /// </summary>
    Task<string> GenerateBootScriptAsync(string macAddress, BootType bootType, CancellationToken ct = default);

    /// <summary>Get the correct boot file path for a given architecture.</summary>
    string GetBootFilePath(BootType bootType);

    /// <summary>Get PXE service statistics (clients served, errors, etc.).</summary>
    PxeServiceStats GetStats();
}

public class PxeServiceStats
{
    public int TotalBootRequests { get; set; }
    public int BiosBootRequests { get; set; }
    public int UefiBootRequests { get; set; }
    public int TftpFilesServed { get; set; }
    public int Errors { get; set; }
    public DateTime StartedAt { get; set; }
}
