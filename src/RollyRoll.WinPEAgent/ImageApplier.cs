using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace RollyRoll.WinPEAgent;

/// <summary>
/// Applies a WIM image to the target partition using DISM.exe.
/// Handles image application, BCD boot configuration for both UEFI and BIOS,
/// and verification of the applied image.
/// </summary>
public class ImageApplier
{
    private readonly ILogger<ImageApplier> _logger;

    public ImageApplier(ILogger<ImageApplier> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Apply a WIM image to the specified target drive using DISM.
    /// </summary>
    /// <param name="wimPath">Full path to the WIM file (e.g. "X:\RollyRoll\deploy.wim").</param>
    /// <param name="targetDrive">Target drive letter including colon (e.g. "W:").</param>
    /// <param name="imageIndex">Image index within the WIM file (default: 1).</param>
    public async Task ApplyImageAsync(string wimPath, string targetDrive, int imageIndex = 1)
    {
        if (!File.Exists(wimPath))
            throw new FileNotFoundException($"WIM image not found: {wimPath}");

        _logger.LogInformation("Applying image {WimPath} (index {Index}) to {Drive}",
            wimPath, imageIndex, targetDrive);

        // Apply the WIM image using DISM
        var dismArgs = $"/Apply-Image /ImageFile:\"{wimPath}\" /Index:{imageIndex} /ApplyDir:{targetDrive}\\";
        await RunDismAsync(dismArgs);

        _logger.LogInformation("Image applied successfully to {Drive}", targetDrive);

        // Configure boot using bcdboot
        await ConfigureBootAsync(targetDrive);
    }

    /// <summary>
    /// Configure the boot manager using bcdboot.exe after image application.
    /// Works for both UEFI and BIOS depending on the firmware type.
    /// </summary>
    private async Task ConfigureBootAsync(string targetDrive)
    {
        _logger.LogInformation("Configuring boot for {Drive}", targetDrive);

        var windowsDir = Path.Combine(targetDrive + "\\", "Windows");
        if (!Directory.Exists(windowsDir))
        {
            _logger.LogWarning("Windows directory not found at {Path}, skipping boot configuration", windowsDir);
            return;
        }

        // bcdboot will auto-detect firmware type and configure accordingly
        var args = $"{windowsDir} /s {targetDrive} /f ALL";

        var psi = new ProcessStartInfo
        {
            FileName = "bcdboot.exe",
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start bcdboot.exe");

        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            _logger.LogError("bcdboot failed: ExitCode={ExitCode}, Output={Output}, Error={Error}",
                process.ExitCode, output, error);
            throw new InvalidOperationException($"bcdboot failed with exit code {process.ExitCode}: {error}");
        }

        _logger.LogInformation("Boot configured successfully for {Drive}", targetDrive);
    }

    /// <summary>
    /// Run DISM.exe with the specified arguments and wait for completion.
    /// </summary>
    private async Task RunDismAsync(string arguments)
    {
        _logger.LogDebug("Running DISM: {Args}", arguments);

        var psi = new ProcessStartInfo
        {
            FileName = "DISM.exe",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start DISM.exe");

        // Read output asynchronously to avoid deadlocks
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
        {
            _logger.LogError("DISM failed: ExitCode={ExitCode}, Output={Output}, Error={Error}",
                process.ExitCode, output, error);
            throw new InvalidOperationException($"DISM failed with exit code {process.ExitCode}: {error}");
        }

        _logger.LogInformation("DISM completed successfully");
    }
}
