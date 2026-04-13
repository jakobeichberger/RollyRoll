using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RollyRoll.Core.Interfaces;
using RollyRoll.Core.Models;

namespace RollyRoll.Infrastructure.PXE;

/// <summary>
/// Generates iPXE boot scripts dynamically per client.
/// When a client boots iPXE, it requests a script from the RollyRoll HTTP endpoint.
/// The script determines whether to boot into WinPE (if a task is pending) or local disk.
///
/// Uses IServiceScopeFactory to resolve scoped services since this is registered as a singleton.
/// </summary>
public class BootMenuGenerator
{
    private readonly ILogger<BootMenuGenerator> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly string _serverBaseUrl;

    public BootMenuGenerator(
        ILogger<BootMenuGenerator> logger,
        IServiceScopeFactory scopeFactory,
        string serverBaseUrl)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _serverBaseUrl = serverBaseUrl.TrimEnd('/');
    }

    /// <summary>
    /// Generate an iPXE script for a client identified by MAC address.
    /// Called by the iPXE HTTP boot endpoint.
    /// </summary>
    public async Task<string> GenerateScriptAsync(string macAddress, CancellationToken ct = default)
    {
        var normalizedMac = NormalizeMac(macAddress);
        _logger.LogInformation("Generating boot script for MAC: {Mac}", normalizedMac);

        using var scope = _scopeFactory.CreateScope();
        var deploymentService = scope.ServiceProvider.GetRequiredService<IDeploymentService>();
        var clientDiscovery = scope.ServiceProvider.GetRequiredService<IClientDiscoveryService>();

        // Check if this client has a pending deployment task
        var pendingTask = await deploymentService.GetPendingTaskForClientAsync(normalizedMac, ct);

        if (pendingTask != null)
        {
            _logger.LogInformation("Client {Mac} has pending task {TaskId} ({TaskType}), booting into WinPE",
                normalizedMac, pendingTask.Id, pendingTask.TaskType);
            return GenerateWinPeBootScript(normalizedMac, pendingTask);
        }

        // Check if client is known
        var client = await clientDiscovery.GetClientByMacAsync(normalizedMac, ct);
        if (client == null)
        {
            _logger.LogInformation("Unknown client {Mac}, registering and booting from local disk", normalizedMac);
        }
        else
        {
            _logger.LogInformation("Known client {Mac} ({Hostname}), no pending tasks, booting from local disk",
                normalizedMac, client.Hostname);
        }

        return GenerateLocalBootScript();
    }

    /// <summary>
    /// Generate iPXE script that boots into WinPE for deployment/capture.
    /// WinPE image is served via HTTP from the RollyRoll server.
    /// </summary>
    private string GenerateWinPeBootScript(string macAddress, ScheduledTask task)
    {
        return $"""
            #!ipxe
            # RollyRoll - Boot into WinPE for task {task.Id} ({task.TaskType})
            # Client: {macAddress}
            # Generated: {DateTime.UtcNow:O}

            echo RollyRoll: Deployment task detected, booting into WinPE...
            echo Task: {task.Name} ({task.TaskType})

            # Set kernel parameters
            set server-url {_serverBaseUrl}
            set task-id {task.Id}
            set client-mac {macAddress}

            # Download WinPE boot files via HTTP (faster than TFTP for large files)
            kernel {_serverBaseUrl}/boot/wimboot
            initrd {_serverBaseUrl}/boot/bcd         BCD
            initrd {_serverBaseUrl}/boot/boot.sdi    boot.sdi
            initrd {_serverBaseUrl}/boot/boot.wim    boot.wim

            # Boot WinPE
            boot || goto failed

            :failed
            echo RollyRoll: WinPE boot failed! Falling back to local disk...
            sleep 5
            exit
            """;
    }

    /// <summary>
    /// Generate iPXE script that boots from the local disk (no pending tasks).
    /// </summary>
    private static string GenerateLocalBootScript()
    {
        return """
            #!ipxe
            # RollyRoll - No pending tasks, boot from local disk

            # Chain to local disk
            exit
            """;
    }

    /// <summary>
    /// Generate iPXE script for the boot menu (shown when client is in menu mode).
    /// </summary>
    public string GenerateMenuScript(string macAddress)
    {
        return $"""
            #!ipxe
            # RollyRoll Boot Menu
            # Client: {macAddress}

            menu RollyRoll Deployment Server
            item --gap --           --- RollyRoll Options ---
            item local              Boot from local disk
            item winpe              Boot into WinPE (maintenance)
            item --gap --           --- Advanced ---
            item shell              iPXE shell
            item reboot             Reboot
            choose --default local --timeout 10000 target && goto ${{target}}

            :local
            exit

            :winpe
            echo Booting into WinPE maintenance mode...
            kernel {_serverBaseUrl}/boot/wimboot
            initrd {_serverBaseUrl}/boot/bcd         BCD
            initrd {_serverBaseUrl}/boot/boot.sdi    boot.sdi
            initrd {_serverBaseUrl}/boot/boot.wim    boot.wim
            boot || goto failed

            :shell
            echo Type 'exit' to return to menu
            shell
            goto menu

            :reboot
            reboot

            :failed
            echo Boot failed!
            sleep 5
            goto menu
            """;
    }

    private static string NormalizeMac(string mac)
    {
        var cleaned = mac.Replace("-", ":").Replace(".", ":").ToUpperInvariant();
        if (!cleaned.Contains(':'))
        {
            if (cleaned.Length == 12)
            {
                cleaned = string.Join(":", Enumerable.Range(0, 6).Select(i => cleaned.Substring(i * 2, 2)));
            }
        }
        return cleaned;
    }
}
