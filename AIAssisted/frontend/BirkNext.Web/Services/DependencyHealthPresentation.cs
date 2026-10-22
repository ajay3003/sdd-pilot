using BirkNext.Dependencies;

namespace BirkNext.Web.Services;

/// <summary>Dependency table filters. "Issues" = what a test lead should look at; being behind alone is not an issue.</summary>
public enum HealthFilter { All, Issues, Security, Outdated, DeprecatedOrUnlisted, LicenseUnknown, RegistryUnavailable }

/// <summary>The sections of a stored health/comparison result. Each evidence mode shows only its own (no false uniformity).</summary>
public enum HealthResultSection { Summary, Findings, Differences, Dependencies, Evidence, Automation, Target, History }

/// <summary>How much of the inventory one kind of evidence covers ("Registry 31/32"), with what the rest is.</summary>
public sealed record EvidenceCoverage(string Check, int Covered, int Total, string Detail, string TestId)
{
    public string Value => Total == 0 ? "—" : $"{Covered}/{Total}";
}

/// <summary>Observations by meaning: review-worthy findings, plain observations (behind, deprecated), and missing evidence/limitations.</summary>
public sealed record HealthObservationGroups(IReadOnlyList<HealthObservation> NeedsReview, IReadOnlyList<HealthObservation> Observed, IReadOnlyList<HealthObservation> MissingEvidence);

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

    /// <summary>Mode-specific navigation: Existing inventory, SBOM (no automation unless observed) and Deployed evidence (differences + target).</summary>
    public static IReadOnlyList<HealthResultSection> Sections(string? mode, DependencyHealthRun run) => mode switch
    {
        DependencyEvidenceModes.Sbom => run.Automation.State == AutomationState.Observed
            ? [HealthResultSection.Summary, HealthResultSection.Findings, HealthResultSection.Dependencies, HealthResultSection.Evidence, HealthResultSection.Automation, HealthResultSection.History]
            : [HealthResultSection.Summary, HealthResultSection.Findings, HealthResultSection.Dependencies, HealthResultSection.Evidence, HealthResultSection.History],
        DependencyEvidenceModes.Deployed => [HealthResultSection.Summary, HealthResultSection.Differences, HealthResultSection.Dependencies, HealthResultSection.Evidence, HealthResultSection.Target, HealthResultSection.History],
        _ => [HealthResultSection.Summary, HealthResultSection.Findings, HealthResultSection.Dependencies, HealthResultSection.Evidence, HealthResultSection.Automation, HealthResultSection.History],
    };

    /// <summary>Needs review = a matched policy/advisory finding or an evidence conflict; Observed = facts such as being behind or deprecated;
    /// Missing evidence = what could not be retrieved or assessed. Never all equivalent findings.</summary>
    public static HealthObservationGroups Groups(DependencyHealthRun run)
    {
        var ordered = OrderedObservations(run);
        return new([.. ordered.Where(o => o.Kind is ObservationKind.Finding or ObservationKind.EvidenceConflict)],
            [.. ordered.Where(o => o.Kind == ObservationKind.Observed)],
            [.. ordered.Where(o => o.Kind is ObservationKind.MissingEvidence or ObservationKind.Limitation)]);
    }

    /// <summary>Evidence coverage per check, counted over the reviewed package@version items.</summary>
    public static IReadOnlyList<EvidenceCoverage> Coverage(DependencyHealthRun run)
    {
        var items = run.Items;
        var total = items.Count;
        var registry = items.Count(i => i.Registry.State is RegistryState.Observed or RegistryState.Unlisted or RegistryState.VersionNotFound or RegistryState.PackageNotFound);
        var registryUnavailable = items.Count(i => i.Registry.State is RegistryState.RegistryUnavailable or RegistryState.Timeout or RegistryState.Unauthorized or RegistryState.RateLimited or RegistryState.ProviderError);
        var advisoryUsable = items.Count(i => i.Security.State is AdvisoryState.Affected or AdvisoryState.NotAffectedByMatchedAdvisory or AdvisoryState.NoMatchedAdvisoryObserved);
        var advisoryUnavailable = items.Count(i => i.Security.State == AdvisoryState.AdvisorySourceUnavailable);
        var license = items.Count(i => i.License.State is LicenseState.Detected or LicenseState.Multiple);
        var version = items.Count(i => i.VersionStatus is VersionStatus.Current or VersionStatus.NewerThanLatestStable or VersionStatus.PatchBehind or VersionStatus.MinorBehind or VersionStatus.MajorBehind);
        var provenance = items.Count(i => i.Locations.Count > 0 || i.Dependency.Location is not null);
        return
        [
            new("Registry", registry, total, $"{registryUnavailable} unavailable · {total - registry - registryUnavailable} not assessed", "dh-coverage-registry"),
            new("Advisory", advisoryUsable, total, $"{advisoryUnavailable} advisory source unavailable · {total - advisoryUsable - advisoryUnavailable} not assessed or not comparable", "dh-coverage-advisory"),
            new("License", license, total, $"{total - license} unknown or not declared — missing evidence, not non-compliance", "dh-coverage-license"),
            new("Version metadata", version, total, $"{total - version} not comparable, unavailable or not assessed", "dh-coverage-version"),
            new("Source provenance", provenance, total, "Items with an inventory location (file, SBOM component or artifact)", "dh-coverage-provenance"),
        ];
    }

    /// <summary>"0 matched advisories" plus what the rest is — never "0 affected" as if every dependency was cleared.</summary>
    public static string SecurityLine(DependencyHealthRun run)
    {
        var s = run.Summary;
        if (run.Categories.FirstOrDefault(c => c.Name == "Security advisories")?.State == ReviewCategoryState.NotAssessed) return "Security advisories not assessed";
        var parts = new List<string> { $"{s.AffectedDependencies} matched advisor{(s.AffectedDependencies == 1 ? "y" : "ies")}" };
        if (s.NoMatchedAdvisory > 0) parts.Add($"{s.NoMatchedAdvisory} dependenc{(s.NoMatchedAdvisory == 1 ? "y" : "ies")} had no matched advisory evidence");
        if (s.AdvisorySourceUnavailable > 0) parts.Add($"advisory source unavailable for {s.AdvisorySourceUnavailable}");
        if (s.SecurityNotAssessed > 0) parts.Add($"{s.SecurityNotAssessed} not assessed");
        return string.Join(" · ", parts);
    }

    /// <summary>Where the reviewed inventory came from, in the evidence's own terms ("SBOM · Manifest dependency", never "Repository source" for an SBOM).</summary>
    public static string InventorySource(DependencyInventorySnapshot inventory) => inventory.SourceType switch
    {
        InventorySourceType.Sbom => $"SBOM · manifest dependencies{(inventory.Format is { } f ? $" · {DependencyHealthLabels.Format(f)}" : "")}",
        InventorySourceType.LockFile => "Lock file · resolved dependencies",
        InventorySourceType.Deployment => "Deployed evidence · deployed artifacts",
        _ => inventory.SourceRunId is not null ? "Previous source review · declared dependencies" : "Repository source · declared dependencies",
    };

    public static string Title(HealthResultSection section, string? mode) => section switch
    {
        HealthResultSection.Dependencies when mode == DependencyEvidenceModes.Deployed => "Deployed artifacts",
        _ => section.ToString(),
    };

    /// <summary>Deployment comparison counts from the expected ↔ deployed comparison (null when the run has none).</summary>
    public sealed record DeploymentCounts(int Observed, int Matched, int ExpectedNotObserved, int Unexpected, int Unresolved, int IntegrityDifferences, int DifferentVersion);

    public static DeploymentCounts? Deployment(DependencyHealthRun run) =>
        run.Comparisons.FirstOrDefault(c => c.Kind == ComparisonKind.ExpectedVsDeployed) is not { } c ? null : new(
            run.Deployed?.Dependencies.Count ?? c.Entries.Count(e => e.State is not (ComparisonState.MissingInDeployment or ComparisonState.NotObservedInEvidence)),
            c.Unchanged, c.Entries.Count(e => e.State is ComparisonState.MissingInDeployment or ComparisonState.NotObservedInEvidence),
            c.Entries.Count(e => e.State is ComparisonState.ObservedNotExpected or ComparisonState.AdditionalInDeployment),
            c.Entries.Count(e => e.State is ComparisonState.MappingUnresolved or ComparisonState.NotComparable),
            c.Entries.Count(e => e.State == ComparisonState.IntegrityDifference), c.Entries.Count(e => e.State == ComparisonState.DifferentVersion));

    /// <summary>Differences grouped by meaning: needs review (integrity/version differences), observations, unresolved mapping.</summary>
    public static string DifferenceGroup(ComparisonState state) => state switch
    {
        ComparisonState.IntegrityDifference or ComparisonState.DifferentVersion or ComparisonState.EvidenceConflict => "Needs review",
        ComparisonState.MappingUnresolved or ComparisonState.NotComparable => "Unresolved mapping",
        _ => "Observations",
    };

    /// <summary>The comparison state of one deployed artifact, read from the expected ↔ deployed comparison.</summary>
    public static ComparisonEntry? ArtifactEntry(DependencyHealthRun run, InventoryDependency artifact) =>
        run.Comparisons.FirstOrDefault(c => c.Kind == ComparisonKind.ExpectedVsDeployed)?.Entries.FirstOrDefault(e => string.Equals(e.PackageName, artifact.PackageName, StringComparison.OrdinalIgnoreCase)
            || e.State == ComparisonState.MappingUnresolved && string.Equals(e.RightValue, artifact.PackageName, StringComparison.OrdinalIgnoreCase));

    public static string CaptureLabel(BirkNext.Dependencies.CaptureOverall overall) => overall switch
    {
        BirkNext.Dependencies.CaptureOverall.Captured => "Captured",
        BirkNext.Dependencies.CaptureOverall.Partial => "Captured with limitations",
        BirkNext.Dependencies.CaptureOverall.Failed => "Not captured · target not readable",
        _ => "Not captured",
    };

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
