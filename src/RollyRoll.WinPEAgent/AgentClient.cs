using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RollyRoll.Core.Models;

namespace RollyRoll.WinPEAgent;

/// <summary>
/// HTTPS client for communicating with the RollyRoll server API.
/// Handles task retrieval, progress reporting, completion reporting, and image downloads.
/// </summary>
public class AgentClient : IDisposable
{
    private readonly ILogger<AgentClient> _logger;
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public AgentClient(ILogger<AgentClient> logger, string serverUrl)
    {
        _logger = logger;
        _baseUrl = serverUrl.TrimEnd('/');

        var handler = new HttpClientHandler
        {
            // In WinPE, server may use self-signed certificates
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };

        _httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(_baseUrl),
            Timeout = TimeSpan.FromMinutes(30)
        };
    }

    /// <summary>
    /// Get the pending deployment task for this client identified by MAC address.
    /// </summary>
    public async Task<ScheduledTask?> GetTaskAsync(string macAddress)
    {
        _logger.LogInformation("Requesting task for MAC {Mac}", macAddress);

        var response = await _httpClient.GetAsync($"/api/task/{Uri.EscapeDataString(macAddress)}");
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogInformation("No pending task found for MAC {Mac}", macAddress);
                return null;
            }

            var errorBody = await response.Content.ReadAsStringAsync();
            _logger.LogError("Failed to get task: {StatusCode} - {Error}", response.StatusCode, errorBody);
            throw new HttpRequestException($"Server returned {response.StatusCode}: {errorBody}");
        }

        var task = await response.Content.ReadFromJsonAsync<ScheduledTask>(JsonOptions);
        _logger.LogInformation("Received task: {TaskId} ({TaskType})", task?.Id, task?.TaskType);
        return task;
    }

    /// <summary>
    /// Report deployment progress to the server.
    /// </summary>
    public async Task ReportProgressAsync(int taskId, int progressPercent, string statusMessage)
    {
        _logger.LogDebug("Reporting progress: Task={TaskId}, Progress={Percent}%, Message={Message}",
            taskId, progressPercent, statusMessage);

        var payload = new
        {
            TaskId = taskId,
            ProgressPercent = progressPercent,
            StatusMessage = statusMessage
        };

        try
        {
            var response = await _httpClient.PostAsJsonAsync($"/api/task/{taskId}/progress", payload, JsonOptions);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            // Progress reporting failures should not abort the deployment
            _logger.LogWarning(ex, "Failed to report progress for task {TaskId}", taskId);
        }
    }

    /// <summary>
    /// Report deployment completion (success or failure) to the server.
    /// </summary>
    public async Task ReportCompletionAsync(int taskId, bool success, string? errorMessage = null)
    {
        _logger.LogInformation("Reporting completion: Task={TaskId}, Success={Success}", taskId, success);

        var payload = new
        {
            TaskId = taskId,
            Success = success,
            ErrorMessage = errorMessage
        };

        var response = await _httpClient.PostAsJsonAsync($"/api/task/{taskId}/complete", payload, JsonOptions);
        response.EnsureSuccessStatusCode();

        _logger.LogInformation("Completion reported successfully for task {TaskId}", taskId);
    }

    /// <summary>
    /// Download a WIM image from the server to a local path.
    /// Supports progress reporting via IProgress callback.
    /// </summary>
    public async Task DownloadImageAsync(int imageId, string localPath, IProgress<int>? progress = null)
    {
        _logger.LogInformation("Downloading image {ImageId} to {Path}", imageId, localPath);

        using var response = await _httpClient.GetAsync(
            $"/api/agent/image/{imageId}", // served by Web/Program.cs
            HttpCompletionOption.ResponseHeadersRead);

        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1;
        var bytesRead = 0L;

        await using var contentStream = await response.Content.ReadAsStreamAsync();
        await using var fileStream = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);

        var buffer = new byte[81920];
        int read;
        var lastReportedPercent = -1;

        while ((read = await contentStream.ReadAsync(buffer)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read));
            bytesRead += read;

            if (totalBytes > 0)
            {
                var percent = (int)(bytesRead * 100 / totalBytes);
                if (percent != lastReportedPercent)
                {
                    progress?.Report(percent);
                    lastReportedPercent = percent;
                }
            }
        }

        _logger.LogInformation("Image downloaded: {Bytes} bytes written to {Path}", bytesRead, localPath);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
