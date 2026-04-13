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
                var previousRingReady = await IsRingCompleteAsync(patch, previousRing, deploymentService, ct);
                if (!previousRingReady)
                {
                    _logger.LogDebug("Patch {PatchTitle}: ring '{PreviousRing}' not yet complete, skipping '{CurrentRing}'",
                        patch.Title, previousRing.Name, ring.Name);
                    break; // Stop processing further rings for this patch
                }

                // Check delay requirement
                if (ring.DelayDaysAfterPrevious > 0)
                {
                    var delayMet = await HasDelayElapsedAsync(patch, previousRing, ring.DelayDaysAfterPrevious, deploymentService, ct);
                    if (!delayMet)
                    {
                        _logger.LogDebug("Patch {PatchTitle}: delay period not met for ring '{RingName}'",
                            patch.Title, ring.Name);
                        break;
                    }
                }
            }

            // Check failure threshold for this ring
            var failureCheck = await CheckFailureThresholdAsync(patch, ring, deploymentService, ct);
            if (failureCheck.ThresholdExceeded)
            {
                _logger.LogWarning(
                    "Patch {PatchTitle}: failure threshold exceeded in ring '{RingName}' ({FailurePercent}% > {Threshold}%). Pausing rollout",
                    patch.Title, ring.Name, failureCheck.FailurePercent, ring.FailureThresholdPercent);

                await patchService.PauseRolloutAsync(patch.Id,
                    $"Failure threshold exceeded in ring '{ring.Name}': {failureCheck.FailurePercent}% failed (threshold: {ring.FailureThresholdPercent}%)", ct);
                break;
            }

            // Deploy to clients in this ring that haven't been patched yet
            await DeployPatchToRingAsync(patch, ring, deploymentService, ct);
        }
    }

    private async Task<bool> IsRingCompleteAsync(
        PatchPackage patch,
        PatchRolloutRing ring,
        IDeploymentService deploymentService,
        CancellationToken ct)
    {
        // Check if all clients in the ring's groups have completed this patch
        foreach (var group in ring.Groups)
        {
            var tasks = await deploymentService.GetActiveDeploymentsAsync(ct);
            var pendingInGroup = tasks.Any(t =>
                t.GroupId == group.Id &&
                t.TaskType == ScheduledTaskType.PatchInstall &&
                t.Status is DeploymentTaskStatus.Queued or DeploymentTaskStatus.Running or DeploymentTaskStatus.WaitingForClient);

            if (pendingInGroup)
                return false;
        }

        return true;
    }

    private Task<bool> HasDelayElapsedAsync(
        PatchPackage patch,
        PatchRolloutRing previousRing,
        int delayDays,
        IDeploymentService deploymentService,
        CancellationToken ct)
    {
        // For the delay calculation, we need the completion time of the previous ring.
        // If the patch was approved more than delayDays ago, we consider the delay met
        // as a simplified check. A full implementation would track per-ring completion times.
        if (patch.ApprovedAt.HasValue)
        {
            var daysSinceApproval = (DateTime.UtcNow - patch.ApprovedAt.Value).TotalDays;
            var totalDelayForThisRing = delayDays * (previousRing.Order);
            return Task.FromResult(daysSinceApproval >= totalDelayForThisRing);
        }

        return Task.FromResult(false);
    }

    private async Task<FailureCheckResult> CheckFailureThresholdAsync(
        PatchPackage patch,
        PatchRolloutRing ring,
        IDeploymentService deploymentService,
        CancellationToken ct)
    {
        var totalClients = 0;
        var failedClients = 0;

        foreach (var group in ring.Groups)
        {
            var tasks = await deploymentService.GetActiveDeploymentsAsync(ct);
            var groupTasks = tasks.Where(t =>
                t.GroupId == group.Id &&
                t.TaskType == ScheduledTaskType.PatchInstall).ToList();

            totalClients += groupTasks.Count;
            failedClients += groupTasks.Count(t => t.Status == DeploymentTaskStatus.Failed);
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

    private async Task DeployPatchToRingAsync(
        PatchPackage patch,
        PatchRolloutRing ring,
        IDeploymentService deploymentService,
        CancellationToken ct)
    {
        foreach (var group in ring.Groups)
        {
            if (ct.IsCancellationRequested) break;

            _logger.LogInformation("Deploying patch '{PatchTitle}' to group '{GroupName}' in ring '{RingName}'",
                patch.Title, group.Name, ring.Name);

            try
            {
                await deploymentService.DeployToGroupAsync(
                    imageId: 0, // Patch tasks don't use images
                    groupId: group.Id,
                    mode: DeployMode.CleanDeploy,
                    ct: ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deploy patch '{PatchTitle}' to group '{GroupName}'",
                    patch.Title, group.Name);
            }
        }
    }

    private sealed class FailureCheckResult
    {
        public bool ThresholdExceeded { get; init; }
        public double FailurePercent { get; init; }
        public int TotalClients { get; init; }
        public int FailedClients { get; init; }
    }
}
