using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

namespace RollyRoll.ClientAgent;

/// <summary>
/// Checks the server for approved patches assigned to this client, downloads and installs
/// them silently, and reports success or failure. Creates a recovery snapshot before
/// patching to enable rollback if something goes wrong.
/// </summary>
public class PatchInstaller : BackgroundService
{
    private readonly ILogger<PatchInstaller> _logger;
    private readonly ServerLocator _serverLocator;
    private readonly IHttpClientFactory _httpClientFactory;

    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(15);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public PatchInstaller(
        ILogger<PatchInstaller> logger,
        ServerLocator serverLocator,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _serverLocator = serverLocator;
        _httpClientFactory = httpClientFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PatchInstaller started. Checking every {Interval}m", CheckInterval.TotalMinutes);

        // Wait for initial server discovery
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAndInstallPatchesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during patch check cycle");
            }

            try
            {
                await Task.Delay(CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("PatchInstaller stopped");
    }

    private async Task CheckAndInstallPatchesAsync(CancellationToken ct)
    {
        var serverUrl = _serverLocator.GetCachedServerUrl();
        if (string.IsNullOrEmpty(serverUrl))
            return;

        var macAddress = GetPrimaryMacAddress();
        var client = _httpClientFactory.CreateClient("RollyRollServer");
        if (client.BaseAddress == null)
            client.BaseAddress = new Uri(serverUrl);

        // Get approved patches for this client
        var response = await client.GetAsync($"/api/agent/patches/{Uri.EscapeDataString(macAddress)}", ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogDebug("No patches available or server unavailable: {Status}", response.StatusCode);
            return;
        }

        var patches = await response.Content.ReadFromJsonAsync<List<PatchAssignment>>(JsonOptions, ct);
        if (patches == null || patches.Count == 0)
        {
            _logger.LogDebug("No pending patches for this client");
            return;
        }

        _logger.LogInformation("Found {Count} pending patches to install", patches.Count);

        // Request a recovery snapshot before patching
        await RequestRecoverySnapshotAsync(client, macAddress, patches, ct);

        foreach (var patch in patches)
        {
            if (ct.IsCancellationRequested) break;

            await InstallPatchAsync(client, macAddress, patch, ct);
        }
    }

    private async Task RequestRecoverySnapshotAsync(
        HttpClient client,
        string macAddress,
        List<PatchAssignment> patches,
        CancellationToken ct)
    {
        try
        {
            var patchNames = string.Join(", ", patches.Select(p => p.Title).Take(3));
            var reason = $"Pre-patch snapshot: {patchNames}";

            var payload = new { MacAddress = macAddress, Reason = reason };
            await client.PostAsJsonAsync("/api/agent/recovery/snapshot", payload, ct);
            _logger.LogInformation("Recovery snapshot requested before patching");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create recovery snapshot. Proceeding with patching");
        }
    }

    private async Task InstallPatchAsync(
        HttpClient client,
        string macAddress,
        PatchAssignment patch,
        CancellationToken ct)
    {
        _logger.LogInformation("Installing patch: {Title} (ID={Id})", patch.Title, patch.PatchId);

        var success = false;
        string? errorMessage = null;

        try
        {
            // Download the patch installer to a temp location
            var tempDir = Path.Combine(Path.GetTempPath(), "RollyRoll", "Patches");
            Directory.CreateDirectory(tempDir);

            var installerPath = Path.Combine(tempDir, patch.FileName);

            using (var downloadResponse = await client.GetAsync(
                $"/api/agent/patches/{patch.PatchId}/download",
                HttpCompletionOption.ResponseHeadersRead, ct))
            {
                downloadResponse.EnsureSuccessStatusCode();

                await using var contentStream = await downloadResponse.Content.ReadAsStreamAsync(ct);
                await using var fileStream = new FileStream(installerPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await contentStream.CopyToAsync(fileStream, ct);
            }

            _logger.LogInformation("Patch downloaded to {Path}", installerPath);

            // Install the patch silently
            var exitCode = await RunInstallerAsync(installerPath, patch.SilentInstallArgs, ct);

            if (exitCode == 0 || exitCode == 3010) // 3010 = success, reboot required
            {
                success = true;
                _logger.LogInformation("Patch {Title} installed successfully (exit code: {ExitCode})",
                    patch.Title, exitCode);

                if (exitCode == 3010)
                {
                    _logger.LogInformation("Patch {Title} requires a reboot", patch.Title);
                }
            }
            else
            {
                errorMessage = $"Installer exited with code {exitCode}";
                _logger.LogError("Patch {Title} installation failed: {Error}", patch.Title, errorMessage);
            }

            // Cleanup installer
            try { File.Delete(installerPath); } catch { /* best effort */ }
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            _logger.LogError(ex, "Failed to install patch {Title}", patch.Title);
        }

        // Report result to server
        try
        {
            var result = new
            {
                MacAddress = macAddress,
                PatchId = patch.PatchId,
                Success = success,
                ErrorMessage = errorMessage
            };

            await client.PostAsJsonAsync("/api/agent/patches/result", result, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to report patch result for {Title}", patch.Title);
        }
    }

    private async Task<int> RunInstallerAsync(string installerPath, string? silentArgs, CancellationToken ct)
    {
        string fileName;
        string arguments;

        if (installerPath.EndsWith(".msu", StringComparison.OrdinalIgnoreCase))
        {
            // Windows Update standalone installer
            fileName = "wusa.exe";
            arguments = $"\"{installerPath}\" /quiet /norestart";
        }
        else if (installerPath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
        {
            fileName = "msiexec.exe";
            arguments = $"/i \"{installerPath}\" /qn /norestart {silentArgs}";
        }
        else
        {
            // EXE installer
            fileName = installerPath;
            arguments = silentArgs ?? "/S /quiet /norestart";
        }

        _logger.LogDebug("Running installer: {FileName} {Args}", fileName, arguments);

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start installer: {fileName}");

        // Wait up to 30 minutes for installation
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(30));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"Installer timed out after 30 minutes: {fileName}");
        }

        return process.ExitCode;
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

    /// <summary>
    /// DTO for patch assignments received from the server.
    /// </summary>
    private sealed class PatchAssignment
    {
        public int PatchId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string? SilentInstallArgs { get; set; }
        public bool RequiresReboot { get; set; }
    }
}
