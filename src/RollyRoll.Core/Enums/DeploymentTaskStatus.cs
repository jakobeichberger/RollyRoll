namespace RollyRoll.Core.Models;

/// <summary>
/// Status of a deployment, capture, or patch task.
/// </summary>
public enum DeploymentTaskStatus
{
    /// <summary>Task is waiting in the queue.</summary>
    Queued,

    /// <summary>Waiting for the scheduled time.</summary>
    Scheduled,

    /// <summary>WoL sent, waiting for client to PXE boot.</summary>
    WaitingForClient,

    /// <summary>Client has booted into WinPE, task is executing.</summary>
    Running,

    /// <summary>Task completed successfully.</summary>
    Success,

    /// <summary>Task failed. Check ErrorMessage for details.</summary>
    Failed,

    /// <summary>Task failed and was automatically rolled back to previous state.</summary>
    RolledBack,

    /// <summary>Task was cancelled by an admin.</summary>
    Cancelled,

    /// <summary>Rollout paused due to failure threshold exceeded in the ring.</summary>
    PausedByThreshold
}
