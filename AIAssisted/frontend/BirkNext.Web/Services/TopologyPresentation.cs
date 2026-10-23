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

    /// <summary>The confidence label. Observed is a runtime dimension, so a resolved-and-observed relationship reads "Resolved" here and
    /// "Observed" in <see cref="Runtime"/>. Strongly supported is below Resolved and says so.</summary>
    public static string Relationship(TopologyRelationshipState state) => state switch
    {
        TopologyRelationshipState.Resolved or TopologyRelationshipState.Observed => "Resolved",
        TopologyRelationshipState.StronglySupported => "Strongly supported · not yet resolved",
        TopologyRelationshipState.Candidate => "Candidate · needs evidence",
        _ => "Expected but unresolved",
    };

    /// <summary>Runtime observation, separate from confidence: never a health or compatibility claim.</summary>
    public static string Runtime(bool observed) => observed ? "Observed at runtime" : "Not observed at runtime";

    /// <summary>The confidence order, weakest first, with what each state means (shown as the page's legend).</summary>
    public static readonly IReadOnlyList<(string State, string Meaning)> ConfidenceLegend =
    [
        ("Expected but unresolved", "Only a Test Pack expects it; no evidence states it, so no connection exists."),
        ("Candidate", "Partial evidence: one side, or an unresolved target."),
        ("Strongly supported", "Several pieces of evidence point to it, but the producer/consumer identity is not yet established."),
        ("Resolved", "Evidence establishes the producer/consumer identity. Not observed, healthy, contract-compatible or tested unless stated separately."),
    ];

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
        TopologySourceRoleState.Bound => "Bound",
        TopologySourceRoleState.AvailableNeedsBinding => "Available — needs binding",
        TopologySourceRoleState.Ambiguous => "Ambiguous — several candidates",
        TopologySourceRoleState.PartlyAvailable => "Partly available",
        TopologySourceRoleState.NeedsAnalysis => "Needs analysis",
        TopologySourceRoleState.ReferenceAvailable => "Reference available",
        TopologySourceRoleState.ReferenceUnavailable => "Reference unavailable",
        _ => "Not provided",
    };

    public static string RoleTone(TopologySourceRoleState state) => state switch
    {
        TopologySourceRoleState.Available or TopologySourceRoleState.Bound or TopologySourceRoleState.ReferenceAvailable => "ready",
        TopologySourceRoleState.PartlyAvailable or TopologySourceRoleState.NeedsAnalysis or TopologySourceRoleState.ReferenceUnavailable
            or TopologySourceRoleState.AvailableNeedsBinding or TopologySourceRoleState.Ambiguous => "attention",
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

    /// <summary>The page's one-line counts: application components, interfaces (one per normalized identity, however many providers stated it)
    /// and resolved connections.</summary>
    public static (int Components, int Interfaces, int Resolved) Counts(IntegrationTopologyView view) => (
        view.Components.Count(item => item.Kind == TopologyComponentKind.Application),
        view.Components.Sum(item => item.Interfaces.Count),
        view.Connections.Count(item => IsResolved(item.Relationship) && item.Status != TopologyStatus.Conflict));

    /// <summary>A call whose target component is not resolved: a relationship candidate (caller + address), not just an interface warning.</summary>
    public static bool UnresolvedTarget(TopologyInterface item) => item.Direction == TopologyDirection.Calls && item.Status == TopologyStatus.NeedsReview;

    /// <summary>
    /// What needs attention, split into mutually exclusive categories (each item is counted once): expected relationships not resolved,
    /// calls whose target is unresolved (relationship candidates), other interfaces needing review, connections in conflict or review
    /// (ambiguous correlations), and source roles whose presence or binding needs action.
    /// </summary>
    public static IReadOnlyList<TopologyAttentionCategory> Attention(IntegrationTopologyView view, TopologyCoverageView? coverage)
    {
        var interfaces = view.Components.SelectMany(item => item.Interfaces).ToList();
        var targets = interfaces.Count(UnresolvedTarget);
        var review = interfaces.Count(item => item.Status is TopologyStatus.Conflict or TopologyStatus.NeedsReview && !UnresolvedTarget(item));
        var ambiguous = view.Connections.Count(item => item.Status is TopologyStatus.Conflict or TopologyStatus.NeedsReview);
        var list = new List<TopologyAttentionCategory>();
        if (coverage is not null)
        {
            list.Add(new("unresolved", "Expected relationships unresolved", coverage.Relationships.Count(item => !IsResolved(item.State)), "it-unresolved-heading"));
            list.Add(new("sources", "Source presence/binding gaps", coverage.Roles.Count(item => SourceGap(item.State)), "it-coverage-heading"));
        }
        list.Add(new("targets", "Unresolved call targets (relationship candidates)", targets, "it-interfaces"));
        list.Add(new("interfaces", "Interfaces needing review", review, "it-interfaces"));
        list.Add(new("ambiguous", "Ambiguous correlations (connections)", ambiguous, "it-connections"));
        return list;
    }

    /// <summary>A source role whose presence or binding needs an action (a Conditional role that is not provided counts: it leaves a relationship unprovable).</summary>
    public static bool SourceGap(TopologySourceRoleState state) => state is TopologySourceRoleState.NotProvided or TopologySourceRoleState.NeedsAnalysis
        or TopologySourceRoleState.AvailableNeedsBinding or TopologySourceRoleState.Ambiguous or TopologySourceRoleState.ReferenceUnavailable;

    /// <summary>The action label for a gap, named after what is actually missing ("Bind existing source", never "Import" for an existing one).</summary>
    public static string? ActionLabel(TopologyGapAction action) => action switch
    {
        TopologyGapAction.ImportSource => "Import source",
        TopologyGapAction.BindSource => "Bind existing source",
        TopologyGapAction.ReviewCandidates => "Review source candidates",
        TopologyGapAction.ReanalyzeSource => "Re-analyze source",
        TopologyGapAction.ViewMissingEvidence => "View missing evidence",
        TopologyGapAction.ConfigureIntegration => "Configure integration",
        TopologyGapAction.ViewEvidence => "View evidence",
        _ => null,
    };

    /// <summary>Where each action is done — always the canonical owner (Topology binds, imports and configures nothing itself).</summary>
    public static string? ActionHref(TopologyGapAction action, string? profileId = null) => action switch
    {
        TopologyGapAction.ImportSource => "/project-import",
        TopologyGapAction.BindSource or TopologyGapAction.ReviewCandidates => SourceGuidancePresentation.ActionHref("iqr-evidence"),
        TopologyGapAction.ReanalyzeSource or TopologyGapAction.ViewMissingEvidence or TopologyGapAction.ViewEvidence => "/source-analysis",
        TopologyGapAction.ConfigureIntegration => IntegrationReviewPresentation.IntegrationsHref(profileId),
        _ => null,
    };

    /// <summary>"3 need M2LB binding · 1 needs BiRK application source import · 2 need integration configuration": unresolved expected
    /// relationships grouped by the action and source that would resolve them.</summary>
    public static IReadOnlyList<(string Text, int Count)> MissingEvidenceGroups(TopologyCoverageView coverage) =>
        [.. coverage.Relationships.Where(item => !IsResolved(item.State))
            .GroupBy(item => (item.Action, Role: item.SourceRoleName ?? ""))
            .Select(group => (Text: GapPhrase(group.Key.Action, group.Key.Role, group.Count()), Count: group.Count()))
            .OrderByDescending(group => group.Count)];

    private static string GapPhrase(TopologyGapAction action, string role, int count)
    {
        var need = count == 1 ? "needs" : "need";
        return action switch
        {
            TopologyGapAction.ImportSource => $"{count} {need} {role} (not provided — import)",
            TopologyGapAction.BindSource => $"{count} {need} {role} binding (source available)",
            TopologyGapAction.ReviewCandidates => $"{count} {need} a {role} source chosen among candidates",
            TopologyGapAction.ReanalyzeSource => $"{count} {need} {role} re-analyzed",
            TopologyGapAction.ConfigureIntegration => $"{count} {need} exact configuration mapping",
            _ => $"{count} {need} evidence not yet stated in {(role.Length > 0 ? role : "the available sources")}",
        };
    }

    /// <summary>Core (required) source coverage in one line, keeping presence and binding apart: "Core sources present · 2 need binding" is
    /// not "partial" — nothing is missing, it is only not canonically bound yet.</summary>
    public static string CoreSources(TopologyCoverageView coverage)
    {
        if (coverage.CoreSourcesComplete) return "Core sources complete";
        var core = coverage.Roles.Where(role => role.Requirement == TopologySourceRequirement.Required).ToList();
        var absent = core.Count(role => role.State is TopologySourceRoleState.NotProvided or TopologySourceRoleState.NeedsAnalysis or TopologySourceRoleState.PartlyAvailable);
        var binding = core.Count(role => role.State is TopologySourceRoleState.AvailableNeedsBinding or TopologySourceRoleState.Ambiguous);
        return absent > 0 ? $"Core sources partial · {absent} missing or incomplete" + (binding > 0 ? $" · {binding} need binding" : "")
            : $"Core sources present · {binding} need{(binding == 1 ? "s" : "")} binding";
    }

    /// <summary>"2 of 6 evidence providers used": the denominator is provider categories, never snapshot files.</summary>
    public static string SourcesUsed(IntegrationTopologyView view) =>
        $"{view.Sources.Count(item => item.State == TopologySourceState.Used)} of {view.Sources.Count} evidence provider categories used";
}

/// <summary>One mutually exclusive attention category and where on the page its items are.</summary>
public sealed record TopologyAttentionCategory(string Key, string Label, int Count, string Anchor);
