using RollyRoll.Core.Models;

namespace RollyRoll.Core.Interfaces;

/// <summary>
/// Manages auto-recovery: create snapshots before changes, rollback on failure.
/// Recovery flow: capture current user profiles -> apply last known-good image -> re-inject profiles.
/// Result: user cannot tell the difference between recovered and normal machine.
/// </summary>
public interface IRecoveryService
{
    /// <summary>Create a recovery snapshot before a deployment or patch installation.</summary>
    Task<RecoverySnapshot> CreateSnapshotAsync(int clientId, string reason, CancellationToken ct = default);

    /// <summary>Initiate auto-recovery for a client using the latest snapshot.</summary>
    Task<ScheduledTask> InitiateRecoveryAsync(int clientId, CancellationToken ct = default);

    /// <summary>Initiate recovery using a specific snapshot.</summary>
    Task<ScheduledTask> InitiateRecoveryAsync(int clientId, int snapshotId, CancellationToken ct = default);

    /// <summary>Get all snapshots for a client.</summary>
    Task<List<RecoverySnapshot>> GetSnapshotsForClientAsync(int clientId, CancellationToken ct = default);

    /// <summary>Get the latest known-good snapshot for a client.</summary>
    Task<RecoverySnapshot?> GetLatestSnapshotAsync(int clientId, CancellationToken ct = default);

    /// <summary>Delete old snapshots beyond retention policy.</summary>
    Task CleanupOldSnapshotsAsync(CancellationToken ct = default);
}
