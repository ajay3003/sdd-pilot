using System.Text.Json.Serialization;

namespace BirkNext.Applicability;

// ── Shared applicability, execution and scoring semantics ──────────────────────────────────────────────────────────────
// One vocabulary every review, the dashboard and navigation use, so a technology BirkNext does not understand stays neutral:
//   Applicability (does this review apply to this project, and can BirkNext assess it?)  ≠  ExecutionState (did it run?)
//   ≠  CheckOutcome (what a check found).  Tool coverage ≠ project quality.
// Unsupported, NotApplicable, Unavailable and NotAssessed never count as failure and are never converted to 0: a quality score's
// denominator holds only checks that were applicable and actually assessed; how much was assessed is reported separately as coverage.

/// <summary>What a project or component provides — never a vendor. Technologies (Kafka, ASP.NET, App Insights) are evidence for a capability.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Capability
{
    SourceCode, BackendApplication, FrontendApplication, BrowserTarget, RestApi, GraphQlApi, SoapApi, GrpcApi, RelationalDatabase, DocumentDatabase,
    Messaging, EventStreaming, FileIntegration, InfrastructureAsCode, Pipeline, Authentication, Authorization, Observability, Containerized, Kubernetes,
    CloudHosted, PackageInventory, ApiTarget, Requirements, Documentation, IntegrationCatalog, DomainExtension,
}

/// <summary>How sure a capability or technology detection is. A package being present is not a confirmed framework architecture.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DetectionConfidence { Confirmed, StronglySupported, Inferred, Unresolved }

/// <summary>One capability with the evidence it was detected from, scoped to a component when known.</summary>
public sealed record CapabilityEvidence(Capability Capability, DetectionConfidence Confidence, string Basis, string? Technology = null, string? Component = null,
    string? ProviderId = null);

/// <summary>Does a review apply, and can BirkNext assess it? Never Failed: a review that cannot run is not a quality result.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApplicabilityStatus
{
    /// <summary>The project has what the review needs and BirkNext has the provider/analyzer.</summary>
    Applicable,
    /// <summary>Some required capabilities or analyzers are present; the review covers part of the project.</summary>
    PartiallyApplicable,
    /// <summary>The project does not have what the review is about (no browser frontend → no frontend review). Neutral.</summary>
    NotApplicable,
    /// <summary>The project has it, but BirkNext has no analyzer/provider for its technology. Neutral — a tool limitation.</summary>
    Unsupported,
    /// <summary>The review may apply but the evidence to decide is not there yet (no source snapshot, no target). Neutral.</summary>
    NotEnoughEvidence,
    /// <summary>The review applies but needs configuration first (a target, credentials, a selected domain extension).</summary>
    NeedsConfiguration,
}

/// <summary>Whether a review ran. Separate from what it found: FailedToExecute (an analyzer threw) is never a project-quality failure.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReviewExecutionState { NotStarted, Blocked, Completed, PartiallyCompleted, FailedToExecute }

/// <summary>The outcome of one check. Only Pass/Fail/Warning/NeedsReview are assessed outcomes; the rest are neutral coverage states.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CheckOutcome { Pass, Fail, Warning, NeedsReview, Partial, NotTested, NotAssessed, Unavailable, Unsupported, NotApplicable }

public sealed record ReviewApplicability
{
    public ApplicabilityStatus Status { get; init; }
    public string Reason { get; init; } = "";
    public List<Capability> RequiredCapabilities { get; init; } = [];
    public List<Capability> AvailableCapabilities { get; init; } = [];
    public List<Capability> MissingCapabilities { get; init; } = [];
    public List<string> Evidence { get; init; } = [];
    /// <summary>What a person can do (configure a target, analyze source, select the domain extension).</summary>
    public string? Action { get; init; }
}

/// <summary>How much of a review's check set was assessed — evidence coverage, not quality.</summary>
public sealed record ReviewCoverage
{
    public int TotalChecks { get; init; }
    public int ApplicableChecks { get; init; }
    public int ExecutedChecks { get; init; }
    public int UnsupportedChecks { get; init; }
    public int NotApplicableChecks { get; init; }
    public int NotAssessedChecks { get; init; }
    /// <summary>Executed share of applicable checks, 0–100; null when nothing is applicable.</summary>
    public double? AssessmentCoveragePercent => ApplicableChecks == 0 ? null : Math.Round(100.0 * ExecutedChecks / ApplicableChecks, 1);
}

/// <summary>A quality result over assessed checks only, with its denominator stated.</summary>
public sealed record QualityResult(double? QualityPercent, int Denominator, int Passed, int Failed, int Warnings, ReviewCoverage Coverage, string Basis);

/// <summary>The standard envelope a review result can expose: applicability, execution, coverage, quality (optional), limitations.</summary>
public sealed record ReviewExecutionResult
{
    public string ReviewId { get; init; } = "";
    public ReviewApplicability Applicability { get; init; } = new();
    public ReviewExecutionState ExecutionState { get; init; }
    public ReviewCoverage Coverage { get; init; } = new();
    public QualityResult? Quality { get; init; }
    public List<string> Limitations { get; init; } = [];
}

public static class ScoreSemantics
{
    /// <summary>Outcomes that are a judgement about the project (they belong in a quality denominator).</summary>
    public static bool IsAssessed(CheckOutcome o) => o is CheckOutcome.Pass or CheckOutcome.Fail or CheckOutcome.Warning or CheckOutcome.NeedsReview or CheckOutcome.Partial;

    /// <summary>Outcomes that say something about tool/evidence coverage only. Never failure, never 0.</summary>
    public static bool IsNeutral(CheckOutcome o) => !IsAssessed(o);

    public static bool IsFailure(CheckOutcome o) => o == CheckOutcome.Fail;

    /// <summary>
    /// Quality over assessed checks only. Unsupported and NotApplicable checks are not applicable to the score; NotTested, NotAssessed and
    /// Unavailable are applicable but not executed — they lower assessment coverage, never quality. Warning and NeedsReview count half;
    /// Partial counts half. Example: 10 checks, 4 Unsupported, 2 NotApplicable, 4 executed → denominator 4.
    /// </summary>
    public static QualityResult Compute(IEnumerable<CheckOutcome> outcomes)
    {
        var list = outcomes.ToList();
        var coverage = Coverage(list);
        var assessed = list.Where(IsAssessed).ToList();
        if (assessed.Count == 0)
            return new QualityResult(null, 0, 0, 0, 0, coverage, list.Count == 0 ? "No checks." : "No check was assessed; quality is not computed (coverage only).");
        var passed = assessed.Count(o => o == CheckOutcome.Pass);
        var failed = assessed.Count(o => o == CheckOutcome.Fail);
        var warnings = assessed.Count(o => o is CheckOutcome.Warning or CheckOutcome.NeedsReview or CheckOutcome.Partial);
        var points = passed + warnings * 0.5;
        return new QualityResult(Math.Round(100.0 * points / assessed.Count, 1), assessed.Count, passed, failed, warnings, coverage,
            $"Quality among {assessed.Count} assessed check(s); {coverage.UnsupportedChecks} unsupported and {coverage.NotApplicableChecks} not applicable excluded; {coverage.NotAssessedChecks} not assessed (coverage, not quality).");
    }

    public static ReviewCoverage Coverage(IReadOnlyCollection<CheckOutcome> outcomes)
    {
        var unsupported = outcomes.Count(o => o == CheckOutcome.Unsupported);
        var notApplicable = outcomes.Count(o => o == CheckOutcome.NotApplicable);
        var notAssessed = outcomes.Count(o => o is CheckOutcome.NotTested or CheckOutcome.NotAssessed or CheckOutcome.Unavailable);
        return new ReviewCoverage
        {
            TotalChecks = outcomes.Count, ApplicableChecks = outcomes.Count - unsupported - notApplicable, ExecutedChecks = outcomes.Count(IsAssessed),
            UnsupportedChecks = unsupported, NotApplicableChecks = notApplicable, NotAssessedChecks = notAssessed,
        };
    }

    /// <summary>Applicability statuses that keep a review out of every quality aggregate (dashboard, release readiness).</summary>
    public static bool ExcludedFromQuality(ApplicabilityStatus s) => s is not (ApplicabilityStatus.Applicable or ApplicabilityStatus.PartiallyApplicable);

    public static string Label(ApplicabilityStatus s) => s switch
    {
        ApplicabilityStatus.Applicable => "Applicable",
        ApplicabilityStatus.PartiallyApplicable => "Partial coverage",
        ApplicabilityStatus.NotApplicable => "Not applicable",
        ApplicabilityStatus.Unsupported => "Unsupported technology",
        ApplicabilityStatus.NotEnoughEvidence => "Not enough evidence",
        _ => "Needs configuration",
    };

    public static string Label(CheckOutcome o) => o switch
    {
        CheckOutcome.NeedsReview => "Needs review",
        CheckOutcome.NotTested => "Not tested",
        CheckOutcome.NotAssessed => "Not assessed",
        CheckOutcome.NotApplicable => "Not applicable",
        _ => o.ToString(),
    };

    public static string Label(ReviewExecutionState s) => s switch
    {
        ReviewExecutionState.NotStarted => "Not started",
        ReviewExecutionState.PartiallyCompleted => "Partially completed",
        ReviewExecutionState.FailedToExecute => "Failed to execute (tool error — not a project-quality result)",
        _ => s.ToString(),
    };
}
