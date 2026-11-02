using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.ProjectActivity;
using BirkNext.Web.Services.ProjectImport;
using Microsoft.Extensions.Logging;

namespace BirkNext.Web.Services.ProjectActivity;

/// <summary>Client of <c>api/project-activity</c>: persistent, project-scoped activity (Dashboard → Recent activity).</summary>
public interface IProjectActivityApiService
{
    /// <summary>The newest events of one logical project/application, newest first. Null when the backend could not be read.</summary>
    Task<IReadOnlyList<ProjectActivityItem>?> ListAsync(string projectContextId, int take = 10, CancellationToken ct = default);

    /// <summary>Records one event (idempotent per project context + key). Null when it could not be recorded.</summary>
    Task<ProjectActivityItem?> RecordAsync(ProjectActivityRecordRequest request, CancellationToken ct = default);
}

public sealed class ProjectActivityApiService(HttpClient http) : IProjectActivityApiService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<ProjectActivityItem>?> ListAsync(string projectContextId, int take = 10, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/project-activity?projectContextId={Uri.EscapeDataString(projectContextId)}&take={take}", ct);
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<List<ProjectActivityItem>>(Json, ct) : null;
    }

    public async Task<ProjectActivityItem?> RecordAsync(ProjectActivityRecordRequest request, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync("api/project-activity", request, Json, ct);
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<ProjectActivityItem>(Json, ct) : null;
    }
}

/// <summary>
/// The one producer-side entry point for project activity: scopes an event to the current logical application (stable id, never a display
/// name) and records it. Recording never fails the operation that produced it — an import or a review is complete whether or not its activity
/// could be written — and it is called only after the operation's own result was saved. Raises <see cref="Recorded"/> so an open Dashboard
/// refreshes its list.
/// </summary>
public sealed class ProjectActivityRecorder(IProjectActivityApiService api, WorkspaceArtifactRepository repository, ILogger<ProjectActivityRecorder>? logger = null)
{
    public event Action? Recorded;

    /// <summary>The current logical application's id, or null (no imported application, or a Sample Project is the workspace).</summary>
    public string? CurrentContextId =>
        string.IsNullOrWhiteSpace(repository.ProjectName) ? ApplicationContexts.Current(repository.SddLifecycle)?.Id : null;

    /// <summary>Records <paramref name="request"/> under its own context, else the current application. False when there is none or recording failed.</summary>
    public async Task<bool> RecordAsync(ProjectActivityRecordRequest request, CancellationToken ct = default)
    {
        var context = string.IsNullOrWhiteSpace(request.ProjectContextId) ? CurrentContextId : request.ProjectContextId;
        if (context is null) return false;
        var name = request.ProjectName ?? ApplicationContexts.Find(repository.SddLifecycle, context)?.Name;
        try
        {
            if (await api.RecordAsync(request with { ProjectContextId = context, ProjectName = name }, ct) is null) return false;
            Recorded?.Invoke();
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger?.LogWarning("Project activity '{Title}' could not be recorded: {Reason}", request.Title, ex.GetType().Name);
            return false;
        }
    }
}
