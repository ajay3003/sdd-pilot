using System.Text.RegularExpressions;
using BirkNext.RealProjectAcceptance;
using Microsoft.Playwright;

namespace BirkNext.Web.PlaywrightTests.RealProjectAcceptance.Features;

/// <summary>
/// Test Coverage &amp; Overlap Review over the run's imported snapshot: the page preselects that snapshot, the review runs, tests/components/
/// journeys/overlaps/gaps are honest (no execution without results, no percentage), every view renders with technical details available,
/// exports produce HTML and Markdown, and the page holds up at four widths, under axe and with the keyboard.
/// </summary>
public sealed class TestCoverageReviewFeature : AcceptanceFeature
{
    public override string FeatureId => "test-coverage-review";
    public override string DisplayName => "Test Coverage & Overlap Review";
    public override string Area => "Quality & Testing";
    public override string Route => "/test-coverage-review";
    public override IReadOnlyList<string> RequiredEvidence => ["Source Analysis snapshot"];
    public override IReadOnlyList<string> DependsOn => [ImportFeatureId, "source-analysis"];

    private static readonly (string Key, string Selector)[] Views =
    [
        ("overview", "[data-testid=tco-projects]"), ("journeys", "[data-testid=tco-journey], [data-testid=tco-no-journeys]"), ("components", "[data-testid=tco-component]"),
        ("requirements", "[data-testid=tco-behavior]"), ("duplicates", "[data-testid=tco-overlap], [data-testid=tco-no-overlaps]"), ("scope", "[data-testid=tco-scope-keep]"),
        ("documentation", "[data-testid=tco-doc-preview]"),
    ];

    protected override async Task RunAsync(RealProjectAcceptanceContext context, PlaywrightAcceptanceSession session, FeatureResultBuilder result, CancellationToken ct)
    {
        await OpenAsync(session, result);
        if (await session.WaitAnyAsync(60_000, "[data-testid=tco-current]", "[data-testid=tco-source-empty]") != "[data-testid=tco-current]")
        {
            result.Defect("tco-no-snapshot", "Test Coverage & Overlap Review offers no Source Analysis snapshot although the import created one.");
            result.Evidence = EvidenceState.NotVerified;
            return;
        }
        var selected = await session.Page.Locator("[data-testid=tco-current]").InputValueAsync();
        RecordIdentity(context, FeatureId, "snapshot", selected);
        if (!string.Equals(selected, context.Workspace.SourceSnapshotId, StringComparison.OrdinalIgnoreCase)) result.Defect("tco-wrong-snapshot", "The page did not preselect the imported project's snapshot.");
        if (await session.CountAsync("[data-testid=tco-page] input[type=file]") > 0) result.Defect("tco-upload", "The review offers a source upload; source must come from Source Analysis.");
        foreach (var v in await session.AxeAsync("[data-testid=tco-page]")) result.Warn("axe", $"readiness state: {v}");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await session.Page.Locator("[data-testid=tco-run]").ClickAsync();
        var state = await session.WaitAnyAsync(300_000, "[data-testid=tco-result]", "[data-testid=tco-error]");
        result.Observe("review duration", $"{watch.Elapsed.TotalSeconds:0.0} s");
        if (state != "[data-testid=tco-result]")
        {
            result.Execution = ExecutionStatus.Failed;
            result.Defect("tco-run-failed", await session.TextAsync("[data-testid=tco-error]") is { Length: > 0 } e ? e : "The review did not complete.");
            return;
        }
        var cards = await session.Page.Locator("[data-testid=tco-overview-cards] li").AllInnerTextsAsync();
        result.Observe("overview", string.Join(" | ", cards.Select(c => c.Replace('\n', ' '))));
        var execution = await session.TextAsync("[data-testid=tco-execution-note]");
        result.Observe("execution evidence", execution);
        var projects = await session.CountAsync("[data-testid=tco-project-row]");
        result.Observe("test projects", projects).Observe("unsupported test files", string.Join("; ", await session.Page.Locator("[data-testid=tco-unsupported]").AllInnerTextsAsync()));
        var body = await session.Page.Locator("[data-testid=tco-result]").InnerTextAsync();
        if (Regex.IsMatch(body, @"\d{1,3}\s*%")) result.Defect("tco-percentage", "A coverage percentage is shown.");
        if (Regex.IsMatch(body, @"\b(fully tested|all requirements covered|qa test unnecessary|remove this test)\b", RegexOptions.IgnoreCase)) result.Defect("tco-certification", "The review certifies coverage or recommends removing tests.");
        if (body.Contains("Passed", StringComparison.Ordinal) && execution.Contains("does not have proof", StringComparison.Ordinal) && Regex.IsMatch(body, @"Executed — passed")) result.Defect("tco-false-pass", "A test is shown as passed without execution evidence.");

        foreach (var (key, selector) in Views)
        {
            await session.Page.Locator($"[data-testid=tco-tab-{key}]").ClickAsync();
            var shown = await session.WaitAnyAsync(20_000, selector.Split(", "));
            if (shown is null) { result.Defect("tco-view-empty", $"The {key} view rendered nothing."); continue; }
            switch (key)
            {
                case "journeys":
                    var journeys = await session.Page.Locator("[data-testid=tco-journey] h3").AllInnerTextsAsync();
                    var steps = await session.Page.Locator("[data-testid=tco-step]").EvaluateAllAsync<string[]>("els => els.map(e => e.getAttribute('data-coverage') + '/' + e.getAttribute('data-evidence'))");
                    result.Observe("journeys shown", journeys.Count).Observe("journeys", string.Join(" || ", journeys.Take(8)))
                        .Observe("journey steps", string.Join(", ", steps.GroupBy(s => s).Select(g => $"{g.Key} {g.Count()}")));
                    result.Fact([.. journeys]);
                    foreach (var v in await session.AxeAsync("[data-testid=tco-panel]")) result.Warn("axe", $"journey view: {v}");
                    await Responsive(session, result, "journey view");
                    break;
                case "components":
                    var components = await session.Page.Locator("[data-testid=tco-component] h3").AllInnerTextsAsync();
                    result.Observe("components", string.Join(", ", components.Select(c => c.Split(" (")[0])));
                    result.Fact([.. components]);
                    break;
                case "duplicates":
                    var overlaps = await session.Page.Locator("[data-testid=tco-overlap]").EvaluateAllAsync<string[]>("els => els.map(e => e.getAttribute('data-kind'))");
                    result.Observe("overlaps", overlaps.Length == 0 ? "none" : string.Join(", ", overlaps.GroupBy(o => o).Select(g => $"{g.Key} {g.Count()}")));
                    foreach (var v in await session.AxeAsync("[data-testid=tco-panel]")) result.Warn("axe", $"duplicate view: {v}");
                    break;
                case "scope":
                    var keep = await session.Page.Locator("[data-testid=tco-scope-keep] li").AllInnerTextsAsync();
                    var gaps = await session.Page.Locator("[data-testid=tco-gap]").EvaluateAllAsync<string[]>("els => els.map(e => e.getAttribute('data-kind'))");
                    result.Observe("QA scope keep", string.Join(" || ", keep.Take(5))).Observe("gaps shown", string.Join(", ", gaps.GroupBy(g => g).Select(g => $"{g.Key} {g.Count()}")));
                    foreach (var v in await session.AxeAsync("[data-testid=tco-panel]")) result.Warn("axe", $"remaining QA scope: {v}");
                    break;
                case "documentation":
                    var preview = await session.TextAsync("[data-testid=tco-doc-preview]");
                    if (!preview.Contains("Covered Tests", StringComparison.Ordinal)) result.Defect("tco-doc", "The documentation preview does not show the Covered Tests document.");
                    foreach (var v in await session.AxeAsync("[data-testid=tco-panel]")) result.Warn("axe", $"documentation preview: {v}");
                    break;
            }
        }
        foreach (var v in await session.AxeAsync("[data-testid=tco-page]")) result.Warn("axe", $"completed review: {v}");
        await Keyboard(session, result);
        await Responsive(session, result, "page");
        var markdown = await session.CaptureDownloadAsync(() => session.Page.Locator("[data-testid=tco-export-scope]").ClickAsync());
        result.Observe("markdown export", markdown is { } md ? $"{md.FileName} ({md.Content.Length:N0} chars)" : "none");
        if (markdown is null || !markdown.Value.Content.Contains("Remaining QA Scope", StringComparison.Ordinal)) result.Defect("tco-markdown-export", "The Remaining QA Scope Markdown export did not download.");
        result.DataFromCount(projects, DataState.NoApplicableData);
        result.Provenance = string.Equals(selected, context.Workspace.SourceSnapshotId, StringComparison.OrdinalIgnoreCase) ? ProvenanceState.Traced : ProvenanceState.Missing;
        await ExportAsync(session, result, session.Page.Locator("[data-testid=tco-export-covered]"), context);
    }

    private static async Task Keyboard(PlaywrightAcceptanceSession session, FeatureResultBuilder result)
    {
        await session.Page.Locator("[data-testid=tco-tab-overview]").ClickAsync();
        await session.Page.Locator("[data-testid=tco-tab-overview]").FocusAsync();
        await session.Page.Keyboard.PressAsync("ArrowRight");
        var view = await session.AttrAsync("[data-testid=tco-panel]", "data-view");
        var focusVisible = await session.Page.EvaluateAsync<bool>("() => { const e = document.activeElement; if (!e) return false; const s = getComputedStyle(e); return s.outlineStyle !== 'none' || s.boxShadow !== 'none'; }");
        result.Observe("arrow-key tab switch", view).Observe("tab focus visible", focusVisible);
        if (view != "journeys") result.Warn("tco-keyboard", "Arrow keys did not move between the review views.");
        var summary = session.Page.Locator("[data-testid=tco-step] summary").First;
        if (await summary.CountAsync() > 0 && !await summary.EvaluateAsync<bool>("s => { s.focus(); return document.activeElement === s; }"))
            result.Warn("tco-keyboard", "Technical details cannot receive focus.");
    }

    private static async Task Responsive(PlaywrightAcceptanceSession session, FeatureResultBuilder result, string label)
    {
        var overflowing = new List<int>();
        try
        {
            foreach (var width in new[] { 1440, 1100, 768, 390 })
            {
                await session.Page.SetViewportSizeAsync(width, 1000);
                await session.Page.WaitForTimeoutAsync(400);
                var (client, scroll) = await session.HorizontalExtentAsync();
                if (scroll > client + 1) { overflowing.Add(width); result.Warn("horizontal-overflow", $"Test Coverage & Overlap Review ({label}) overflows at {width}px ({scroll}px > {client}px)."); }
            }
        }
        finally { await session.Page.SetViewportSizeAsync(1440, 1000); }
        result.Observe($"overflowing widths ({label})", overflowing.Count == 0 ? "none" : string.Join(", ", overflowing));
    }
}
