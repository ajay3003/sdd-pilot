using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BirkNext.DeliveryReporting;

// Functional Quality Scope: a thin projection over Delivery's parsed specification (features, user stories, scenarios, acceptance criteria,
// requirements) and the canonical correlation engine (capabilities, VERIFIES, aliases), plus declared domain-profile flows. It stores only scope
// definitions, explicit include/exclude references (stable item keys) and test-design assessments — never copies of the specification content.

/// <summary>What kind of quality behaviour an item describes. A category, never a status.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QualityCategory { Functional, Security, Privacy, Performance, Reliability, DataQuality, Compliance, Availability, Technical, Unclassified }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScopeArtifactType { UserStory, Scenario, AcceptanceCriterion, Requirement, TestCase }

/// <summary>How an item relates to the selected scope (always with a reason).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScopeRelation { Direct, Upstream, Downstream, RelatedCapability, ExplicitlyIncluded }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScopeInclusion { Smart, ExplicitInclude, ExplicitExclude }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestabilityClassification { Unclassified, Manual, ManualAndTechnical, Technical, AlreadyCovered, NeedsClarification, NotTestableYet }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScopeCoverage { NotAssessed, NoKnownCoverage, AutomatedCoverage, CandidateCoverageOnly }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScopeTestDataState { Unknown, Available, MissingRequired, NotRequired }

/// <summary>One scoped item: a reference to a parsed specification item by stable key (module/feature/key), with its classification and why it is in.</summary>
public sealed record ScopeItem(
    string Key, ScopeArtifactType Type, string DisplayId, string Title, QualityCategory Category, string CategoryRule, string Module, string FeatureKey, string FeatureTitle,
    string? ParentKey, string? Priority, ScopeRelation Relation, ScopeInclusion Inclusion, string Reason, ScopeCoverage Coverage, string? CoverageDetail,
    ScopeTestDataState TestData, TestabilityClassification Testability, bool Unavailable, string? SourcePath, int? SourceLine);

public sealed record ScopeRelatedModule(string Module, ScopeRelation Relation, string Reason, bool Included);

public sealed record ScopeSummary(int UserStories, int Scenarios, int AcceptanceCriteria, int Requirements, int FormalTestCases,
    IReadOnlyDictionary<QualityCategory, int> ByCategory, IReadOnlyDictionary<ScopeCoverage, int> ByCoverage, int TechnicalExcluded, int Unavailable);

public sealed record ScopeDefinition
{
    public Guid? Id { get; init; }
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    /// <summary>Smart roots: Delivery module names and/or "profile:{id}" domain profiles. Empty = manual scope.</summary>
    public List<string> Roots { get; init; } = [];
    public bool IncludeRelated { get; init; }
    public List<string> ExplicitIncludes { get; init; } = [];
    public List<string> ExplicitExcludes { get; init; } = [];
    public int Version { get; init; } = 1;
    public bool BuiltIn { get; init; }
}

public sealed record AvailableScope(string Key, string Name, string Kind, string Detail, Guid? CustomId = null);

public sealed record ScopeQuery(string? ScopeKey, Guid? CustomScopeId, bool IncludeRelated, bool IncludeTechnical = false, ScopeDefinition? Draft = null);

public sealed record ScopeResolution(ScopeDefinition Scope, Guid? SnapshotId, string? SnapshotLabel, string RuleSetVersion, IReadOnlyList<ScopeItem> Items, ScopeSummary Summary,
    IReadOnlyList<ScopeRelatedModule> RelatedModules, IReadOnlyList<string> Limitations, string PrecedenceNote);

public sealed record ScopeItemDetail(ScopeItem Item, string FullText, IReadOnlyList<ScopeItem> Children, IReadOnlyList<string> CoverageEvidence, TestDesignAssessmentView? Assessment);

public sealed record ScopeReferenceRequest(string ItemKey, ScopeInclusion Mode);

public sealed record TestDesignAssessmentView(string ItemKey, TestabilityClassification Testability, List<string> VerificationTypes, List<string> RequiredTestDataAssetIds,
    string? Comment, DateTimeOffset? ReviewedAt);

public sealed record ScopeSnapshotView(Guid Id, Guid? ScopeId, string ScopeName, int ScopeVersion, Guid? DeliverySnapshotId, string RuleSetVersion, int ItemCount, DateTimeOffset CreatedAt);

/// <summary>
/// Deterministic quality classification of specification text (rule set <see cref="RuleSetVersion"/>). Technical work (migrations, framework
/// swaps, refactors, IaC/pipeline work, ADR translation) is recognised first and excluded from the default view; quality categories come from
/// explicit wording in English or Norwegian; text without a quality signal is Functional when it is a user/system behaviour item; an item without
/// text is Unclassified (never silently hidden). Every result names its rule.
/// </summary>
public static partial class FunctionalQualityClassifier
{
    /// <remarks>functional-quality/2: feature-level work type (<see cref="ClassifyFeature"/>) shared by Delivery, Functional Quality Scope,
    /// Test Coverage and Traceability; developer tooling is technical work.</remarks>
    public const string RuleSetVersion = "functional-quality/2";

    /// <summary>The non-technical quality categories (second level under Work type = Non-technical).</summary>
    public static readonly QualityCategory[] NonTechnicalCategories =
    [
        QualityCategory.Functional, QualityCategory.Security, QualityCategory.Privacy, QualityCategory.Performance, QualityCategory.Reliability,
        QualityCategory.DataQuality, QualityCategory.Compliance, QualityCategory.Availability,
    ];

    /// <summary>
    /// One feature's work type and primary quality category, from its title and its user stories: a technical title (migration, refactoring,
    /// infrastructure, pipelines, decision-record work such as translation, developer tooling) makes the feature Technical; otherwise the title's
    /// quality wording, else the quality category most of its stories state (more than half), else Functional. Same rules as <see cref="Classify"/>.
    /// A consumer with canonical evidence may additionally mark a feature Technical when it belongs to a technical change intent.
    /// </summary>
    public static (QualityCategory Category, string Rule) ClassifyFeature(string title, IEnumerable<string>? storyTitles = null)
    {
        if (TechnicalRe().IsMatch(title)) return (QualityCategory.Technical, "fq.technical.feature");
        var (category, rule) = Classify(title);
        if (category is not (QualityCategory.Functional or QualityCategory.Unclassified)) return (category, rule);
        var stories = (storyTitles ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => Classify(t).Category).ToList();
        if (stories.Count > 0 && stories.Where(c => c is not (QualityCategory.Functional or QualityCategory.Unclassified))
                .GroupBy(c => c).OrderByDescending(g => g.Count()).FirstOrDefault() is { } top && top.Count() * 2 > stories.Count)
            return (top.Key, "fq.feature.stories-majority");
        return (QualityCategory.Functional, "fq.functional.behaviour");
    }

    public static bool IsTechnical(QualityCategory category) => category == QualityCategory.Technical;

    private static readonly (QualityCategory Category, string Rule, Regex Pattern)[] Rules =
    [
        (QualityCategory.Technical, "fq.technical.migration", TechnicalRe()),
        (QualityCategory.Privacy, "fq.privacy.wording", PrivacyRe()),
        (QualityCategory.Security, "fq.security.wording", SecurityRe()),
        (QualityCategory.Performance, "fq.performance.wording", PerformanceRe()),
        (QualityCategory.Reliability, "fq.reliability.wording", ReliabilityRe()),
        (QualityCategory.DataQuality, "fq.data-quality.wording", DataQualityRe()),
        (QualityCategory.Compliance, "fq.compliance.wording", ComplianceRe()),
        (QualityCategory.Availability, "fq.availability.wording", AvailabilityRe()),
    ];

    /// <summary>Classifies one item; <paramref name="featureTitle"/> lets a technical feature mark its stories technical.</summary>
    public static (QualityCategory Category, string Rule) Classify(string? text, string? featureTitle = null)
    {
        if (featureTitle is { Length: > 0 } && TechnicalRe().IsMatch(featureTitle)) return (QualityCategory.Technical, "fq.technical.feature");
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Length < 6) return (QualityCategory.Unclassified, "fq.unclassified.no-text");
        foreach (var (category, rule, pattern) in Rules)
            if (pattern.IsMatch(text)) return (category, rule);
        return (QualityCategory.Functional, "fq.functional.behaviour");
    }

    public static string Label(QualityCategory c) => c switch
    {
        QualityCategory.DataQuality => "Data quality",
        QualityCategory.Availability => "Availability / resilience",
        _ => c.ToString(),
    };

    public static readonly QualityCategory[] DefaultCategories =
    [
        QualityCategory.Functional, QualityCategory.Security, QualityCategory.Privacy, QualityCategory.Performance, QualityCategory.Reliability,
        QualityCategory.DataQuality, QualityCategory.Compliance, QualityCategory.Availability, QualityCategory.Unclassified,
    ];

    [GeneratedRegex(@"(?i)\b(migrat\w*|migrer\w*|refactor\w*|refaktor\w*|upgrade\w*|oppgrader\w*|rename (?:namespaces?|projects?)|ADR-\d+|terraform|pipeline\w*|ci/cd|framework|mediatr|wolverine\w*|shouldly|testcontainers|fluentassertions|nuget|translate (?:the )?source|oversett\w*|source[- ]code translation|kildekode|developer mode|devmode|developer tool\w*|utviklermodus)\b")]
    private static partial Regex TechnicalRe();

    [GeneratedRegex(@"(?i)\b(personvern|privacy|gdpr|kode\s?[67]|code\s?[67]|skjerm\w*|fortrolig\w*|sensitive personal|protected identit\w*|diskresjon\w*|sperret adresse)\b")]
    private static partial Regex PrivacyRe();

    [GeneratedRegex(@"(?i)\b(tilgangs?\w*|autoris\w*|authori[sz]\w*|autentis\w*|authenticat\w*|security|sikkerhet\w*|access control|permission\w*|rolle(?:r|n)?|token\w*|innlogg\w*|sign[- ]in)\b")]
    private static partial Regex SecurityRe();

    [GeneratedRegex(@"(?i)\b(ytelse\w*|performance|responstid\w*|response time|latency|throughput|svartid\w*|load test\w*|last(?:test|tid)\w*)\b")]
    private static partial Regex PerformanceRe();

    [GeneratedRegex(@"(?i)\b(retry\w*|resilien\w*|robust\w*|gjenoppretting|recover\w*|not (?:be )?lost|ikke (?:går )?tapt|idempoten\w*|resync\w*|resynkron\w*|restart\w*|dead[- ]letter|eventual\w*|retransmi\w*)\b")]
    private static partial Regex ReliabilityRe();

    [GeneratedRegex(@"(?i)\b(datakvalitet\w*|data quality|duplikat\w*|duplicate\w*|konsisten\w*|consisten\w*|valider\w*|validat\w*|xsd|schema|skjema)\b")]
    private static partial Regex DataQualityRe();

    [GeneratedRegex(@"(?i)\b(compliance|regelverk\w*|forskrift\w*|lovkrav\w*|legal requirement\w*|arkivlov\w*|retention|oppbevaring\w*|revisjonsspor\w*|audit trail)\b")]
    private static partial Regex ComplianceRe();

    [GeneratedRegex(@"(?i)\b(tilgjengelighet|availability|oppetid|uptime|failover|redundan\w*|høy tilgjengelighet)\b")]
    private static partial Regex AvailabilityRe();
}

public static class FunctionalQualityScopeText
{
    public const string Precedence = "Explicit exclusion > explicit inclusion > smart relation-based inclusion > not included.";

    public static string RelationLabel(ScopeRelation r) => r switch
    {
        ScopeRelation.RelatedCapability => "Related capability",
        ScopeRelation.ExplicitlyIncluded => "Explicitly included",
        _ => r.ToString(),
    };

    public static string TypeLabel(ScopeArtifactType t) => t switch
    {
        ScopeArtifactType.UserStory => "User story",
        ScopeArtifactType.Scenario => "Acceptance scenario",
        ScopeArtifactType.AcceptanceCriterion => "Acceptance criterion",
        ScopeArtifactType.TestCase => "Formal test case",
        _ => "Requirement",
    };

    public static string Icon(ScopeArtifactType t) => t switch
    {
        ScopeArtifactType.UserStory => "◧",
        ScopeArtifactType.Scenario => "▷",
        ScopeArtifactType.AcceptanceCriterion => "☑",
        ScopeArtifactType.TestCase => "⚑",
        _ => "§",
    };

    public static string CoverageLabel(ScopeCoverage c) => c switch
    {
        ScopeCoverage.AutomatedCoverage => "Automated test linked",
        ScopeCoverage.CandidateCoverageOnly => "Candidate coverage only",
        ScopeCoverage.NoKnownCoverage => "No known coverage",
        _ => "Not assessed",
    };

    public static string TestDataLabel(ScopeTestDataState s) => s switch
    {
        ScopeTestDataState.Available => "Test Data available",
        ScopeTestDataState.MissingRequired => "Missing required Test Data",
        ScopeTestDataState.NotRequired => "No Test Data required",
        _ => "Unknown requirement",
    };

    public static string TestabilityLabel(TestabilityClassification t) => t switch
    {
        TestabilityClassification.ManualAndTechnical => "Manual + technical",
        TestabilityClassification.AlreadyCovered => "Already covered",
        TestabilityClassification.NeedsClarification => "Needs clarification",
        TestabilityClassification.NotTestableYet => "Not testable yet",
        TestabilityClassification.Technical => "Technical verification",
        _ => t.ToString(),
    };
}
