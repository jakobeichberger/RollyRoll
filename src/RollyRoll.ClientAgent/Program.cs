namespace RollyRoll.ClientAgent;

/// <summary>
/// Entry point for the RollyRoll Client Agent.
/// Runs as a Windows Service on managed PCs, providing health reporting, patch installation,
/// auto-recovery monitoring, and profile capture capabilities.
/// </summary>
public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateDefaultBuilder(args)
            .UseWindowsService(options =>
            {
                options.ServiceName = "RollyRoll Client Agent";
            })
            .ConfigureAppConfiguration((context, config) =>
            {
                config.SetBasePath(AppContext.BaseDirectory);
                config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
                config.AddEnvironmentVariables("ROLLYROLL_");
                config.AddCommandLine(args);
            })
            .ConfigureServices((context, services) =>
            {
                // Register the server locator as a singleton (caches discovered server address)
                services.AddSingleton<ServerLocator>();

                // Register an HttpClient configured to talk to the RollyRoll server
                services.AddHttpClient("RollyRollServer", (sp, client) =>
                {
                    var locator = sp.GetRequiredService<ServerLocator>();
                    var serverUrl = locator.GetCachedServerUrl();
                    if (!string.IsNullOrEmpty(serverUrl))
                    {
                        client.BaseAddress = new Uri(serverUrl);
                    }
                    client.Timeout = TimeSpan.FromMinutes(10);
                })
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (_, _, _, _) => true
                });

                // Register profile capturer for use by RecoveryMonitor
                services.AddSingleton<ProfileCapturer>();

                // Register background workers
                services.AddHostedService<HealthReporter>();
                services.AddHostedService<PatchInstaller>();
                services.AddHostedService<RecoveryMonitor>();
            });

        var host = builder.Build();

        // Discover server on startup
        var logger = host.Services.GetRequiredService<ILogger<Program>>();
        var locator = host.Services.GetRequiredService<ServerLocator>();

        try
        {
            var serverUrl = await locator.DiscoverAsync();
            logger.LogInformation("RollyRoll server discovered at: {ServerUrl}", serverUrl);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to discover server on startup. Will retry during operation");
        }

        await host.RunAsync();
    }
}
