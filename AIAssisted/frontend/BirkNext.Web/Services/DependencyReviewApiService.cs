using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.Dependencies;

namespace BirkNext.Web.Services;

/// <summary>
/// Client for the backend Dependency Review (Renovate policy). Archives are uploaded for read-only, in-memory analysis; nothing is written to a
/// repository and Renovate is never run. Simulations use synthetic candidate versions only.
/// </summary>
public interface IDependencyReviewApiService
{
    /// <summary>Runs a review of the uploaded archives; a config is optional and replaces that repository's in-repository configuration.</summary>
    Task<(DependencyReviewResult? Result, string? Error)> RunAsync(string label, IReadOnlyList<(string FileName, Stream Content)> archives,
        (string Repository, string FileName, Stream Content)? configOverride, CancellationToken ct = default);
    Task<IReadOnlyList<DependencyReviewRunSummary>> HistoryAsync(CancellationToken ct = default);
    Task<DependencyReviewResult?> GetAsync(Guid runId, CancellationToken ct = default);
    Task<(PolicySimulation? Simulation, string? Error)> SimulateAsync(PolicySimulationRequest request, CancellationToken ct = default);
}

public sealed class DependencyReviewApiService(HttpClient http) : IDependencyReviewApiService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<(DependencyReviewResult? Result, string? Error)> RunAsync(string label, IReadOnlyList<(string FileName, Stream Content)> archives,
        (string Repository, string FileName, Stream Content)? configOverride, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(label), "label");
        foreach (var (name, content) in archives)
        {
            var part = new StreamContent(content);
            part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
            form.Add(part, "archives", name);
        }
        if (configOverride is { } config)
        {
            var part = new StreamContent(config.Content);
            part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            form.Add(part, $"config:{config.Repository}", config.FileName);
        }
        using var response = await http.PostAsync("api/dependency-review/runs", form, ct);
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
}
