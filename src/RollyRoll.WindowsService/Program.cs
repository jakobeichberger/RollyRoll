using Microsoft.EntityFrameworkCore;
using RollyRoll.Core.Interfaces;
using RollyRoll.Infrastructure.Data;
using RollyRoll.Infrastructure.Services;
using RollyRoll.Infrastructure.PXE;
using RollyRoll.WindowsService.Workers;

namespace RollyRoll.WindowsService;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateDefaultBuilder(args)
            .UseWindowsService(options =>
            {
                options.ServiceName = "RollyRoll Deployment Server";
            })
            .ConfigureAppConfiguration((context, config) =>
            {
                config.SetBasePath(AppContext.BaseDirectory);
                config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
                config.AddJsonFile($"appsettings.{context.HostingEnvironment.EnvironmentName}.json", optional: true, reloadOnChange: true);
                config.AddEnvironmentVariables("ROLLYROLL_");
                config.AddCommandLine(args);
            })
            .ConfigureServices((context, services) =>
            {
                var configuration = context.Configuration;

                // SQLite database
                var connectionString = configuration.GetConnectionString("DefaultConnection")
                    ?? "Data Source=RollyRoll.db";
                services.AddDbContext<RollyRollDbContext>(options =>
                    options.UseSqlite(connectionString));

                // Configuration values
                var pxeSection = configuration.GetSection("PxeSettings");
                var tftpPort = pxeSection.GetValue("TftpPort", 69);
                var tftpRoot = pxeSection.GetValue("TftpRoot", Path.Combine(AppContext.BaseDirectory, "TftpRoot"))!;
                var serverIp = configuration.GetSection("NetworkSettings").GetValue("ServerIp", "0.0.0.0")!;
                var serverBaseUrl = configuration.GetSection("NetworkSettings").GetValue("ServerBaseUrl", "http://localhost:5000")!;

                var imageStorePath = configuration.GetSection("ImageSettings")
                    .GetValue("StorePath", Path.Combine(AppContext.BaseDirectory, "Images"))!;

                var wolSection = configuration.GetSection("WolSettings");
                var wolPort = wolSection.GetValue("Port", 9);
                var wolRetries = wolSection.GetValue("Retries", 3);

                // Ensure directories exist
                Directory.CreateDirectory(tftpRoot);
                Directory.CreateDirectory(imageStorePath);

                // ---------- Scoped services (depend on DbContext) ----------
                services.AddScoped<IClientDiscoveryService, ClientDiscoveryService>();
                services.AddScoped<ISettingsService, SettingsService>();

                services.AddScoped<IWakeOnLanService>(sp =>
                    new WakeOnLanService(
                        sp.GetRequiredService<ILogger<WakeOnLanService>>(),
                        sp.GetRequiredService<RollyRollDbContext>(),
                        wolPort,
                        wolRetries));

                services.AddScoped<IImageService>(sp =>
                    new DismImageService(
                        sp.GetRequiredService<ILogger<DismImageService>>(),
                        sp.GetRequiredService<RollyRollDbContext>(),
                        imageStorePath));

                services.AddScoped<IDeploymentService, DeploymentEngine>();
                services.AddScoped<IRecoveryService, RecoveryService>();

                // ---------- Singleton services (no DbContext dependency) ----------
                // TftpServer and DhcpProxyService are singletons that use IServiceScopeFactory
                services.AddSingleton(sp =>
                    new TftpServer(sp.GetRequiredService<ILogger<TftpServer>>(), tftpRoot, tftpPort));

                services.AddSingleton<DhcpProxyService>(sp =>
                    new DhcpProxyService(
                        sp.GetRequiredService<ILogger<DhcpProxyService>>(),
                        sp.GetRequiredService<IServiceScopeFactory>(),
                        serverIp));

                services.AddSingleton<BootMenuGenerator>(sp =>
                    new BootMenuGenerator(
                        sp.GetRequiredService<ILogger<BootMenuGenerator>>(),
                        sp.GetRequiredService<IServiceScopeFactory>(),
                        serverBaseUrl));

                services.AddSingleton<IAutoDiscoveryService, AutoDiscoveryService>();

                // ---------- Background workers ----------
                services.AddHostedService<PxeBootWorker>();
                services.AddHostedService<DeploymentWorker>();
                // PatchWorker and SchedulerWorker depend on IPatchService/ISchedulerService
                // which are not yet implemented — uncomment when ready:
                // services.AddHostedService<PatchWorker>();
                // services.AddHostedService<SchedulerWorker>();
            });

        var host = builder.Build();

        // Ensure database is created and default settings are initialized
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RollyRollDbContext>();
            await db.Database.EnsureCreatedAsync();

            var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
            await settings.InitializeDefaultSettingsAsync();
        }

        await host.RunAsync();
    }
}
