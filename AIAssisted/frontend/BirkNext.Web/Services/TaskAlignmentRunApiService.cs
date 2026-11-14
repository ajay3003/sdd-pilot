using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.TaskAlignment;

namespace BirkNext.Web.Services;

/// <summary>Persisted Task Alignment runs: save a completed run, read the latest run of one project's Tasks document, its history, one run.</summary>
public interface ITaskAlignmentRunApiService
{
    Task<(TaskAlignmentRunSummary? Run, string? Error)> SaveAsync(SaveTaskAlignmentRunRequest request, CancellationToken ct = default);
    /// <summary>The newest run for exactly this project and Tasks document path; (null, null) when there is none.</summary>
    Task<(TaskAlignmentRun? Run, string? Error)> LatestAsync(string projectKey, string tasksPath, CancellationToken ct = default);
    /// <summary>
    /// As <see cref="LatestAsync"/>; when this project key has no run and <paramref name="projectName"/> is given, the newest run of an earlier
    /// import of the same project and full path (classify it: stale unless the fingerprints are identical).
    /// </summary>
    Task<(TaskAlignmentRun? Run, string? Error)> LatestOfProjectAsync(string projectKey, string tasksPath, string? projectName, CancellationToken ct = default) =>
        LatestAsync(projectKey, tasksPath, ct);
    Task<(List<TaskAlignmentRunSummary>? Runs, string? Error)> ListAsync(string projectKey, string? tasksPath, CancellationToken ct = default);
    Task<(TaskAlignmentRun? Run, string? Error)> GetAsync(string projectKey, Guid id, CancellationToken ct = default);
}

/// <summary>The project name a run is filed under across imports: the current workspace's project name, only when it is this project key.</summary>
public static class TaskAlignmentProjectName
{
    public static async Task<string?> OfAsync(IServiceProvider services, string? projectKey)
    {
        if (projectKey is not { Length: > 0 } || services.GetService(typeof(ICurrentWorkspaceProjection)) is not ICurrentWorkspaceProjection projection) return null;
        CurrentWorkspaceSnapshot? snapshot;
        try { snapshot = projection.Current is { } current && current.ProjectSlug == projectKey ? current : await projection.GetAsync(); }
        catch (Exception) { return null; }
        return snapshot is not null && string.Equals(snapshot.ProjectSlug, projectKey, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(snapshot.ProjectName)
            ? snapshot.ProjectName.Trim() : null;
    }
}

public sealed class TaskAlignmentRunApiService(HttpClient http) : ITaskAlignmentRunApiService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private const string Base = "api/task-alignment/runs";
    private const string Area = "Task Alignment runs";

    public async Task<(TaskAlignmentRunSummary? Run, string? Error)> SaveAsync(SaveTaskAlignmentRunRequest request, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(Base, request, Json, ct);
            if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<TaskAlignmentRunSummary>(Json, ct), null);
            return (null, response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
                ? "You do not have permission to save Task Alignment results." : await Message(response, ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return (null, BackendRequestClassifier.FromException(Area, ex).UserMessage); }
    }

    public Task<(TaskAlignmentRun? Run, string? Error)> LatestAsync(string projectKey, string tasksPath, CancellationToken ct = default) =>
        GetAsync<TaskAlignmentRun>($"{Base}/latest?projectKey={Uri.EscapeDataString(projectKey)}&tasksPath={Uri.EscapeDataString(tasksPath)}", ct);

    public Task<(TaskAlignmentRun? Run, string? Error)> LatestOfProjectAsync(string projectKey, string tasksPath, string? projectName, CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(projectName) ? LatestAsync(projectKey, tasksPath, ct)
        : GetAsync<TaskAlignmentRun>($"{Base}/latest?projectKey={Uri.EscapeDataString(projectKey)}&tasksPath={Uri.EscapeDataString(tasksPath)}&projectName={Uri.EscapeDataString(projectName)}", ct);

    public Task<(List<TaskAlignmentRunSummary>? Runs, string? Error)> ListAsync(string projectKey, string? tasksPath, CancellationToken ct = default) =>
        GetAsync<List<TaskAlignmentRunSummary>>($"{Base}?projectKey={Uri.EscapeDataString(projectKey)}"
            + (string.IsNullOrWhiteSpace(tasksPath) ? "" : $"&tasksPath={Uri.EscapeDataString(tasksPath)}"), ct);

    public Task<(TaskAlignmentRun? Run, string? Error)> GetAsync(string projectKey, Guid id, CancellationToken ct = default) =>
        GetAsync<TaskAlignmentRun>($"{Base}/{id:D}?projectKey={Uri.EscapeDataString(projectKey)}", ct);

    private async Task<(T? Value, string? Error)> GetAsync<T>(string url, CancellationToken ct) where T : class
    {
        try
        {
            using var response = await http.GetAsync(url, ct);
            if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound) return (null, null);
            return response.IsSuccessStatusCode ? (await response.Content.ReadFromJsonAsync<T>(Json, ct), null) : (null, await Message(response, ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return (null, BackendRequestClassifier.FromException(Area, ex).UserMessage); }
    }

    private static async Task<string> Message(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        try
        {
            if (JsonDocument.Parse(text).RootElement is { ValueKind: JsonValueKind.Object } o)
                foreach (var key in new[] { "error", "message" })
                    if (o.TryGetProperty(key, out var m)) return m.GetString() ?? text;
        }
        catch (JsonException) { }
        return string.IsNullOrWhiteSpace(text) ? $"The request failed (HTTP {(int)response.StatusCode})." : text.Trim('"');
    }
}
