namespace RollyRoll.Core.Models;

/// <summary>
/// A pre-change snapshot taken before a deployment or patch installation.
/// Used for auto-rollback if something fails.
/// Includes the image reference AND separately captured user profiles.
/// </summary>
public class RecoverySnapshot
{
    public int Id { get; set; }

    /// <summary>The client this snapshot belongs to.</summary>
    public int ClientId { get; set; }
    public Client? Client { get; set; }

    /// <summary>The known-good image at the time of snapshot.</summary>
    public int? ImageId { get; set; }
    public Image? Image { get; set; }

    /// <summary>Path to the captured user profiles (USMT export) on the server.</summary>
    public string? UserProfilesPath { get; set; }

    /// <summary>Size of the user profiles export in bytes.</summary>
    public long UserProfilesSizeBytes { get; set; }

    /// <summary>What triggered this snapshot (e.g. "Pre-patch KB5035853", "Pre-deploy Win11-23H2-Base").</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>When this snapshot was created.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Whether this snapshot has been used for a recovery.</summary>
    public bool WasUsedForRecovery { get; set; }

    /// <summary>When this snapshot was used for recovery (if applicable).</summary>
    public DateTime? RecoveredAt { get; set; }

    /// <summary>Whether the user profiles in this snapshot are encrypted at rest.</summary>
    public bool IsEncrypted { get; set; } = true;
}
