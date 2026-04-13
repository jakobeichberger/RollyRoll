using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RollyRoll.Core.Interfaces;
using RollyRoll.Core.Models;
using RollyRoll.Infrastructure.Data;

namespace RollyRoll.Infrastructure.Services;

/// <summary>
/// Manages auto-recovery workflows: creates pre-change snapshots, initiates rollback
/// when deployments or patches fail, and handles snapshot lifecycle/cleanup.
///
/// Recovery flow:
/// 1. Capture current user profiles from the failed client (USMT ScanState).
/// 2. Re-deploy the last known-good image from the snapshot.
/// 3. Re-inject the captured user profiles (USMT LoadState).
/// Result: the user's desktop is restored to its pre-failure state.
/// </summary>
public class RecoveryService : IRecoveryService
{
    private readonly ILogger<RecoveryService> _logger;
    private readonly RollyRollDbContext _db;
    private readonly IUserProfileService? _profileService;
    private readonly IDeploymentService _deploymentService;

    /// <summary>Default number of days to retain recovery snapshots.</summary>
    private const int DefaultRetentionDays = 30;

    /// <summary>Maximum number of snapshots to keep per client regardless of age.</summary>
    private const int MaxSnapshotsPerClient = 10;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecoveryService"/> class.
    /// </summary>
    /// <param name="logger">Logger for recovery operations.</param>
    /// <param name="db">Database context for persisting snapshots.</param>
    /// <param name="deploymentService">Service for deploying images during recovery.</param>
    /// <param name="profileService">Optional: service for capturing and restoring user profiles.</param>
    public RecoveryService(
        ILogger<RecoveryService> logger,
        RollyRollDbContext db,
        IDeploymentService deploymentService,
        IUserProfileService? profileService = null)
    {
        _logger = logger;
        _db = db;
        _deploymentService = deploymentService;
        _profileService = profileService;
    }

    /// <inheritdoc />
    public async Task<RecoverySnapshot> CreateSnapshotAsync(
        int clientId,
        string reason,
        CancellationToken ct = default)
    {
        var client = await _db.Clients.FindAsync([clientId], ct)
            ?? throw new InvalidOperationException($"Client {clientId} not found.");

        _logger.LogInformation(
            "Creating recovery snapshot for client {Hostname} ({Mac}). Reason: {Reason}",
            client.Hostname, client.MacAddress, reason);

        // Find the image currently deployed to this client (most recent successful deployment)
        var lastSuccessfulDeploy = await _db.ScheduledTasks
            .Where(t => t.ClientId == clientId
                     && t.TaskType == ScheduledTaskType.Deploy
                     && t.Status == DeploymentTaskStatus.Success)
            .OrderByDescending(t => t.CompletedAt)
            .FirstOrDefaultAsync(ct);

        // Capture current user profiles from the client (if profile service is available)
        string? profilesPath = null;
        long profilesSizeBytes = 0;

        if (_profileService is null)
        {
            _logger.LogWarning("IUserProfileService not available. Snapshot will be created without user profiles.");
        }
        else
        {
        try
        {
            profilesPath = await _profileService.CaptureProfilesAsync(client.MacAddress, ct);
            profilesSizeBytes = await _profileService.GetProfilesSizeAsync(profilesPath, ct);

            _logger.LogInformation(
                "User profiles captured for client {Hostname}: {Path} ({Size} bytes)",
                client.Hostname, profilesPath, profilesSizeBytes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to capture user profiles for client {Hostname}. Snapshot will be created without profiles.",
                client.Hostname);
        }
        } // end if (_profileService is not null)

        var snapshot = new RecoverySnapshot
        {
            ClientId = clientId,
            ImageId = lastSuccessfulDeploy?.ImageId,
            UserProfilesPath = profilesPath,
            UserProfilesSizeBytes = profilesSizeBytes,
            Reason = reason,
            CreatedAt = DateTime.UtcNow,
            IsEncrypted = profilesPath is not null
        };

        _db.RecoverySnapshots.Add(snapshot);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Recovery snapshot created: Id={SnapshotId}, Client={Hostname}, ImageId={ImageId}, HasProfiles={HasProfiles}",
            snapshot.Id, client.Hostname, snapshot.ImageId, profilesPath is not null);

        return snapshot;
    }

    /// <inheritdoc />
    public async Task<ScheduledTask> InitiateRecoveryAsync(int clientId, CancellationToken ct = default)
    {
        var snapshot = await GetLatestSnapshotAsync(clientId, ct)
            ?? throw new InvalidOperationException(
                $"No recovery snapshot found for client {clientId}. Cannot initiate auto-recovery.");

        return await InitiateRecoveryAsync(clientId, snapshot.Id, ct);
    }

    /// <inheritdoc />
    public async Task<ScheduledTask> InitiateRecoveryAsync(
        int clientId,
        int snapshotId,
        CancellationToken ct = default)
    {
        var client = await _db.Clients.FindAsync([clientId], ct)
            ?? throw new InvalidOperationException($"Client {clientId} not found.");

        var snapshot = await _db.RecoverySnapshots
            .Include(s => s.Image)
            .FirstOrDefaultAsync(s => s.Id == snapshotId && s.ClientId == clientId, ct)
            ?? throw new InvalidOperationException(
                $"Snapshot {snapshotId} not found for client {clientId}.");

        if (snapshot.ImageId is null)
        {
            throw new InvalidOperationException(
                $"Snapshot {snapshotId} has no associated image. Cannot perform image-based recovery.");
        }

        _logger.LogInformation(
            "Initiating auto-recovery for client {Hostname} ({Mac}) using snapshot {SnapshotId}. " +
            "Image: '{ImageName}' (Id={ImageId}), HasProfiles={HasProfiles}",
            client.Hostname, client.MacAddress, snapshotId,
            snapshot.Image?.Name ?? "Unknown", snapshot.ImageId,
            snapshot.UserProfilesPath is not null);

        // Determine deploy mode: if the snapshot has user profiles, restore them after imaging
        var deployMode = snapshot.UserProfilesPath is not null
            ? DeployMode.RestoreWithProfiles
            : DeployMode.CleanDeploy;

        // Create a deployment task for the recovery
        var task = await _deploymentService.DeployToClientAsync(
            snapshot.ImageId.Value,
            clientId,
            deployMode,
            ct: ct);

        // Mark the snapshot as used for recovery
        snapshot.WasUsedForRecovery = true;
        snapshot.RecoveredAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Auto-recovery deployment queued: TaskId={TaskId} for client {Hostname}",
            task.Id, client.Hostname);

        return task;
    }

    /// <inheritdoc />
    public async Task<List<RecoverySnapshot>> GetSnapshotsForClientAsync(
        int clientId,
        CancellationToken ct = default)
    {
        return await _db.RecoverySnapshots
            .Include(s => s.Image)
            .Where(s => s.ClientId == clientId)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<RecoverySnapshot?> GetLatestSnapshotAsync(int clientId, CancellationToken ct = default)
    {
        return await _db.RecoverySnapshots
            .Include(s => s.Image)
            .Where(s => s.ClientId == clientId)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc />
    public async Task CleanupOldSnapshotsAsync(CancellationToken ct = default)
    {
        var cutoffDate = DateTime.UtcNow.AddDays(-DefaultRetentionDays);

        _logger.LogInformation(
            "Starting snapshot cleanup. Retention: {Days} days (cutoff: {Cutoff:O}), max per client: {Max}",
            DefaultRetentionDays, cutoffDate, MaxSnapshotsPerClient);

        // Remove snapshots older than the retention period
        var expiredSnapshots = await _db.RecoverySnapshots
            .Where(s => s.CreatedAt < cutoffDate)
            .ToListAsync(ct);

        foreach (var snapshot in expiredSnapshots)
        {
            await DeleteSnapshotDataAsync(snapshot, ct);
        }

        _db.RecoverySnapshots.RemoveRange(expiredSnapshots);

        // Enforce per-client maximum: keep only the newest MaxSnapshotsPerClient per client
        var clientIds = await _db.RecoverySnapshots
            .Select(s => s.ClientId)
            .Distinct()
            .ToListAsync(ct);

        var excessSnapshots = new List<RecoverySnapshot>();

        foreach (var clientId in clientIds)
        {
            var clientSnapshots = await _db.RecoverySnapshots
                .Where(s => s.ClientId == clientId)
                .OrderByDescending(s => s.CreatedAt)
                .Skip(MaxSnapshotsPerClient)
                .ToListAsync(ct);

            foreach (var snapshot in clientSnapshots)
            {
                await DeleteSnapshotDataAsync(snapshot, ct);
            }

            excessSnapshots.AddRange(clientSnapshots);
        }

        _db.RecoverySnapshots.RemoveRange(excessSnapshots);
        await _db.SaveChangesAsync(ct);

        var totalRemoved = expiredSnapshots.Count + excessSnapshots.Count;
        _logger.LogInformation(
            "Snapshot cleanup complete. Removed {Total} snapshots ({Expired} expired, {Excess} excess).",
            totalRemoved, expiredSnapshots.Count, excessSnapshots.Count);
    }

    /// <summary>
    /// Deletes the user profiles data associated with a snapshot from server storage.
    /// </summary>
    private async Task DeleteSnapshotDataAsync(RecoverySnapshot snapshot, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(snapshot.UserProfilesPath) || _profileService is null)
            return;

        try
        {
            await _profileService.DeleteProfilesAsync(snapshot.UserProfilesPath, ct);
            _logger.LogDebug(
                "Deleted profile data for snapshot {SnapshotId}: {Path}",
                snapshot.Id, snapshot.UserProfilesPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to delete profile data for snapshot {SnapshotId}: {Path}",
                snapshot.Id, snapshot.UserProfilesPath);
        }
    }
}
