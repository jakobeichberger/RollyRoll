using Microsoft.Extensions.Logging;
using RollyRoll.Core.Interfaces;
using RollyRoll.Core.Models;
using RollyRoll.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Security.Cryptography;

namespace RollyRoll.Infrastructure.Services;

/// <summary>
/// Image management service using DISM (Deployment Image Servicing and Management) for WIM operations.
/// Handles image capture, apply, verification, and storage management.
///
/// Uses DISM.exe command-line tool (available on all Windows Server installations) rather than
/// the DISM API COM interop, for simplicity and reliability.
///
/// Compression levels map to DISM options:
///   - None    -> /Compress:none
///   - Fast    -> /Compress:fast    (XPRESS, ~40% reduction)
///   - Maximum -> /Compress:max     (LZX, ~60% reduction)
/// </summary>
public class DismImageService : IImageService
{
    private readonly ILogger<DismImageService> _logger;
    private readonly RollyRollDbContext _db;
    private readonly string _imageStorePath;

    public DismImageService(ILogger<DismImageService> logger, RollyRollDbContext db, string imageStorePath)
    {
        _logger = logger;
        _db = db;
        _imageStorePath = imageStorePath;
        Directory.CreateDirectory(_imageStorePath);
    }

    public async Task<Image> CaptureImageAsync(string clientMac, string imageName, CompressionLevel compression, CancellationToken ct = default)
    {
        var sanitizedName = SanitizeFileName(imageName);
        var fileName = $"{sanitizedName}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.wim";
        var filePath = Path.Combine(_imageStorePath, fileName);

        _logger.LogInformation("Capturing image '{Name}' from client {Mac}, compression={Compression}",
            imageName, clientMac, compression);

        // The actual DISM capture runs on the client (WinPE agent).
        // This server-side method creates the database record and prepares the storage location.
        // The WinPE agent will upload the captured WIM to this path.

        var image = new Image
        {
            Name = imageName,
            FilePath = filePath,
            SourceClientMac = clientMac,
            Compression = compression,
            CapturedAt = DateTime.UtcNow,
            Type = ImageType.MachineCapture
        };

        _db.Images.Add(image);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Image record created: Id={Id}, Path={Path}", image.Id, filePath);
        return image;
    }

    public async Task ApplyImageAsync(int imageId, string clientMac, DeployMode deployMode, CancellationToken ct = default)
    {
        var image = await _db.Images.FindAsync([imageId], ct)
            ?? throw new InvalidOperationException($"Image {imageId} not found");

        if (!File.Exists(image.FilePath))
            throw new FileNotFoundException($"Image file not found: {image.FilePath}");

        _logger.LogInformation("Applying image '{Name}' (Id={Id}) to client {Mac}, mode={Mode}",
            image.Name, imageId, clientMac, deployMode);

        // The actual DISM apply runs on the client (WinPE agent).
        // Server prepares the image for transfer and generates the deployment instructions.
        // WinPE agent calls: DISM /Apply-Image /ImageFile:<path> /Index:1 /ApplyDir:C:\
    }

    public async Task<List<Image>> GetAllImagesAsync(CancellationToken ct = default)
    {
        return await _db.Images.OrderByDescending(i => i.CapturedAt).ToListAsync(ct);
    }

    public async Task<Image?> GetImageByIdAsync(int id, CancellationToken ct = default)
    {
        return await _db.Images.FindAsync([id], ct);
    }

    public async Task DeleteImageAsync(int id, CancellationToken ct = default)
    {
        var image = await _db.Images.FindAsync([id], ct)
            ?? throw new InvalidOperationException($"Image {id} not found");

        // Delete the physical WIM file
        if (File.Exists(image.FilePath))
        {
            File.Delete(image.FilePath);
            _logger.LogInformation("Deleted image file: {Path}", image.FilePath);
        }

        _db.Images.Remove(image);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Deleted image: Id={Id}, Name={Name}", id, image.Name);
    }

    public async Task<bool> VerifyImageIntegrityAsync(int id, CancellationToken ct = default)
    {
        var image = await _db.Images.FindAsync([id], ct);
        if (image == null || !File.Exists(image.FilePath)) return false;

        if (string.IsNullOrEmpty(image.FileHash))
        {
            // No stored hash — compute and store it
            image.FileHash = await ComputeFileHashAsync(image.FilePath, ct);
            await _db.SaveChangesAsync(ct);
            return true;
        }

        var currentHash = await ComputeFileHashAsync(image.FilePath, ct);
        return string.Equals(currentHash, image.FileHash, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<Image?> GetDefaultImageAsync(CancellationToken ct = default)
    {
        return await _db.Images.FirstOrDefaultAsync(i => i.IsDefault, ct);
    }

    public async Task<ImageStorageStats> GetStorageStatsAsync(CancellationToken ct = default)
    {
        var images = await _db.Images.ToListAsync(ct);
        var driveInfo = new DriveInfo(Path.GetPathRoot(_imageStorePath) ?? "C:");

        return new ImageStorageStats
        {
            TotalSizeBytes = images.Sum(i => i.SizeBytes),
            TotalImages = images.Count,
            AvailableSpaceBytes = driveInfo.AvailableFreeSpace,
            StoragePath = _imageStorePath
        };
    }

    /// <summary>
    /// Build DISM command-line arguments for image capture.
    /// Called by WinPE agent to get the correct command.
    /// </summary>
    public static string BuildCaptureCommand(string sourceDrive, string wimPath, string imageName, CompressionLevel compression)
    {
        var compressArg = compression switch
        {
            CompressionLevel.None => "none",
            CompressionLevel.Fast => "fast",
            CompressionLevel.Maximum => "max",
            _ => "fast"
        };

        return $"DISM /Capture-Image /ImageFile:\"{wimPath}\" /CaptureDir:{sourceDrive}\\ /Name:\"{imageName}\" /Compress:{compressArg}";
    }

    /// <summary>
    /// Build DISM command-line arguments for image apply.
    /// Called by WinPE agent to get the correct command.
    /// </summary>
    public static string BuildApplyCommand(string wimPath, string targetDrive, int imageIndex = 1)
    {
        return $"DISM /Apply-Image /ImageFile:\"{wimPath}\" /Index:{imageIndex} /ApplyDir:{targetDrive}\\";
    }

    private static async Task<string> ComputeFileHashAsync(string filePath, CancellationToken ct)
    {
        using var sha256 = SHA256.Create();
        await using var stream = File.OpenRead(filePath);
        var hash = await sha256.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash);
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Where(c => !invalid.Contains(c)).ToArray()).Replace(' ', '_');
    }
}
