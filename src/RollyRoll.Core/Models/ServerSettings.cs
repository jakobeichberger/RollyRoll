namespace RollyRoll.Core.Models;

/// <summary>
/// Persisted server setting with description and default value.
/// All settings are managed through the admin dashboard.
/// </summary>
public class ServerSetting
{
    public int Id { get; set; }

    /// <summary>Category for grouping in the UI (Network, Images, Deployment, etc.).</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Setting key, e.g. "Network.TftpPort", "Images.DefaultCompression".</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Display name in the UI.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Human-readable description of what this setting does.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Current value (stored as string, parsed by type).</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>Default value for "reset to default" functionality.</summary>
    public string DefaultValue { get; set; } = string.Empty;

    /// <summary>Data type for validation and UI rendering.</summary>
    public SettingValueType ValueType { get; set; }

    /// <summary>JSON validation rules (min, max, regex, allowed values, etc.).</summary>
    public string? ValidationRulesJson { get; set; }

    /// <summary>Display order within category.</summary>
    public int SortOrder { get; set; }
}

public enum SettingValueType
{
    String,
    Integer,
    Boolean,
    Path,
    Port,
    Enum,
    Percentage
}
