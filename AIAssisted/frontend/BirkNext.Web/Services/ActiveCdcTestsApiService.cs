using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.Integrations;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Client for IQR → Active tests → CDC. It can only start a built-in scenario by id; there is no call that carries a payload, a hub
/// or a credential. Every safety gate is the backend's — this client shows what the backend decided.
/// </summary>
public interface IActiveCdcTestsApiService
{
    Task<ActiveCdcReadiness> ReadinessAsync(FrontendAnalysisProfile profile, string integrationId, Guid? snapshotId, CancellationToken ct = default);
    /// <summary>Starts a run. Returns the run (Running, or already Blocked with its reason) or the backend's refusal message.</summary>
    Task<(ActiveCdcRun? Run, string? Error)> StartAsync(FrontendAnalysisProfile profile, ActiveCdcRunRequest request, CancellationToken ct = default);
    Task<ActiveCdcRun?> GetAsync(Guid runId, CancellationToken ct = default);
    Task<bool> CancelAsync(Guid runId, CancellationToken ct = default);
    Task<IReadOnlyList<ActiveCdcRunSummary>> HistoryAsync(string environmentId, string? integrationId, CancellationToken ct = default);
}

public sealed class ActiveCdcTestsApiService(HttpClient http) : IActiveCdcTestsApiService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static string Scope(FrontendAnalysisProfile profile) =>
        $"environmentType={Uri.EscapeDataString(profile.EnvironmentType.ToString())}&targetUrl={Uri.EscapeDataString(profile.TargetUrl ?? "")}";

    public async Task<ActiveCdcReadiness> ReadinessAsync(FrontendAnalysisProfile profile, string integrationId, Guid? snapshotId, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<ActiveCdcReadiness>(
            $"api/active-cdc-tests/readiness?environmentId={Uri.EscapeDataString(profile.Id)}&integrationId={Uri.EscapeDataString(integrationId)}&{Scope(profile)}{(snapshotId is { } id ? $"&snapshotId={id}" : "")}", Json, ct)
        ?? new ActiveCdcReadiness { EnvironmentId = profile.Id, IntegrationId = integrationId };

    public async Task<(ActiveCdcRun? Run, string? Error)> StartAsync(FrontendAnalysisProfile profile, ActiveCdcRunRequest request, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"api/active-cdc-tests/runs?{Scope(profile)}", request, Json, ct);
        if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<ActiveCdcRun>(Json, ct), null);
        try { return (null, (await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct)).GetProperty("message").GetString()); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return (null, $"The backend refused the run (HTTP {(int)response.StatusCode})."); }
    }

    public async Task<ActiveCdcRun?> GetAsync(Guid runId, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/active-cdc-tests/runs/{runId}", ct);
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<ActiveCdcRun>(Json, ct) : null;
    }

    public async Task<bool> CancelAsync(Guid runId, CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"api/active-cdc-tests/runs/{runId}/cancel", null, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<IReadOnlyList<ActiveCdcRunSummary>> HistoryAsync(string environmentId, string? integrationId, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<ActiveCdcRunSummary>>(
            $"api/active-cdc-tests/runs?environmentId={Uri.EscapeDataString(environmentId)}{(integrationId is null ? "" : $"&integrationId={Uri.EscapeDataString(integrationId)}")}", Json, ct) ?? [];
}

/// <summary>Presentation of active CDC runs: tones and ordered stage rows. Never upgrades a state — Not assessed stays Not assessed.</summary>
public static class ActiveCdcPresentation
{
    public static readonly ActiveCdcStepKind[] Order = Enum.GetValues<ActiveCdcStepKind>();

    public static string Tone(ActiveCdcRunStatus status) => status switch
    {
        ActiveCdcRunStatus.Passed => "ready",
        ActiveCdcRunStatus.Partial or ActiveCdcRunStatus.Inconclusive or ActiveCdcRunStatus.Running or ActiveCdcRunStatus.Cancelled => "attention",
        _ => "blocked",
    };

    public static string Tone(ActiveCdcEvidenceState state) => state switch
    {
        ActiveCdcEvidenceState.Observed => "ready",
        ActiveCdcEvidenceState.Error or ActiveCdcEvidenceState.NotAuthorized => "blocked",
        _ => "neutral",
    };

    public static string Tone(ActiveCdcReadinessState state) => state switch
    {
        ActiveCdcReadinessState.Ready => "ready",
        ActiveCdcReadinessState.Blocked => "blocked",
        _ => "neutral",
    };

    /// <summary>Every stage in pipeline order; a stage the run has not reached yet is shown as pending (running) or Not assessed (completed).</summary>
    public static IReadOnlyList<(ActiveCdcStepKind Kind, string Label, string State, string Tone, string Detail, string Source)> Stages(ActiveCdcRun run) =>
        Order.Select(kind => run.Step(kind) is { } s
                ? (kind, ActiveCdcLabels.Step(kind), ActiveCdcLabels.State(s.State), Tone(s.State), s.Detail, s.Source)
                : (kind, ActiveCdcLabels.Step(kind), run.Completed ? "Not assessed" : "Pending", "neutral", run.Completed ? "Not reached in this run." : "Waiting…", ""))
            .ToList();

    public static string Headline(ActiveCdcRun run) => run.Status switch
    {
        ActiveCdcRunStatus.Running => run.SendAttempted ? "Sent — observing read-only evidence" : "Preparing — nothing sent yet",
        ActiveCdcRunStatus.Partial => "Partial — Event Hub accepted the synthetic event; Person persistence not assessed",
        ActiveCdcRunStatus.Blocked => run.SendAttempted ? "Blocked — the send was refused" : "Blocked — nothing was sent",
        ActiveCdcRunStatus.Inconclusive => "Inconclusive — the send outcome is unknown",
        ActiveCdcRunStatus.Cancelled => run.SendAttempted ? "Cancelled — the event had already been sent" : "Cancelled — nothing was sent",
        ActiveCdcRunStatus.Failed => "Failed — Event Hub did not accept the event",
        _ => ActiveCdcLabels.Status(run.Status),
    };
}
