namespace RollyRoll.Core.Models;

/// <summary>
/// The only difference between a "backup restore" and a "clean deployment".
/// Same image, different deploy mode.
/// </summary>
public enum DeployMode
{
    /// <summary>Wipe user profiles, deploy a fresh clean image.</summary>
    CleanDeploy,

    /// <summary>Restore/inject user profiles after applying the image. User sees their familiar desktop.</summary>
    RestoreWithProfiles
}
