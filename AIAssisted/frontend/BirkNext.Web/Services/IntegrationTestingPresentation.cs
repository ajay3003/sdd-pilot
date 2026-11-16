using BirkNext.IntegrationSuites;
using BirkNext.Integrations;
using BirkNext.IntegrationTopology;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Technical Testing → Integration Testing: one guided entry over the existing integration modules, in the order a technical tester works —
/// Discover &amp; understand (Integration Topology, Integrations catalog), Define &amp; assess (declared Integration Journeys), Prepare test suite
/// (Integration Test Suites), Verify &amp; investigate (Integration Quality Review). Every fact comes from the module that owns it and every
/// handoff passes that module's own identity (catalog integration id, declared journey id, suite id) — never a match by display name. Each count
/// names the entity it counts. Pure; no I/O.
/// </summary>
public static class IntegrationTestingPresentation
{
    public const string HomeRoute = "technical-testing/integrations";
    public const string DeclareRoute = "integration-journeys/declare";

    public static string StageRoute(string stage, string? journeyId = null) =>
        $"{HomeRoute}?stage={stage}{(journeyId is { Length: > 0 } j ? $"&journey={Uri.EscapeDataString(j)}" : "")}";

    public static readonly IReadOnlyList<WorkspaceStage> Stages =
    [
        new("discover", "Discover & understand", "What integrations exist, and how are they connected?", StageRoute("discover")),
        new("define", "Define & assess", "Which journey matters, and what supports it?", StageRoute("define")),
        new("prepare", "Prepare test suite", "How will the journey be tested?", StageRoute("prepare")),
        new("verify", "Verify & investigate", "What was actually verified?", StageRoute("verify")),
    ];

    public static WorkspaceStage? Stage(string? id) => Stages.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <param name="Topology">The active Target Environment's Integration Topology (components and connections); null = not read.</param>
    /// <param name="Integrations">Configured integrations of the Integrations catalog (the identity Integration Quality Review uses).</param>
    /// <param name="Journeys">The project's declared Integration Journeys (current revisions).</param>
    /// <param name="Suites">Integration Test Suites generated from this project's own source snapshot.</param>
    /// <param name="OtherSuites">Suites of other source snapshots (not shown: they belong to other sources).</param>
    /// <param name="Reviews">Integration Quality Review runs of the active Target Environment.</param>
    public sealed record Facts(string? ProjectKey, string? ProjectName, string? EnvironmentId, string? TargetName, IntegrationTopologyView? Topology,
        IReadOnlyList<IntegrationDefinition> Integrations, IReadOnlyList<DeclaredIntegrationJourney> Journeys, IReadOnlyList<IntegrationSuiteSummary> Suites, int OtherSuites,
        IReadOnlyList<IntegrationReviewRunSummary> Reviews, IReadOnlyList<string> Errors)
    {
        public static Facts Empty => new(null, null, null, null, null, [], [], [], 0, [], []);
        public DeclaredIntegrationJourney? Journey(string? id) => id is null ? null : Journeys.FirstOrDefault(j => string.Equals(j.Id, id, StringComparison.Ordinal));
    }

    // ---- counts (each names its entity) ---------------------------------------------------------------------------------------------

    public sealed record Count(string Entity, int Value, string Detail, string? Href);

    public static IReadOnlyList<Count> Counts(Facts f)
    {
        var connections = f.Topology?.Connections ?? [];
        var review = connections.Count(c => c.Relationship is TopologyRelationshipState.Candidate or TopologyRelationshipState.ExpectedUnresolved);
        return
        [
            new("Systems and components", f.Topology?.Components.Count ?? 0, f.Topology is null ? "Integration Topology not read" : "in the Target Environment's topology", "integration-topology"),
            new("Connections", connections.Count, review == 0 ? "between components" : $"{review} need review (candidate or expected, not resolved)", "integration-topology?tab=map"),
            new("Configured integrations", f.Integrations.Count, "in the Integrations catalog (what Integration Quality Review checks)", "integration-quality-review?view=setup-evidence"),
            new("Journeys", f.Journeys.Count, "declared for this project", "integration-journeys"),
            new("Test suites", f.Suites.Count, f.OtherSuites > 0 ? $"from this project's source ({f.OtherSuites} of other sources not shown)" : "from this project's source", "integration-suites"),
            new("Quality reviews", f.Reviews.Count, "Integration Quality Review runs in this environment", "integration-quality-review?view=review"),
        ];
    }

    // ---- evidence wording (static, human, runtime kept apart) ---------------------------------------------------------------------

    public static string RelationshipLabel(TopologyRelationshipState s) => s switch
    {
        TopologyRelationshipState.Observed => "Observed at runtime",
        TopologyRelationshipState.Resolved => "Resolved from sources",
        TopologyRelationshipState.StronglySupported => "Strongly supported by sources",
        TopologyRelationshipState.Candidate => "Candidate — needs review",
        _ => "Expected, not resolved",
    };

    public static string RelationshipTone(TopologyRelationshipState s) => s switch
    {
        TopologyRelationshipState.Observed or TopologyRelationshipState.Resolved => "ok",
        TopologyRelationshipState.StronglySupported => "info",
        _ => "warn",
    };

    public static string SuiteLabel(SuiteStatus s) => s switch
    {
        SuiteStatus.ReadyToExecute => "Ready to execute",
        SuiteStatus.PartiallyExecutable => "Partly executable",
        SuiteStatus.NeedsReview => "Needs review",
        SuiteStatus.Blocked => "Blocked",
        _ => "Draft",
    };

    public static string ReviewLabel(IntegrationReviewOutcome o, int findings) => o switch
    {
        IntegrationReviewOutcome.Completed => findings == 0 ? "Completed without findings" : $"{findings} finding{(findings == 1 ? "" : "s")}",
        IntegrationReviewOutcome.CompletedWithLimitations => "Completed with limitations",
        IntegrationReviewOutcome.ManualReviewRequired => "Manual review required",
        _ => "Nothing assessed",
    };

    /// <summary>The connection's catalog integration (an Integrations-catalog provenance names the integration id), so IQR opens on exactly it.</summary>
    public static string? CatalogIntegrationId(TopologyConnection c, IReadOnlyList<IntegrationDefinition> integrations) =>
        c.Provenance.Where(p => p.Source == TopologySource.IntegrationCatalog).Select(p => p.Reference)
            .FirstOrDefault(r => integrations.Any(i => string.Equals(i.Id, r, StringComparison.Ordinal)));

    public static string ComponentName(IntegrationTopologyView? topology, string id) => topology?.Components.FirstOrDefault(c => c.Id == id)?.Name ?? id;

    // ---- links (exact identities) ------------------------------------------------------------------------------------------------

    public static string IqrHref(string integrationId, string view = "review") => $"integration-quality-review?view={view}&integration={Uri.EscapeDataString(integrationId)}";
    public static string ReviewJourneyHref(DeclaredIntegrationJourney j) => $"{DeclareRoute}?journey={Uri.EscapeDataString(j.JourneyId)}";
    public static string SuitesForJourneyHref(DeclaredIntegrationJourney j) => $"integration-suites?journey={Uri.EscapeDataString(j.Id)}";
    public static string SuiteHref(IntegrationSuiteSummary s, string area = "generate") => $"integration-suites?suite={s.Id}&area={area}";

    /// <summary>The journey editor for an association target, only when it is a declared project journey ("project.{key}:{journeyId}");
    /// registered test-pack journeys have no editor here. Exact id only.</summary>
    public static string? DeclaredJourneyHref(string integrationJourneyId)
    {
        if (!integrationJourneyId.StartsWith("project.", StringComparison.Ordinal)) return null;
        var colon = integrationJourneyId.IndexOf(':');
        return colon > 0 && colon < integrationJourneyId.Length - 1 ? $"{DeclareRoute}?journey={Uri.EscapeDataString(integrationJourneyId[(colon + 1)..])}" : null;
    }

    // ---- next action -----------------------------------------------------------------------------------------------------------

    public sealed record NextAction(string Label, string Href, string Why);

    /// <summary>One next step from stored state: a project → a journey (explore what was discovered, or create one) → a suite for it → readiness →
    /// results.</summary>
    public static NextAction Next(Facts f)
    {
        if (f.ProjectKey is null) return new("Import or choose a project", "project-import", "Journeys and suites belong to a project.");
        if (f.Journeys.Count == 0)
            return (f.Topology?.Connections.Count ?? 0) > 0 || f.Integrations.Count > 0
                ? new("Explore discovered integrations", StageRoute("discover"), "BirkNext found integrations for this environment; choose the path that matters and declare it as a journey.")
                : new("Create journey", DeclareRoute, "No integration evidence was found for this environment yet; declare the journey's systems and steps yourself.");
        if (f.Suites.FirstOrDefault(s => s.Status is SuiteStatus.Blocked or SuiteStatus.NeedsReview) is { } stuck)
            return new("Resolve readiness", SuiteHref(stuck, "readiness"), $"Test suite {stuck.ScopeName} is {SuiteLabel(stuck.Status).ToLowerInvariant()}.");
        if (f.Suites.Count == 0)
            return new("Prepare test suite", StageRoute("prepare", f.Journeys[0].Id), $"Journey {f.Journeys[0].DisplayName} has no test suite from this project's source yet.");
        if (f.Reviews.Count > 0) return new("Review verification results", StageRoute("verify"), "Integration Quality Review has recorded runs for this environment.");
        return new("Review journey", ReviewJourneyHref(f.Journeys[0]), "Check the journey's systems and steps against what was discovered.");
    }

    public static IReadOnlyList<GuidedStep> Steps(Facts f)
    {
        if (f.ProjectKey is null)
            return [.. Stages.Select(s => new GuidedStep(s.Id, s.Title, GuidedStepState.Waiting, "No project", s.Question, Href: s.Route, ActionLabel: "Open"))];
        var connections = f.Topology?.Connections.Count ?? 0;
        return
        [
            new("discover", "Discover & understand", connections + f.Integrations.Count > 0 ? GuidedStepState.Done : GuidedStepState.Current,
                $"{connections} connection{(connections == 1 ? "" : "s")} · {f.Integrations.Count} configured integration{(f.Integrations.Count == 1 ? "" : "s")}",
                "Systems, interfaces and connections from Integration Topology, and the integrations configured for quality review.", Href: Stages[0].Route, ActionLabel: "Open"),
            new("define", "Define & assess", f.Journeys.Count > 0 ? GuidedStepState.Done : connections > 0 ? GuidedStepState.Current : GuidedStepState.Waiting,
                f.Journeys.Count == 0 ? "No journey yet" : $"{f.Journeys.Count} journey{(f.Journeys.Count == 1 ? "" : "s")}",
                "Declare the journey that matters — its systems and steps in order — from discovered components or by hand.", Href: Stages[1].Route, ActionLabel: "Open"),
            new("prepare", "Prepare test suite", f.Suites.Count > 0 ? (f.Suites.Any(s => s.Status is SuiteStatus.Blocked or SuiteStatus.NeedsReview) ? GuidedStepState.Current : GuidedStepState.Done)
                    : f.Journeys.Count > 0 ? GuidedStepState.Current : GuidedStepState.Waiting,
                f.Suites.Count == 0 ? "No suite yet" : $"{f.Suites.Count} suite{(f.Suites.Count == 1 ? "" : "s")}",
                "Bind source snapshots, generate and review the tests, resolve readiness — in Integration Test Suites.", Href: Stages[2].Route, ActionLabel: "Open"),
            new("verify", "Verify & investigate", f.Reviews.Count > 0 ? GuidedStepState.Done : GuidedStepState.Waiting,
                f.Reviews.Count == 0 ? "No quality review yet" : $"{f.Reviews.Count} review run{(f.Reviews.Count == 1 ? "" : "s")}",
                "Static quality review and authorised runs in Integration Quality Review; results keep their evidence type.", Href: Stages[3].Route, ActionLabel: "Open"),
        ];
    }
}
