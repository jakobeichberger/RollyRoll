using RollyRoll.Core.Interfaces;

namespace RollyRoll.WindowsService.Workers;

/// <summary>
/// Background service that processes scheduled tasks.
/// Checks for due tasks every 30 seconds and dispatches them for execution.
/// Handles one-time and recurring (cron-based) schedules.
/// </summary>
public class SchedulerWorker : BackgroundService
{
    private readonly ILogger<SchedulerWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    public SchedulerWorker(
        ILogger<SchedulerWorker> logger,
        IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SchedulerWorker started. Checking for due tasks every {Interval}s", PollInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessDueTasksAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing scheduled tasks");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("SchedulerWorker stopped");
    }

    private async Task ProcessDueTasksAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var schedulerService = scope.ServiceProvider.GetRequiredService<ISchedulerService>();

        await schedulerService.ProcessDueTasksAsync(ct);
    }
}
