using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>User-facing grouping of the lifecycle evidence observations produced by <see cref="SddEvidenceGraphService.QualityFindings(IWorkspaceSessionService, LifecycleProjectionMode, Guid?)"/>.</summary>
public enum SddObservationCategory { ImplementationEvidence, TestEvidence, Traceability, Clarifications, Other }

/// <summary>How an observation is styled. NotAssessed and Information are neutral; only an actual failed result is <see cref="Failed"/>.</summary>
public enum SddObservationTone { Neutral, NeedsReview, Failed }

public sealed record SddObservationRequirement(string RequirementId, IReadOnlyList<SddQualityReviewFinding> Observations);

/// <summary>All observations sharing one rule code, grouped by unique requirement.</summary>
public sealed record SddObservationGroup(
    string Code,
    SddObservationCategory Category,
    string Title,
    string StatusLabel,
    SddObservationTone Tone,
    string Explanation,
    IReadOnlyList<SddObservationRequirement> Requirements,
    int ObservationCount)
{
    public int RequirementCount => Requirements.Count;
}

public sealed record SddObservationCategorySummary(SddObservationCategory Category, string Title, IReadOnlyList<SddObservationGroup> Groups)
{
    /// <summary>Unique requirements with at least one observation in this category; several observations on one requirement count once.</summary>
    public int RequirementCount => Groups.SelectMany(g => g.Requirements).Select(r => r.RequirementId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
}

/// <summary>
/// Aggregate of the shared SDD lifecycle evidence shown before a Quality Review run. It is a projection of the same graph
/// Requirements Traceability uses: it counts unique requirements, never invents a score, and keeps policy significance
/// separate from the observation status.
/// </summary>
public sealed record SharedSddEvidenceSummary(
    int RequirementsEvaluated,
    int ObservationCount,
    IReadOnlyList<SddObservationCategorySummary> Categories,
    string PolicySignificance,
    string PolicySignificanceExplanation)
{
    public bool HasObservations => ObservationCount > 0;

    /// <summary>No project policy model assigns release significance to lifecycle observations today, so they are informational.</summary>
    public const string InformationalPolicy = "Informational";

    public static SharedSddEvidenceSummary Build(IReadOnlyList<SddRequirementGraphRow> rows, IReadOnlyList<SddQualityReviewFinding> observations)
    {
        var requirementOrder = rows.Select(r => r.Requirement.Id).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index, StringComparer.OrdinalIgnoreCase);
        int Order(string id) => requirementOrder.TryGetValue(id, out var index) ? index : int.MaxValue;

        var groups = observations
            .GroupBy(o => o.Code, StringComparer.Ordinal)
            .Select(g =>
            {
                var label = Describe(g.Key, g.First().Severity);
                var requirements = g.GroupBy(o => o.RequirementId, StringComparer.OrdinalIgnoreCase)
                    .Select(r => new SddObservationRequirement(r.First().RequirementId, r.ToList()))
                    .OrderBy(r => Order(r.RequirementId)).ThenBy(r => r.RequirementId, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return new SddObservationGroup(g.Key, label.Category, label.Title, label.Status, label.Tone, label.Explanation, requirements, g.Count());
            })
            .ToList();

        var categories = Enum.GetValues<SddObservationCategory>()
            .Select(c => new SddObservationCategorySummary(c, CategoryTitle(c), groups.Where(g => g.Category == c)
                .OrderBy(g => g.Tone == SddObservationTone.Neutral ? 1 : 0).ThenByDescending(g => g.RequirementCount).ToList()))
            .Where(c => c.Groups.Count > 0)
            .ToList();

        return new SharedSddEvidenceSummary(
            requirementOrder.Count,
            observations.Count,
            categories,
            InformationalPolicy,
            "Informational unless project policy assigns release significance. No project policy currently does, so these observations are not Quality Review findings.");
    }

    public static string CategoryTitle(SddObservationCategory category) => category switch
    {
        SddObservationCategory.ImplementationEvidence => "Implementation evidence",
        SddObservationCategory.TestEvidence => "Test execution evidence",
        SddObservationCategory.Traceability => "Traceability",
        SddObservationCategory.Clarifications => "Clarifications",
        _ => "Other observations",
    };

    public static string StatusLabel(string severity) => severity switch
    {
        "NotAssessed" => "Not assessed",
        "Information" => "Informational",
        "NeedsReview" => "Needs review",
        "Failed" => "Failed",
        _ => severity,
    };

    private static SddObservationTone Tone(string severity) => severity switch
    {
        "Failed" => SddObservationTone.Failed,
        "NeedsReview" => SddObservationTone.NeedsReview,
        _ => SddObservationTone.Neutral,
    };

    private sealed record Label(SddObservationCategory Category, string Title, string Status, SddObservationTone Tone, string Explanation);

    /// <summary>Maps an internal rule code to a user-facing label. The code itself stays available under technical details.</summary>
    private static Label Describe(string code, string severity)
    {
        var status = StatusLabel(severity);
        var tone = Tone(severity);
        return code switch
        {
            "RequirementWithoutCurrentImplementationEvidence" => new(SddObservationCategory.ImplementationEvidence, "Implementation evidence not assessed", status, tone,
                "No current source-backed implementation evidence is linked. This does not mean the implementation is wrong."),
            "StaleImplementationEvidence" => new(SddObservationCategory.ImplementationEvidence, "Implementation evidence may be stale", status, tone,
                "Linked source evidence may no longer match the current source."),
            "RequirementWithoutCurrentExecutionEvidence" => new(SddObservationCategory.TestEvidence, "Execution evidence not assessed", status, tone,
                "No current executed-test evidence is linked. This does not mean tests failed."),
            "LinkedSourceTestWithoutExecutionEvidence" => new(SddObservationCategory.TestEvidence, "Linked tests without execution evidence", status, tone,
                "A test linked from source has no imported execution result yet."),
            "StaleTestEvidence" => new(SddObservationCategory.TestEvidence, "Test execution evidence may be stale", status, tone,
                "An imported test result may no longer match the current source."),
            "StaleSourceTestEvidence" => new(SddObservationCategory.TestEvidence, "Test definition may be stale", status, tone,
                "A linked source test definition may no longer match the current source."),
            "TestResultSourceUnknown" => new(SddObservationCategory.TestEvidence, "Test result not bound to a source version", status, tone,
                "The imported run does not record which source version it tested."),
            "LinkedTestFailedSourceUnknown" => new(SddObservationCategory.TestEvidence, "Linked test failed (source version unknown)", status, tone,
                "A linked test failed in a run that is not bound to a source version, so it is not a current-source result."),
            "CurrentLinkedTestFailed" => new(SddObservationCategory.TestEvidence, "Linked test failed in its current run", status, tone,
                "A linked test failed in a run bound to the current source."),
            "RequirementTestLinkUnresolved" => new(SddObservationCategory.Traceability, "Requirement–test link unresolved", status, tone,
                "A requirement-to-test link could not be resolved to a test."),
            "UnresolvedQuestion" => new(SddObservationCategory.Clarifications, "Unresolved clarification", status, tone,
                "An open clarification question affects this requirement."),
            _ => new(SddObservationCategory.Other, Humanize(code), status, tone, "Lifecycle evidence observation."),
        };
    }

    private static string Humanize(string code)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var ch in code)
        {
            if (char.IsUpper(ch) && builder.Length > 0) builder.Append(' ').Append(char.ToLowerInvariant(ch));
            else builder.Append(ch);
        }
        return builder.ToString();
    }
}
