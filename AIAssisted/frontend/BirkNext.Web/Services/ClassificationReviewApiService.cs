using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>Client for the backend Security Classification review. Tokens go with a run request only; they are never stored by BirkNext.</summary>
public interface IClassificationReviewApiService
{
    Task<ClassificationOverview> OverviewAsync(string environmentId, CancellationToken ct = default);
    /// <summary>Source Analysis snapshots for this review and, for a selected scope, its candidates and combined evidence (read-only).</summary>
    Task<ClassificationScopeOptions> SourceScopeAsync(string environmentId, ReviewSourceScopeRequest? scope, CancellationToken ct = default);
    Task<(ClassificationTestContext? Context, string? Error)> SaveContextAsync(string environmentId, ClassificationTestContext context, CancellationToken ct = default);
    /// <summary>Clears the temporary in-memory test context on the backend (no stored review is touched).</summary>
    Task ClearContextAsync(string environmentId, CancellationToken ct = default);
    /// <summary>Throws <see cref="InvalidOperationException"/> with the backend's reason when the source scope is rejected.</summary>
    Task<ClassificationReviewResult> RunAsync(string environmentId, ClassificationRunRequest request, CancellationToken ct = default);
    Task<ClassificationReviewResult?> GetRunAsync(Guid runId, CancellationToken ct = default);
}

public sealed class ClassificationReviewApiService(HttpClient http) : IClassificationReviewApiService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Env(string id) => $"environmentId={Uri.EscapeDataString(id)}";

    public async Task<ClassificationOverview> OverviewAsync(string environmentId, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<ClassificationOverview>($"api/security-classification?{Env(environmentId)}", Json, ct) ?? new ClassificationOverview();

    public async Task<ClassificationScopeOptions> SourceScopeAsync(string environmentId, ReviewSourceScopeRequest? scope, CancellationToken ct = default)
    {
        var query = Env(environmentId);
        if (scope is not null)
            query += $"&primary={scope.PrimarySnapshotId}" + string.Concat(scope.RelatedSnapshotIds.Select(id => $"&related={id}")) + string.Concat(scope.ExcludedSuggestions.Select(e => $"&excluded={Uri.EscapeDataString(e)}"));
        return await http.GetFromJsonAsync<ClassificationScopeOptions>($"api/security-classification/source-scope?{query}", Json, ct) ?? new ClassificationScopeOptions();
    }

    public async Task<(ClassificationTestContext? Context, string? Error)> SaveContextAsync(string environmentId, ClassificationTestContext context, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"api/security-classification/context?{Env(environmentId)}", context, Json, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest) return (null, (await response.Content.ReadAsStringAsync(ct)).Trim('"'));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ClassificationTestContext>(Json, ct), null);
    }

    public async Task ClearContextAsync(string environmentId, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"api/security-classification/context?{Env(environmentId)}", ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<ClassificationReviewResult> RunAsync(string environmentId, ClassificationRunRequest request, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"api/security-classification/runs?{Env(environmentId)}", request, Json, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest) throw new InvalidOperationException((await response.Content.ReadAsStringAsync(ct)).Trim('"'));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ClassificationReviewResult>(Json, ct))!;
    }

    public async Task<ClassificationReviewResult?> GetRunAsync(Guid runId, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/security-classification/runs/{runId}", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ClassificationReviewResult>(Json, ct);
    }
}
