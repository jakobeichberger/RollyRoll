namespace RollyRoll.Core.Models;

/// <summary>
/// WIM compression level for image capture. Maps to DISM compression options.
/// </summary>
public enum CompressionLevel
{
    /// <summary>No compression — fastest capture, largest files.</summary>
    None,

    /// <summary>XPRESS compression — good balance, ~40% size reduction.</summary>
    Fast,

    /// <summary>LZX compression — best compression, ~60% reduction, slower capture.</summary>
    Maximum
}
