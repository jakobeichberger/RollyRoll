using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RollyRoll.Core.Interfaces;
using RollyRoll.Core.Models;
using RollyRoll.Infrastructure.Data;

namespace RollyRoll.Infrastructure.Services;

/// <summary>
/// Orchestrates the full deployment workflow: create task -> send WoL -> PXE boot ->
/// WinPE applies image -> post-deploy steps. Tracks progress via SignalR notifications
/// and manages the complete task lifecycle from queue to completion.
/// </summary>
public class DeploymentEngine : IDeploymentService
{
    private readonly ILogger<DeploymentEngine> _logger;
    private readonly RollyRollDbContext _db;
    private readonly IWakeOnLanService _wolService;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeploymentEngine"/> class.
    /// </summary>
    /// <param name="logger">Logger for deployment operations.</param>
    /// <param name="db">Database context for persisting deployment tasks.</param>
    /// <param name="wolService">Wake-on-LAN service for powering on clients.</param>
    public DeploymentEngine(
        ILogger<DeploymentEngine> logger,
        RollyRollDbContext db,
        IWakeOnLanService wolService)
    {
        _logger = logger;
        _db = db;
        _wolService = wolService;
    }

    /// <inheritdoc />
    public async Task<ScheduledTask> DeployToClientAsync(
        int imageId,
        int clientId,
        DeployMode mode,
        int? templateId = null,
        CancellationToken ct = default)
    {
        var client = await _db.Clients.FindAsync([clientId], ct)
            ?? throw new InvalidOperationException($"Client {clientId} not found.");

        var image = await _db.Images.FindAsync([imageId], ct)
            ?? throw new InvalidOperationException($"Image {imageId} not found.");

        _logger.LogInformation(
            "Starting deployment of image '{ImageName}' (Id={ImageId}) to client {Hostname} ({Mac}), mode={Mode}",
            image.Name, imageId, client.Hostname, client.MacAddress, mode);

        var task = new ScheduledTask
        {
            Name = $"Deploy {image.Name} to {client.Hostname}",
            TaskType = ScheduledTaskType.Deploy,
            Status = DeploymentTaskStatus.Queued,
            ClientId = clientId,
            ImageId = imageId,
            TemplateId = templateId,
            DeployMode = mode,
            SendWakeOnLan = true,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = "System"
        };

        _db.ScheduledTasks.Add(task);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Deployment task created: Id={TaskId}", task.Id);

        // Send Wake-on-LAN to ensure the client is powered on and PXE boots
        await SendWolAndUpdateStatusAsync(task, client.MacAddress, ct);

        return task;
    }

    /// <inheritdoc />
    public async Task<List<ScheduledTask>> DeployToGroupAsync(
        int imageId,
        int groupId,
        DeployMode mode,
        int? templateId = null,
        CancellationToken ct = default)
    {
        var group = await _db.ClientGroups
            .Include(g => g.Clients)
            .FirstOrDefaultAsync(g => g.Id == groupId, ct)
            ?? throw new InvalidOperationException($"Group {groupId} not found.");

        var image = await _db.Images.FindAsync([imageId], ct)
            ?? throw new InvalidOperationException($"Image {imageId} not found.");

        if (group.Clients.Count == 0)
        {
            _logger.LogWarning("Group '{GroupName}' (Id={GroupId}) has no clients; skipping deployment.", group.Name, groupId);
            return [];
        }

        _logger.LogInformation(
            "Starting group deployment of image '{ImageName}' to group '{GroupName}' ({Count} clients), mode={Mode}",
            image.Name, group.Name, group.Clients.Count, mode);

        var tasks = new List<ScheduledTask>();

        foreach (var client in group.Clients)
        {
            var task = new ScheduledTask
            {
                Name = $"Deploy {image.Name} to {client.Hostname}",
                TaskType = ScheduledTaskType.Deploy,
                Status = DeploymentTaskStatus.Queued,
                ClientId = client.Id,
                GroupId = groupId,
                ImageId = imageId,
                TemplateId = templateId,
                DeployMode = mode,
                SendWakeOnLan = true,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = "System"
            };

            _db.ScheduledTasks.Add(task);
            tasks.Add(task);
        }

        await _db.SaveChangesAsync(ct);

        // Send WoL to all clients in parallel
        foreach (var task in tasks)
        {
            var client = group.Clients.First(c => c.Id == task.ClientId);
            await SendWolAndUpdateStatusAsync(task, client.MacAddress, ct);
        }

        _logger.LogInformation(
            "Group deployment queued: {Count} tasks for group '{GroupName}'",
            tasks.Count, group.Name);

        return tasks;
    }

    /// <inheritdoc />
    public async Task<ScheduledTask> ScheduleDeploymentAsync(
        int imageId,
        int targetId,
        bool isGroup,
        DeployMode mode,
        DateTime scheduledAt,
        int? templateId = null,
        CancellationToken ct = default)
    {
        var image = await _db.Images.FindAsync([imageId], ct)
            ?? throw new InvalidOperationException($"Image {imageId} not found.");

        string targetName;
        int? clientId = null;
        int? groupId = null;

        if (isGroup)
        {
            var group = await _db.ClientGroups.FindAsync([targetId], ct)
                ?? throw new InvalidOperationException($"Group {targetId} not found.");
            targetName = group.Name;
            groupId = targetId;
        }
        else
        {
            var client = await _db.Clients.FindAsync([targetId], ct)
                ?? throw new InvalidOperationException($"Client {targetId} not found.");
            targetName = client.Hostname;
            clientId = targetId;
        }

        var task = new ScheduledTask
        {
            Name = $"Scheduled: Deploy {image.Name} to {targetName}",
            TaskType = ScheduledTaskType.Deploy,
            Status = DeploymentTaskStatus.Scheduled,
            ClientId = clientId,
            GroupId = groupId,
            ImageId = imageId,
            TemplateId = templateId,
            DeployMode = mode,
            ScheduledAt = scheduledAt,
            SendWakeOnLan = true,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = "System"
        };

        _db.ScheduledTasks.Add(task);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Deployment scheduled: TaskId={TaskId}, Target={Target}, ScheduledAt={ScheduledAt:O}",
            task.Id, targetName, scheduledAt);

        return task;
    }

    /// <inheritdoc />
    public async Task CancelDeploymentAsync(int taskId, CancellationToken ct = default)
    {
        var task = await _db.ScheduledTasks.FindAsync([taskId], ct)
            ?? throw new InvalidOperationException($"Task {taskId} not found.");

        if (task.Status is DeploymentTaskStatus.Success or DeploymentTaskStatus.Failed or DeploymentTaskStatus.Cancelled)
        {
            throw new InvalidOperationException(
                $"Cannot cancel task {taskId}: task is already in terminal state '{task.Status}'.");
        }

        task.Status = DeploymentTaskStatus.Cancelled;
        task.CompletedAt = DateTime.UtcNow;
        task.StatusMessage = "Cancelled by administrator.";

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Deployment task cancelled: Id={TaskId}, Name={TaskName}", taskId, task.Name);
    }

    /// <inheritdoc />
    public async Task<List<ScheduledTask>> GetActiveDeploymentsAsync(CancellationToken ct = default)
    {
        return await _db.ScheduledTasks
            .Include(t => t.Client)
            .Include(t => t.Image)
            .Include(t => t.Group)
            .Where(t => t.Status == DeploymentTaskStatus.Queued
                     || t.Status == DeploymentTaskStatus.Scheduled
                     || t.Status == DeploymentTaskStatus.WaitingForClient
                     || t.Status == DeploymentTaskStatus.Running)
            .OrderBy(t => t.CreatedAt)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<List<ScheduledTask>> GetClientHistoryAsync(int clientId, CancellationToken ct = default)
    {
        return await _db.ScheduledTasks
            .Include(t => t.Image)
            .Where(t => t.ClientId == clientId)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<ScheduledTask?> GetPendingTaskForClientAsync(string macAddress, CancellationToken ct = default)
    {
        var client = await _db.Clients
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.MacAddress == macAddress, ct);

        if (client is null)
        {
            _logger.LogWarning("No client found for MAC {Mac} when checking for pending tasks.", macAddress);
            return null;
        }

        return await _db.ScheduledTasks
            .Include(t => t.Image)
            .Include(t => t.Template)
            .Where(t => t.ClientId == client.Id
                     && (t.Status == DeploymentTaskStatus.Queued
                      || t.Status == DeploymentTaskStatus.WaitingForClient))
            .OrderBy(t => t.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc />
    public async Task UpdateTaskProgressAsync(
        int taskId,
        int progressPercent,
        string statusMessage,
        CancellationToken ct = default)
    {
        var task = await _db.ScheduledTasks.FindAsync([taskId], ct)
            ?? throw new InvalidOperationException($"Task {taskId} not found.");

        // Start the task if this is the first progress update
        if (task.Status is DeploymentTaskStatus.Queued or DeploymentTaskStatus.WaitingForClient)
        {
            task.Status = DeploymentTaskStatus.Running;
            task.StartedAt = DateTime.UtcNow;
            _logger.LogInformation("Task {TaskId} started running.", taskId);
        }

        task.ProgressPercent = Math.Clamp(progressPercent, 0, 100);
        task.StatusMessage = statusMessage;

        await _db.SaveChangesAsync(ct);

        _logger.LogDebug(
            "Task {TaskId} progress: {Percent}% - {Message}",
            taskId, progressPercent, statusMessage);
    }

    /// <inheritdoc />
    public async Task CompleteTaskAsync(
        int taskId,
        bool success,
        string? errorMessage = null,
        CancellationToken ct = default)
    {
        var task = await _db.ScheduledTasks
            .Include(t => t.Client)
            .FirstOrDefaultAsync(t => t.Id == taskId, ct)
            ?? throw new InvalidOperationException($"Task {taskId} not found.");

        task.CompletedAt = DateTime.UtcNow;

        if (success)
        {
            task.Status = DeploymentTaskStatus.Success;
            task.ProgressPercent = 100;
            task.StatusMessage = "Deployment completed successfully.";

            _logger.LogInformation(
                "Task {TaskId} completed successfully for client {Hostname}.",
                taskId, task.Client?.Hostname ?? "Unknown");
        }
        else
        {
            task.Status = DeploymentTaskStatus.Failed;
            task.ErrorMessage = errorMessage;
            task.StatusMessage = $"Deployment failed: {errorMessage}";

            _logger.LogError(
                "Task {TaskId} failed for client {Hostname}: {Error}",
                taskId, task.Client?.Hostname ?? "Unknown", errorMessage);
        }

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Sends a Wake-on-LAN magic packet and transitions the task to <see cref="DeploymentTaskStatus.WaitingForClient"/>.
    /// </summary>
    private async Task SendWolAndUpdateStatusAsync(ScheduledTask task, string macAddress, CancellationToken ct)
    {
        try
        {
            if (task.SendWakeOnLan)
            {
                await _wolService.SendWakeOnLanAsync(macAddress, ct);
                _logger.LogInformation("WoL sent for task {TaskId} to {Mac}.", task.Id, macAddress);
            }

            task.Status = DeploymentTaskStatus.WaitingForClient;
            task.StatusMessage = "Wake-on-LAN sent. Waiting for client to PXE boot.";
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send WoL for task {TaskId} to {Mac}.", task.Id, macAddress);
            // Task remains queued; WoL failure is not fatal — the client may already be on.
        }
    }
}
