using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace RollyRoll.WinPEAgent;

/// <summary>
/// Restores user profiles using USMT (User State Migration Tool) LoadState.
/// Only runs when the deployment mode is RestoreWithProfiles.
/// Downloads the captured profile data from the server and applies it to the target OS.
/// </summary>
public class ProfileInjector
{
    private readonly ILogger<ProfileInjector> _logger;

    public ProfileInjector(ILogger<ProfileInjector> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Download previously captured profiles from the server and inject them into the target OS.
    /// Uses USMT LoadState to restore user settings, files, and profile data.
    /// </summary>
    /// <param name="macAddress">Client MAC address used to look up stored profiles on the server.</param>
    /// <param name="serverUrl">Base URL of the RollyRoll server.</param>
    /// <param name="targetDrive">Target drive letter where the OS was deployed (e.g. "W:").</param>
    public async Task InjectProfilesAsync(string macAddress, string serverUrl, string targetDrive)
    {
        _logger.LogInformation("Injecting user profiles for {Mac} into {Drive}", macAddress, targetDrive);

        // Download profile store from server
        var profileStorePath = Path.Combine("X:\\", "RollyRoll", "ProfileStore");
        Directory.CreateDirectory(profileStorePath);

        await DownloadProfileStoreAsync(macAddress, serverUrl, profileStorePath);

        // Verify the profile store was downloaded
        if (!Directory.Exists(profileStorePath) || !Directory.EnumerateFiles(profileStorePath, "*", SearchOption.AllDirectories).Any())
        {
            _logger.LogWarning("No profile data found for {Mac}. Skipping profile injection", macAddress);
            return;
        }

        // Run USMT LoadState to restore profiles into the offline Windows installation
        await RunLoadStateAsync(profileStorePath, targetDrive);

        _logger.LogInformation("User profiles injected successfully for {Mac}", macAddress);
    }

    /// <summary>
    /// Download the USMT profile store from the RollyRoll server.
    /// </summary>
    private async Task DownloadProfileStoreAsync(string macAddress, string serverUrl, string localPath)
    {
        _logger.LogInformation("Downloading profile store for {Mac} from {Server}", macAddress, serverUrl);

        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri(serverUrl),
            Timeout = TimeSpan.FromMinutes(30)
        };

        var response = await httpClient.GetAsync(
            $"/api/agent/profiles/{Uri.EscapeDataString(macAddress)}",
            HttpCompletionOption.ResponseHeadersRead);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Failed to download profiles: {StatusCode}", response.StatusCode);
            return;
        }

        // The server sends profiles as a zip archive
        var archivePath = Path.Combine(localPath, "profiles.zip");
        await using var contentStream = await response.Content.ReadAsStreamAsync();
        await using var fileStream = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None);
        await contentStream.CopyToAsync(fileStream);
        fileStream.Close();

        // Extract the archive
        System.IO.Compression.ZipFile.ExtractToDirectory(archivePath, localPath, overwriteFiles: true);
        File.Delete(archivePath);

        _logger.LogInformation("Profile store downloaded and extracted to {Path}", localPath);
    }

    /// <summary>
    /// Run USMT LoadState to restore user profiles into an offline Windows installation.
    /// </summary>
    private async Task RunLoadStateAsync(string profileStorePath, string targetDrive)
    {
        // USMT LoadState restores user state data to an offline Windows image
        // /offlinewindir points to the Windows directory on the target partition
        var windowsDir = Path.Combine(targetDrive + "\\", "Windows");
        var loadStatePath = GetUsmtToolPath("loadstate.exe");

        var arguments = $"\"{profileStorePath}\" /offlinewindir:{windowsDir} /i:migdocs.xml /i:migapp.xml /lac /lae /v:5";

        _logger.LogInformation("Running USMT LoadState: {Path} {Args}", loadStatePath, arguments);

        var psi = new ProcessStartInfo
        {
            FileName = loadStatePath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start USMT LoadState");

        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            _logger.LogError("USMT LoadState failed: ExitCode={ExitCode}, Output={Output}, Error={Error}",
                process.ExitCode, output, error);
            throw new InvalidOperationException($"USMT LoadState failed with exit code {process.ExitCode}: {error}");
        }

        _logger.LogInformation("USMT LoadState completed successfully");
    }

    /// <summary>
    /// Locate the USMT tool path. USMT tools should be included in the WinPE image or on a network share.
    /// </summary>
    private static string GetUsmtToolPath(string toolName)
    {
        // Check common USMT locations in WinPE
        var candidates = new[]
        {
            Path.Combine("X:\\", "USMT", toolName),
            Path.Combine("X:\\", "Program Files", "USMT", toolName),
            Path.Combine("X:\\", "Windows", "System32", toolName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "USMT", toolName)
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        // Fall back to PATH resolution
        return toolName;
    }
}
