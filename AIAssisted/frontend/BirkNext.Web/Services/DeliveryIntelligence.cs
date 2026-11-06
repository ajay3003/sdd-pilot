using BirkNext.DeliveryReporting;

namespace BirkNext.Web.Services;

/// <summary>
/// One feature as both Delivery pages show it: identity, business summary, delivery status, a qualified progress line, the quality evidence that
/// exists, attention and the change this period. Built once by <see cref="DeliveryIntelligence.Cards"/>; components only render it.
/// </summary>
public sealed record DeliveryFeatureCard(string FeatureKey, string Title, string Module, string? Summary, BacklogStatus Status, string StatusLabel,
    DeliveryDashboardTone StatusTone, string Progress, int? ProgressPercent, string Evidence, DeliveryDashboardTone EvidenceTone, string RequirementModel,
    int Attention, string? StrongestAttention, string Change, DeliveryDashboardTone ChangeTone, int Priority)
{
    public bool Removed { get; init; }
    public bool Verified => Status is BacklogStatus.ImplementationVerified or BacklogStatus.QaVerified;
}

/// <summary>Checklist progress and status counts of one module (features of the scoped view only).</summary>
public sealed record DeliveryModuleRow(string Module, int Features, int TasksChecked, int Tasks, int InProgress, int NeedReview, int Blocked, int Verified)
{
    public int? Percent => Tasks == 0 ? null : 100 * TasksChecked / Tasks;
}

/// <summary>One "Attention required" line: what, how many, why it matters, and where to act (a page view and an optional filter).</summary>
public sealed record DeliveryAttentionEntry(string Key, string Label, int Count, string Detail, DeliveryDashboardTone Tone, string View, string? Filter = null);

/// <summary>A Change Board lane (Delivery Reports): features of one change kind with its business impact in one sentence.</summary>
public sealed record DeliveryChangeLane(string Key, string Label, string Impact, DeliveryDashboardTone Tone, IReadOnlyList<DeliveryFeatureCard> Features)
{
    /// <summary>A lane whose value cannot be established from the evidence (shown as Not assessed, never as 0).</summary>
    public string? NotAssessed { get; init; }
}

/// <summary>Sort orders of the shared feature list.</summary>
public enum DeliveryFeatureSort { Priority, Name, Module, Progress }

/// <summary>
/// The shared Delivery Intelligence calculations of Delivery Overview (Project Leader, current state) and Delivery Reports (Scrum Master, comparison).
/// Every figure on either page comes from here or from <see cref="DeliveryDashboard"/> — never a formula in a component. Checklist progress, stated
/// status and verification evidence stay separate; unknown stays unknown; a candidate is never confirmed.
/// </summary>
public static class DeliveryIntelligence
{
    public const string ProgressNote = "Checkbox state in tasks.md — progress, not verified delivery.";
    public const string NoVerification = "No implementation or QA verification linked";

    public static DeliveryDashboardTone StatusTone(BacklogStatus status) => status switch
    {
        BacklogStatus.Blocked => DeliveryDashboardTone.Blocker,
        BacklogStatus.NeedsClarification => DeliveryDashboardTone.Review,
        BacklogStatus.ImplementationVerified or BacklogStatus.QaVerified => DeliveryDashboardTone.Ready,
        BacklogStatus.InProgress or BacklogStatus.ChecklistComplete or BacklogStatus.Accepted => DeliveryDashboardTone.Progress,
        _ => DeliveryDashboardTone.Neutral,
    };

    /// <summary>The feature cards of the view in dashboard priority (blocked, needs review, new, changed, in progress, rest), plus removed features
    /// of the report (comparison mode only — never backlog items of the current state).</summary>
    public static IReadOnlyList<DeliveryFeatureCard> Cards(DeliveryBacklogView view, WeeklyDeliveryReport? report, bool includeRemoved = false)
    {
        var rows = report?.Stories.ToDictionary(s => s.FeatureKey, StringComparer.Ordinal) ?? [];
        var cards = DeliveryDashboard.Rows(view, report).Select(r => Card(r, rows.GetValueOrDefault(r.Item.FeatureKey))).ToList();
        if (includeRemoved && report is not null)
            cards.AddRange(report.Stories.Where(s => s.RemovedFeature).Select(Removed));
        return cards;
    }

    private static DeliveryFeatureCard Card(DeliveryFeatureGridRow r, DeliveryStoryRow? row)
    {
        var i = r.Item;
        var (progress, pct) = Progress(i, row);
        var verified = i.Status is BacklogStatus.ImplementationVerified or BacklogStatus.QaVerified;
        return new(i.FeatureKey, i.Title, i.Module, Summary(row), i.Status, DeliveryBacklog.Label(i.Status), StatusTone(i.Status), progress, pct,
            verified ? DeliveryBacklog.Label(i.Status) : NoVerification, verified ? DeliveryDashboardTone.Ready : DeliveryDashboardTone.Neutral,
            r.RequirementModel, i.Attention, r.StrongestAttention, r.Change, r.ChangeTone, r.Priority);
    }

    private static DeliveryFeatureCard Removed(DeliveryStoryRow s)
    {
        var (change, tone) = DeliveryDashboard.Change(s);
        return new(s.FeatureKey, s.Title, s.Module, Summary(s), BacklogStatus.NotAssessed, "Removed from scope", DeliveryDashboardTone.Neutral,
            $"Baseline checklist {s.BaselineTaskCompleted} / {s.BaselineTaskTotal}", null, "Not part of the current snapshot", DeliveryDashboardTone.Neutral,
            "", s.Attention.Count, s.Attention.FirstOrDefault(), change, tone, 6) { Removed = true };
    }

    /// <summary>"Checklist 12 / 14 (86%)" or why no progress can be stated — never 0/0.</summary>
    public static (string Text, int? Percent) Progress(BacklogItem item, DeliveryStoryRow? row)
    {
        if (row?.Hierarchy is { HasTaskList: false } || item.TasksTotal == 0) return ("No task checklist", null);
        var pct = DeliveryMetrics.ChecklistPercent(item.TasksCompleted, item.TasksTotal)!.Value;
        return ($"Checklist {item.TasksCompleted} / {item.TasksTotal} ({pct}%)", pct);
    }

    /// <summary>The business objective as the specification states it: the first story's description, else the first story title. Never generated.</summary>
    public static string? Summary(DeliveryStoryRow? row)
    {
        var story = row?.Hierarchy?.Stories.FirstOrDefault(s => !s.Removed && s.Change != DeliveryItemChange.Removed);
        var text = story?.Description is { Length: > 0 } d ? d : story?.Title;
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        return text.Length <= 220 ? text : text[..217].TrimEnd() + "…";
    }

    public static IReadOnlyList<DeliveryFeatureCard> Sort(IEnumerable<DeliveryFeatureCard> cards, DeliveryFeatureSort sort) => sort switch
    {
        DeliveryFeatureSort.Name => [.. cards.OrderBy(c => c.Title, StringComparer.OrdinalIgnoreCase)],
        DeliveryFeatureSort.Module => [.. cards.OrderBy(c => c.Module, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase)],
        DeliveryFeatureSort.Progress => [.. cards.OrderBy(c => c.ProgressPercent ?? int.MaxValue).ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase)],
        _ => [.. cards.OrderBy(c => c.Priority).ThenBy(c => c.Module, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase)],
    };

    /// <summary>Per module: features, checked / total tasks, and status counts. Ordered by blockers, then review, then name.</summary>
    public static IReadOnlyList<DeliveryModuleRow> Modules(DeliveryBacklogView view) =>
    [
        .. view.Items.GroupBy(i => i.Module.Length == 0 ? "Project" : i.Module, StringComparer.OrdinalIgnoreCase).Select(g => new DeliveryModuleRow(g.Key, g.Count(),
                g.Sum(i => i.TasksCompleted), g.Sum(i => i.TasksTotal), g.Count(i => i.Status == BacklogStatus.InProgress),
                g.Count(i => i.Attention > 0 || i.Status == BacklogStatus.NeedsClarification), g.Count(i => i.Status == BacklogStatus.Blocked),
                g.Count(i => i.Status is BacklogStatus.ImplementationVerified or BacklogStatus.QaVerified)))
            .OrderByDescending(m => m.Blocked).ThenByDescending(m => m.NeedReview).ThenBy(m => m.Module, StringComparer.OrdinalIgnoreCase),
    ];

    /// <summary>Checklist progress by module as bars (percent of checked tasks; modules without tasks are left out and counted in the note).</summary>
    public static IReadOnlyList<DeliveryBar> ModuleProgress(DeliveryBacklogView view) =>
    [
        .. Modules(view).Where(m => m.Tasks > 0).OrderBy(m => m.Module, StringComparer.OrdinalIgnoreCase).Select(m => new DeliveryBar(m.Module, m.Module, m.Percent ?? 0,
            DeliveryDashboardTone.Progress, $"{m.TasksChecked} of {m.Tasks} tasks checked in {m.Features} feature{(m.Features == 1 ? "" : "s")} — checklist only, not verification.")),
    ];

    /// <summary>Blocked features by module; empty when nothing is blocked (no chart of zeros).</summary>
    public static IReadOnlyList<DeliveryBar> BlockersByModule(DeliveryBacklogView view) =>
    [
        .. Modules(view).Where(m => m.Blocked > 0).Select(m => new DeliveryBar(m.Module, m.Module, m.Blocked, DeliveryDashboardTone.Blocker, "Features explicitly marked Blocked.")),
    ];

    /// <summary>
    /// What a project leader has to act on, in priority order: blockers, clarifications, feature attention items, unconfirmed removals, scope
    /// candidates awaiting review, missing verification evidence and comparison limits. Only non-empty entries; each says where to act.
    /// </summary>
    public static IReadOnlyList<DeliveryAttentionEntry> AttentionRequired(DeliveryBacklogView view, WeeklyDeliveryReport? report, int scopeCandidates = 0)
    {
        var list = new List<DeliveryAttentionEntry>();
        var blocked = view.Count(BacklogStatus.Blocked);
        if (blocked > 0) list.Add(new("blocked", "Blocked features", blocked, "Explicitly marked Blocked in the feature documents.", DeliveryDashboardTone.Blocker, "features", "state=Blocked"));
        var clarify = view.Count(BacklogStatus.NeedsClarification);
        if (clarify > 0) list.Add(new("clarification", "Open clarifications", clarify, "[NEEDS CLARIFICATION] markers still in the specification.", DeliveryDashboardTone.Review, "features", "state=NeedsClarification"));
        var attention = view.Items.Count(i => i.Attention > 0);
        if (attention > 0) list.Add(new("attention", "Features with review items", attention, "Broken traceability, removed-story references or specifications without tasks.", DeliveryDashboardTone.Review, "features", "attention=attention"));
        if (report?.Stories.Count(s => s.RemovedFeature && s.RemovalUnconfirmed) is > 0 and var unconfirmed)
            list.Add(new("unconfirmed-removal", "Removals to confirm", unconfirmed, "Absent from an incompletely read snapshot — missing, not a confirmed removal.", DeliveryDashboardTone.Review, "risks"));
        if (report?.ScopeChanges.Count is > 0 and var scope)
            list.Add(new("scope-change", "Scope changes to confirm", scope, "New or removed features since the baseline — confirm they are intentional (not defects).", DeliveryDashboardTone.Change, "risks"));
        if (scopeCandidates > 0)
            list.Add(new("candidates", "Ambiguous scope associations", scopeCandidates, "Features that may belong to the selected scope; not counted until confirmed in Relationship Review.", DeliveryDashboardTone.Review, "risks"));
        var unverified = view.Items.Count(i => i.Status is not (BacklogStatus.ImplementationVerified or BacklogStatus.QaVerified));
        if (unverified > 0 && view.Items.Count > 0)
            list.Add(new("evidence", "Missing verification evidence", unverified, $"{unverified} of {view.Items.Count} features have no linked implementation or QA verification — completion cannot be confirmed.", DeliveryDashboardTone.Neutral, "risks"));
        if (report is not null && report.ComparisonWarnings.Count + report.CoverageNotes.Count is > 0 and var limits)
            list.Add(new("limitations", "Comparison limitations", limits, "Same-day, partial coverage or unknown revisions: read period figures with care.", DeliveryDashboardTone.Neutral, "risks"));
        return list;
    }

    // ── Comparison (Delivery Reports) ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Baseline → current, from the frozen report only: scope, change, declared (stated done) and verified completion apart, checklist apart.</summary>
    public static IReadOnlyList<DeliveryKpi> Comparison(WeeklyDeliveryReport report)
    {
        var current = report.Stories.Where(s => !s.RemovedFeature).ToList();
        var baseline = report.Stories.Where(s => !s.NewFeature).ToList();
        var changed = report.Stories.Count(s => !s.NewFeature && !s.RemovedFeature && s.Changed);
        var declaredNow = current.Count(s => s.CurrentStatus == DeliveryStatus.Done);
        var declaredBefore = baseline.Count(s => s.BaselineStatus == DeliveryStatus.Done);
        int Pct(int done, int total) => DeliveryMetrics.ChecklistPercent(done, total) ?? 0;
        var (cDone, cTotal) = (current.Sum(s => s.CurrentTaskCompleted), current.Sum(s => s.CurrentTaskTotal));
        var (bDone, bTotal) = (baseline.Sum(s => s.BaselineTaskCompleted), baseline.Sum(s => s.BaselineTaskTotal));
        var attention = report.Attention.Count(a => a.FeatureKey is not null && a.Kind != DeliveryAttentionKind.SourceEncoding);
        return
        [
            new("scope", $"{baseline.Count} → {current.Count}", "Features in scope", "Baseline → current snapshot", DeliveryDashboardTone.Progress),
            new("new", report.Stories.Count(s => s.NewFeature).ToString(), "New features", "Present now, absent in the baseline", DeliveryDashboardTone.Change),
            new("removed", report.Stories.Count(s => s.RemovedFeature).ToString(), "Removed features", "Scope change to confirm — not delivered, not a defect", DeliveryDashboardTone.Neutral),
            new("changed", changed.ToString(), "Changed features", "Retained features whose specification, tasks or status differ", DeliveryDashboardTone.Change),
            new("declared", $"{declaredBefore} → {declaredNow}", "Declared done", "Explicit “Status: Done” lines — a declaration, not verification", DeliveryDashboardTone.Progress,
                "Counts features whose documents state “Status: Done”. A declaration is not verified completion."),
            new("verified", "Not assessed", "Completed with verified evidence", "No implementation or QA verification is linked to either snapshot", DeliveryDashboardTone.Neutral, DeliveryDashboard.VerifiedInfo),
            new("checklist", bTotal + cTotal == 0 ? "–" : $"{Pct(bDone, bTotal)}% → {Pct(cDone, cTotal)}%", "Task checklist", $"{cDone} / {cTotal} checked now · {DeliveryDashboard.ChecklistInfo}", DeliveryDashboardTone.Progress, DeliveryDashboard.ChecklistInfo),
            new("attention", attention.ToString(), "Feature review items", "Attention items attached to a feature (hygiene and comparison limits apart)", attention == 0 ? DeliveryDashboardTone.Neutral : DeliveryDashboardTone.Review),
        ];
    }

    /// <summary>
    /// The Change Board: New · Changed · Removed · Completed with evidence · Checklist completed this period · Needs review. A feature can be in
    /// Changed and Needs review at once (review is a signal, not a change kind). "Completed with evidence" stays Not assessed without verification.
    /// </summary>
    public static IReadOnlyList<DeliveryChangeLane> ChangeBoard(WeeklyDeliveryReport report, IReadOnlyList<DeliveryFeatureCard> cards)
    {
        var byKey = report.Stories.ToDictionary(s => s.FeatureKey, StringComparer.Ordinal);
        IReadOnlyList<DeliveryFeatureCard> Where(Func<DeliveryStoryRow, bool> predicate) =>
            [.. cards.Where(c => byKey.TryGetValue(c.FeatureKey, out var s) && predicate(s))];
        static bool ChecklistDone(DeliveryStatus? s) => s is DeliveryStatus.TasksComplete or DeliveryStatus.ReadyForTest or DeliveryStatus.Testing or DeliveryStatus.Done;
        var verified = cards.Where(c => c.Verified && !c.Removed).ToList();
        return
        [
            new("new", "New", "Scope added since the baseline — confirm it was planned.", DeliveryDashboardTone.Change, Where(s => s.NewFeature)),
            new("changed", "Changed", "Specification, acceptance criteria, tasks or status changed — review what the change means for the plan.", DeliveryDashboardTone.Change,
                Where(s => !s.NewFeature && !s.RemovedFeature && s.Changed)),
            new("removed", "Removed", "No longer in the current snapshot — confirm the removal was intended; it is not delivered work.", DeliveryDashboardTone.Neutral, Where(s => s.RemovedFeature)),
            new("verified", "Completed with evidence", "Implementation or QA verification linked.", DeliveryDashboardTone.Ready, verified)
                { NotAssessed = verified.Count == 0 ? "Not assessed — no verification evidence is linked to these snapshots." : null },
            new("checklist-complete", "Checklist completed this period", "Every task is now checked — still to be verified, tested and accepted.", DeliveryDashboardTone.Progress,
                Where(s => !s.RemovedFeature && ChecklistDone(s.CurrentStatus) && !ChecklistDone(s.BaselineStatus))),
            new("review", "Needs review", "Traceability gaps, unconfirmed removals or open clarifications.", DeliveryDashboardTone.Review,
                Where(s => s.HasActiveAttention || s.RemovalUnconfirmed || s.CurrentStatus == DeliveryStatus.NeedsClarification)),
        ];
    }

    /// <summary>Next steps for one feature, derived from its own evidence only (no schedule, no invented priority).</summary>
    public static IReadOnlyList<string> NextActions(DeliveryFeatureCard card, DeliveryStoryRow? row)
    {
        var list = new List<string>();
        if (card.Removed) { list.Add(row?.RemovalUnconfirmed == true ? "Confirm whether the feature was removed or only missing from the snapshot." : "Confirm the removal was intended."); return list; }
        if (card.Status == BacklogStatus.Blocked) list.Add("Resolve the blocker stated in the feature documents.");
        if (card.Status == BacklogStatus.NeedsClarification) list.Add("Answer the open clarification markers in the specification.");
        if (card.Attention > 0) list.Add($"Review {card.Attention} traceability item{(card.Attention == 1 ? "" : "s")} (see Risks & attention below).");
        if (row?.Hierarchy is { HasTaskList: false }) list.Add("Add a task list (tasks.md) so progress can be followed.");
        if (card.Status == BacklogStatus.ChecklistComplete) list.Add("All tasks are checked: link implementation and test evidence to verify completion.");
        else if (!card.Verified) list.Add("Link implementation or QA verification evidence — completion cannot be confirmed without it.");
        return list;
    }
}
