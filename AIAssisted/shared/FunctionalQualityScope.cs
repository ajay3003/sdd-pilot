using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BirkNext.DeliveryReporting;

// Functional Quality Scope: a thin projection over Delivery's parsed specification (features, user stories, scenarios, acceptance criteria,
// requirements) and the canonical correlation engine (capabilities, VERIFIES, aliases), plus declared domain-profile flows. It stores only scope
// definitions, explicit include/exclude references (stable item keys) and test-design assessments — never copies of the specification content.

/// <summary>What kind of quality behaviour an item describes. A category, never a status. Integration (functional-quality/3) is appended so stored
/// ordinals never shift; lists order categories with <see cref="FunctionalQualityClassifier.DefaultCategories"/>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QualityCategory { Functional, Security, Privacy, Performance, Reliability, DataQuality, Compliance, Availability, Technical, Unclassified, Integration }

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

/// <summary><paramref name="Environment"/>: the Target Environment (canonical interpretation scope) whose test and schema evidence is used;
/// <see cref="ScopeEnvironments.All"/> aggregates every environment (labelled as such); null = the only environment with evidence, if exactly one.</summary>
public sealed record ScopeQuery(string? ScopeKey, Guid? CustomScopeId, bool IncludeRelated, bool IncludeTechnical = false, ScopeDefinition? Draft = null, string? Environment = null);

public sealed record ScopeResolution(ScopeDefinition Scope, Guid? SnapshotId, string? SnapshotLabel, string RuleSetVersion, IReadOnlyList<ScopeItem> Items, ScopeSummary Summary,
    IReadOnlyList<ScopeRelatedModule> RelatedModules, IReadOnlyList<string> Limitations, string PrecedenceNote)
{
    /// <summary>Which environment's evidence the coverage counts come from (and which environments have any).</summary>
    public ScopeEvidenceContext? Evidence { get; init; }
}

public static class ScopeEnvironments
{
    /// <summary>Every environment, aggregated: never presented as evidence from one environment.</summary>
    public const string All = "*";
}

/// <summary>One environment with interpreted test evidence: the canonical scope id (a Target Environment id) and how much it holds.</summary>
public sealed record ScopeEvidenceEnvironment(string Id, int TestRelations, int Tests);

/// <summary>
/// The environment context of test and schema evidence. <see cref="Environment"/> null = none applied (no evidence, or several environments and none
/// chosen). Canonical test relations are source relationships read from that environment's interpreted source — never executions.
/// </summary>
public sealed record ScopeEvidenceContext(string? Environment, bool CrossEnvironment, IReadOnlyList<ScopeEvidenceEnvironment> Available, string Basis)
{
    public bool Applied => Environment is not null || CrossEnvironment;
}

public sealed record ScopeItemDetail(ScopeItem Item, string FullText, IReadOnlyList<ScopeItem> Children, IReadOnlyList<string> CoverageEvidence, TestDesignAssessmentView? Assessment)
{
    /// <summary>The tests the canonical engine relates to this item (VERIFIES), one per relation, explicit apart from candidates; rejected candidates stay
    /// listed as rejected so a refresh never turns them back into coverage. Empty when nothing is interpreted.</summary>
    public IReadOnlyList<ScopeTestLink> Tests { get; init; } = [];
    /// <summary>False when no canonical interpretation exists: "no tests" then means "not assessed", not "untested".</summary>
    public bool TestsAssessed { get; init; }
    /// <summary>The schemas and contracts associated with this item (explicit, confirmed or candidate), from the same environment's evidence.</summary>
    public IReadOnlyList<ScopeSchemaLink> Schemas { get; init; } = [];
    /// <summary>False when no contract evidence is interpreted for the environment: no schemas then means "not assessed", not "none".</summary>
    public bool SchemasAssessed { get; init; }
    public ScopeEvidenceContext? Evidence { get; init; }
}

/// <summary>How a schema relates to a requirement. Explicit: the requirement names it (or a canonical relation states it). Confirmed: a person
/// confirmed a canonical relation. Candidate: inferred from module ownership only — never a statement that the requirement uses it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScopeSchemaLinkKind { Explicit, Confirmed, Candidate }

/// <summary>Whether a schema association carries validation evidence. An existing schema is never a validation result.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScopeSchemaValidation { NotPerformed, ServerValidated }

/// <summary>
/// One schema or contract associated with a requirement: its canonical identity, kind (XmlSchema, JsonSchema, OpenApi, GraphQlSchema, messaging…),
/// version/namespace, where it was found (package, Source Analysis snapshot, path), the relationship with its rule and confidence, and validation.
/// </summary>
public sealed record ScopeSchemaLink(string ContractIdentity, string Name, string SchemaKind, string? Namespace, string? Version, string? Package, Guid? SnapshotId,
    string? SourcePath, ScopeSchemaLinkKind Kind, string Relationship, string Confidence, string RuleId, string Explanation, ScopeSchemaValidation Validation,
    string ValidationDetail, string Scope);

/// <summary>How a test relates to a requirement. Explicit: the source states it, or a person confirmed it. Candidate: inferred, ambiguous, or an id several
/// features share — never coverage. Rejected: a person rejected it in the shared review registry.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScopeTestLinkKind { Explicit, Candidate, Rejected }

public sealed record ScopeTestEvidence(string Kind, string Reference, string? Detail);

/// <summary>
/// One canonical Test → VERIFIES → requirement relation as Functional Quality Scope shows it: the test's identity and where it was found, the rule and
/// evidence of the relation, its review state (decisions go to the shared canonical registry by <see cref="RelationKey"/> in <see cref="Scope"/>), and the
/// execution state — discovery is never execution, so without linked run evidence it says so.
/// </summary>
public sealed record ScopeTestLink(string RelationKey, string Scope, ScopeTestLinkKind Kind, string TestIdentity, string TestName, string? Location,
    string Resolution, string Confidence, string RuleId, string Explanation, string ReviewState, IReadOnlyList<ScopeTestEvidence> Evidence, string Execution);

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
    /// <remarks>functional-quality/3: adds Integration (system-to-system exchange). functional-quality/2: feature-level work type
    /// (<see cref="ClassifyFeature"/>) shared by Delivery, Functional Quality Scope, Test Coverage and Traceability; developer tooling is technical work.</remarks>
    public const string RuleSetVersion = "functional-quality/3";

    /// <summary>The rule set before Integration existed. A report frozen with it (or before rule sets were recorded) is viewed with it, so a newer
    /// category never silently reclassifies a historical report.</summary>
    public const string LegacyRuleSetVersion = "functional-quality/2";

    /// <summary>The rule set a frozen report is viewed with: its own, or the legacy one when it predates recorded rule sets.</summary>
    public static string RuleSetOf(string? frozen) => frozen is { Length: > 0 } ? frozen : LegacyRuleSetVersion;

    private static bool HasIntegration(string? ruleSet) => ruleSet is null || ruleSet != LegacyRuleSetVersion;

    /// <summary>The non-technical quality categories (second level under Work type = Non-technical).</summary>
    public static readonly QualityCategory[] NonTechnicalCategories =
    [
        QualityCategory.Functional, QualityCategory.Integration, QualityCategory.Security, QualityCategory.Privacy, QualityCategory.Performance,
        QualityCategory.Reliability, QualityCategory.DataQuality, QualityCategory.Compliance, QualityCategory.Availability,
    ];

    /// <summary>
    /// One feature's work type and primary quality category, from its title and its user stories: a technical title (migration, refactoring,
    /// infrastructure, pipelines, decision-record work such as translation, developer tooling) makes the feature Technical; otherwise the title's
    /// quality wording, else the quality category most of its stories state (more than half), else Functional. Same rules as <see cref="Classify"/>.
    /// A consumer with canonical evidence may additionally mark a feature Technical when it belongs to a technical change intent.
    /// </summary>
    public static (QualityCategory Category, string Rule) ClassifyFeature(string title, IEnumerable<string>? storyTitles = null, string? ruleSet = null)
    {
        if (TechnicalRe().IsMatch(title)) return (QualityCategory.Technical, "fq.technical.feature");
        var (category, rule) = Classify(title, null, ruleSet);
        if (category is not (QualityCategory.Functional or QualityCategory.Unclassified)) return (category, rule);
        var stories = (storyTitles ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => Classify(t, null, ruleSet).Category).ToList();
        if (stories.Count > 0 && stories.Where(c => c is not (QualityCategory.Functional or QualityCategory.Unclassified))
                .GroupBy(c => c).OrderByDescending(g => g.Count()).FirstOrDefault() is { } top && top.Count() * 2 > stories.Count)
            return (top.Key, "fq.feature.stories-majority");
        return (QualityCategory.Functional, "fq.functional.behaviour");
    }

    public static bool IsTechnical(QualityCategory category) => category == QualityCategory.Technical;

    // Categories are exclusive: the first rule that matches wins. Precedence (documented in the Delivery Management report): technical work, then
    // privacy, security, performance and reliability (an integration requirement about access, latency or retry keeps that meaning), then
    // integration, then data quality (a message checked against its schema on its way between systems is integration), compliance, availability.
    // Since3: rules of functional-quality/3 only. Besides Integration, a delivery-failure rule ("retries", "backoff", "silently discard") keeps failure
    // handling Reliability: without it such an item would now read as Integration; the legacy rule set did not need it and is left unchanged.
    private static readonly (QualityCategory Category, string Rule, bool Since3, Func<string, bool> Match)[] Rules =
    [
        (QualityCategory.Technical, "fq.technical.migration", false, t => TechnicalRe().IsMatch(t)),
        (QualityCategory.Privacy, "fq.privacy.wording", false, t => PrivacyRe().IsMatch(t)),
        (QualityCategory.Security, "fq.security.wording", false, t => SecurityRe().IsMatch(t)),
        (QualityCategory.Performance, "fq.performance.wording", false, t => PerformanceRe().IsMatch(t)),
        (QualityCategory.Reliability, "fq.reliability.wording", false, t => ReliabilityRe().IsMatch(t)),
        (QualityCategory.Reliability, "fq.reliability.delivery-failure", true, t => DeliveryFailureRe().IsMatch(t)),
        (QualityCategory.Integration, "fq.integration.wording", true, t => IntegrationWordRe().IsMatch(t)),
        (QualityCategory.Integration, "fq.integration.system-exchange", true, t => IntegrationChannelRe().IsMatch(t) && IntegrationExchangeRe().IsMatch(t)),
        (QualityCategory.DataQuality, "fq.data-quality.wording", false, t => DataQualityRe().IsMatch(t)),
        (QualityCategory.Compliance, "fq.compliance.wording", false, t => ComplianceRe().IsMatch(t)),
        (QualityCategory.Availability, "fq.availability.wording", false, t => AvailabilityRe().IsMatch(t)),
    ];

    /// <summary>
    /// Classifies one item; <paramref name="featureTitle"/> lets a technical feature mark its stories technical. Integration needs the item's intent,
    /// not one keyword: either it says it is about an integration ("integrasjon", "integration with/layer/contract", never "integration test"), or it
    /// names an exchange channel (message, event stream, queue, API, Altinn, CDC…) AND a system-to-system exchange (receives from, sends to, delivers,
    /// publishes, consumers, an external system). <paramref name="ruleSet"/> = the rule set to apply (the legacy one has no Integration).
    /// </summary>
    public static (QualityCategory Category, string Rule) Classify(string? text, string? featureTitle = null, string? ruleSet = null)
    {
        if (featureTitle is { Length: > 0 } && TechnicalRe().IsMatch(featureTitle)) return (QualityCategory.Technical, "fq.technical.feature");
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Length < 6) return (QualityCategory.Unclassified, "fq.unclassified.no-text");
        var current = HasIntegration(ruleSet);
        foreach (var (category, rule, since3, match) in Rules)
            if ((current || !since3) && match(text)) return (category, rule);
        return (QualityCategory.Functional, "fq.functional.behaviour");
    }

    public static string Label(QualityCategory c) => c switch
    {
        QualityCategory.DataQuality => "Data quality",
        QualityCategory.Availability => "Availability / resilience",
        _ => c.ToString(),
    };

    /// <summary>Display order of the categories (Integration after Functional, although its enum value is last).</summary>
    public static readonly QualityCategory[] DefaultCategories =
    [
        QualityCategory.Functional, QualityCategory.Integration, QualityCategory.Security, QualityCategory.Privacy, QualityCategory.Performance,
        QualityCategory.Reliability, QualityCategory.DataQuality, QualityCategory.Compliance, QualityCategory.Availability, QualityCategory.Unclassified,
    ];

    /// <summary>Every category in display order, technical work last.</summary>
    public static readonly QualityCategory[] AllCategories = [.. DefaultCategories, QualityCategory.Technical];

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

    [GeneratedRegex(@"(?i)\b(retries|retried|backoff|back-off|silently (?:discard|drop|lose)\w*|never (?:silently )?(?:discard|drop|lose)\w*|forsøk\w* på nytt)\b")]
    private static partial Regex DeliveryFailureRe();

    [GeneratedRegex(@"(?i)\b(integrasjon\w*|systemintegrasjon\w*|integration (?:with|between|layer|point|contract|flow|partner)s?|system[- ]to[- ]system|interoperab\w*)\b")]
    private static partial Regex IntegrationWordRe();

    [GeneratedRegex(@"(?i)\b(messages?|melding\w*|event hubs?|service bus|cdc|change[- ]data[- ]capture|queues?|topics?|webhooks?|apis?|endpoints?|endepunkt\w*|payloads?|receipts?|kvittering\w*|altinn|rapportmottak|wire|stream)\b")]
    private static partial Regex IntegrationChannelRe();

    [GeneratedRegex(@"(?i)\b(receiv\w*|mottar|mottak\w*|sends? (?:it |them |the \w+ )?to|sender (?:den |det )?til|deliver\w*|leverer\w*|publish\w*|publiser\w*|consum\w*|subscrib\w*|reaches|arrives?|ingest\w*|innmating|from (?:the )?(?:BiRK|Altinn|an? (?:external|other|upstream) (?:system|service))|fra (?:BiRK|Altinn)|external system\w*|eksterne? system\w*|between (?:systems|services|modules)|mellom (?:systemer|tjenester)|upstream|downstream|callers?)\b")]
    private static partial Regex IntegrationExchangeRe();

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
