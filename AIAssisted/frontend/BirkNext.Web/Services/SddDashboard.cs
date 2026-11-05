using BirkNext.ProjectActivity;
using BirkNext.Web.Models;
using BirkNext.Web.Services.ProjectActivity;
using BirkNext.Web.Services.ProjectEvidence;
using BirkNext.Web.Services.ProjectImport;

namespace BirkNext.Web.Services;

/// <summary>One slice of a dashboard ring chart: a state, its count and the tone it is drawn with (never colour-only: the legend names it).</summary>
public sealed record SddChartSlice(string Key, string Label, int Count, string Tone, string Description);

/// <summary>A ring chart's data: slices that are counted, plus what was left out and why (Not applicable is excluded, never a gap).</summary>
public sealed record SddChart(string Title, IReadOnlyList<SddChartSlice> Slices, int Excluded, string? ExcludedNote, string EmptyText)
{
    public int Total => Slices.Sum(s => s.Count);
    public string Summary => Total == 0 ? EmptyText : string.Join(", ", Slices.Where(s => s.Count > 0).Select(s => $"{s.Count} {s.Label.ToLowerInvariant()}"));
}

/// <summary>The hero: overall readiness in one word, the counts that decide it, and when anything was last assessed.</summary>
public sealed record SddHero(string Readiness, DashboardStatus Status, string Detail, int Blockers, int ReviewItems, int AvailableReviews, int CompletedReviews, string LastAssessment);

/// <summary>One imported package as the dashboard lists it: every role it provides (capability-based), what its document count means, its association.</summary>
public sealed record SddPackageRow(string ImportId, string Name, IReadOnlyList<string> Roles, string Documents, string Association, string Tone, string Detail, bool NeedsResolution);

/// <summary>What kind of event a timeline row is (drives the icon colour), independent of severity (the badge).</summary>
public enum SddActivityKind { Import, System, ReviewDone, NeedsReview, Failed, Info }

/// <summary>One timeline row: a single event, or a safely grouped batch of imports (members listed when expanded).</summary>
public sealed record SddActivityRow(string Key, SddActivityKind Kind, string Icon, string Title, string? Summary, string Badge, string BadgeTone,
    DateTimeOffset At, string? Route, string LinkLabel, string? Environment, IReadOnlyList<SddActivityRow> Members)
{
    public bool IsGroup => Members.Count > 0;
}

/// <summary>
/// SDD Quality &amp; Readiness: the hero, the two charts, the package list and the activity timeline, all derived from canonical state the rest of
/// BirkNext already uses — the Review availability rows and attention items (<see cref="DashboardPresentation"/>), the review navigator's
/// capabilities (<see cref="WorkflowReadiness.Capabilities"/>), Project Evidence associations (<see cref="PackageAssociation"/>) and persisted
/// project activity. No dashboard-only readiness rule: this class only counts and words what those sources say. Pure.
/// </summary>
public static class SddDashboard
{
    public const int ActivityLimit = 5;
    /// <summary>Imports grouped into one row only when they are consecutive, of the same application and role, and this close together.</summary>
    public static readonly TimeSpan ImportBatchWindow = TimeSpan.FromMinutes(15);
    public const string ResolveAssociationKey = "package-association";

    // ── Hero ──────────────────────────────────────────────────────────────────────────────────────

    public static SddHero Hero(IReadOnlyList<DashboardReadinessRow> readiness, IReadOnlyList<DashboardAttentionItem> attention, SddChart reviews,
        IReadOnlyList<ProjectActivityItem>? activity, DateTimeOffset now)
    {
        var blockers = attention.Count(a => !a.Informational);
        // The same inputs the Evidence readiness chart counts, so the hero and the chart never disagree.
        var evidence = EvidenceReadiness(readiness);
        var counted = evidence.Total;
        var ready = evidence.Slices.First(s => s.Key == "ready").Count;
        var reviewItems = reviews.Slices.FirstOrDefault(s => s.Key == "needs-review")?.Count ?? 0;
        var available = reviews.Slices.FirstOrDefault(s => s.Key == "ready")?.Count ?? 0;
        var completed = reviews.Slices.FirstOrDefault(s => s.Key == "completed")?.Count ?? 0;
        var (label, status) = counted == 0 ? ("Not assessed", DashboardStatus.NotAssessed)
            : blockers > 0 ? ("Needs attention", DashboardStatus.AttentionNeeded)
            : ready == counted ? ("Ready", DashboardStatus.Available)
            : ("Partial", DashboardStatus.Partial);
        var detail = counted == 0 ? "No project evidence is loaded yet."
            : $"{ready} of {counted} inputs ready{(blockers > 0 ? $" · {blockers} blocker{S(blockers)} to resolve" : "")}. Ready means the input exists — not that it was reviewed.";
        var last = activity?.Where(a => a.Category == ProjectActivityCategory.Review && a.Status is ProjectActivityStatus.Completed or ProjectActivityStatus.Attention)
            .OrderByDescending(a => a.OccurredAt).FirstOrDefault();
        return new(label, status, detail, blockers, reviewItems, available, completed,
            last is null ? "No review has run yet" : $"{last.Title} · {ProjectActivityPresentation.When(last.OccurredAt, now)}");
    }

    // ── Charts ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Evidence readiness from the Review availability rows (the same rows the table shows): Ready, Partial, Missing, Not assessed.</summary>
    public static SddChart EvidenceReadiness(IReadOnlyList<DashboardReadinessRow> rows)
    {
        var inputs = rows.Where(r => r.Key != "dqr").ToList();
        int Count(params DashboardStatus[] statuses) => inputs.Count(r => statuses.Contains(r.Status));
        var excluded = Count(DashboardStatus.NotApplicable);
        return new("Evidence readiness",
        [
            new("ready", "Ready", Count(DashboardStatus.Available, DashboardStatus.ReadyToReview, DashboardStatus.Passed), "ready", "The input exists and can be reviewed."),
            new("partial", "Partial", Count(DashboardStatus.Partial, DashboardStatus.InProgress), "partial", "Present for part of the project, or incomplete."),
            new("missing", "Missing", Count(DashboardStatus.NeedsSetup, DashboardStatus.Failed, DashboardStatus.Unavailable, DashboardStatus.AttentionNeeded), "missing", "Required input that is absent, failed or needs setup."),
            new("not-assessed", "Not assessed", Count(DashboardStatus.NotAssessed, DashboardStatus.Loading), "neutral", "Nothing has assessed this input yet."),
        ], excluded, excluded > 0 ? $"{excluded} not applicable to this project, not counted" : null, "No project evidence yet.");
    }

    /// <summary>
    /// Review execution from the review navigator's capabilities: Completed (a review decision is recorded), Needs review (evidence changed
    /// since), Ready to run (prerequisites met, not run yet — available is not ready-to-execute until then), Blocked (waiting for setup or evidence).
    /// Not applicable reviews are excluded.
    /// </summary>
    public static SddChart ReviewExecution(IReadOnlyList<ReviewCapability> capabilities)
    {
        var applicable = capabilities.Where(c => c.Applicability != ReviewApplicabilityState.NotApplicable && c.Readiness != ReviewReadiness.NotApplicable).ToList();
        var completed = applicable.Count(c => c.Decision == ReviewDecisionState.Reviewed);
        var changed = applicable.Count(c => c.Decision == ReviewDecisionState.EvidenceChanged || c.Decision != ReviewDecisionState.Reviewed && c.Readiness == ReviewReadiness.EvidenceChanged);
        var ready = applicable.Count(c => c.Decision is ReviewDecisionState.NotReviewed or ReviewDecisionState.NoDecisionTracked && c.Readiness == ReviewReadiness.Ready);
        var blocked = applicable.Count(c => c.Decision is ReviewDecisionState.NotReviewed or ReviewDecisionState.NoDecisionTracked && c.Readiness is ReviewReadiness.NeedsSetup or ReviewReadiness.WaitingForEvidence);
        var excluded = capabilities.Count - applicable.Count;
        return new("Review execution",
        [
            new("completed", "Completed", completed, "ready", "A review decision is recorded."),
            new("needs-review", "Needs review", changed, "partial", "The evidence changed after the last review."),
            new("ready", "Ready to run", ready, "info", "Prerequisites are met; the review has not run yet."),
            new("blocked", "Blocked", blocked, "missing", "Waiting for setup or evidence (for example a Target Environment)."),
        ], excluded, excluded > 0 ? $"{excluded} not applicable, not counted" : null, "No reviews apply yet.");
    }

    // ── Packages ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every imported package with all the roles its capabilities give it (an infrastructure package with XSD/JSON schemas is also contracts
    /// evidence). Documents are counted only where the role expects them; for infrastructure/contracts/supporting packages their absence is
    /// "not applicable", never a gap. Packages are never offered for re-import: an uncertain one is "Resolve package association".
    /// </summary>
    public static IReadOnlyList<SddPackageRow> Packages(IReadOnlyList<PackageAssociation> associations) =>
        [.. associations.OrderBy(a => RoleOrder(a.Role)).ThenBy(a => a.Package.DisplayName, StringComparer.OrdinalIgnoreCase).Select(a =>
        {
            var p = a.Package;
            var roles = new List<string> { ApplicationContexts.RoleLabel(a.Role) };
            foreach (var (capability, label) in CapabilityRoles)
                if (p.Has(capability) && !roles.Contains(label) && !(capability == ProjectEvidenceCapability.Documentation && a.Role == PackageEvidenceRole.Documentation))
                    roles.Add(label);
            var expectsDocuments = a.Role is PackageEvidenceRole.PrimaryApplicationSource or PackageEvidenceRole.RelatedApplicationSource or PackageEvidenceRole.Documentation;
            var documents = p.DocumentCount > 0 ? $"{p.DocumentCount} SDD document{S(p.DocumentCount)}"
                : expectsDocuments ? "No SDD project documents detected" : "SDD documents: not applicable";
            var resolve = a.State == EvidenceAssociationState.NeedsReview;
            var tone = a.State switch
            {
                EvidenceAssociationState.Associated => "ready",
                EvidenceAssociationState.NeedsReview => "partial",
                _ => "neutral",
            };
            return new SddPackageRow(p.ImportId, p.DisplayName, roles, documents, resolve ? "Needs association" : a.StateLabel == ApplicationContexts.RoleLabel(a.Role) ? "Associated with application" : a.StateLabel,
                tone, a.Detail, resolve);
        })];

    /// <summary>One attention item for packages whose association is uncertain (never "import again": they are imported).</summary>
    public static DashboardAttentionItem? AssociationAttention(IReadOnlyList<PackageAssociation> associations)
    {
        var pending = associations.Where(a => a.State == EvidenceAssociationState.NeedsReview).ToList();
        if (pending.Count == 0) return null;
        return new(ResolveAssociationKey, $"Resolve package association: {pending.Count} imported package{S(pending.Count)} need{(pending.Count == 1 ? "s" : "")} a decision",
            "The packages are imported; BirkNext cannot tell with certainty which application or role they belong to. Confirm or exclude them — no re-import is needed.",
            "Resolve package association", DashboardQualitySummary.ProjectEvidenceHref,
            Items: [.. pending.Select(a => $"{a.Package.DisplayName} — {a.Detail}")]);
    }

    private static readonly (ProjectEvidenceCapability Capability, string Label)[] CapabilityRoles =
    [
        (ProjectEvidenceCapability.Documentation, "SDD documents"),
        (ProjectEvidenceCapability.Infrastructure, ApplicationContexts.RoleLabel(PackageEvidenceRole.Infrastructure)),
        (ProjectEvidenceCapability.ContractSchema, ApplicationContexts.RoleLabel(PackageEvidenceRole.Contracts)),
        (ProjectEvidenceCapability.GeneratedDocumentation, ApplicationContexts.RoleLabel(PackageEvidenceRole.GeneratedDocumentation)),
    ];

    private static int RoleOrder(PackageEvidenceRole role) => role switch
    {
        PackageEvidenceRole.PrimaryApplicationSource => 0,
        PackageEvidenceRole.RelatedApplicationSource => 1,
        PackageEvidenceRole.SupportingSource => 2,
        PackageEvidenceRole.Documentation => 3,
        PackageEvidenceRole.Contracts => 4,
        PackageEvidenceRole.Infrastructure => 5,
        PackageEvidenceRole.GeneratedDocumentation => 6,
        _ => 7,
    };

    // ── Recent activity ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Newest first, at most <paramref name="limit"/> rows. Consecutive imports of the same application and role within
    /// <see cref="ImportBatchWindow"/> become one expandable row ("Imported 5 infrastructure packages"); anything else stays its own row.
    /// An import is "Imported", never "Attention": application-wide setup items are not the package's own problem.
    /// </summary>
    public static IReadOnlyList<SddActivityRow> Timeline(IReadOnlyList<ProjectActivityItem> items, DateTimeOffset now, int limit = ActivityLimit) =>
        [.. AllRows(items, now).Take(limit)];

    /// <summary>The grouped timeline without a limit (for "View all activity").</summary>
    public static IReadOnlyList<SddActivityRow> AllRows(IReadOnlyList<ProjectActivityItem> items, DateTimeOffset now)
    {
        var ordered = items.OrderByDescending(a => a.OccurredAt).ToList();
        var rows = new List<SddActivityRow>();
        for (var i = 0; i < ordered.Count;)
        {
            var first = ordered[i];
            var batch = new List<ProjectActivityItem> { first };
            if (IsImport(first))
                while (i + batch.Count < ordered.Count && ordered[i + batch.Count] is var next && IsImport(next)
                       && next.ProjectContextId == first.ProjectContextId && RoleOf(next) == RoleOf(first)
                       && batch[^1].OccurredAt - next.OccurredAt <= ImportBatchWindow)
                    batch.Add(next);
            rows.Add(batch.Count > 1 ? Group(batch) : Row(first));
            i += batch.Count;
        }
        return rows;
    }

    public static SddActivityRow Row(ProjectActivityItem a)
    {
        if (a.Type == ProjectActivityType.PerformanceTlsTrustRepaired)
            return new(a.Id.ToString(), SddActivityKind.System, "⚙", "Local TLS trust repair applied", TlsSummary(a.Summary), "Locally repaired", "system",
                a.OccurredAt, a.Route, "View TLS trust settings", a.EnvironmentName, []);
        if (IsImport(a))
            return new(a.Id.ToString(), SddActivityKind.Import, "↓", a.Title, ImportSummary(a.Summary), "Imported", "import", a.OccurredAt, a.Route, "View import", a.EnvironmentName, []);
        var kind = a.Status switch
        {
            ProjectActivityStatus.Failed => SddActivityKind.Failed,
            ProjectActivityStatus.Attention => SddActivityKind.NeedsReview,
            ProjectActivityStatus.Completed => SddActivityKind.ReviewDone,
            _ => SddActivityKind.Info,
        };
        var (badge, tone) = kind switch
        {
            SddActivityKind.Failed => ("Failed", "failed"),
            SddActivityKind.NeedsReview => ("Needs review", "needs-review"),
            SddActivityKind.ReviewDone => ("Completed", "done"),
            _ => (ProjectActivityPresentation.Badge(a.Status), "neutral"),
        };
        var icon = kind switch
        {
            SddActivityKind.Failed => "✕",
            SddActivityKind.NeedsReview => "!",
            SddActivityKind.ReviewDone => "✓",
            _ => ProjectActivityPresentation.Icon(a.Category),
        };
        return new(a.Id.ToString(), kind, icon, a.Title, Join(a.Summary, a.Outcome is { Length: > 0 } o ? $"Outcome: {o}" : null), badge, tone, a.OccurredAt, a.Route,
            ProjectActivityPresentation.LinkLabel(a), a.EnvironmentName, []);
    }

    private static SddActivityRow Group(IReadOnlyList<ProjectActivityItem> batch)
    {
        var members = batch.Select(Row).ToList();
        var role = RoleOf(batch[0]);
        var noun = role switch
        {
            "Infrastructure evidence" => "infrastructure packages",
            "Supporting/shared source" => "supporting source packages",
            "Contracts/schemas" => "contract packages",
            "Documentation" => "documentation packages",
            _ => "packages",
        };
        var title = $"Imported {batch.Count} {noun}";
        return new($"group-{batch[0].Id}", SddActivityKind.Import, "↓", title, string.Join(", ", batch.Select(b => b.PackageName ?? b.Title.Replace("Imported ", ""))),
            "Imported", "import", batch[0].OccurredAt, batch[0].Route, "View imports", null, members);
    }

    private static bool IsImport(ProjectActivityItem a) => a.Type == ProjectActivityType.ImportCompleted;

    /// <summary>The package role an import summary starts with ("Infrastructure evidence · …"), as the import recorded it.</summary>
    private static string RoleOf(ProjectActivityItem a) => (a.Summary ?? "").Split(" · ")[0];

    /// <summary>
    /// An import's line without what does not belong to the package: application-wide attention counts, and "no project documents" / "no
    /// application code" for roles that never carry them (infrastructure, contracts, supporting source). Events recorded before this rule keep
    /// their stored text; only these segments are left out when shown.
    /// </summary>
    public static string? ImportSummary(string? summary)
    {
        if (summary is null) return null;
        var parts = summary.Split(" · ").ToList();
        var role = parts.FirstOrDefault() ?? "";
        var documentsExpected = role is "Primary application source" or "Related application/system source" or "Documentation";
        parts.RemoveAll(p => p.EndsWith("need attention", StringComparison.Ordinal) || p.EndsWith("needs attention", StringComparison.Ordinal)
            || p == "no application code" && role != "Primary application source"
            || p == "no project documents" && !documentsExpected);
        return string.Join(" · ", parts.Select(p => p == "no project documents" ? "No SDD project documents detected" : p));
    }

    private static string? TlsSummary(string? summary)
    {
        var host = summary?.Split(" · ")[0];
        return host is { Length: > 0 }
            ? $"{host} · a missing intermediate certificate was validated against a trusted root and trusted locally · the server's certificate chain remains incomplete"
            : "A missing intermediate certificate was trusted locally · the server's certificate chain remains incomplete";
    }

    private static string? Join(string? a, string? b) => a is null ? b : b is null ? a : $"{a} · {b}";
    private static string S(int n) => n == 1 ? "" : "s";
}
