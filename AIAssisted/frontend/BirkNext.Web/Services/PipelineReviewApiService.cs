using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.PipelineReview;

namespace BirkNext.Web.Services;

/// <summary>One selectable Source Analysis snapshot; <paramref name="Status"/> is the Source Analysis status, <paramref name="CiCdStatus"/> the CI/CD domain status.</summary>
public sealed record PipelineReviewSourceOption(Guid Id, string ArchiveName, string Fingerprint, DateTimeOffset AnalyzedAt, int CiCdAnalyzerVersion, int Pipelines, int Templates, string? Repository,
    string? Status = null, string? CiCdStatus = null);
public sealed record PipelineReviewSourceList(bool SourceAnalysisEnabled, List<PipelineReviewSourceOption> Snapshots, Guid? DefaultSnapshotId);

/// <summary>Pipeline Review reads (all GET): snapshots, the review of one snapshot, a path probe and a comparison of two snapshots.</summary>
public interface IPipelineReviewApiService
{
    Task<PipelineReviewSourceList> SourcesAsync(string environmentId, CancellationToken ct = default);
    Task<(PipelineReviewResult? Result, string? Error)> ReviewAsync(string environmentId, Guid? snapshotId, bool metadata, CancellationToken ct = default);
    Task<PathProbeResult?> ProbeAsync(string environmentId, Guid snapshotId, string path, CancellationToken ct = default);
    Task<PipelineReviewComparison?> CompareAsync(string environmentId, Guid previous, Guid current, CancellationToken ct = default);
}

public sealed class PipelineReviewApiService(HttpClient http) : IPipelineReviewApiService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Env(string id) => $"environmentId={Uri.EscapeDataString(id)}";

    public async Task<PipelineReviewSourceList> SourcesAsync(string environmentId, CancellationToken ct = default)
    {
        try { return await http.GetFromJsonAsync<PipelineReviewSourceList>($"api/pipeline-review/sources?{Env(environmentId)}", Json, ct) ?? new(true, [], null); }
        catch (HttpRequestException) { return new(true, [], null); }
    }

    public async Task<(PipelineReviewResult? Result, string? Error)> ReviewAsync(string environmentId, Guid? snapshotId, bool metadata, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.GetAsync($"api/pipeline-review?{Env(environmentId)}{(snapshotId is { } id ? $"&snapshotId={id}" : "")}&metadata={(metadata ? "true" : "false")}", ct);
            if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<PipelineReviewResult>(Json, ct), null);
            var text = await response.Content.ReadAsStringAsync(ct);
            try { if (JsonDocument.Parse(text).RootElement.TryGetProperty("message", out var m)) return (null, m.GetString()); } catch (JsonException) { }
            return (null, $"The review could not be loaded (HTTP {(int)response.StatusCode}).");
        }
        catch (HttpRequestException e) { return (null, $"BirkNext.Api could not be reached ({e.GetType().Name})."); }
    }

    public async Task<PathProbeResult?> ProbeAsync(string environmentId, Guid snapshotId, string path, CancellationToken ct = default)
    {
        try { return await http.GetFromJsonAsync<PathProbeResult>($"api/pipeline-review/probe?{Env(environmentId)}&snapshotId={snapshotId}&path={Uri.EscapeDataString(path)}", Json, ct); }
        catch (HttpRequestException) { return null; }
    }

    public async Task<PipelineReviewComparison?> CompareAsync(string environmentId, Guid previous, Guid current, CancellationToken ct = default)
    {
        try { return await http.GetFromJsonAsync<PipelineReviewComparison>($"api/pipeline-review/compare?{Env(environmentId)}&previous={previous}&current={current}", Json, ct); }
        catch (HttpRequestException) { return null; }
    }
}
