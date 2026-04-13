using RollyRoll.Core.Interfaces;
using RollyRoll.Core.Models;

namespace RollyRoll.WindowsService.Workers;

/// <summary>
/// Background service that monitors the deployment queue and processes pending tasks.
/// Polls every 5 seconds for queued tasks that are ready to execute.
/// Handles WoL, state transitions, and timeout detection for active deployments.
/// </summary>
public class DeploymentWorker : BackgroundService
{
    private readonly ILogger<DeploymentWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    public DeploymentWorker(
        ILogger<DeploymentWorker> logger,
        IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DeploymentWorker started. Polling every {Interval}s", PollInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessDeploymentQueueAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing deployment queue");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("DeploymentWorker stopped");
    }

    private async Task ProcessDeploymentQueueAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var deploymentService = scope.ServiceProvider.GetRequiredService<IDeploymentService>();
        var wolService = scope.ServiceProvider.GetRequiredService<IWakeOnLanService>();
        var clientDiscovery = scope.ServiceProvider.GetRequiredService<IClientDiscoveryService>();

        var activeTasks = await deploymentService.GetActiveDeploymentsAsync(ct);
        if (activeTasks.Count == 0)
            return;

        foreach (var task in activeTasks)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                await ProcessTaskAsync(task, deploymentService, wolService, clientDiscovery, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing deployment task {TaskId} ({TaskName})", task.Id, task.Name);
                await deploymentService.CompleteTaskAsync(task.Id, success: false, errorMessage: ex.Message, ct: ct);
            }
        }
    }

    private async Task ProcessTaskAsync(
        ScheduledTask task,
        IDeploymentService deploymentService,
        IWakeOnLanService wolService,
        IClientDiscoveryService clientDiscovery,
        CancellationToken ct)
    {
        switch (task.Status)
        {
            case DeploymentTaskStatus.Queued:
                _logger.LogInformation("Processing queued task {TaskId}: {TaskName}", task.Id, task.Name);

                // Send Wake-on-LAN if configured
                if (task.SendWakeOnLan && task.ClientId.HasValue)
                {
                    var client = await clientDiscovery.GetClientByIdAsync(task.ClientId.Value, ct);
                    if (client != null)
                    {
                        _logger.LogInformation("Sending WoL to {Mac} for task {TaskId}", client.MacAddress, task.Id);
                        await wolService.SendWakeOnLanAsync(client.MacAddress, ct);
                    }
                }

                // Transition to WaitingForClient — the WinPE agent will pick up the task
                await deploymentService.UpdateTaskProgressAsync(task.Id, 0, "Waiting for client to PXE boot...", ct);
                break;

            case DeploymentTaskStatus.WaitingForClient:
                // Check for timeout — if client hasn't booted within a reasonable window
                if (task.Template != null && task.StartedAt.HasValue)
                {
                    var elapsed = DateTime.UtcNow - task.StartedAt.Value;
                    var timeout = TimeSpan.FromMinutes(task.Template.TimeoutMinutes);
                    if (elapsed > timeout)
                    {
                        _logger.LogWarning("Task {TaskId} timed out waiting for client", task.Id);
                        await deploymentService.CompleteTaskAsync(task.Id, success: false,
                            errorMessage: $"Timed out after {elapsed.TotalMinutes:F0} minutes waiting for client to boot", ct: ct);
                    }
                }
                break;

            case DeploymentTaskStatus.Running:
                // Monitor running tasks for timeout
                if (task.Template != null && task.StartedAt.HasValue)
                {
                    var elapsed = DateTime.UtcNow - task.StartedAt.Value;
                    var timeout = TimeSpan.FromMinutes(task.Template.TimeoutMinutes);
                    if (elapsed > timeout)
                    {
                        _logger.LogWarning("Task {TaskId} timed out during execution ({Elapsed:F0}m > {Timeout}m)",
                            task.Id, elapsed.TotalMinutes, task.Template.TimeoutMinutes);
                        await deploymentService.CompleteTaskAsync(task.Id, success: false,
                            errorMessage: $"Deployment timed out after {elapsed.TotalMinutes:F0} minutes", ct: ct);
                    }
                }
                break;
        }
    }
}
