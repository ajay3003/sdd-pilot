using BirkNext.BrowserCompanion;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>The four WCAG principles, used in Browser Discovery purely to group observed evidence.</summary>
public enum WcagPrinciple { Perceivable, Operable, Understandable, Robust }

/// <summary>
/// Whether browser evidence of a kind exists for a page. Deliberately not a verdict: "Unavailable" means
/// nothing was observed, never that the page is bad, and never a numeric zero.
/// </summary>
public enum BrowserEvidenceState { Unavailable, Available }

/// <summary>One observed browser-side evidence item. Label/detail are descriptive; no status, score or verdict.</summary>
/// <param name="Label">What was observed.</param>
/// <param name="Detail">The observation itself (counts, values, sanitized selectors).</param>
/// <param name="CriterionId">The WCAG criterion the evidence relates to, when the collector recorded one.</param>
public sealed record BrowserEvidenceItem(string Label, string Detail, string? CriterionId = null);

/// <summary>Observed accessibility evidence grouped under one WCAG principle. Grouping only — never a result.</summary>
public sealed record BrowserWcagAreaEvidence(WcagPrinciple Principle, IReadOnlyList<BrowserEvidenceItem> Items);

/// <summary>One page/route row of the Browser Discovery overview table.</summary>
public sealed record BrowserPageRow(
    string Identity,
    string Route,
    IReadOnlyList<WcagPrinciple> WcagAreas,
    BrowserEvidenceState Dom,
    BrowserEvidenceState Performance,
    DateTimeOffset? LastSeen,
    string Source);

/// <summary>
/// Normalized, page-level presentation of Browser Companion evidence for Browser Discovery.
///
/// Browser Discovery collects and presents browser evidence; Frontend Quality Review interprets it. Everything here
/// is therefore evidence-shaped: WCAG principles are used only to group the evidence that was actually observed, and
/// carry no conformance meaning. Nothing in this file assesses, scores, thresholds or judges — it reads the existing
/// <see cref="BrowserPageEvidence"/> already stored on <see cref="PageAnalysis"/> and shapes it for display.
/// </summary>
public static class BrowserDiscoveryPresentation
{
    /// <summary>Browser evidence only ever comes from the Browser Companion extension; proxy traffic is Endpoint Discovery's.</summary>
    public const string Source = "Browser Companion";

    public const string NoEvidenceTitle = "No browser evidence collected yet";
    public const string NoEvidenceHelp = "Open an approved application page in the paired browser to collect browser evidence.";

    /// <summary>Stated wherever WCAG areas appear, so the grouping can never read as an assessment.</summary>
    public const string AreaDisclaimer =
        "WCAG areas show which principles the observed evidence relates to. They are not an assessment: no page is marked as passing, failing or conformant here. Frontend Quality Review interprets this evidence.";

    public static string PrincipleLabel(WcagPrinciple principle) => principle switch
    {
        WcagPrinciple.Perceivable => "Perceivable",
        WcagPrinciple.Operable => "Operable",
        WcagPrinciple.Understandable => "Understandable",
        _ => "Robust"
    };

    /// <summary>Evidence availability wording. Never "Passed"/"Failed", never 0.</summary>
    public static string StateLabel(BrowserEvidenceState state) =>
        state == BrowserEvidenceState.Available ? "Available" : "Unavailable";

    public static string StateCss(BrowserEvidenceState state) =>
        state == BrowserEvidenceState.Available ? "available" : "unavailable";

    /// <summary>WCAG numbers this: 1.x Perceivable, 2.x Operable, 3.x Understandable, 4.x Robust.</summary>
    public static WcagPrinciple? PrincipleOf(string? criterionId) => criterionId?.TrimStart() switch
    {
        ['1', ..] => WcagPrinciple.Perceivable,
        ['2', ..] => WcagPrinciple.Operable,
        ['3', ..] => WcagPrinciple.Understandable,
        ['4', ..] => WcagPrinciple.Robust,
        _ => null
    };

    // ── Overview ─────────────────────────────────────────────────────────────

    /// <summary>One row per page that actually carries browser evidence. Pages known only from network traffic are not browser evidence.</summary>
    public static IReadOnlyList<BrowserPageRow> Rows(EndpointDiscoverySnapshot? snapshot) =>
        (snapshot?.Pages ?? [])
            .Where(p => p.BrowserEvidence is not null)
            .OrderByDescending(p => p.BrowserEvidence!.CapturedAt)
            .ThenBy(p => p.Identity, StringComparer.Ordinal)
            .Select(p => new BrowserPageRow(
                p.Identity,
                p.PagePath.Length == 0 ? "/" : p.PagePath,
                Areas(p.BrowserEvidence!.Accessibility),
                p.BrowserEvidence.Dom is null ? BrowserEvidenceState.Unavailable : BrowserEvidenceState.Available,
                HasPerformanceEvidence(p.BrowserEvidence.Performance) ? BrowserEvidenceState.Available : BrowserEvidenceState.Unavailable,
                p.BrowserEvidence.CapturedAt,
                Source))
            .ToList();

    public static int PagesWithEvidence(EndpointDiscoverySnapshot? snapshot) => Rows(snapshot).Count;

    public static int PagesWith(EndpointDiscoverySnapshot? snapshot, Func<BrowserPageRow, bool> predicate) =>
        Rows(snapshot).Count(predicate);

    public static DateTimeOffset? LastEvidenceAt(EndpointDiscoverySnapshot? snapshot) =>
        (snapshot?.Pages ?? []).Where(p => p.BrowserEvidence is not null)
            .Select(p => (DateTimeOffset?)p.BrowserEvidence!.CapturedAt).Max();

    /// <summary>
    /// Performance evidence exists when the collector actually recorded at least one supported observation.
    /// An empty summary is not evidence, and a missing metric is never reported as zero.
    /// </summary>
    public static bool HasPerformanceEvidence(BrowserPerformanceSummary? performance) =>
        performance is not null && PerformanceEvidence(performance).Count > 0;

    // ── Accessibility evidence, grouped by WCAG principle ────────────────────

    /// <summary>Principles that observed evidence relates to. Absence of a principle only means nothing was observed for it.</summary>
    public static IReadOnlyList<WcagPrinciple> Areas(BrowserAccessibilitySummary? accessibility) =>
        AreaEvidence(accessibility).Select(a => a.Principle).ToList();

    /// <summary>
    /// The observed accessibility evidence, grouped under the WCAG principle each item relates to. Only evidence that
    /// was actually produced is included: checks and axe rules that never ran contribute nothing, because "not tested"
    /// is not evidence and must never become a pass.
    /// </summary>
    public static IReadOnlyList<BrowserWcagAreaEvidence> AreaEvidence(BrowserAccessibilitySummary? accessibility)
    {
        if (accessibility is null) return [];
        var byPrinciple = new Dictionary<WcagPrinciple, List<BrowserEvidenceItem>>();

        void Add(string? criterionId, BrowserEvidenceItem item)
        {
            if (PrincipleOf(criterionId) is not { } principle) return;
            if (!byPrinciple.TryGetValue(principle, out var items)) byPrinciple[principle] = items = [];
            items.Add(item with { CriterionId = criterionId });
        }

        // Rule results the collector produced for this page (rule id, title, how many nodes, sanitized selectors).
        foreach (var finding in accessibility.Findings)
            foreach (var criterion in Criteria(finding.Wcag))
                Add(criterion, new BrowserEvidenceItem(
                    finding.Title.Length > 0 ? finding.Title : finding.RuleId,
                    Join($"{accessibility.Engine} · {finding.RuleId}",
                         finding.Count > 0 ? $"{finding.Count} element(s) observed" : null,
                         finding.Selectors.Count > 0 ? $"selectors: {string.Join(", ", finding.Selectors.Take(3))}" : null)));

        // Explicit BirkNext checks. A check that did not run produced no evidence.
        foreach (var check in accessibility.Checks.Where(Executed))
            foreach (var criterion in CriteriaForCheck(check.CheckId))
                Add(criterion, new BrowserEvidenceItem(
                    $"Check · {check.CheckId}",
                    Join($"{check.Tested} element(s) examined",
                         check.Failed > 0 ? $"{check.Failed} flagged" : null,
                         check.Uncertain > 0 ? $"{check.Uncertain} uncertain" : null)));

        // axe rule evidence: which rules were evaluated and how many nodes they saw. Outcomes stay out of Discovery.
        foreach (var rule in accessibility.Axe?.Rules ?? [])
        {
            if (string.Equals(rule.Outcome, "NotTested", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var criterion in rule.CriterionIds)
                Add(criterion, new BrowserEvidenceItem(
                    $"axe · {rule.RuleId}",
                    rule.Count > 0 ? $"{rule.Count} node(s) observed" : "evaluated"));
        }

        return byPrinciple
            .OrderBy(kv => kv.Key)
            .Select(kv => new BrowserWcagAreaEvidence(kv.Key, kv.Value))
            .ToList();
    }

    private static bool Executed(BrowserWcagCheck check) =>
        !string.Equals(check.Outcome, "NotTested", StringComparison.OrdinalIgnoreCase) ||
        check.Tested > 0 || check.Failed > 0 || check.Uncertain > 0;

    /// <summary>A finding's WCAG reference may name one or several criteria.</summary>
    private static IEnumerable<string> Criteria(string? wcag) =>
        (wcag ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Which criteria a BirkNext check id supports, read from the existing registry rather than a parallel map.</summary>
    private static IEnumerable<string> CriteriaForCheck(string checkId) =>
        WcagRegistry.All.Where(d => d.SupportedChecks.Contains(checkId, StringComparer.OrdinalIgnoreCase))
            .Select(d => d.CriterionId);

    // ── DOM evidence ─────────────────────────────────────────────────────────

    /// <summary>Structural DOM counts already sanitized by the companion. Never raw DOM, text, attributes or values.</summary>
    public static IReadOnlyList<BrowserEvidenceItem> DomEvidence(BrowserDomSummary? dom)
    {
        if (dom is null) return [];
        var items = new List<BrowserEvidenceItem>
        {
            new("Nodes", dom.NodeCount.ToString()),
            new("Maximum depth", dom.MaxDepth.ToString()),
            new("Interactive elements", dom.InteractiveCount.ToString()),
            new("Form controls", dom.FormControlCount.ToString()),
            new("Images", dom.ImageCount.ToString()),
            new("Iframes", dom.IframeCount.ToString()),
            new("Dialogs", dom.DialogCount.ToString()),
        };
        if (dom.Landmarks.Count > 0)
            items.Add(new("Landmarks", string.Join(", ", dom.Landmarks.OrderBy(l => l.Key, StringComparer.Ordinal).Select(l => $"{l.Key} ×{l.Value}"))));
        if (dom.HeadingCounts.Count > 0)
            items.Add(new("Headings", string.Join(", ", dom.HeadingCounts.OrderBy(h => h.Key, StringComparer.Ordinal).Select(h => $"{h.Key} ×{h.Value}"))));
        if (dom.DuplicateIdCount > 0) items.Add(new("Duplicate ids", dom.DuplicateIdCount.ToString()));
        if (dom.HiddenFocusableCount > 0) items.Add(new("Hidden focusable elements", dom.HiddenFocusableCount.ToString()));
        if (dom.PositiveTabIndexCount > 0) items.Add(new("Positive tabindex", dom.PositiveTabIndexCount.ToString()));
        return items;
    }

    // ── Performance evidence ─────────────────────────────────────────────────

    /// <summary>
    /// Metrics the browser actually reported, as raw observations. A metric the browser did not report is omitted
    /// rather than shown as zero, and no threshold, rating or verdict is applied — that is Frontend Quality Review's job.
    /// </summary>
    public static IReadOnlyList<BrowserEvidenceItem> PerformanceEvidence(BrowserPerformanceSummary? performance)
    {
        if (performance is null) return [];
        var items = new List<BrowserEvidenceItem>();
        void Ms(string label, double? value) { if (value is { } v) items.Add(new(label, $"{v:0} ms")); }

        Ms("Time to first byte", performance.TtfbMs);
        Ms("First contentful paint", performance.FirstContentfulPaintMs);
        Ms("Largest contentful paint", performance.LcpMs);
        if (performance.Cls is { } cls) items.Add(new("Cumulative layout shift", cls.ToString("0.###")));
        Ms("DOM content loaded", performance.DomContentLoadedMs);
        Ms("Load event", performance.LoadEventMs);
        Ms("Page stabilization", performance.StabilizationMs);

        // INP is published only when the browser exposed enough distinct interactions; otherwise the reason is the evidence.
        if (performance.Interaction is { } interaction)
        {
            if (interaction.InpMs is { } inp) items.Add(new("Interaction to next paint", $"{inp:0} ms"));
            else if (interaction.InteractionCount > 0)
                items.Add(new("Interactions observed", $"{interaction.InteractionCount} (INP needs {interaction.MinimumInteractions})"));
        }

        if (performance.LongTaskCount > 0) items.Add(new("Long tasks", performance.LongTaskCount.ToString()));
        if (performance.ResourceCount > 0) items.Add(new("Resources fetched", performance.ResourceCount.ToString()));
        if (performance.TransferredBytes > 0) items.Add(new("Transferred", Bytes(performance.TransferredBytes)));
        return items;
    }

    /// <summary>"initial-load" / "spa-navigation" describe how the page was observed; they are not a quality phase.</summary>
    public static string ObservationLabel(BrowserPerformanceSummary? performance) => performance?.ObservationType switch
    {
        "spa-navigation" => "SPA navigation",
        "initial-load" => "Initial load",
        { Length: > 0 } other => other,
        _ => "Not recorded"
    };

    private static string Bytes(long value) => value switch
    {
        >= 1024 * 1024 => $"{value / 1024d / 1024d:0.#} MB",
        >= 1024 => $"{value / 1024d:0.#} KB",
        _ => $"{value} B"
    };

    private static string Join(params string?[] parts) =>
        string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}
