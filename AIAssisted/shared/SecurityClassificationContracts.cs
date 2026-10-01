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

/// <summary>What completing the review needs, in the order a test lead acts on it. Missing evidence is never a finding.
/// RequiredForLiveChecks is the runtime test context (the stored name is kept so recorded runs stay readable).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClassificationMissingGroup { RequiredForLiveChecks, RuntimeEvidence, Secondary, SourceEvidence }

/// <summary>Readiness of one review prerequisite. Never Pass/Fail: a prerequisite is present or not, never a security result.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClassificationReadiness { Ready, Partial, Missing, NotAssessed, NotAvailable, Disabled }

/// <summary>How much classification-relevant source the selected source scope shows for one area. Coverage, not a verdict.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClassificationCoverageState { Detected, Partial, NotFound, NotAssessed }

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
    /// <summary>The exact Source Analysis snapshot(s) the fact comes from, each with its own locations. Empty for legacy archive input.</summary>
    public List<ReviewSourceProvenance> Sources { get; init; } = [];
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
    /// <summary>Repositories whose own tests this row describes (empty for legacy archive input).</summary>
    public List<string> Repositories { get; init; } = [];
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
    /// <summary>The Source Analysis snapshots this evidence was combined from (null for legacy uploaded archives).</summary>
    public ReviewSourceScope? Scope { get; init; }
}

// ── Source Analysis snapshots as Security Classification's source ─────────────────────────────────────────────────────────
// Source Analysis owns upload, immutable snapshots, fingerprints and history. When it analyzes an archive it also asks this review's analyzer
// for the classification-relevant observations of THAT snapshot (ClassificationSnapshotEvidence: facts with file:line, never a verdict).
// Security Classification combines the observations of an explicit scope (one primary + explicitly included related snapshots) at review
// time, keeping each fact tied to its own snapshot. Nothing is merged into a synthetic snapshot.

/// <summary>The CDC classification path as one snapshot's source shows it (names and locations only).</summary>
public sealed record ClassificationCdcSummary
{
    public string? EventType { get; init; }
    /// <summary>True when the event type is declared in this snapshot; false when it is only constructed here (declared elsewhere).</summary>
    public bool EventDeclared { get; init; }
    public string? LevelParameter { get; init; }
    public string? DeserializerType { get; init; }
    public string? DeserializerMethod { get; init; }
    public SourceLocation? DeserializerLocation { get; init; }
    public bool ConstantGuardInput { get; init; }
    public int? ConstantValue { get; init; }
    public bool DeleteFromBefore { get; init; }
    public string? GuardType { get; init; }
    public List<int> GuardedLevels { get; init; } = [];
    public bool RouterFound { get; init; }
    public bool GuardBeforeMapping { get; init; }
    public bool DeletesDiscarded { get; init; }
    public string? PayloadField { get; init; }
    public SourceLocation? MapperLocation { get; init; }
}

public sealed record ReferencedNamespace(string Name, int Files, List<string> Examples);

/// <summary>A place that names a level with a Kode 6/7 label (documentation or a test name); compared with the reference data at review time.</summary>
public sealed record ClassificationTerminologyMention(string Kind, int Level, string Code, string Where, SourceLocation Location);

/// <summary>
/// Classification-relevant source observations of ONE Source Analysis snapshot, captured when Source Analysis analyzes the archive. Facts and
/// locations only: no review result, no Pass, no "secure" — Security Classification interprets them per review. Null on snapshots analyzed
/// before it was captured.
/// </summary>
public sealed record ClassificationSnapshotEvidence
{
    public int AnalyzerVersion { get; init; }
    public List<ClassificationLevel> Levels { get; init; } = [];
    public List<ClassificationFact> Facts { get; init; } = [];
    public ClassificationCdcSummary Cdc { get; init; } = new();
    public List<ClassificationTerminologyMention> Terminology { get; init; } = [];
    public List<RepositoryTestCoverage> TestCoverage { get; init; } = [];
    /// <summary>Classification-relevant types declared here (reference model, CDC event, guard).</summary>
    public List<string> DeclaredTypes { get; init; } = [];
    /// <summary>Classification-relevant types this snapshot uses but does not declare.</summary>
    public List<string> ReferencedTypes { get; init; } = [];
    /// <summary>Security-named namespaces declared here (e.g. a shared authorization package).</summary>
    public List<string> DeclaredNamespaces { get; init; } = [];
    /// <summary>Security-named namespaces this snapshot imports but does not declare (exact names, with how many files import them).</summary>
    public List<ReferencedNamespace> ReferencedNamespaces { get; init; } = [];
    /// <summary>Why no observations could be extracted from the archive (null when extraction ran).</summary>
    public string? Unavailable { get; init; }
}

public sealed record ClassificationCoverageRow(string Id, string Title, ClassificationCoverageState State, string Detail);

/// <summary>Read-only view of Source Analysis for this review (shared source options) plus, for a chosen scope, its combined evidence.</summary>
public sealed record ClassificationScopeOptions : ReviewSourceOptions
{
    public ClassificationSourceEvidence? Evidence { get; init; }
    public List<ClassificationCoverageRow> Coverage { get; init; } = [];
}

/// <summary>The security source coverage summary of a source scope. Detected = found in source — never protected, secure or passed.</summary>
public static class ClassificationSourceCoverage
{
    public const string RuntimeNote = "Source evidence shows implementation/configuration paths only. It does not prove runtime enforcement.";
    private static readonly ClassificationArea[] AuthorizationAreas = [ClassificationArea.DirectAccess, ClassificationArea.Search, ClassificationArea.GraphQL, ClassificationArea.ChildAccess, ClassificationArea.AuditAccess];

    private static bool Found(ClassificationFact? f) => f is not null && f.State is not (ClassificationState.NotFound or ClassificationState.NotApplicable);

    public static List<ClassificationCoverageRow> Rows(ClassificationSourceEvidence? evidence)
    {
        const string runtime = "Runtime authorization is assessed only by the safe live checks.";
        if (evidence is null)
            return
            [
                new("model", "Classification model", ClassificationCoverageState.NotAssessed, "No source snapshot selected."),
                new("cdc", "CDC classification path", ClassificationCoverageState.NotAssessed, "No source snapshot selected."),
                new("authorization", "Authorization path", ClassificationCoverageState.NotAssessed, "No source snapshot selected."),
                new("guard", "Graded-access guard", ClassificationCoverageState.NotAssessed, "No source snapshot selected."),
                new("runtime", "Runtime authorization", ClassificationCoverageState.NotAssessed, runtime),
            ];
        ClassificationFact? F(string id) => evidence.Facts.FirstOrDefault(f => f.Id == id);
        string Issues(IEnumerable<ClassificationFact> facts) => facts.Count(f => f.State == ClassificationState.IssueDetected) is var n and > 0 ? $" {n} source issue(s) — see findings." : "";
        if (!evidence.Detected)
        {
            const string skipped = "Not assessed: no classification model was found, so the rest of the source was not analyzed.";
            return
            [
                new("model", "Classification model", ClassificationCoverageState.NotFound, F("model-reference")?.Detail ?? "No classification reference model was found in the selected source scope."),
                new("cdc", "CDC classification path", ClassificationCoverageState.NotAssessed, skipped),
                new("authorization", "Authorization path", ClassificationCoverageState.NotAssessed, skipped),
                new("guard", "Graded-access guard", ClassificationCoverageState.NotAssessed, skipped),
                new("runtime", "Runtime authorization", ClassificationCoverageState.NotAssessed, runtime),
            ];
        }
        var graded = evidence.Levels.Where(l => l.KreverGradertTilgang).Select(l => l.Nivaa).ToList();
        var model = new ClassificationCoverageRow("model", "Classification model", ClassificationCoverageState.Detected,
            $"{evidence.Levels.Count} level(s) from the reference data{(graded.Count > 0 ? $"; graded access for level {string.Join(", ", graded)}" : "")}.");
        var cdcParts = new (string Id, string Name)[] { ("cdc-event", "event field"), ("cdc-deserializer", "production deserializer"), ("cdc-router", "guard-before-mapping router"), ("cdc-field-name", "payload mapper") };
        var cdcFound = cdcParts.Where(p => Found(F(p.Id))).Select(p => p.Name).ToList();
        var cdcState = Found(F("cdc-deserializer")) && Found(F("cdc-field-name")) ? ClassificationCoverageState.Detected : cdcFound.Count > 0 ? ClassificationCoverageState.Partial : ClassificationCoverageState.NotFound;
        var cdc = new ClassificationCoverageRow("cdc", "CDC classification path", cdcState,
            (cdcFound.Count > 0 ? $"Found: {string.Join(", ", cdcFound)}." : "No production CDC classification path was found.") + Issues(evidence.Facts.Where(f => f.Area == ClassificationArea.Pipeline)));
        var authFacts = evidence.Facts.Where(f => AuthorizationAreas.Contains(f.Area) && Found(f)).ToList();
        var areas = authFacts.Select(f => f.Area).Distinct().ToList();
        var auth = new ClassificationCoverageRow("authorization", "Authorization path",
            areas.Count >= 2 ? ClassificationCoverageState.Detected : areas.Count == 1 ? ClassificationCoverageState.Partial : ClassificationCoverageState.NotFound,
            (areas.Count > 0 ? $"Found: {string.Join(", ", areas.Select(ClassificationLabels.Area))}." : "No access path for graded children was found.") + Issues(authFacts));
        var guardFact = F("guard-logic");
        var guard = new ClassificationCoverageRow("guard", "Graded-access guard", Found(guardFact) ? ClassificationCoverageState.Detected : ClassificationCoverageState.NotFound,
            Found(guardFact) ? $"{guardFact!.Title} found in source. Whether it receives the real level is part of the CDC path." : "No guard evaluating the classification was found.");
        return [model, cdc, auth, guard, new("runtime", "Runtime authorization", ClassificationCoverageState.NotAssessed, runtime)];
    }

    public static string Label(ClassificationCoverageState state) => state switch
    {
        ClassificationCoverageState.Detected => "Detected",
        ClassificationCoverageState.Partial => "Partial",
        ClassificationCoverageState.NotFound => "Not found",
        _ => "Not assessed",
    };
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
    /// <summary>The Source Analysis snapshots to review. Null = no source evidence (source/configuration checks from source are then not run).</summary>
    public ReviewSourceScopeRequest? SourceScope { get; init; }
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
    public List<ReviewSourceProvenance> Sources { get; init; } = [];
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
    /// <summary>The exact source snapshots behind a source finding (a cross-source finding cites each one). Empty for runtime findings and legacy input.</summary>
    public List<ReviewSourceProvenance> Sources { get; init; } = [];
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

/// <summary>One prerequisite row of the readiness card (ready rows included), grouped Source evidence / Runtime test context / Runtime evidence.</summary>
public sealed record ClassificationReadinessRow
{
    public string Id { get; init; } = "";
    public ClassificationMissingGroup Group { get; init; }
    public string Title { get; init; } = "";
    public ClassificationReadiness Status { get; init; }
    public string Detail { get; init; } = "";
    public bool ConfiguresContext { get; init; }
    /// <summary>Counted as missing evidence (Missing or Not available outside the secondary group).</summary>
    [JsonIgnore]
    public bool Counts => Group != ClassificationMissingGroup.Secondary && Status is ClassificationReadiness.Missing or ClassificationReadiness.NotAvailable;
}

/// <summary>The source part of readiness: Source Analysis visibility, whether a scope is selected, and its coverage.</summary>
public sealed record ClassificationSourceReadiness(bool Disabled, string? Scope, IReadOnlyList<ClassificationCoverageRow> Coverage)
{
    public static ClassificationSourceReadiness For(ClassificationSourceEvidence? evidence, bool disabled = false) =>
        new(disabled, evidence?.Scope is { } s ? string.Join(" + ", new[] { s.Primary }.Concat(s.Related).Select(e => $"{e.Repository} · {e.Fingerprint[..Math.Min(8, e.Fingerprint.Length)]}…"))
            : evidence is null ? null : "Legacy source input", ClassificationSourceCoverage.Rows(evidence));
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
    /// <summary>The grouped readiness rows as of this run (empty for runs recorded before it existed).</summary>
    public List<ClassificationReadinessRow> Readiness { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
    /// <summary>The exact Source Analysis snapshots this run reviewed. Null for runs without source and for legacy archive-based runs
    /// (those carry <see cref="SourceArchives"/>); no snapshot id is ever invented for them.</summary>
    public ReviewSourceScope? SourceScope { get; init; }
}

/// <summary>
/// The one rule for "what's needed to complete this review", used for a run's snapshot and for the live card on the page (so configuring the
/// test context removes its items immediately). Ordered: required for live authorization checks, then runtime evidence, then secondary.
/// </summary>
public static class ClassificationPrerequisites
{
    /// <summary>Missing evidence (the rows that count, plus secondary items), in readiness order.</summary>
    public static List<ClassificationMissingItem> Evaluate(ClassificationSourceReadiness source, IReadOnlyList<ClassificationLevel> levels, ClassificationTestContext context,
        bool countsAvailable, string? metricName, bool telemetryObserved) =>
        Readiness(source, levels, context, countsAvailable, metricName, telemetryObserved).Where(r => r.Counts || r.Group == ClassificationMissingGroup.Secondary)
            .Select(r => new ClassificationMissingItem { Id = r.Id, Group = r.Group, Title = r.Title, Detail = r.Detail, ConfiguresContext = r.ConfiguresContext }).ToList();

    /// <summary>
    /// Every prerequisite with its readiness, grouped: Source evidence (a Source Analysis snapshot and what it shows — never an archive to
    /// upload here), Runtime test context (what live checks need), Runtime evidence (counts, telemetry), then secondary items.
    /// </summary>
    public static List<ClassificationReadinessRow> Readiness(ClassificationSourceReadiness source, IReadOnlyList<ClassificationLevel> levels, ClassificationTestContext context,
        bool countsAvailable, string? metricName, bool telemetryObserved)
    {
        var rows = new List<ClassificationReadinessRow>();
        void Add(string id, ClassificationMissingGroup group, string title, ClassificationReadiness status, string detail, bool configures = false) =>
            rows.Add(new ClassificationReadinessRow { Id = id, Group = group, Title = title, Status = status, Detail = detail, ConfiguresContext = configures && status != ClassificationReadiness.Ready });
        const ClassificationMissingGroup S = ClassificationMissingGroup.SourceEvidence, C = ClassificationMissingGroup.RequiredForLiveChecks, R = ClassificationMissingGroup.RuntimeEvidence;
        if (source.Disabled && source.Scope is null)
            Add("source", S, "Source Analysis snapshot", ClassificationReadiness.Disabled, "Source Analysis is disabled in Feature Visibility; source checks are not run. Stored reviews stay readable.");
        else if (source.Scope is null)
            Add("source", S, "Source Analysis snapshot", ClassificationReadiness.Missing, "Not selected — choose a source snapshot managed by Source Analysis. Source checks are not run without one.");
        else
            Add("source", S, "Source Analysis snapshot", ClassificationReadiness.Ready, source.Scope);
        foreach (var row in source.Coverage.Where(r => r.Id is "model" or "cdc" or "authorization"))
            Add($"source-{row.Id}", S, row.Title, row.State switch
            {
                ClassificationCoverageState.Detected => ClassificationReadiness.Ready,
                ClassificationCoverageState.Partial => ClassificationReadiness.Partial,
                ClassificationCoverageState.NotFound => ClassificationReadiness.Missing,
                _ => ClassificationReadiness.NotAssessed,
            }, row.Detail);
        var env = context.Environment?.Trim().ToUpperInvariant();
        Add("context-environment", C, "Approved DEV/QA environment", env is "DEV" or "QA" ? ClassificationReadiness.Ready : ClassificationReadiness.Missing,
            env is "DEV" or "QA" ? env : "Not configured (DEV or QA only — Production is never tested).", configures: true);
        Add("context-endpoint", C, "Person GraphQL endpoint", string.IsNullOrWhiteSpace(context.GraphQlEndpoint) ? ClassificationReadiness.Missing : ClassificationReadiness.Ready,
            string.IsNullOrWhiteSpace(context.GraphQlEndpoint) ? "Not configured." : "Configured.", configures: true);
        Add("context-approval", C, "Test-lead approval", context.ApprovedByTestLead ? ClassificationReadiness.Ready : ClassificationReadiness.Missing,
            context.ApprovedByTestLead ? "Approved." : "Not approved.", configures: true);
        foreach (var level in levels)
        {
            var configured = context.TestChildren.Any(c => c.Nivaa == level.Nivaa && c.BarnRegistreringId is not null);
            var codes = string.Join(" / ", new[] { level.BiRKKode, level.ElementsKode }.OfType<string>().Where(c => c.Length > 0).Distinct());
            Add($"child-{level.Nivaa}", C, $"Synthetic level {level.Nivaa} test child ({level.Verdi}{(codes.Length > 0 ? $" / {codes}" : "")})", configured ? ClassificationReadiness.Ready : ClassificationReadiness.Missing,
                configured ? "Configured (id only)." : "A synthetic or approved test child with this level, identified by id only.", configures: true);
        }
        Add("identity-unauthorized", C, "Unauthorized test identity", string.IsNullOrWhiteSpace(context.UnauthorizedIdentityLabel) ? ClassificationReadiness.Missing : ClassificationReadiness.Ready,
            string.IsNullOrWhiteSpace(context.UnauthorizedIdentityLabel) ? "A test identity without graded access (label only; its token is given per run)." : "Configured (label only; token per run).", configures: true);
        Add("identity-authorized", C, "Authorized graded identity", string.IsNullOrWhiteSpace(context.AuthorizedIdentityLabel) ? ClassificationReadiness.Missing : ClassificationReadiness.Ready,
            string.IsNullOrWhiteSpace(context.AuthorizedIdentityLabel) ? "A test identity with graded access (label only; its token is given per run)." : "Configured (label only; token per run).", configures: true);
        Add("counts", R, "Aligned classification counts (source and target system)", countsAvailable ? ClassificationReadiness.Ready : ClassificationReadiness.Missing,
            countsAvailable ? "Supplied for this run — data consistency only, never authorization." : "Counts per level from both systems (e.g. BiRK and the consuming service), captured at aligned times (run option).");
        Add("telemetry", R, $"Runtime telemetry for {metricName ?? "the Kode 6/7 rejection metric"}", telemetryObserved ? ClassificationReadiness.Ready : ClassificationReadiness.NotAvailable,
            telemetryObserved ? "Observed." : "No telemetry source is connected: the runtime value is Not available (never 0 rejections).");
        Add("browser", ClassificationMissingGroup.Secondary, "Browser storage / route leakage checks", ClassificationReadiness.NotAssessed, "Not part of this version (Browser Companion). Browser evidence would not prove server authorization.");
        return rows;
    }
}

public sealed record ClassificationRunSummary(Guid RunId, DateTimeOffset CompletedAt, ClassificationOverall Overall, int Findings, int LiveObservations);

public sealed record ClassificationOverview
{
    /// <summary>No longer set: source evidence comes from the Source Analysis scope chosen on the page (see <see cref="ClassificationScopeOptions"/>).
    /// Kept so clients that read it get null instead of a stale uploaded-archive analysis.</summary>
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
        ClassificationMissingGroup.SourceEvidence => "Source evidence",
        ClassificationMissingGroup.RequiredForLiveChecks => "Runtime test context",
        ClassificationMissingGroup.RuntimeEvidence => "Runtime evidence",
        _ => "Optional / secondary",
    };

    public static string Readiness(ClassificationReadiness status) => status switch
    {
        ClassificationReadiness.NotAssessed => "Not assessed",
        ClassificationReadiness.NotAvailable => "Not available",
        _ => status.ToString(),
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
