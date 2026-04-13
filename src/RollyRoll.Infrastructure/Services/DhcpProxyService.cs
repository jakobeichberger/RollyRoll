using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RollyRoll.Core.Interfaces;
using RollyRoll.Core.Models;

namespace RollyRoll.Infrastructure.Services;

/// <summary>
/// DHCP Proxy service for PXE boot. Does NOT replace the existing DHCP server.
/// Listens for PXE DHCP Discover packets, detects client architecture (UEFI/BIOS)
/// via Option 93, and responds with the appropriate iPXE boot file.
///
/// Boot file selection:
///   - BIOS (Option 93 = 0)    -> undionly.kpxe
///   - UEFI 64-bit (Option 93 = 7/9) -> ipxe.efi
///   - UEFI 32-bit (Option 93 = 6)   -> ipxe32.efi
///
/// Uses IServiceScopeFactory to resolve scoped services (IClientDiscoveryService)
/// since this service is registered as a singleton.
/// </summary>
public class DhcpProxyService : IDisposable
{
    private readonly ILogger<DhcpProxyService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly string _tftpServerIp;
    private UdpClient? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private int _biosRequests;
    private int _uefiRequests;

    // DHCP Option numbers
    private const byte OptMessageType = 53;
    private const byte OptServerIdentifier = 54;
    private const byte OptVendorClassId = 60;
    private const byte OptClientSystemArch = 93;
    private const byte OptEnd = 255;

    // DHCP message types
    private const byte DhcpOffer = 2;

    public DhcpProxyService(
        ILogger<DhcpProxyService> logger,
        IServiceScopeFactory scopeFactory,
        string tftpServerIp)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _tftpServerIp = tftpServerIp;
    }

    public bool IsRunning => _listener != null;
    public int BiosRequests => _biosRequests;
    public int UefiRequests => _uefiRequests;

    public Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Listen on DHCP port 4011 (PXE proxy) to avoid conflicting with existing DHCP server on port 67
        _listener = new UdpClient(new IPEndPoint(IPAddress.Any, 4011));
        _listener.EnableBroadcast = true;

        _listenTask = ListenLoopAsync(_cts.Token);
        _logger.LogInformation("DHCP Proxy started on port 4011, TFTP server IP: {TftpIp}", _tftpServerIp);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        _listener?.Close();
        if (_listenTask != null)
        {
            try { await _listenTask; }
            catch (OperationCanceledException) { /* expected */ }
        }
        _listener?.Dispose();
        _listener = null;
        _cts?.Dispose();
        _cts = null;
        _listenTask = null;
        _logger.LogInformation("DHCP Proxy stopped");
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _listener!.ReceiveAsync(ct);
                _ = HandleDhcpPacketAsync(result.Buffer, result.RemoteEndPoint, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DHCP Proxy listener error");
            }
        }
    }

    private async Task HandleDhcpPacketAsync(byte[] data, IPEndPoint remoteEp, CancellationToken ct)
    {
        if (data.Length < 240) return; // Minimum DHCP packet size

        // Check for PXE vendor class identifier
        var vendorClass = GetDhcpOption(data, OptVendorClassId);
        if (vendorClass == null || !System.Text.Encoding.ASCII.GetString(vendorClass).StartsWith("PXEClient"))
            return;

        // Get client MAC address (bytes 28-33 in DHCP packet)
        var macBytes = new byte[6];
        Array.Copy(data, 28, macBytes, 0, 6);
        var macAddress = BitConverter.ToString(macBytes).Replace("-", ":");

        // Detect client architecture from Option 93
        var archOption = GetDhcpOption(data, OptClientSystemArch);
        var bootType = BootType.LegacyBIOS;
        var bootFile = "undionly.kpxe";

        if (archOption != null && archOption.Length >= 2)
        {
            var archType = (ushort)((archOption[0] << 8) | archOption[1]);
            switch (archType)
            {
                case 0: // Intel x86 BIOS
                    bootType = BootType.LegacyBIOS;
                    bootFile = "undionly.kpxe";
                    Interlocked.Increment(ref _biosRequests);
                    break;
                case 6: // EFI IA32
                    bootType = BootType.UEFI32;
                    bootFile = "ipxe32.efi";
                    Interlocked.Increment(ref _uefiRequests);
                    break;
                case 7: // EFI BC (Byte Code)
                case 9: // EFI x86-64
                    bootType = BootType.UEFI;
                    bootFile = "ipxe.efi";
                    Interlocked.Increment(ref _uefiRequests);
                    break;
                default:
                    _logger.LogWarning("Unknown client architecture {Arch} from {Mac}", archType, macAddress);
                    bootFile = "ipxe.efi"; // Default to UEFI 64-bit
                    break;
            }
        }

        _logger.LogInformation("PXE boot request: MAC={Mac}, Arch={BootType}, BootFile={BootFile}", macAddress, bootType, bootFile);

        // Register/update the client in our database using a scoped service
        using (var scope = _scopeFactory.CreateScope())
        {
            var clientDiscovery = scope.ServiceProvider.GetRequiredService<IClientDiscoveryService>();
            await clientDiscovery.RegisterFromPxeBootAsync(macAddress, bootType, remoteEp.Address.ToString(), ct);
        }

        // Build and send DHCP Offer/Ack with boot file information
        var response = BuildProxyResponse(data, bootFile);
        if (response != null)
        {
            using var sendSocket = new UdpClient();
            sendSocket.EnableBroadcast = true;
            await sendSocket.SendAsync(response, response.Length, new IPEndPoint(IPAddress.Broadcast, 68));
        }
    }

    private byte[]? BuildProxyResponse(byte[] request, string bootFile)
    {
        // Build a minimal DHCP proxy response
        var response = new byte[576]; // RFC 2131 minimum DHCP packet size
        response[0] = 2; // BOOTREPLY
        response[1] = request[1]; // Hardware type
        response[2] = request[2]; // Hardware address length
        response[3] = 0; // Hops

        // Copy transaction ID (bytes 4-7)
        Array.Copy(request, 4, response, 4, 4);

        // Copy client hardware address (bytes 28-43)
        Array.Copy(request, 28, response, 28, 16);

        // Set TFTP server IP in siaddr field (bytes 20-23)
        var serverIpBytes = IPAddress.Parse(_tftpServerIp).GetAddressBytes();
        Array.Copy(serverIpBytes, 0, response, 20, 4);

        // Set boot filename (bytes 108-235, null-terminated)
        var bootFileBytes = System.Text.Encoding.ASCII.GetBytes(bootFile);
        Array.Copy(bootFileBytes, 0, response, 108, Math.Min(bootFileBytes.Length, 127));

        // DHCP magic cookie (bytes 236-239)
        response[236] = 99; response[237] = 130; response[238] = 83; response[239] = 99;

        // DHCP options
        var optOffset = 240;

        // Option 53: DHCP Message Type = Offer (2)
        response[optOffset++] = OptMessageType;
        response[optOffset++] = 1;
        response[optOffset++] = DhcpOffer;

        // Option 54: Server Identifier
        response[optOffset++] = OptServerIdentifier;
        response[optOffset++] = 4;
        Array.Copy(serverIpBytes, 0, response, optOffset, 4);
        optOffset += 4;

        // Option 60: Vendor Class = PXEClient
        var pxeClient = System.Text.Encoding.ASCII.GetBytes("PXEClient");
        response[optOffset++] = OptVendorClassId;
        response[optOffset++] = (byte)pxeClient.Length;
        Array.Copy(pxeClient, 0, response, optOffset, pxeClient.Length);
        optOffset += pxeClient.Length;

        // End option
        response[optOffset] = OptEnd;

        return response;
    }

    private static byte[]? GetDhcpOption(byte[] data, byte optionNumber)
    {
        // DHCP options start at byte 240 (after magic cookie at 236-239)
        var offset = 240;
        while (offset < data.Length)
        {
            var option = data[offset++];
            if (option == OptEnd) break;
            if (option == 0) continue; // Padding

            if (offset >= data.Length) break;
            var length = data[offset++];

            if (option == optionNumber)
            {
                var value = new byte[length];
                Array.Copy(data, offset, value, 0, Math.Min(length, data.Length - offset));
                return value;
            }

            offset += length;
        }
        return null;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        // Give the listen loop a moment to exit before disposing resources
        try { _listenTask?.Wait(TimeSpan.FromSeconds(2)); }
        catch { /* best effort */ }
        _listener?.Dispose();
        _listener = null;
        _cts?.Dispose();
        _cts = null;
        _listenTask = null;
    }
}
