namespace RollyRoll.Core.Models;

/// <summary>
/// Audit trail entry for every significant action in the system.
/// </summary>
public class AuditLogEntry
{
    public int Id { get; set; }

    /// <summary>What happened, e.g. "ImageDeployed", "PatchApproved", "ClientRecovered".</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Category for filtering.</summary>
    public AuditCategory Category { get; set; }

    /// <summary>Who performed this action (admin username or "System" for automated actions).</summary>
    public string PerformedBy { get; set; } = string.Empty;

    /// <summary>The target of the action (client hostname, image name, etc.).</summary>
    public string? TargetName { get; set; }

    /// <summary>Target entity ID for linking.</summary>
    public int? TargetId { get; set; }

    /// <summary>Detailed description of what happened.</summary>
    public string Details { get; set; } = string.Empty;

    /// <summary>Whether the action succeeded.</summary>
    public bool Success { get; set; } = true;

    /// <summary>Error message if the action failed.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Source IP address of the admin who performed this action.</summary>
    public string? SourceIpAddress { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public enum AuditCategory
{
    Deployment,
    ImageCapture,
    PatchManagement,
    Recovery,
    ClientManagement,
    GroupManagement,
    Settings,
    Authentication,
    WakeOnLan,
    Scheduling
}
