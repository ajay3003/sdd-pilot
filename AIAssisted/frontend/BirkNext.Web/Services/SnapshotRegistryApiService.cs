using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.Snapshots;

namespace BirkNext.Web.Services;

/// <summary>Shared Snapshot Registry calls (api/snapshots): metadata, lineage, pointers and retention only — never a payload.</summary>
public interface ISnapshotRegistryApiService
{
    Task<(SnapshotListPage? Page, string? Error)> ListAsync(string? project, SnapshotType? type, string? module, SnapshotProcessingStatus? status, string? sort, int skip, int take, CancellationToken ct = default);
    Task<(SnapshotLineageView? Lineage, string? Error)> LineageAsync(SnapshotType type, string id, CancellationToken ct = default);
    /// <summary>The listing with every filter the registry supports (search by label, created date range) — paged on the server, counts true.</summary>
    Task<(SnapshotListPage? Page, string? Error)> QueryAsync(SnapshotQuery query, CancellationToken ct = default) =>
        ListAsync(query.Project, query.Type, null, query.Status, query.Sort, query.Skip, query.Take, ct);
    /// <summary>What references a Source Analysis snapshot (checked stores; <see cref="SnapshotReferenceReport.NotChecked"/> names the others).</summary>
    Task<(SnapshotReferenceReport? Report, string? Error)> SourceAnalysisReferencesAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult<(SnapshotReferenceReport?, string?)>((null, "Not available."));
    Task<(IReadOnlyList<SnapshotPointerView> Pointers, string? Error)> PointersAsync(string? project, SnapshotType type, string module, CancellationToken ct = default);
    Task<(SnapshotDeletionCheck? Check, string? Error)> DeletionCheckAsync(SnapshotType type, string id, CancellationToken ct = default);
    Task<string?> SetPointerAsync(SnapshotPointerRequest request, CancellationToken ct = default);
    Task<string?> SetRetentionAsync(SnapshotType type, string id, SnapshotRetentionClass retention, CancellationToken ct = default);
}

public sealed class SnapshotRegistryApiService(HttpClient http) : ISnapshotRegistryApiService
{
    private const string Title = "Snapshots";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<(SnapshotListPage?, string?)> ListAsync(string? project, SnapshotType? type, string? module, SnapshotProcessingStatus? status, string? sort, int skip, int take, CancellationToken ct = default) =>
        GetAsync<SnapshotListPage>("api/snapshots?" + string.Join("&", new[]
        {
            $"skip={skip}", $"take={take}", project is null ? null : $"project={Uri.EscapeDataString(project)}", type is null ? null : $"type={type}",
            module is null ? null : $"module={Uri.EscapeDataString(module)}", status is null ? null : $"status={status}", sort is null ? null : $"sort={sort}",
        }.OfType<string>()), ct);

    public Task<(SnapshotListPage?, string?)> QueryAsync(SnapshotQuery q, CancellationToken ct = default) =>
        GetAsync<SnapshotListPage>("api/snapshots?" + string.Join("&", new[]
        {
            $"skip={q.Skip}", $"take={q.Take}", q.Project is null ? null : $"project={Uri.EscapeDataString(q.Project)}", q.Type is null ? null : $"type={q.Type}",
            q.Status is null ? null : $"status={q.Status}", q.Sort is null ? null : $"sort={q.Sort}", q.Search is null ? null : $"search={Uri.EscapeDataString(q.Search)}",
            q.From is null ? null : $"from={Uri.EscapeDataString(q.From.Value.ToString("O"))}", q.To is null ? null : $"to={Uri.EscapeDataString(q.To.Value.ToString("O"))}",
        }.OfType<string>()), ct);

    public Task<(SnapshotReferenceReport?, string?)> SourceAnalysisReferencesAsync(Guid id, CancellationToken ct = default) =>
        GetAsync<SnapshotReferenceReport>($"api/snapshots/source-analysis/{id:D}/references", ct);

    public Task<(SnapshotLineageView?, string?)> LineageAsync(SnapshotType type, string id, CancellationToken ct = default) =>
        GetAsync<SnapshotLineageView>($"api/snapshots/{type}/{Uri.EscapeDataString(id)}/lineage?depth=3", ct);

    public async Task<(IReadOnlyList<SnapshotPointerView>, string?)> PointersAsync(string? project, SnapshotType type, string module, CancellationToken ct = default)
    {
        var (items, error) = await GetAsync<List<SnapshotPointerView>>($"api/snapshots/pointers?type={type}&module={Uri.EscapeDataString(module)}{(project is null ? "" : $"&project={Uri.EscapeDataString(project)}")}", ct);
        return (items ?? [], error);
    }

    public Task<(SnapshotDeletionCheck?, string?)> DeletionCheckAsync(SnapshotType type, string id, CancellationToken ct = default) =>
        GetAsync<SnapshotDeletionCheck>($"api/snapshots/{type}/{Uri.EscapeDataString(id)}/deletion-check", ct);

    public Task<string?> SetPointerAsync(SnapshotPointerRequest request, CancellationToken ct = default) => PutAsync("api/snapshots/pointers", request, ct);

    public Task<string?> SetRetentionAsync(SnapshotType type, string id, SnapshotRetentionClass retention, CancellationToken ct = default) =>
        PutAsync($"api/snapshots/{type}/{Uri.EscapeDataString(id)}/retention", new SnapshotRetentionRequest(retention), ct);

    private async Task<(T?, string?)> GetAsync<T>(string url, CancellationToken ct) where T : class
    {
        try
        {
            using var response = await http.GetAsync(url, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return (null, null);
            return response.IsSuccessStatusCode ? (await response.Content.ReadFromJsonAsync<T>(Json, ct), null) : (null, await MessageAsync(response, ct));
        }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e))
        { return (null, BackendRequestClassifier.FromException(Title, e, $"GET {url}", ct).UserMessage); }
    }

    private async Task<string?> PutAsync(string url, object body, CancellationToken ct)
    {
        try
        {
            using var response = await http.PutAsJsonAsync(url, body, Json, ct);
            return response.IsSuccessStatusCode ? null : await MessageAsync(response, ct);
        }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e))
        { return BackendRequestClassifier.FromException(Title, e, $"PUT {url}", ct).UserMessage; }
    }

    private static async Task<string> MessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("message", out var m) && m.GetString() is { Length: > 0 } text) return text;
        }
        catch (JsonException) { }
        return $"{Title} request failed ({(int)response.StatusCode}).";
    }
}

/// <summary>Snapshot metadata through the shared vocabulary: processing status as a status, type/retention/pointers as MetaBadges.</summary>
/// <summary>One listing request: every filter is applied by the registry before paging.</summary>
public sealed record SnapshotQuery(string? Project, SnapshotType? Type, SnapshotProcessingStatus? Status, string? Search, DateTimeOffset? From, DateTimeOffset? To,
    string? Sort, int Skip, int Take);

public static class SnapshotPresentation
{
    public static SemanticStatus Status(SnapshotProcessingStatus status) => status switch
    {
        SnapshotProcessingStatus.Completed => SemanticStatus.Completed,
        SnapshotProcessingStatus.Partial => SemanticStatus.Partial,
        SnapshotProcessingStatus.Failed => SemanticStatus.Failed,
        SnapshotProcessingStatus.Running => SemanticStatus.Assessing,
        _ => SemanticStatus.Unknown,
    };

    public static string Retention(SnapshotRetentionClass retention) => retention switch
    {
        SnapshotRetentionClass.FrozenReportEvidence => "Frozen report evidence",
        _ => retention.ToString(),
    };

    public static string Pointer(SnapshotPointerKind kind) => kind switch
    {
        SnapshotPointerKind.LatestCreated => "Latest",
        SnapshotPointerKind.LatestCompleted => "Latest completed",
        SnapshotPointerKind.CurrentSelected => "Current",
        _ => "Approved",
    };

    public static string Lineage(SnapshotLineageKind kind) => kind switch
    {
        SnapshotLineageKind.ImportedFrom => "imported from",
        SnapshotLineageKind.DerivedFrom => "derived from",
        SnapshotLineageKind.ComparedAgainst => "compared against",
        SnapshotLineageKind.InterpretedUsing => "interpreted from",
        SnapshotLineageKind.GeneratedReportFrom => "report generated from",
        _ => "supersedes",
    };

    public static string Type(SnapshotType type) => type switch
    {
        SnapshotType.SourceAnalysis => "Source Analysis",
        SnapshotType.SourceImport => "Project import",
        SnapshotType.GeneratedReport => "Report",
        _ => type.ToString(),
    };
}
