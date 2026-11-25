using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.DeliveryReporting;
using BirkNext.TestPlanning;

namespace BirkNext.Web.Services;

/// <summary>
/// Azure DevOps Test Plans (api/azure-devops/test-plans): destination settings, test plan / suite discovery and confirmed creation, and «Send
/// direkte». Connections are the stored Azure DevOps connections (api/delivery-reporting/azure-devops/connections): one login for BirkNext.
/// Errors come back as a short Norwegian message.
/// </summary>
public interface IAdoTestPlansApiService
{
    Task<(AdoTestPlanSettingsView? View, string? Error)> SettingsAsync(string? projectKey, CancellationToken ct = default);
    Task<(AzureDevOpsConnectionView? Connection, string? Error)> AddConnectionAsync(AzureDevOpsConnectionRequest request, CancellationToken ct = default);
    Task<(AzureDevOpsValidationResult? Result, string? Error)> CheckAsync(Guid connectionId, string? project, CancellationToken ct = default);
    Task<AdoListResult<AdoProjectInfo>> ProjectsAsync(Guid connectionId, CancellationToken ct = default);
    Task<AdoListResult<AdoTestPlanInfo>> PlansAsync(Guid connectionId, string project, CancellationToken ct = default);
    Task<AdoListResult<AdoTestSuiteInfo>> SuitesAsync(Guid connectionId, string project, int planId, CancellationToken ct = default);
    Task<(AdoCreateResult? Result, string? Error)> CreatePlanAsync(AdoCreatePlanRequest request, CancellationToken ct = default);
    Task<(AdoCreateResult? Result, string? Error)> CreateSuiteAsync(AdoCreateSuiteRequest request, CancellationToken ct = default);
    Task<(AdoVerifyResult? Result, string? Error)> VerifyAsync(Guid pendingId, CancellationToken ct = default);
    Task<(AdoSavedDestination? Saved, string? Error)> SaveDefaultAsync(AdoSaveDestinationRequest request, CancellationToken ct = default);
    Task<(AdoTransferPreview? Preview, string? Error)> PreviewAsync(AdoTransferRequest request, CancellationToken ct = default);
    Task<(AdoTransferResult? Result, string? Error)> TransferAsync(AdoTransferRequest request, CancellationToken ct = default);
    Task<(AdoReconcileResult? Result, string? Error)> ReconcileAsync(AdoReconcileRequest request, CancellationToken ct = default);
    /// <summary>Results fetched earlier from Azure DevOps for the plan (read from BirkNext only).</summary>
    Task<(List<AdoCaseResult>? Results, string? Error)> ResultsAsync(string projectKey, Guid draftId, CancellationToken ct = default);
    /// <summary>«Hent resultater fra Azure DevOps»: reads Azure DevOps (read-only) and stores new results locally.</summary>
    Task<(AdoResultsFetchResult? Result, string? Error)> FetchResultsAsync(AdoResultsFetchRequest request, CancellationToken ct = default);
}

public sealed class AdoTestPlansApiService(HttpClient http) : IAdoTestPlansApiService
{
    private const string Base = "api/azure-devops/test-plans";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static string E(string value) => Uri.EscapeDataString(value);

    private sealed record SavedAnswer(AdoSavedDestination? Saved);

    public Task<(AdoTestPlanSettingsView?, string?)> SettingsAsync(string? projectKey, CancellationToken ct = default) =>
        Send<AdoTestPlanSettingsView>(() => http.GetAsync($"{Base}/settings" + (projectKey is { Length: > 0 } k ? $"?projectKey={E(k)}" : ""), ct), ct);

    public Task<(AzureDevOpsConnectionView?, string?)> AddConnectionAsync(AzureDevOpsConnectionRequest request, CancellationToken ct = default) =>
        Send<AzureDevOpsConnectionView>(() => http.PostAsJsonAsync("api/delivery-reporting/azure-devops/connections", request, Json, ct), ct);

    public Task<(AzureDevOpsValidationResult?, string?)> CheckAsync(Guid connectionId, string? project, CancellationToken ct = default) =>
        Send<AzureDevOpsValidationResult>(() => http.PostAsync($"{Base}/connections/{connectionId}/check" + (project is { Length: > 0 } p ? $"?project={E(p)}" : ""), null, ct), ct);

    public Task<AdoListResult<AdoProjectInfo>> ProjectsAsync(Guid connectionId, CancellationToken ct = default) =>
        List<AdoProjectInfo>(() => http.GetAsync($"{Base}/connections/{connectionId}/projects", ct), ct);

    public Task<AdoListResult<AdoTestPlanInfo>> PlansAsync(Guid connectionId, string project, CancellationToken ct = default) =>
        List<AdoTestPlanInfo>(() => http.GetAsync($"{Base}/plans?connectionId={connectionId}&project={E(project)}", ct), ct);

    public Task<AdoListResult<AdoTestSuiteInfo>> SuitesAsync(Guid connectionId, string project, int planId, CancellationToken ct = default) =>
        List<AdoTestSuiteInfo>(() => http.GetAsync($"{Base}/suites?connectionId={connectionId}&project={E(project)}&planId={planId}", ct), ct);

    public Task<(AdoCreateResult?, string?)> CreatePlanAsync(AdoCreatePlanRequest request, CancellationToken ct = default) =>
        Send<AdoCreateResult>(() => http.PostAsJsonAsync($"{Base}/plans", request, Json, ct), ct);

    public Task<(AdoCreateResult?, string?)> CreateSuiteAsync(AdoCreateSuiteRequest request, CancellationToken ct = default) =>
        Send<AdoCreateResult>(() => http.PostAsJsonAsync($"{Base}/suites", request, Json, ct), ct);

    public Task<(AdoVerifyResult?, string?)> VerifyAsync(Guid pendingId, CancellationToken ct = default) =>
        Send<AdoVerifyResult>(() => http.PostAsync($"{Base}/pending/{pendingId}/verify", null, ct), ct);

    public async Task<(AdoSavedDestination?, string?)> SaveDefaultAsync(AdoSaveDestinationRequest request, CancellationToken ct = default)
    {
        var (answer, error) = await Send<SavedAnswer>(() => http.PutAsJsonAsync($"{Base}/default", request, Json, ct), ct);
        return (answer?.Saved, error);
    }

    public Task<(AdoTransferPreview?, string?)> PreviewAsync(AdoTransferRequest request, CancellationToken ct = default) =>
        Send<AdoTransferPreview>(() => http.PostAsJsonAsync($"{Base}/transfer/preview", request, Json, ct), ct);

    public Task<(AdoTransferResult?, string?)> TransferAsync(AdoTransferRequest request, CancellationToken ct = default) =>
        Send<AdoTransferResult>(() => http.PostAsJsonAsync($"{Base}/transfer", request, Json, ct), ct);

    public Task<(AdoReconcileResult?, string?)> ReconcileAsync(AdoReconcileRequest request, CancellationToken ct = default) =>
        Send<AdoReconcileResult>(() => http.PostAsJsonAsync($"{Base}/transfer/reconcile", request, Json, ct), ct);

    public Task<(List<AdoCaseResult>?, string?)> ResultsAsync(string projectKey, Guid draftId, CancellationToken ct = default) =>
        Send<List<AdoCaseResult>>(() => http.GetAsync($"{Base}/results?projectKey={E(projectKey)}&draftId={draftId}", ct), ct);

    public Task<(AdoResultsFetchResult?, string?)> FetchResultsAsync(AdoResultsFetchRequest request, CancellationToken ct = default) =>
        Send<AdoResultsFetchResult>(() => http.PostAsJsonAsync($"{Base}/results/fetch", request, Json, ct), ct);

    private static async Task<AdoListResult<T>> List<T>(Func<Task<HttpResponseMessage>> call, CancellationToken ct)
    {
        var (result, error) = await Send<AdoListResult<T>>(call, ct);
        return result ?? new AdoListResult<T>([], error ?? "Kunne ikke hente listen.");
    }

    private static async Task<(T? Value, string? Error)> Send<T>(Func<Task<HttpResponseMessage>> call, CancellationToken ct)
    {
        try
        {
            using var response = await call();
            if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<T>(Json, ct), null);
            if ((int)response.StatusCode is 401 or 403) return (default, "Du har ikke tilgang til å gjøre dette i BirkNext.");
            string? message = null;
            try { message = (await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct)).GetProperty("message").GetString(); }
            catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException) { }
            return (default, message ?? $"Forespørselen feilet (HTTP {(int)response.StatusCode}).");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            return (default, "Får ikke kontakt med BirkNext-tjenesten. Prøv igjen.");
        }
    }
}

/// <summary>Plain-Norwegian labels shared by the settings section and the transfer panel.</summary>
public static class AdoTestPlansPresentation
{
    private enum Connection { None, Connected, AccessDenied, NeedsCheck }

    private static Connection State(AzureDevOpsConnectionInfo? c) => c switch
    {
        null or { CredentialConfigured: false } => Connection.None,
        { LastValidationStatus: "Connected" } => Connection.Connected,
        { LastValidationStatus: "AccessDenied" } => Connection.AccessDenied,
        _ => Connection.NeedsCheck,
    };

    /// <summary>Connection status words: English in System Settings, Norwegian in Manual Testing.</summary>
    public static string ConnectionState(AzureDevOpsConnectionInfo? c, bool english = false) => (State(c), english) switch
    {
        (Connection.None, false) => "Ikke tilkoblet", (Connection.None, true) => "Not connected",
        (Connection.Connected, false) => "Tilkoblet", (Connection.Connected, true) => "Connected",
        (Connection.AccessDenied, false) => "Tilgang mangler", (Connection.AccessDenied, true) => "Access missing",
        (_, false) => "Tilkobling må kontrolleres", (_, true) => "Connection needs checking",
    };

    public static string ConnectionTone(AzureDevOpsConnectionInfo? c) => State(c) switch { Connection.Connected => "ok", Connection.None => "neutral", _ => "pending" };

    /// <summary>"25.11.2026 kl. 14:05" (Norwegian) or "25 Nov 2026 14:05" (English).</summary>
    public static string WhenEnglish(DateTimeOffset at) => at.ToLocalTime().ToString("d MMM yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    public static string SourceLabel(AdoDestinationSource source) => source switch
    {
        AdoDestinationSource.Project => "Prosjektets standard",
        AdoDestinationSource.System => "Systemstandard",
        AdoDestinationSource.Explicit => "Valgt for denne overføringen",
        _ => "Ikke valgt",
    };

    public static string ActionLabel(AdoTransferAction action) => action switch
    {
        AdoTransferAction.Create => "Opprett nytt",
        AdoTransferAction.Update => "Oppdater eksisterende",
        _ => "Hopp over",
    };

    public static string OutcomeLabel(AdoTransferOutcome outcome) => outcome switch
    {
        AdoTransferOutcome.Created => "Opprettet",
        AdoTransferOutcome.Updated => "Oppdatert",
        AdoTransferOutcome.Skipped => "Hoppet over",
        AdoTransferOutcome.Failed => "Kunne ikke overføre",
        AdoTransferOutcome.Uncertain => "Usikkert – kontroller",
        AdoTransferOutcome.NotInSuite => "Ikke lagt i testgruppen",
        _ => "Må kontrolleres",
    };

    public static string OutcomeTone(AdoTransferOutcome outcome) => outcome switch
    {
        AdoTransferOutcome.Created or AdoTransferOutcome.Updated => "ok",
        AdoTransferOutcome.Skipped => "neutral",
        AdoTransferOutcome.Failed => "bad",
        _ => "pending",
    };

    /// <summary>History status words. A transfer is never a test result.</summary>
    public static string HistoryLabel(AdoExportHistoryEntry h) => h.Format == "Direct"
        ? h.Status switch { "Transferred" => "Overført", "Partial" => "Delvis overført", "NeedsReview" => "Må kontrolleres", _ => "Kunne ikke overføre" }
        : "Fil laget";

    public static string HistoryTone(AdoExportHistoryEntry h) => h.Format == "Direct"
        ? h.Status switch { "Transferred" => "ok", "Partial" or "NeedsReview" => "pending", _ => "bad" }
        : "neutral";

    public static string ResultTone(string outcome) => outcome switch { "Passed" => "ok", "Failed" => "bad", "Blocked" => "warn", _ => "muted" };

    public static string When(DateTimeOffset at) => at.ToLocalTime().ToString("dd.MM.yyyy 'kl.' HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>"Rot › Gruppe › Undergruppe" for a suite (names only).</summary>
    public static string SuitePath(IReadOnlyList<AdoTestSuiteInfo> suites, int suiteId)
    {
        var names = new List<string>();
        var current = suites.FirstOrDefault(s => s.Id == suiteId);
        for (var guard = 0; current is not null && guard < 20; guard++)
        {
            names.Insert(0, current.Name);
            current = current.ParentId is { } p ? suites.FirstOrDefault(s => s.Id == p) : null;
        }
        return string.Join(" › ", names);
    }

    /// <summary>Suites in tree order with their depth (children under their parent, alphabetical).</summary>
    public static List<(AdoTestSuiteInfo Suite, int Depth)> Tree(IReadOnlyList<AdoTestSuiteInfo> suites)
    {
        var result = new List<(AdoTestSuiteInfo, int)>();
        var ids = suites.Select(s => s.Id).ToHashSet();
        void Add(AdoTestSuiteInfo s, int depth)
        {
            if (depth > 20 || result.Any(r => r.Item1.Id == s.Id)) return;
            result.Add((s, depth));
            foreach (var child in suites.Where(c => c.ParentId == s.Id).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)) Add(child, depth + 1);
        }
        foreach (var root in suites.Where(s => s.ParentId is null || !ids.Contains(s.ParentId.Value)).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)) Add(root, 0);
        return result;
    }
}
