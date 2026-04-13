namespace RollyRoll.Core.Models;

/// <summary>
/// A timetabled deployment, capture, patch, or maintenance task.
/// Can target a single client, a group, or multiple groups.
/// </summary>
public class ScheduledTask
{
    public int Id { get; set; }

    /// <summary>Display name for this task.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>What type of task this is.</summary>
    public ScheduledTaskType TaskType { get; set; }

    /// <summary>Current status of this task.</summary>
    public DeploymentTaskStatus Status { get; set; } = DeploymentTaskStatus.Queued;

    /// <summary>Detailed status message (e.g. "Applying image: 45%").</summary>
    public string StatusMessage { get; set; } = string.Empty;

    /// <summary>Progress percentage (0-100).</summary>
    public int ProgressPercent { get; set; }

    // Target: either a single client or a group (or both for group tasks creating per-client subtasks)
    public int? ClientId { get; set; }
    public Client? Client { get; set; }

    public int? GroupId { get; set; }
    public ClientGroup? Group { get; set; }

    /// <summary>Image to deploy (for Deploy/Capture tasks).</summary>
    public int? ImageId { get; set; }
    public Image? Image { get; set; }

    /// <summary>Template to use (for Deploy tasks).</summary>
    public int? TemplateId { get; set; }
    public DeploymentTemplate? Template { get; set; }

    /// <summary>Deploy mode for this specific task.</summary>
    public DeployMode DeployMode { get; set; } = DeployMode.CleanDeploy;

    /// <summary>When this task should execute. Null = execute immediately.</summary>
    public DateTime? ScheduledAt { get; set; }

    /// <summary>Cron expression for recurring tasks (null = one-time).</summary>
    public string? CronExpression { get; set; }

    /// <summary>Whether to send WoL before executing.</summary>
    public bool SendWakeOnLan { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>Error message if the task failed.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Who created this task (admin username).</summary>
    public string CreatedBy { get; set; } = string.Empty;
}

public enum ScheduledTaskType
{
    Deploy,
    Capture,
    WakeOnLan,
    Shutdown,
    Reboot,
    PatchInstall,
    DriverUpdate
}
