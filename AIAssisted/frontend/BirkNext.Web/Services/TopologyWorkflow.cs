using BirkNext.IntegrationTopology;

namespace BirkNext.Web.Services;

/// <summary>
/// Integration Topology's guided review (not a strict sequence): environment → source coverage → inspect topology → resolve evidence gaps → review
/// integrations → runtime evidence. Derived from the topology view and coverage the page already loads; a step is never "done" because it was
/// visited. Technical relationships are not integrations; topology never registers a system or approves an integration.
/// </summary>
public static class TopologyWorkflow
{
    public const string OverviewTab = "overview", MapTab = "map", GapsTab = "gaps", InventoryTab = "inventory", MessagesTab = "messages", StorageTab = "storage";

    public static readonly IReadOnlyList<(string Key, string Label)> Tabs =
        [(OverviewTab, "Overview"), (MapTab, "Topology Map"), (GapsTab, "Evidence & Gaps"), (InventoryTab, "Technical Inventory"), (MessagesTab, "Messages"), (StorageTab, "Storage")];

    public static string ResolveTab(string? tab) => Tabs.Any(t => string.Equals(t.Key, tab, StringComparison.OrdinalIgnoreCase)) ? tab!.ToLowerInvariant() : OverviewTab;

    /// <summary>The tab that holds an attention category's anchor.</summary>
    public static string TabFor(string anchor) => anchor is "it-interfaces" or "it-connections" or "it-inventory-heading" ? InventoryTab : GapsTab;

    public static IReadOnlyList<GuidedStep> Derive(IntegrationTopologyView view, TopologyCoverageView? coverage, int observations, string? profileId, Func<string, string> tabHref)
    {
        var connections = view.Connections.Count;
        var gaps = coverage?.Roles.Where(r => TopologyPresentation.SourceGap(r.State)).ToList() ?? [];
        var needsBinding = gaps.Count(r => r.State == TopologySourceRoleState.AvailableNeedsBinding);
        var unresolved = coverage?.Relationships.Count(r => !TopologyPresentation.IsResolved(r.State)) ?? 0;
        var expected = coverage?.Relationships.Count ?? 0;
        var steps = new List<GuidedStep>
        {
            new("environment", "Select project and environment", GuidedStepState.Done, view.EnvironmentName ?? view.EnvironmentId,
                $"Built {view.GeneratedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC from stored source facts (resolver v{view.ResolverVersion}); snapshots are never changed."),
            coverage is null
                ? new("coverage", "Review source coverage", GuidedStepState.NotApplicable, "No expected-topology profile",
                    "No Test Pack declares an expected topology for this environment, so source coverage is not assessed.")
                : gaps.Count > 0
                    ? new("coverage", "Review source coverage", GuidedStepState.Current, $"{gaps.Count} source role{(gaps.Count == 1 ? "" : "s")} need action",
                        $"{needsBinding} available but not bound (bind, do not re-import) · {gaps.Count - needsBinding} missing, ambiguous or needing analysis. Sources are recognized by content, never by file name.",
                        Evidence: $"Core sources: {TopologyPresentation.CoreSources(coverage)}.", ActionLabel: "Open Evidence & Gaps", Href: tabHref(GapsTab))
                    : new("coverage", "Review source coverage", GuidedStepState.Done, "Sources in place", $"Core sources: {TopologyPresentation.CoreSources(coverage)}.",
                        ActionLabel: "Open Evidence & Gaps", Href: tabHref(GapsTab)),
            new("inspect", "Inspect topology", connections == 0 ? GuidedStepState.Waiting : GuidedStepState.Current,
                connections == 0 ? "Nothing to draw" : $"{connections} connection{(connections == 1 ? "" : "s")}",
                connections == 0 ? "No source states a connection yet." : "The map shows the expected flow or the system connections; resolved, inferred and unresolved relationships are drawn differently.",
                ActionLabel: "Open Topology Map", Href: tabHref(MapTab)),
            coverage is null
                ? new("gaps", "Resolve evidence gaps", GuidedStepState.NotApplicable, "Not assessed", "Expected relationships come from a Test Pack topology profile.")
                : unresolved > 0
                    ? new("gaps", "Resolve evidence gaps", GuidedStepState.Current, $"{unresolved} of {expected} expected unresolved",
                        "Each unresolved relationship names the evidence it needs and where to add it. An expected relationship never creates a connection.",
                        ActionLabel: "Open Evidence & Gaps", Href: tabHref(GapsTab))
                    : new("gaps", "Resolve evidence gaps", GuidedStepState.Done, $"All {expected} expected relationships resolved",
                        "Resolved means supported by source evidence — not observed, healthy or contract-compatible."),
            new("integrations", "Review integrations", GuidedStepState.Waiting, "Reviewed elsewhere",
                "Technical relationships are not integrations: configure integrations in the Integration Catalog and review their evidence in Integration Quality Review. Topology never registers a system or approves an integration.",
                ActionLabel: "Open Integration Quality Review", Href: "/integration-quality-review"),
            new("runtime", "Validate runtime evidence", GuidedStepState.Waiting, observations == 0 ? "No runtime observation" : $"{observations} observation{(observations == 1 ? "" : "s")} from this browser",
                "Runtime observations come from Endpoint Discovery in this browser and mark interfaces as observed; they never create a connection or verify a relationship's health."),
        };
        var first = steps.FindIndex(s => s.State == GuidedStepState.Current);
        return [.. steps.Select((s, i) => i > first && first >= 0 && s.State == GuidedStepState.Current ? s with { State = GuidedStepState.Waiting } : s)];
    }
}
