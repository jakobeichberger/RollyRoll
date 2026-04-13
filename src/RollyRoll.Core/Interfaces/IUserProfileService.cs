namespace RollyRoll.Core.Interfaces;

/// <summary>
/// Captures and restores user profiles via USMT (User State Migration Tool).
/// Used during backup/restore and auto-recovery to preserve the user's desktop state.
/// </summary>
public interface IUserProfileService
{
    /// <summary>
    /// Capture all user profiles from a client and store on the server.
    /// Uses USMT ScanState under the hood.
    /// </summary>
    /// <returns>Path to the encrypted profile export on the server.</returns>
    Task<string> CaptureProfilesAsync(string clientMac, CancellationToken ct = default);

    /// <summary>
    /// Restore previously captured user profiles to a client.
    /// Uses USMT LoadState under the hood.
    /// </summary>
    Task RestoreProfilesAsync(string clientMac, string profilesPath, CancellationToken ct = default);

    /// <summary>Delete a stored profile export from the server.</summary>
    Task DeleteProfilesAsync(string profilesPath, CancellationToken ct = default);

    /// <summary>Get the size of a stored profile export.</summary>
    Task<long> GetProfilesSizeAsync(string profilesPath, CancellationToken ct = default);
}
