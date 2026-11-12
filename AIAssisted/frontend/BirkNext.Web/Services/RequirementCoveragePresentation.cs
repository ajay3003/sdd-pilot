using BirkNext.TestPlanning;

namespace BirkNext.Web.Services;

/// <summary>
/// One wording for MT-7 reviewed requirement coverage on every page (Requirements Traceability, Test Plan Suggestions, Dashboard, Recommended
/// Workflow). Reviewed scenario coverage is never called "tested", "covered" or "passed": it is a tester's review of a scenario that a verified
/// association links to the requirement.
/// </summary>
public static class RequirementCoveragePresentation
{
    public const string RelationshipReviewHref = "correlation-review?tab=review";

    public static string StateLabel(RequirementCoverageState state) => state switch
    {
        RequirementCoverageState.ReviewedScenarioCoverage => "Reviewed scenario coverage",
        RequirementCoverageState.AssociationVerifiedScenarioNotReviewed => "Association verified · scenario not reviewed",
        RequirementCoverageState.NeedsRelationshipReview => "Needs relationship review",
        _ => "No documented scenario coverage",
    };

    /// <summary>Tone class suffix (ok / warn / info / muted): a state, never a pass colour for something that did not run.</summary>
    public static string StateTone(RequirementCoverageState state) => state switch
    {
        RequirementCoverageState.ReviewedScenarioCoverage => "ok",
        RequirementCoverageState.AssociationVerifiedScenarioNotReviewed => "info",
        RequirementCoverageState.NeedsRelationshipReview => "warn",
        _ => "muted",
    };

    public static string TrustLabel(AssociationTrust trust) => trust switch
    {
        AssociationTrust.ExplicitSourceReference => "Named by the published guide",
        AssociationTrust.ReviewedCanonicalAssociation => "Confirmed in Relationship Review",
        AssociationTrust.SuggestedAssociation => "Suggested — not verified",
        _ => "Rejected",
    };

    public static string RuleLabel(string ruleId) => ruleId switch
    {
        "CR-S-EXPLICIT" => "the guide names the requirement",
        "CR-S-VIA-TEST" => "a test the guide names refers to this requirement",
        "CR-S-SCOPE" => "several feature packs define this id",
        _ => ruleId,
    };

    public static string Requirement(RequirementScenarioAssociation a) => $"{a.RequirementId} ({ShortKey(a.RequirementKey)})";

    /// <summary>"Requirement|skole/001-skoletjenesten/FR-006" → "skole/001-skoletjenesten".</summary>
    public static string ShortKey(string requirementKey)
    {
        var identity = requirementKey[(requirementKey.IndexOf('|') + 1)..];
        var cut = identity.LastIndexOf('/');
        return cut > 0 ? identity[..cut] : identity;
    }

    /// <summary>The associations of one guide scenario, split by trust (verified first; suggested and rejected never mixed in).</summary>
    public static (List<RequirementScenarioAssociation> Verified, List<RequirementScenarioAssociation> Suggested, List<RequirementScenarioAssociation> Rejected)
        ForScenario(RequirementCoverageView? view, string guideKey, string scenarioId)
    {
        var all = view?.Associations.Where(a => a.GuideKey == guideKey && a.ScenarioId == scenarioId).ToList() ?? [];
        return ([.. all.Where(a => a.Verified)], [.. all.Where(a => a.Trust == AssociationTrust.SuggestedAssociation)], [.. all.Where(a => a.Trust == AssociationTrust.Rejected)]);
    }

    /// <summary>The focus-panel facts (counts only with a valid denominator: the requirements of the latest interpretation).</summary>
    public static string Summary(RequirementCoverageView view) => !view.Known
        ? "Reviewed requirement coverage: unknown"
        : $"Reviewed scenario coverage: {view.Reviewed} of {view.Requirements} requirements · {view.NeedsReview} need relationship review · {view.VerifiedNotReviewed} linked, scenario not reviewed · {view.NoScenario} without a documented scenario";
}
