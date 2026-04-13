using Microsoft.EntityFrameworkCore;
using RollyRoll.Core.Interfaces;
using RollyRoll.Infrastructure.Data;
using RollyRoll.Infrastructure.PXE;
using RollyRoll.Infrastructure.Services;
using RollyRoll.Web.Hubs;

var builder = WebApplication.CreateBuilder(args);

// ---------- EF Core with SQLite ----------
builder.Services.AddDbContext<RollyRollDbContext>(options =>
    options.UseSqlite(
        builder.Configuration.GetConnectionString("DefaultConnection")
            ?? "Data Source=rollyroll.db"));

// ---------- Configuration values ----------
var imageStorePath = builder.Configuration.GetValue<string>("RollyRoll:ImageStorePath")
    ?? Path.Combine(AppContext.BaseDirectory, "Images");
var serverBaseUrl = builder.Configuration.GetValue<string>("RollyRoll:ServerBaseUrl")
    ?? "http://localhost:5000";
var tftpServerIp = builder.Configuration.GetValue<string>("RollyRoll:TftpServerIp")
    ?? "0.0.0.0";
var masterKey = builder.Configuration.GetValue<string>("RollyRoll:MasterEncryptionKey")
    ?? "RollyRoll-Default-Key-CHANGE-ME";

Directory.CreateDirectory(imageStorePath);

// ---------- Infrastructure services ----------
// Scoped services (depend on DbContext)
builder.Services.AddScoped<IClientDiscoveryService, ClientDiscoveryService>();
builder.Services.AddScoped<IDeploymentService, DeploymentEngine>();
builder.Services.AddScoped<IImageService>(sp =>
    new DismImageService(
        sp.GetRequiredService<ILogger<DismImageService>>(),
        sp.GetRequiredService<RollyRollDbContext>(),
        imageStorePath));
builder.Services.AddScoped<ISettingsService, SettingsService>();

// WakeOnLanService is scoped (depends on DbContext)
builder.Services.AddScoped<IWakeOnLanService, WakeOnLanService>();

// RecoveryService — register without IUserProfileService for now (made optional)
builder.Services.AddScoped<IRecoveryService, RecoveryService>();

// Singletons (no DbContext dependency)
builder.Services.AddSingleton<IAutoDiscoveryService, AutoDiscoveryService>();
builder.Services.AddSingleton(new EncryptionService(
    LoggerFactory.Create(b => b.AddConsole()).CreateLogger<EncryptionService>(),
    masterKey));

// BootMenuGenerator uses IServiceScopeFactory internally (see refactored class)
builder.Services.AddSingleton<BootMenuGenerator>(sp =>
    new BootMenuGenerator(
        sp.GetRequiredService<ILogger<BootMenuGenerator>>(),
        sp.GetRequiredService<IServiceScopeFactory>(),
        serverBaseUrl));

// Services pending full implementation:
// builder.Services.AddScoped<IPatchService, PatchService>();
// builder.Services.AddScoped<ISchedulerService, SchedulerService>();
// builder.Services.AddScoped<IAuditService, AuditService>();
// builder.Services.AddScoped<IUserProfileService, UserProfileService>();

// ---------- Blazor Server & SignalR ----------
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSignalR();
builder.Services.AddControllers();

var app = builder.Build();

// ---------- Middleware ----------
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

// ---------- Endpoints ----------
app.MapRazorComponents<RollyRoll.Web.Components.App>()
    .AddInteractiveServerRenderMode();

app.MapHub<DeploymentHub>("/hubs/deployment");
app.MapControllers();

// ---------- WinPE Agent API ----------

// GET /api/boot/script/{mac} — iPXE boot script for a given MAC
app.MapGet("/api/boot/script/{mac}", async (string mac, BootMenuGenerator bootMenu, CancellationToken ct) =>
{
    var script = await bootMenu.GenerateScriptAsync(mac, ct);
    return Results.Content(script, "text/plain");
});

// GET /api/task/{mac} — Get pending task for a client MAC
app.MapGet("/api/task/{mac}", async (string mac, IDeploymentService deploymentService) =>
{
    var task = await deploymentService.GetPendingTaskForClientAsync(mac);
    return task is not null ? Results.Ok(task) : Results.NotFound();
});

// POST /api/task/{id}/progress — Report progress from WinPE agent
app.MapPost("/api/task/{id:int}/progress", async (int id, TaskProgressRequest request, IDeploymentService deploymentService) =>
{
    await deploymentService.UpdateTaskProgressAsync(id, request.ProgressPercent, request.StatusMessage);
    return Results.Ok();
});

// POST /api/task/{id}/complete — Report task completion from WinPE agent
app.MapPost("/api/task/{id:int}/complete", async (int id, TaskCompleteRequest request, IDeploymentService deploymentService) =>
{
    await deploymentService.CompleteTaskAsync(id, request.Success, request.ErrorMessage);
    return Results.Ok();
});

// ---------- Client Agent API ----------

// POST /api/agent/heartbeat — Receive heartbeat from ClientAgent
app.MapPost("/api/agent/heartbeat", async (AgentHeartbeatRequest request, IClientDiscoveryService clientDiscovery) =>
{
    await clientDiscovery.RegisterFromAgentAsync(
        request.MacAddress, request.Hostname, request.IpAddress,
        request.OsVersion, request.HardwareModel);
    return Results.Ok();
});

// GET /api/agent/task/{mac} — Get pending task (alias for WinPE agent compatibility)
app.MapGet("/api/agent/task/{mac}", async (string mac, IDeploymentService deploymentService) =>
{
    var task = await deploymentService.GetPendingTaskForClientAsync(mac);
    return task is not null ? Results.Ok(task) : Results.NotFound();
});

// POST /api/agent/progress — Report progress from agent
app.MapPost("/api/agent/progress", async (AgentProgressRequest request, IDeploymentService deploymentService) =>
{
    await deploymentService.UpdateTaskProgressAsync(request.TaskId, request.ProgressPercent, request.StatusMessage);
    return Results.Ok();
});

// POST /api/agent/complete — Report completion from agent
app.MapPost("/api/agent/complete", async (AgentCompleteRequest request, IDeploymentService deploymentService) =>
{
    await deploymentService.CompleteTaskAsync(request.TaskId, request.Success, request.ErrorMessage);
    return Results.Ok();
});

// GET /api/agent/image/{id} — Download image file
app.MapGet("/api/agent/image/{id:int}", async (int id, IImageService imageService) =>
{
    var image = await imageService.GetImageByIdAsync(id);
    if (image is null || !File.Exists(image.FilePath))
        return Results.NotFound();

    return Results.File(image.FilePath, "application/octet-stream", Path.GetFileName(image.FilePath));
});

// ---------- Ensure database is created ----------
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<RollyRollDbContext>();
    await db.Database.EnsureCreatedAsync();

    var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
    await settings.InitializeDefaultSettingsAsync();
}

app.Run();

// ---------- Request DTOs ----------

/// <summary>Progress update from WinPE agent.</summary>
public record TaskProgressRequest(int ProgressPercent, string StatusMessage);

/// <summary>Task completion report from WinPE agent.</summary>
public record TaskCompleteRequest(bool Success, string? ErrorMessage);

/// <summary>Heartbeat from ClientAgent.</summary>
public record AgentHeartbeatRequest(
    string MacAddress, string Hostname, string IpAddress,
    string OsVersion, string? HardwareModel, bool IsOnline);

/// <summary>Progress report from agent (includes TaskId in body).</summary>
public record AgentProgressRequest(int TaskId, int ProgressPercent, string StatusMessage);

/// <summary>Completion report from agent (includes TaskId in body).</summary>
public record AgentCompleteRequest(int TaskId, bool Success, string? ErrorMessage);
