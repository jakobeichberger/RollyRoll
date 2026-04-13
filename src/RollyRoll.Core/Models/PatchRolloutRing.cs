namespace RollyRoll.Core.Models;

/// <summary>
/// A ring in the patch rollout pipeline. Patches flow through rings sequentially:
/// Pilot (key users test) -> Early Adopters -> Broad -> All.
/// Each ring must succeed before the next one starts.
/// </summary>
public class PatchRolloutRing
{
    public int Id { get; set; }

    /// <summary>Ring name, e.g. "Pilot", "Early Adopters", "Broad", "All".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Description of this ring's purpose.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Execution order (1 = first ring, patches start here).</summary>
    public int Order { get; set; }

    /// <summary>Days to wait after previous ring succeeds before deploying to this ring.</summary>
    public int DelayDaysAfterPrevious { get; set; }

    /// <summary>
    /// If more than this percentage of clients in the ring fail, auto-pause the rollout.
    /// E.g. 10 = pause if >10% of ring clients fail.
    /// </summary>
    public int FailureThresholdPercent { get; set; } = 10;

    /// <summary>Groups assigned to this ring.</summary>
    public List<ClientGroup> Groups { get; set; } = [];

    /// <summary>Whether this ring is currently active (accepting patches).</summary>
    public bool IsActive { get; set; } = true;
}
