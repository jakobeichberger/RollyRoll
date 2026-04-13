using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RollyRoll.Core.Models;

namespace RollyRoll.WinPEAgent;

/// <summary>
/// Executes post-deployment steps defined in the deployment template.
/// Supports running scripts, installing MSI/EXE packages, copying files,
/// editing the registry, and performing reboots.
/// Steps are executed in order; each step can optionally continue on failure.
/// </summary>
public class PostDeployRunner
{
    private readonly ILogger<PostDeployRunner> _logger;

    public PostDeployRunner(ILogger<PostDeployRunner> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Execute all post-deployment steps from the template in order.
    /// </summary>
    /// <param name="template">The deployment template containing PostDeployStepsJson.</param>
    /// <param name="targetDrive">The drive letter where the OS was deployed (e.g. "W:").</param>
    public async Task ExecuteStepsAsync(DeploymentTemplate template, string targetDrive)
    {
        var steps = DeserializeSteps(template.PostDeployStepsJson);
        if (steps.Count == 0)
        {
            _logger.LogInformation("No post-deployment steps defined in template '{Template}'", template.Name);
            return;
        }

        _logger.LogInformation("Executing {Count} post-deployment steps from template '{Template}'",
            steps.Count, template.Name);

        foreach (var step in steps.OrderBy(s => s.Order))
        {
            _logger.LogInformation("Step {Order}/{Total}: {StepName} ({StepType})",
                step.Order, steps.Count, step.Name, step.StepType);

            try
            {
                await ExecuteStepAsync(step, targetDrive);
                _logger.LogInformation("Step {Order} completed successfully: {StepName}", step.Order, step.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Step {Order} failed: {StepName}", step.Order, step.Name);

                if (!step.ContinueOnFailure)
                {
                    throw new InvalidOperationException(
                        $"Post-deployment step '{step.Name}' failed and ContinueOnFailure is false", ex);
                }

                _logger.LogWarning("Continuing despite failure (ContinueOnFailure=true)");
            }
        }

        _logger.LogInformation("All post-deployment steps completed");
    }

    private async Task ExecuteStepAsync(DeploymentStep step, string targetDrive)
    {
        switch (step.StepType)
        {
            case DeploymentStepType.RunScript:
                await RunScriptAsync(step, targetDrive);
                break;

            case DeploymentStepType.InstallMsi:
                await InstallMsiAsync(step, targetDrive);
                break;

            case DeploymentStepType.InstallExe:
                await InstallExeAsync(step, targetDrive);
                break;

            case DeploymentStepType.CopyFiles:
                await CopyFilesAsync(step, targetDrive);
                break;

            case DeploymentStepType.RegistryEdit:
                await ApplyRegistryEditAsync(step, targetDrive);
                break;

            case DeploymentStepType.Reboot:
                _logger.LogInformation("Reboot step — will be handled after all steps complete");
                break;

            case DeploymentStepType.WaitForUser:
                _logger.LogInformation("WaitForUser step skipped in automated WinPE deployment");
                break;

            default:
                _logger.LogWarning("Unknown step type: {StepType}", step.StepType);
                break;
        }
    }

    private async Task RunScriptAsync(DeploymentStep step, string targetDrive)
    {
        var scriptPath = ResolveTargetPath(step.Command, targetDrive);
        var fileName = scriptPath.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)
            ? "powershell.exe"
            : "cmd.exe";

        var arguments = scriptPath.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)
            ? $"-ExecutionPolicy Bypass -NoProfile -File \"{scriptPath}\" {step.Arguments}"
            : $"/c \"{scriptPath}\" {step.Arguments}";

        await RunProcessAsync(fileName, arguments, step.TimeoutSeconds);
    }

    private async Task InstallMsiAsync(DeploymentStep step, string targetDrive)
    {
        var msiPath = ResolveTargetPath(step.Command, targetDrive);
        var arguments = $"/i \"{msiPath}\" /qn /norestart {step.Arguments}";

        await RunProcessAsync("msiexec.exe", arguments, step.TimeoutSeconds);
    }

    private async Task InstallExeAsync(DeploymentStep step, string targetDrive)
    {
        var exePath = ResolveTargetPath(step.Command, targetDrive);
        await RunProcessAsync(exePath, step.Arguments, step.TimeoutSeconds);
    }

    private Task CopyFilesAsync(DeploymentStep step, string targetDrive)
    {
        // Command format: "source|destination"
        var parts = step.Command.Split('|', 2);
        if (parts.Length != 2)
        {
            throw new ArgumentException($"CopyFiles step expects 'source|destination' format, got: {step.Command}");
        }

        var source = ResolveTargetPath(parts[0].Trim(), targetDrive);
        var destination = ResolveTargetPath(parts[1].Trim(), targetDrive);

        if (Directory.Exists(source))
        {
            CopyDirectory(source, destination);
        }
        else if (File.Exists(source))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
        }
        else
        {
            throw new FileNotFoundException($"Source not found: {source}");
        }

        _logger.LogInformation("Copied {Source} -> {Destination}", source, destination);
        return Task.CompletedTask;
    }

    private async Task ApplyRegistryEditAsync(DeploymentStep step, string targetDrive)
    {
        // Load the offline registry hive and apply edits using reg.exe
        // Command format: "HKLM\SOFTWARE\Key /v ValueName /t REG_SZ /d Data"
        var regHivePath = Path.Combine(targetDrive + "\\", "Windows", "System32", "config", "SOFTWARE");

        // Load the offline hive
        await RunProcessAsync("reg.exe", $"load HKLM\\OFFLINE \"{regHivePath}\"", 30);

        try
        {
            // Apply the registry edit (replace HKLM\SOFTWARE with HKLM\OFFLINE)
            var regCommand = step.Command.Replace("HKLM\\SOFTWARE", "HKLM\\OFFLINE", StringComparison.OrdinalIgnoreCase);
            await RunProcessAsync("reg.exe", $"add {regCommand} /f {step.Arguments}", step.TimeoutSeconds);
        }
        finally
        {
            // Unload the hive
            await RunProcessAsync("reg.exe", "unload HKLM\\OFFLINE", 30);
        }
    }

    private async Task RunProcessAsync(string fileName, string arguments, int timeoutSeconds)
    {
        _logger.LogDebug("Running: {FileName} {Arguments}", fileName, arguments);

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
            ?? throw new InvalidOperationException($"Failed to start process: {fileName}");

        // Read stdout and stderr concurrently to avoid deadlocks
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"Process '{fileName}' timed out after {timeoutSeconds} seconds");
        }

        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Process '{fileName}' exited with code {process.ExitCode}: {stderr}");
        }
    }

    private static string ResolveTargetPath(string path, string targetDrive)
    {
        // If path starts with a drive letter, replace it with the target drive
        if (path.Length >= 2 && path[1] == ':')
        {
            return targetDrive + path[2..];
        }

        // Relative paths are resolved against the target drive root
        return Path.Combine(targetDrive + "\\", path);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
    }

    private static List<DeploymentStep> DeserializeSteps(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]")
            return [];

        return JsonSerializer.Deserialize<List<DeploymentStep>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? [];
    }
}
