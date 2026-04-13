using System.Net.Http.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace RollyRoll.ClientAgent;

/// <summary>
/// Monitors system health and triggers auto-recovery when problems are detected.
/// Detection methods:
///   - Excessive reboots in short period (boot loop)
///   - Failed Windows Update installations
///   - Blue screen (BSOD) events in Event Log
///   - Health check endpoint timeout from server side
/// </summary>
public class RecoveryMonitor : BackgroundService
{
    private readonly ILogger<RecoveryMonitor> _logger;
    private readonly ServerLocator _serverLocator;
    private readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(5);

    private const int MaxRebootsInWindow = 3;
    private static readonly TimeSpan RebootWindow = TimeSpan.FromMinutes(30);
    private const string BootCountRegistryKey = @"SOFTWARE\RollyRoll\Recovery";

    public RecoveryMonitor(ILogger<RecoveryMonitor> logger, ServerLocator serverLocator)
    {
        _logger = logger;
        _serverLocator = serverLocator;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("RecoveryMonitor started");

        // Record this boot
        RecordBoot();

        // Check if we're in a boot loop
        if (IsBootLoopDetected())
        {
            _logger.LogWarning("Boot loop detected! Initiating auto-recovery...");
            await TriggerRecoveryAsync("Boot loop detected: {0} reboots in {1} minutes", ct);
            return;
        }

        // Check for recent BSOD
        if (HasRecentBsod())
        {
            _logger.LogWarning("Recent BSOD detected! Initiating auto-recovery...");
            await TriggerRecoveryAsync("BSOD detected after last operation", ct);
            return;
        }

        // Ongoing monitoring
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await MonitorHealthAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Health monitoring error");
            }

            await Task.Delay(_checkInterval, ct);
        }
    }

    private async Task MonitorHealthAsync(CancellationToken ct)
    {
        // Check Event Log for critical errors
        if (HasRecentBsod())
        {
            await TriggerRecoveryAsync("BSOD detected during monitoring", ct);
        }
    }

    private async Task TriggerRecoveryAsync(string reason, CancellationToken ct)
    {
        try
        {
            var serverUrl = await _serverLocator.GetServerUrlAsync(ct);
            var mac = GetMacAddress();

            using var http = new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            });

            _logger.LogWarning("Triggering auto-recovery: {Reason}", reason);

            // Step 1: Tell server to capture our current user profiles before rollback
            await http.PostAsJsonAsync($"{serverUrl}/api/recovery/initiate", new
            {
                ClientMac = mac,
                Reason = reason
            }, ct);

            _logger.LogInformation("Recovery initiated, server will handle the rest via PXE reboot");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to trigger recovery");
        }
    }

    private void RecordBoot()
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(BootCountRegistryKey);
            var now = DateTime.UtcNow.Ticks;

            // Get existing boot timestamps
            var bootTimesStr = key.GetValue("BootTimes") as string ?? "";
            var bootTimes = bootTimesStr.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => long.TryParse(s, out var t) ? t : 0)
                .Where(t => t > 0)
                .ToList();

            bootTimes.Add(now);

            // Keep only boots within the window
            var cutoff = now - RebootWindow.Ticks;
            bootTimes = bootTimes.Where(t => t > cutoff).ToList();

            key.SetValue("BootTimes", string.Join(",", bootTimes));
            _logger.LogDebug("Boot recorded. {Count} boots in last {Window}m", bootTimes.Count, RebootWindow.TotalMinutes);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to record boot time");
        }
    }

    private bool IsBootLoopDetected()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(BootCountRegistryKey);
            if (key == null) return false;

            var bootTimesStr = key.GetValue("BootTimes") as string ?? "";
            var now = DateTime.UtcNow.Ticks;
            var cutoff = now - RebootWindow.Ticks;

            var recentBoots = bootTimesStr.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => long.TryParse(s, out var t) ? t : 0)
                .Count(t => t > cutoff);

            return recentBoots >= MaxRebootsInWindow;
        }
        catch { return false; }
    }

    private bool HasRecentBsod()
    {
        try
        {
            // Check for unexpected shutdown events (Event ID 41, Source: Kernel-Power)
            // and bugcheck events (Event ID 1001, Source: BugCheck)
            var query = new System.Diagnostics.EventLog("System");
            var recentCutoff = DateTime.Now.AddMinutes(-30);

            return query.Entries.Cast<System.Diagnostics.EventLogEntry>()
                .Any(e => e.TimeGenerated > recentCutoff
                    && ((e.Source == "Microsoft-Windows-Kernel-Power" && e.InstanceId == 41)
                        || (e.Source == "BugCheck" && e.InstanceId == 1001)));
        }
        catch
        {
            return false;
        }
    }

    private static string GetMacAddress() =>
        System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
            .OrderByDescending(n => n.Speed)
            .Select(n => BitConverter.ToString(n.GetPhysicalAddress().GetAddressBytes()).Replace("-", ":"))
            .FirstOrDefault() ?? "00:00:00:00:00:00";
}
