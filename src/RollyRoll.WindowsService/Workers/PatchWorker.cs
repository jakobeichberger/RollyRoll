using RollyRoll.Core.Interfaces;
using RollyRoll.Core.Models;

namespace RollyRoll.WindowsService.Workers;

/// <summary>
/// Background service that processes the patch rollout queue.
/// Checks ring schedules, processes patches per ring order, and pauses rollouts
/// when the failure threshold is exceeded within a ring.
/// </summary>
public class PatchWorker : BackgroundService
{
    private readonly ILogger<PatchWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    public PatchWorker(
        ILogger<PatchWorker> logger,
        IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PatchWorker started. Checking ring schedules every {Interval}m", PollInterval.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPatchRolloutAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing patch rollout queue");
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

        _logger.LogInformation("PatchWorker stopped");
    }

    private async Task ProcessPatchRolloutAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var patchService = scope.ServiceProvider.GetRequiredService<IPatchService>();
        var deploymentService = scope.ServiceProvider.GetRequiredService<IDeploymentService>();

        // Get approved patches and rollout rings
        var approvedPatches = await patchService.GetApprovedPatchesAsync(ct);
        if (approvedPatches.Count == 0)
            return;

        var rings = await patchService.GetRolloutRingsAsync(ct);
        if (rings.Count == 0)
        {
            _logger.LogWarning("No rollout rings configured. Patches will not be deployed until rings are set up");
            return;
        }

        // Process rings in order
        var orderedRings = rings.Where(r => r.IsActive).OrderBy(r => r.Order).ToList();

        foreach (var patch in approvedPatches)
        {
            if (ct.IsCancellationRequested) break;

            await ProcessPatchAcrossRingsAsync(patch, orderedRings, patchService, deploymentService, ct);
        }
    }

    private async Task ProcessPatchAcrossRingsAsync(
        PatchPackage patch,
        List<PatchRolloutRing> rings,
        IPatchService patchService,
        IDeploymentService deploymentService,
        CancellationToken ct)
    {
        for (var i = 0; i < rings.Count; i++)
        {
            if (ct.IsCancellationRequested) break;

            var ring = rings[i];
            var previousRing = i > 0 ? rings[i - 1] : null;

            // Check if previous ring has completed successfully and delay period has passed
            if (previousRing != null)
            {
                var previousRingReady = await IsRingCompleteAsync(previousRing, deploymentService, ct);
                if (!previousRingReady)
                {
                    _logger.LogDebug("Patch {PatchTitle}: ring '{PreviousRing}' not yet complete, skipping '{CurrentRing}'",
                        patch.Title, previousRing.Name, ring.Name);
                    break;
                }

                // Check delay requirement
                if (ring.DelayDaysAfterPrevious > 0)
                {
                    var delayMet = HasDelayElapsed(patch, previousRing, ring.DelayDaysAfterPrevious);
                    if (!delayMet)
                    {
                        _logger.LogDebug("Patch {PatchTitle}: delay period not met for ring '{RingName}'",
                            patch.Title, ring.Name);
                        break;
                    }
                }
            }

            // Check failure threshold for this ring (queries ALL tasks including failed ones)
            var failureCheck = await CheckFailureThresholdAsync(ring, deploymentService, ct);
            if (failureCheck.ThresholdExceeded)
            {
                _logger.LogWarning(
                    "Patch {PatchTitle}: failure threshold exceeded in ring '{RingName}' ({FailurePercent:F0}% > {Threshold}%). Pausing rollout",
                    patch.Title, ring.Name, failureCheck.FailurePercent, ring.FailureThresholdPercent);

                await patchService.PauseRolloutAsync(patch.Id,
                    $"Failure threshold exceeded in ring '{ring.Name}': {failureCheck.FailurePercent:F0}% failed (threshold: {ring.FailureThresholdPercent}%)", ct);
                break;
            }

            // Patch tasks are handled by the ClientAgent, not image-based deployment.
            // Log that the ring is being processed — the actual installation is done by
            // the PatchInstaller on each client which polls the server for approved patches.
            _logger.LogInformation("Ring '{RingName}' is active for patch '{PatchTitle}'. " +
                "ClientAgents in this ring's groups will pick up the patch on next check cycle.",
                ring.Name, patch.Title);
        }
    }

    /// <summary>
    /// Check if all patch tasks in a ring's groups have completed (Success or Failed — not still Running/Queued).
    /// Uses GetAllTasksForGroupAsync which includes ALL statuses, not just active ones.
    /// </summary>
    private async Task<bool> IsRingCompleteAsync(
        PatchRolloutRing ring,
        IDeploymentService deploymentService,
        CancellationToken ct)
    {
        foreach (var group in ring.Groups)
        {
            var allTasks = await deploymentService.GetAllTasksForGroupAsync(group.Id, ScheduledTaskType.PatchInstall, ct);

            var hasPending = allTasks.Any(t =>
                t.Status is DeploymentTaskStatus.Queued
                    or DeploymentTaskStatus.Running
                    or DeploymentTaskStatus.WaitingForClient
                    or DeploymentTaskStatus.Scheduled);

            if (hasPending)
                return false;
        }

        return true;
    }

    private static bool HasDelayElapsed(PatchPackage patch, PatchRolloutRing previousRing, int delayDays)
    {
        if (!patch.ApprovedAt.HasValue)
            return false;

        var daysSinceApproval = (DateTime.UtcNow - patch.ApprovedAt.Value).TotalDays;
        var totalDelayForThisRing = delayDays * previousRing.Order;
        return daysSinceApproval >= totalDelayForThisRing;
    }

    /// <summary>
    /// Check failure threshold using ALL tasks (including completed/failed), not just active ones.
    /// </summary>
    private async Task<FailureCheckResult> CheckFailureThresholdAsync(
        PatchRolloutRing ring,
        IDeploymentService deploymentService,
        CancellationToken ct)
    {
        var totalClients = 0;
        var failedClients = 0;

        foreach (var group in ring.Groups)
        {
            var allTasks = await deploymentService.GetAllTasksForGroupAsync(group.Id, ScheduledTaskType.PatchInstall, ct);
            totalClients += allTasks.Count;
            failedClients += allTasks.Count(t => t.Status == DeploymentTaskStatus.Failed);
        }

        var failurePercent = totalClients > 0 ? (double)failedClients / totalClients * 100 : 0;

        return new FailureCheckResult
        {
            ThresholdExceeded = failurePercent > ring.FailureThresholdPercent,
            FailurePercent = failurePercent,
            TotalClients = totalClients,
            FailedClients = failedClients
        };
    }

    private sealed class FailureCheckResult
    {
        public bool ThresholdExceeded { get; init; }
        public double FailurePercent { get; init; }
        public int TotalClients { get; init; }
        public int FailedClients { get; init; }
    }
}
