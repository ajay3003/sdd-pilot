using System.Text.Json.Serialization;

namespace BirkNext.Integrations;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// Security Classification / Gradert tilgang review. Classification levels and their meaning come from the analyzed source, never from
// BirkNext. Source evidence (what the code does), configuration (the approved test context) and runtime evidence (safe GraphQL QUERIES
// against configured synthetic test children only) stay separate. A guard that exists is not a guard that receives the right value; a
// count match is data consistency, never authorization. BirkNext never searches for real classified children, never mutates
// classification or grants, never runs against Production, and stores no child PII, token or raw payload.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Check states. Pass/Fail only for a bounded executed check with an explicit expectation; source facts use SourceVerified,
/// Warning, IssueDetected, DocumentedOnly or NotFound; NotTested is never Pass; NoIndicatorsObserved is never Pass. NotApplicable = the check is
/// irrelevant here; NotAssessedHere = relevant to the end-to-end flow but not evaluated by this review (outside the analyzed source, or owned by
/// another review) — never the same thing.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClassificationState
{
    Pass, Fail, IssueDetected, Warning, SourceVerified, Configured, Observed, Verified, Partial, NotTested, NotAvailable, NeedsDecision,
    NotApplicable, NoIndicatorsObserved, DocumentedOnly, NotFound, NotAssessedHere,
}

/// <summary>What completing the review needs, in the order a test lead acts on it. Missing evidence is never a finding.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClassificationMissingGroup { RequiredForLiveChecks, RuntimeEvidence, Secondary }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClassificationTestType { Static, Functional, Negative, NonFunctional, DataConsistency }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClassificationArea
{
    Model, Pipeline, Guard, DirectAccess, Search, GraphQL, AuditAccess, ChildAccess, Grants, EmergencyAccess, ReadLogging, Caching, Privacy,
    Observability, Consistency, Tests,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClassificationPipelineStage { BiRK, Debezium, EventHub, PersonAdapterDeserialization, GuardInput, Guard, PersonService }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClassificationSeverity { Critical, High, Medium, Low, Info }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClassificationOverall { Verified, Partial, IssueDetected, NotTestable }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClassificationIdentity { Unauthorized, Authorized }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClassificationSurface { DirectProfile, Search, SearchTotalCount, AuditLog, NonexistentComparison }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CountComparisonState { Match, Mismatch, NotComparable, NotAvailable }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RepositoryTestCoverageState { Present, UnitOnly, Missing }

public sealed record ClassificationLevel
{
    public int Nivaa { get; init; }
    public string Verdi { get; init; } = "";
    public string? BiRKKode { get; init; }
    public string? ElementsKode { get; init; }
    public string Beskrivelse { get; init; } = "";
    public bool KreverGradertTilgang { get; init; }
    public SourceLocation? Location { get; init; }
}

public sealed record ClassificationFact
{
    public string Id { get; init; } = "";
    public ClassificationArea Area { get; init; }
    public ClassificationTestType TestType { get; init; } = ClassificationTestType.Static;
    public string Title { get; init; } = "";
    public ClassificationState State { get; init; }
    public string Detail { get; init; } = "";
    public List<SourceLocation> Locations { get; init; } = [];
}

/// <summary>One pipeline stage with its source/configuration state and its runtime state kept apart.</summary>
public sealed record ClassificationStageEvidence
{
    public ClassificationPipelineStage Stage { get; init; }
    public string Title { get; init; } = "";
    public ClassificationState Source { get; init; }
    public string SourceDetail { get; init; } = "";
    public ClassificationState Runtime { get; init; } = ClassificationState.NotTested;
    public string RuntimeDetail { get; init; } = "";
}

/// <summary>How the analyzed repository's own tests cover a behaviour. UnitOnly = only a directly constructed input (no production path).</summary>
public sealed record RepositoryTestCoverage
{
    public string Scenario { get; init; } = "";
    public RepositoryTestCoverageState State { get; init; }
    public List<string> Tests { get; init; } = [];
    public string Note { get; init; } = "";
}

/// <summary>A regression test BirkNext proposes for the analyzed repository. Text only — BirkNext never runs or commits it.</summary>
public sealed record ProposedRegressionTest
{
    public string Name { get; init; } = "";
    public string Purpose { get; init; } = "";
    public string Code { get; init; } = "";
}

public sealed record ClassificationSourceEvidence
{
    public string EnvironmentId { get; init; } = "";
    public DateTimeOffset AnalyzedAt { get; init; }
    public int AnalyzerVersion { get; init; }
    public List<SourceArchive> Archives { get; init; } = [];
    /// <summary>True only when a classification reference model (levels with KreverGradertTilgang) is found.</summary>
    public bool Detected { get; init; }
    public List<ClassificationLevel> Levels { get; init; } = [];
    public List<ClassificationFact> Facts { get; init; } = [];
    public List<ClassificationStageEvidence> Pipeline { get; init; } = [];
    public List<RepositoryTestCoverage> TestCoverage { get; init; } = [];
    public List<ProposedRegressionTest> ProposedTests { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
}

/// <summary>A configured synthetic/approved test child. Opaque ids only — never a name, national id or address.</summary>
public sealed record ClassificationTestChild
{
    public int Nivaa { get; init; }
    public Guid? BarnRegistreringId { get; init; }
    /// <summary>The child's BiRK id, used only to search for exactly this test child.</summary>
    public string? BirkId { get; init; }
}

/// <summary>
/// The explicit, approved context live checks need. DEV/QA only, synthetic test children per level and two identity LABELS. Tokens are
/// supplied per run and never stored. Live mutation (classification change, grant, revoke) is never enabled by BirkNext.
/// </summary>
public sealed record ClassificationTestContext
{
    public string? Environment { get; init; }
    /// <summary>GraphQL endpoint of the Person module in the approved environment.</summary>
    public string? GraphQlEndpoint { get; init; }
    public List<ClassificationTestChild> TestChildren { get; init; } = [];
    public string? UnauthorizedIdentityLabel { get; init; }
    public string? AuthorizedIdentityLabel { get; init; }
    public bool ApprovedByTestLead { get; init; }
    /// <summary>Always false: live mutation tests are not offered.</summary>
    public bool LiveMutationTests => false;
    public DateTimeOffset? UpdatedAt { get; init; }

    public string? Validate()
    {
        if (Environment is { Length: > 0 } env && env.Trim().ToUpperInvariant() is not ("DEV" or "QA")) return "Live security checks are limited to DEV or QA test contexts.";
        if (GraphQlEndpoint is { Length: > 0 } url)
        {
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return "The GraphQL endpoint is an absolute https URL.";
            if (uri.Scheme == "http" && !uri.IsLoopback) return "The GraphQL endpoint must use https (plain http is accepted for localhost only).";
            if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query)) return "The GraphQL endpoint must not carry credentials or a query string.";
        }
        foreach (var label in new[] { UnauthorizedIdentityLabel, AuthorizedIdentityLabel })
            if (label is { Length: > 0 } l && (l.Contains('@') || l.Length > 60)) return "Identity labels are short descriptions (no e-mail address or user name).";
        if (TestChildren.Any(c => c.Nivaa is < 0 or > 3)) return "Test children are configured for levels 0–3.";
        if (TestChildren.GroupBy(c => c.Nivaa).Any(g => g.Count() > 1)) return "Configure at most one test child per level.";
        if (TestChildren.Any(c => c.BirkId is { Length: > 40 })) return "The BiRK id is an identifier, not free text.";
        return null;
    }
}

/// <summary>Which parts of the temporary test context were available, never the values themselves.</summary>
public sealed record ClassificationContextSummary
{
    public string? Environment { get; init; }
    public bool Approved { get; init; }
    public bool EndpointConfigured { get; init; }
    public List<int> ConfiguredLevels { get; init; } = [];
    public List<int> LevelsWithBirkId { get; init; } = [];
    public bool UnauthorizedIdentityConfigured { get; init; }
    public bool AuthorizedIdentityConfigured { get; init; }

    public static ClassificationContextSummary From(ClassificationTestContext context) => new()
    {
        Environment = context.Environment is { Length: > 0 } env ? env.Trim().ToUpperInvariant() : null, Approved = context.ApprovedByTestLead,
        EndpointConfigured = !string.IsNullOrWhiteSpace(context.GraphQlEndpoint),
        ConfiguredLevels = context.TestChildren.Where(c => c.BarnRegistreringId is not null).Select(c => c.Nivaa).Distinct().Order().ToList(),
        LevelsWithBirkId = context.TestChildren.Where(c => !string.IsNullOrWhiteSpace(c.BirkId)).Select(c => c.Nivaa).Distinct().Order().ToList(),
        UnauthorizedIdentityConfigured = !string.IsNullOrWhiteSpace(context.UnauthorizedIdentityLabel),
        AuthorizedIdentityConfigured = !string.IsNullOrWhiteSpace(context.AuthorizedIdentityLabel),
    };
}

/// <summary>What a live run is given in addition to the temporary (in-memory) context. Tokens are held in memory for the run only.</summary>
public sealed record ClassificationRunRequest
{
    public string? EnvironmentType { get; init; }
    public string? UnauthorizedToken { get; init; }
    public string? AuthorizedToken { get; init; }
    /// <summary>Optional, operator-supplied classification count evidence (timestamped observations, never expectations).</summary>
    public ClassificationCountEvidence? SourceCounts { get; init; }
    public ClassificationCountEvidence? TargetCounts { get; init; }
}

/// <summary>One safe GraphQL query observation for a configured test child. Derived facts only — no response body is kept.</summary>
public sealed record ClassificationObservation
{
    public int Nivaa { get; init; }
    public ClassificationIdentity Identity { get; init; }
    public ClassificationSurface Surface { get; init; }
    public ClassificationTestType TestType { get; init; }
    public ClassificationState State { get; init; }
    public string Expected { get; init; } = "";
    public string Observed { get; init; } = "";
    public string Detail { get; init; } = "";
}

public sealed record ClassificationLiveEvidence
{
    public IntegrationEvidenceState State { get; init; }
    public string Reason { get; init; } = "";
    /// <summary>Scheme and host only.</summary>
    public string? Target { get; init; }
    public DateTimeOffset CapturedAt { get; init; }
    public List<ClassificationObservation> Observations { get; init; } = [];
}

/// <summary>Classification counts of one system at one time, with where they came from. Observed evidence — never a business rule.</summary>
public sealed record ClassificationCountEvidence
{
    public string System { get; init; } = "";
    public DateTimeOffset CapturedAt { get; init; }
    public DateTimeOffset? WindowStart { get; init; }
    public DateTimeOffset? WindowEnd { get; init; }
    public string Provenance { get; init; } = "";
    /// <summary>Level (Nivaa) → count.</summary>
    public Dictionary<int, long> Counts { get; init; } = [];
}

public sealed record ClassificationCountComparison
{
    public int Nivaa { get; init; }
    public long? Source { get; init; }
    public long? Target { get; init; }
    public CountComparisonState State { get; init; }
    public string Detail { get; init; } = "";
}

public sealed record ClassificationCheck
{
    public string CheckId { get; init; } = "";
    public ClassificationArea Area { get; init; }
    public ClassificationTestType TestType { get; init; }
    public string Title { get; init; } = "";
    public ClassificationState State { get; init; }
    public string Detail { get; init; } = "";
    public IntegrationEvidenceSource Provenance { get; init; }
    public List<SourceLocation> Locations { get; init; } = [];
}

public sealed record ClassificationFinding
{
    public string RuleId { get; init; } = "";
    public ClassificationSeverity Severity { get; init; }
    public ClassificationArea Area { get; init; }
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public List<string> Evidence { get; init; } = [];
    public string Recommendation { get; init; } = "";
}

/// <summary>One row of the summary: an area and its state with the kind of evidence behind it.</summary>
public sealed record ClassificationSummaryRow(ClassificationArea Area, string Title, ClassificationState State, string Evidence)
{
    /// <summary>Evidence items behind the state when they differ in kind (e.g. metric definition in source vs runtime telemetry).</summary>
    public List<ClassificationSubStatus> Parts { get; init; } = [];
}

public sealed record ClassificationSubStatus(string Name, ClassificationState State, string Detail);

/// <summary>One thing needed to complete the review. <see cref="ConfiguresContext"/> = resolved in "Configure test context".</summary>
public sealed record ClassificationMissingItem
{
    public string Id { get; init; } = "";
    public ClassificationMissingGroup Group { get; init; }
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public bool ConfiguresContext { get; init; }
}

/// <summary>A stored, immutable review run (source snapshot + configuration + safe runtime observations + counts).</summary>
public sealed record ClassificationReviewResult
{
    public Guid RunId { get; init; }
    public string EnvironmentId { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public ClassificationOverall Overall { get; init; }
    public DateTimeOffset? SourceAnalyzedAt { get; init; }
    public List<SourceArchive> SourceArchives { get; init; } = [];
    public List<ClassificationLevel> Levels { get; init; } = [];
    public List<ClassificationSummaryRow> Summary { get; init; } = [];
    public List<ClassificationStageEvidence> Pipeline { get; init; } = [];
    public List<ClassificationCheck> Checks { get; init; } = [];
    public List<ClassificationFinding> Findings { get; init; } = [];
    public ClassificationLiveEvidence Live { get; init; } = new();
    public ClassificationCountEvidence? SourceCounts { get; init; }
    public ClassificationCountEvidence? TargetCounts { get; init; }
    public List<ClassificationCountComparison> CountComparisons { get; init; } = [];
    public List<RepositoryTestCoverage> TestCoverage { get; init; } = [];
    public List<ProposedRegressionTest> ProposedTests { get; init; } = [];
    /// <summary>Only in runs recorded before the test context became in-memory only, if such a run carried context values. New runs
    /// leave it empty and carry <see cref="ContextSummary"/> instead; this value is never shown or exported.</summary>
    public ClassificationTestContext Context { get; init; } = new();
    /// <summary>What the temporary test context provided for this run, without any value (no id, BiRK id, label, token or endpoint).</summary>
    public ClassificationContextSummary? ContextSummary { get; init; }
    public List<string> Missing { get; init; } = [];
    /// <summary>Structured, prioritized missing evidence as of this run (empty for runs recorded before it existed; <see cref="Missing"/> then applies).</summary>
    public List<ClassificationMissingItem> MissingItems { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
}

/// <summary>
/// The one rule for "what's needed to complete this review", used for a run's snapshot and for the live card on the page (so configuring the
/// test context removes its items immediately). Ordered: required for live authorization checks, then runtime evidence, then secondary.
/// </summary>
public static class ClassificationPrerequisites
{
    public static List<ClassificationMissingItem> Evaluate(bool sourceAnalyzed, IReadOnlyList<ClassificationLevel> levels, ClassificationTestContext context,
        bool countsAvailable, string? metricName, bool telemetryObserved)
    {
        var items = new List<ClassificationMissingItem>();
        void Add(string id, ClassificationMissingGroup group, string title, string detail, bool configures = false) =>
            items.Add(new ClassificationMissingItem { Id = id, Group = group, Title = title, Detail = detail, ConfiguresContext = configures });
        if (!sourceAnalyzed)
            Add("source", ClassificationMissingGroup.RequiredForLiveChecks, "M2LB source archive", "Upload the repository archive to analyze the classification model, the CDC path and the access paths.");
        var env = context.Environment?.Trim().ToUpperInvariant();
        var contextGaps = new List<string>();
        if (env is not ("DEV" or "QA")) contextGaps.Add("a DEV or QA environment");
        if (string.IsNullOrWhiteSpace(context.GraphQlEndpoint)) contextGaps.Add("the Person GraphQL endpoint");
        if (!context.ApprovedByTestLead) contextGaps.Add("test-lead approval");
        if (contextGaps.Count > 0)
            Add("context", ClassificationMissingGroup.RequiredForLiveChecks, "Approved DEV/QA security test context", $"Not configured: {string.Join(", ", contextGaps)}.", configures: true);
        foreach (var level in levels.Where(l => !context.TestChildren.Any(c => c.Nivaa == l.Nivaa && c.BarnRegistreringId is not null)))
        {
            var codes = string.Join(" / ", new[] { level.BiRKKode, level.ElementsKode }.OfType<string>().Where(c => c.Length > 0).Distinct());
            Add($"child-{level.Nivaa}", ClassificationMissingGroup.RequiredForLiveChecks, $"Synthetic level {level.Nivaa} test child ({level.Verdi}{(codes.Length > 0 ? $" / {codes}" : "")})",
                "A synthetic or approved test child with this level, identified by id only.", configures: true);
        }
        if (string.IsNullOrWhiteSpace(context.UnauthorizedIdentityLabel))
            Add("identity-unauthorized", ClassificationMissingGroup.RequiredForLiveChecks, "Unauthorized test identity", "A test identity without graded access (label only; its token is given per run).", configures: true);
        if (string.IsNullOrWhiteSpace(context.AuthorizedIdentityLabel))
            Add("identity-authorized", ClassificationMissingGroup.RequiredForLiveChecks, "Authorized graded identity", "A test identity with graded access (label only; its token is given per run).", configures: true);
        if (!countsAvailable)
            Add("counts", ClassificationMissingGroup.RuntimeEvidence, "Classification count evidence from BiRK and M2LB", "Counts per level from both systems, captured at aligned times (run option).");
        if (!telemetryObserved)
            Add("telemetry", ClassificationMissingGroup.RuntimeEvidence, $"Runtime telemetry for {metricName ?? "the Kode 6/7 rejection metric"}",
                "No telemetry source is connected: the runtime value is Not available (never 0 rejections).");
        Add("browser", ClassificationMissingGroup.Secondary, "Browser storage / route leakage checks", "Not part of this version (Browser Companion). Browser evidence would not prove server authorization.");
        return items;
    }
}

public sealed record ClassificationRunSummary(Guid RunId, DateTimeOffset CompletedAt, ClassificationOverall Overall, int Findings, int LiveObservations);

public sealed record ClassificationOverview
{
    public ClassificationSourceEvidence? Source { get; init; }
    public ClassificationTestContext Context { get; init; } = new();
    public ClassificationReviewResult? Latest { get; init; }
    public List<ClassificationRunSummary> History { get; init; } = [];
}

public static class ClassificationLabels
{
    public static string State(ClassificationState state) => state switch
    {
        ClassificationState.IssueDetected => "Issue detected",
        ClassificationState.SourceVerified => "Source verified",
        ClassificationState.NotTested => "Not tested",
        ClassificationState.NotAvailable => "Not available",
        ClassificationState.NeedsDecision => "Needs decision",
        ClassificationState.NotApplicable => "Not applicable",
        ClassificationState.NotAssessedHere => "Not assessed here",
        ClassificationState.NoIndicatorsObserved => "No indicators observed",
        ClassificationState.DocumentedOnly => "Documented only",
        ClassificationState.NotFound => "Not found",
        _ => state.ToString(),
    };

    /// <summary>Help text for states whose meaning is easy to confuse.</summary>
    public static string? StateHelp(ClassificationState state) => state switch
    {
        ClassificationState.NotAssessedHere => "This stage is relevant to the end-to-end flow but is not evaluated by this review.",
        ClassificationState.NotApplicable => "This check does not apply here.",
        ClassificationState.NotAvailable => "The evidence source did not provide a value — this is not zero.",
        _ => null,
    };

    public static string MissingGroup(ClassificationMissingGroup group) => group switch
    {
        ClassificationMissingGroup.RequiredForLiveChecks => "Required for live authorization checks",
        ClassificationMissingGroup.RuntimeEvidence => "Runtime evidence",
        _ => "Optional / secondary",
    };

    public static string TestType(ClassificationTestType type) => type switch
    {
        ClassificationTestType.Static => "Static security",
        ClassificationTestType.Functional => "Functional security",
        ClassificationTestType.Negative => "Negative security",
        ClassificationTestType.NonFunctional => "Non-functional security",
        _ => "Data consistency",
    };

    public static string Area(ClassificationArea area) => area switch
    {
        ClassificationArea.Model => "Classification model",
        ClassificationArea.Pipeline => "CDC propagation",
        ClassificationArea.Guard => "Guard",
        ClassificationArea.DirectAccess => "Direct access protection",
        ClassificationArea.Search => "Search protection",
        ClassificationArea.GraphQL => "GraphQL protection",
        ClassificationArea.AuditAccess => "Revision log access",
        ClassificationArea.ChildAccess => "Child-specific authorization",
        ClassificationArea.Grants => "Grant / revoke",
        ClassificationArea.EmergencyAccess => "Emergency access",
        ClassificationArea.ReadLogging => "Audit / read logging",
        ClassificationArea.Caching => "Caching",
        ClassificationArea.Privacy => "Logging / telemetry privacy",
        ClassificationArea.Observability => "Observability",
        ClassificationArea.Consistency => "Classification consistency",
        _ => "Repository tests",
    };

    public static string Stage(ClassificationPipelineStage stage) => stage switch
    {
        ClassificationPipelineStage.EventHub => "Event Hub",
        ClassificationPipelineStage.PersonAdapterDeserialization => "Person Adapter deserialization",
        ClassificationPipelineStage.GuardInput => "CdcEvent.Sikkerhetsnivaa (guard input)",
        ClassificationPipelineStage.Guard => "SecurityClassificationGuard",
        ClassificationPipelineStage.PersonService => "PersonService",
        _ => stage.ToString(),
    };

    public static string Overall(ClassificationOverall overall) => overall switch
    {
        ClassificationOverall.IssueDetected => "Issue detected",
        ClassificationOverall.NotTestable => "Not testable",
        _ => overall.ToString(),
    };

    public static string Surface(ClassificationSurface surface) => surface switch
    {
        ClassificationSurface.DirectProfile => "Direct profile (hentBarn)",
        ClassificationSurface.Search => "Search (soekBarn)",
        ClassificationSurface.SearchTotalCount => "Search totaltAntall",
        ClassificationSurface.AuditLog => "Revision log (hentRevisjonslogg)",
        _ => "Unauthorized vs nonexistent",
    };

    public static string Coverage(RepositoryTestCoverageState state) => state switch
    {
        RepositoryTestCoverageState.UnitOnly => "Unit only (constructed input)",
        RepositoryTestCoverageState.Missing => "Missing",
        _ => "Present",
    };

    public static string Comparison(CountComparisonState state) => state switch
    {
        CountComparisonState.NotComparable => "Not comparable",
        CountComparisonState.NotAvailable => "Not available",
        _ => state.ToString(),
    };

    /// <summary>States that come from a bounded executed runtime check.</summary>
    public static bool IsRuntimeResult(ClassificationState state) => state is ClassificationState.Pass or ClassificationState.Fail or ClassificationState.Observed or ClassificationState.Verified;
}
