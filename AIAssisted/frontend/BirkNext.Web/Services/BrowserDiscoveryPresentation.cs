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

/// <summary>
/// How an observation came out, as the collector reported it — never a WCAG verdict. "Flagged" means the collector
/// marked something for a human to look at; "uncertain" means it could not decide. Neither is a failed success
/// criterion, and neither is an assessment: Frontend Quality Review is where evidence becomes a result.
/// Ordered so the interesting states sort first.
/// </summary>
public enum BrowserObservationState { Observed, Uncertain, Flagged }

/// <summary>
/// One accessibility check, rule or finding the collector produced for a page.
/// </summary>
/// <param name="Key">Dedupe identity. One observation relates to every criterion it maps to and is therefore listed
/// under several principles; page-level totals count it once, through this key.</param>
/// <param name="Id">The collector's own check/rule id, for the compact attention rows.</param>
/// <param name="Label">Display name including the engine that produced it.</param>
/// <param name="CriterionId">The criterion this listing sits under. Observing a rule associated with a criterion is
/// not the same as assessing that criterion.</param>
/// <param name="Elements">How many elements the observation examined. Zero means the rule ran and matched nothing.</param>
public sealed record BrowserObservation(
    string Key, string Id, string Label, string Detail, string? CriterionId,
    BrowserObservationState State, int Elements, int FlaggedCount, int UncertainCount, bool Automated)
{
    /// <summary>The compact right-hand text of an observation row. Counts only; never a verdict word.</summary>
    public string StateDetail => State switch
    {
        BrowserObservationState.Flagged => FlaggedCount > 0 ? $"{FlaggedCount} flagged" : "flagged",
        BrowserObservationState.Uncertain => UncertainCount > 0 ? $"{UncertainCount} uncertain" : "uncertain",
        _ => Elements > 0 ? $"{Elements} observed" : "evaluated",
    };
}

/// <summary>Observed evidence grouped under one WCAG principle. Grouping only — never a result for that principle.</summary>
public sealed record BrowserPrincipleEvidence(WcagPrinciple Principle, IReadOnlyList<BrowserObservation> Observations)
{
    public int Flagged => Observations.Count(o => o.State == BrowserObservationState.Flagged);
    public int Uncertain => Observations.Count(o => o.State == BrowserObservationState.Uncertain);

    /// <summary>What is worth reading when this principle is expanded: anything flagged, uncertain, or that examined elements.</summary>
    public IReadOnlyList<BrowserObservation> Primary =>
        Observations.Where(o => o.State != BrowserObservationState.Observed || o.Elements > 0).ToList();

    /// <summary>Rules that ran and matched nothing. Still evidence, still kept — just not the first thing to read.</summary>
    public IReadOnlyList<BrowserObservation> OtherEvaluated =>
        Observations.Where(o => o.State == BrowserObservationState.Observed && o.Elements == 0).ToList();
}

/// <summary>
/// The page-level accessibility evidence summary Browser Discovery leads with, so the rule catalogue can stay behind
/// progressive disclosure.
///
/// Count semantics, because the two levels deliberately differ:
/// <list type="bullet">
/// <item>Per principle, an observation is counted in every principle it relates to — that is what "evidence related to
/// this principle" means, and an observation mapped to three criteria genuinely is evidence about three principles.
/// Principle counts therefore do not sum to the page total.</item>
/// <item>Page totals (<see cref="Evaluated"/>, <see cref="Flagged"/>, <see cref="Uncertain"/>) and
/// <see cref="Attention"/> count each underlying observation once, by <see cref="BrowserObservation.Key"/>.</item>
/// <item>Scope is evidence that maps to at least one WCAG criterion; a rule the collector could not relate to a
/// criterion has no principle to sit under and is not counted here.</item>
/// </list>
/// Nothing in this model is a score, rate or grade, and no count is a conformance statement.
/// </summary>
public sealed record BrowserAccessibilityOverview(
    IReadOnlyList<BrowserPrincipleEvidence> Principles,
    IReadOnlyList<BrowserObservation> Attention,
    int Evaluated, int Flagged, int Uncertain,
    IReadOnlyList<BrowserObservation> AutomatedRules)
{
    public static readonly BrowserAccessibilityOverview Empty = new([], [], 0, 0, 0, []);

    /// <summary>Evidence exists when at least one check, rule or finding ran — including when none of them flagged anything.</summary>
    public bool Any => Evaluated > 0;
    public int PrincipleCount => Principles.Count;
    public int AutomatedFlagged => AutomatedRules.Count(o => o.State == BrowserObservationState.Flagged);
    public int AutomatedUncertain => AutomatedRules.Count(o => o.State == BrowserObservationState.Uncertain);
}


/// <summary>One page's DOM evidence as a single comparison row. Counts only — never a rating of those counts.</summary>
public sealed record BrowserDomComparisonRow(
    string Identity, string Route, DateTimeOffset ObservedAt,
    int Nodes, int Depth, int Interactive, int FormControls, int Images, int Iframes, int Dialogs, int HiddenFocusable,
    IReadOnlyList<BrowserEvidenceItem> Detail);

/// <summary>
/// One page's performance evidence as a single comparison row. Every metric is nullable on purpose: a metric the
/// browser never reported is absent, and rendering it as 0 would claim a measurement that was never taken.
/// </summary>
public sealed record BrowserPerformanceComparisonRow(
    string Identity, string Route, DateTimeOffset ObservedAt, string Observation,
    double? Cls, double? StabilizationMs, int? Resources, long? TransferredBytes,
    IReadOnlyList<BrowserEvidenceItem> Detail);

/// <summary>One accessibility observation, on one page, listed under one WCAG principle.</summary>
public sealed record BrowserAccessibilityComparisonRow(
    string Identity, string Route, DateTimeOffset ObservedAt, WcagPrinciple Principle, BrowserObservation Observation)
{
    public string? CriterionId => Observation.CriterionId;
    public bool NeedsAttention => Observation.State != BrowserObservationState.Observed;
}

/// <summary>
/// The cross-page evidence inventory behind Browser Discovery's Evidence tab: what evidence exists across the pages
/// the companion observed, so they can be compared without opening each one.
///
/// Aggregation rule, and it differs from the per-page summary on purpose:
/// <list type="bullet">
/// <item>Evidence observed on two pages is two observations. <see cref="Observations"/>, <see cref="Flagged"/> and
/// <see cref="Uncertain"/> sum each page's own distinct counts, because "how much evidence do we have across pages"
/// is a question about pages, not about rule ids.</item>
/// <item>Within one page an observation is still counted once, even when it relates to several criteria — the same
/// key-based dedupe the per-page summary uses.</item>
/// <item><see cref="Accessibility"/> rows are per principle, so one observation related to two principles is two
/// rows. That is the grouping the table exists to show, and it is why the row count exceeds
/// <see cref="Observations"/>.</item>
/// </list>
/// Nothing here is a score, a rate or a verdict.
/// </summary>
public sealed record BrowserEvidenceInventory(
    IReadOnlyList<BrowserDomComparisonRow> Dom,
    IReadOnlyList<BrowserAccessibilityComparisonRow> Accessibility,
    IReadOnlyList<BrowserPerformanceComparisonRow> Performance,
    int AccessibilityPages, int Observations, int Flagged, int Uncertain)
{
    public static readonly BrowserEvidenceInventory Empty = new([], [], [], 0, 0, 0, 0);

    /// <summary>The principles the observed evidence relates to, across all pages. Grouping, never coverage.</summary>
    public IReadOnlyList<WcagPrinciple> Areas =>
        Accessibility.Select(r => r.Principle).Distinct().OrderBy(p => p).ToList();

    public bool Any => Dom.Count > 0 || Accessibility.Count > 0 || Performance.Count > 0;
}

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

    /// <summary>
    /// The one absence message for browser evidence. The session state is stated once in the summary strip and once on
    /// the Browser Companion card; this says what is missing and what to do, without restating either.
    /// </summary>
    public const string NoEvidenceTitle = "No browser evidence yet";

    /// <summary>Next action when nothing is paired yet: both steps in one sentence.</summary>
    public const string NoEvidenceHelp =
        "Pair Browser Companion and open an approved application page to start collecting browser evidence.";

    /// <summary>Next action when a session already exists — pairing is done, so only the page step remains.</summary>
    public const string NoEvidencePairedHelp =
        "Open an approved application page in the paired browser to start collecting browser evidence.";

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
    /// The one derivation of a page's accessibility evidence. <see cref="AreaEvidence"/> is a projection of it, so the
    /// summary the Pages tab leads with and the per-principle listing can never disagree about what was observed.
    /// Checks and axe rules that never ran contribute nothing, because "not tested" is not evidence and must never
    /// become a pass.
    /// </summary>
    public static BrowserAccessibilityOverview Accessibility(BrowserAccessibilitySummary? accessibility)
    {
        if (accessibility is null) return BrowserAccessibilityOverview.Empty;
        var byPrinciple = new Dictionary<WcagPrinciple, List<BrowserObservation>>();
        var distinct = new Dictionary<string, BrowserObservation>(StringComparer.Ordinal);

        void Add(string? criterionId, BrowserObservation observation)
        {
            if (PrincipleOf(criterionId) is not { } principle) return;
            if (!byPrinciple.TryGetValue(principle, out var items)) byPrinciple[principle] = items = [];
            items.Add(observation with { CriterionId = criterionId });
            distinct.TryAdd(observation.Key, observation);
        }

        // Rule results the collector produced for this page. A finding means the collector matched something, so it is
        // flagged for review — which is not the same as a failed success criterion.
        foreach (var finding in accessibility.Findings)
        {
            var observation = new BrowserObservation(
                $"finding:{finding.RuleId}", finding.RuleId,
                finding.Title.Length > 0 ? finding.Title : finding.RuleId,
                Join($"{accessibility.Engine} · {finding.RuleId}",
                     finding.Count > 0 ? $"{finding.Count} element(s) observed" : null,
                     finding.Selectors.Count > 0 ? $"selectors: {string.Join(", ", finding.Selectors.Take(3))}" : null),
                null, BrowserObservationState.Flagged, finding.Count, finding.Count, 0, false);
            foreach (var criterion in Criteria(finding.Wcag)) Add(criterion, observation);
        }

        // Explicit BirkNext checks. A check that did not run produced no evidence.
        foreach (var check in accessibility.Checks.Where(Executed))
        {
            var observation = new BrowserObservation(
                $"check:{check.CheckId}", check.CheckId, $"Check · {check.CheckId}",
                Join($"{check.Tested} element(s) examined",
                     check.Failed > 0 ? $"{check.Failed} flagged" : null,
                     check.Uncertain > 0 ? $"{check.Uncertain} uncertain" : null),
                null,
                check.Failed > 0 ? BrowserObservationState.Flagged
                    : check.Uncertain > 0 ? BrowserObservationState.Uncertain : BrowserObservationState.Observed,
                check.Tested, check.Failed, check.Uncertain, false);
            foreach (var criterion in CriteriaForCheck(check.CheckId)) Add(criterion, observation);
        }

        // axe rule evidence: which rules were evaluated and how many nodes they saw. axe's own vocabulary
        // ("violations", "incomplete") is normalized to the observation vocabulary; Browser Discovery does not
        // publish outcomes as results.
        foreach (var rule in accessibility.Axe?.Rules ?? [])
        {
            if (string.Equals(rule.Outcome, "NotTested", StringComparison.OrdinalIgnoreCase)) continue;
            var state = rule.Outcome switch
            {
                "Fail" => BrowserObservationState.Flagged,
                "ManualReviewRequired" => BrowserObservationState.Uncertain,
                _ => BrowserObservationState.Observed,
            };
            var observation = new BrowserObservation(
                $"axe:{rule.RuleId}", rule.RuleId, $"axe · {rule.RuleId}",
                rule.Count > 0 ? $"{rule.Count} node(s) observed" : "evaluated",
                null, state, rule.Count,
                state == BrowserObservationState.Flagged ? rule.Count : 0,
                state == BrowserObservationState.Uncertain ? rule.Count : 0, true);
            foreach (var criterion in rule.CriterionIds) Add(criterion, observation);
        }

        var all = distinct.Values.ToList();
        return new BrowserAccessibilityOverview(
            byPrinciple.OrderBy(kv => kv.Key).Select(kv => new BrowserPrincipleEvidence(kv.Key, Ordered(kv.Value))).ToList(),
            Ordered(all.Where(o => o.State != BrowserObservationState.Observed)),
            all.Count,
            all.Count(o => o.State == BrowserObservationState.Flagged),
            all.Count(o => o.State == BrowserObservationState.Uncertain),
            Ordered(all.Where(o => o.Automated)));
    }

    /// <summary>Flagged first, then uncertain, then observations that examined elements, then rules that matched nothing.</summary>
    private static IReadOnlyList<BrowserObservation> Ordered(IEnumerable<BrowserObservation> observations) =>
        observations.OrderByDescending(o => o.State).ThenByDescending(o => o.Elements)
            .ThenBy(o => o.Label, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Whether a page carries accessibility evidence at all. Zero flagged observations is evidence, not absence.</summary>
    public static BrowserEvidenceState AccessibilityState(BrowserAccessibilitySummary? accessibility) =>
        Accessibility(accessibility).Any ? BrowserEvidenceState.Available : BrowserEvidenceState.Unavailable;

    /// <summary>
    /// The observed accessibility evidence, grouped under the WCAG principle each item relates to — a projection of
    /// <see cref="Accessibility"/>, so the Evidence tab and the Pages tab read from one derivation.
    /// </summary>
    public static IReadOnlyList<BrowserWcagAreaEvidence> AreaEvidence(BrowserAccessibilitySummary? accessibility) =>
        Accessibility(accessibility).Principles
            .Select(p => new BrowserWcagAreaEvidence(p.Principle,
                p.Observations.Select(o => new BrowserEvidenceItem(o.Label, o.Detail, o.CriterionId)).ToList()))
            .ToList();

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

    // ── Cross-page evidence inventory (Evidence tab) ─────────────────────────

    /// <summary>
    /// The one derivation of the cross-page inventory, built from the same per-page evidence the Pages tab reads.
    /// A page contributes a DOM row only when it carries DOM evidence, and a performance row only when the browser
    /// actually reported something — an empty summary is not a row of zeros.
    /// </summary>
    public static BrowserEvidenceInventory Inventory(EndpointDiscoverySnapshot? snapshot)
    {
        var pages = (snapshot?.Pages ?? [])
            .Where(p => p.BrowserEvidence is not null)
            .Select(p => (Identity: p.Identity, Route: p.PagePath.Length == 0 ? "/" : p.PagePath, Evidence: p.BrowserEvidence!))
            .OrderByDescending(p => p.Evidence.CapturedAt)
            .ThenBy(p => p.Identity, StringComparer.Ordinal)
            .ToList();
        if (pages.Count == 0) return BrowserEvidenceInventory.Empty;

        var dom = pages.Where(p => p.Evidence.Dom is not null).Select(p =>
        {
            var d = p.Evidence.Dom!;
            return new BrowserDomComparisonRow(p.Identity, p.Route, p.Evidence.CapturedAt,
                d.NodeCount, d.MaxDepth, d.InteractiveCount, d.FormControlCount, d.ImageCount, d.IframeCount,
                d.DialogCount, d.HiddenFocusableCount, DomStructure(d));
        }).ToList();

        var performance = pages.Where(p => HasPerformanceEvidence(p.Evidence.Performance)).Select(p =>
        {
            var perf = p.Evidence.Performance!;
            // Compared columns come out of the summary; whatever else the browser reported stays available as detail.
            var compared = new[] { "Cumulative layout shift", "Page stabilization", "Resources fetched", "Transferred" };
            return new BrowserPerformanceComparisonRow(p.Identity, p.Route, p.Evidence.CapturedAt, ObservationLabel(perf),
                perf.Cls, perf.StabilizationMs,
                perf.ResourceCount > 0 ? perf.ResourceCount : null,
                perf.TransferredBytes > 0 ? perf.TransferredBytes : null,
                PerformanceEvidence(perf).Where(i => !compared.Contains(i.Label)).ToList());
        }).ToList();

        var accessibility = new List<BrowserAccessibilityComparisonRow>();
        var accessiblePages = 0; var observations = 0; var flagged = 0; var uncertain = 0;
        foreach (var page in pages)
        {
            var overview = Accessibility(page.Evidence.Accessibility);
            if (!overview.Any) continue;
            accessiblePages++;
            observations += overview.Evaluated;
            flagged += overview.Flagged;
            uncertain += overview.Uncertain;
            foreach (var principle in overview.Principles)
                foreach (var observation in principle.Observations)
                    accessibility.Add(new BrowserAccessibilityComparisonRow(
                        page.Identity, page.Route, page.Evidence.CapturedAt, principle.Principle, observation));
        }

        return new BrowserEvidenceInventory(dom,
            // Attention first, then the biggest observations, then a stable page/rule order.
            accessibility.OrderByDescending(r => r.Observation.State).ThenByDescending(r => r.Observation.Elements)
                .ThenBy(r => r.Route, StringComparer.Ordinal).ThenBy(r => r.Observation.Label, StringComparer.OrdinalIgnoreCase).ToList(),
            performance, accessiblePages, observations, flagged, uncertain);
    }

    /// <summary>Landmarks and headings: structure worth seeing per page, but not worth a column each.</summary>
    private static IReadOnlyList<BrowserEvidenceItem> DomStructure(BrowserDomSummary dom) =>
        DomEvidence(dom).Where(i => i.Label is "Landmarks" or "Headings" or "Duplicate ids" or "Positive tabindex").ToList();

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
        items.AddRange(DomDetail(dom));
        return items;
    }

    /// <summary>
    /// The compact DOM block: structure counts, landmarks and headings. Everything <see cref="DomEvidence"/> also
    /// reports lives in <see cref="DomDetail"/> behind a disclosure, so no observation is dropped by being compact.
    /// </summary>
    public static IReadOnlyList<BrowserEvidenceItem> DomPrimary(BrowserDomSummary? dom)
    {
        var detail = DomDetail(dom).Select(d => d.Label).ToHashSet(StringComparer.Ordinal);
        return DomEvidence(dom).Where(i => !detail.Contains(i.Label)).ToList();
    }

    /// <summary>
    /// Structural signals the collector only reports when it saw them. They are held behind disclosure because they
    /// are absent on most pages, and the disclosure carries their count so their presence is never hidden.
    /// </summary>
    public static IReadOnlyList<BrowserEvidenceItem> DomDetail(BrowserDomSummary? dom)
    {
        if (dom is null) return [];
        var items = new List<BrowserEvidenceItem>();
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

    /// <summary>Human-readable transfer size. A size, never a budget.</summary>
    public static string ByteLabel(long value) => Bytes(value);

    private static string Bytes(long value) => value switch
    {
        >= 1024 * 1024 => $"{value / 1024d / 1024d:0.#} MB",
        >= 1024 => $"{value / 1024d:0.#} KB",
        _ => $"{value} B"
    };

    private static string Join(params string?[] parts) =>
        string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}

/// <summary>
/// What Browser Discovery should say and offer right now, derived once from the session state and whether any
/// evidence exists. Four distinct situations, because collapsing any two of them loses something real:
/// being connected is not the same as having evidence, and being paired is not the same as reporting.
/// </summary>
public enum BrowserDiscoveryState
{
    /// <summary>No session. Pairing is the next step.</summary>
    NotConnected,
    /// <summary>Pairing is under way and waiting for the code.</summary>
    Pairing,
    /// <summary>A session exists but nothing is reporting — an approved page needs opening or refreshing.</summary>
    PairedNotReporting,
    /// <summary>Connected, but no approved page is open and nothing has been observed yet. Asking the user to pair again would be wrong.</summary>
    ConnectedWithoutEvidence,
    /// <summary>
    /// Connected AND an approved application page is open right now, but no evidence has arrived for it. Knowing which
    /// page is open is not evidence of anything on it, so this cannot be folded into either neighbouring state: telling
    /// the reader to open a page BirkNext already reports as open is wrong, and calling it "collecting" is a claim
    /// nothing has yet backed.
    /// </summary>
    ConnectedOnApprovedPageWithoutEvidence,
    /// <summary>Evidence exists; the page shows it rather than an empty state.</summary>
    EvidenceAvailable,
}

/// <summary>
/// The single derivation of Browser Discovery's user-facing state. The summary strip, the empty state and the
/// Browser Companion card all read from here, so they cannot describe one situation in three different ways.
/// </summary>
public static class BrowserDiscoveryStates
{
    /// <param name="onApprovedPage">
    /// An approved application page is open in the paired browser right now (the companion reports a current page).
    /// This is liveness, never evidence: it says where the browser is, not that anything was observed there.
    /// </param>
    public static BrowserDiscoveryState Of(BrowserCompanionState session, bool hasEvidence, bool onApprovedPage = false) => (session, hasEvidence) switch
    {
        (_, true) => BrowserDiscoveryState.EvidenceAvailable,
        (BrowserCompanionState.Connected, _) when onApprovedPage => BrowserDiscoveryState.ConnectedOnApprovedPageWithoutEvidence,
        (BrowserCompanionState.Connected, _) => BrowserDiscoveryState.ConnectedWithoutEvidence,
        (BrowserCompanionState.Disconnected, _) => BrowserDiscoveryState.PairedNotReporting,
        (BrowserCompanionState.PairingPending, _) => BrowserDiscoveryState.Pairing,
        _ => BrowserDiscoveryState.NotConnected,
    };

    /// <summary>
    /// Is the paired browser currently on an approved application page? The backend clears the current page whenever the
    /// reporting tab is not an approved one, so a current origin is already an approved origin; the membership check is
    /// kept so this reads as the security fact it is rather than as a trusted leftover.
    /// </summary>
    public static bool OnApprovedPage(BrowserCompanionStatus? status) =>
        status is { CurrentPageOrigin: { Length: > 0 } origin } &&
        status.ApprovedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase);

    /// <summary>The one primary connection vocabulary. Technical pairing state stays in Connection details.</summary>
    public static string SessionLabel(BrowserCompanionState session) => session switch
    {
        BrowserCompanionState.Connected => "Connected",
        BrowserCompanionState.Disconnected => "Paired · not reporting",
        BrowserCompanionState.PairingPending => "Pairing…",
        BrowserCompanionState.Expired => "Session expired",
        _ => "Not connected",
    };

    /// <summary>
    /// What is missing and what to do about it — never what the state IS, which the summary strip already says.
    /// A connected session is never told to pair again.
    /// </summary>
    public static string Explanation(BrowserDiscoveryState state) => state switch
    {
        BrowserDiscoveryState.ConnectedWithoutEvidence =>
            "Open an approved application page to start collecting browser evidence.",
        // BirkNext already knows this page is open, so telling the reader to open it reads as though nothing were
        // happening at all. What is actually missing is the evidence, and that is what this says.
        BrowserDiscoveryState.ConnectedOnApprovedPageWithoutEvidence =>
            "Connected to an approved application page. No browser evidence has been received for it yet.",
        // Pairing does not reach a page that was already open: the content script starts with a page load.
        // So the fix is a reload, and saying so is the whole point of this state — it is not a failure.
        BrowserDiscoveryState.PairedNotReporting =>
            "Open or refresh an approved application page to start collecting browser evidence.",
        BrowserDiscoveryState.Pairing =>
            "Enter the pairing code in Browser Companion, then open an approved application page.",
        _ => "Pair the managed Edge browser and open an approved application page to start collecting browser evidence.",
    };

    /// <summary>
    /// Whether the empty state owns the Pair action. Only one Pair control is ever visible, and it belongs to the
    /// surface the reader is actually looking at.
    /// </summary>
    public static bool ShowsPairAction(BrowserDiscoveryState state) =>
        NextAction(state) is BrowserDiscoveryNextAction.Pair;

    /// <summary>
    /// The states that deserve a visible callout: an expected, recoverable situation where the next step is easy
    /// to miss as a sentence. Only paired-but-silent qualifies — "not connected" already has a button, and a
    /// reporting session needs nothing from the reader.
    /// </summary>
    public static bool ShowsGuidance(BrowserDiscoveryState state) =>
        state is BrowserDiscoveryState.PairedNotReporting;

    public const string GuidanceTitle = "Resume browser reporting";

    /// <summary>
    /// Names the browser, because that is the part a reader cannot infer: the page has to be reloaded in the
    /// managed Edge session the pairing belongs to, not in whichever browser is showing BirkNext.
    /// </summary>
    public const string GuidanceText = "Open or refresh an approved application page in your managed Edge browser.";

    /// <summary>
    /// What the reader should do next, as a value rather than a string the markup has to recognise. Each state maps
    /// to exactly one next step, which is what keeps "Paired · not reporting" from reading as a dead end.
    /// </summary>
    public static BrowserDiscoveryNextAction NextAction(BrowserDiscoveryState state) => state switch
    {
        BrowserDiscoveryState.NotConnected => BrowserDiscoveryNextAction.Pair,
        BrowserDiscoveryState.Pairing => BrowserDiscoveryNextAction.EnterPairingCode,
        BrowserDiscoveryState.PairedNotReporting => BrowserDiscoveryNextAction.OpenOrRefreshApprovedPage,
        BrowserDiscoveryState.ConnectedWithoutEvidence => BrowserDiscoveryNextAction.OpenApprovedPage,
        // Nothing for the reader to do: the approved page is already open and the next report is the companion's move.
        BrowserDiscoveryState.ConnectedOnApprovedPageWithoutEvidence => BrowserDiscoveryNextAction.None,
        _ => BrowserDiscoveryNextAction.None,
    };
}

/// <summary>
/// The one next step for a Browser Discovery state. A paired session is never told to pair again, and a session
/// that is already reporting is not told to open anything.
/// </summary>
public enum BrowserDiscoveryNextAction
{
    /// <summary>Evidence is arriving; nothing is being asked of the reader.</summary>
    None,
    Pair,
    EnterPairingCode,
    /// <summary>Paired, but the page was open before pairing, so it has to be reloaded to start reporting.</summary>
    OpenOrRefreshApprovedPage,
    OpenApprovedPage,
}

/// <summary>One compact fact for the Browser Discovery overview: a label, a value, and a test id.</summary>
public sealed record BrowserDiscoveryFact(string Label, string Value, string TestId, bool Muted = false);

/// <summary>
/// Whether a page is open right now, has only been captured before, or both.
/// </summary>
public enum BrowserPageLiveness { LiveNow, HistoricalOnly, LiveWithoutEvidence }

/// <summary>
/// The two questions Browser Discovery has to answer separately: what is live right now, and what have we captured
/// before.
///
/// They were one set of facts, and that is how "Connected · 4 pages with evidence · DOM available" could describe a
/// browser with no application page open at all. Every label below belongs to exactly one of the two, and nothing in
/// the live half is derived from stored evidence.
/// </summary>
public static class BrowserDiscoveryLive
{
    /// <summary>Live facts. Five compact rows; none of them reads stored evidence.</summary>
    public static IReadOnlyList<BrowserDiscoveryFact> Facts(BrowserCompanionStatus? status)
    {
        var live = status?.EffectiveLive ?? BrowserCompanionLiveSession.Disconnected;
        return
        [
            new("Browser Companion", BrowserDiscoveryStates.SessionLabel(status?.State ?? BrowserCompanionState.NotPaired), "bd-session"),
            new("Live approved pages", live.LiveApprovedPageCount.ToString(), "bd-live-pages"),
            new("Current page", CurrentPageLabel(live), "bd-current-page", live.CurrentPage is null),
            // Without a content script there is no DOM to read and no step to run, whatever was captured before.
            new("Content script", live.ContentScriptAlive ? "Live" : "Not available", "bd-content-script", !live.ContentScriptAlive),
            new("Live DOM", live.LiveDomAvailable ? "Available now" : "Not available", "bd-live-dom", !live.LiveDomAvailable),
        ];
    }

    /// <summary>
    /// Never "the newest evidence route". With no live page this is None even when a hundred pages have evidence, and
    /// with several open it says so rather than naming one.
    /// </summary>
    public static string CurrentPageLabel(BrowserCompanionLiveSession live) => live switch
    {
        { CurrentPage: { } page } => page.Identity,
        { LiveApprovedPageCount: 0 } => "None",
        var many => $"{many.LiveApprovedPageCount} pages open",
    };

    /// <summary>Historical facts. Every label says "evidence", because every one of them is about the past.</summary>
    public static IReadOnlyList<BrowserDiscoveryFact> EvidenceFacts(BrowserCompanionStatus? status, EndpointDiscoverySnapshot? snapshot)
    {
        // The stored snapshot is the fuller history (it survives re-pairing); the session summary covers the case where
        // the snapshot has not been merged yet.
        var rows = BrowserDiscoveryPresentation.Rows(snapshot);
        var summary = status?.Evidence ?? BrowserCompanionEvidenceSummary.Empty;
        var pages = Math.Max(rows.Count, summary.PagesWithEvidence);
        var last = BrowserDiscoveryPresentation.LastEvidenceAt(snapshot) ?? summary.LastEvidenceAt;
        return
        [
            new("Pages with evidence", pages.ToString(), "bd-pages-count"),
            new("Last evidence", last is { } at ? at.ToLocalTime().ToString("HH:mm:ss") : "None", "bd-last-evidence", last is null),
            new("DOM evidence", Captured(Math.Max(BrowserDiscoveryPresentation.PagesWith(snapshot, r => r.Dom == BrowserEvidenceState.Available), summary.DomEvidencePageCount)), "bd-evidence-dom"),
            new("Accessibility evidence", Captured(Math.Max(BrowserDiscoveryPresentation.PagesWith(snapshot, r => r.WcagAreas.Count > 0), summary.AccessibilityEvidencePageCount)), "bd-evidence-accessibility"),
            new("Performance evidence", Captured(Math.Max(BrowserDiscoveryPresentation.PagesWith(snapshot, r => r.Performance == BrowserEvidenceState.Available), summary.PerformanceEvidencePageCount)), "bd-evidence-performance"),
        ];
    }

    /// <summary>"Available" alone reads as "available now". These counts are all about captures that already happened.</summary>
    private static string Captured(int pages) => pages == 0 ? "None captured" : $"{pages} page{(pages == 1 ? "" : "s")} captured";

    /// <summary>Is this evidence row a page that is also open right now?</summary>
    public static BrowserPageLiveness Liveness(BrowserPageRow row, BrowserCompanionLiveSession? live) =>
        live?.LivePages.Any(p => string.Equals(p.Identity, row.Identity, StringComparison.OrdinalIgnoreCase)) == true
            ? BrowserPageLiveness.LiveNow
            : BrowserPageLiveness.HistoricalOnly;

    public static string LivenessLabel(BrowserPageLiveness liveness) => liveness switch
    {
        BrowserPageLiveness.LiveNow => "Live now",
        BrowserPageLiveness.LiveWithoutEvidence => "Live now · no evidence yet",
        _ => "Historical evidence only",
    };

    /// <summary>Live pages that have produced no evidence yet — real, current, and invisible to an evidence-only list.</summary>
    public static IReadOnlyList<BrowserCompanionLivePage> LiveWithoutEvidence(BrowserCompanionLiveSession? live, EndpointDiscoverySnapshot? snapshot)
    {
        var known = BrowserDiscoveryPresentation.Rows(snapshot).Select(r => r.Identity).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return live?.LivePages.Where(p => !known.Contains(p.Identity)).ToList() ?? [];
    }
}
