using BirkNext.Dependencies;

namespace BirkNext.Web.Services;

/// <summary>The sections of a stored source dependency review, in decision order (most important first).</summary>
public enum SourceResultSection { Summary, Findings, Mapping, Simulation, Usage, VersionDifferences, Policy, SourceScope, Technical, History }

/// <summary>How a cross-source dependency relationship was established. Never "incompatible": a version difference is only an observation.</summary>
public enum DependencyMappingState { Mapped, MappedWithVersionDifference, NeedsConfirmation }

/// <summary>Consumer projects declaring one version of a mapped dependency.</summary>
public sealed record MappingConsumerVersion(string Version, IReadOnlyList<ProjectPackageUsage> Projects);

/// <summary>
/// One producer → consumer relationship between selected sources: the consumer references, by exact package ID, a package the producer's
/// own project declares it produces. Never inferred from similar names.
/// </summary>
public sealed record DependencyMapping(string Producer, string Consumer, string PackageName, string? ProducerVersion, IReadOnlyList<MappingConsumerVersion> ConsumerVersions,
    int ReferencingProjects, string Evidence, DependencyMappingState State)
{
    /// <summary>The versions the consumer declares (from its projects when resolved, otherwise as the relationship recorded them).</summary>
    public IReadOnlyList<string> ConsumerValues { get; init; } = [];
}

/// <summary>The synthetic scenarios of one declared package, shown together with one explanation.</summary>
public sealed record SimulationGroup(string PackageName, string? OwnerFile, string? CurrentValue, IReadOnlyList<PolicySimulation> Scenarios);

public sealed record SimulationSummary(int Generated, int Assessable, int NotAssessable, IReadOnlyList<(PolicyResult Result, int Count)> ByResult);

/// <summary>Why Renovate would or would not process a declared dependency, with its cause named.</summary>
public sealed record RenovateProcessing(string State, string? Reason);

/// <summary>The policy summary shown before the expert configuration detail.</summary>
public sealed record PolicySummary(string Automerge, string Presets, ReviewCategory SecurityUpdatePolicy, string RuntimeExecution, string SimulationCoverage, string? SimulationReason);

/// <summary>
/// Projection of a stored source dependency review into decision-ordered sections. Pure: it never re-evaluates policy, never contacts a
/// registry and keeps the engine's distinctions — findings (policy risks to review) are separate from coverage limitations (what BirkNext
/// could not evaluate), and a declared version difference is an observation, never an incompatibility.
/// </summary>
public static class DependencySourceResultPresentation
{
    public static readonly IReadOnlyList<SourceResultSection> Sections = Enum.GetValues<SourceResultSection>();

    public static string Title(SourceResultSection section) => section switch
    {
        SourceResultSection.Mapping => "Dependency mapping",
        SourceResultSection.Simulation => "Policy simulation",
        SourceResultSection.VersionDifferences => "Version differences",
        SourceResultSection.Policy => "Policy & updates",
        SourceResultSection.SourceScope => "Source scope",
        SourceResultSection.Technical => "Technical details",
        _ => section.ToString(),
    };

    public static string Slug(SourceResultSection section) => section switch
    {
        SourceResultSection.VersionDifferences => "versions",
        SourceResultSection.SourceScope => "scope",
        _ => section.ToString().ToLowerInvariant(),
    };

    // ── Findings vs coverage limitations ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Review-worthy policy observations (the engine's Needs review and Warning findings).</summary>
    public static IReadOnlyList<DependencyReviewPresentation.FindingGroup> NeedsReview(DependencyReviewResult result) =>
        [.. DependencyReviewPresentation.FindingGroups(result).Where(g => g.Findings[0].Finding.Severity != DependencyFindingSeverity.Info)];

    /// <summary>What this offline review could not evaluate (the engine's informational findings): presets not expanded, managers not
    /// processed, unsupported syntax. Information about coverage, never a dependency risk.</summary>
    public static IReadOnlyList<DependencyReviewPresentation.FindingGroup> Limitations(DependencyReviewResult result) =>
        [.. DependencyReviewPresentation.FindingGroups(result).Where(g => g.Findings[0].Finding.Severity == DependencyFindingSeverity.Info)];

    /// <summary>"Major updates would be automerged — Needs review": the rule wording a finding card leads with.</summary>
    public static string WhyItMatters(string ruleId) => ruleId switch
    {
        "major-automerge" => "Major updates can contain breaking changes; merging them without review skips that check.",
        "rule-scope-inconsistent" => "Rule metadata or scope may not match the configured package matcher, so the rule may apply to other packages than intended.",
        "rule-update-type-broad" => "The rule allows an update type that usually needs human review.",
        "rule-manager-inconsistent" => "The rule's manager scope may not match the packages it describes.",
        "rule-broad-disable" => "A broad rule disables updates for many dependencies at once.",
        "rule-duplicate-conflict" => "Two rules configure the same packages differently; the later rule wins.",
        "renovate-missing" => "Dependencies in this repository are not covered by any Renovate policy.",
        "config-invalid" => "Renovate cannot read an invalid configuration file.",
        _ => "",
    };

    /// <summary>The action a finding card offers, in the reviewer's words.</summary>
    public static string Action(string ruleId) => ruleId switch
    {
        "major-automerge" => "Review affected packages",
        "rule-scope-inconsistent" or "rule-manager-inconsistent" or "rule-matches-nothing" => "Review rule matchers",
        "rule-duplicate-conflict" or "rule-broad-disable" or "rule-update-type-broad" => "Review package rules",
        "renovate-missing" => "Review repository coverage",
        _ => "Review evidence",
    };

    // ── Dependency mapping ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Producer → consumer relationships from the engine's exact-package-ID evidence (the producer's csproj PackageId, the consumer's
    /// PackageReference). Consumer versions come only from the consumer's own declarations; nothing is matched by name similarity.
    /// </summary>
    public static IReadOnlyList<DependencyMapping> Mappings(DependencyReviewResult result)
    {
        var usage = DependencyUsagePresentation.Build(result);
        return [.. result.SourceRelationships.Select(r =>
        {
            var projects = usage.Rows.FirstOrDefault(row => string.Equals(row.PackageName, r.PackageName, StringComparison.OrdinalIgnoreCase))?.Projects
                .Where(p => p.Repository == r.FromRepository).ToList() ?? [];
            var versions = projects.GroupBy(p => p.Version ?? "Not declared", StringComparer.Ordinal)
                .Select(g => new MappingConsumerVersion(g.Key, [.. g.OrderBy(p => p.Project, StringComparer.Ordinal)]))
                .OrderByDescending(v => v.Projects.Count).ThenBy(v => v.Version, StringComparer.Ordinal).ToList();
            var consumerValues = versions.Count > 0 ? versions.Select(v => v.Version).ToList() : r.ReferencedValues;
            var state = r.PublishedVersion is null || consumerValues.Count == 0 ? DependencyMappingState.NeedsConfirmation
                : consumerValues.Any(v => !string.Equals(v, r.PublishedVersion, StringComparison.Ordinal)) ? DependencyMappingState.MappedWithVersionDifference
                : DependencyMappingState.Mapped;
            return new DependencyMapping(r.ToRepository, r.FromRepository, r.PackageName, r.PublishedVersion, versions,
                Math.Max(r.Projects, projects.Count), "PackageReference to a package ID the producer's project declares (exact package ID)", state) { ConsumerValues = consumerValues };
        }).OrderBy(m => m.Producer, StringComparer.Ordinal).ThenBy(m => m.PackageName, StringComparer.OrdinalIgnoreCase)];
    }

    public static string Label(DependencyMappingState state) => state switch
    {
        DependencyMappingState.MappedWithVersionDifference => "Mapped with version difference",
        DependencyMappingState.NeedsConfirmation => "Needs confirmation",
        _ => "Mapped",
    };

    /// <summary>Shown when the selected sources have no mapping: never "unrelated" — only this scope was searched.</summary>
    public static string NoMapping(DependencyReviewResult result) => result.Repositories.Count < 2
        ? "Only one source was reviewed. Include a related source to map dependencies between sources."
        : "No cross-source dependency mapping found in the selected sources. This does not mean the sources are unrelated elsewhere.";

    /// <summary>Why a related source is part of the review, from mapping evidence ("M2LB references a package M2LB.Common produces").</summary>
    public static string? WhyIncluded(DependencyReviewResult result, string repository)
    {
        var mappings = Mappings(result);
        var asConsumer = mappings.Where(m => m.Consumer == repository).ToList();
        if (asConsumer.Count > 0)
            return $"{repository} contains explicit references to {string.Join(", ", asConsumer.Select(m => m.PackageName).Distinct())}, produced by {string.Join(", ", asConsumer.Select(m => m.Producer).Distinct())}.";
        var asProducer = mappings.Where(m => m.Producer == repository).ToList();
        return asProducer.Count > 0
            ? $"{repository} produces {string.Join(", ", asProducer.Select(m => m.PackageName).Distinct())}, referenced by {string.Join(", ", asProducer.Select(m => m.Consumer).Distinct())}."
            : null;
    }

    /// <summary>The mapping that explains a version difference of one package, when there is one.</summary>
    public static DependencyMapping? MappingFor(DependencyReviewResult result, string packageName) =>
        Mappings(result).FirstOrDefault(m => string.Equals(m.PackageName, packageName, StringComparison.OrdinalIgnoreCase));

    // ── Policy simulation ───────────────────────────────────────────────────────────────────────────────────────────────

    public static SimulationSummary Summary(IEnumerable<PolicySimulation> simulations)
    {
        var all = simulations.ToList();
        var notAssessable = all.Count(s => s.Result == PolicyResult.NotAssessable || s.UpdateType == DependencyUpdateType.NotAssessable);
        return new(all.Count, all.Count - notAssessable, notAssessable, [.. all.GroupBy(s => s.Result).OrderBy(g => g.Key).Select(g => (g.Key, g.Count()))]);
    }

    /// <summary>Scenarios grouped by declared package (one row per package, Patch → Minor → Major → Prerelease inside), optionally filtered.</summary>
    public static IReadOnlyList<SimulationGroup> Groups(RepositoryDependencyReview repo, PolicyResult? filter, DependencyUpdateType? updateType = null) =>
        [.. repo.Simulations.Where(s => (filter is null || s.Result == filter) && (updateType is null || s.UpdateType == updateType))
            .GroupBy(s => (s.PackageName, s.OwnerFile, s.CurrentValue))
            .Select(g => new SimulationGroup(g.Key.PackageName, g.Key.OwnerFile, g.Key.CurrentValue, [.. g]))
            .OrderBy(g => g.PackageName, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.OwnerFile, StringComparer.Ordinal)];

    public const string ScenarioVersion = "Scenario version";
    public const string ScenarioVersionHelp = "Synthetic test value — generated only to test policy behavior. It is not a registry result.";
    public const string SimulationHelp = "Test how the stored Renovate configuration would treat synthetic Patch, Minor, Major and prerelease updates.";
    public const string SimulationIsNotAnUpdateCheck = "Simulation scenarios test policy only. They do not query registries, prove a version exists, prove compatibility, prove CI passes or prove an update is safe.";

    // ── Policy & updates ────────────────────────────────────────────────────────────────────────────────────────────────

    public static PolicySummary Policy(RepositoryDependencyReview repo)
    {
        var automerge = repo.RepositorySettings.TryGetValue("automerge", out var a) ? a switch { "true" => "Enabled", "false" => "Disabled", _ => a } : "Not set in repository configuration";
        var presets = repo.Extends.Count == 0 ? "None" : $"{repo.Extends.Count} not expanded";
        var partial = repo.Extends.Count > 0 || repo.Simulations.Any(s => s.Limitations.Count > 0);
        var reason = repo.Extends.Count > 0 ? "Preset-provided rules and defaults are not included in this offline evaluation."
            : partial ? "Some scenarios carry evaluation limitations." : null;
        var simulation = repo.Simulations.Count == 0 ? "No scenarios" : partial ? "Partial" : "Complete";
        return new(automerge, presets, repo.SecurityUpdatePolicy, "Not assessed", simulation, reason);
    }

    public const string SecurityPolicyCaveat = "This does not mean known vulnerabilities exist. Vulnerability status is assessed by dependency-health (advisory) evidence, not by a source-only Renovate policy review.";
    public const string RuntimeCaveat = "Source declares automation, but Renovate runtime execution is not verified.";

    /// <summary>Renovate processing state with its cause, instead of a bare "Not processed".</summary>
    public static RenovateProcessing Processing(DeclaredDependency dep) => dep.IgnoredBy switch
    {
        null => new("Processed", null),
        var why when why.StartsWith("Configured ignoreDeps", StringComparison.Ordinal) => new("Ignored by Renovate", why),
        var why when why.StartsWith("Configured ignorePaths", StringComparison.Ordinal) => new("Not processed: path excluded", why),
        var why when why.Contains("manager is disabled", StringComparison.Ordinal) => new("Not processed by manager configuration", why),
        var why => new("Not processed", why),
    };

    // ── Summary ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    public static int DeclaredPackages(DependencyReviewResult result) => result.Repositories.Sum(r => r.Dependencies.Count);

    /// <summary>"Source dependency review completed · 2 repositories reviewed · …" with actual counts.</summary>
    public static IReadOnlyList<string> Headline(DependencyReviewResult result)
    {
        var needs = NeedsReview(result).Sum(g => g.Findings.Count);
        var sims = Summary(result.Repositories.SelectMany(r => r.Simulations));
        var partial = result.Repositories.Any(r => Policy(r).SimulationCoverage == "Partial");
        return
        [
            $"{result.Repositories.Count} repositor{(result.Repositories.Count == 1 ? "y" : "ies")} reviewed",
            $"{DeclaredPackages(result)} declared dependenc{(DeclaredPackages(result) == 1 ? "y" : "ies")} assessed",
            $"{needs} policy observation{(needs == 1 ? "" : "s")} need{(needs == 1 ? "s" : "")} review",
            .. result.Repositories.Count > 1 ? [$"{result.CrossSource.Count} version difference{(result.CrossSource.Count == 1 ? "" : "s")} across selected sources"] : Array.Empty<string>(),
            sims.Generated == 0 ? "No policy simulation scenarios" : $"Policy simulation {(partial ? "partial" : "complete")}",
        ];
    }
}
