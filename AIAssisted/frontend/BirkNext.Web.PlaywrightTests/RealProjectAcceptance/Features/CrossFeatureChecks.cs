using BirkNext.RealProjectAcceptance;
using Microsoft.Playwright;

namespace BirkNext.Web.PlaywrightTests.RealProjectAcceptance.Features;

/// <summary>Every feature that reported which snapshot/project it used must agree with the import's workspace identity.</summary>
public sealed class WorkspaceConsistencyFeature : AcceptanceFeature
{
    public override string FeatureId => "workspace-consistency";
    public override string DisplayName => "Cross-feature workspace consistency";
    public override string Area => "Cross-feature";
    public override string Route => "";
    public override AcceptanceMode MinimumMode => AcceptanceMode.Smoke;

    protected override Task RunAsync(RealProjectAcceptanceContext context, PlaywrightAcceptanceSession session, FeatureResultBuilder result, CancellationToken ct)
    {
        var identities = context.Shared.Where(kv => kv.Key.StartsWith("identity:", StringComparison.Ordinal)).Select(kv => (Parts: kv.Key.Split(':', 3), Value: (string)kv.Value)).ToList();
        var snapshots = identities.Where(i => i.Parts[1] == "snapshot").ToList();
        var projects = identities.Where(i => i.Parts[1] == "project").ToList();
        result.Observe("features reporting a snapshot", snapshots.Count).Observe("features reporting a project", projects.Count);
        foreach (var (parts, value) in snapshots)
            if (!string.Equals(value, context.Workspace.SourceSnapshotId, StringComparison.OrdinalIgnoreCase))
                result.Defect("snapshot-divergence", $"{parts[2]} used snapshot {value[..Math.Min(8, value.Length)]}…, not the import's.");
        var expectedProject = context.Workspace.ProjectName;
        foreach (var (parts, value) in projects)
            if (expectedProject is { Length: > 0 } && !value.Contains(expectedProject, StringComparison.OrdinalIgnoreCase) && !expectedProject.Contains(value, StringComparison.OrdinalIgnoreCase))
                result.Defect("project-divergence", $"{parts[2]} shows project \"{value}\", not \"{expectedProject}\".");
        result.DataFromCount(snapshots.Count + projects.Count, DataState.NotAssessed);
        result.Provenance = result.Findings.Count == 0 ? ProvenanceState.Traced : ProvenanceState.PartiallyTraced;
        return Task.CompletedTask;
    }
}

/// <summary>Explorer render identity vs Explorer Text Coverage: each rendered document must be one the diagnostic measured.</summary>
public sealed class RenderProvenanceFeature : AcceptanceFeature
{
    public override string FeatureId => "render-provenance";
    public override string DisplayName => "Explorer render provenance";
    public override string Area => "Cross-feature";
    public override string Route => "";
    public override AcceptanceType AcceptanceType => AcceptanceType.Diagnostics;
    // Not blocked by coverage findings (e.g. a changed baseline): it compares whatever the diagnostic measured, and says so when nothing.

    protected override Task RunAsync(RealProjectAcceptanceContext context, PlaywrightAcceptanceSession session, FeatureResultBuilder result, CancellationToken ct)
    {
        var rendered = context.Shared.Where(kv => kv.Key.StartsWith(ExplorerFeature.RenderedDocumentKeyPrefix, StringComparison.Ordinal))
            .Select(kv => (Feature: kv.Key[ExplorerFeature.RenderedDocumentKeyPrefix.Length..], Id: (string)kv.Value)).ToList();
        if (!context.Shared.TryGetValue(DiagnosticFeature.CoverageExpectedIdsKey, out var raw) || raw is not HashSet<string> { Count: > 0 } expected)
        {
            result.Execution = ExecutionStatus.NotAvailable;
            result.Evidence = EvidenceState.NotVerified;
            result.Note("Explorer Text Coverage did not measure the real archive; render provenance cannot be compared.");
            return Task.CompletedTask;
        }
        var matched = rendered.Count(r => expected.Contains(r.Id));
        result.Observe("explorers rendered", rendered.Count).Observe("matched coverage documents", matched).Observe("coverage documents", expected.Count);
        foreach (var r in rendered.Where(r => !expected.Contains(r.Id)))
            result.Defect("render-not-measured", $"{r.Feature} rendered a document the coverage diagnostic did not measure (identity {r.Id[..Math.Min(12, r.Id.Length)]}…).");
        result.DataFromCount(matched, DataState.NotAssessed);
        result.Provenance = rendered.Count > 0 && matched == rendered.Count ? ProvenanceState.Traced : ProvenanceState.PartiallyTraced;
        return Task.CompletedTask;
    }
}

/// <summary>Client-side navigation across features and a full reload keep the same imported workspace and snapshot.</summary>
public sealed class NavigationPersistenceFeature : AcceptanceFeature
{
    public override string FeatureId => "navigation-persistence";
    public override string DisplayName => "Navigation and reload persistence";
    public override string Area => "Cross-feature";
    public override string Route => "/source-analysis";
    public override AcceptanceType AcceptanceType => AcceptanceType.Navigation;
    public override IReadOnlyList<string> DependsOn => [ImportFeatureId, "source-analysis"];

    private static readonly string[] Sequence = ["/source-analysis", "/dependency-review", "/pipeline-review", "/task-alignment", "/integration-quality-review", "/specification-explorer", "/source-analysis"];

    protected override async Task RunAsync(RealProjectAcceptanceContext context, PlaywrightAcceptanceSession session, FeatureResultBuilder result, CancellationToken ct)
    {
        foreach (var route in Sequence) await OpenAsync(session, result, route);
        await ContractEvidenceFeature.EnsureSourceOverviewAsync(session);
        var afterNavigation = await session.AttrAsync("[data-testid=sa-current]", "data-snapshot");
        await session.Page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        var state = await session.WaitAnyAsync(120_000, "[data-testid=sa-current]", "[data-testid=sa-empty]");
        var afterReload = await session.AttrAsync("[data-testid=sa-current]", "data-snapshot");
        await session.NavigateAsync("/specification-explorer");
        await session.Page.WaitForFunctionAsync("() => { const h = document.querySelector('[data-testid=artifact-explorer]'); return h && h.getAttribute('data-status') !== 'Loading'; }", null, new() { Timeout = 120_000 });
        var explorer = await session.AttrAsync("[data-testid=artifact-explorer]", "data-status");
        result.Observe("snapshot after navigation", afterNavigation).Observe("snapshot after reload", afterReload).Observe("explorer after reload", explorer);
        if (!string.Equals(afterNavigation, context.Workspace.SourceSnapshotId, StringComparison.OrdinalIgnoreCase)) result.Defect("navigation-lost-snapshot", "Navigation changed the current snapshot.");
        if (state != "[data-testid=sa-current]" || !string.Equals(afterReload, context.Workspace.SourceSnapshotId, StringComparison.OrdinalIgnoreCase)) result.Defect("reload-lost-snapshot", "A reload lost the imported snapshot.");
        if (explorer is not ("Loaded" or "SelectionRequired")) result.Defect("reload-lost-workspace", $"After reload the Specification Explorer is {explorer}.");
        result.Data = DataState.RealDataObserved;
        result.Provenance = result.Findings.Count == 0 ? ProvenanceState.Traced : ProvenanceState.Missing;
    }
}

/// <summary>
/// Representative pages at desktop and phone width: no horizontal page overflow, axe WCAG A/AA violations (reported, never a conformance
/// claim), and keyboard focus reaching the main content. Findings here are warnings unless the page cannot be used at all.
/// </summary>
public sealed class ResponsiveAccessibilityFeature : AcceptanceFeature
{
    public override string FeatureId => "responsive-accessibility";
    public override string DisplayName => "Responsive layout, axe and keyboard";
    public override string Area => "Cross-feature";
    public override string Route => "";
    public override AcceptanceType AcceptanceType => AcceptanceType.Navigation;

    private static readonly string[] Pages = ["/dashboard", "/source-analysis", "/specification-explorer", "/dependency-review", "/pipeline-review", "/task-alignment"];

    protected override async Task RunAsync(RealProjectAcceptanceContext context, PlaywrightAcceptanceSession session, FeatureResultBuilder result, CancellationToken ct)
    {
        int[] widths = context.Mode == AcceptanceMode.Full ? [1440, 1024, 768, 390] : [1440, 390];
        var overflow = 0; var violations = 0;
        try
        {
            foreach (var width in widths)
            {
                await session.Page.SetViewportSizeAsync(width, 1000);
                foreach (var page in Pages)
                {
                    await OpenAsync(session, result, page);
                    await session.Page.WaitForTimeoutAsync(1200);
                    var (client, scroll) = await session.HorizontalExtentAsync();
                    if (scroll > client + 1)
                    {
                        overflow++;
                        // Name the outermost elements that stick out, so the report points at the cause rather than the symptom.
                        var culprits = await session.Page.EvaluateAsync<string[]>(@"() => {
                            const w = document.documentElement.clientWidth, out = [];
                            for (const el of document.querySelectorAll('body *')) {
                                const r = el.getBoundingClientRect();
                                if (r.right > w + 1 && r.width > 0 && !(el.parentElement && el.parentElement.getBoundingClientRect().right > w + 1))
                                    out.push(el.tagName.toLowerCase() + (el.getAttribute('data-testid') ? '[data-testid=' + el.getAttribute('data-testid') + ']' : el.className ? '.' + String(el.className).trim().split(/\s+/).join('.') : '') + ' (' + Math.round(r.right) + 'px)');
                                if (out.length >= 3) break;
                            }
                            return out; }");
                        result.Warn("horizontal-overflow", $"{page} overflows horizontally at {width}px ({scroll}px > {client}px): {string.Join(", ", culprits)}.");
                    }
                    if (width == 1440 || width == 390)
                        foreach (var v in await session.AxeAsync("main"))
                        { violations++; result.Warn("axe", $"{page} @ {width}px: {v}"); }
                }
            }
            await session.Page.SetViewportSizeAsync(1440, 1000);
            await OpenAsync(session, result, "/source-analysis");
            var reached = false;
            for (var i = 0; i < 40 && !reached; i++)
            {
                await session.Page.Keyboard.PressAsync("Tab");
                reached = await session.Page.EvaluateAsync<bool>("() => !!document.activeElement && !!document.activeElement.closest('main, article, .content')");
            }
            result.Observe("keyboard reaches main content", reached);
            if (!reached) result.Warn("keyboard-main-unreachable", "Tabbing 40 times did not reach the main content.");
        }
        finally { await session.Page.SetViewportSizeAsync(1440, 1000); }
        result.Observe("widths", string.Join(", ", widths)).Observe("pages", Pages.Length).Observe("overflowing page/width pairs", overflow).Observe("axe violation groups", violations);
        result.Data = DataState.RealDataObserved;
        result.Evidence = violations > 0 || overflow > 0 ? EvidenceState.PartiallyVerified : EvidenceState.Verified;
    }
}
