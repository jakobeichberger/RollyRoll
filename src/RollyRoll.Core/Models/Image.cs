namespace RollyRoll.Core.Models;

/// <summary>
/// Unified image model — backups and deployment images are the same WIM file.
/// The only difference is a deploy-time option (DeployMode): clean install or restore with profiles.
/// Any image can be deployed either way.
/// </summary>
public class Image
{
    public int Id { get; set; }

    /// <summary>Display name, e.g. "Win11-23H2-Base" or "PC-SMITH-2026-04-08".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional description for this image.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Full path to the .wim file on server storage.</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>File size in bytes.</summary>
    public long SizeBytes { get; set; }

    /// <summary>Hostname of the PC this image was captured from.</summary>
    public string? SourceClientHostname { get; set; }

    /// <summary>MAC of the source client (for traceability).</summary>
    public string? SourceClientMac { get; set; }

    /// <summary>Detected OS version inside the image, e.g. "Windows 11 23H2".</summary>
    public string OsVersion { get; set; } = string.Empty;

    /// <summary>When this image was captured.</summary>
    public DateTime CapturedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Informational: does this capture include user profile data?</summary>
    public bool ContainsUserProfiles { get; set; }

    /// <summary>Gold image (clean reference) vs machine capture (from specific PC).</summary>
    public ImageType Type { get; set; }

    /// <summary>Compression level used during capture.</summary>
    public CompressionLevel Compression { get; set; } = CompressionLevel.Fast;

    /// <summary>SHA-256 hash of the WIM file for integrity verification.</summary>
    public string? FileHash { get; set; }

    /// <summary>Whether this image is marked as the default for new deployments.</summary>
    public bool IsDefault { get; set; }

    /// <summary>Version tag for tracking image iterations.</summary>
    public int Version { get; set; } = 1;
}
