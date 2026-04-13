using RollyRoll.Core.Models;

namespace RollyRoll.Core.Interfaces;

/// <summary>
/// Manages all server settings. Every setting has a description, default value, and validation rules.
/// All settings are configurable through the admin dashboard.
/// </summary>
public interface ISettingsService
{
    /// <summary>Get all settings grouped by category.</summary>
    Task<Dictionary<string, List<ServerSetting>>> GetAllSettingsGroupedAsync(CancellationToken ct = default);

    /// <summary>Get a single setting by key.</summary>
    Task<ServerSetting?> GetSettingAsync(string key, CancellationToken ct = default);

    /// <summary>Get a setting value with type conversion.</summary>
    Task<T> GetValueAsync<T>(string key, CancellationToken ct = default);

    /// <summary>Update a setting value (validates before saving).</summary>
    Task UpdateSettingAsync(string key, string value, CancellationToken ct = default);

    /// <summary>Reset a setting to its default value.</summary>
    Task ResetToDefaultAsync(string key, CancellationToken ct = default);

    /// <summary>Reset all settings in a category to defaults.</summary>
    Task ResetCategoryToDefaultsAsync(string category, CancellationToken ct = default);

    /// <summary>Initialize default settings on first run.</summary>
    Task InitializeDefaultSettingsAsync(CancellationToken ct = default);

    /// <summary>Export all settings as JSON (for backup).</summary>
    Task<string> ExportSettingsAsync(CancellationToken ct = default);

    /// <summary>Import settings from JSON (for restore).</summary>
    Task ImportSettingsAsync(string json, CancellationToken ct = default);
}
