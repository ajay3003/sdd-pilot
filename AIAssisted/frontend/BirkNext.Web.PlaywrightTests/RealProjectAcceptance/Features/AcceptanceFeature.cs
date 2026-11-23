using System.Text.Json;
using BirkNext.RealProjectAcceptance;
using Microsoft.Playwright;

namespace BirkNext.Web.PlaywrightTests.RealProjectAcceptance.Features;

/// <summary>
/// Base for browser-driven acceptance features: navigates the production route, records page errors and Sample Project coupling as
/// defects, takes a screenshot when a feature fails, and gives access to the run-shared Source Analysis snapshot.
/// </summary>
public abstract class AcceptanceFeature : IRealProjectAcceptanceFeature
{
    public const string ImportFeatureId = "project-import";
    public const string SnapshotKey = "source-snapshot";

    public abstract string FeatureId { get; }
    public abstract string DisplayName { get; }
    public abstract string Area { get; }
    public abstract string Route { get; }
    public virtual AcceptanceType AcceptanceType => AcceptanceType.Source;
    public virtual AcceptanceMode MinimumMode => AcceptanceMode.Standard;
    public virtual IReadOnlyList<string> RequiredEvidence => ["imported project"];
    public virtual IReadOnlyList<string> DependsOn => [ImportFeatureId];
    public virtual string? CanRun(RealProjectAcceptanceContext context) => null;

    public async Task<FeatureAcceptanceResult> ExecuteAsync(RealProjectAcceptanceContext context, CancellationToken ct)
    {
        var session = context.SessionAs<PlaywrightAcceptanceSession>();
        var errorsBefore = session.PageErrors.Count;
        var failedBefore = session.FailedRequests.Count;
        var builder = new FeatureResultBuilder();
        try
        {
            await RunAsync(context, session, builder, ct);
        }
        catch (TimeoutException ex)
        {
            builder.Execution = ExecutionStatus.Failed;
            builder.Evidence = EvidenceState.NotVerified;
            builder.Defect("timeout", ex.Message.Split('\n')[0]);
        }
        catch (PlaywrightException ex)
        {
            builder.Execution = ExecutionStatus.Failed;
            builder.Evidence = EvidenceState.NotVerified;
            builder.Defect("browser-error", ex.Message.Split('\n')[0]);
        }
        foreach (var error in session.PageErrors.Skip(errorsBefore).Distinct())
            builder.Defect("page-error", error);
        foreach (var failure in session.FailedRequests.Skip(failedBefore).Distinct())
            builder.Warn("request-failed", failure);
        if (builder.Browser == BrowserState.Rendered && await session.SampleProjectCouplingAsync() is { } coupling)
            builder.Defect("sample-project-coupling", $"The page says \"{coupling}\" although a valid imported project is the current workspace.");
        if (builder.Findings.Any(f => f.Severity == AcceptanceFindingSeverity.Defect))
        {
            try { builder.Screenshots.Add(await session.ScreenshotAsync($"failure-{FeatureId}")); } catch (PlaywrightException) { }
        }
        return builder.Build();
    }

    protected abstract Task RunAsync(RealProjectAcceptanceContext context, PlaywrightAcceptanceSession session, FeatureResultBuilder result, CancellationToken ct);

    /// <summary>Navigates (client-side) to this feature's route and marks the browser state.</summary>
    protected async Task OpenAsync(PlaywrightAcceptanceSession session, FeatureResultBuilder result, string? route = null)
    {
        await session.NavigateAsync(route ?? Route);
        result.Browser = BrowserState.Rendered;
    }

    /// <summary>The Source Analysis snapshot of this run, fetched once from the backend and reused by every feature.</summary>
    protected static async Task<JsonElement?> SnapshotAsync(RealProjectAcceptanceContext context, PlaywrightAcceptanceSession session, CancellationToken ct)
    {
        if (context.Shared.TryGetValue(SnapshotKey, out var cached)) return (JsonElement)cached;
        if (context.Workspace.SourceSnapshotId is not { } id) return null;
        // The listing carries summaries only (2026-11); the snapshot with its evidence is fetched by id, as the pages do.
        if (await session.GetJsonAsync($"api/source-analysis/{Uri.EscapeDataString(id)}", ct) is not { ValueKind: JsonValueKind.Object } snapshot) return null;
        context.Shared[SnapshotKey] = snapshot.Clone();
        return snapshot.Clone();
    }

    /// <summary>True when the run's snapshot is listed by Source Analysis (the summary listing the pages read).</summary>
    protected static async Task<bool> SnapshotListedAsync(RealProjectAcceptanceContext context, PlaywrightAcceptanceSession session, CancellationToken ct) =>
        context.Workspace.SourceSnapshotId is { } id && await session.GetJsonAsync("api/source-analysis", ct) is { ValueKind: JsonValueKind.Array } list
        && list.EnumerateArray().Any(s => string.Equals(Str(s, "id"), id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The import's document roles and their states (e.g. Specification → "Not detected", Plan → "11 candidates").</summary>
    public const string ImportRolesKey = "import-roles";

    /// <summary>True when the import found at least one document (or candidate set) for the role.</summary>
    protected static bool RoleImported(RealProjectAcceptanceContext context, string role) =>
        context.Shared.TryGetValue(ImportRolesKey, out var raw) && raw is IReadOnlyDictionary<string, string> roles
        && roles.TryGetValue(role, out var state) && !System.Text.RegularExpressions.Regex.IsMatch(state, "not|missing|absent|none", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>An export behind an export menu (toggle, then the HTML option), as ExportMenu renders it.</summary>
    protected static async Task ExportMenuAsync(PlaywrightAcceptanceSession session, FeatureResultBuilder result, string testId, RealProjectAcceptanceContext context, bool expectIdentity = true)
    {
        var toggle = session.Page.Locator($"[data-testid={testId}-toggle]");
        if (await toggle.CountAsync() > 0 && await toggle.First.IsEnabledAsync()) await toggle.First.ClickAsync();
        await ExportAsync(session, result, session.Page.Locator($"[data-testid={testId}-html]"), context, expectIdentity);
    }

    /// <summary>Records which snapshot/project a feature actually used, for the cross-feature consistency check.</summary>
    protected static void RecordIdentity(RealProjectAcceptanceContext context, string featureId, string kind, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        context.Shared[$"identity:{kind}:{featureId}"] = value;
    }

    /// <summary>Exports are tested with the real result: the download must happen, carry content, and leak no credential/PII pattern.</summary>
    protected static async Task ExportAsync(PlaywrightAcceptanceSession session, FeatureResultBuilder result, ILocator trigger, RealProjectAcceptanceContext context, bool expectIdentity = true)
    {
        if (await trigger.CountAsync() == 0 || !await trigger.First.IsEnabledAsync())
        {
            result.Export = ExportState.NotSupported;
            result.Note("Export not offered in this state.");
            return;
        }
        var download = await session.CaptureDownloadAsync(() => trigger.First.ClickAsync());
        if (download is not { } file || file.Content.Length < 200)
        {
            result.Export = ExportState.Failed;
            result.Defect("export-failed", "The export did not produce a file.");
            return;
        }
        result.Export = ExportState.Exported;
        result.Observe("export", $"{file.FileName} ({file.Content.Length:N0} chars)");
        if (AcceptanceReportWriter.Redact(file.Content) != file.Content)
            result.Defect("export-sensitive-value", "The export contains a credential/secret/national-id pattern.");
        if (expectIdentity && context.Workspace.ProjectName is { Length: > 0 } project && !file.Content.Contains(project, StringComparison.OrdinalIgnoreCase)
            && !(context.Preparation.Fingerprint?.FileName is { } archive && file.Content.Contains(archive, StringComparison.OrdinalIgnoreCase)))
            result.Warn("export-without-identity", "The export does not name the project or archive it was produced from.");
    }

    protected static string? Str(JsonElement e, params string[] path)
    {
        var current = e;
        foreach (var p in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !TryProperty(current, p, out current)) return null;
        }
        return current.ValueKind switch { JsonValueKind.String => current.GetString(), JsonValueKind.Number => current.GetRawText(), JsonValueKind.True => "true", JsonValueKind.False => "false", _ => null };
    }

    protected static IEnumerable<JsonElement> Arr(JsonElement e, params string[] path)
    {
        var current = e;
        foreach (var p in path)
            if (current.ValueKind != JsonValueKind.Object || !TryProperty(current, p, out current)) return [];
        return current.ValueKind == JsonValueKind.Array ? current.EnumerateArray().ToList() : [];
    }

    protected static bool TryProperty(JsonElement e, string name, out JsonElement value)
    {
        foreach (var prop in e.EnumerateObject())
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase)) { value = prop.Value; return true; }
        value = default;
        return false;
    }

    protected static int Number(string text) => int.TryParse(new string(text.TakeWhile(c => char.IsDigit(c) || c == ',' || c == ' ').Where(char.IsDigit).ToArray()), out var n) ? n : 0;
}
