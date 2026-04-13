using RollyRoll.Core.Models;

namespace RollyRoll.Core.Interfaces;

/// <summary>
/// Records audit trail entries for all significant actions.
/// </summary>
public interface IAuditService
{
    /// <summary>Log an audit entry.</summary>
    Task LogAsync(string action, AuditCategory category, string performedBy, string? targetName = null, int? targetId = null, string? details = null, bool success = true, string? errorMessage = null, string? sourceIp = null, CancellationToken ct = default);

    /// <summary>Get audit log entries with optional filtering.</summary>
    Task<List<AuditLogEntry>> GetEntriesAsync(AuditCategory? category = null, DateTime? from = null, DateTime? to = null, string? performedBy = null, int skip = 0, int take = 100, CancellationToken ct = default);

    /// <summary>Get total count of entries matching a filter.</summary>
    Task<int> GetCountAsync(AuditCategory? category = null, DateTime? from = null, DateTime? to = null, string? performedBy = null, CancellationToken ct = default);

    /// <summary>Export audit log as CSV.</summary>
    Task<string> ExportCsvAsync(AuditCategory? category = null, DateTime? from = null, DateTime? to = null, CancellationToken ct = default);

    /// <summary>Delete entries older than retention period.</summary>
    Task CleanupOldEntriesAsync(int retentionDays, CancellationToken ct = default);
}
