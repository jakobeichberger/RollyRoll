using RollyRoll.Infrastructure.Services;

namespace RollyRoll.WindowsService.Workers;

/// <summary>
/// Background service that hosts the TFTP server and DHCP proxy for PXE boot.
/// Both services are started on startup and stopped gracefully on shutdown.
/// </summary>
public class PxeBootWorker : BackgroundService
{
    private readonly ILogger<PxeBootWorker> _logger;
    private readonly TftpServer _tftpServer;
    private readonly DhcpProxyService _dhcpProxy;

    public PxeBootWorker(
        ILogger<PxeBootWorker> logger,
        TftpServer tftpServer,
        DhcpProxyService dhcpProxy)
    {
        _logger = logger;
        _tftpServer = tftpServer;
        _dhcpProxy = dhcpProxy;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PxeBootWorker starting TFTP server and DHCP proxy...");

        try
        {
            await _tftpServer.StartAsync(stoppingToken);
            _logger.LogInformation("TFTP server started successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start TFTP server. PXE boot will not be available");
        }

        try
        {
            await _dhcpProxy.StartAsync(stoppingToken);
            _logger.LogInformation("DHCP proxy started successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start DHCP proxy. PXE boot will not be available");
        }

        _logger.LogInformation("PxeBootWorker is running. TFTP={TftpRunning}, DHCP={DhcpRunning}",
            _tftpServer.IsRunning, _dhcpProxy.IsRunning);

        // Keep the worker alive until cancellation is requested
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("PxeBootWorker stopping...");

        try
        {
            await _tftpServer.StopAsync();
            _logger.LogInformation("TFTP server stopped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping TFTP server");
        }

        try
        {
            await _dhcpProxy.StopAsync();
            _logger.LogInformation("DHCP proxy stopped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping DHCP proxy");
        }

        await base.StopAsync(cancellationToken);
        _logger.LogInformation("PxeBootWorker stopped");
    }
}
