using System.Management;
using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RollyRoll.ClientAgent;

/// <summary>
/// Sends periodic heartbeats to the RollyRoll server.
/// Reports: MAC, hostname, IP, OS version, hardware model.
/// Detects and reports IP/hostname changes automatically.
/// </summary>
public class HealthReporter : BackgroundService
{
    private readonly ILogger<HealthReporter> _logger;
    private readonly ServerLocator _serverLocator;
    private readonly TimeSpan _heartbeatInterval = TimeSpan.FromSeconds(60);
    private string? _lastHostname;
    private string? _lastIpAddress;

    public HealthReporter(ILogger<HealthReporter> logger, ServerLocator serverLocator)
    {
        _logger = logger;
        _serverLocator = serverLocator;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("HealthReporter started, interval: {Interval}s", _heartbeatInterval.TotalSeconds);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SendHeartbeatAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send heartbeat");
            }

            await Task.Delay(_heartbeatInterval, ct);
        }
    }

    private async Task SendHeartbeatAsync(CancellationToken ct)
    {
        var serverUrl = await _serverLocator.GetServerUrlAsync(ct);
        var mac = GetPrimaryMacAddress();
        var hostname = Environment.MachineName;
        var ipAddress = GetPrimaryIpAddress();
        var osVersion = Environment.OSVersion.VersionString;

        // Detect changes
        if (_lastHostname != null && (_lastHostname != hostname || _lastIpAddress != ipAddress))
        {
            _logger.LogInformation("Network change detected: {OldHost}/{OldIp} -> {NewHost}/{NewIp}",
                _lastHostname, _lastIpAddress, hostname, ipAddress);
        }
        _lastHostname = hostname;
        _lastIpAddress = ipAddress;

        var heartbeat = new
        {
            MacAddress = mac,
            Hostname = hostname,
            IpAddress = ipAddress,
            OsVersion = osVersion,
            HardwareModel = GetHardwareModel(),
            SerialNumber = GetSerialNumber()
        };

        using var http = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        });

        await http.PostAsJsonAsync($"{serverUrl}/api/client/heartbeat", heartbeat, ct);
        _logger.LogDebug("Heartbeat sent: {Host} ({Mac})", hostname, mac);
    }

    private static string GetPrimaryMacAddress()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
            .OrderByDescending(n => n.Speed)
            .Select(n => BitConverter.ToString(n.GetPhysicalAddress().GetAddressBytes()).Replace("-", ":"))
            .FirstOrDefault() ?? "00:00:00:00:00:00";
    }

    private static string GetPrimaryIpAddress()
    {
        try
        {
            using var socket = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);
            return ((IPEndPoint)socket.LocalEndPoint!).Address.ToString();
        }
        catch { return "127.0.0.1"; }
    }

    private static string GetHardwareModel()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem");
            foreach (var obj in searcher.Get())
                return $"{obj["Manufacturer"]} {obj["Model"]}";
        }
        catch { }
        return "Unknown";
    }

    private static string GetSerialNumber()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BIOS");
            foreach (var obj in searcher.Get())
                return obj["SerialNumber"]?.ToString() ?? "";
        }
        catch { }
        return "";
    }
}
