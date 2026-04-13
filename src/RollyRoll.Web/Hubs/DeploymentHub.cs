using Microsoft.AspNetCore.SignalR;

namespace RollyRoll.Web.Hubs;

/// <summary>
/// SignalR hub for real-time deployment status, client discovery, and alert notifications.
/// The web dashboard connects here for live updates; API endpoints push updates through it.
/// </summary>
public class DeploymentHub : Hub
{
    /// <summary>Send deployment progress to all connected dashboards.</summary>
    public async Task SendProgress(int taskId, int progressPercent, string statusMessage)
    {
        await Clients.All.SendAsync("ReceiveProgress", taskId, progressPercent, statusMessage);
    }

    /// <summary>Notify all dashboards that a deployment task completed.</summary>
    public async Task SendTaskComplete(int taskId, bool success, string? errorMessage)
    {
        await Clients.All.SendAsync("ReceiveTaskComplete", taskId, success, errorMessage);
    }

    /// <summary>Notify all dashboards that a new client was discovered via PXE or agent.</summary>
    public async Task SendClientDiscovered(int clientId, string macAddress, string hostname)
    {
        await Clients.All.SendAsync("ReceiveClientDiscovered", clientId, macAddress, hostname);
    }

    /// <summary>Send an alert to all connected dashboards.</summary>
    public async Task SendAlert(string severity, string title, string message)
    {
        await Clients.All.SendAsync("ReceiveAlert", severity, title, message);
    }

    public override async Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await base.OnDisconnectedAsync(exception);
    }
}
