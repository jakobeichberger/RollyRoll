using RollyRoll.Core.Models;

namespace RollyRoll.Core.Interfaces;

/// <summary>
/// Manages WIM image capture, apply, storage, and lifecycle.
/// Uses DISM API for all WIM operations.
/// </summary>
public interface IImageService
{
    /// <summary>Capture an image from a client's disk to a WIM file on the server.</summary>
    Task<Image> CaptureImageAsync(string clientMac, string imageName, CompressionLevel compression, CancellationToken ct = default);

    /// <summary>Apply a WIM image to a client's disk.</summary>
    Task ApplyImageAsync(int imageId, string clientMac, DeployMode deployMode, CancellationToken ct = default);

    /// <summary>Get all images in the library.</summary>
    Task<List<Image>> GetAllImagesAsync(CancellationToken ct = default);

    /// <summary>Get a specific image by ID.</summary>
    Task<Image?> GetImageByIdAsync(int id, CancellationToken ct = default);

    /// <summary>Delete an image and its WIM file from storage.</summary>
    Task DeleteImageAsync(int id, CancellationToken ct = default);

    /// <summary>Verify image integrity by checking SHA-256 hash.</summary>
    Task<bool> VerifyImageIntegrityAsync(int id, CancellationToken ct = default);

    /// <summary>Get the default image (if one is set).</summary>
    Task<Image?> GetDefaultImageAsync(CancellationToken ct = default);

    /// <summary>Calculate and return storage usage statistics.</summary>
    Task<ImageStorageStats> GetStorageStatsAsync(CancellationToken ct = default);
}

public class ImageStorageStats
{
    public long TotalSizeBytes { get; set; }
    public int TotalImages { get; set; }
    public long AvailableSpaceBytes { get; set; }
    public string StoragePath { get; set; } = string.Empty;
}
