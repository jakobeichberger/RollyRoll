using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using RollyRoll.Core.Interfaces;
using RollyRoll.Infrastructure.Data;

namespace RollyRoll.Infrastructure.Services;

/// <summary>
/// Sends Wake-on-LAN magic packets to power on PCs remotely.
/// Magic packet = 6 bytes of 0xFF followed by the target MAC address repeated 16 times.
/// Sent as UDP broadcast on port 9 (or 7).
/// Supports cross-subnet via directed broadcast address.
/// </summary>
public class WakeOnLanService : IWakeOnLanService
{
    private readonly ILogger<WakeOnLanService> _logger;
    private readonly RollyRollDbContext _db;
    private readonly int _wolPort;
    private readonly int _retryCount;
    private readonly int _retryDelayMs;

    public WakeOnLanService(
        ILogger<WakeOnLanService> logger,
        RollyRollDbContext db,
        int wolPort = 9,
        int retryCount = 3,
        int retryDelayMs = 500)
    {
        _logger = logger;
        _db = db;
        _wolPort = wolPort;
        _retryCount = retryCount;
        _retryDelayMs = retryDelayMs;
    }

    public async Task SendWakeOnLanAsync(string macAddress, CancellationToken ct = default)
    {
        var magicPacket = BuildMagicPacket(macAddress);

        for (var attempt = 0; attempt < _retryCount; attempt++)
        {
            try
            {
                using var client = new UdpClient();
                client.EnableBroadcast = true;
                await client.SendAsync(magicPacket, magicPacket.Length, new IPEndPoint(IPAddress.Broadcast, _wolPort));
                _logger.LogInformation("WoL magic packet sent to {Mac} (attempt {Attempt})", macAddress, attempt + 1);

                if (attempt < _retryCount)
                    await Task.Delay(_retryDelayMs, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send WoL packet to {Mac} (attempt {Attempt})", macAddress, attempt + 1);
            }
        }
    }

    public async Task SendWakeOnLanAsync(IEnumerable<string> macAddresses, CancellationToken ct = default)
    {
        var tasks = macAddresses.Select(mac => SendWakeOnLanAsync(mac, ct));
        await Task.WhenAll(tasks);
    }

    public async Task WakeGroupAsync(int groupId, CancellationToken ct = default)
    {
        var clients = await _db.Clients
            .Where(c => c.GroupId == groupId)
            .Select(c => c.MacAddress)
            .ToListAsync(ct);

        _logger.LogInformation("Waking group {GroupId}: {Count} clients", groupId, clients.Count);
        await SendWakeOnLanAsync(clients, ct);
    }

    public async Task SendDirectedWakeOnLanAsync(string macAddress, string subnetBroadcastAddress, CancellationToken ct = default)
    {
        var magicPacket = BuildMagicPacket(macAddress);
        var broadcastIp = IPAddress.Parse(subnetBroadcastAddress);

        for (var attempt = 0; attempt < _retryCount; attempt++)
        {
            try
            {
                using var client = new UdpClient();
                client.EnableBroadcast = true;
                await client.SendAsync(magicPacket, magicPacket.Length, new IPEndPoint(broadcastIp, _wolPort));
                _logger.LogInformation("Directed WoL sent to {Mac} via {Broadcast} (attempt {Attempt})",
                    macAddress, subnetBroadcastAddress, attempt + 1);

                if (attempt < _retryCount)
                    await Task.Delay(_retryDelayMs, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send directed WoL to {Mac} via {Broadcast}", macAddress, subnetBroadcastAddress);
            }
        }
    }

    /// <summary>
    /// Build a Wake-on-LAN magic packet.
    /// Format: 6 bytes of 0xFF + target MAC repeated 16 times = 102 bytes total.
    /// </summary>
    private static byte[] BuildMagicPacket(string macAddress)
    {
        var macBytes = ParseMacAddress(macAddress);
        var packet = new byte[6 + 16 * 6]; // 102 bytes

        // 6 bytes of 0xFF
        for (var i = 0; i < 6; i++)
            packet[i] = 0xFF;

        // MAC address repeated 16 times
        for (var i = 0; i < 16; i++)
            Array.Copy(macBytes, 0, packet, 6 + i * 6, 6);

        return packet;
    }

    private static byte[] ParseMacAddress(string mac)
    {
        var cleaned = mac.Replace(":", "").Replace("-", "").Replace(".", "");
        if (cleaned.Length != 12)
            throw new ArgumentException($"Invalid MAC address: {mac}");

        return Enumerable.Range(0, 6)
            .Select(i => Convert.ToByte(cleaned.Substring(i * 2, 2), 16))
            .ToArray();
    }
}
