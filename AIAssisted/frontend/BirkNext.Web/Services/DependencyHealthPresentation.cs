using BirkNext.Dependencies;

namespace BirkNext.Web.Services;

/// <summary>Dependency table filters. "Issues" = what a test lead should look at; being behind alone is not an issue.</summary>
public enum HealthFilter { All, Issues, Security, Outdated, DeprecatedOrUnlisted, LicenseUnknown, RegistryUnavailable }

public sealed record HealthTile(string Label, string Value, string Detail, string Tone, string TestId);

/// <summary>
/// Pure projection of a dependency health run for the page and export. Wording follows the evidence: "Latest published stable" (never
/// recommended), "No matched advisories observed" (never safe), "Security evidence unavailable" (never 0), license policy "not configured"
/// (never pass), and every value carries its source and retrieval time.
/// </summary>
public static class DependencyHealthPresentation
{
    public static string Tone(VersionStatus status) => status switch
    {
        VersionStatus.Current or VersionStatus.NewerThanLatestStable => "ok",
        VersionStatus.MajorBehind => "attention",
        VersionStatus.PatchBehind or VersionStatus.MinorBehind => "info",
        _ => "muted",
    };

    public static string Tone(AdvisoryState state) => state switch
    {
        AdvisoryState.Affected => "danger",
        AdvisoryState.AdvisorySourceUnavailable => "attention",
        AdvisoryState.NoMatchedAdvisoryObserved or AdvisoryState.NotAffectedByMatchedAdvisory => "info",
        _ => "muted",
    };

    public static string Tone(RegistryState state) => state switch
    {
        RegistryState.Observed => "info",
        RegistryState.Unlisted or RegistryState.VersionNotFound or RegistryState.PackageNotFound => "attention",
        RegistryState.NotAssessed => "muted",
        _ => "attention",
    };

    public static string Tone(LicenseState state) => state is LicenseState.Detected or LicenseState.Multiple ? "info" : "muted";

    public static string Tone(ObservationKind kind) => kind switch
    {
        ObservationKind.Finding or ObservationKind.EvidenceConflict => "attention",
        ObservationKind.Limitation or ObservationKind.MissingEvidence => "info",
        _ => "muted",
    };

    /// <summary>Health categories: "Ready" means assessed, shown neutrally — being behind or having no advisory is not a green pass.</summary>
    public static string Tone(ReviewCategoryState state) => state switch
    {
        ReviewCategoryState.NeedsReview or ReviewCategoryState.Issue => "attention",
        ReviewCategoryState.Ready or ReviewCategoryState.Partial => "info",
        _ => "muted",
    };

    public static string CategoryLabel(ReviewCategoryState state) => state switch
    {
        ReviewCategoryState.Ready => "Assessed",
        ReviewCategoryState.Missing => "Missing evidence",
        ReviewCategoryState.Issue => "Unavailable",
        _ => DependencyLabels.Category(state),
    };

    public static string Tone(ComparisonState state) => state switch
    {
        ComparisonState.EvidenceConflict => "attention",
        ComparisonState.Matched or ComparisonState.DeclaredAndPresent => "ok",
        ComparisonState.NotComparable => "muted",
        _ => "info",
    };

    public static string Tone(RemediationPolicyState state) => state switch
    {
        RemediationPolicyState.PermittedByPolicy or RemediationPolicyState.PermittedWithinSchedule => "info",
        RemediationPolicyState.NotAssessed or RemediationPolicyState.NotAssessable => "muted",
        _ => "attention",
    };

    public static string Tone(InventoryFreshness freshness) => freshness switch { InventoryFreshness.Current => "info", InventoryFreshness.Stale => "attention", _ => "muted" };

    public static string Tone(AutomationState state) => state switch
    {
        AutomationState.Observed => "info",
        AutomationState.Unauthorized or AutomationState.ProviderUnavailable => "attention",
        _ => "muted",
    };

    public static bool IsIssue(DependencyHealthItem item) =>
        item.Security.State == AdvisoryState.Affected || item.Conflicts.Count > 0 || item.Registry.Deprecation is not null
        || item.Registry.State is RegistryState.Unlisted or RegistryState.VersionNotFound || item.License.Policy == LicensePolicyState.Denied
        || item.Remediation?.State is RemediationPolicyState.BlockedByPolicy or RemediationPolicyState.BlockedByVersionConstraint or RemediationPolicyState.IgnoredByRenovate;

    public static bool Matches(DependencyHealthItem item, HealthFilter filter) => filter switch
    {
        HealthFilter.Issues => IsIssue(item),
        HealthFilter.Security => item.Security.State is AdvisoryState.Affected or AdvisoryState.AdvisorySourceUnavailable or AdvisoryState.VersionNotComparable,
        HealthFilter.Outdated => item.VersionStatus is VersionStatus.PatchBehind or VersionStatus.MinorBehind or VersionStatus.MajorBehind,
        HealthFilter.DeprecatedOrUnlisted => item.Registry.Deprecation is not null || item.Registry.State == RegistryState.Unlisted,
        HealthFilter.LicenseUnknown => item.License.State is LicenseState.Unknown or LicenseState.NotDeclared,
        HealthFilter.RegistryUnavailable => item.VersionStatus == VersionStatus.RegistryUnavailable,
        _ => true,
    };

    public static string FilterLabel(HealthFilter filter) => filter switch
    {
        HealthFilter.DeprecatedOrUnlisted => "Deprecated/unlisted",
        HealthFilter.LicenseUnknown => "License unknown",
        HealthFilter.RegistryUnavailable => "Registry unavailable",
        _ => filter.ToString(),
    };

    /// <summary>Issues first (affected, conflicts, deprecated …), then by package name.</summary>
    public static IReadOnlyList<DependencyHealthItem> Items(DependencyHealthRun run, HealthFilter filter, string? search) => run.Items
        .Where(i => Matches(i, filter) && (string.IsNullOrWhiteSpace(search) || i.Dependency.PackageName.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)))
        .OrderByDescending(i => i.Security.State == AdvisoryState.Affected).ThenByDescending(IsIssue).ThenBy(i => i.Dependency.PackageName, StringComparer.OrdinalIgnoreCase).ToList();

    public static int Count(DependencyHealthRun run, HealthFilter filter) => run.Items.Count(i => Matches(i, filter));

    public static string ObservedVersion(InventoryDependency dep) => dep.Version ?? (dep.VersionRange is { } range ? $"{range} (range)" : "Unknown");

    public static string SourceText(InventoryDependency dep) => $"{DependencyHealthLabels.Stage(dep.Stage)} · {DependencyHealthLabels.Relationship(dep.Relationship)}";

    public static string SecurityText(DependencyHealthItem item) => item.Security.State switch
    {
        AdvisoryState.Affected => $"Affected ({item.Security.Advisories.Count(a => a.AffectsObservedVersion == true)})",
        _ => DependencyHealthLabels.Advisory(item.Security.State),
    };

    public static string LicenseText(DependencyHealthItem item) => item.License.State switch
    {
        LicenseState.Detected => item.License.Licenses[0],
        LicenseState.Multiple => string.Join(" / ", item.License.Licenses),
        var s => DependencyHealthLabels.License(s),
    };

    public static string RegistryText(DependencyHealthItem item) =>
        item.Registry.Deprecation is not null ? $"{DependencyHealthLabels.Registry(item.Registry.State)} · Deprecated" : DependencyHealthLabels.Registry(item.Registry.State);

    public static string Utc(DateTimeOffset? at) => at is { } t ? $"{t.ToUniversalTime():yyyy-MM-dd HH:mm} UTC" : "Unknown";

    public static string Date(DateTimeOffset? at) => at is { } t ? t.ToUniversalTime().ToString("yyyy-MM-dd") : "—";

    /// <summary>How old the registry/advisory evidence is at run time, and whether it came from cache.</summary>
    public static string EvidenceAge(DependencyHealthItem item, DateTimeOffset completedAt)
    {
        var at = new[] { item.Registry.RetrievedAt, item.Security.RetrievedAt }.Where(t => t is not null).Min();
        if (at is null) return "Not retrieved";
        var minutes = Math.Max(0, (int)(completedAt - at.Value).TotalMinutes);
        return $"{(minutes < 1 ? "At run time" : $"{minutes} min before run")}{(item.Registry.FromCache ? " (cached)" : "")}";
    }

    public static string Age(int? days) => days switch
    {
        null => "Unknown",
        < 60 => $"{days} days",
        < 730 => $"{days / 30} months ({days} days)",
        _ => $"{days / 365} years ({days} days)",
    };

    public static IReadOnlyList<HealthTile> Tiles(DependencyHealthRun run)
    {
        var s = run.Summary;
        ReviewCategory? C(string name) => run.Categories.FirstOrDefault(c => c.Name == name);
        var updates = s.PatchBehind + s.MinorBehind + s.MajorBehind;
        var licensePct = s.Dependencies == 0 ? 0 : 100 * s.LicenseDetected / s.Dependencies;
        return
        [
            new("Inventory", run.Freshness.ToString(), $"{run.Inventory.Name} · captured {Utc(run.Inventory.CapturedAt)}", Tone(run.Freshness), "dh-tile-inventory"),
            new("Dependencies", s.Dependencies.ToString(), $"{(s.Declarations > s.Dependencies ? $"{s.Declarations} declarations · " : "")}{DependencyHealthLabels.Source(run.Inventory.SourceType)} · {DependencyHealthLabels.Stage(run.Inventory.Stage)}", "muted", "dh-tile-dependencies"),
            new("Version health", s.RegistryObserved + s.RegistryNotFound + s.RegistryUnavailable == 0 ? "Not assessed" : $"{updates} updates observed",
                $"{s.PatchBehind} patch · {s.MinorBehind} minor · {s.MajorBehind} major behind · registry {s.RegistryObserved} observed, {s.RegistryNotFound} not found, {s.RegistryUnavailable} unavailable", Tone(C("Version health")?.State ?? ReviewCategoryState.NotAssessed), "dh-tile-version"),
            new("Security advisories", C("Security advisories")?.State == ReviewCategoryState.NotAssessed ? "Not assessed" : $"{s.AffectedDependencies} affected",
                $"{s.AdvisorySourceUnavailable} advisory source unavailable · {s.NoMatchedAdvisory} no matched advisory observed", s.AffectedDependencies > 0 ? "danger" : Tone(C("Security advisories")?.State ?? ReviewCategoryState.NotAssessed), "dh-tile-security"),
            new("Deprecated / unlisted", C("Deprecated / unlisted")?.State == ReviewCategoryState.NotAssessed ? "Not assessed" : $"{s.DeprecatedOrUnlisted} observed", "Registry metadata, in NuGet's terms", "muted", "dh-tile-deprecated"),
            new("License metadata", $"{licensePct}% available", $"{s.LicenseDetected} detected · {s.LicenseUnknown} unknown or not declared", "muted", "dh-tile-license"),
            new("Renovate policy", CategoryLabel(C("Renovate policy")?.State ?? ReviewCategoryState.NotAssessed), C("Renovate policy")?.Detail ?? "", Tone(C("Renovate policy")?.State ?? ReviewCategoryState.NotAssessed), "dh-tile-policy"),
            new("Automation", DependencyHealthLabels.Automation(run.Automation.State), run.Automation.Provider, Tone(run.Automation.State), "dh-tile-automation"),
            new("Deployment comparison", CategoryLabel(C("Deployment comparison")?.State ?? ReviewCategoryState.NotAssessed), C("Deployment comparison")?.Detail ?? "", Tone(C("Deployment comparison")?.State ?? ReviewCategoryState.NotAssessed), "dh-tile-deployment"),
        ];
    }

    public static string Headline(DependencyHealthRun run) =>
        $"Dependency health · {run.Summary.Dependencies} dependencies from {run.Inventory.Name} · {run.Summary.AffectedDependencies} affected by a matched advisory · "
        + $"{run.Observations.Count(o => o.Kind is ObservationKind.Finding or ObservationKind.EvidenceConflict)} finding(s) · {run.Observations.Count(o => o.Kind == ObservationKind.Limitation)} limitation(s)";

    /// <summary>Findings first, then conflicts, missing evidence, limitations and plain observations.</summary>
    public static IReadOnlyList<HealthObservation> OrderedObservations(DependencyHealthRun run) =>
        run.Observations.OrderBy(o => o.Kind).ThenBy(o => o.Severity).ThenBy(o => o.PackageName, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>One option line for an inventory picker: name · source · captured · build/environment · count · freshness.</summary>
    public static string InventoryOption(InventorySummary i) =>
        $"{i.Name} · {DependencyHealthLabels.Source(i.SourceType)} · captured {(i.CapturedAt is { } c ? c.ToUniversalTime().ToString("yyyy-MM-dd") : "unknown")}"
        + (i.BuildId is { } b ? $" · build {b}" : "") + (i.Environment is { } e ? $" · {e}" : "") + $" · {i.Dependencies} deps · {i.Freshness}";

    public static string CurrentToProposed(DependencyPullRequest pr) => $"{pr.FromVersion ?? "current not in inventory"} → {pr.ToVersion ?? "(see PR)"}";
}
