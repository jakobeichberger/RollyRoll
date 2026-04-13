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

// ---------- Infrastructure services ----------
// Services with existing implementations
builder.Services.AddScoped<IClientDiscoveryService, ClientDiscoveryService>();
builder.Services.AddScoped<IDeploymentService, DeploymentEngine>();
builder.Services.AddScoped<IImageService, DismImageService>();
builder.Services.AddScoped<IRecoveryService, RecoveryService>();
builder.Services.AddScoped<ISettingsService, SettingsService>();
builder.Services.AddSingleton<IWakeOnLanService, WakeOnLanService>();
builder.Services.AddSingleton<IAutoDiscoveryService, AutoDiscoveryService>();

// Services pending implementation — register when concrete classes are added:
// builder.Services.AddScoped<IPatchService, PatchService>();
// builder.Services.AddScoped<ISchedulerService, SchedulerService>();
// builder.Services.AddScoped<IAuditService, AuditService>();
// builder.Services.AddSingleton<IPxeService, PxeService>();
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
app.MapGet("/api/boot/script/{mac}", async (string mac, IPxeService pxeService) =>
{
    var script = await pxeService.GenerateBootScriptAsync(mac, RollyRoll.Core.Models.BootType.Unknown);
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
