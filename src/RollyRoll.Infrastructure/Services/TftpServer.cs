using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace RollyRoll.Infrastructure.Services;

/// <summary>
/// Lightweight TFTP server for serving iPXE boot files.
/// Only serves small boot files (~100KB) — actual image data goes over HTTPS.
/// Implements RFC 1350 (TFTP) with RFC 2347 (Option Extension) for block size negotiation.
/// </summary>
public class TftpServer : IDisposable
{
    private readonly ILogger<TftpServer> _logger;
    private readonly string _tftpRoot;
    private readonly int _port;
    private UdpClient? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private int _filesServed;
    private int _errors;

    // TFTP opcodes
    private const ushort OpRrq = 1;   // Read request
    private const ushort OpData = 3;  // Data
    private const ushort OpAck = 4;   // Acknowledgment
    private const ushort OpError = 5; // Error
    private const ushort OpOack = 6;  // Option acknowledgment

    public TftpServer(ILogger<TftpServer> logger, string tftpRoot, int port = 69)
    {
        _logger = logger;
        _tftpRoot = tftpRoot;
        _port = port;
    }

    public bool IsRunning => _listener != null;
    public int FilesServed => _filesServed;
    public int Errors => _errors;

    public Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listener = new UdpClient(new IPEndPoint(IPAddress.Any, _port));
        _listenTask = ListenLoopAsync(_cts.Token);
        _logger.LogInformation("TFTP server started on port {Port}, root: {Root}", _port, _tftpRoot);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        _listener?.Close();
        if (_listenTask != null)
            await _listenTask;
        _listener = null;
        _logger.LogInformation("TFTP server stopped");
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _listener!.ReceiveAsync(ct);
                _ = HandleRequestAsync(result.Buffer, result.RemoteEndPoint, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _errors);
                _logger.LogError(ex, "TFTP listener error");
            }
        }
    }

    private async Task HandleRequestAsync(byte[] data, IPEndPoint remoteEp, CancellationToken ct)
    {
        if (data.Length < 4) return;

        var opcode = (ushort)((data[0] << 8) | data[1]);
        if (opcode != OpRrq)
        {
            await SendErrorAsync(remoteEp, 4, "Only read requests supported", ct);
            return;
        }

        // Parse filename from RRQ packet (null-terminated string after opcode)
        var filename = ParseNullTerminatedString(data, 2);
        if (string.IsNullOrEmpty(filename))
        {
            await SendErrorAsync(remoteEp, 1, "Invalid filename", ct);
            return;
        }

        // Sanitize path to prevent directory traversal
        filename = filename.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(_tftpRoot, filename));
        if (!fullPath.StartsWith(_tftpRoot, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("TFTP path traversal attempt: {Filename} from {Remote}", filename, remoteEp);
            await SendErrorAsync(remoteEp, 2, "Access denied", ct);
            return;
        }

        if (!File.Exists(fullPath))
        {
            _logger.LogWarning("TFTP file not found: {Path} requested by {Remote}", fullPath, remoteEp);
            await SendErrorAsync(remoteEp, 1, "File not found", ct);
            return;
        }

        _logger.LogInformation("TFTP serving {File} to {Remote}", filename, remoteEp);

        // Parse options (RFC 2347) for block size negotiation
        var blockSize = 512; // default TFTP block size
        var options = ParseOptions(data, 2);
        if (options.TryGetValue("blksize", out var blkSizeStr) && int.TryParse(blkSizeStr, out var requestedBlkSize))
        {
            blockSize = Math.Clamp(requestedBlkSize, 512, 65464);
        }

        // Send file using a new UDP socket for this transfer
        using var transferSocket = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        transferSocket.Client.ReceiveTimeout = 5000;

        // Send OACK if options were negotiated
        if (options.Count > 0)
        {
            var oack = BuildOackPacket(blockSize);
            await transferSocket.SendAsync(oack, oack.Length, remoteEp);
            // Wait for ACK of OACK (block 0)
            try { await transferSocket.ReceiveAsync(ct); }
            catch { return; }
        }

        // Read and send file in blocks
        var fileData = await File.ReadAllBytesAsync(fullPath, ct);
        ushort blockNum = 1;
        var offset = 0;

        while (offset < fileData.Length || blockNum == 1)
        {
            var remaining = fileData.Length - offset;
            var chunkSize = Math.Min(remaining, blockSize);
            var packet = BuildDataPacket(blockNum, fileData, offset, chunkSize);

            var retries = 0;
            while (retries < 3)
            {
                await transferSocket.SendAsync(packet, packet.Length, remoteEp);
                try
                {
                    var ack = await transferSocket.ReceiveAsync(ct);
                    var ackBlock = (ushort)((ack.Buffer[2] << 8) | ack.Buffer[3]);
                    if (ackBlock == blockNum) break;
                }
                catch (SocketException) { retries++; }
            }

            if (retries >= 3)
            {
                _logger.LogWarning("TFTP transfer timeout for {File} to {Remote}", filename, remoteEp);
                Interlocked.Increment(ref _errors);
                return;
            }

            offset += chunkSize;
            blockNum++;

            // Last block: if chunk was less than block size, transfer is complete
            if (chunkSize < blockSize) break;
        }

        Interlocked.Increment(ref _filesServed);
        _logger.LogInformation("TFTP transfer complete: {File} ({Bytes} bytes) to {Remote}", filename, fileData.Length, remoteEp);
    }

    private static byte[] BuildDataPacket(ushort blockNum, byte[] data, int offset, int length)
    {
        var packet = new byte[4 + length];
        packet[0] = (byte)(OpData >> 8);
        packet[1] = (byte)(OpData & 0xFF);
        packet[2] = (byte)(blockNum >> 8);
        packet[3] = (byte)(blockNum & 0xFF);
        Array.Copy(data, offset, packet, 4, length);
        return packet;
    }

    private static byte[] BuildOackPacket(int blockSize)
    {
        var oackStr = $"blksize\0{blockSize}\0";
        var payload = System.Text.Encoding.ASCII.GetBytes(oackStr);
        var packet = new byte[2 + payload.Length];
        packet[0] = (byte)(OpOack >> 8);
        packet[1] = (byte)(OpOack & 0xFF);
        Array.Copy(payload, 0, packet, 2, payload.Length);
        return packet;
    }

    private async Task SendErrorAsync(IPEndPoint remote, ushort errorCode, string message, CancellationToken ct)
    {
        var msgBytes = System.Text.Encoding.ASCII.GetBytes(message + "\0");
        var packet = new byte[4 + msgBytes.Length];
        packet[0] = (byte)(OpError >> 8);
        packet[1] = (byte)(OpError & 0xFF);
        packet[2] = (byte)(errorCode >> 8);
        packet[3] = (byte)(errorCode & 0xFF);
        Array.Copy(msgBytes, 0, packet, 4, msgBytes.Length);

        using var socket = new UdpClient();
        await socket.SendAsync(packet, packet.Length, remote);
    }

    private static string ParseNullTerminatedString(byte[] data, int startIndex)
    {
        var end = Array.IndexOf(data, (byte)0, startIndex);
        if (end < 0) return string.Empty;
        return System.Text.Encoding.ASCII.GetString(data, startIndex, end - startIndex);
    }

    private static Dictionary<string, string> ParseOptions(byte[] data, int startIndex)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Skip filename and mode (two null-terminated strings)
        var pos = startIndex;
        var nullCount = 0;
        while (pos < data.Length && nullCount < 2)
        {
            if (data[pos] == 0) nullCount++;
            pos++;
        }
        // Parse remaining key-value pairs
        while (pos < data.Length)
        {
            var key = ParseNullTerminatedString(data, pos);
            if (string.IsNullOrEmpty(key)) break;
            pos += key.Length + 1;
            var value = ParseNullTerminatedString(data, pos);
            pos += value.Length + 1;
            options[key] = value;
        }
        return options;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _listener?.Dispose();
        _cts?.Dispose();
    }
}
