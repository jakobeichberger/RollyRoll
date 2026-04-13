namespace RollyRoll.Core.Models;

/// <summary>
/// Where a patch/update package came from.
/// </summary>
public enum PatchSource
{
    /// <summary>Microsoft Windows Update catalog.</summary>
    Microsoft,

    /// <summary>Third-party application update (manually uploaded or from catalog).</summary>
    ThirdParty,

    /// <summary>Hardware driver update.</summary>
    Driver
}
