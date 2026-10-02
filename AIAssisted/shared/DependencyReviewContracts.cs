using System.Text.Json.Serialization;

namespace BirkNext.Dependencies;

// Dependency / supply-chain review with Renovate as one evidence source. Static policy (config + declared dependencies) is kept apart from
// repository runtime (whether Renovate actually runs). Simulation uses SYNTHETIC candidate versions to test the policy only: a simulated
// candidate is never "latest", "available" or "published". No dependency file, branch, commit, PR or Renovate run is ever produced.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RenovateCoverage { Configured, Inherited, Partial, Missing, NotAssessed }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DependencyUpdateType { Patch, Minor, Major, Digest, NotAssessable }

/// <summary>What Renovate's policy would do with an update. Deferred by schedule is not blocked; ignored is not up to date.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PolicyResult { Allowed, Blocked, BlockedByVersionConstraint, RequiresApproval, DeferredBySchedule, Ignored, NotProposed, NotAssessable }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DependencyFindingSeverity { NeedsReview, Warning, Info }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReviewCategoryState { Ready, NeedsReview, Partial, Missing, Issue, NotConfigured, NotAssessed }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ManagerState { Enabled, DisabledByConfig, DisabledByDefault, IgnoredByPaths, NoConfig }

public sealed record SourceProvenance(string File, int Line);

public sealed record RenovateConfigFile
{
    public string Repository { get; init; } = "";
    public string Path { get; init; } = "";
    public string Sha256 { get; init; } = "";
    /// <summary>True for the file Renovate would read (first match in Renovate's config file order, or an explicitly supplied override).</summary>
    public bool Used { get; init; }
    public bool SyntaxValid { get; init; }
    public string? SyntaxError { get; init; }
    public string Note { get; init; } = "";
}

public sealed record RenovateRule
{
    public int Index { get; init; }
    public string? Description { get; init; }
    /// <summary>Matcher key → values exactly as configured (e.g. matchFileNames → ["Aspire/**"]).</summary>
    public Dictionary<string, List<string>> Matchers { get; init; } = [];
    /// <summary>Policy settings the rule applies (enabled, automerge, groupName, schedule …); secrets redacted.</summary>
    public Dictionary<string, string> Settings { get; init; } = [];
    public List<string> UnsupportedMatchers { get; init; } = [];
    public List<string> DeprecatedKeys { get; init; } = [];
    public string? GroupName { get; init; }
    public int MatchedDependencies { get; init; }
    public SourceProvenance? Location { get; init; }
}

public sealed record DeclaredDependency
{
    public string Repository { get; init; } = "";
    public string Manager { get; init; } = "";
    public string? Datasource { get; init; }
    public string? DepType { get; init; }
    public string PackageName { get; init; } = "";
    /// <summary>The value as declared: an exact version, a range (e.g. [1.0,2.0)), a floating version or a Docker tag.</summary>
    public string? CurrentValue { get; init; }
    public bool IsRange { get; init; }
    public string? Digest { get; init; }
    /// <summary>The file that owns the version (Directory.Packages.props under central package management, not every csproj).</summary>
    public string OwnerFile { get; init; } = "";
    public int Line { get; init; }
    public List<string> ReferencedBy { get; init; } = [];
    /// <summary>Why Renovate would not process this dependency at all (ignoreDeps, ignorePaths, manager not enabled), when it would not.</summary>
    public string? IgnoredBy { get; init; }
}

public sealed record RuleMatch
{
    public int RuleIndex { get; init; }
    public string? Description { get; init; }
    public List<string> MatchedOn { get; init; } = [];
    public Dictionary<string, string> Applied { get; init; } = [];
}

public sealed record EffectivePolicy
{
    public bool Enabled { get; init; } = true;
    public bool? Automerge { get; init; }
    public string? AutomergeType { get; init; }
    public string? GroupName { get; init; }
    public List<string> Schedule { get; init; } = [];
    public string? AllowedVersions { get; init; }
    public string? RangeStrategy { get; init; }
    public bool? DependencyDashboardApproval { get; init; }
    public string? MinimumReleaseAge { get; init; }
    public List<string> Labels { get; init; } = [];
    public bool? IgnoreUnstable { get; init; }
    /// <summary>Setting → which layer set it ("repository config", "rule #7 …"), for "why".</summary>
    public Dictionary<string, string> Sources { get; init; } = [];
}

/// <summary>A policy test with a SYNTHETIC candidate version — the candidate is a test value, never an observed or published version.</summary>
public sealed record PolicySimulation
{
    public string Repository { get; init; } = "";
    public string PackageName { get; init; } = "";
    public string Manager { get; init; } = "";
    public string? Datasource { get; init; }
    public string? OwnerFile { get; init; }
    public string? CurrentValue { get; init; }
    public string? CandidateVersion { get; init; }
    public bool IsSyntheticCandidate { get; init; } = true;
    public string Scenario { get; init; } = "";
    public DependencyUpdateType UpdateType { get; init; }
    public List<RuleMatch> MatchedRules { get; init; } = [];
    public EffectivePolicy Effective { get; init; } = new();
    public PolicyResult Result { get; init; }
    public string Explanation { get; init; } = "";
    /// <summary>Why the result is partial (e.g. an unexpanded preset), never hidden.</summary>
    public List<string> Limitations { get; init; } = [];
}

public sealed record DependencyFinding
{
    public string RuleId { get; init; } = "";
    public DependencyFindingSeverity Severity { get; init; }
    public string Repository { get; init; } = "";
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public int? RuleIndex { get; init; }
    public List<string> Evidence { get; init; } = [];
}

public sealed record ManagerCoverage
{
    public string Manager { get; init; } = "";
    public int Files { get; init; }
    public int Dependencies { get; init; }
    public ManagerState State { get; init; }
    public string Detail { get; init; } = "";
}

public sealed record ReviewCategory(string Name, ReviewCategoryState State, string Detail);

public sealed record RepositoryDependencyReview
{
    public string Repository { get; init; } = "";
    public string ArchiveSha256 { get; init; } = "";
    public RenovateCoverage Coverage { get; init; }
    public string CoverageDetail { get; init; } = "";
    public List<RenovateConfigFile> ConfigFiles { get; init; } = [];
    public string? ConfigHash { get; init; }
    public List<string> Extends { get; init; } = [];
    public List<string> UnresolvedPresets { get; init; } = [];
    public bool ConfigSyntaxValid { get; init; }
    public List<string> ValidationIssues { get; init; } = [];
    public List<string> UnrecognizedKeys { get; init; } = [];
    public List<ManagerCoverage> Managers { get; init; } = [];
    public List<DeclaredDependency> Dependencies { get; init; } = [];
    public List<RenovateRule> Rules { get; init; } = [];
    public List<string> IgnoreDeps { get; init; } = [];
    public List<string> IgnorePaths { get; init; } = [];
    public Dictionary<string, string> RepositorySettings { get; init; } = [];
    public List<PolicySimulation> Simulations { get; init; } = [];
    public List<DependencyFinding> Findings { get; init; } = [];
    public ReviewCategory SecurityUpdatePolicy { get; init; } = new("Security-update policy", ReviewCategoryState.NotAssessed, "");
    public ReviewCategory RuntimeAutomation { get; init; } = new("Runtime automation", ReviewCategoryState.NotAssessed, "");
    public List<string> AutomationEvidence { get; init; } = [];
    /// <summary>Redacted, normalized config the simulations used — so later single-package simulations use the same snapshot.</summary>
    public string? NormalizedConfig { get; init; }
    /// <summary>Manifests of ecosystems BirkNext does not read (Maven, npm, pip …): their dependencies are not in this review — a tool
    /// limitation reported as such, never a project defect. Import an SBOM to review them.</summary>
    public List<string> UnsupportedManifests { get; init; } = [];
    public string? DriftSincePrevious { get; init; }
}

public sealed record DependencyReviewResult
{
    public Guid RunId { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public string Label { get; init; } = "";
    public string EvaluationMechanism { get; init; } = "";
    public List<string> UnsupportedSemantics { get; init; } = [];
    public List<RepositoryDependencyReview> Repositories { get; init; } = [];
    public List<ReviewCategory> Categories { get; init; } = [];
    /// <summary>The exact Source Analysis snapshots reviewed. Null for legacy runs over directly uploaded archives (never back-filled).</summary>
    public ReviewSourceScope? SourceScope { get; init; }
    public List<CrossSourceObservation> CrossSource { get; init; } = [];
    public List<SourceRelationship> SourceRelationships { get; init; } = [];
}

public sealed record DependencyReviewRunSummary(Guid RunId, DateTimeOffset CompletedAt, string Label, int Repositories, int Findings);

public sealed record PolicySimulationRequest
{
    public Guid RunId { get; init; }
    public string Repository { get; init; } = "";
    public string PackageName { get; init; } = "";
    public string Manager { get; init; } = "nuget";
    public string CurrentVersion { get; init; } = "";
    public string CandidateVersion { get; init; } = "";
    public string? File { get; init; }
}

public static class DependencyLabels
{
    public static string Result(PolicyResult result) => result switch
    {
        PolicyResult.BlockedByVersionConstraint => "Blocked by version constraint",
        PolicyResult.RequiresApproval => "Requires approval",
        PolicyResult.DeferredBySchedule => "Deferred by schedule",
        PolicyResult.Ignored => "Ignored by Renovate",
        PolicyResult.NotProposed => "Not proposed",
        PolicyResult.NotAssessable => "Not assessable",
        _ => result.ToString(),
    };

    public static string Category(ReviewCategoryState state) => state switch
    {
        ReviewCategoryState.NeedsReview => "Needs review",
        ReviewCategoryState.NotConfigured => "Not configured",
        ReviewCategoryState.NotAssessed => "Not assessed",
        _ => state.ToString(),
    };

    public static string Severity(DependencyFindingSeverity severity) => severity == DependencyFindingSeverity.NeedsReview ? "Needs review" : severity.ToString();

    public static string Manager(ManagerState state) => state switch
    {
        ManagerState.DisabledByConfig => "Disabled by config",
        ManagerState.DisabledByDefault => "Disabled by Renovate default",
        ManagerState.IgnoredByPaths => "Ignored by ignorePaths",
        ManagerState.NoConfig => "No Renovate config",
        _ => "Enabled",
    };

    /// <summary>The only wording allowed for a simulated version.</summary>
    public const string SyntheticCandidate = "Synthetic candidate";
}
