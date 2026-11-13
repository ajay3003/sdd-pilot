using BirkNext.DeliveryReporting;
using Microsoft.JSInterop;

namespace BirkNext.Web.Services;

/// <summary>Who is looking: decides only which view a Delivery page opens on. Never a permission and never a different calculation.</summary>
public enum DeliveryRole { ProjectLeader, ScrumMaster, TestLead }

/// <summary>One frozen delivery report on the timeline: when, which comparison, and its key changes (from the report summary only).</summary>
public sealed record DeliveryTimelinePoint(Guid ReportId, DateTimeOffset PeriodEnd, string Title, string Period, string Comparison, int StoriesChanged, int Attention,
    DeliveryCoverage Coverage, bool Current);

/// <summary>A milestone stated by the evidence of the current report: a feature whose checklist became complete, or that is stated done, in the period.</summary>
public sealed record DeliveryMilestone(string FeatureKey, string Title, string Module, string Kind, string Meaning);

/// <summary>One rung of the Functional Quality Scope evidence ladder: from acceptance criteria defined to tests passed. Candidates are never coverage.</summary>
public sealed record DeliveryLadderRow(string Key, string Label, string Value, int? Count, string Meaning, DeliveryDashboardTone Tone, ScopeCoverage? Filter);

/// <summary>
/// History and role helpers of the shared Delivery Intelligence layer (Delivery Overview and Delivery Reports): the timeline of frozen reports, the
/// previous/next report of the same kind, milestones the current report states, the Functional Quality Scope evidence ladder and the role's default view.
/// </summary>
public static class DeliveryIntelligenceHistory
{
    /// <summary>Frozen whole-comparison reports in time order (scoped reports cover part of the project and are not points of the project timeline).</summary>
    public static IReadOnlyList<DeliveryTimelinePoint> Timeline(IEnumerable<DeliveryReportSummaryRow> reports, Guid? current) =>
    [
        .. reports.Where(r => r.ScopeLabel.Length == 0).OrderBy(r => r.PeriodEnd).ThenBy(r => r.GeneratedAt)
            .Select(r => new DeliveryTimelinePoint(r.Id, r.PeriodEnd, r.DisplayTitle.Length > 0 ? r.DisplayTitle : r.Title,
                $"{r.PeriodStart:d MMM} – {r.PeriodEnd:d MMM yyyy}", $"{r.BaselineLabel} → {r.CurrentLabel}", r.StoriesChanged, r.AttentionCount, r.Coverage, r.Id == current)),
    ];

    /// <summary>The report before and after this one among reports of the same scope (whole comparison or the same scoped label), by generation time.</summary>
    public static (DeliveryReportSummaryRow? Previous, DeliveryReportSummaryRow? Next) Neighbours(IEnumerable<DeliveryReportSummaryRow> reports, Guid current)
    {
        var all = reports.ToList();
        if (all.FirstOrDefault(r => r.Id == current) is not { } me) return (null, null);
        var same = all.Where(r => r.ScopeLabel == me.ScopeLabel).OrderBy(r => r.GeneratedAt).ToList();
        var at = same.FindIndex(r => r.Id == current);
        return (at > 0 ? same[at - 1] : null, at < same.Count - 1 ? same[at + 1] : null);
    }

    /// <summary>Milestones the current report states: checklist completed this period, and explicit "Status: Done" new this period. Never a planned date.</summary>
    public static IReadOnlyList<DeliveryMilestone> Milestones(WeeklyDeliveryReport? report)
    {
        if (report is null) return [];
        static bool ChecklistDone(DeliveryStatus? s) => s is DeliveryStatus.TasksComplete or DeliveryStatus.ReadyForTest or DeliveryStatus.Testing or DeliveryStatus.Done;
        return
        [
            .. report.Stories.Where(s => !s.RemovedFeature && s.CurrentStatus == DeliveryStatus.Done && s.BaselineStatus != DeliveryStatus.Done)
                .Select(s => new DeliveryMilestone(s.FeatureKey, s.Title, s.Module, "Stated done", "An explicit “Status: Done” appeared this period — a declaration, not verification.")),
            .. report.Stories.Where(s => !s.RemovedFeature && s.CurrentStatus != DeliveryStatus.Done && ChecklistDone(s.CurrentStatus) && !ChecklistDone(s.BaselineStatus))
                .Select(s => new DeliveryMilestone(s.FeatureKey, s.Title, s.Module, "Checklist completed", "Every task is now checked — still to be verified, tested and accepted.")),
        ];
    }

    /// <summary>
    /// The Functional Quality Scope evidence ladder: acceptance criteria defined → explicitly linked tests → candidate links only (never coverage) → no
    /// known coverage → not assessed, then executed and passed, which no snapshot carries (Not assessed, never 0). Each coverage rung can filter the list.
    /// </summary>
    public static IReadOnlyList<DeliveryLadderRow> QualityLadder(ScopeSummary s)
    {
        int C(ScopeCoverage c) => s.ByCoverage.GetValueOrDefault(c);
        return
        [
            new("criteria", "Acceptance criteria defined", s.AcceptanceCriteria.ToString(), s.AcceptanceCriteria,
                $"{s.UserStories} user stories · {s.Scenarios} scenarios · {s.Requirements} requirements in scope", DeliveryDashboardTone.Progress, null),
            new("linked", "Explicitly linked tests", C(ScopeCoverage.AutomatedCoverage).ToString(), C(ScopeCoverage.AutomatedCoverage),
                "Items with a test that names them (an explicit link) — test design, not execution", DeliveryDashboardTone.Ready, ScopeCoverage.AutomatedCoverage),
            new("candidate", "Candidate links only", C(ScopeCoverage.CandidateCoverageOnly).ToString(), C(ScopeCoverage.CandidateCoverageOnly),
                "A test may relate by name or proximity — not coverage until confirmed in Relationship Review", DeliveryDashboardTone.Review, ScopeCoverage.CandidateCoverageOnly),
            new("none", "No known coverage", C(ScopeCoverage.NoKnownCoverage).ToString(), C(ScopeCoverage.NoKnownCoverage),
                "Assessed: no test relates to these items", DeliveryDashboardTone.Blocker, ScopeCoverage.NoKnownCoverage),
            new("not-assessed", "Coverage not assessed", C(ScopeCoverage.NotAssessed).ToString(), C(ScopeCoverage.NotAssessed),
                "No relationship analysis covers these items yet", DeliveryDashboardTone.Neutral, ScopeCoverage.NotAssessed),
            new("executed", "Tests executed", "Not assessed", null, "No execution evidence is mapped to the scope", DeliveryDashboardTone.Neutral, null),
            new("passed", "Tests passed", "Not assessed", null, "Passed needs executed results for this scope and environment", DeliveryDashboardTone.Neutral, null),
        ];
    }

    public static string DefaultOverviewSection(DeliveryRole role) => role switch
    {
        DeliveryRole.ScrumMaster => "features",
        DeliveryRole.TestLead => "risks",
        _ => "overview",
    };

    public static string DefaultReportsTab(DeliveryRole role) => role switch
    {
        DeliveryRole.ScrumMaster => "changes",
        DeliveryRole.TestLead => "scope",
        _ => "overview",
    };

    public static string Label(DeliveryRole role) => role switch
    {
        DeliveryRole.ScrumMaster => "Scrum Master",
        DeliveryRole.TestLead => "Test Lead",
        _ => "Project Manager",
    };

    // The role is a per-browser convenience (which view opens first), kept in localStorage; a failure to read or write it changes nothing else.
    private const string RoleKey = "birknext:delivery-role";

    public static async Task<DeliveryRole?> ReadRoleAsync(IJSRuntime js)
    {
        try { return Enum.TryParse<DeliveryRole>(await js.InvokeAsync<string?>("localStorage.getItem", RoleKey), out var role) ? role : null; }
        catch (Exception e) when (e is JSException or InvalidOperationException or TaskCanceledException or JSDisconnectedException) { return null; }
    }

    public static async Task WriteRoleAsync(IJSRuntime js, DeliveryRole role)
    {
        try { await js.InvokeVoidAsync("localStorage.setItem", RoleKey, role.ToString()); }
        catch (Exception e) when (e is JSException or InvalidOperationException or TaskCanceledException or JSDisconnectedException) { }
    }
}
