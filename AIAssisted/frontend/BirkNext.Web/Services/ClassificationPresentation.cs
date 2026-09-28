using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>
/// Pure projection of a Security Classification review for the page and export. Source evidence, configuration and runtime observations
/// stay apart; Pass only appears for an executed live check; "No result" and "Not tested" are never shown as Pass.
/// </summary>
public static class ClassificationPresentation
{
    public static string Tone(ClassificationState state) => state switch
    {
        ClassificationState.Pass or ClassificationState.Verified => "ready",
        ClassificationState.SourceVerified or ClassificationState.Configured or ClassificationState.Observed => "info",
        ClassificationState.Fail or ClassificationState.IssueDetected => "fail",
        ClassificationState.Warning or ClassificationState.NeedsDecision or ClassificationState.DocumentedOnly or ClassificationState.NotFound or ClassificationState.Partial => "attention",
        _ => "muted",
    };

    public static string Tone(ClassificationOverall overall) => overall switch
    {
        ClassificationOverall.Verified => "ready",
        ClassificationOverall.IssueDetected => "fail",
        ClassificationOverall.Partial => "attention",
        _ => "muted",
    };

    public static string Tone(ClassificationSeverity severity) => severity switch
    {
        ClassificationSeverity.Critical or ClassificationSeverity.High => "fail",
        ClassificationSeverity.Medium => "attention",
        ClassificationSeverity.Low => "info",
        _ => "muted",
    };

    public static string Tone(CountComparisonState state) => state switch
    {
        CountComparisonState.Match => "info",
        CountComparisonState.Mismatch => "attention",
        _ => "muted",
    };

    public static string Tone(RepositoryTestCoverageState state) => state switch { RepositoryTestCoverageState.Present => "info", RepositoryTestCoverageState.UnitOnly => "attention", _ => "fail" };

    /// <summary>Label with the kind of pass: an unauthorized not-found is anti-disclosure behavior, not a generic Pass.</summary>
    public static string Cell(ClassificationObservation? observation) => observation is null ? "Not tested"
        : observation.State == ClassificationState.Pass && observation.Identity == ClassificationIdentity.Unauthorized && observation.Surface is ClassificationSurface.DirectProfile or ClassificationSurface.NonexistentComparison
            ? "Pass — anti-disclosure" : ClassificationLabels.State(observation.State);

    public static readonly (ClassificationIdentity Identity, ClassificationSurface Surface)[] MatrixColumns =
    [
        (ClassificationIdentity.Unauthorized, ClassificationSurface.DirectProfile), (ClassificationIdentity.Unauthorized, ClassificationSurface.NonexistentComparison),
        (ClassificationIdentity.Unauthorized, ClassificationSurface.Search), (ClassificationIdentity.Unauthorized, ClassificationSurface.SearchTotalCount),
        (ClassificationIdentity.Unauthorized, ClassificationSurface.AuditLog),
        (ClassificationIdentity.Authorized, ClassificationSurface.DirectProfile), (ClassificationIdentity.Authorized, ClassificationSurface.Search), (ClassificationIdentity.Authorized, ClassificationSurface.AuditLog),
    ];

    public static ClassificationObservation? Observation(ClassificationReviewResult result, int level, ClassificationIdentity identity, ClassificationSurface surface) =>
        result.Live.Observations.FirstOrDefault(o => o.Nivaa == level && o.Identity == identity && o.Surface == surface);

    public static string LevelName(ClassificationLevel level) =>
        $"Level {level.Nivaa} — {level.Verdi}" + (level.BiRKKode is not null ? $" / {level.BiRKKode}" : "") + (level.ElementsKode is not null ? $" / {level.ElementsKode}" : "");

    public static string Headline(ClassificationReviewResult result)
    {
        var high = result.Findings.Count(f => f.Severity is ClassificationSeverity.Critical or ClassificationSeverity.High);
        return $"{ClassificationLabels.Overall(result.Overall)} · {result.Live.Observations.Count(o => ClassificationLabels.IsRuntimeResult(o.State))} live observation(s) · {result.Findings.Count} finding(s){(high > 0 ? $", {high} high or critical" : "")}";
    }

    public static readonly ClassificationTestType[] TestTypes = Enum.GetValues<ClassificationTestType>();

    public static string Location(SourceLocation location)
    {
        var parts = location.File.Split('/');
        return $"{string.Join('/', parts.Skip(Math.Max(0, parts.Length - 2)))}:{location.Line}";
    }

    public static string Locations(IEnumerable<SourceLocation> locations) => string.Join(", ", locations.Take(3).Select(Location));

    /// <summary>Mirrors the backend live gate for the pre-run message (the backend decides).</summary>
    public static (bool CanRun, string Reason) LiveReadiness(ClassificationTestContext context, string environmentType, bool hasToken)
    {
        if (environmentType == "Production") return (false, "Production is never tested.");
        var env = context.Environment?.ToUpperInvariant();
        if (env is not ("DEV" or "QA")) return (false, "No approved DEV/QA test context is configured — the review runs source and configuration checks only.");
        var allowed = env == "DEV" ? new[] { "Local", "Development" } : ["QA", "Test"];
        if (!allowed.Contains(environmentType)) return (false, $"The test context is for {env}, but the active Target Environment is {environmentType}.");
        if (!context.ApprovedByTestLead) return (false, "The test context is not approved by the test lead.");
        if (string.IsNullOrWhiteSpace(context.GraphQlEndpoint) || context.TestChildren.Count == 0) return (false, "The test context needs a GraphQL endpoint and at least one synthetic test child.");
        if (!hasToken) return (false, "Enter at least one test identity token for this run (never stored).");
        return (true, "Safe GraphQL queries for the configured test children only.");
    }
}
