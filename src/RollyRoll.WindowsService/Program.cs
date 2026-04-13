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

                // PXE settings
                var pxeSection = configuration.GetSection("PxeSettings");
                var tftpPort = pxeSection.GetValue("TftpPort", 69);
                var tftpRoot = pxeSection.GetValue("TftpRoot", Path.Combine(AppContext.BaseDirectory, "TftpRoot"))!;
                var serverIp = configuration.GetSection("NetworkSettings").GetValue("ServerIp", "0.0.0.0")!;

                // Image settings
                var imageStorePath = configuration.GetSection("ImageSettings")
                    .GetValue("StorePath", Path.Combine(AppContext.BaseDirectory, "Images"))!;

                // WoL settings
                var wolSection = configuration.GetSection("WolSettings");
                var wolPort = wolSection.GetValue("Port", 9);
                var wolRetries = wolSection.GetValue("Retries", 3);

                // Ensure directories exist
                Directory.CreateDirectory(tftpRoot);
                Directory.CreateDirectory(imageStorePath);

                // Register infrastructure services
                services.AddSingleton(sp =>
                    new TftpServer(sp.GetRequiredService<ILogger<TftpServer>>(), tftpRoot, tftpPort));

                services.AddSingleton(sp =>
                    new DhcpProxyService(
                        sp.GetRequiredService<ILogger<DhcpProxyService>>(),
                        sp.GetRequiredService<IClientDiscoveryService>(),
                        serverIp));

                services.AddScoped<IImageService>(sp =>
                    new DismImageService(
                        sp.GetRequiredService<ILogger<DismImageService>>(),
                        sp.GetRequiredService<RollyRollDbContext>(),
                        imageStorePath));

                services.AddScoped<IWakeOnLanService>(sp =>
                    new WakeOnLanService(
                        sp.GetRequiredService<ILogger<WakeOnLanService>>(),
                        sp.GetRequiredService<RollyRollDbContext>(),
                        wolPort,
                        wolRetries));

                services.AddSingleton(sp =>
                    new BootMenuGenerator(
                        sp.GetRequiredService<ILogger<BootMenuGenerator>>(),
                        sp.GetRequiredService<IDeploymentService>(),
                        sp.GetRequiredService<IClientDiscoveryService>(),
                        configuration.GetSection("NetworkSettings").GetValue("ServerBaseUrl", "http://localhost:5000")!));

                // Background workers
                services.AddHostedService<PxeBootWorker>();
                services.AddHostedService<DeploymentWorker>();
                services.AddHostedService<PatchWorker>();
                services.AddHostedService<SchedulerWorker>();
            })
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseStartup<WebStartup>();
            });

        var host = builder.Build();

        // Ensure database is created and migrations are applied
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RollyRollDbContext>();
            await db.Database.EnsureCreatedAsync();

            var settings = scope.ServiceProvider.GetService<ISettingsService>();
            if (settings != null)
            {
                await settings.InitializeDefaultSettingsAsync();
            }
        }

        await host.RunAsync();
    }
}

/// <summary>
/// Minimal startup class for the Blazor web server hosted within the Windows Service.
/// </summary>
public class WebStartup
{
    private readonly IConfiguration _configuration;

    public WebStartup(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddRazorPages();
        services.AddServerSideBlazor();
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        if (env.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }
        else
        {
            app.UseExceptionHandler("/Error");
            app.UseHsts();
        }

        app.UseHttpsRedirection();
        app.UseStaticFiles();
        app.UseRouting();

        app.UseEndpoints(endpoints =>
        {
            endpoints.MapBlazorHub();
            endpoints.MapFallbackToPage("/_Host");
        });
    }
}
