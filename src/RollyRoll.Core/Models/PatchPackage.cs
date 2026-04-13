namespace RollyRoll.Core.Models;

/// <summary>
/// Represents a Windows update, third-party update, or driver update package.
/// </summary>
public class PatchPackage
{
    public int Id { get; set; }

    /// <summary>Display title, e.g. "2026-04 Cumulative Update for Windows 11".</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>KB article ID for Microsoft patches (e.g. "KB5035853").</summary>
    public string? KbArticleId { get; set; }

    /// <summary>Source of this patch.</summary>
    public PatchSource Source { get; set; }

    /// <summary>Patch classification for categorization.</summary>
    public PatchClassification Classification { get; set; }

    /// <summary>Version string.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Size of the patch file in bytes.</summary>
    public long SizeBytes { get; set; }

    /// <summary>Whether an admin has approved this patch for rollout.</summary>
    public bool IsApproved { get; set; }

    /// <summary>When the patch was approved.</summary>
    public DateTime? ApprovedAt { get; set; }

    /// <summary>Who approved this patch.</summary>
    public string? ApprovedBy { get; set; }

    /// <summary>Path to installer file for third-party/driver packages.</summary>
    public string? InstallerPath { get; set; }

    /// <summary>Silent install arguments for third-party packages.</summary>
    public string? SilentInstallArgs { get; set; }

    /// <summary>When this patch was first synced/discovered.</summary>
    public DateTime DiscoveredAt { get; set; } = DateTime.UtcNow;

    /// <summary>Release date from the vendor.</summary>
    public DateTime? ReleaseDate { get; set; }

    /// <summary>Whether this patch requires a reboot.</summary>
    public bool RequiresReboot { get; set; }

    /// <summary>Severity rating from vendor.</summary>
    public PatchSeverity Severity { get; set; }
}

public enum PatchClassification
{
    SecurityUpdate,
    CumulativeUpdate,
    FeatureUpdate,
    DriverUpdate,
    ThirdPartySoftware,
    DefinitionUpdate
}

public enum PatchSeverity
{
    Low,
    Moderate,
    Important,
    Critical
}
