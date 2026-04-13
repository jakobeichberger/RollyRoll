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

// WakeOnLanService is scoped (depends on DbContext); factory needed for int ctor params
builder.Services.AddScoped<IWakeOnLanService>(sp =>
    new WakeOnLanService(
        sp.GetRequiredService<ILogger<WakeOnLanService>>(),
        sp.GetRequiredService<RollyRollDbContext>()));

// RecoveryService — register without IUserProfileService for now (made optional)
builder.Services.AddScoped<IRecoveryService, RecoveryService>();

// Singletons (no DbContext dependency)
builder.Services.AddSingleton<IAutoDiscoveryService, AutoDiscoveryService>();
builder.Services.AddSingleton<EncryptionService>(sp =>
    new EncryptionService(
        sp.GetRequiredService<ILogger<EncryptionService>>(),
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

// ---------- Health Check ----------
app.MapGet("/api/health", () => Results.Ok(new { Status = "Healthy", Timestamp = DateTime.UtcNow }));

// ---------- Client Agent Patch/Recovery/Profile Endpoints ----------

// GET /api/agent/patches/{mac} — Get approved patches for a client
app.MapGet("/api/agent/patches/{mac}", async (string mac, RollyRollDbContext db) =>
{
    var patches = await db.PatchPackages
        .Where(p => p.IsApproved)
        .Select(p => new { p.Id, PatchId = p.Id, p.Title, FileName = Path.GetFileName(p.InstallerPath ?? ""), p.SilentInstallArgs })
        .ToListAsync();
    return Results.Ok(patches);
});

// GET /api/agent/patches/{id}/download — Download a patch installer
app.MapGet("/api/agent/patches/{id:int}/download", async (int id, RollyRollDbContext db) =>
{
    var patch = await db.PatchPackages.FindAsync(id);
    if (patch?.InstallerPath is null || !File.Exists(patch.InstallerPath))
        return Results.NotFound();
    return Results.File(patch.InstallerPath, "application/octet-stream", Path.GetFileName(patch.InstallerPath));
});

// POST /api/agent/patches/result — Report patch installation result
app.MapPost("/api/agent/patches/result", async (PatchResultRequest request, RollyRollDbContext db) =>
{
    var entry = new RollyRoll.Core.Models.AuditLogEntry
    {
        Action = request.Success ? "PatchInstalled" : "PatchFailed",
        Category = RollyRoll.Core.Models.AuditCategory.PatchManagement,
        PerformedBy = "ClientAgent",
        TargetName = request.MacAddress,
        TargetId = request.PatchId,
        Details = request.ErrorMessage ?? "Patch installed successfully",
        Success = request.Success,
        ErrorMessage = request.ErrorMessage
    };
    db.AuditLogEntries.Add(entry);
    await db.SaveChangesAsync();
    return Results.Ok();
});

// POST /api/agent/recovery/snapshot — Request a recovery snapshot
app.MapPost("/api/agent/recovery/snapshot", async (RecoverySnapshotRequest request, IRecoveryService recoveryService, IClientDiscoveryService clientService) =>
{
    var client = await clientService.GetClientByMacAsync(request.MacAddress);
    if (client is null) return Results.NotFound();
    await recoveryService.CreateSnapshotAsync(client.Id, request.Reason);
    return Results.Ok();
});

// POST /api/agent/recovery/trigger — Trigger auto-recovery for a client
app.MapPost("/api/agent/recovery/trigger", async (RecoveryTriggerRequest request, IRecoveryService recoveryService, IClientDiscoveryService clientService) =>
{
    var client = await clientService.GetClientByMacAsync(request.MacAddress);
    if (client is null) return Results.NotFound();
    var task = await recoveryService.InitiateRecoveryAsync(client.Id);
    return Results.Ok(new { task.Id });
});

// POST /api/agent/health/issues — Report health issues from client
app.MapPost("/api/agent/health/issues", async (HealthIssuesRequest request, RollyRollDbContext db) =>
{
    foreach (var issue in request.Issues)
    {
        db.AuditLogEntries.Add(new RollyRoll.Core.Models.AuditLogEntry
        {
            Action = "HealthIssue",
            Category = RollyRoll.Core.Models.AuditCategory.Recovery,
            PerformedBy = "ClientAgent",
            TargetName = request.MacAddress,
            Details = issue,
            Success = false
        });
    }
    await db.SaveChangesAsync();
    return Results.Ok();
});

// POST /api/agent/profiles/upload — Upload user profile archive
app.MapPost("/api/agent/profiles/upload", async (HttpRequest httpRequest, RollyRollDbContext db) =>
{
    var form = await httpRequest.ReadFormAsync();
    var file = form.Files.GetFile("profileArchive");
    var macAddress = form["macAddress"].ToString();
    var reason = form["reason"].ToString();

    if (file is null || string.IsNullOrEmpty(macAddress))
        return Results.BadRequest("Missing profileArchive file or macAddress");

    var profilesDir = Path.Combine(AppContext.BaseDirectory, "Profiles", macAddress.Replace(":", ""));
    Directory.CreateDirectory(profilesDir);
    var profilePath = Path.Combine(profilesDir, file.FileName);

    await using var fs = new FileStream(profilePath, FileMode.Create);
    await file.CopyToAsync(fs);

    return Results.Ok(new { Path = profilePath });
}).DisableAntiforgery();

// GET /api/agent/profiles/{mac} — Download profile archive for a client
app.MapGet("/api/agent/profiles/{mac}", async (string mac, RollyRollDbContext db) =>
{
    var profilesDir = Path.Combine(AppContext.BaseDirectory, "Profiles", mac.Replace(":", ""));
    if (!Directory.Exists(profilesDir))
        return Results.NotFound();

    var latestFile = Directory.GetFiles(profilesDir, "*.zip")
        .OrderByDescending(f => new FileInfo(f).CreationTimeUtc)
        .FirstOrDefault();

    if (latestFile is null) return Results.NotFound();
    return Results.File(latestFile, "application/zip", Path.GetFileName(latestFile));
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

/// <summary>Patch installation result from ClientAgent.</summary>
public record PatchResultRequest(string MacAddress, int PatchId, bool Success, string? ErrorMessage);

/// <summary>Recovery snapshot request from ClientAgent.</summary>
public record RecoverySnapshotRequest(string MacAddress, string Reason);

/// <summary>Recovery trigger request from ClientAgent.</summary>
public record RecoveryTriggerRequest(string MacAddress, string Reason);

/// <summary>Health issues report from ClientAgent.</summary>
public record HealthIssuesRequest(string MacAddress, List<string> Issues);
