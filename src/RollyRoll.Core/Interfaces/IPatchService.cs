using RollyRoll.Core.Models;

namespace RollyRoll.Core.Interfaces;

/// <summary>
/// Manages Windows patches, third-party updates, and driver updates with ring-based rollout.
/// </summary>
public interface IPatchService
{
    /// <summary>Sync available updates from Microsoft Update catalog.</summary>
    Task SyncMicrosoftCatalogAsync(CancellationToken ct = default);

    /// <summary>Get all available (unapproved) patches.</summary>
    Task<List<PatchPackage>> GetAvailablePatchesAsync(CancellationToken ct = default);

    /// <summary>Get all approved patches.</summary>
    Task<List<PatchPackage>> GetApprovedPatchesAsync(CancellationToken ct = default);

    /// <summary>Approve a patch for ring-based rollout.</summary>
    Task ApprovePatchAsync(int patchId, string approvedBy, CancellationToken ct = default);

    /// <summary>Revoke approval for a patch (stops further rollout).</summary>
    Task RevokePatchAsync(int patchId, CancellationToken ct = default);

    /// <summary>Upload a third-party update package.</summary>
    Task<PatchPackage> UploadThirdPartyPatchAsync(string title, string version, Stream installerStream, string fileName, string? silentArgs = null, CancellationToken ct = default);

    /// <summary>Get all rollout rings with their current status.</summary>
    Task<List<PatchRolloutRing>> GetRolloutRingsAsync(CancellationToken ct = default);

    /// <summary>Create or update a rollout ring.</summary>
    Task<PatchRolloutRing> SaveRolloutRingAsync(PatchRolloutRing ring, CancellationToken ct = default);

    /// <summary>Get the patch compliance status for all clients.</summary>
    Task<PatchComplianceReport> GetComplianceReportAsync(CancellationToken ct = default);

    /// <summary>Pause rollout for a specific patch (e.g. due to failures exceeding threshold).</summary>
    Task PauseRolloutAsync(int patchId, string reason, CancellationToken ct = default);

    /// <summary>Resume a paused rollout.</summary>
    Task ResumeRolloutAsync(int patchId, CancellationToken ct = default);
}

public class PatchComplianceReport
{
    public int TotalClients { get; set; }
    public int FullyPatched { get; set; }
    public int PendingPatches { get; set; }
    public int FailedPatches { get; set; }
    public double CompliancePercent => TotalClients > 0 ? (double)FullyPatched / TotalClients * 100 : 0;
}
