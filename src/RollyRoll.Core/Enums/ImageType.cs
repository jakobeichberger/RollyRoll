namespace RollyRoll.Core.Models;

/// <summary>
/// Classification of an image — informational only, does not restrict how it can be deployed.
/// </summary>
public enum ImageType
{
    /// <summary>A clean reference image built from scratch (no user data).</summary>
    GoldImage,

    /// <summary>A capture from a specific machine (may contain user data).</summary>
    MachineCapture
}
