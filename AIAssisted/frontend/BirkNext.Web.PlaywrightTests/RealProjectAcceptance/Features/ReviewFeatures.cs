using BirkNext.RealProjectAcceptance;
using Microsoft.Playwright;

namespace BirkNext.Web.PlaywrightTests.RealProjectAcceptance.Features;

/// <summary>Document Quality Review runs over the imported documents and reports how many packs were actually assessed.</summary>
public sealed class DocumentQualityReviewFeature : AcceptanceFeature
{
    public override string FeatureId => "document-quality-review";
    public override string DisplayName => "Document Quality Review";
    public override string Area => "Document Review";
    public override string Route => "/quality/document";

    protected override async Task RunAsync(RealProjectAcceptanceContext context, PlaywrightAcceptanceSession session, FeatureResultBuilder result, CancellationToken ct)
    {
        await OpenAsync(session, result);
        if (await session.WaitAnyAsync(60_000, "[data-testid=qr-context]", "[data-testid=qr-no-workspace]") != "[data-testid=qr-context]")
        {
            result.Defect("quality-review-no-workspace", "Document Quality Review does not see the imported workspace.");
            result.Evidence = EvidenceState.NotVerified;
            return;
        }
        var contextText = await session.TextAsync("[data-testid=qr-context]");
        result.Observe("context", contextText.Replace('\n', ' '));
        var run = session.Page.GetByRole(AriaRole.Button, new() { Name = "Run Document Quality Review" });
        if (await run.CountAsync() == 0 && await session.CountAsync(".qr-input-panel-toggle") > 0)
            await session.Page.Locator(".qr-input-panel-toggle").First.ClickAsync();
        if (await run.CountAsync() == 0 || !await run.First.IsEnabledAsync())
        {
            var reason = await session.TextAsync("[data-testid=qr-run-disabled-reason]");
            result.Execution = ExecutionStatus.Blocked;
            result.Evidence = EvidenceState.NotVerified;
            result.Defect("quality-review-not-runnable", $"The review cannot be run for the imported documents: {reason}");
            return;
        }
        await run.First.ClickAsync();
        await session.Page.Locator("section.qr-review-summary").WaitForAsync(new() { Timeout = 300_000 });
        var packs = await session.TextAsync("[data-testid=qr-assessed-packs]");
        var evaluated = await session.TextAsync("[data-testid=qr-requirements-evaluated]");
        result.Observe("assessed packs", packs).Observe("requirements evaluated", evaluated);
        var assessed = Number(packs.TrimStart('('));
        result.DataFromCount(assessed, DataState.NotAssessed);
        if (assessed == 0) { result.Evidence = EvidenceState.NotVerified; result.Warn("quality-review-nothing-assessed", "No quality pack was assessed for the imported documents."); }
        await ExportAsync(session, result, session.Page.GetByRole(AriaRole.Button, new() { Name = "Export HTML" }), context);
    }
}

/// <summary>Requirements Traceability is derived automatically from the imported documents; Plan→Tasks is honestly Not assessed.</summary>
public sealed class RequirementsTraceabilityFeature : AcceptanceFeature
{
    public override string FeatureId => "requirements-traceability";
    public override string DisplayName => "Requirements Traceability";
    public override string Area => "Traceability";
    public override string Route => "/artifact-traceability";

    protected override async Task RunAsync(RealProjectAcceptanceContext context, PlaywrightAcceptanceSession session, FeatureResultBuilder result, CancellationToken ct)
    {
        await OpenAsync(session, result);
        var state = await session.WaitAnyAsync(120_000, ".at-health-bar", "[data-testid=at-no-documents]", "[data-testid=at-selection-required]");
        var workspace = await session.TextAsync("[data-testid=at-workspace-context]");
        result.Observe("workspace context", workspace.Replace('\n', ' ')).Observe("state", state);
        if (state == "[data-testid=at-selection-required]")
        {
            result.Execution = ExecutionStatus.Partial;
            result.Evidence = EvidenceState.PartiallyVerified;
            result.Note("Several candidate artifacts per role: traceability waits for an explicit choice (by design).");
            result.Data = DataState.RealDataObserved;
            return;
        }
        if (state != ".at-health-bar")
        {
            result.Defect("traceability-no-documents", "Requirements Traceability reports no documents for the imported project.");
            result.Evidence = EvidenceState.NotVerified;
            return;
        }
        var metrics = await session.Page.Locator(".at-health-bar > *").AllInnerTextsAsync();
        result.Observe("metrics", string.Join(" | ", metrics.Select(m => m.Replace('\n', ' '))));
        if (!metrics.Any(m => m.Contains("Not assessed", StringComparison.OrdinalIgnoreCase) && m.Contains("Plan", StringComparison.OrdinalIgnoreCase)))
            result.Warn("traceability-plan-tasks", "Plan to Tasks is not labelled Not assessed.");
        result.Data = DataState.RealDataObserved;
        result.Provenance = workspace.Contains("Import", StringComparison.OrdinalIgnoreCase) ? ProvenanceState.Traced : ProvenanceState.PartiallyTraced;
        await ExportAsync(session, result, session.Page.GetByRole(AriaRole.Button, new() { Name = "Export HTML" }), context);
    }
}

/// <summary>Implementation Review (task ↔ spec alignment) over the imported documents: results are counted, never presented as coverage.</summary>
public sealed class ImplementationReviewFeature : AcceptanceFeature
{
    public override string FeatureId => "implementation-review";
    public override string DisplayName => "Implementation Review";
    public override string Area => "Traceability";
    public override string Route => "/task-alignment";

    protected override async Task RunAsync(RealProjectAcceptanceContext context, PlaywrightAcceptanceSession session, FeatureResultBuilder result, CancellationToken ct)
    {
        await OpenAsync(session, result);
        var state = await session.WaitAnyAsync(120_000, "[data-testid=ir-status]", "[data-testid=ir-no-project]", "[data-testid=ir-no-tasks]", "[data-testid=ir-prerun]");
        result.Observe("state", state);
        if (state == "[data-testid=ir-no-project]") { result.Defect("implementation-review-no-project", "Implementation Review does not see the imported project."); result.Evidence = EvidenceState.NotVerified; return; }
        if (state == "[data-testid=ir-no-tasks]") { result.Data = DataState.NoApplicableData; result.Note("The imported project has no tasks artifact."); return; }
        await session.WaitAnyAsync(180_000, "[data-testid=ir-card-analyzed]", "[data-testid=ir-zero-findings]");
        var currentness = await session.AttrAsync("[data-testid=ir-status]", "data-state");
        var analyzed = await session.TextAsync("[data-testid=ir-card-analyzed] .ir-card-value");
        var rows = await session.CountAsync("[data-testid=ir-row]");
        result.Observe("currentness", currentness).Observe("tasks analyzed", analyzed).Observe("result rows", rows);
        if (currentness == "NotRun") result.Warn("implementation-review-not-run", "Implementation Review did not analyse the imported tasks automatically.");
        result.DataFromCount(Number(analyzed) + rows, DataState.NotAssessed);
        await ExportAsync(session, result, session.Page.Locator("[data-testid=ir-export]"), context);
    }
}

/// <summary>
/// Implementation Evidence Review binds the run's Source Analysis snapshot (workspace-level, no Target Environment). The listing must
/// include the import's snapshot; binding explicit CodeLinks is a BirkNext-local operation (no external write).
/// </summary>
public sealed class ImplementationEvidenceReviewFeature : AcceptanceFeature
{
    public override string FeatureId => "implementation-evidence-review";
    public override string DisplayName => "Implementation Evidence Review";
    public override string Area => "Traceability";
    public override string Route => "/implementation-review";
    public override IReadOnlyList<string> RequiredEvidence => ["imported project", "Source Analysis snapshot"];

    protected override async Task RunAsync(RealProjectAcceptanceContext context, PlaywrightAcceptanceSession session, FeatureResultBuilder result, CancellationToken ct)
    {
        await OpenAsync(session, result);
        var load = session.Page.Locator("[data-testid=ier-load-snapshots]");
        await load.WaitForAsync();
        if (!await load.IsEnabledAsync())
        {
            result.Defect("evidence-review-load-gated", "Loading Source Analysis snapshots is disabled although the workspace has a snapshot that needs no environment.");
            result.Evidence = EvidenceState.NotVerified;
            return;
        }
        await load.ClickAsync();
        await session.WaitAnyAsync(60_000, "[data-testid=ier-source-snapshot]", "[data-testid=ier-source-message]");
        var message = await session.TextAsync("[data-testid=ier-source-message]");
        var options = await session.Page.Locator("[data-testid=ier-source-snapshot] option").EvaluateAllAsync<string[]>("els => els.map(e => e.value)");
        result.Observe("message", message).Observe("snapshots listed", options.Length);
        if (context.Workspace.SourceSnapshotId is { } id && !options.Any(o => string.Equals(o, id, StringComparison.OrdinalIgnoreCase)))
        {
            result.Defect("evidence-review-snapshot-missing", "The import's Source Analysis snapshot is not offered for binding.");
            result.Provenance = ProvenanceState.Missing;
            return;
        }
        await session.Page.Locator("[data-testid=ier-source-snapshot]").SelectOptionAsync(context.Workspace.SourceSnapshotId!);
        RecordIdentity(context, FeatureId, "snapshot", context.Workspace.SourceSnapshotId);
        var bind = session.Page.Locator("[data-testid=ier-bind-codelinks]");
        if (await bind.IsEnabledAsync())
        {
            await bind.ClickAsync();
            await session.Page.WaitForFunctionAsync("m => { const e = document.querySelector('[data-testid=ier-source-message]'); return e && e.innerText !== m; }", message, new() { Timeout = 60_000 });
            var bound = await session.TextAsync("[data-testid=ier-source-message]");
            result.Observe("binding", bound);
            result.DataFromCount(bound.StartsWith("Bound", StringComparison.OrdinalIgnoreCase) ? Math.Max(1, Number(bound["Bound ".Length..])) : 0, DataState.NoApplicableData);
            if (bound.StartsWith("Bound 0", StringComparison.Ordinal)) result.Note("The source declares no explicit CodeLinks; 0 bound is a real empty result, not coverage.");
        }
        else
        {
            result.Execution = ExecutionStatus.Partial;
            result.Evidence = EvidenceState.PartiallyVerified;
            result.Note("Binding needs a named project in the lifecycle; snapshot listing verified only.");
            result.Data = DataState.RealDataObserved;
        }
        result.Provenance = ProvenanceState.Traced;
    }
}

/// <summary>A page that only needs to load against the imported project (no project-specific data contract to check).</summary>
public sealed class RenderOnlyFeature(string featureId, string displayName, string area, string route, string readySelector, AcceptanceType type = AcceptanceType.Navigation, string? note = null, ExpectedDataPresence outcome = ExpectedDataPresence.Unspecified) : AcceptanceFeature
{
    public override string FeatureId => featureId;
    public override string DisplayName => displayName;
    public override string Area => area;
    public override string Route => route;
    public override AcceptanceType AcceptanceType => type;

    protected override async Task RunAsync(RealProjectAcceptanceContext context, PlaywrightAcceptanceSession session, FeatureResultBuilder result, CancellationToken ct)
    {
        await OpenAsync(session, result);
        await session.Page.Locator(readySelector).First.WaitForAsync(new() { State = WaitForSelectorState.Attached });
        await session.Page.WaitForTimeoutAsync(1000);
        var heading = await session.TextAsync("main h1, h1");
        result.Observe("heading", heading);
        if (note is not null) result.Note(note);
        switch (outcome)
        {
            case ExpectedDataPresence.NotApplicable:
                result.Execution = ExecutionStatus.NotApplicable; result.Evidence = EvidenceState.NotApplicable; result.Data = DataState.NoApplicableData; break;
            case ExpectedDataPresence.RuntimeNotVerified:
                result.Evidence = EvidenceState.NotVerified; result.Data = DataState.NotAssessed; break;
            default:
                result.Data = DataState.NoApplicableData; break;
        }
    }
}
