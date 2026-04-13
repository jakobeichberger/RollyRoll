using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace RollyRoll.ClientAgent;

/// <summary>
/// Captures user profiles using USMT ScanState before recovery or migration.
/// Uploads the captured data to the RollyRoll server for later re-injection.
/// </summary>
public class ProfileCapturer
{
    private readonly ILogger<ProfileCapturer> _logger;
    private readonly ServerLocator _serverLocator;

    public ProfileCapturer(ILogger<ProfileCapturer> logger, ServerLocator serverLocator)
    {
        _logger = logger;
        _serverLocator = serverLocator;
    }

    /// <summary>
    /// Capture all user profiles from this machine.
    /// Uses USMT ScanState if available, falls back to direct file copy.
    /// </summary>
    public async Task<string> CaptureProfilesAsync(CancellationToken ct = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"rollyroll_profiles_{DateTime.UtcNow:yyyyMMddHHmmss}");
        Directory.CreateDirectory(tempDir);

        _logger.LogInformation("Capturing user profiles to {Path}", tempDir);

        // Try USMT ScanState first (preferred method)
        var usmtPath = FindUsmtPath();
        if (usmtPath != null)
        {
            await CaptureWithUsmtAsync(usmtPath, tempDir, ct);
        }
        else
        {
            // Fallback: direct copy of user profile directories
            _logger.LogWarning("USMT not found, using direct file copy fallback");
            await CaptureDirectCopyAsync(tempDir, ct);
        }

        _logger.LogInformation("Profile capture complete: {Path}", tempDir);
        return tempDir;
    }

    /// <summary>Upload captured profiles to the server.</summary>
    public async Task UploadProfilesAsync(string profilesPath, CancellationToken ct = default)
    {
        var serverUrl = await _serverLocator.GetServerUrlAsync(ct);
        var mac = GetMacAddress();

        // Create a zip archive of the profiles
        var zipPath = profilesPath + ".zip";
        System.IO.Compression.ZipFile.CreateFromDirectory(profilesPath, zipPath);

        _logger.LogInformation("Uploading profiles ({Size} bytes)...", new FileInfo(zipPath).Length);

        using var http = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        });
        http.Timeout = TimeSpan.FromMinutes(30);

        using var content = new MultipartFormDataContent();
        using var fileStream = File.OpenRead(zipPath);
        content.Add(new StreamContent(fileStream), "profiles", Path.GetFileName(zipPath));
        content.Add(new StringContent(mac), "clientMac");

        await http.PostAsync($"{serverUrl}/api/profiles/upload", content, ct);

        // Cleanup
        try
        {
            File.Delete(zipPath);
            Directory.Delete(profilesPath, true);
        }
        catch { }

        _logger.LogInformation("Profiles uploaded to server");
    }

    private async Task CaptureWithUsmtAsync(string usmtPath, string outputDir, CancellationToken ct)
    {
        var scanStatePath = Path.Combine(usmtPath, "scanstate.exe");

        var psi = new ProcessStartInfo
        {
            FileName = scanStatePath,
            Arguments = $"\"{outputDir}\" /i:\"{Path.Combine(usmtPath, "MigDocs.xml")}\" /i:\"{Path.Combine(usmtPath, "MigApp.xml")}\" /o /c /efs:copyraw /v:5",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _logger.LogInformation("Running USMT ScanState...");
        using var process = Process.Start(psi);
        if (process != null)
        {
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync(ct);
                _logger.LogWarning("USMT ScanState exited with code {Code}: {Error}", process.ExitCode, error);
            }
            else
            {
                _logger.LogInformation("USMT ScanState completed successfully");
            }
        }
    }

    private async Task CaptureDirectCopyAsync(string outputDir, CancellationToken ct)
    {
        var usersDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var parentDir = Directory.GetParent(usersDir)?.FullName ?? @"C:\Users";

        foreach (var userDir in Directory.GetDirectories(parentDir))
        {
            var userName = Path.GetFileName(userDir);
            if (userName is "Default" or "Default User" or "Public" or "All Users") continue;

            var destDir = Path.Combine(outputDir, userName);
            Directory.CreateDirectory(destDir);

            // Copy Desktop, Documents, AppData
            var foldersToCapture = new[] { "Desktop", "Documents", "Downloads", "AppData\\Roaming" };
            foreach (var folder in foldersToCapture)
            {
                var srcPath = Path.Combine(userDir, folder);
                if (Directory.Exists(srcPath))
                {
                    var destPath = Path.Combine(destDir, folder);
                    await CopyDirectoryAsync(srcPath, destPath, ct);
                }
            }
        }
    }

    private static async Task CopyDirectoryAsync(string source, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            ct.ThrowIfCancellationRequested();
            var destFile = Path.Combine(destination, Path.GetFileName(file));
            try { File.Copy(file, destFile, true); }
            catch { } // Skip locked/inaccessible files
        }
        foreach (var dir in Directory.GetDirectories(source))
        {
            var destDir = Path.Combine(destination, Path.GetFileName(dir));
            await CopyDirectoryAsync(dir, destDir, ct);
        }
    }

    private static string? FindUsmtPath()
    {
        var possiblePaths = new[]
        {
            @"C:\Program Files (x86)\Windows Kits\10\Assessment and Deployment Kit\User State Migration Tool\amd64",
            @"C:\RollyRoll\USMT\amd64"
        };
        return possiblePaths.FirstOrDefault(Directory.Exists);
    }

    private static string GetMacAddress() =>
        System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
            .OrderByDescending(n => n.Speed)
            .Select(n => BitConverter.ToString(n.GetPhysicalAddress().GetAddressBytes()).Replace("-", ":"))
            .FirstOrDefault() ?? "00:00:00:00:00:00";
}
