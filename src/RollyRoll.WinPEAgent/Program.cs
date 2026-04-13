using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using RollyRoll.Core.Models;
using RollyRoll.WinPEAgent;

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Information);
});

var logger = loggerFactory.CreateLogger("WinPEAgent");

logger.LogInformation("RollyRoll WinPE Agent starting...");

try
{
    // Step 1: Detect boot type (UEFI or Legacy BIOS)
    var bootType = DetectBootType();
    logger.LogInformation("Detected boot type: {BootType}", bootType);

    // Step 2: Discover the RollyRoll server
    var serverUrl = await DiscoverServerAsync(logger);
    logger.LogInformation("Server discovered at: {ServerUrl}", serverUrl);

    var agentClient = new AgentClient(
        loggerFactory.CreateLogger<AgentClient>(),
        serverUrl);

    // Step 3: Get MAC address for identification
    var macAddress = GetPrimaryMacAddress();
    logger.LogInformation("Primary MAC address: {Mac}", macAddress);

    // Step 4: Get the pending deployment task from the server
    var task = await agentClient.GetTaskAsync(macAddress);
    if (task == null)
    {
        logger.LogWarning("No pending task found for this client. Exiting");
        return 0;
    }

    logger.LogInformation("Received task {TaskId}: {TaskName} (Type={TaskType}, Mode={Mode})",
        task.Id, task.Name, task.TaskType, task.DeployMode);

    await agentClient.ReportProgressAsync(task.Id, 5, "WinPE agent started, beginning deployment...");

    // Step 5: Partition the disk
    var diskPartitioner = new DiskPartitioner(loggerFactory.CreateLogger<DiskPartitioner>());
    await agentClient.ReportProgressAsync(task.Id, 10, "Partitioning disk...");

    var partitionResult = await diskPartitioner.PartitionDiskAsync(bootType);
    logger.LogInformation("Disk partitioned: WindowsDrive={WinDrive}", partitionResult.WindowsDriveLetter);

    await agentClient.ReportProgressAsync(task.Id, 20, "Disk partitioned successfully");

    // Step 6: Download and apply image
    var imageApplier = new ImageApplier(loggerFactory.CreateLogger<ImageApplier>());
    await agentClient.ReportProgressAsync(task.Id, 25, "Downloading image from server...");

    var localWimPath = Path.Combine("X:\\", "RollyRoll", "deploy.wim");
    Directory.CreateDirectory(Path.GetDirectoryName(localWimPath)!);

    await agentClient.DownloadImageAsync(task.ImageId ?? 0, localWimPath, new Progress<int>(async percent =>
    {
        // Map download progress to 25-60% of overall progress
        var overallProgress = 25 + (int)(percent * 0.35);
        await agentClient.ReportProgressAsync(task.Id, overallProgress, $"Downloading image: {percent}%");
    }));

    await agentClient.ReportProgressAsync(task.Id, 60, "Applying image to disk...");

    await imageApplier.ApplyImageAsync(localWimPath, partitionResult.WindowsDriveLetter);
    logger.LogInformation("Image applied successfully");

    await agentClient.ReportProgressAsync(task.Id, 80, "Image applied successfully");

    // Step 7: Inject user profiles if RestoreWithProfiles mode
    if (task.DeployMode == DeployMode.RestoreWithProfiles)
    {
        await agentClient.ReportProgressAsync(task.Id, 82, "Restoring user profiles...");

        var profileInjector = new ProfileInjector(loggerFactory.CreateLogger<ProfileInjector>());
        await profileInjector.InjectProfilesAsync(macAddress, serverUrl, partitionResult.WindowsDriveLetter);

        logger.LogInformation("User profiles restored successfully");
        await agentClient.ReportProgressAsync(task.Id, 90, "User profiles restored");
    }
    else
    {
        await agentClient.ReportProgressAsync(task.Id, 90, "Clean deploy mode, skipping profile restore");
    }

    // Step 8: Run post-deployment steps from the template
    if (task.Template != null)
    {
        await agentClient.ReportProgressAsync(task.Id, 92, "Running post-deployment steps...");

        var postDeployRunner = new PostDeployRunner(loggerFactory.CreateLogger<PostDeployRunner>());
        await postDeployRunner.ExecuteStepsAsync(task.Template, partitionResult.WindowsDriveLetter);

        logger.LogInformation("Post-deployment steps completed");
    }

    await agentClient.ReportProgressAsync(task.Id, 98, "Finalizing deployment...");

    // Step 9: Report completion
    await agentClient.ReportCompletionAsync(task.Id, success: true);
    logger.LogInformation("Deployment completed successfully. Rebooting...");

    // Reboot into the deployed OS
    if (task.Template?.RebootAfterDeploy != false)
    {
        RunProcess("wpeutil", "reboot");
    }

    return 0;
}
catch (Exception ex)
{
    logger.LogCritical(ex, "Deployment failed with unhandled exception");

    // Try to report failure to server
    try
    {
        var serverUrl = Environment.GetEnvironmentVariable("ROLLYROLL_SERVER");
        if (!string.IsNullOrEmpty(serverUrl))
        {
            var errorClient = new AgentClient(
                loggerFactory.CreateLogger<AgentClient>(),
                serverUrl);
            var mac = GetPrimaryMacAddress();
            var failedTask = await errorClient.GetTaskAsync(mac);
            if (failedTask != null)
            {
                await errorClient.ReportCompletionAsync(failedTask.Id, success: false, errorMessage: ex.Message);
            }
        }
    }
    catch
    {
        // Best effort — if we can't report, just log locally
    }

    return 1;
}

// --- Helper methods ---

static BootType DetectBootType()
{
    // Check for the presence of EFI system partition markers
    // In WinPE, the firmware type can be detected via GetFirmwareEnvironmentVariable
    // or by checking if the EFI system directory exists
    var efiDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "EFI");
    if (Directory.Exists(efiDir))
        return BootType.UEFI;

    // Alternative: check via registry or environment
    var firmwareType = Environment.GetEnvironmentVariable("firmware_type");
    if (string.Equals(firmwareType, "UEFI", StringComparison.OrdinalIgnoreCase))
        return BootType.UEFI;

    // Check SecureBoot variable availability as a UEFI indicator
    try
    {
        var result = RunProcess("bcdedit", "/enum firmware");
        if (result.Contains("EFI", StringComparison.OrdinalIgnoreCase))
            return BootType.UEFI;
    }
    catch
    {
        // If bcdedit fails, fall through to BIOS default
    }

    return BootType.LegacyBIOS;
}

static async Task<string> DiscoverServerAsync(ILogger logger)
{
    // Check environment variable first (set by iPXE boot script)
    var serverUrl = Environment.GetEnvironmentVariable("ROLLYROLL_SERVER");
    if (!string.IsNullOrEmpty(serverUrl))
    {
        logger.LogInformation("Server URL from environment: {Url}", serverUrl);
        return serverUrl;
    }

    // Try reading from winpeshl.ini or unattend.xml configuration
    var configPath = @"X:\RollyRoll\server.txt";
    if (File.Exists(configPath))
    {
        serverUrl = (await File.ReadAllTextAsync(configPath)).Trim();
        if (!string.IsNullOrEmpty(serverUrl))
        {
            logger.LogInformation("Server URL from config file: {Url}", serverUrl);
            return serverUrl;
        }
    }

    // Try UDP broadcast discovery on port 5150
    using var udpClient = new System.Net.Sockets.UdpClient();
    udpClient.EnableBroadcast = true;
    var discoveryMessage = System.Text.Encoding.UTF8.GetBytes("ROLLYROLL_DISCOVER");
    await udpClient.SendAsync(discoveryMessage, discoveryMessage.Length,
        new System.Net.IPEndPoint(System.Net.IPAddress.Broadcast, 5150));

    udpClient.Client.ReceiveTimeout = 5000;
    try
    {
        var remoteEp = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
        var responseData = udpClient.Receive(ref remoteEp);
        serverUrl = System.Text.Encoding.UTF8.GetString(responseData);
        logger.LogInformation("Server discovered via broadcast: {Url}", serverUrl);
        return serverUrl;
    }
    catch (System.Net.Sockets.SocketException)
    {
        logger.LogWarning("Broadcast discovery timed out");
    }

    throw new InvalidOperationException("Unable to discover RollyRoll server. Set ROLLYROLL_SERVER environment variable or ensure server is reachable via broadcast");
}

static string GetPrimaryMacAddress()
{
    var interfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
        .Where(nic => nic.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
        .Where(nic => nic.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
        .OrderByDescending(nic => nic.Speed)
        .FirstOrDefault();

    if (interfaces == null)
        throw new InvalidOperationException("No active network interfaces found");

    var macBytes = interfaces.GetPhysicalAddress().GetAddressBytes();
    return string.Join(":", macBytes.Select(b => b.ToString("X2")));
}

static string RunProcess(string fileName, string arguments)
{
    var psi = new System.Diagnostics.ProcessStartInfo
    {
        FileName = fileName,
        Arguments = arguments,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };

    using var process = System.Diagnostics.Process.Start(psi)
        ?? throw new InvalidOperationException($"Failed to start process: {fileName}");

    var output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    return output;
}
