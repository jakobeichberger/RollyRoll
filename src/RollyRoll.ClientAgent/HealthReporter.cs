using System.Net.Http.Json;
using System.Net.NetworkInformation;

namespace RollyRoll.ClientAgent;

/// <summary>
/// Sends periodic heartbeat reports to the RollyRoll server every 60 seconds.
/// Reports: MAC address, hostname, IP, OS version, hardware model, and online status.
/// Detects IP/hostname changes and reports them immediately.
/// </summary>
public class HealthReporter : BackgroundService
{
    private readonly ILogger<HealthReporter> _logger;
    private readonly ServerLocator _serverLocator;
    private readonly IHttpClientFactory _httpClientFactory;

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(60);

    private string _lastReportedHostname = string.Empty;
    private string _lastReportedIp = string.Empty;

    public HealthReporter(
        ILogger<HealthReporter> logger,
        ServerLocator serverLocator,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _serverLocator = serverLocator;
        _httpClientFactory = httpClientFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("HealthReporter started. Heartbeat interval: {Interval}s", HeartbeatInterval.TotalSeconds);

        // Small initial delay to let the server locator finish discovery
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SendHeartbeatAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send heartbeat");
            }

            try
            {
                await Task.Delay(HeartbeatInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("HealthReporter stopped");
    }

    private async Task SendHeartbeatAsync(CancellationToken ct)
    {
        var serverUrl = _serverLocator.GetCachedServerUrl();
        if (string.IsNullOrEmpty(serverUrl))
        {
            // Try to rediscover the server
            try
            {
                serverUrl = await _serverLocator.DiscoverAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Server discovery failed during heartbeat");
                return;
            }
        }

        var macAddress = GetPrimaryMacAddress();
        var hostname = Environment.MachineName;
        var ipAddress = GetPrimaryIpAddress();
        var osVersion = Environment.OSVersion.ToString();
        var hardwareModel = GetHardwareModel();

        // Detect changes
        var changed = hostname != _lastReportedHostname || ipAddress != _lastReportedIp;
        if (changed)
        {
            _logger.LogInformation(
                "Detected change: hostname={OldHost}->{NewHost}, IP={OldIp}->{NewIp}",
                _lastReportedHostname, hostname, _lastReportedIp, ipAddress);
        }

        var heartbeat = new
        {
            MacAddress = macAddress,
            Hostname = hostname,
            IpAddress = ipAddress,
            OsVersion = osVersion,
            HardwareModel = hardwareModel,
            IsOnline = true,
            WolEnabled = IsWolEnabledOnPrimaryNic()
        };

        try
        {
            var client = _httpClientFactory.CreateClient("RollyRollServer");
            if (client.BaseAddress == null)
            {
                client.BaseAddress = new Uri(serverUrl);
            }

            var response = await client.PostAsJsonAsync("/api/agent/heartbeat", heartbeat, ct);
            response.EnsureSuccessStatusCode();

            _lastReportedHostname = hostname;
            _lastReportedIp = ipAddress;

            _logger.LogDebug("Heartbeat sent: MAC={Mac}, Host={Host}, IP={Ip}", macAddress, hostname, ipAddress);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send heartbeat to {Server}", serverUrl);
        }
    }

    private static string GetPrimaryMacAddress()
    {
        var nic = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .OrderByDescending(n => n.Speed)
            .FirstOrDefault();

        if (nic == null)
            return "00:00:00:00:00:00";

        var macBytes = nic.GetPhysicalAddress().GetAddressBytes();
        return string.Join(":", macBytes.Select(b => b.ToString("X2")));
    }

    private static string GetPrimaryIpAddress()
    {
        try
        {
            var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
            var ip = host.AddressList
                .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            return ip?.ToString() ?? "0.0.0.0";
        }
        catch
        {
            return "0.0.0.0";
        }
    }

    private static string GetHardwareModel()
    {
        try
        {
            // Read from WMI via process call (works without WMI .NET dependency)
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "wmic",
                Arguments = "computersystem get manufacturer,model /format:list",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null) return "Unknown";

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            var manufacturer = string.Empty;
            var model = string.Empty;

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("Manufacturer=", StringComparison.OrdinalIgnoreCase))
                    manufacturer = line.Split('=', 2)[1].Trim();
                else if (line.StartsWith("Model=", StringComparison.OrdinalIgnoreCase))
                    model = line.Split('=', 2)[1].Trim();
            }

            return $"{manufacturer} {model}".Trim();
        }
        catch
        {
            return "Unknown";
        }
    }

    /// <summary>
    /// Quick check if Wake-on-Magic-Packet is enabled on the primary NIC.
    /// Reports this in the heartbeat so the server dashboard can show WoL readiness.
    /// </summary>
    private static bool IsWolEnabledOnPrimaryNic()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -Command \"Get-NetAdapterPowerManagement | Select-Object -First 1 -ExpandProperty WakeOnMagicPacket\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null) return false;

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();

            return output.Equals("Enabled", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
