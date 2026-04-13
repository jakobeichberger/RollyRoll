using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;

namespace RollyRoll.ClientAgent;

/// <summary>
/// Captures user profiles using USMT (User State Migration Tool) ScanState before recovery operations.
/// The captured profile data is uploaded to the RollyRoll server for later re-injection after
/// the machine is reimaged.
/// </summary>
public class ProfileCapturer
{
    private readonly ILogger<ProfileCapturer> _logger;
    private readonly ServerLocator _serverLocator;
    private readonly IHttpClientFactory _httpClientFactory;

    private static readonly string UsmtStorePath = Path.Combine(Path.GetTempPath(), "RollyRoll", "USMTStore");

    public ProfileCapturer(
        ILogger<ProfileCapturer> logger,
        ServerLocator serverLocator,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _serverLocator = serverLocator;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Capture all user profiles on this machine and upload them to the server.
    /// Uses USMT ScanState to capture user settings, documents, and profile data.
    /// </summary>
    /// <param name="reason">The reason for capture (e.g. "Pre-recovery", "Pre-deployment").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if capture and upload succeeded, false otherwise.</returns>
    public async Task<bool> CaptureAndUploadAsync(string reason, CancellationToken ct = default)
    {
        var macAddress = GetPrimaryMacAddress();
        _logger.LogInformation("Starting profile capture for {Mac}. Reason: {Reason}", macAddress, reason);

        try
        {
            // Clean up any previous capture
            if (Directory.Exists(UsmtStorePath))
                Directory.Delete(UsmtStorePath, recursive: true);
            Directory.CreateDirectory(UsmtStorePath);

            // Run USMT ScanState to capture profiles
            await RunScanStateAsync(UsmtStorePath);

            // Verify capture produced data
            var capturedFiles = Directory.GetFiles(UsmtStorePath, "*", SearchOption.AllDirectories);
            if (capturedFiles.Length == 0)
            {
                _logger.LogWarning("ScanState produced no output files. No profiles to capture");
                return false;
            }

            var totalSize = capturedFiles.Sum(f => new FileInfo(f).Length);
            _logger.LogInformation("Profile capture complete: {FileCount} files, {SizeMB:F1} MB",
                capturedFiles.Length, totalSize / (1024.0 * 1024));

            // Compress the capture into a zip archive
            var archivePath = Path.Combine(Path.GetTempPath(), "RollyRoll", $"profiles_{macAddress.Replace(":", "")}.zip");
            if (File.Exists(archivePath))
                File.Delete(archivePath);

            ZipFile.CreateFromDirectory(UsmtStorePath, archivePath, CompressionLevel.Optimal, includeBaseDirectory: false);

            var archiveSize = new FileInfo(archivePath).Length;
            _logger.LogInformation("Profile archive created: {Path} ({SizeMB:F1} MB)",
                archivePath, archiveSize / (1024.0 * 1024));

            // Upload to server
            await UploadProfileArchiveAsync(macAddress, reason, archivePath, ct);

            // Cleanup
            try
            {
                File.Delete(archivePath);
                Directory.Delete(UsmtStorePath, recursive: true);
            }
            catch { /* best effort */ }

            _logger.LogInformation("Profile capture and upload completed for {Mac}", macAddress);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Profile capture failed for {Mac}", macAddress);
            return false;
        }
    }

    /// <summary>
    /// Run USMT ScanState to capture user profiles from the running OS.
    /// </summary>
    private async Task RunScanStateAsync(string storePath)
    {
        var scanStatePath = FindUsmtTool("scanstate.exe");

        // ScanState arguments:
        //   storePath       - where to store the captured data
        //   /i:migdocs.xml  - include user documents migration rules
        //   /i:migapp.xml   - include application settings migration rules
        //   /o              - overwrite existing store
        //   /c              - continue on non-fatal errors
        //   /localonly       - only capture local accounts (not domain-cached profiles)
        //   /efs:copyraw    - capture EFS-encrypted files as-is
        //   /v:5            - verbose logging
        var arguments = $"\"{storePath}\" /i:migdocs.xml /i:migapp.xml /o /c /localonly /efs:copyraw /v:5";

        _logger.LogInformation("Running USMT ScanState: {Path} {Args}", scanStatePath, arguments);

        var psi = new ProcessStartInfo
        {
            FileName = scanStatePath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start USMT ScanState");

        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        // ScanState exit codes: 0 = success, 1 = non-fatal errors (some profiles skipped)
        if (process.ExitCode > 1)
        {
            _logger.LogError("ScanState failed: ExitCode={ExitCode}, Error={Error}", process.ExitCode, error);
            throw new InvalidOperationException($"USMT ScanState failed with exit code {process.ExitCode}: {error}");
        }

        if (process.ExitCode == 1)
        {
            _logger.LogWarning("ScanState completed with non-fatal errors. Some profiles may not be captured");
        }
        else
        {
            _logger.LogInformation("ScanState completed successfully");
        }
    }

    /// <summary>
    /// Upload the compressed profile archive to the RollyRoll server.
    /// </summary>
    private async Task UploadProfileArchiveAsync(string macAddress, string reason, string archivePath, CancellationToken ct)
    {
        var serverUrl = _serverLocator.GetCachedServerUrl();
        if (string.IsNullOrEmpty(serverUrl))
            serverUrl = await _serverLocator.DiscoverAsync(ct);

        var client = _httpClientFactory.CreateClient("RollyRollServer");
        if (client.BaseAddress == null)
            client.BaseAddress = new Uri(serverUrl);

        _logger.LogInformation("Uploading profile archive for {Mac} to {Server}", macAddress, serverUrl);

        await using var fileStream = File.OpenRead(archivePath);
        using var content = new MultipartFormDataContent();

        var streamContent = new StreamContent(fileStream);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        content.Add(streamContent, "profileArchive", Path.GetFileName(archivePath));
        content.Add(new StringContent(macAddress), "macAddress");
        content.Add(new StringContent(reason), "reason");

        var response = await client.PostAsync("/api/agent/profiles/upload", content, ct);
        response.EnsureSuccessStatusCode();

        _logger.LogInformation("Profile archive uploaded successfully for {Mac}", macAddress);
    }

    /// <summary>
    /// Locate the USMT ScanState tool on the system.
    /// </summary>
    private static string FindUsmtTool(string toolName)
    {
        // Check common USMT installation paths
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Windows Kits", "10", "Assessment and Deployment Kit", "User State Migration Tool", "amd64", toolName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Windows Kits", "10", "Assessment and Deployment Kit", "User State Migration Tool", "amd64", toolName),
            Path.Combine(AppContext.BaseDirectory, "USMT", toolName),
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

    private static string GetPrimaryMacAddress()
    {
        var nic = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
            .Where(n => n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
            .OrderByDescending(n => n.Speed)
            .FirstOrDefault();

        if (nic == null) return "00:00:00:00:00:00";

        var macBytes = nic.GetPhysicalAddress().GetAddressBytes();
        return string.Join(":", macBytes.Select(b => b.ToString("X2")));
    }
}
