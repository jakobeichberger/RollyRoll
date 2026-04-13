using RollyRoll.Core.Models;

namespace RollyRoll.Core.Interfaces;

/// <summary>
/// Orchestrates the full deployment workflow: WoL -> PXE boot -> WinPE -> apply image -> post-deploy steps.
/// </summary>
public interface IDeploymentService
{
    /// <summary>Deploy an image to a single client.</summary>
    Task<ScheduledTask> DeployToClientAsync(int imageId, int clientId, DeployMode mode, int? templateId = null, CancellationToken ct = default);

    /// <summary>Deploy an image to all clients in a group.</summary>
    Task<List<ScheduledTask>> DeployToGroupAsync(int imageId, int groupId, DeployMode mode, int? templateId = null, CancellationToken ct = default);

    /// <summary>Schedule a deployment for a future time.</summary>
    Task<ScheduledTask> ScheduleDeploymentAsync(int imageId, int targetId, bool isGroup, DeployMode mode, DateTime scheduledAt, int? templateId = null, CancellationToken ct = default);

    /// <summary>Cancel a queued or scheduled deployment.</summary>
    Task CancelDeploymentAsync(int taskId, CancellationToken ct = default);

    /// <summary>Get all active (running + queued) deployment tasks.</summary>
    Task<List<ScheduledTask>> GetActiveDeploymentsAsync(CancellationToken ct = default);

    /// <summary>Get deployment history for a client.</summary>
    Task<List<ScheduledTask>> GetClientHistoryAsync(int clientId, CancellationToken ct = default);

    /// <summary>Get the current task for a client MAC (called by WinPE agent).</summary>
    Task<ScheduledTask?> GetPendingTaskForClientAsync(string macAddress, CancellationToken ct = default);

    /// <summary>Update task progress (called by WinPE agent during deployment).</summary>
    Task UpdateTaskProgressAsync(int taskId, int progressPercent, string statusMessage, CancellationToken ct = default);

    /// <summary>Mark task as completed or failed (called by WinPE agent).</summary>
    Task CompleteTaskAsync(int taskId, bool success, string? errorMessage = null, CancellationToken ct = default);

    /// <summary>Get all tasks for a group with any status (including completed/failed). Used by patch rollout logic.</summary>
    Task<List<ScheduledTask>> GetAllTasksForGroupAsync(int groupId, ScheduledTaskType taskType, CancellationToken ct = default);
}
