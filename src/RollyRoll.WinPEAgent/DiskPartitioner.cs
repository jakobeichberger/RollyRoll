using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RollyRoll.Core.Models;

namespace RollyRoll.WinPEAgent;

/// <summary>
/// Partitions the target disk based on the detected boot type.
/// UEFI: GPT layout with EFI System Partition (ESP) + MSR + Windows partition.
/// BIOS: MBR layout with a single active Windows partition.
/// Uses diskpart.exe for all partitioning operations.
/// </summary>
public class DiskPartitioner
{
    private readonly ILogger<DiskPartitioner> _logger;

    public DiskPartitioner(ILogger<DiskPartitioner> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Partition disk 0 based on the boot type and return the result with drive letter assignments.
    /// </summary>
    public async Task<PartitionResult> PartitionDiskAsync(BootType bootType, int diskNumber = 0)
    {
        _logger.LogInformation("Partitioning disk {DiskNumber} for boot type {BootType}", diskNumber, bootType);

        var script = bootType switch
        {
            BootType.UEFI or BootType.UEFI32 => BuildUefiScript(diskNumber),
            BootType.LegacyBIOS => BuildBiosScript(diskNumber),
            _ => throw new ArgumentException($"Unsupported boot type: {bootType}")
        };

        await RunDiskpartScriptAsync(script);

        var result = new PartitionResult
        {
            BootType = bootType,
            WindowsDriveLetter = "W:",
            DiskNumber = diskNumber
        };

        if (bootType is BootType.UEFI or BootType.UEFI32)
        {
            result.EfiDriveLetter = "S:";
        }

        _logger.LogInformation("Disk partitioned successfully. Windows={WinDrive}, EFI={EfiDrive}",
            result.WindowsDriveLetter, result.EfiDriveLetter ?? "N/A");

        return result;
    }

    /// <summary>
    /// Build a diskpart script for UEFI/GPT partitioning.
    /// Layout: EFI System Partition (100MB, FAT32) + MSR (16MB) + Windows (remainder, NTFS).
    /// </summary>
    private static string BuildUefiScript(int diskNumber)
    {
        return $"""
            select disk {diskNumber}
            clean
            convert gpt

            rem === EFI System Partition (ESP) ===
            create partition efi size=100
            format quick fs=fat32 label="System"
            assign letter=S

            rem === Microsoft Reserved Partition ===
            create partition msr size=16

            rem === Windows Partition ===
            create partition primary
            format quick fs=ntfs label="Windows"
            assign letter=W

            exit
            """;
    }

    /// <summary>
    /// Build a diskpart script for Legacy BIOS/MBR partitioning.
    /// Layout: Single active primary partition (entire disk, NTFS).
    /// </summary>
    private static string BuildBiosScript(int diskNumber)
    {
        return $"""
            select disk {diskNumber}
            clean

            rem === Windows Partition (MBR, Active) ===
            create partition primary
            active
            format quick fs=ntfs label="Windows"
            assign letter=W

            exit
            """;
    }

    /// <summary>
    /// Execute a diskpart script by writing it to a temporary file and invoking diskpart.exe.
    /// </summary>
    private async Task RunDiskpartScriptAsync(string script)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"diskpart_{Guid.NewGuid():N}.txt");

        try
        {
            await File.WriteAllTextAsync(scriptPath, script);
            _logger.LogDebug("Diskpart script written to {Path}:\n{Script}", scriptPath, script);

            var psi = new ProcessStartInfo
            {
                FileName = "diskpart.exe",
                Arguments = $"/s \"{scriptPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start diskpart.exe");

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                _logger.LogError("Diskpart failed with exit code {ExitCode}. Output: {Output}. Error: {Error}",
                    process.ExitCode, output, error);
                throw new InvalidOperationException($"Diskpart failed with exit code {process.ExitCode}: {error}");
            }

            _logger.LogInformation("Diskpart completed successfully. Output: {Output}", output);
        }
        finally
        {
            if (File.Exists(scriptPath))
                File.Delete(scriptPath);
        }
    }
}

/// <summary>
/// Result of a disk partitioning operation containing drive letter assignments.
/// </summary>
public class PartitionResult
{
    /// <summary>The boot type used for partitioning.</summary>
    public BootType BootType { get; set; }

    /// <summary>Drive letter assigned to the Windows partition (e.g. "W:").</summary>
    public string WindowsDriveLetter { get; set; } = string.Empty;

    /// <summary>Drive letter assigned to the EFI System Partition (UEFI only).</summary>
    public string? EfiDriveLetter { get; set; }

    /// <summary>Physical disk number that was partitioned.</summary>
    public int DiskNumber { get; set; }
}
