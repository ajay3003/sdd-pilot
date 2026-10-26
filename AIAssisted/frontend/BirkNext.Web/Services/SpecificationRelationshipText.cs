namespace BirkNext.Web.Services;

/// <summary>
/// One sentence that explains the Specification Explorer relationship metrics together, so "0 / 45 linked to requirements" next to
/// "45 / 45 under a user story" reads as testability information, not a failure. Neutral: direct requirement links are not required by policy.
/// </summary>
public static class SpecificationRelationshipText
{
    public static string? Explain(int linkedToRequirements, int underUserStory, int tests)
    {
        if (tests == 0) return null;
        string Count(int n) => n == tests ? $"All {tests}" : $"{n} of {tests}";
        if (linkedToRequirements == 0 && underUserStory > 0)
            return $"{Count(underUserStory)} tests are organized under user stories, but none is linked directly to a requirement ID. Direct requirement links are not established.";
        if (linkedToRequirements == 0)
            return $"None of the {tests} tests is linked directly to a requirement ID or organized under a user story.";
        if (linkedToRequirements < tests)
            return $"{Count(linkedToRequirements)} tests name a requirement ID directly; {Count(underUserStory).ToLowerInvariant()} are organized under user stories.";
        return null;
    }
}
