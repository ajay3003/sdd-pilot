using BirkNext.Integrations;

namespace BirkNext.Api.Services.IntegrationJourneys;

/// <summary>
/// Describes any registered journey pack as a test pack, from the pack's own metadata only: journeys (with their component flow and executor),
/// verification views, architecture rules, required capabilities, history support and the navigation a page renders. Generic: it never
/// names a pack, project, system or domain, so a second pack is described exactly like the first.
/// </summary>
public static class TestPackDescriber
{
    public const string OverviewViewId = "overview";
    public const string RulesViewId = "architecture-rules";
    public const string HistoryViewId = "history";

    public static TestPackDescriptor Describe(IIntegrationJourneyPack pack)
    {
        var journeys = pack.Journeys.OrderBy(journey => journey.Order).ThenBy(journey => journey.JourneyId, StringComparer.Ordinal)
            .Select(journey => new TestPackJourney(journey.JourneyId, journey.DisplayName, Flow(journey), journey.ExecutionMode, journey.DelegatedProviderId)).ToList();
        var integrations = pack.Journeys.SelectMany(journey => journey.Integrations)
            .GroupBy(requirement => requirement.Key, StringComparer.Ordinal)
            .Select(group => group.First())
            .Select(requirement => new TestPackCapabilityRequirement(requirement.Key, requirement.Label, "Target Environment → Integration Catalog",
                $"{requirement.Producer} → {requirement.Consumer} ({requirement.Kind}): {requirement.Purpose}"))
            .ToList();
        var verification = pack.VerificationViews.ToList();
        var navigation = new List<TestPackNavigationGroup>
        {
            new("Overview", [new(OverviewViewId, "Overview", TestPackViewKind.Overview)]),
            new("Journeys", journeys.Select(journey => new TestPackView(journey.JourneyId, journey.DisplayName, TestPackViewKind.Journey, journey.JourneyId)).ToList()),
        };
        if (pack.SecurityView is { } security) navigation.Add(new("Security", [security with { Kind = TestPackViewKind.Security }]));
        var verify = verification.Concat(pack.Rules.Count > 0 ? [new TestPackView(RulesViewId, "Architecture Rules", TestPackViewKind.Rules)] : Array.Empty<TestPackView>()).ToList();
        if (verify.Count > 0) navigation.Add(new("Verify", verify));
        if (pack.SupportsHistory) navigation.Add(new("History", [new(HistoryViewId, "History", TestPackViewKind.History)]));

        return new TestPackDescriptor
        {
            PackId = pack.PackId, PackVersion = pack.PackVersion, DisplayName = pack.DisplayName, Description = pack.Description,
            Journeys = journeys, VerificationViews = verification, RuleIds = pack.Rules.Select(rule => rule.RuleId).ToList(),
            RequiredIntegrationCapabilities = integrations, RequiredEvidenceCapabilities = pack.RequiredEvidenceCapabilities, RequiredTestDataCapabilities = pack.RequiredTestDataCapabilities,
            RequiredConfiguration = pack.RequiredConfiguration, SourceRoles = pack.SourceRoles,
            ExecutionProviders = journeys.Select(journey => journey.ExecutionProvider ?? "Shared journey runner").Distinct(StringComparer.Ordinal).ToList(),
            SupportsHistory = pack.SupportsHistory, Navigation = navigation,
        };
    }

    /// <summary>The journey's declared flow, else the component chain taken from its steps (From → To), in step order, without repeats.</summary>
    public static string Flow(IntegrationJourneyDefinition journey)
    {
        if (journey.Flow is { Length: > 0 } declared) return declared;
        var chain = new List<string>();
        void Add(string? component) { if (component is { Length: > 0 } && (chain.Count == 0 || chain[^1] != component)) chain.Add(component); }
        foreach (var step in journey.Steps) { Add(step.FromComponent); Add(step.ToComponent); }
        return string.Join(" → ", chain);
    }

    /// <summary>A view id a pack's navigation actually declares (route validation for pages); null for anything else.</summary>
    public static TestPackView? FindView(TestPackDescriptor descriptor, string? viewId) =>
        descriptor.Navigation.SelectMany(group => group.Views).FirstOrDefault(view => string.Equals(view.ViewId, viewId, StringComparison.Ordinal));
}
