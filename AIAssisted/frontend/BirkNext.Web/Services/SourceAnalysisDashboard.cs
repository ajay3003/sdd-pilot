using BirkNext.Integrations;
using BirkNext.SourceArchitecture;
using BirkNext.SourceDomains;
using BirkNext.TestEvidence;

namespace BirkNext.Web.Services;

/// <summary>
/// The one status vocabulary of Source Analysis. Each state says what source reading established — never a generic "Partial" or "Complete".
/// Kept apart: what was detected, what the analyzer resolved, what source could not settle (a limitation, never a defect), and what a person
/// must decide (human review). Runtime is never verified here.
/// </summary>
public enum SourceEvidenceState
{
    Detected, InventoryComplete, SourceModelAvailable, ClassificationIncomplete, Heuristic, LimitedBySource, NotDetectedInSnapshot,
    NotVerifiedAtRuntime, NeedsReview, Conflict, Failed, ConfirmedInSource, Inferred, Ambiguous, NotApplicable, NotAssessed,
}

public static class SourceEvidenceStates
{
    public static string Label(SourceEvidenceState s) => s switch
    {
        SourceEvidenceState.Detected => "Detected",
        SourceEvidenceState.InventoryComplete => "Inventory complete",
        SourceEvidenceState.SourceModelAvailable => "Source model available",
        SourceEvidenceState.ClassificationIncomplete => "Classification incomplete",
        SourceEvidenceState.Heuristic => "Heuristic",
        SourceEvidenceState.LimitedBySource => "Limited by source",
        SourceEvidenceState.NotDetectedInSnapshot => "Not detected in this snapshot",
        SourceEvidenceState.NotVerifiedAtRuntime => "Not verified at runtime",
        SourceEvidenceState.NeedsReview => "Needs review",
        SourceEvidenceState.Conflict => "Conflict",
        SourceEvidenceState.Failed => "Failed",
        SourceEvidenceState.ConfirmedInSource => "Confirmed in source",
        SourceEvidenceState.Inferred => "Inferred",
        SourceEvidenceState.Ambiguous => "Ambiguous",
        SourceEvidenceState.NotApplicable => "Not applicable",
        _ => "Not assessed",
    };

    /// <summary>What the state means, in one sentence (the legend and tooltips use it).</summary>
    public static string Meaning(SourceEvidenceState s) => s switch
    {
        SourceEvidenceState.Detected => "Found in source; the inventory is shown, nothing is judged.",
        SourceEvidenceState.InventoryComplete => "Every item of this kind in the snapshot was read with a supported reader.",
        SourceEvidenceState.SourceModelAvailable => "A model was built from source; some relationships may be limited by what source states.",
        SourceEvidenceState.ClassificationIncomplete => "Everything was read, but some items could not be put into a category. A classification gap, not a defect.",
        SourceEvidenceState.Heuristic => "Read with heuristics (pattern-based), not a structured parser. A parse approach, not a defect.",
        SourceEvidenceState.LimitedBySource => "Source does not state enough to resolve this (e.g. a target set only at deployment).",
        SourceEvidenceState.NotDetectedInSnapshot => "Nothing of this kind exists in this snapshot. It may exist in another package.",
        SourceEvidenceState.NotVerifiedAtRuntime => "Source states it; whether it is deployed or running is outside Source Analysis.",
        SourceEvidenceState.NeedsReview => "A person must decide — the analyzer will not guess.",
        SourceEvidenceState.Conflict => "Deterministic source evidence contradicts itself.",
        SourceEvidenceState.Failed => "The analyzer could not read this area.",
        SourceEvidenceState.ConfirmedInSource => "Stated explicitly in source.",
        SourceEvidenceState.Inferred => "Derived from naming or conventions, not stated explicitly.",
        SourceEvidenceState.Ambiguous => "Several candidates match; none is chosen.",
        SourceEvidenceState.NotApplicable => "This kind of evidence does not apply to this snapshot.",
        _ => "This snapshot predates the analyzer for this area; counts are not zero, just not assessed.",
    };

    /// <summary>Badge tone (sa-tone-*): green only for established evidence, amber for limits and decisions, red for conflicts/failures.</summary>
    public static string Tone(SourceEvidenceState s) => s switch
    {
        SourceEvidenceState.InventoryComplete or SourceEvidenceState.SourceModelAvailable or SourceEvidenceState.ConfirmedInSource => "established",
        SourceEvidenceState.Detected or SourceEvidenceState.Inferred or SourceEvidenceState.Heuristic or SourceEvidenceState.NotVerifiedAtRuntime => "info",
        SourceEvidenceState.ClassificationIncomplete or SourceEvidenceState.LimitedBySource or SourceEvidenceState.NeedsReview or SourceEvidenceState.Ambiguous => "limited",
        SourceEvidenceState.Conflict or SourceEvidenceState.Failed => "conflict",
        _ => "neutral",
    };

    /// <summary>
    /// The global semantic status for a Source Analysis state, with the scope-qualified label Source Analysis shows ("Not detected in this snapshot",
    /// "Not verified at runtime", "Confirmed in source"). Same meaning as everywhere else; the label only names the scope.
    /// </summary>
    public static (SemanticStatus Status, string? QualifiedLabel) ToSemantic(SourceEvidenceState s) => s switch
    {
        SourceEvidenceState.Detected => (SemanticStatus.Detected, null),
        SourceEvidenceState.InventoryComplete => (SemanticStatus.InventoryComplete, null),
        SourceEvidenceState.SourceModelAvailable => (SemanticStatus.SourceModelAvailable, null),
        SourceEvidenceState.ClassificationIncomplete => (SemanticStatus.ClassificationIncomplete, null),
        SourceEvidenceState.Heuristic => (SemanticStatus.Heuristic, null),
        SourceEvidenceState.LimitedBySource => (SemanticStatus.LimitedBySource, null),
        SourceEvidenceState.NotDetectedInSnapshot => (SemanticStatus.NotDetected, "Not detected in this snapshot"),
        SourceEvidenceState.NotVerifiedAtRuntime => (SemanticStatus.NotVerified, "Not verified at runtime"),
        SourceEvidenceState.NeedsReview => (SemanticStatus.NeedsReview, null),
        SourceEvidenceState.Conflict => (SemanticStatus.Conflict, null),
        SourceEvidenceState.Failed => (SemanticStatus.FailedToAssess, null),
        SourceEvidenceState.ConfirmedInSource => (SemanticStatus.Confirmed, "Confirmed in source"),
        SourceEvidenceState.Inferred => (SemanticStatus.Inferred, null),
        SourceEvidenceState.Ambiguous => (SemanticStatus.NeedsReview, "Ambiguous"),
        SourceEvidenceState.NotApplicable => (SemanticStatus.NotApplicable, null),
        _ => (SemanticStatus.NotAssessed, null),
    };

    /// <summary>The states the legend explains, in reading order.</summary>
    public static readonly IReadOnlyList<SourceEvidenceState> Legend =
    [
        SourceEvidenceState.InventoryComplete, SourceEvidenceState.SourceModelAvailable, SourceEvidenceState.Detected, SourceEvidenceState.ClassificationIncomplete,
        SourceEvidenceState.Heuristic, SourceEvidenceState.LimitedBySource, SourceEvidenceState.NeedsReview, SourceEvidenceState.Conflict,
        SourceEvidenceState.NotDetectedInSnapshot, SourceEvidenceState.NotVerifiedAtRuntime, SourceEvidenceState.NotAssessed,
    ];
}

/// <summary>
/// One metric with its definition: what it counts, from where, and whether it counts unique items or occurrences. Null value = not assessed
/// (never shown as 0).
/// </summary>
public sealed record SourceMetric(string Key, string Label, int? Value, string Definition, bool Occurrences = false);

/// <summary>Detected → resolved / limited by source / needs human review, in the area's own unit. The parts never sum across areas.</summary>
public sealed record SourceResolution(string Unit, int Detected, int Resolved, int Limited, int Review);

/// <summary>One cause of limitations, with its count and what it means (grouped so 547 is not read as 547 decisions).</summary>
public sealed record SourceLimitationGroup(string Cause, int Count, string Meaning);

/// <summary>An actionable review item: a decision a person must make (conflicts, confirmations), never a count of analyzer limits.</summary>
public sealed record SourceReviewItem(string Key, string Area, int Count, string Text, string Href);

/// <summary>Evidence of this kind in another imported package of the same application (Project Evidence), linked, never merged.</summary>
public sealed record SourceRelatedPackage(string Name, Guid? SnapshotId, string Href);

public sealed record SourceAreaSummary(
    string Key, string Title, string Icon, SourceEvidenceState State, string StateNote, IReadOnlyList<SourceMetric> Metrics,
    SourceResolution? Resolution, IReadOnlyList<SourceLimitationGroup> Limitations, int ReviewCount, string Href, string ActionLabel,
    IReadOnlyList<SourceRelatedPackage> RelatedPackages)
{
    public int LimitationCount => Limitations.Sum(l => l.Count);
}

/// <summary>Coverage of the snapshot as four separate dimensions (source, parsing, analyzer capability, runtime scope) — no single percentage.</summary>
public sealed record SourceCoverageDimension(string Key, string Label, SourceEvidenceState State, string Text);

public sealed record SourceKpi(string Key, string Label, string Value, string Detail);

/// <summary>
/// Source Analysis Overview: per-area cards, KPIs, coverage dimensions, actionable review items and cross-package hints, from the stored
/// snapshot only (the same counts the area pages show). Pure.
/// </summary>
public static class SourceAnalysisDashboard
{
    private static string P(int n, string one, string many) => SourceAnalysisReview.Plural(n, one, many);
    /// <summary>A quality domain opens by its own name (<see cref="SourceAnalysisAreas.Resolve"/> maps it to Quality Evidence).</summary>
    private static string Href(string area, string? view = null) => $"source-analysis?area={Uri.EscapeDataString(view ?? area)}";

    /// <summary>The page area (and Quality Evidence domain) a card or review item opens.</summary>
    public static (string Area, string? Domain) Target(string key) => key switch
    {
        "architecture" => (SourceAnalysisAreas.Architecture, null),
        "data" => (SourceAnalysisAreas.Data, null),
        "observability" => (SourceAnalysisAreas.Quality, "Observability"),
        "configuration" => (SourceAnalysisAreas.Quality, "Configuration"),
        "cicd" => (SourceAnalysisAreas.Quality, "CI/CD"),
        "contracts" => (SourceAnalysisAreas.Quality, "Contracts"),
        "dependencies" => (SourceAnalysisAreas.Quality, "Dependencies"),
        "tests" => (SourceAnalysisAreas.Quality, "Tests"),
        _ => (SourceAnalysisAreas.Quality, "Infrastructure"),
    };

    public static IReadOnlyList<SourceAreaSummary> Areas(IqrSourceSnapshot s, IReadOnlyList<SourceRelatedPackage>? infrastructureElsewhere = null) =>
        [Architecture(s), Data(s), Observability(s), Configuration(s), CiCd(s), Contracts(s), Dependencies(s), Tests(s), Infrastructure(s, infrastructureElsewhere ?? [])];

    public static SourceAreaSummary Architecture(IqrSourceSnapshot s)
    {
        var href = Href(SourceAnalysisAreas.Architecture);
        if (s.Architecture is not { } a || a.Status == ArchitectureStatus.Unsupported)
            return NotAssessed("architecture", "Architecture", "◇", href, s.Architecture is null
                ? "This snapshot predates architecture extraction."
                : "No supported project type was found, so no source model was built (counts are not assessed, not zero).");
        var o = ArchitecturePresentation.Overview(a);
        var dep = ArchitectureEvidenceSummary.Dependencies(a);
        var linked = dep.Confirmed + dep.StronglySupported + dep.Inferred;
        var conflicts = ArchitectureReview.Conflicts(a);
        var unresolved = a.Dependencies.Select(ArchitectureReview.ReasonCategory).OfType<string>().Where(r => r != ArchitectureReview.ReasonConflict).ToList();
        return new("architecture", "Architecture", "◇", conflicts > 0 ? SourceEvidenceState.Conflict : SourceEvidenceState.SourceModelAvailable,
            conflicts > 0 ? $"Source model available · {P(conflicts, "deterministic conflict", "deterministic conflicts")} to review" : "Source model available · deployment not verified",
            [
                new("components", "Components", o.Components, "Deployable projects detected in source (APIs, workers, adapters, frontends). Unique."),
                new("channels", "Messaging channels", o.Channels, "Queues, topics and event hubs named in source. Unique."),
                new("datastores", "Datastores", o.DataStores, "Databases and storage the components reference. Unique."),
                new("external", "External systems", o.ExternalSystems, "Systems outside this source that components call. Unique."),
                new("linked", "Linked dependencies", linked, "Component relationships whose target was confirmed, strongly supported or inferred from source."),
                new("unresolved-targets", "Unresolved targets", dep.Unresolved, "Relationships detected whose target component source does not state (a limitation)."),
                new("unresolved-evidence", "Unresolved evidence items", dep.TargetNotIdentified, "Extractor items left unresolved (targets, producers, consumers, channels). Overlaps unresolved targets; not decisions.", Occurrences: true),
            ],
            new("runtime dependencies", linked + dep.Unresolved + dep.Conflict, linked, dep.Unresolved, conflicts),
            [.. unresolved.GroupBy(r => r).OrderByDescending(g => g.Count()).Select(g => new SourceLimitationGroup(g.Key, g.Count(), ReasonMeaning(g.Key)))],
            conflicts, href, "Open architecture", []);
    }

    private static string ReasonMeaning(string reason) => reason switch
    {
        ArchitectureReview.ReasonBaseUrl => "The HTTP/GraphQL base address is configured outside this source.",
        ArchitectureReview.ReasonChannel => "The queue/topic name comes from configuration not present in source.",
        ArchitectureReview.ReasonLocalOrchestration => "Only local orchestration (Aspire/launch settings) states it.",
        ArchitectureReview.ReasonCompose => "Only container compose files state it.",
        ArchitectureReview.ReasonExternal => "A package or project reference to something outside this source.",
        _ => "No component in this source matches the target.",
    };

    public static SourceAreaSummary Data(IqrSourceSnapshot s)
    {
        var href = Href(SourceAnalysisAreas.Data);
        if (s.DatabaseArchitecture is not { } d || d.Status == BirkNext.DatabaseArchitecture.DatabaseAnalysisStatus.Unsupported)
            return new("data", "Data model", "▤", SourceEvidenceState.NotDetectedInSnapshot, "No supported database declaration was found in this snapshot.", [], null, [], 0, href, "Open data", []);
        var tables = d.Databases.SelectMany(x => x.Schemas.SelectMany(sc => sc.Tables)).ToList();
        var relationships = tables.Sum(t => t.Relationships.Count);
        return new("data", "Data model", "▤", d.Conflicts.Count > 0 ? SourceEvidenceState.Conflict : SourceEvidenceState.SourceModelAvailable,
            "Data model — available from source · physical database identity not verified from source",
            [
                new("databases", "Database candidates", d.Databases.Count, "Database contexts/connections declared in source. Candidates: the physical database is not verified."),
                new("schemas", "Schemas", d.Databases.Sum(x => x.Schemas.Count), "Schemas across the database candidates."),
                new("entities", "Tables / entities", tables.Count, "Entities mapped to tables in source. Unique per schema."),
                new("relationships", "Relationships", relationships, "Foreign keys and navigations declared in source."),
                new("indexes", "Indexes", tables.Sum(t => t.Indexes.Count), "Indexes declared in source."),
            ],
            new("declarations", tables.Count + d.UnresolvedEvidence.Count, tables.Count, d.UnresolvedEvidence.Count, d.Conflicts.Count),
            d.UnresolvedEvidence.Count > 0 ? [new("Declarations not fully resolved", d.UnresolvedEvidence.Count, "Source declarations the reader could not map completely (e.g. dynamic configuration); not schema defects.")] : [],
            d.Conflicts.Count, href, "Open data", []);
    }

    public static SourceAreaSummary Observability(IqrSourceSnapshot s)
    {
        var href = Href(SourceAnalysisAreas.Quality, "Observability");
        if (s.Observability is not { } o) return NotAssessed("observability", "Observability", "◎", href, "This snapshot predates observability analysis.");
        if (o.Status == ArchitectureStatus.Unsupported)
            return NotAssessed("observability", "Observability", "◎", href, "No supported project type was found, so observability was not analyzed (counts are not assessed, not zero).");
        var review = SourceObservabilityPresentation.NeedsReview(o);
        return new("observability", "Observability", "◎", review > 0 ? SourceEvidenceState.NeedsReview : SourceEvidenceState.Detected,
            "Logging, tracing and correlation as written in source · runtime telemetry not verified",
            [
                new("log-calls", "Log calls", SourceObservabilityPresentation.LogCalls(o), "Logging statements in source.", Occurrences: true),
                new("boundaries", "Boundaries", o.Boundaries.Count, "Request/message boundaries where correlation should propagate."),
                new("inferred", "Propagation inferred", SourceObservabilityPresentation.Inferred(o), "Boundaries where propagation is inferred, not explicit."),
                new("findings", "Findings to review", review, "Analyzer findings at Needs review severity, counted by occurrence.", Occurrences: true),
            ],
            null, o.Limitations.Count > 0 ? [new("Analyzer limitations", o.Limitations.Count, "Constructs the observability reader does not interpret.")] : [],
            review, href, "Open observability", []);
    }

    public static SourceAreaSummary Configuration(IqrSourceSnapshot s)
    {
        var href = Href(SourceAnalysisAreas.Quality, "Configuration");
        if (s.EvidenceDomains is not { } e) return NotAssessed("configuration", "Configuration", "⚙", href, PredatesDomains);
        var c = e.Configuration;
        if (!SourceEvidenceDomainsPresentation.Assessed(c) || c.Entries.Count == 0)
            return new("configuration", "Configuration", "⚙", SourceEvidenceState.NotDetectedInSnapshot, "No configuration files were found in this snapshot.", [], null, [], 0, href, "Open configuration", []);
        var unknown = SourceAnalysisReview.Unknown(c);
        var sensitive = c.Entries.Count(x => x.Sensitivity != ConfigurationSensitivity.None);
        var conflicts = c.Conflicts.Count;
        return new("configuration", "Configuration", "⚙", unknown > 0 ? SourceEvidenceState.ClassificationIncomplete : SourceEvidenceState.InventoryComplete,
            "Source-declared environment labels — not runtime verified · values stay redacted",
            [
                new("entries", "Entries", c.Entries.Count, "Configuration keys read from source files, per file and environment.", Occurrences: true),
                new("classified", "Classified", c.Entries.Count - unknown, "Entries placed in a category (connection, endpoint, feature flag, …)."),
                new("unclassified", "Unclassified", unknown, "Entries the analyzer could not categorize: a classification gap, not decisions to make."),
                new("files", "Files", c.Files.Count, "Configuration files read."),
                new("sensitive", "Sensitive references", sensitive, "Entries that look like secrets or credentials. Values are never shown."),
            ],
            new("entries", c.Entries.Count, c.Entries.Count - unknown, unknown, conflicts),
            UnclassifiedGroups(c),
            conflicts, href, "Open configuration", []);
    }

    /// <summary>Unclassified entries by technology: the five largest, the rest as one group — every entry counted exactly once.</summary>
    private static IReadOnlyList<SourceLimitationGroup> UnclassifiedGroups(ConfigurationEvidence c)
    {
        var groups = c.Entries.Where(x => x.Category == ConfigurationCategory.Unknown).GroupBy(x => string.IsNullOrWhiteSpace(x.Technology) ? "Other" : x.Technology)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
        var shown = groups.Take(5).Select(g => new SourceLimitationGroup($"Unclassified · {g.Key}", g.Count(), "Keys of this technology without a known category.")).ToList();
        if (groups.Count > 5) shown.Add(new($"Unclassified · {groups.Count - 5} other technologies", groups.Skip(5).Sum(g => g.Count()), "Keys of smaller technology groups without a known category."));
        return shown;
    }

    public static SourceAreaSummary CiCd(IqrSourceSnapshot s)
    {
        var href = Href(SourceAnalysisAreas.Quality, "CI/CD");
        if (s.EvidenceDomains is not { } e) return NotAssessed("cicd", "CI/CD", "▶", href, PredatesDomains);
        var p = e.CiCd;
        var runnable = p.Pipelines.Where(x => !x.IsTemplate).ToList();
        if (p.Pipelines.Count == 0)
            return new("cicd", "CI/CD", "▶", SourceEvidenceState.NotDetectedInSnapshot, "No pipeline definitions were found in this snapshot.", [], null, [], 0, href, "Open CI/CD", []);
        var steps = p.Pipelines.SelectMany(x => x.Steps).ToList();
        var other = steps.Count(x => x.Kind == PipelineStepKind.Other);
        return new("cicd", "CI/CD", "▶", SourceEvidenceState.Detected, "Inventory — detected · templates, expressions and conditions are not evaluated, and nothing here says a pipeline ran",
            [
                new("pipelines", "Pipelines", runnable.Count, "Runnable pipeline definitions (templates counted separately)."),
                new("build", "Build steps", steps.Count(x => x.Kind is PipelineStepKind.Build or PipelineStepKind.Restore or PipelineStepKind.ContainerBuild), "Restore, build and container build steps.", Occurrences: true),
                new("test", "Test steps", steps.Count(x => SourceEvidenceDomainsPresentation.IsTest(x.Kind)), "Steps that run tests (configured, not executed results).", Occurrences: true),
                new("deploy", "Deployment stages", p.Pipelines.Sum(x => x.Stages.Count(st => st.Deployment)), "Stages marked as deployments.", Occurrences: true),
                new("templates", "Templates", p.Pipelines.Count(x => x.IsTemplate), "Reusable templates referenced by pipelines."),
                new("unclassified", "Unclassified steps", other, "Setup and other steps BirkNext does not categorize — not failures, not decisions.", Occurrences: true),
            ],
            new("steps", steps.Count, steps.Count - other, other, 0),
            other > 0 ? [new("Steps not categorized", other, "Script and setup steps without a known kind.")] : [],
            0, href, "Open CI/CD", []);
    }

    public static SourceAreaSummary Contracts(IqrSourceSnapshot s)
    {
        var href = Href(SourceAnalysisAreas.Quality, "Contracts");
        if (s.EvidenceDomains is not { } e) return NotAssessed("contracts", "Contracts", "⇄", href, PredatesDomains);
        var k = e.Contracts;
        if (k.Contracts.Count == 0)
            return new("contracts", "Contracts", "⇄", SourceEvidenceState.NotDetectedInSnapshot, "No contracts (OpenAPI, GraphQL, XSD, message types) were found in this snapshot.", [], null, [], 0, href, "Open contracts", []);
        var summary = BirkNext.ContractSchemaEvidence.ContractEvidenceQueries.Summary(new BirkNext.ContractSchemaEvidence.ContractEvidenceSource(e.SourceSnapshotId, "", e.ExtractedAt, k));
        var heuristic = SourceAnalysisReview.Heuristic(k);
        // Generated clients are code generated from a contract, not contracts: listed apart and never in the contract counts.
        var generated = k.Contracts.Count(c => c.Type == SourceContractType.GeneratedClient);
        var types = string.Join(" · ", k.Contracts.Where(c => c.Type != SourceContractType.GeneratedClient).GroupBy(c => c.Type).OrderByDescending(g => g.Count())
            .Select(g => $"{SourceDomainText.Label(g.Key)} {g.Count()}")) + (generated > 0 ? $" · plus {P(generated, "generated client", "generated clients")}" : "");
        return new("contracts", "Contracts", "⇄", summary.Failed > 0 ? SourceEvidenceState.Failed : heuristic > 0 ? SourceEvidenceState.Heuristic : SourceEvidenceState.ConfirmedInSource,
            $"{types} · parse approach (structured/heuristic) is not a result, and heuristic is not a defect",
            [
                new("contracts", "Contracts", summary.Total, "Contract documents and message types detected (generated clients excluded), plus files that failed to parse. Unique."),
                new("structured", "Structured parse", k.Contracts.Count - heuristic, "Contract files read with a structured parser (approach). Counts every contract file, generated clients included."),
                new("heuristic", "Heuristic parse", heuristic, "Contract files read with pattern-based parsing (approach, not a defect). Counts every contract file, generated clients included."),
                new("partial", "Partial result", summary.Partial, "Parsed, but some references or parts are unresolved (result)."),
                new("failed", "Failed", summary.Failed, "Could not be parsed at all (result)."),
                new("confirm", "Needing confirmation", summary.NeedsReview, "Partial results, unresolved references or several candidate entry points: a person confirms."),
            ],
            new("contracts", summary.Total, summary.Total - summary.NeedsReview - summary.Failed, summary.Failed, summary.NeedsReview),
            // Partial results are the confirmations above (one count, never also a limitation); only unparseable contracts are limitations.
            summary.Failed > 0 ? [new("Could not be parsed", summary.Failed, "The contract file was found but no reader could parse it.")] : [],
            summary.NeedsReview, href, "Open contracts", []);
    }

    public static SourceAreaSummary Dependencies(IqrSourceSnapshot s)
    {
        var href = Href(SourceAnalysisAreas.Quality, "Dependencies");
        if (s.DependencyEvidence is not { } d) return NotAssessed("dependencies", "Dependencies", "⧉", href, "This snapshot predates dependency evidence.");
        var packages = d.Dependencies.Select(x => (BirkNext.Dependencies.DependencyCoordinate.EcosystemOf(x.Manager), x.PackageName)).Distinct().Count();
        return new("dependencies", "Dependencies", "⧉", d.UnsupportedManifests.Count > 0 ? SourceEvidenceState.LimitedBySource : SourceEvidenceState.InventoryComplete,
            "Dependency inventory — declared in source; resolved versions and runtime loading are other evidence",
            [
                new("packages", "Declared packages", packages, "Distinct packages per ecosystem declared in manifests."),
                new("managers", "Package managers", d.Managers.Count, "Package managers whose manifests were read."),
                new("produced", "Internal packages produced", d.PublishedPackages.Count, "Packages this source publishes."),
                new("unsupported", "Manifests not read natively", d.UnsupportedManifests.Count, "Manifests of managers without a native reader."),
            ],
            null, d.UnsupportedManifests.Count > 0 ? [new("Manifests not read natively", d.UnsupportedManifests.Count, "Their dependencies are not in the inventory.")] : [],
            0, href, "Open dependencies", []);
    }

    public static SourceAreaSummary Tests(IqrSourceSnapshot s)
    {
        var href = Href(SourceAnalysisAreas.Quality, "Tests");
        if (s.TestInventory is not { } inventory) return NotAssessed("tests", "Tests", "✓", href, "This snapshot predates source test discovery.");
        var tests = TestEvidenceQueries.FromInventory(inventory);
        var explicitLinked = tests.Count(t => t.Requirements.Any(r => r.Confidence is TestMappingConfidence.Explicit or TestMappingConfidence.Strong));
        var candidateOnly = tests.Count(t => t.Requirements.Count > 0 && t.Requirements.All(r => r.Confidence is TestMappingConfidence.Candidate or TestMappingConfidence.Inferred));
        return new("tests", "Tests", "✓", tests.Count == 0 ? SourceEvidenceState.NotDetectedInSnapshot : SourceEvidenceState.InventoryComplete,
            "Test inventory — discovered in source; not execution results and not coverage",
            [
                new("tests", "Tests discovered", tests.Count, "Test methods discovered in source."),
                new("projects", "Test projects", inventory.Projects.Count, "Projects containing tests."),
                new("explicit", "Explicit requirement links", explicitLinked, "Tests linked to a requirement by an explicit or strong reference."),
                new("candidates", "Candidate links only", candidateOnly, "Tests with only candidate/inferred links — not confirmed."),
                new("skipped", "Skip declared", tests.Count(t => t.SkipDeclared), "Tests marked as skipped in source."),
            ],
            null, [], 0, href, "Open tests", []);
    }

    public static SourceAreaSummary Infrastructure(IqrSourceSnapshot s, IReadOnlyList<SourceRelatedPackage> elsewhere)
    {
        var href = Href(SourceAnalysisAreas.Quality, "Infrastructure");
        if (s.EvidenceDomains is not { } e) return NotAssessed("infrastructure", "Infrastructure", "▦", href, PredatesDomains);
        var i = e.Infrastructure;
        var resources = i.Resources.Where(r => r.Kind == "resource").ToList();
        if (resources.Count == 0)
            return new("infrastructure", "Infrastructure", "▦", SourceEvidenceState.NotDetectedInSnapshot,
                elsewhere.Count > 0 ? $"Not detected in this snapshot · infrastructure evidence available in {P(elsewhere.Count, "associated package", "associated packages")}"
                    : "Not detected in this snapshot · it may exist in another source package or repository",
                [], null, [], 0, href, "Open infrastructure", elsewhere);
        return new("infrastructure", "Infrastructure", "▦", i.UnresolvedReferences.Count > 0 ? SourceEvidenceState.LimitedBySource : SourceEvidenceState.Detected,
            "Declared in infrastructure as code · deployed state not verified",
            [
                new("resources", "Resources", resources.Count, "Resources declared in IaC."),
                new("types", "Resource types", resources.Select(r => r.ResourceType).Distinct().Count(), "Distinct resource types."),
                new("bindings", "Bindings", i.Dependencies.Count, "References between resources."),
                new("environments", "Environment references", i.Environments.Count, "Environments named in IaC (source-declared)."),
                new("unresolved", "Unresolved references", i.UnresolvedReferences.Count, "References whose target is not declared in this source (limitation)."),
            ],
            null, i.UnresolvedReferences.Count > 0 ? [new("Unresolved references", i.UnresolvedReferences.Count, "Targets declared elsewhere (modules, other repositories).")] : [],
            0, href, "Open infrastructure", elsewhere);
    }

    private const string PredatesDomains = "This snapshot predates the source-evidence domains; analyze the archive again to capture them.";

    private static SourceAreaSummary NotAssessed(string key, string title, string icon, string href, string note) =>
        new(key, title, icon, SourceEvidenceState.NotAssessed, note, [], null, [], 0, href, $"Open {title.ToLowerInvariant()}", []);

    // ── Header: KPIs, coverage, review ───────────────────────────────────────────────────────────

    public static IReadOnlyList<SourceKpi> Kpis(IqrSourceSnapshot s, IReadOnlyList<SourceAreaSummary> areas)
    {
        var coverage = SourceAnalysisReview.Coverage(s);
        var withModel = areas.Count(a => a.State is not (SourceEvidenceState.NotAssessed or SourceEvidenceState.NotDetectedInSnapshot));
        var review = areas.Where(a => a.ReviewCount > 0).ToList();
        return
        [
            new("files", "Files in snapshot", s.Archive.FilesAnalyzed.ToString(), s.TechnologyCoverage is { UnsupportedSourceFiles: > 0 } c ? $"{c.UnsupportedSourceFiles} in languages without an analyzer" : "All source languages have an analyzer"),
            new("areas", "Areas with evidence", $"{withModel} of {areas.Count}", "Not detected or not assessed areas are listed, not hidden"),
            new("review", "Areas needing a decision", review.Count.ToString(), review.Count == 0 ? "No conflict or confirmation is pending" : string.Join(", ", review.Select(a => a.Title))),
            new("limitations", "Analyzer limitations", coverage.Limitations.Count.ToString(), "What analyzers could not interpret — not application defects"),
        ];
    }

    public static IReadOnlyList<SourceCoverageDimension> Coverage(IqrSourceSnapshot s)
    {
        var coverage = SourceAnalysisReview.Coverage(s);
        var unsupported = s.TechnologyCoverage?.UnsupportedSourceFiles ?? 0;
        var heuristic = s.EvidenceDomains is { } e ? SourceAnalysisReview.Heuristic(e.Contracts) : 0;
        var contracts = s.EvidenceDomains?.Contracts.Contracts.Count ?? 0;
        return
        [
            new("source", "Source coverage", unsupported > 0 ? SourceEvidenceState.LimitedBySource : SourceEvidenceState.InventoryComplete,
                unsupported > 0 ? $"{P(unsupported, "source file is", "source files are")} in a language detected but not analyzed" : "Every detected source language has an analyzer"),
            new("parsing", "Parsing", heuristic > 0 ? SourceEvidenceState.Heuristic : SourceEvidenceState.ConfirmedInSource,
                heuristic > 0 ? $"{heuristic} of {P(contracts, "contract", "contracts")} read heuristically (approach, not a defect)" : "Structured parsers read every contract"),
            new("capability", "Analyzer capability", coverage.Limitations.Count > 0 ? SourceEvidenceState.LimitedBySource : SourceEvidenceState.InventoryComplete,
                coverage.Limitations.Count > 0 ? $"{P(coverage.Limitations.Count, "analyzer limitation applies", "analyzer limitations apply")}" : "No analyzer limitation recorded"),
            new("runtime", "Runtime scope", SourceEvidenceState.NotVerifiedAtRuntime, "Source Analysis never connects to running systems; deployment and runtime are other evidence"),
        ];
    }

    /// <summary>Only decisions: conflicts, confirmations and findings at Needs review. Limitations (unresolved, unclassified) are never listed here.</summary>
    public static IReadOnlyList<SourceReviewItem> Review(IReadOnlyList<SourceAreaSummary> areas) =>
        [.. areas.Where(a => a.ReviewCount > 0).Select(a => new SourceReviewItem(a.Key, a.Title, a.ReviewCount, a.Key switch
        {
            "architecture" => P(a.ReviewCount, "deterministic conflict between relationship targets", "deterministic conflicts between relationship targets"),
            "data" => P(a.ReviewCount, "conflicting source declaration", "conflicting source declarations"),
            "configuration" => P(a.ReviewCount, "potential configuration conflict", "potential configuration conflicts"),
            "contracts" => P(a.ReviewCount, "contract needs confirmation", "contracts need confirmation"),
            "observability" => P(a.ReviewCount, "observability finding to review", "observability findings to review"),
            _ => P(a.ReviewCount, "item to review", "items to review"),
        }, a.Href))];

    /// <summary>Packages of the same application whose own snapshot holds infrastructure evidence (Project Evidence capabilities), excluding this one.</summary>
    public static IReadOnlyList<SourceRelatedPackage> InfrastructureElsewhere(IqrSourceSnapshot s, IEnumerable<BirkNext.Web.Services.ProjectEvidence.PackageAssociation> associations) =>
        [.. associations.Where(a => a.State is not BirkNext.Web.Services.ProjectEvidence.EvidenceAssociationState.NotAssociated
                && a.Package.SnapshotId != s.Id && a.Package.Has(BirkNext.Web.Models.ProjectEvidenceCapability.Infrastructure))
            .OrderBy(a => a.Package.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(a => new SourceRelatedPackage(a.Package.DisplayName, a.Package.SnapshotId,
                a.Package.SnapshotId is { } id ? $"source-analysis?snapshot={id}&area=Infrastructure" : "source-analysis"))];
}
