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

    /// <summary>Safe fuzzing eligibility and case preview (the backend decides safety and derives cases; nothing is fuzzed).</summary>
    Task<(ApiFuzzingPlan? Plan, string? Error)> PlanFuzzingAsync(ApiFuzzingRunRequest request, CancellationToken ct = default) =>
        Task.FromResult<(ApiFuzzingPlan?, string?)>((null, "Safe fuzzing is not available in this client."));
    /// <summary>Starts a bounded background run; the backend re-derives every case and refuses blocked environments.</summary>
    Task<(ApiFuzzingReport? Run, string? Error)> StartFuzzingAsync(ApiFuzzingRunRequest request, CancellationToken ct = default) =>
        Task.FromResult<(ApiFuzzingReport?, string?)>((null, "Safe fuzzing is not available in this client."));
    Task<ApiFuzzingReport?> GetFuzzingRunAsync(string runId, CancellationToken ct = default) => Task.FromResult<ApiFuzzingReport?>(null);
    Task<ApiFuzzingReport?> CancelFuzzingAsync(string runId, CancellationToken ct = default) => Task.FromResult<ApiFuzzingReport?>(null);
    /// <summary>Whether protected security execution is available and whether the Target Environment is server-registered (no target request).</summary>
    Task<BirkNext.RuntimeSecurity.SecurityExecutionStatus?> GetSecurityExecutionAsync(string? profileId, CancellationToken ct = default) =>
        Task.FromResult<BirkNext.RuntimeSecurity.SecurityExecutionStatus?>(null);
    /// <summary>Runs explicit authorization scenarios; the backend re-checks trust, safety and every scenario.</summary>
    Task<(BirkNext.RuntimeSecurity.AuthorizationRunReport? Report, string? Error)> RunAuthorizationAsync(BirkNext.RuntimeSecurity.AuthorizationRunRequest request, CancellationToken ct = default) =>
        Task.FromResult<(BirkNext.RuntimeSecurity.AuthorizationRunReport?, string?)>((null, "Authorization scenarios are not available in this client."));
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

    public async Task<(ApiFuzzingPlan? Plan, string? Error)> PlanFuzzingAsync(ApiFuzzingRunRequest request, CancellationToken ct = default)
    {
        try
        {
            using var response = await client.PostAsJsonAsync("api/api-quality/fuzzing/plan", request, ct);
            if (!response.IsSuccessStatusCode) return (null, await ErrorMessageAsync(response, "Eligibility analysis failed", ct));
            return (await response.Content.ReadFromJsonAsync<ApiFuzzingPlan>(cancellationToken: ct), null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return (null, "Could not reach the backend. Check that the server is running."); }
    }

    public async Task<(ApiFuzzingReport? Run, string? Error)> StartFuzzingAsync(ApiFuzzingRunRequest request, CancellationToken ct = default)
    {
        try
        {
            using var response = await client.PostAsJsonAsync("api/api-quality/fuzzing/runs", request, ct);
            if (!response.IsSuccessStatusCode) return (null, await ErrorMessageAsync(response, "Safe fuzzing could not start", ct));
            return (await response.Content.ReadFromJsonAsync<ApiFuzzingReport>(cancellationToken: ct), null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return (null, "Could not reach the backend. Check that the server is running."); }
    }

    public async Task<ApiFuzzingReport?> GetFuzzingRunAsync(string runId, CancellationToken ct = default)
    {
        try { return await client.GetFromJsonAsync<ApiFuzzingReport>($"api/api-quality/fuzzing/runs/{Uri.EscapeDataString(runId)}", ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return null; }
    }

    public async Task<ApiFuzzingReport?> CancelFuzzingAsync(string runId, CancellationToken ct = default)
    {
        try
        {
            using var response = await client.PostAsync($"api/api-quality/fuzzing/runs/{Uri.EscapeDataString(runId)}/cancel", null, ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<ApiFuzzingReport>(cancellationToken: ct) : null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return null; }
    }

    public async Task<BirkNext.RuntimeSecurity.SecurityExecutionStatus?> GetSecurityExecutionAsync(string? profileId, CancellationToken ct = default)
    {
        try { return await client.GetFromJsonAsync<BirkNext.RuntimeSecurity.SecurityExecutionStatus>($"api/api-quality/security-execution?profileId={Uri.EscapeDataString(profileId ?? "")}", ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return null; }
    }

    public async Task<(BirkNext.RuntimeSecurity.AuthorizationRunReport? Report, string? Error)> RunAuthorizationAsync(BirkNext.RuntimeSecurity.AuthorizationRunRequest request, CancellationToken ct = default)
    {
        try
        {
            using var response = await client.PostAsJsonAsync("api/api-quality/authorization/runs", request, ct);
            if (!response.IsSuccessStatusCode) return (null, await ErrorMessageAsync(response, "Authorization scenarios could not run", ct));
            return (await response.Content.ReadFromJsonAsync<BirkNext.RuntimeSecurity.AuthorizationRunReport>(cancellationToken: ct), null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return (null, "Could not reach the backend. Check that the server is running."); }
    }

    /// <summary>The backend's own message (safety policy, conflict) when it sent one; never a raw body.</summary>
    private static async Task<string> ErrorMessageAsync(HttpResponseMessage response, string prefix, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("message", out var m) && m.GetString() is { Length: > 0 and < 600 } message) return message;
        }
        catch (JsonException) { }
        return $"{prefix} (HTTP {(int)response.StatusCode}).";
    }
}

/// <summary>Per-environment API review history: last report, run summaries and drift baselines (structural evidence only).</summary>
public sealed class ApiReviewHistory
{
    public ApiReviewReport? LastReport { get; set; }
    public List<ApiReviewRunSummary> Runs { get; set; } = [];
    public Dictionary<string, ApiReviewBaseline> Baselines { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Completed safe-fuzzing runs, newest first, each with the environment, settings, contract fingerprints and results it ran with.</summary>
    public List<ApiFuzzingReport> FuzzRuns { get; set; } = [];
    /// <summary>Completed authorization scenario runs, newest first, each bound to the scenarios, trust decision and expectation fingerprint it ran with.</summary>
    public List<BirkNext.RuntimeSecurity.AuthorizationRunReport> AuthorizationRuns { get; set; } = [];
}

public sealed record ApiReviewRunSummary(DateTimeOffset GeneratedAt, string EnvironmentName, int Targets, int Completed, int Blocked, int High, int Medium, int Low, int Info);

/// <summary>
/// Safe persistence of API review results per Target Environment (<c>birknext:api-review</c>): environment id, target identities,
/// contract hashes, findings, structural evidence, coverage and access mode. Never tokens, cookies, bodies or query bodies (none exist in
/// the model). Baselines from the previous run feed contract-drift detection of the next run.
/// </summary>
public interface IApiReviewHistoryService
{
    /// <summary>Local data reset: forgets API review history in memory (the storage key is removed by the reset coordinator).</summary>
    void ResetForLocalDataReset() { }
    Task LoadAsync(IJSRuntime js);
    ApiReviewHistory For(string profileId);
    Task RecordAsync(IJSRuntime js, string profileId, ApiReviewReport report);
    Task ClearAsync(IJSRuntime js, string profileId);
    /// <summary>Stores a finished fuzzing run as recorded (never re-evaluated against a newer contract).</summary>
    Task RecordFuzzingAsync(IJSRuntime js, string profileId, ApiFuzzingReport report) => Task.CompletedTask;
    /// <summary>Stores a finished authorization run as recorded (identity aliases and roles only; never a credential).</summary>
    Task RecordAuthorizationAsync(IJSRuntime js, string profileId, BirkNext.RuntimeSecurity.AuthorizationRunReport report) => Task.CompletedTask;
}

public sealed class ApiReviewHistoryService : IApiReviewHistoryService
{
    public const string StorageKey = "birknext:api-review";
    public const int MaxRuns = 10;
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, Converters = { new JsonStringEnumConverter() } };
    private Dictionary<string, ApiReviewHistory> _byProfile = new(StringComparer.Ordinal);
    private bool _loaded;

    public void ResetForLocalDataReset() { _byProfile = new(StringComparer.Ordinal); _loaded = false; }

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

    public const int MaxFuzzRuns = 5;

    public async Task RecordFuzzingAsync(IJSRuntime js, string profileId, ApiFuzzingReport report)
    {
        var history = For(profileId);
        history.FuzzRuns.RemoveAll(r => r.RunId == report.RunId);
        history.FuzzRuns.Insert(0, report with { Running = false });
        if (history.FuzzRuns.Count > MaxFuzzRuns) history.FuzzRuns = history.FuzzRuns.Take(MaxFuzzRuns).ToList();
        _byProfile[profileId] = history;
        try { await js.InvokeVoidAsync("birkNextStorage.setItem", StorageKey, JsonSerializer.Serialize(_byProfile, Options)); } catch { /* best effort */ }
    }

    public async Task RecordAuthorizationAsync(IJSRuntime js, string profileId, BirkNext.RuntimeSecurity.AuthorizationRunReport report)
    {
        var history = For(profileId);
        history.AuthorizationRuns.RemoveAll(r => r.RunId == report.RunId);
        history.AuthorizationRuns.Insert(0, report);
        if (history.AuthorizationRuns.Count > MaxFuzzRuns) history.AuthorizationRuns = history.AuthorizationRuns.Take(MaxFuzzRuns).ToList();
        _byProfile[profileId] = history;
        try { await js.InvokeVoidAsync("birkNextStorage.setItem", StorageKey, JsonSerializer.Serialize(_byProfile, Options)); } catch { /* best effort */ }
    }

    public async Task ClearAsync(IJSRuntime js, string profileId)
    {
        _byProfile.Remove(profileId);
        try { await js.InvokeVoidAsync("birkNextStorage.setItem", StorageKey, JsonSerializer.Serialize(_byProfile, Options)); } catch { }
    }
}
