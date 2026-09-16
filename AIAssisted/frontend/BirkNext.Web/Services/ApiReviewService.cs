using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.ApiReview;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;
using Microsoft.JSInterop;

namespace BirkNext.Web.Services;

public interface IApiReviewService
{
    Task<(ApiReviewReport? Report, string? Error)> RunAsync(ApiReviewRunRequest request, CancellationToken ct = default);
}

/// <summary>Backend client of the API Quality Review v2 engine. The request carries the review snapshot and non-secret identity only.</summary>
public sealed class ApiReviewService(HttpClient client) : IApiReviewService
{
    public async Task<(ApiReviewReport? Report, string? Error)> RunAsync(ApiReviewRunRequest request, CancellationToken ct = default)
    {
        try
        {
            using var response = await client.PostAsJsonAsync("api/api-quality/review", request, ct);
            if (!response.IsSuccessStatusCode)
                return (null, (int)response.StatusCode == 400 ? ApiReviewRunEligibility.NoTargetsReason : $"API review failed (HTTP {(int)response.StatusCode}). Check that the backend is running.");
            return (await response.Content.ReadFromJsonAsync<ApiReviewReport>(cancellationToken: ct), null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return (null, "Could not reach the backend. Check that the server is running."); }
    }

}

/// <summary>Per-environment API review history: last report, run summaries and drift baselines (structural evidence only).</summary>
public sealed class ApiReviewHistory
{
    public ApiReviewReport? LastReport { get; set; }
    public List<ApiReviewRunSummary> Runs { get; set; } = [];
    public Dictionary<string, ApiReviewBaseline> Baselines { get; set; } = new(StringComparer.Ordinal);
}

public sealed record ApiReviewRunSummary(DateTimeOffset GeneratedAt, string EnvironmentName, int Targets, int Completed, int Blocked, int High, int Medium, int Low, int Info);

/// <summary>
/// Safe persistence of API review results per Target Environment (<c>birknext:api-review</c>): environment id, target identities,
/// contract hashes, findings, structural evidence, coverage and access mode. Never tokens, cookies, bodies or query bodies (none exist in
/// the model). Baselines from the previous run feed contract-drift detection of the next run.
/// </summary>
public interface IApiReviewHistoryService
{
    Task LoadAsync(IJSRuntime js);
    ApiReviewHistory For(string profileId);
    Task RecordAsync(IJSRuntime js, string profileId, ApiReviewReport report);
    Task ClearAsync(IJSRuntime js, string profileId);
}

public sealed class ApiReviewHistoryService : IApiReviewHistoryService
{
    public const string StorageKey = "birknext:api-review";
    public const int MaxRuns = 10;
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, Converters = { new JsonStringEnumConverter() } };
    private Dictionary<string, ApiReviewHistory> _byProfile = new(StringComparer.Ordinal);
    private bool _loaded;

    public async Task LoadAsync(IJSRuntime js)
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            var json = await js.InvokeAsync<string?>("birkNextStorage.getItem", StorageKey);
            if (!string.IsNullOrWhiteSpace(json)) _byProfile = JsonSerializer.Deserialize<Dictionary<string, ApiReviewHistory>>(json, Options) ?? new(StringComparer.Ordinal);
        }
        catch { /* corrupt or unavailable storage → start empty */ }
    }

    public ApiReviewHistory For(string profileId) => _byProfile.TryGetValue(profileId, out var h) ? h : new ApiReviewHistory();

    public async Task RecordAsync(IJSRuntime js, string profileId, ApiReviewReport report)
    {
        var history = For(profileId);
        foreach (var target in report.Targets.Where(t => t.Baseline is not null && t.Status is ApiReviewTargetStatus.Completed or ApiReviewTargetStatus.PartiallyCompleted))
            history.Baselines[target.Target.TargetId] = target.Baseline!;
        history.Runs.Insert(0, new ApiReviewRunSummary(report.GeneratedAt, report.Environment.Name, report.Targets.Count, report.Targets.Count(t => t.Status == ApiReviewTargetStatus.Completed),
            report.Targets.Count(t => t.Status == ApiReviewTargetStatus.Blocked), report.Findings.Count(f => f.Severity is ApiReviewSeverity.Critical or ApiReviewSeverity.High),
            report.Findings.Count(f => f.Severity == ApiReviewSeverity.Medium), report.Findings.Count(f => f.Severity == ApiReviewSeverity.Low), report.Findings.Count(f => f.Severity == ApiReviewSeverity.Info)));
        if (history.Runs.Count > MaxRuns) history.Runs = history.Runs.Take(MaxRuns).ToList();
        history.LastReport = report;
        _byProfile[profileId] = history;
        try { await js.InvokeVoidAsync("birkNextStorage.setItem", StorageKey, JsonSerializer.Serialize(_byProfile, Options)); } catch { /* best effort */ }
    }

    public async Task ClearAsync(IJSRuntime js, string profileId)
    {
        _byProfile.Remove(profileId);
        try { await js.InvokeVoidAsync("birkNextStorage.setItem", StorageKey, JsonSerializer.Serialize(_byProfile, Options)); } catch { }
    }
}
