using BirkNext.IntegrationTopology;

namespace BirkNext.Web.Services;

/// <summary>Labels for the canonical topology. Presentation only: every status is the backend's own.</summary>
public static class TopologyPresentation
{
    public static string Status(TopologyStatus status) => status switch
    {
        TopologyStatus.Expected => "Expected",
        TopologyStatus.Configured => "Configured",
        TopologyStatus.Discovered => "Discovered",
        TopologyStatus.Observed => "Observed",
        TopologyStatus.NeedsReview => "Needs review",
        TopologyStatus.Conflict => "Conflict",
        _ => "Not evaluated",
    };

    public static string Tone(TopologyStatus status) => status switch
    {
        TopologyStatus.Observed or TopologyStatus.Discovered => "ready",
        TopologyStatus.Configured => "neutral",
        TopologyStatus.Conflict or TopologyStatus.NeedsReview => "attention",
        _ => "muted",
    };

    public static string Type(TopologyInterfaceType type) => type switch
    {
        TopologyInterfaceType.Rest => "REST",
        TopologyInterfaceType.GraphQl => "GraphQL",
        TopologyInterfaceType.ExternalApplication => "External application",
        _ => type.ToString(),
    };

    public static string Source(TopologySource source) => source switch
    {
        TopologySource.TargetEnvironment => "Target Environment",
        TopologySource.OpenApi => "OpenAPI",
        TopologySource.GraphQlSchema => "GraphQL schema",
        TopologySource.SourceAnalysis => "Source Analysis",
        TopologySource.IntegrationCatalog => "Integration catalog",
        TopologySource.ExternalApplicationMetadata => "Application metadata",
        TopologySource.BrowserObservation => "Browser observation",
        TopologySource.RuntimeEvidence => "Runtime evidence",
        _ => "Domain profile",
    };

    public static string Sources(IEnumerable<TopologyProvenance> provenance) => string.Join(", ", provenance.Select(item => Source(item.Source)).Distinct());

    public static string Direction(TopologyDirection direction) => direction switch
    {
        TopologyDirection.Provides => "provides",
        TopologyDirection.Calls => "calls",
        TopologyDirection.Produces => "produces",
        TopologyDirection.Consumes => "consumes",
        _ => "",
    };

    public static string Relationship(TopologyRelationshipState state) => state switch
    {
        TopologyRelationshipState.Resolved => "Resolved",
        TopologyRelationshipState.Observed => "Observed",
        TopologyRelationshipState.StronglySupported => "Strongly supported",
        TopologyRelationshipState.Candidate => "Candidate · needs evidence",
        _ => "Expected but unresolved",
    };

    public static string RelationshipTone(TopologyRelationshipState state) => state switch
    {
        TopologyRelationshipState.Resolved or TopologyRelationshipState.Observed => "ready",
        TopologyRelationshipState.StronglySupported => "neutral",
        TopologyRelationshipState.Candidate => "attention",
        _ => "muted",
    };

    public static bool IsResolved(TopologyRelationshipState state) => state is TopologyRelationshipState.Resolved or TopologyRelationshipState.Observed;

    public static string RoleState(TopologySourceRoleState state) => state switch
    {
        TopologySourceRoleState.Available => "Available",
        TopologySourceRoleState.PartlyAvailable => "Partly available",
        TopologySourceRoleState.NeedsAnalysis => "Needs analysis",
        TopologySourceRoleState.ReferenceAvailable => "Reference available",
        TopologySourceRoleState.ReferenceUnavailable => "Reference unavailable",
        _ => "Not provided",
    };

    public static string RoleTone(TopologySourceRoleState state) => state switch
    {
        TopologySourceRoleState.Available or TopologySourceRoleState.ReferenceAvailable => "ready",
        TopologySourceRoleState.PartlyAvailable or TopologySourceRoleState.NeedsAnalysis or TopologySourceRoleState.ReferenceUnavailable => "attention",
        _ => "muted",
    };

    public static string Requirement(TopologySourceRequirement requirement) => requirement switch
    {
        TopologySourceRequirement.Required => "Required",
        TopologySourceRequirement.Recommended => "Needed for full infrastructure topology",
        TopologySourceRequirement.Conditional => "Conditional",
        TopologySourceRequirement.BuiltInReference => "Built-in reference · no import required",
        _ => "Optional",
    };

    /// <summary>Coverage groups in reading order: required application sources, infrastructure, built-in references, additional evidence.</summary>
    public static IEnumerable<(string Title, List<TopologySourceRoleCoverage> Roles)> RoleGroups(TopologyCoverageView coverage) =>
        new (string Title, Func<TopologySourceRoleCoverage, bool> Match)[]
        {
            ("Required application sources", role => role.Requirement == TopologySourceRequirement.Required),
            ("Infrastructure", role => role.Requirement == TopologySourceRequirement.Recommended),
            ("Built-in references", role => role.Requirement == TopologySourceRequirement.BuiltInReference),
            ("Additional evidence", role => role.Requirement is TopologySourceRequirement.Conditional or TopologySourceRequirement.Optional),
        }.Select(group => (group.Title, coverage.Roles.Where(group.Match).ToList())).Where(group => group.Item2.Count > 0);

    public static string Completeness(TopologyCompletenessState state) => state switch
    {
        TopologyCompletenessState.Complete => "Complete",
        TopologyCompletenessState.Partial => "Partial",
        TopologyCompletenessState.Observed => "Observed",
        TopologyCompletenessState.NotObserved => "Not observed",
        _ => "Not available",
    };

    public static string Strength(TopologyEvidenceStrength strength) => strength switch
    {
        TopologyEvidenceStrength.VeryStrong => "Very strong",
        TopologyEvidenceStrength.Strong => "Strong",
        TopologyEvidenceStrength.Supporting => "Supporting",
        _ => "Weak",
    };

    public static string Kind(TopologyComponentKind kind) => kind switch
    {
        TopologyComponentKind.Resource => "Messaging and data resources",
        TopologyComponentKind.External => "External applications",
        _ => "Applications",
    };

    /// <summary>The page's one-line counts: application components, interfaces, resolved connections and things needing attention.</summary>
    public static (int Components, int Interfaces, int Resolved, int Attention) Counts(IntegrationTopologyView view) => (
        view.Components.Count(item => item.Kind == TopologyComponentKind.Application),
        view.Components.Sum(item => item.Interfaces.Count),
        view.Connections.Count(item => IsResolved(item.Relationship) && item.Status != TopologyStatus.Conflict),
        view.Connections.Count(item => item.Status is TopologyStatus.Conflict or TopologyStatus.NeedsReview)
            + view.Components.SelectMany(item => item.Interfaces).Count(item => item.Status is TopologyStatus.Conflict or TopologyStatus.NeedsReview));
}
