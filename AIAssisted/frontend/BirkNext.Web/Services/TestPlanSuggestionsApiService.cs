using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.TestPlanning;

namespace BirkNext.Web.Services;

/// <summary>Test Plan Suggestions (api/test-plan-suggestions). Errors come back as a message, never as an exception. Read-only towards Azure DevOps:
/// nothing here talks to it.</summary>
public interface ITestPlanSuggestionsApiService
{
    Task<(TestPlanWorkspace? Workspace, string? Error)> WorkspaceAsync(string? guideKey, CancellationToken ct = default);
    /// <summary>Persisted facts only (drafts, snapshots, candidate count): what the Manual Testing focus panel shows.</summary>
    Task<(TestPlanWorkspace? Workspace, string? Error)> SummaryAsync(string? guideKey, CancellationToken ct = default);
    Task<(TestPlanDraft? Draft, string? Error)> DraftAsync(Guid id, CancellationToken ct = default);
    Task<(TestPlanDraft? Draft, string? Error)> CreateAsync(CreateTestPlanDraftRequest request, CancellationToken ct = default);
    Task<(TestPlanDraft? Draft, string? Error)> SaveAsync(Guid id, SaveTestPlanDraftRequest request, CancellationToken ct = default);
    Task<(TestPlanDraft? Draft, string? Error)> ReviewAsync(Guid id, ReviewTestCaseRequest request, CancellationToken ct = default);
    Task<(TestPlanDraft? Draft, string? Error)> RefreshAsync(Guid id, int baseRevision, CancellationToken ct = default);
    Task<(SaveTestPlanSnapshotResult? Result, string? Error)> SaveSnapshotAsync(Guid id, SaveTestPlanSnapshotRequest request, CancellationToken ct = default);
    Task<(TestPlanSnapshot? Snapshot, string? Error)> SnapshotAsync(Guid id, CancellationToken ct = default);
    Task<(DeliveryComparison? Comparison, string? Error)> CompareAsync(Guid from, Guid to, CancellationToken ct = default);
    Task<(TestPlanExport? Export, string? Error)> ExportAsync(Guid snapshotId, CancellationToken ct = default);
}

public sealed class TestPlanSuggestionsApiService(HttpClient http) : ITestPlanSuggestionsApiService
{
    private const string Base = "api/test-plan-suggestions";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public Task<(TestPlanWorkspace?, string?)> WorkspaceAsync(string? guideKey, CancellationToken ct = default) =>
        Send<TestPlanWorkspace>(() => http.GetAsync(guideKey is null ? Base : $"{Base}?guideKey={Uri.EscapeDataString(guideKey)}", ct), ct);

    public Task<(TestPlanWorkspace?, string?)> SummaryAsync(string? guideKey, CancellationToken ct = default) =>
        Send<TestPlanWorkspace>(() => http.GetAsync(guideKey is null ? $"{Base}/summary" : $"{Base}/summary?guideKey={Uri.EscapeDataString(guideKey)}", ct), ct);

    public Task<(TestPlanDraft?, string?)> DraftAsync(Guid id, CancellationToken ct = default) => Send<TestPlanDraft>(() => http.GetAsync($"{Base}/drafts/{id}", ct), ct);

    public Task<(TestPlanDraft?, string?)> CreateAsync(CreateTestPlanDraftRequest request, CancellationToken ct = default) =>
        Send<TestPlanDraft>(() => http.PostAsJsonAsync($"{Base}/drafts", request, Json, ct), ct);

    public Task<(TestPlanDraft?, string?)> SaveAsync(Guid id, SaveTestPlanDraftRequest request, CancellationToken ct = default) =>
        Send<TestPlanDraft>(() => http.PutAsJsonAsync($"{Base}/drafts/{id}", request, Json, ct), ct);

    public Task<(TestPlanDraft?, string?)> ReviewAsync(Guid id, ReviewTestCaseRequest request, CancellationToken ct = default) =>
        Send<TestPlanDraft>(() => http.PostAsJsonAsync($"{Base}/drafts/{id}/review", request, Json, ct), ct);

    public Task<(TestPlanDraft?, string?)> RefreshAsync(Guid id, int baseRevision, CancellationToken ct = default) =>
        Send<TestPlanDraft>(() => http.PostAsync($"{Base}/drafts/{id}/refresh?baseRevision={baseRevision}", null, ct), ct);

    public Task<(SaveTestPlanSnapshotResult?, string?)> SaveSnapshotAsync(Guid id, SaveTestPlanSnapshotRequest request, CancellationToken ct = default) =>
        Send<SaveTestPlanSnapshotResult>(() => http.PostAsJsonAsync($"{Base}/drafts/{id}/snapshots", request, Json, ct), ct);

    public Task<(TestPlanSnapshot?, string?)> SnapshotAsync(Guid id, CancellationToken ct = default) => Send<TestPlanSnapshot>(() => http.GetAsync($"{Base}/snapshots/{id}", ct), ct);

    public Task<(DeliveryComparison?, string?)> CompareAsync(Guid from, Guid to, CancellationToken ct = default) =>
        Send<DeliveryComparison>(() => http.GetAsync($"{Base}/compare?from={from}&to={to}", ct), ct);

    public Task<(TestPlanExport?, string?)> ExportAsync(Guid snapshotId, CancellationToken ct = default) =>
        Send<TestPlanExport>(() => http.GetAsync($"{Base}/snapshots/{snapshotId}/export", ct), ct);

    private static async Task<(T? Value, string? Error)> Send<T>(Func<Task<HttpResponseMessage>> call, CancellationToken ct)
    {
        try
        {
            using var response = await call();
            if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<T>(Json, ct), null);
            if ((int)response.StatusCode is 401 or 403) return (default, "You do not have permission to change Test Plan proposals.");
            string? message = null;
            try { message = (await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct)).GetProperty("message").GetString(); }
            catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException) { }
            return (default, message ?? $"The request failed (HTTP {(int)response.StatusCode}).");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            return (default, "Test Plan Suggestions is not reachable.");
        }
    }
}
