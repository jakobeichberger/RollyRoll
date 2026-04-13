using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;

namespace RollyRoll.ClientAgent;

/// <summary>
/// Automatically configures Wake-on-LAN on the local machine at the OS and NIC level.
///
/// What it configures:
/// 1. NIC: Enables "Wake on Magic Packet" via PowerShell (Set-NetAdapterAdvancedProperty)
/// 2. NIC: Enables "Allow this device to wake the computer" via PowerShell (Enable-NetAdapterPowerManagement)
/// 3. OS:  Disables Windows Fast Startup (Hybrid Shutdown) which breaks reliable WoL
/// 4. OS:  Prevents Windows from turning off the NIC to save power
/// 5. BIOS (optional): Attempts WoL enable via vendor CLI tools (Dell CCTK, Lenovo TBCT, HP BCU)
///
/// Runs once on startup and periodically re-verifies the configuration.
/// </summary>
[SupportedOSPlatform("windows")]
public class WolConfigurator : BackgroundService
{
    private readonly ILogger<WolConfigurator> _logger;
    private readonly IConfiguration _configuration;

    /// <summary>Re-check WoL configuration every 24 hours.</summary>
    private static readonly TimeSpan RecheckInterval = TimeSpan.FromHours(24);

    public WolConfigurator(ILogger<WolConfigurator> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for system to fully initialize
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConfigureWolAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to configure Wake-on-LAN");
            }

            try
            {
                await Task.Delay(RecheckInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Run all WoL configuration steps.
    /// </summary>
    private async Task ConfigureWolAsync()
    {
        _logger.LogInformation("Checking and configuring Wake-on-LAN...");

        var primaryNic = GetPrimaryNetworkAdapter();
        if (primaryNic is null)
        {
            _logger.LogWarning("No active network adapter found. Cannot configure WoL");
            return;
        }

        _logger.LogInformation("Primary NIC: {Name} ({Description})", primaryNic.Name, primaryNic.Description);

        // Step 1: Enable WoL on the NIC via advanced properties
        await EnableNicWakeOnMagicPacketAsync(primaryNic.Name);

        // Step 2: Enable "Allow this device to wake the computer" on the NIC
        await EnableNicWakeDeviceAsync(primaryNic.Name);

        // Step 3: Prevent Windows from turning off the NIC to save power
        await PreventNicPowerSaveShutdownAsync(primaryNic.Name);

        // Step 4: Disable Windows Fast Startup (Hybrid Shutdown)
        await DisableFastStartupAsync();

        // Step 5: Optionally configure BIOS/UEFI via vendor tools
        var enableBiosConfig = _configuration.GetValue("WoL:ConfigureBios", false);
        if (enableBiosConfig)
        {
            await ConfigureBiosWolAsync();
        }

        _logger.LogInformation("Wake-on-LAN configuration complete");
    }

    // ────────────────────────────────────────────────────────
    // Step 1: Enable "Wake on Magic Packet" NIC property
    // ────────────────────────────────────────────────────────

    /// <summary>
    /// Sets the NIC advanced property "Wake on Magic Packet" to Enabled.
    /// Different NIC vendors use different property names — we try the most common ones.
    /// </summary>
    private async Task EnableNicWakeOnMagicPacketAsync(string adapterName)
    {
        // Common property names used by different NIC manufacturers:
        // Intel:    "Wake on Magic Packet"
        // Realtek:  "Wake on Magic Packet", "WakeOnMagicPacket"
        // Broadcom: "Wake-On-Magic Packet", "Wake Up Capabilities" = "Magic Packet"
        // Marvel:   "Wake From Shutdown"

        var propertyNames = new[]
        {
            "Wake on Magic Packet",
            "WakeOnMagicPacket",
            "Wake-On-Magic Packet",
            "Wake on magic packet",
            "Wake From Shutdown"
        };

        foreach (var propName in propertyNames)
        {
            var result = await RunPowerShellAsync(
                $"Set-NetAdapterAdvancedProperty -Name '{EscapePsString(adapterName)}' " +
                $"-DisplayName '{EscapePsString(propName)}' -DisplayValue 'Enabled' -ErrorAction SilentlyContinue");

            if (result.ExitCode == 0)
            {
                _logger.LogInformation("NIC WoL enabled: '{Property}' = Enabled on adapter '{Adapter}'",
                    propName, adapterName);
                return;
            }
        }

        // Try using the registryKeyword approach (works with most NICs)
        var regResult = await RunPowerShellAsync(
            $"Set-NetAdapterAdvancedProperty -Name '{EscapePsString(adapterName)}' " +
            $"-RegistryKeyword '*WakeOnMagicPacket' -RegistryValue 1 -ErrorAction SilentlyContinue");

        if (regResult.ExitCode == 0)
        {
            _logger.LogInformation("NIC WoL enabled via registry keyword on adapter '{Adapter}'", adapterName);
        }
        else
        {
            _logger.LogWarning("Could not enable 'Wake on Magic Packet' on adapter '{Adapter}'. " +
                "WoL may not work. Check NIC driver support.", adapterName);
        }
    }

    // ────────────────────────────────────────────────────────
    // Step 2: Enable "Allow device to wake the computer"
    // ────────────────────────────────────────────────────────

    /// <summary>
    /// Enables the "Allow this device to wake the computer" checkbox in the NIC's
    /// Power Management properties. Uses Enable-NetAdapterPowerManagement.
    /// </summary>
    private async Task EnableNicWakeDeviceAsync(string adapterName)
    {
        // Enable-NetAdapterPowerManagement sets WakeOnMagicPacket and WakeOnPattern
        var script = $@"
            $adapter = Get-NetAdapterPowerManagement -Name '{EscapePsString(adapterName)}' -ErrorAction SilentlyContinue
            if ($adapter) {{
                $adapter.WakeOnMagicPacket = 'Enabled'
                $adapter.WakeOnPattern = 'Enabled'
                $adapter | Set-NetAdapterPowerManagement -ErrorAction SilentlyContinue
                Write-Output 'WakeOnMagicPacket and WakeOnPattern enabled'
            }} else {{
                Write-Output 'NetAdapterPowerManagement not available for this adapter'
            }}
        ";

        var result = await RunPowerShellAsync(script);
        if (result.ExitCode == 0)
        {
            _logger.LogInformation("NIC power management WoL enabled on '{Adapter}': {Output}",
                adapterName, result.Output.Trim());
        }
        else
        {
            _logger.LogWarning("Failed to configure NIC power management WoL on '{Adapter}': {Error}",
                adapterName, result.Error);
        }
    }

    // ────────────────────────────────────────────────────────
    // Step 3: Prevent Windows from disabling NIC to save power
    // ────────────────────────────────────────────────────────

    /// <summary>
    /// Unchecks "Allow the computer to turn off this device to save power" for the NIC.
    /// This prevents Windows from cutting power to the NIC during sleep/hibernation,
    /// which would prevent WoL from working.
    /// </summary>
    private async Task PreventNicPowerSaveShutdownAsync(string adapterName)
    {
        // This is done via the PnP device property through PowerShell
        var script = $@"
            $nic = Get-NetAdapter -Name '{EscapePsString(adapterName)}' -ErrorAction SilentlyContinue
            if ($nic) {{
                $pnpDevice = Get-PnpDevice -InstanceId $nic.PnPDeviceID -ErrorAction SilentlyContinue
                if ($pnpDevice) {{
                    # Disable power saving for this device
                    $powerMgmt = Get-CimInstance -ClassName MSPower_DeviceEnable -Namespace root\wmi -ErrorAction SilentlyContinue |
                        Where-Object {{ $_.InstanceName -like ""*$($nic.PnPDeviceID)*"" }}
                    if ($powerMgmt) {{
                        $powerMgmt | Set-CimInstance -Property @{{ Enable = $false }} -ErrorAction SilentlyContinue
                        Write-Output 'Power saving disabled for NIC'
                    }} else {{
                        Write-Output 'Could not find power management instance for NIC'
                    }}
                }}
            }}
        ";

        var result = await RunPowerShellAsync(script);
        _logger.LogInformation("NIC power save config on '{Adapter}': {Output}",
            adapterName, result.Output.Trim());
    }

    // ────────────────────────────────────────────────────────
    // Step 4: Disable Windows Fast Startup (Hybrid Shutdown)
    // ────────────────────────────────────────────────────────

    /// <summary>
    /// Disables Windows Fast Startup (Hybrid Shutdown) which is known to prevent
    /// reliable Wake-on-LAN. When Fast Startup is enabled, the NIC driver may not
    /// reinitialize properly for WoL during the hybrid shutdown state.
    ///
    /// Sets registry: HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power\HiberbootEnabled = 0
    /// </summary>
    private async Task DisableFastStartupAsync()
    {
        var script = @"
            $path = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power'
            $current = Get-ItemProperty -Path $path -Name 'HiberbootEnabled' -ErrorAction SilentlyContinue
            if ($current -and $current.HiberbootEnabled -eq 0) {
                Write-Output 'Fast Startup already disabled'
            } else {
                Set-ItemProperty -Path $path -Name 'HiberbootEnabled' -Value 0 -Type DWord -Force
                Write-Output 'Fast Startup disabled (HiberbootEnabled=0). Reboot required for effect.'
            }
        ";

        var result = await RunPowerShellAsync(script);
        if (result.ExitCode == 0)
        {
            _logger.LogInformation("Fast Startup config: {Output}", result.Output.Trim());
        }
        else
        {
            _logger.LogWarning("Failed to disable Fast Startup: {Error}", result.Error);
        }
    }

    // ────────────────────────────────────────────────────────
    // Step 5: BIOS/UEFI WoL via vendor CLI tools (optional)
    // ────────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to enable WoL in the BIOS/UEFI firmware using vendor-specific CLI tools.
    /// Supports Dell (CCTK/Dell Command Configure), Lenovo (TBCT), and HP (BCU).
    ///
    /// These tools must be pre-installed on the client. The agent detects the hardware
    /// manufacturer and runs the appropriate tool.
    /// </summary>
    private async Task ConfigureBiosWolAsync()
    {
        var manufacturer = GetManufacturer().ToLowerInvariant();
        _logger.LogInformation("Attempting BIOS WoL configuration for manufacturer: {Manufacturer}", manufacturer);

        if (manufacturer.Contains("dell"))
        {
            await ConfigureDellBiosWolAsync();
        }
        else if (manufacturer.Contains("lenovo"))
        {
            await ConfigureLenovoBiosWolAsync();
        }
        else if (manufacturer.Contains("hp") || manufacturer.Contains("hewlett"))
        {
            await ConfigureHpBiosWolAsync();
        }
        else
        {
            _logger.LogInformation("No BIOS WoL tool available for manufacturer '{Manufacturer}'. " +
                "WoL must be enabled manually in BIOS/UEFI setup.", manufacturer);
        }
    }

    /// <summary>
    /// Dell: Uses Dell Command | Configure (CCTK) to enable WoL.
    /// Tool path: C:\Program Files (x86)\Dell\Command Configure\X86_64\cctk.exe
    /// </summary>
    private async Task ConfigureDellBiosWolAsync()
    {
        var cctkPaths = new[]
        {
            @"C:\Program Files (x86)\Dell\Command Configure\X86_64\cctk.exe",
            @"C:\Program Files\Dell\Command Configure\X86_64\cctk.exe",
        };

        var cctkPath = cctkPaths.FirstOrDefault(File.Exists);
        if (cctkPath is null)
        {
            _logger.LogInformation("Dell CCTK not found. Install 'Dell Command | Configure' for BIOS WoL config.");
            return;
        }

        // Enable Wake-on-LAN in Dell BIOS
        var commands = new[]
        {
            "--wakeonlan=LanOnly",           // Enable WoL via LAN
            "--deepsleepctrl=Disabled",       // Disable deep sleep (prevents WoL)
            "--BlockSleep=Disabled",          // Allow sleep states
        };

        foreach (var cmd in commands)
        {
            var result = await RunProcessAsync(cctkPath, cmd);
            _logger.LogInformation("Dell CCTK '{Cmd}': exit={ExitCode}, output={Output}",
                cmd, result.ExitCode, result.Output.Trim());
        }
    }

    /// <summary>
    /// Lenovo: Uses Lenovo BIOS Configuration Tool (TBCT) or WMI.
    /// Lenovo supports BIOS settings via WMI: Lenovo_SetBiosSetting / Lenovo_SaveBiosSettings
    /// </summary>
    private async Task ConfigureLenovoBiosWolAsync()
    {
        // Lenovo supports BIOS config via WMI — no external tool needed
        var script = @"
            try {
                # Set WoL to 'AC Only' (enable Wake on LAN when on AC power)
                $setBios = Get-WmiObject -Namespace root\wmi -Class Lenovo_SetBiosSetting -ErrorAction Stop
                $setBios.SetBiosSetting('WakeOnLAN,AC Only') | Out-Null

                # Save the BIOS settings
                $saveBios = Get-WmiObject -Namespace root\wmi -Class Lenovo_SaveBiosSettings -ErrorAction Stop
                $saveBios.SaveBiosSettings() | Out-Null

                Write-Output 'Lenovo BIOS WoL enabled via WMI (AC Only)'
            } catch {
                Write-Output ""Lenovo WMI BIOS config failed: $_""
            }
        ";

        var result = await RunPowerShellAsync(script);
        _logger.LogInformation("Lenovo BIOS WoL: {Output}", result.Output.Trim());
    }

    /// <summary>
    /// HP: Uses HP BIOS Configuration Utility (BCU).
    /// Tool path: C:\Program Files (x86)\HP\BIOS Configuration Utility\BiosConfigUtility64.exe
    /// </summary>
    private async Task ConfigureHpBiosWolAsync()
    {
        var bcuPaths = new[]
        {
            @"C:\Program Files (x86)\HP\BIOS Configuration Utility\BiosConfigUtility64.exe",
            @"C:\Program Files\HP\BIOS Configuration Utility\BiosConfigUtility64.exe",
        };

        var bcuPath = bcuPaths.FirstOrDefault(File.Exists);
        if (bcuPath is null)
        {
            _logger.LogInformation("HP BCU not found. Install 'HP BIOS Configuration Utility' for BIOS WoL config.");
            return;
        }

        // Create a temporary BIOS config file to enable WoL
        var configContent = """
            BIOSSetting
            Wake On LAN
            	Boot to Hard Drive
            	*Boot to Network
            """;

        var configPath = Path.Combine(Path.GetTempPath(), "RollyRoll", "hp_wol_config.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        await File.WriteAllTextAsync(configPath, configContent);

        try
        {
            var result = await RunProcessAsync(bcuPath, $"/Set:\"{configPath}\"");
            _logger.LogInformation("HP BCU WoL config: exit={ExitCode}, output={Output}",
                result.ExitCode, result.Output.Trim());
        }
        finally
        {
            try { File.Delete(configPath); } catch { /* best effort */ }
        }
    }

    // ────────────────────────────────────────────────────────
    // Helper methods
    // ────────────────────────────────────────────────────────

    private static NetworkInterface? GetPrimaryNetworkAdapter()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet
                or NetworkInterfaceType.GigabitEthernet)
            .OrderByDescending(n => n.Speed)
            .FirstOrDefault()
            // Fall back to any non-loopback adapter if no Ethernet found
            ?? NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .OrderByDescending(n => n.Speed)
                .FirstOrDefault();
    }

    private static string GetManufacturer()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "wmic",
                Arguments = "computersystem get manufacturer /format:list",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null) return "Unknown";

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("Manufacturer=", StringComparison.OrdinalIgnoreCase))
                    return line.Split('=', 2)[1].Trim();
            }
        }
        catch { /* fall through */ }

        return "Unknown";
    }

    private static async Task<ProcessResult> RunPowerShellAsync(string script)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{EscapePsCommandArg(script)}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start PowerShell");

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new ProcessResult
        {
            ExitCode = process.ExitCode,
            Output = await outputTask,
            Error = await errorTask
        };
    }

    private static async Task<ProcessResult> RunProcessAsync(string fileName, string arguments)
    {
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
            ?? throw new InvalidOperationException($"Failed to start {fileName}");

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new ProcessResult
        {
            ExitCode = process.ExitCode,
            Output = await outputTask,
            Error = await errorTask
        };
    }

    /// <summary>Escape single quotes in PowerShell string literals.</summary>
    private static string EscapePsString(string value) => value.Replace("'", "''");

    /// <summary>Escape double quotes for PowerShell -Command argument.</summary>
    private static string EscapePsCommandArg(string script) => script.Replace("\"", "\\\"");

    private sealed class ProcessResult
    {
        public int ExitCode { get; init; }
        public string Output { get; init; } = string.Empty;
        public string Error { get; init; } = string.Empty;
    }
}
