using System.Text.RegularExpressions;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>One thing the Plan says that matters for testing, with where it comes from. Every item here is declared in the Plan.</summary>
public sealed record PlanTestingImpactItem(string Category, string Text);

/// <summary>
/// Plan Explorer in tester terms, from the existing deterministic Plan extraction only: what the Plan declares that affects testing, the
/// source-specification link state, and labels for BirkNext-derived complexity signals. Nothing here infers new facts or generates tests.
/// </summary>
public static partial class PlanTesterPresentation
{
    /// <summary>
    /// "What this Plan means for testing": performance targets, scope, explicit prohibitions and constraints, risks the Plan lists, declared
    /// integrations, and the testing approach — each as the Plan states it. Empty when the Plan states none of these.
    /// </summary>
    public static IReadOnlyList<PlanTestingImpactItem> TestingImpact(PlanDocument plan)
    {
        var items = new List<PlanTestingImpactItem>();
        foreach (var c in plan.Constraints)
        {
            var category = c.ConstraintType switch
            {
                ConstraintType.PerformanceGoal => "Performance target",
                ConstraintType.ScaleScope => "Scope",
                ConstraintType.NoViolation => "Must not happen",
                ConstraintType.ComplexityJustification => "Justified complexity",
                _ => "Constraint",
            };
            items.Add(new(category, c.Title));
        }
        foreach (var r in plan.Risks)
            items.Add(new(r.Severity == RiskSeverity.Unrated ? "Risk" : $"{r.Severity} risk", r.Title));
        if (plan.Dependencies.Count > 0)
            items.Add(new("Integrations and dependencies", string.Join(", ", plan.Dependencies.Select(d => d.Name).Distinct(StringComparer.OrdinalIgnoreCase).Take(8))
                + (plan.Dependencies.Count > 8 ? $" and {plan.Dependencies.Count - 8} more" : "")));
        foreach (var statement in TestingStatements(plan).Take(4))
            items.Add(new("Testing approach", statement));
        if (plan.TestingInfo is { Frameworks.Count: > 0 } testing && !TestingStatements(plan).Any())
            items.Add(new("Testing approach", $"Declares {string.Join(", ", testing.Frameworks)}"));
        return items;
    }

    /// <summary>The Plan's own testing statements (paragraphs and list items of its testing section), in order.</summary>
    public static IReadOnlyList<string> TestingStatements(PlanDocument plan) =>
        plan.TestingInfo is not { } testing ? [] :
        [.. testing.Blocks.Where(b => !b.IsCodeBlock)
            .SelectMany(b => (string.IsNullOrWhiteSpace(b.Paragraph) ? [] : new[] { b.Paragraph!.Trim() }).Concat(b.BulletPoints.Select(p => p.Trim())))
            .Where(t => t.Length > 0 && t is not ("---" or "***" or "___"))];

    /// <summary>The source-specification link in tester words: Linked, Not found in this feature, Needs review, or Named only.</summary>
    public static (string State, string Text) SourceSpecification(PlanSpecificationReferenceResolution? reference) => reference?.Status switch
    {
        null => ("Not stated", "The plan does not name its source specification."),
        PlanSpecificationReferenceStatus.Resolved => ("Linked", reference.Artifact?.DisplayName is { Length: > 0 } title ? title : "Specification of this feature"),
        PlanSpecificationReferenceStatus.Ambiguous => ("Needs review", "Specification reference requires review."),
        PlanSpecificationReferenceStatus.Missing => ("Not found", "Referenced specification not found in this feature."),
        _ => ("Named only", reference.Detail ?? "Named in the plan; not linked to this feature's specification."),
    };

    /// <summary>The dimension a derived complexity signal describes, so testers do not read one generic score as difficulty or risk.</summary>
    public static string ComplexityDimension(PlanComplexityItem item)
    {
        var factors = string.Join(" ", item.Factors);
        if (factors.Contains("performance goal", StringComparison.OrdinalIgnoreCase) || factors.Contains("scale/scope", StringComparison.OrdinalIgnoreCase))
            return "Scale / performance target";
        return item.Area switch
        {
            "External Integrations" or "Message Processing & Streaming" or "Distributed Systems" => "Integration complexity",
            "Data Storage & Persistence" or "Data Migration / Schema Evolution" => "Data complexity",
            "Authentication & Authorization" => "Security complexity",
            "Checkpointing & State Management" => "State / processing complexity",
            "Risk Surface" => "Risk-driven",
            _ => "Stated in plan",
        };
    }

    /// <summary>User story references in a phase title or description ("User Story 1", "US1", "US-02"), as written.</summary>
    public static IReadOnlyList<string> UserStoryRefs(PlanImplementationPhase phase) =>
        [.. UserStory().Matches($"{phase.Title} {phase.Description}").Select(m => m.Groups["n"].Value.TrimStart('0') is { Length: > 0 } n ? $"US{n}" : "US0")
            .Distinct(StringComparer.Ordinal)];

    [GeneratedRegex(@"\b(?:User\s+Story\s+|US-?)(?<n>\d{1,3})\b", RegexOptions.IgnoreCase)]
    private static partial Regex UserStory();
}
