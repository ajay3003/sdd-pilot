using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.Dependencies;

namespace BirkNext.Web.Services;

/// <summary>
/// Client for the backend Dependency Review. Source review: reviews Source Analysis snapshots (one primary + explicitly included related
/// snapshots) — Dependency Review uploads no source; nothing is written to a repository and Renovate is never run; simulations use synthetic
/// candidate versions only. Dependency health: runs over a stored inventory
/// (no source upload); stored runs are returned exactly as recorded and a refresh creates a new run.
/// </summary>
public interface IDependencyReviewApiService
{
    /// <summary>Source Analysis snapshots of the environment and, for a chosen primary, its related-source candidates. Reads only.</summary>
    /// <summary>Source Analysis snapshots and, for a chosen scope, its related-source candidates, newer snapshots and problems (read-only).</summary>
    Task<ReviewSourceOptions> SourceScopeAsync(string environmentId, ReviewSourceScopeRequest? scope, CancellationToken ct = default);
    /// <summary>Reviews exactly the selected snapshots; a config override is optional and replaces that repository's in-repository configuration.</summary>
    Task<(DependencyReviewResult? Result, string? Error)> RunSourceAsync(SourceDependencyReviewRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<DependencyReviewRunSummary>> HistoryAsync(CancellationToken ct = default);
    Task<DependencyReviewResult?> GetAsync(Guid runId, CancellationToken ct = default);
    Task<(PolicySimulation? Simulation, string? Error)> SimulateAsync(PolicySimulationRequest request, CancellationToken ct = default);

    Task<IReadOnlyList<InventorySummary>> InventoriesAsync(CancellationToken ct = default);
    /// <summary>Uploads an SBOM or lock file; the result carries the validation and, only when valid, the stored inventory.</summary>
    Task<InventoryImportResult> ImportInventoryAsync(string fileName, Stream content, SbomRole role, string? environment, string? name, CancellationToken ct = default);
    Task<InventoryImportResult> CaptureDeployedAsync(DeployedCaptureRequest request, CancellationToken ct = default);
    Task<(DependencyHealthRun? Run, string? Error)> RunHealthAsync(DependencyHealthRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<DependencyHealthRunSummary>> HealthHistoryAsync(CancellationToken ct = default);
    Task<DependencyHealthRun?> GetHealthAsync(Guid runId, CancellationToken ct = default);
    Task<(DependencyHealthRun? Run, string? Error)> RefreshHealthAsync(Guid runId, CancellationToken ct = default);
}

public sealed class DependencyReviewApiService(HttpClient http) : IDependencyReviewApiService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ReviewSourceOptions> SourceScopeAsync(string environmentId, ReviewSourceScopeRequest? scope, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<ReviewSourceOptions>($"api/dependency-review/source-scope?environmentId={Uri.EscapeDataString(environmentId)}{ReviewSourceQuery.Of(scope)}", Json, ct) ?? new();

    public async Task<(DependencyReviewResult? Result, string? Error)> RunSourceAsync(SourceDependencyReviewRequest request, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync("api/dependency-review/source-runs", request, Json, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest) return (null, (await response.Content.ReadAsStringAsync(ct)).Trim('"'));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<DependencyReviewResult>(Json, ct), null);
    }

    public async Task<IReadOnlyList<DependencyReviewRunSummary>> HistoryAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<DependencyReviewRunSummary>>("api/dependency-review/runs", Json, ct) ?? [];

    public async Task<DependencyReviewResult?> GetAsync(Guid runId, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/dependency-review/runs/{runId}", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DependencyReviewResult>(Json, ct);
    }

    public async Task<(PolicySimulation? Simulation, string? Error)> SimulateAsync(PolicySimulationRequest request, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync("api/dependency-review/simulate", request, Json, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest) return (null, (await response.Content.ReadAsStringAsync(ct)).Trim('"'));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PolicySimulation>(Json, ct), null);
    }
    public async Task<IReadOnlyList<InventorySummary>> InventoriesAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<InventorySummary>>("api/dependency-review/inventories", Json, ct) ?? [];

    public async Task<InventoryImportResult> ImportInventoryAsync(string fileName, Stream content, SbomRole role, string? environment, string? name, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        var part = new StreamContent(content);
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(fileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ? "application/xml" : "application/json");
        form.Add(part, "document", fileName);
        form.Add(new StringContent(role.ToString()), "role");
        form.Add(new StringContent(environment ?? ""), "environment");
        form.Add(new StringContent(name ?? ""), "name");
        using var response = await http.PostAsync("api/dependency-review/inventories/import", form, ct);
        return await ImportResultAsync(response, ct);
    }

    public async Task<InventoryImportResult> CaptureDeployedAsync(DeployedCaptureRequest request, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync("api/dependency-review/inventories/deployed", request, Json, ct);
        return await ImportResultAsync(response, ct);
    }

    private static async Task<InventoryImportResult> ImportResultAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest) return new InventoryImportResult(null, null, (await response.Content.ReadAsStringAsync(ct)).Trim('"'));
        if (response.StatusCode != System.Net.HttpStatusCode.UnprocessableEntity) response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<InventoryImportResult>(Json, ct) ?? new InventoryImportResult(null, null, "Empty response.");
    }

    public async Task<(DependencyHealthRun? Run, string? Error)> RunHealthAsync(DependencyHealthRequest request, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync("api/dependency-review/health", request, Json, ct);
        return await HealthAsync(response, ct);
    }

    public async Task<IReadOnlyList<DependencyHealthRunSummary>> HealthHistoryAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<DependencyHealthRunSummary>>("api/dependency-review/health", Json, ct) ?? [];

    public async Task<DependencyHealthRun?> GetHealthAsync(Guid runId, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/dependency-review/health/{runId}", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DependencyHealthRun>(Json, ct);
    }

    public async Task<(DependencyHealthRun? Run, string? Error)> RefreshHealthAsync(Guid runId, CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"api/dependency-review/health/{runId}/refresh", null, ct);
        return await HealthAsync(response, ct);
    }

    private static async Task<(DependencyHealthRun?, string?)> HealthAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest) return (null, (await response.Content.ReadAsStringAsync(ct)).Trim('"'));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<DependencyHealthRun>(Json, ct), null);
    }
}
