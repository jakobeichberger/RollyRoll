namespace RollyRoll.Core.Models;

/// <summary>
/// A logical group of PCs for batch operations (deployment, patching, WoL, scheduling).
/// </summary>
public class ClientGroup
{
    public int Id { get; set; }

    /// <summary>Group name, e.g. "Lab-Room-201", "Finance-Department", "Pilot-Testers".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Description of what this group represents.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Default template to use when deploying to this group.</summary>
    public int? DefaultTemplateId { get; set; }
    public DeploymentTemplate? DefaultTemplate { get; set; }

    /// <summary>Default image for this group (overrides template if set).</summary>
    public int? DefaultImageId { get; set; }
    public Image? DefaultImage { get; set; }

    /// <summary>Clients belonging to this group.</summary>
    public List<Client> Clients { get; set; } = [];

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
