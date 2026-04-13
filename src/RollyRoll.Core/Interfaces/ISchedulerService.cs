using RollyRoll.Core.Models;

namespace RollyRoll.Core.Interfaces;

/// <summary>
/// Manages timetabled tasks: one-time scheduled deployments and recurring cron jobs.
/// </summary>
public interface ISchedulerService
{
    /// <summary>Schedule a one-time task at a specific date/time.</summary>
    Task<ScheduledTask> ScheduleTaskAsync(ScheduledTask task, CancellationToken ct = default);

    /// <summary>Schedule a recurring task with a cron expression.</summary>
    Task<ScheduledTask> ScheduleRecurringTaskAsync(ScheduledTask task, string cronExpression, CancellationToken ct = default);

    /// <summary>Cancel a scheduled task.</summary>
    Task CancelTaskAsync(int taskId, CancellationToken ct = default);

    /// <summary>Get all scheduled (future) tasks.</summary>
    Task<List<ScheduledTask>> GetScheduledTasksAsync(CancellationToken ct = default);

    /// <summary>Get tasks scheduled within a date range (for calendar view).</summary>
    Task<List<ScheduledTask>> GetTasksInRangeAsync(DateTime start, DateTime end, CancellationToken ct = default);

    /// <summary>Process due tasks (called by the scheduler worker).</summary>
    Task ProcessDueTasksAsync(CancellationToken ct = default);
}
