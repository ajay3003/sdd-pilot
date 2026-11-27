using BirkNext.TestPlanning;

namespace BirkNext.TestPlanning;

/// <summary>
/// The filters of Finn og forstå that a server search over the scenarios a page did NOT load can apply (2026-11-27): every one is a property of
/// the discovered scenario itself, read exactly as the page reads it (<c>ScenarioBrowserPresentation.Apply</c>). Filters that need state only the
/// page holds (work queue, preparation status, test plans, automatic tests, reviewer type decisions, relevance) are NOT here; the page names them
/// as not applied to those matches instead of pretending.
/// </summary>
public sealed record ScenarioSearchFilter(string? Area = null, string? Type = null, string? Story = null, string? Requirement = null, string? Priority = null,
    string? TestLevel = null)
{
    /// <summary>The page's «no user story» story key and «priority not given» value (same constants as the page).</summary>
    public const string NoStory = "(none)", NoPriority = "(none)";

    public bool IsEmpty => Area is null && Type is null && Story is null && Requirement is null && Priority is null && TestLevel is null;

    /// <summary>The page's area: the scenario's area, or its source category when it has none.</summary>
    public static string AreaOf(DiscoveredScenario c) => string.IsNullOrWhiteSpace(c.Area) ? c.SourceCategory : c.Area!;

    public bool Matches(DiscoveredScenario c) =>
        (Area is null || AreaOf(c) == Area)
        && (Type is null || ScenarioCandidateTypes.Label(c.Type) == Type)
        && (Story is null || (Story == NoStory ? c.Story is null : c.Story?.Key == Story))
        && (Requirement is null || c.RequirementIds.Contains(Requirement, StringComparer.OrdinalIgnoreCase))
        && (Priority is null || (Priority == NoPriority ? string.IsNullOrEmpty(c.Story?.Priority) : string.Equals(c.Story?.Priority, Priority, StringComparison.OrdinalIgnoreCase)))
        && (TestLevel is null || TestLevelClassifier.Of(c.Type).Level.ToString() == TestLevel);
}
