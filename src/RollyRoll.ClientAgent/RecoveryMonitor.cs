using System.Net.Http.Json;

namespace RollyRoll.ClientAgent;

/// <summary>
/// Monitors system health and detects conditions that warrant auto-recovery.
/// Detects:
///   - Boot failures (excessive reboots within a time window)
///   - Patch failures (failed patches reported by PatchInstaller)
///   - Deployment issues (OS not booting correctly after deployment)
/// Triggers auto-recovery by notifying the RollyRoll server when problems are detected.
/// </summary>
public class RecoveryMonitor : BackgroundService
{
    private readonly ILogger<RecoveryMonitor> _logger;
    private readonly ServerLocator _serverLocator;
    private readonly IHttpClientFactory _httpClientFactory;

    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);
    private static readonly string BootTimestampPath = Path.Combine(AppContext.BaseDirectory, "boot_timestamps.txt");

    /// <summary>Maximum number of reboots within the tracking window before triggering recovery.</summary>
    private const int MaxRebootsInWindow = 5;

    /// <summary>Time window for tracking excessive reboots.</summary>
    private static readonly TimeSpan RebootTrackingWindow = TimeSpan.FromMinutes(30);

    public RecoveryMonitor(
        ILogger<RecoveryMonitor> logger,
        ServerLocator serverLocator,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _serverLocator = serverLocator;
        _httpClientFactory = httpClientFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("RecoveryMonitor started. Checking every {Interval}m", CheckInterval.TotalMinutes);

        // Record this boot on startup
        await RecordBootAsync();

        // Check for excessive reboots immediately
        if (await DetectExcessiveRebootsAsync())
        {
            _logger.LogWarning("Excessive reboots detected on startup. Triggering auto-recovery");
            await TriggerAutoRecoveryAsync("Excessive reboots detected", stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PerformHealthCheckAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during health check");
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

        _logger.LogInformation("RecoveryMonitor stopped");
    }

    private async Task PerformHealthCheckAsync(CancellationToken ct)
    {
        // Check for critical system issues
        var issues = new List<string>();

        // Check for critical event log entries
        if (await DetectCriticalEventLogEntriesAsync())
        {
            issues.Add("Critical system errors found in event log");
        }

        // Check for disk health
        if (DetectLowDiskSpace())
        {
            issues.Add("System drive critically low on disk space");
        }

        // Check for pending failed updates
        if (DetectFailedWindowsUpdates())
        {
            issues.Add("Multiple Windows Update failures detected");
        }

        if (issues.Count > 0)
        {
            _logger.LogWarning("Health check detected {Count} issues: {Issues}",
                issues.Count, string.Join("; ", issues));

            // Report issues to server (server decides if recovery is needed)
            await ReportHealthIssuesAsync(issues, ct);
        }
        else
        {
            _logger.LogDebug("Health check passed — no issues detected");
        }
    }

    /// <summary>
    /// Record the current boot time for reboot tracking.
    /// </summary>
    private async Task RecordBootAsync()
    {
        try
        {
            var timestamps = new List<DateTime>();

            if (File.Exists(BootTimestampPath))
            {
                var lines = await File.ReadAllLinesAsync(BootTimestampPath);
                foreach (var line in lines)
                {
                    if (DateTime.TryParse(line, out var ts))
                        timestamps.Add(ts);
                }
            }

            timestamps.Add(DateTime.UtcNow);

            // Keep only timestamps within the tracking window
            timestamps = timestamps
                .Where(ts => DateTime.UtcNow - ts < RebootTrackingWindow)
                .ToList();

            await File.WriteAllLinesAsync(BootTimestampPath,
                timestamps.Select(ts => ts.ToString("O")));

            _logger.LogDebug("Recorded boot. {Count} boots in tracking window", timestamps.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record boot timestamp");
        }
    }

    /// <summary>
    /// Detect if the system has rebooted excessively within the tracking window.
    /// </summary>
    private async Task<bool> DetectExcessiveRebootsAsync()
    {
        try
        {
            if (!File.Exists(BootTimestampPath))
                return false;

            var lines = await File.ReadAllLinesAsync(BootTimestampPath);
            var recentBoots = lines
                .Select(line => DateTime.TryParse(line, out var ts) ? ts : DateTime.MinValue)
                .Where(ts => ts != DateTime.MinValue)
                .Where(ts => DateTime.UtcNow - ts < RebootTrackingWindow)
                .Count();

            if (recentBoots >= MaxRebootsInWindow)
            {
                _logger.LogWarning("Excessive reboots: {Count} in the last {Window} minutes",
                    recentBoots, RebootTrackingWindow.TotalMinutes);
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check reboot history");
        }

        return false;
    }

    /// <summary>
    /// Check the Windows Event Log for critical errors (BSOD, service crashes, etc.).
    /// </summary>
    private Task<bool> DetectCriticalEventLogEntriesAsync()
    {
        try
        {
            // Check for BugCheck (BSOD) events in the System log
            // Event ID 1001 (BugCheck) or 6008 (Unexpected shutdown)
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "wevtutil",
                Arguments = "qe System /q:\"*[System[(EventID=1001 or EventID=6008) and TimeCreated[timediff(@SystemTime) <= 3600000]]]\" /c:1 /f:text",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null) return Task.FromResult(false);

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            return Task.FromResult(!string.IsNullOrWhiteSpace(output));
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Check if the system drive is critically low on space (less than 1 GB).
    /// </summary>
    private bool DetectLowDiskSpace()
    {
        try
        {
            var systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var driveInfo = new DriveInfo(systemDrive);
            var freeGb = driveInfo.AvailableFreeSpace / (1024.0 * 1024 * 1024);

            if (freeGb < 1.0)
            {
                _logger.LogWarning("System drive has only {FreeGb:F2} GB free", freeGb);
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to check disk space");
        }

        return false;
    }

    /// <summary>
    /// Check for repeated Windows Update failures.
    /// </summary>
    private bool DetectFailedWindowsUpdates()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "wevtutil",
                Arguments = "qe Setup /q:\"*[System[(EventID=2 or EventID=3) and TimeCreated[timediff(@SystemTime) <= 86400000]]]\" /c:5 /f:text",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null) return false;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            // If there are multiple failure events, flag it
            var failureCount = output.Split("Event[", StringSplitOptions.RemoveEmptyEntries).Length - 1;
            return failureCount >= 3;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Report detected health issues to the server. Server decides if recovery is needed.
    /// </summary>
    private async Task ReportHealthIssuesAsync(List<string> issues, CancellationToken ct)
    {
        try
        {
            var serverUrl = _serverLocator.GetCachedServerUrl();
            if (string.IsNullOrEmpty(serverUrl)) return;

            var macAddress = GetPrimaryMacAddress();
            var client = _httpClientFactory.CreateClient("RollyRollServer");
            if (client.BaseAddress == null)
                client.BaseAddress = new Uri(serverUrl);

            var report = new
            {
                MacAddress = macAddress,
                Issues = issues,
                Timestamp = DateTime.UtcNow
            };

            await client.PostAsJsonAsync("/api/agent/health/issues", report, ct);
            _logger.LogInformation("Health issues reported to server");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to report health issues to server");
        }
    }

    /// <summary>
    /// Trigger auto-recovery by requesting the server to initiate a recovery deployment.
    /// </summary>
    private async Task TriggerAutoRecoveryAsync(string reason, CancellationToken ct)
    {
        try
        {
            var serverUrl = _serverLocator.GetCachedServerUrl();
            if (string.IsNullOrEmpty(serverUrl))
            {
                serverUrl = await _serverLocator.DiscoverAsync(ct);
            }

            var macAddress = GetPrimaryMacAddress();
            var client = _httpClientFactory.CreateClient("RollyRollServer");
            if (client.BaseAddress == null)
                client.BaseAddress = new Uri(serverUrl);

            var request = new
            {
                MacAddress = macAddress,
                Reason = reason,
                Timestamp = DateTime.UtcNow
            };

            var response = await client.PostAsJsonAsync("/api/agent/recovery/trigger", request, ct);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Auto-recovery triggered: {Reason}", reason);
            }
            else
            {
                _logger.LogError("Server rejected auto-recovery request: {Status}", response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to trigger auto-recovery");
        }
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
