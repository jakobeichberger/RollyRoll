using System.ComponentModel;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RollyRoll.Core.Interfaces;
using RollyRoll.Core.Models;
using RollyRoll.Infrastructure.Data;

namespace RollyRoll.Infrastructure.Services;

/// <summary>
/// Manages all server settings with categories, descriptions, defaults, and validation.
/// Settings are stored in the database and exposed through the admin dashboard.
/// Supports export/import as JSON for backup and migration.
/// </summary>
public class SettingsService : ISettingsService
{
    private readonly ILogger<SettingsService> _logger;
    private readonly RollyRollDbContext _db;

    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsService"/> class.
    /// </summary>
    /// <param name="logger">Logger for settings operations.</param>
    /// <param name="db">Database context for persisting settings.</param>
    public SettingsService(ILogger<SettingsService> logger, RollyRollDbContext db)
    {
        _logger = logger;
        _db = db;
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, List<ServerSetting>>> GetAllSettingsGroupedAsync(
        CancellationToken ct = default)
    {
        var settings = await _db.ServerSettings
            .OrderBy(s => s.Category)
            .ThenBy(s => s.SortOrder)
            .ToListAsync(ct);

        return settings
            .GroupBy(s => s.Category)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    /// <inheritdoc />
    public async Task<ServerSetting?> GetSettingAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return await _db.ServerSettings
            .FirstOrDefaultAsync(s => s.Key == key, ct);
    }

    /// <inheritdoc />
    public async Task<T> GetValueAsync<T>(string key, CancellationToken ct = default)
    {
        var setting = await GetSettingAsync(key, ct)
            ?? throw new InvalidOperationException($"Setting '{key}' not found.");

        try
        {
            var converter = TypeDescriptor.GetConverter(typeof(T));
            if (converter.CanConvertFrom(typeof(string)))
            {
                var result = converter.ConvertFromInvariantString(setting.Value);
                return result is T typed
                    ? typed
                    : throw new InvalidOperationException(
                        $"Setting '{key}' value '{setting.Value}' could not be converted to {typeof(T).Name}.");
            }

            throw new InvalidOperationException(
                $"No converter available for type {typeof(T).Name}.");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Failed to convert setting '{key}' value '{setting.Value}' to {typeof(T).Name}.", ex);
        }
    }

    /// <inheritdoc />
    public async Task UpdateSettingAsync(string key, string value, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var setting = await _db.ServerSettings
            .FirstOrDefaultAsync(s => s.Key == key, ct)
            ?? throw new InvalidOperationException($"Setting '{key}' not found.");

        ValidateSettingValue(setting, value);

        var previousValue = setting.Value;
        setting.Value = value;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Setting '{Key}' updated: '{PreviousValue}' -> '{NewValue}'",
            key, previousValue, value);
    }

    /// <inheritdoc />
    public async Task ResetToDefaultAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var setting = await _db.ServerSettings
            .FirstOrDefaultAsync(s => s.Key == key, ct)
            ?? throw new InvalidOperationException($"Setting '{key}' not found.");

        var previousValue = setting.Value;
        setting.Value = setting.DefaultValue;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Setting '{Key}' reset to default: '{PreviousValue}' -> '{DefaultValue}'",
            key, previousValue, setting.DefaultValue);
    }

    /// <inheritdoc />
    public async Task ResetCategoryToDefaultsAsync(string category, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);

        var settings = await _db.ServerSettings
            .Where(s => s.Category == category)
            .ToListAsync(ct);

        if (settings.Count == 0)
            throw new InvalidOperationException($"No settings found for category '{category}'.");

        foreach (var setting in settings)
        {
            setting.Value = setting.DefaultValue;
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "All settings in category '{Category}' reset to defaults ({Count} settings).",
            category, settings.Count);
    }

    /// <inheritdoc />
    public async Task InitializeDefaultSettingsAsync(CancellationToken ct = default)
    {
        var existingKeys = await _db.ServerSettings
            .Select(s => s.Key)
            .ToHashSetAsync(ct);

        var defaults = GetDefaultSettings();
        var newSettings = defaults.Where(s => !existingKeys.Contains(s.Key)).ToList();

        if (newSettings.Count == 0)
        {
            _logger.LogDebug("All default settings already exist. No initialization needed.");
            return;
        }

        _db.ServerSettings.AddRange(newSettings);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Initialized {Count} default settings ({Existing} already existed).",
            newSettings.Count, existingKeys.Count);
    }

    /// <inheritdoc />
    public async Task<string> ExportSettingsAsync(CancellationToken ct = default)
    {
        var settings = await _db.ServerSettings
            .OrderBy(s => s.Category)
            .ThenBy(s => s.SortOrder)
            .ToListAsync(ct);

        var exportData = settings.Select(s => new
        {
            s.Key,
            s.Category,
            s.DisplayName,
            s.Description,
            s.Value,
            s.DefaultValue,
            ValueType = s.ValueType.ToString(),
            s.ValidationRulesJson,
            s.SortOrder
        });

        var json = JsonSerializer.Serialize(exportData, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        _logger.LogInformation("Exported {Count} settings to JSON.", settings.Count);
        return json;
    }

    /// <inheritdoc />
    public async Task ImportSettingsAsync(string json, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        var importData = JsonSerializer.Deserialize<List<SettingsImportEntry>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("Failed to deserialize settings JSON.");

        var existingSettings = await _db.ServerSettings.ToDictionaryAsync(s => s.Key, ct);
        var updatedCount = 0;
        var createdCount = 0;

        foreach (var entry in importData)
        {
            if (string.IsNullOrWhiteSpace(entry.Key))
                continue;

            if (existingSettings.TryGetValue(entry.Key, out var existing))
            {
                existing.Value = entry.Value ?? existing.DefaultValue;
                updatedCount++;
            }
            else
            {
                var setting = new ServerSetting
                {
                    Key = entry.Key,
                    Category = entry.Category ?? "Imported",
                    DisplayName = entry.DisplayName ?? entry.Key,
                    Description = entry.Description ?? string.Empty,
                    Value = entry.Value ?? entry.DefaultValue ?? string.Empty,
                    DefaultValue = entry.DefaultValue ?? string.Empty,
                    ValueType = Enum.TryParse<SettingValueType>(entry.ValueType, out var vt) ? vt : SettingValueType.String,
                    ValidationRulesJson = entry.ValidationRulesJson,
                    SortOrder = entry.SortOrder
                };

                _db.ServerSettings.Add(setting);
                createdCount++;
            }
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Imported settings: {Updated} updated, {Created} created.",
            updatedCount, createdCount);
    }

    /// <summary>
    /// Validates a setting value against its type and any configured validation rules.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the value fails validation.</exception>
    private static void ValidateSettingValue(ServerSetting setting, string value)
    {
        switch (setting.ValueType)
        {
            case SettingValueType.Integer:
                if (!int.TryParse(value, out _))
                    throw new ArgumentException($"Value '{value}' is not a valid integer for setting '{setting.Key}'.");
                break;

            case SettingValueType.Boolean:
                if (!bool.TryParse(value, out _))
                    throw new ArgumentException($"Value '{value}' is not a valid boolean for setting '{setting.Key}'.");
                break;

            case SettingValueType.Port:
                if (!int.TryParse(value, out var port) || port < 1 || port > 65535)
                    throw new ArgumentException($"Value '{value}' is not a valid port number (1-65535) for setting '{setting.Key}'.");
                break;

            case SettingValueType.Percentage:
                if (!int.TryParse(value, out var pct) || pct < 0 || pct > 100)
                    throw new ArgumentException($"Value '{value}' is not a valid percentage (0-100) for setting '{setting.Key}'.");
                break;

            case SettingValueType.Path:
                if (string.IsNullOrWhiteSpace(value))
                    throw new ArgumentException($"Path value cannot be empty for setting '{setting.Key}'.");
                if (value.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                    throw new ArgumentException($"Value '{value}' contains invalid path characters for setting '{setting.Key}'.");
                break;

            case SettingValueType.Enum:
                if (string.IsNullOrWhiteSpace(value))
                    throw new ArgumentException($"Enum value cannot be empty for setting '{setting.Key}'.");
                ValidateEnumAgainstRules(setting, value);
                break;

            case SettingValueType.String:
                // No specific validation for free-form strings
                break;
        }

        // Apply JSON validation rules if present
        if (!string.IsNullOrEmpty(setting.ValidationRulesJson) && setting.ValueType is SettingValueType.Integer)
        {
            ValidateNumericRange(setting, value);
        }
    }

    /// <summary>
    /// Validates an integer value against min/max constraints defined in ValidationRulesJson.
    /// </summary>
    private static void ValidateNumericRange(ServerSetting setting, string value)
    {
        if (!int.TryParse(value, out var numericValue))
            return;

        try
        {
            using var doc = JsonDocument.Parse(setting.ValidationRulesJson!);
            var root = doc.RootElement;

            if (root.TryGetProperty("min", out var minProp) && minProp.TryGetInt32(out var min) && numericValue < min)
                throw new ArgumentException($"Value {numericValue} is below minimum {min} for setting '{setting.Key}'.");

            if (root.TryGetProperty("max", out var maxProp) && maxProp.TryGetInt32(out var max) && numericValue > max)
                throw new ArgumentException($"Value {numericValue} exceeds maximum {max} for setting '{setting.Key}'.");
        }
        catch (JsonException)
        {
            // Malformed validation rules; skip range check
        }
    }

    /// <summary>
    /// Validates an enum value against the allowed values defined in ValidationRulesJson.
    /// </summary>
    private static void ValidateEnumAgainstRules(ServerSetting setting, string value)
    {
        if (string.IsNullOrEmpty(setting.ValidationRulesJson))
            return;

        try
        {
            using var doc = JsonDocument.Parse(setting.ValidationRulesJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("allowedValues", out var allowedProp) && allowedProp.ValueKind == JsonValueKind.Array)
            {
                var allowed = allowedProp.EnumerateArray()
                    .Select(v => v.GetString())
                    .Where(v => v is not null)
                    .ToList();

                if (allowed.Count > 0 && !allowed.Contains(value, StringComparer.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        $"Value '{value}' is not one of the allowed values [{string.Join(", ", allowed)}] for setting '{setting.Key}'.");
                }
            }
        }
        catch (JsonException)
        {
            // Malformed validation rules; skip enum check
        }
    }

    /// <summary>
    /// Returns the complete set of default server settings across all categories.
    /// </summary>
    private static List<ServerSetting> GetDefaultSettings()
    {
        var settings = new List<ServerSetting>();
        var order = 0;

        // --- Network ---
        settings.AddRange(
        [
            new ServerSetting
            {
                Category = "Network", Key = "Network.TftpPort", DisplayName = "TFTP Port",
                Description = "Port for the TFTP server used to serve PXE boot files.",
                Value = "69", DefaultValue = "69", ValueType = SettingValueType.Port, SortOrder = order++
            },
            new ServerSetting
            {
                Category = "Network", Key = "Network.HttpPort", DisplayName = "HTTP Port",
                Description = "Port for the RollyRoll web dashboard and API.",
                Value = "5000", DefaultValue = "5000", ValueType = SettingValueType.Port, SortOrder = order++
            },
            new ServerSetting
            {
                Category = "Network", Key = "Network.MulticastEnabled", DisplayName = "Enable Multicast",
                Description = "Enable multicast image transfer for group deployments (faster for many clients).",
                Value = "false", DefaultValue = "false", ValueType = SettingValueType.Boolean, SortOrder = order++
            },
            new ServerSetting
            {
                Category = "Network", Key = "Network.MulticastPort", DisplayName = "Multicast Port",
                Description = "Port for multicast image streaming.",
                Value = "5001", DefaultValue = "5001", ValueType = SettingValueType.Port, SortOrder = order++
            },
            new ServerSetting
            {
                Category = "Network", Key = "Network.BindAddress", DisplayName = "Bind Address",
                Description = "Network interface IP to bind services to. Use 0.0.0.0 for all interfaces.",
                Value = "0.0.0.0", DefaultValue = "0.0.0.0", ValueType = SettingValueType.String, SortOrder = order++
            },
        ]);

        // --- Images ---
        order = 0;
        settings.AddRange(
        [
            new ServerSetting
            {
                Category = "Images", Key = "Images.StoragePath", DisplayName = "Image Storage Path",
                Description = "Directory where WIM image files are stored on the server.",
                Value = @"C:\RollyRoll\Images", DefaultValue = @"C:\RollyRoll\Images",
                ValueType = SettingValueType.Path, SortOrder = order++
            },
            new ServerSetting
            {
                Category = "Images", Key = "Images.DefaultCompression", DisplayName = "Default Compression",
                Description = "Default compression level for new image captures.",
                Value = "Fast", DefaultValue = "Fast", ValueType = SettingValueType.Enum,
                ValidationRulesJson = """{"allowedValues":["None","Fast","Maximum"]}""",
                SortOrder = order++
            },
            new ServerSetting
            {
                Category = "Images", Key = "Images.VerifyAfterCapture", DisplayName = "Verify After Capture",
                Description = "Automatically verify image integrity (SHA-256) after capture completes.",
                Value = "true", DefaultValue = "true", ValueType = SettingValueType.Boolean, SortOrder = order++
            },
            new ServerSetting
            {
                Category = "Images", Key = "Images.MaxConcurrentTransfers", DisplayName = "Max Concurrent Transfers",
                Description = "Maximum number of simultaneous image transfers to clients.",
                Value = "5", DefaultValue = "5", ValueType = SettingValueType.Integer,
                ValidationRulesJson = """{"min":1,"max":50}""",
                SortOrder = order++
            },
        ]);

        // --- Deployment ---
        order = 0;
        settings.AddRange(
        [
            new ServerSetting
            {
                Category = "Deployment", Key = "Deployment.DefaultTimeoutMinutes", DisplayName = "Default Timeout (Minutes)",
                Description = "Default timeout in minutes before a deployment task is marked as failed.",
                Value = "120", DefaultValue = "120", ValueType = SettingValueType.Integer,
                ValidationRulesJson = """{"min":10,"max":1440}""",
                SortOrder = order++
            },
            new ServerSetting
            {
                Category = "Deployment", Key = "Deployment.AutoRebootAfterDeploy", DisplayName = "Auto-Reboot After Deploy",
                Description = "Automatically reboot the client after a successful deployment.",
                Value = "true", DefaultValue = "true", ValueType = SettingValueType.Boolean, SortOrder = order++
            },
            new ServerSetting
            {
                Category = "Deployment", Key = "Deployment.MaxConcurrentDeployments", DisplayName = "Max Concurrent Deployments",
                Description = "Maximum number of deployments that can run simultaneously.",
                Value = "10", DefaultValue = "10", ValueType = SettingValueType.Integer,
                ValidationRulesJson = """{"min":1,"max":100}""",
                SortOrder = order++
            },
            new ServerSetting
            {
                Category = "Deployment", Key = "Deployment.DefaultDeployMode", DisplayName = "Default Deploy Mode",
                Description = "Default deployment mode for new deployment tasks.",
                Value = "CleanDeploy", DefaultValue = "CleanDeploy", ValueType = SettingValueType.Enum,
                ValidationRulesJson = """{"allowedValues":["CleanDeploy","RestoreWithProfiles"]}""",
                SortOrder = order++
            },
        ]);

        // --- Patches ---
        order = 0;
        settings.AddRange(
        [
            new ServerSetting
            {
                Category = "Patches", Key = "Patches.AutoApproveEnabled", DisplayName = "Auto-Approve Patches",
                Description = "Automatically approve patches that pass ring-based testing.",
                Value = "false", DefaultValue = "false", ValueType = SettingValueType.Boolean, SortOrder = order++
            },
            new ServerSetting
            {
                Category = "Patches", Key = "Patches.FailureThresholdPercent", DisplayName = "Failure Threshold (%)",
                Description = "Percentage of failures in a ring that triggers automatic rollout pause.",
                Value = "10", DefaultValue = "10", ValueType = SettingValueType.Percentage, SortOrder = order++
            },
            new ServerSetting
            {
                Category = "Patches", Key = "Patches.StoragePath", DisplayName = "Patch Storage Path",
                Description = "Directory where downloaded patch packages are stored.",
                Value = @"C:\RollyRoll\Patches", DefaultValue = @"C:\RollyRoll\Patches",
                ValueType = SettingValueType.Path, SortOrder = order++
            },
        ]);

        // --- Recovery ---
        order = 0;
        settings.AddRange(
        [
            new ServerSetting
            {
                Category = "Recovery", Key = "Recovery.SnapshotRetentionDays", DisplayName = "Snapshot Retention (Days)",
                Description = "Number of days to retain recovery snapshots before automatic cleanup.",
                Value = "30", DefaultValue = "30", ValueType = SettingValueType.Integer,
                ValidationRulesJson = """{"min":1,"max":365}""",
                SortOrder = order++
            },
            new ServerSetting
            {
                Category = "Recovery", Key = "Recovery.MaxSnapshotsPerClient", DisplayName = "Max Snapshots Per Client",
                Description = "Maximum number of recovery snapshots to retain per client.",
                Value = "10", DefaultValue = "10", ValueType = SettingValueType.Integer,
                ValidationRulesJson = """{"min":1,"max":50}""",
                SortOrder = order++
            },
            new ServerSetting
            {
                Category = "Recovery", Key = "Recovery.AutoRecoveryEnabled", DisplayName = "Auto-Recovery Enabled",
                Description = "Automatically initiate recovery when a deployment fails.",
                Value = "true", DefaultValue = "true", ValueType = SettingValueType.Boolean, SortOrder = order++
            },
            new ServerSetting
            {
                Category = "Recovery", Key = "Recovery.ProfileStoragePath", DisplayName = "Profile Storage Path",
                Description = "Directory where captured user profiles (USMT exports) are stored.",
                Value = @"C:\RollyRoll\Profiles", DefaultValue = @"C:\RollyRoll\Profiles",
                ValueType = SettingValueType.Path, SortOrder = order++
            },
        ]);

        // --- Security ---
        order = 0;
        settings.AddRange(
        [
            new ServerSetting
            {
                Category = "Security", Key = "Security.EncryptProfilesAtRest", DisplayName = "Encrypt Profiles At Rest",
                Description = "Encrypt captured user profile data using AES-256-GCM.",
                Value = "true", DefaultValue = "true", ValueType = SettingValueType.Boolean, SortOrder = order++
            },
            new ServerSetting
            {
                Category = "Security", Key = "Security.RequireClientApproval", DisplayName = "Require Client Approval",
                Description = "Require admin approval before auto-discovered clients can receive deployments.",
                Value = "true", DefaultValue = "true", ValueType = SettingValueType.Boolean, SortOrder = order++
            },
            new ServerSetting
            {
                Category = "Security", Key = "Security.AuditRetentionDays", DisplayName = "Audit Log Retention (Days)",
                Description = "Number of days to retain audit log entries.",
                Value = "90", DefaultValue = "90", ValueType = SettingValueType.Integer,
                ValidationRulesJson = """{"min":30,"max":3650}""",
                SortOrder = order++
            },
        ]);

        // --- Performance ---
        order = 0;
        settings.AddRange(
        [
            new ServerSetting
            {
                Category = "Performance", Key = "Performance.TransferBufferSizeKB", DisplayName = "Transfer Buffer Size (KB)",
                Description = "Buffer size in KB for image file transfers to clients.",
                Value = "512", DefaultValue = "512", ValueType = SettingValueType.Integer,
                ValidationRulesJson = """{"min":64,"max":8192}""",
                SortOrder = order++
            },
            new ServerSetting
            {
                Category = "Performance", Key = "Performance.DatabaseConnectionPoolSize", DisplayName = "DB Connection Pool Size",
                Description = "Maximum number of database connections in the connection pool.",
                Value = "20", DefaultValue = "20", ValueType = SettingValueType.Integer,
                ValidationRulesJson = """{"min":5,"max":100}""",
                SortOrder = order++
            },
            new ServerSetting
            {
                Category = "Performance", Key = "Performance.EnableResponseCompression", DisplayName = "Enable Response Compression",
                Description = "Enable gzip/brotli compression for API responses.",
                Value = "true", DefaultValue = "true", ValueType = SettingValueType.Boolean, SortOrder = order++
            },
        ]);

        // --- WoL (Wake-on-LAN) ---
        order = 0;
        settings.AddRange(
        [
            new ServerSetting
            {
                Category = "WoL", Key = "WoL.Port", DisplayName = "WoL Port",
                Description = "UDP port for sending Wake-on-LAN magic packets.",
                Value = "9", DefaultValue = "9", ValueType = SettingValueType.Port, SortOrder = order++
            },
            new ServerSetting
            {
                Category = "WoL", Key = "WoL.RetryCount", DisplayName = "Retry Count",
                Description = "Number of times to retry sending a WoL packet.",
                Value = "3", DefaultValue = "3", ValueType = SettingValueType.Integer,
                ValidationRulesJson = """{"min":0,"max":10}""",
                SortOrder = order++
            },
            new ServerSetting
            {
                Category = "WoL", Key = "WoL.RetryDelayMs", DisplayName = "Retry Delay (ms)",
                Description = "Delay in milliseconds between WoL packet retry attempts.",
                Value = "500", DefaultValue = "500", ValueType = SettingValueType.Integer,
                ValidationRulesJson = """{"min":100,"max":5000}""",
                SortOrder = order++
            },
            new ServerSetting
            {
                Category = "WoL", Key = "WoL.SendBeforeDeploy", DisplayName = "Send WoL Before Deploy",
                Description = "Automatically send a WoL packet before starting a deployment.",
                Value = "true", DefaultValue = "true", ValueType = SettingValueType.Boolean, SortOrder = order++
            },
        ]);

        return settings;
    }

    /// <summary>
    /// DTO for deserializing settings during JSON import.
    /// </summary>
    private sealed class SettingsImportEntry
    {
        public string Key { get; set; } = string.Empty;
        public string? Category { get; set; }
        public string? DisplayName { get; set; }
        public string? Description { get; set; }
        public string? Value { get; set; }
        public string? DefaultValue { get; set; }
        public string? ValueType { get; set; }
        public string? ValidationRulesJson { get; set; }
        public int SortOrder { get; set; }
    }
}
