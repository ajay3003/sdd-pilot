using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.ExecutionExplorer;

namespace BirkNext.Web.Services;

/// <summary>Filters of the unified run history (all optional; sent to the backend, which filters and pages).</summary>
public sealed record ExplorerFilter
{
    public string? EnvironmentId { get; init; }
    public string? Project { get; init; }
    public bool Unattributed { get; init; }
    public string? Search { get; init; }
    public IReadOnlyList<ExplorerSource> Sources { get; init; } = [];
    public IReadOnlyList<ExplorerCategory> Categories { get; init; } = [];
    public IReadOnlyList<ExplorerOutcome> Outcomes { get; init; } = [];
    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }
    public bool ProtectedOnly { get; init; }
    public EvidenceCompleteness? Evidence { get; init; }
    public bool IncludeStatic { get; init; }
    public int Skip { get; init; }
    public int Take { get; init; } = 25;
}

/// <summary>Test Execution &amp; Evidence: the read-only unified projection. Nothing here executes or changes evidence.</summary>
public interface IExecutionExplorerApiService
{
    Task<(ExplorerOverview? Overview, string? Error)> OverviewAsync(ExplorerFilter filter, CancellationToken ct = default);
    Task<(ExplorerPage? Page, string? Error)> RunsAsync(ExplorerFilter filter, CancellationToken ct = default);
    Task<(ExplorerRunDetail? Detail, string? Error)> RunAsync(ExplorerSource source, string id, CancellationToken ct = default);
    Task<(ExplorerComparison? Comparison, string? Error)> CompareAsync(ExplorerSource source, string baseline, string current, CancellationToken ct = default);
    string ReportUrl(ExplorerSource source, string id);
}

public sealed class ExecutionExplorerApiService(HttpClient http) : IExecutionExplorerApiService
{
    public const string Title = "Test Execution & Evidence";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    internal static string Query(ExplorerFilter f, bool paging)
    {
        var q = new List<string>();
        void Add(string k, string? v) { if (!string.IsNullOrWhiteSpace(v)) q.Add($"{k}={Uri.EscapeDataString(v)}"); }
        Add("environmentId", f.EnvironmentId);
        Add("project", f.Project);
        if (f.Unattributed) q.Add("unattributed=true");
        if (f.From is { } from) Add("from", from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime().ToString("O"));
        if (f.To is { } to) Add("to", to.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Local).ToUniversalTime().ToString("O"));
        if (!paging) return string.Join('&', q);
        Add("search", f.Search);
        Add("sources", string.Join(',', f.Sources));
        Add("categories", string.Join(',', f.Categories));
        Add("outcomes", string.Join(',', f.Outcomes));
        if (f.ProtectedOnly) q.Add("protected=true");
        if (f.Evidence is { } e) Add("evidence", e.ToString());
        if (f.IncludeStatic) q.Add("static=true");
        q.Add($"skip={Math.Max(0, f.Skip)}");
        q.Add($"take={Math.Clamp(f.Take, 1, 100)}");
        return string.Join('&', q);
    }

    public Task<(ExplorerOverview? Overview, string? Error)> OverviewAsync(ExplorerFilter filter, CancellationToken ct = default) =>
        GetAsync<ExplorerOverview>($"api/execution-explorer/overview?{Query(filter, false)}", ct);

    public Task<(ExplorerPage? Page, string? Error)> RunsAsync(ExplorerFilter filter, CancellationToken ct = default) =>
        GetAsync<ExplorerPage>($"api/execution-explorer/runs?{Query(filter, true)}", ct);

    public Task<(ExplorerRunDetail? Detail, string? Error)> RunAsync(ExplorerSource source, string id, CancellationToken ct = default) =>
        GetAsync<ExplorerRunDetail>($"api/execution-explorer/runs/{source}/{Uri.EscapeDataString(id)}", ct);

    public Task<(ExplorerComparison? Comparison, string? Error)> CompareAsync(ExplorerSource source, string baseline, string current, CancellationToken ct = default) =>
        GetAsync<ExplorerComparison>($"api/execution-explorer/compare?source={source}&baseline={Uri.EscapeDataString(baseline)}&current={Uri.EscapeDataString(current)}", ct);

    public string ReportUrl(ExplorerSource source, string id) => new Uri(http.BaseAddress!, $"api/execution-explorer/runs/{source}/{Uri.EscapeDataString(id)}/report").ToString();

    private async Task<(T? Body, string? Error)> GetAsync<T>(string path, CancellationToken ct) where T : class
    {
        try
        {
            using var response = await http.GetAsync(path, ct);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                return (null, $"{Title}: you do not have permission to read these results (HTTP {(int)response.StatusCode}).");
            if (!response.IsSuccessStatusCode) return (null, response.StatusCode == System.Net.HttpStatusCode.NotFound ? "Not found." : $"{Title}: the request failed ({(int)response.StatusCode}).");
            return (await response.Content.ReadFromJsonAsync<T>(Json, ct), null);
        }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e))
        { return (null, BackendRequestClassifier.FromException(Title, e, $"GET {path}", ct).UserMessage); }
        catch (JsonException)
        { return (null, $"{Title}: the stored result could not be read (unexpected format)."); }
    }
}
