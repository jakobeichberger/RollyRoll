using System.Diagnostics;
using System.Net.Http.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RollyRoll.ClientAgent;

/// <summary>
/// Checks server for approved patches assigned to this client.
/// Downloads and installs silently. Reports success/failure.
/// Creates recovery snapshot before patching via the server API.
/// </summary>
public class PatchInstaller : BackgroundService
{
    private readonly ILogger<PatchInstaller> _logger;
    private readonly ServerLocator _serverLocator;
    private readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(30);

    public PatchInstaller(ILogger<PatchInstaller> logger, ServerLocator serverLocator)
    {
        _logger = logger;
        _serverLocator = serverLocator;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("PatchInstaller started, check interval: {Interval}m", _checkInterval.TotalMinutes);

        // Initial delay to let the system fully boot
        await Task.Delay(TimeSpan.FromMinutes(2), ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckAndInstallPatchesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Patch check/install failed");
            }

            await Task.Delay(_checkInterval, ct);
        }
    }

    private async Task CheckAndInstallPatchesAsync(CancellationToken ct)
    {
        var serverUrl = await _serverLocator.GetServerUrlAsync(ct);
        var mac = GetMacAddress();

        using var http = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        });

        // Check for assigned patches
        var response = await http.GetAsync($"{serverUrl}/api/patches/assigned/{mac}", ct);
        if (!response.IsSuccessStatusCode) return;

        var patches = await response.Content.ReadFromJsonAsync<List<PatchAssignment>>(cancellationToken: ct);
        if (patches == null || patches.Count == 0) return;

        _logger.LogInformation("Found {Count} patches to install", patches.Count);

        foreach (var patch in patches)
        {
            try
            {
                // Request recovery snapshot before patching
                await http.PostAsync($"{serverUrl}/api/recovery/snapshot/{mac}?reason=Pre-patch+{patch.Title}", null, ct);

                _logger.LogInformation("Installing patch: {Title}", patch.Title);

                bool success;
                if (string.IsNullOrEmpty(patch.InstallerPath))
                {
                    // Windows Update patch — use wusa.exe or PowerShell
                    success = await InstallWindowsUpdateAsync(patch.KbArticleId, ct);
                }
                else
                {
                    // Third-party or driver — download and execute
                    var installerPath = await DownloadPatchAsync(http, serverUrl, patch.Id, ct);
                    success = await InstallPackageAsync(installerPath, patch.SilentInstallArgs, ct);
                }

                // Report result
                await http.PostAsJsonAsync($"{serverUrl}/api/patches/result", new
                {
                    PatchId = patch.Id,
                    ClientMac = mac,
                    Success = success,
                    ErrorMessage = success ? null : "Installation failed"
                }, ct);

                _logger.LogInformation("Patch {Title}: {Result}", patch.Title, success ? "SUCCESS" : "FAILED");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to install patch {Title}", patch.Title);
                await http.PostAsJsonAsync($"{serverUrl}/api/patches/result", new
                {
                    PatchId = patch.Id,
                    ClientMac = mac,
                    Success = false,
                    ErrorMessage = ex.Message
                }, ct);
            }
        }
    }

    private async Task<bool> InstallWindowsUpdateAsync(string? kbId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(kbId)) return false;

        // Use PowerShell to install Windows Update
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -Command \"Install-WindowsUpdate -KBArticleID {kbId} -AcceptAll -AutoReboot:$false\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null) return false;
        await process.WaitForExitAsync(ct);
        return process.ExitCode == 0;
    }

    private async Task<string> DownloadPatchAsync(HttpClient http, string serverUrl, int patchId, CancellationToken ct)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"rollyroll_patch_{patchId}.exe");
        var response = await http.GetAsync($"{serverUrl}/api/patches/download/{patchId}", ct);
        response.EnsureSuccessStatusCode();
        await using var fs = File.Create(tempPath);
        await response.Content.CopyToAsync(fs, ct);
        return tempPath;
    }

    private async Task<bool> InstallPackageAsync(string installerPath, string? silentArgs, CancellationToken ct)
    {
        var ext = Path.GetExtension(installerPath).ToLowerInvariant();
        var psi = new ProcessStartInfo
        {
            FileName = ext == ".msi" ? "msiexec.exe" : installerPath,
            Arguments = ext == ".msi"
                ? $"/i \"{installerPath}\" /quiet /norestart {silentArgs}"
                : silentArgs ?? "/S /quiet",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null) return false;
        await process.WaitForExitAsync(ct);

        // Clean up
        try { File.Delete(installerPath); } catch { }

        return process.ExitCode == 0;
    }

    private static string GetMacAddress() =>
        System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
            .OrderByDescending(n => n.Speed)
            .Select(n => BitConverter.ToString(n.GetPhysicalAddress().GetAddressBytes()).Replace("-", ":"))
            .FirstOrDefault() ?? "00:00:00:00:00:00";

    private record PatchAssignment(int Id, string Title, string? KbArticleId, string? InstallerPath, string? SilentInstallArgs);
}
