using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RollyRoll.ClientAgent;

var builder = Host.CreateApplicationBuilder(args);

// Configure as Windows Service
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "RollyRoll Client Agent";
});

// Configure logging
builder.Logging.AddEventLog(settings =>
{
    settings.SourceName = "RollyRoll.ClientAgent";
});

// Register services
builder.Services.AddSingleton<ServerLocator>();
builder.Services.AddHostedService<HealthReporter>();
builder.Services.AddHostedService<PatchInstaller>();
builder.Services.AddHostedService<RecoveryMonitor>();

var host = builder.Build();
await host.RunAsync();
