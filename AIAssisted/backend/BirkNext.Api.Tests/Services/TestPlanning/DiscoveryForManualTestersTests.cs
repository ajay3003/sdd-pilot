using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Services.SystemUnderstanding;
using BirkNext.Api.Services.TestPlanning;
using BirkNext.CriticalE2E;
using BirkNext.Integrations;
using BirkNext.SystemUnderstanding;
using BirkNext.TestCoverage;
using BirkNext.TestEvidence;
using BirkNext.TestPlanning;
using FluentAssertions;
using Xunit;
using D = BirkNext.Api.Services.TestPlanning.TestScenarioDiscoveryEngine;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// Discover test scenarios offers what a manual tester can test (2026-11-22, from the Tiltak 2 / MU archive): framework pages and developer/demo
/// tools are not candidates, one part of a cross-system guide gets checks for its own guide steps, a pack recognised through one source offers that
/// source's journeys only, and developer unit tests are explained instead of shown as an empty source. M2LB's guide scenarios are unchanged.
/// </summary>
public sealed class DiscoveryForManualTestersTests
{
    private static SourceUiPage Page(string module, string file, params string[] routes) => new() { Module = module, File = file, Routes = [.. routes] };

    [Fact]
    public void Framework_pages_and_developer_tools_are_not_manual_test_candidates_and_application_pages_are()
    {
        D.IsFrameworkPage(Page("App", "Pages/Error.razor", "/Error")).Should().BeTrue();
        D.IsFrameworkPage(Page("App", "Pages/NotFound.razor", "/not-found")).Should().BeTrue();
        D.IsFrameworkPage(Page("App", "Pages/Authentication.razor", "/authentication/{action}")).Should().BeTrue();
        D.IsFrameworkPage(Page("App", "Pages/Orders.razor", "/orders")).Should().BeFalse();
        D.IsFrameworkPage(Page("App", "Pages/Errors.razor", "/error", "/orders/errors")).Should().BeFalse("a page that also serves an application route stays");

        D.IsDeveloperToolPage(Page("Buf.Meldingsutveksler.Demo.DebugPanel", "Buf.Meldingsutveksler.Demo.DebugPanel/Components/Pages/Home.razor", "/")).Should().BeTrue();
        D.IsDeveloperToolPage(Page("Frontend", "samples/Playground/Index.razor", "/")).Should().BeTrue();
        D.IsDeveloperToolPage(Page("Frontend", "Frontend/src/M2LB.Frontend.Web/Modules/Admin/Pages/AuditLogPage.razor", "/admin/audit-log")).Should().BeFalse();
        D.IsDeveloperToolPage(Page("Frontend", "Frontend/src/M2LB.Frontend.Web/Modules/Person/Pages/BarnSearchPage.razor", "/person")).Should().BeFalse();
    }

    [Fact]
    public async Task A_repository_whose_only_routed_pages_are_a_debug_panel_offers_no_UI_candidates_and_says_why()
    {
        var snapshot = new IqrSourceSnapshot
        {
            UiSurface = new SourceUiSurface { Pages = [Page("Buf.Meldingsutveksler.Demo.DebugPanel", "Buf.Meldingsutveksler.Demo.DebugPanel/Components/Pages/Home.razor", "/"),
                Page("Buf.Meldingsutveksler.Demo.DebugPanel", "Buf.Meldingsutveksler.Demo.DebugPanel/Components/Pages/Error.razor", "/Error")] },
        };
        var result = await new UiSourceScenarioProvider().DiscoverAsync(new ScenarioDiscoveryContext("p", "p", snapshot, null, null), default);
        result.Candidates.Should().BeEmpty();
        result.Capability.State.Should().Be(DiscoveryCapabilityState.NoEvidence);
        result.Capability.Detail.Should().StartWith("No application page to test").And.Contain("developer or demo tools");
    }

    private static SystemGuideRevision Guide() =>
        new() { GuideKey = "skolenaervaer", Revision = 1, State = GuideRevisionState.Draft, Content = SystemGuideService.LoadSeed("SystemGuide.skolenaervaer.guide.json") };

    [Fact]
    public void The_MU_part_of_the_Skolenaervaer_guide_gets_checks_for_its_own_steps()
    {
        var mu = D.FromGuide("tiltak-2", Guide(), new HashSet<string> { "mu" });
        mu.Should().HaveCount(4, "no guide scenario lies in MU; its own steps 03–06 are offered as checks");
        mu.Should().OnlyContain(c => c.CanonicalKey.StartsWith("guide:skolenaervaer/step/") && c.Title.StartsWith("MU "));
        mu.Should().OnlyContain(c => c.ExpectedBehaviour != null && c.Steps.Count >= 1 && c.OpenQuestions.Count == 1);
        mu.Single(c => c.CanonicalKey.EndsWith("/05")).Steps.Should().HaveCountGreaterThan(1, "what can go wrong becomes checks of its own");
    }

    [Fact]
    public void M2LB_keeps_every_guide_scenario_and_only_gains_checks_for_its_uncovered_steps()
    {
        var guide = Guide();
        var m2lb = D.FromGuide("m2lb", guide, new HashSet<string> { "m2lb" });
        m2lb.Where(c => c.CanonicalKey.StartsWith("guide:skolenaervaer/") && !c.CanonicalKey.Contains("/step/")).Should().HaveCount(guide.Content.Scenarios.Count);
        var steps = m2lb.Where(c => c.CanonicalKey.Contains("/step/")).Select(c => c.CanonicalKey.Split('/').Last()).ToList();
        steps.Should().OnlyContain(id => guide.Content.Steps.Single(s => s.Id == id).Evidence.Any(e => e.SourceRole == "m2lb"));
        steps.Should().NotContain(guide.Content.Scenarios.SelectMany(s => s.StepIds), "a step a scenario covers is never offered twice");
        D.FromGuide("any", guide).Should().HaveCount(guide.Content.Scenarios.Count, "without recognised roles nothing changes");
    }

    private sealed class Registry(IIntegrationJourneyPack pack) : IIntegrationJourneyPackRegistry
    {
        public IReadOnlyList<IIntegrationJourneyPack> Packs => [pack];
        public IIntegrationJourneyPack? Find(string packId) => packId == pack.PackId ? pack : null;
    }

    private sealed class HubPack : IIntegrationJourneyPack
    {
        public string PackId => "demo.hub";
        public string PackVersion => "1";
        public string DisplayName => "Hub";
        public string Description => "A hub and a downstream system.";
        public IReadOnlyList<string> Limitations => [];
        public IReadOnlyList<IntegrationJourneyDefinition> Journeys { get; } =
        [
            new() { PackId = "demo.hub", JourneyId = "intake", DisplayName = "Intake", Order = 1, ExecutionMode = JourneyExecutionMode.JourneyRunner,
                Steps = [new("receive", "Hub receives", JourneyStepKind.ExternalSubmission, "Hub", "Accepted")], Scenarios = [new("valid", "Valid", "A valid message.", JourneyScenarioSupport.Supported)] },
            new() { PackId = "demo.hub", JourneyId = "closing", DisplayName = "Closing", Order = 2, ExecutionMode = JourneyExecutionMode.JourneyRunner,
                Steps = [new("close", "Downstream closes", JourneyStepKind.ExternalSubmission, "Downstream", "Closed")], Scenarios = [new("close", "Close", "A period closes.", JourneyScenarioSupport.Supported)] },
        ];
        public IReadOnlyList<TestPackSourceRole> SourceRoles { get; } =
        [
            new("hub", "Hub", "The hub") { ComponentTokens = ["Hub"], Journeys = ["intake"] },
            new("downstream", "Downstream", "The downstream system") { ComponentTokens = ["Downstream"], Journeys = ["intake", "closing"] },
        ];
        public IReadOnlyList<ArchitectureResponsibilityRule> Rules => [];
        public bool Matches(JourneyIntegrationRequirement requirement, IntegrationDefinition integration) => false;
        public Task<IReadOnlyList<JourneyPrerequisite>> PrerequisitesAsync(IntegrationJourneyDefinition journey, JourneyPackContext context, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<JourneyPrerequisite>>([]);
    }

    private static IqrSourceSnapshot Source(string project) =>
        new() { Projects = [new SourceProject(project, $"src/{project}/{project}.csproj", false, "Application", SourceConfidence.Confirmed)] };

    [Fact]
    public async Task A_pack_recognised_through_one_source_offers_only_the_journeys_that_source_takes_part_in()
    {
        var provider = new IntegrationJourneyScenarioProvider(packs: new Registry(new HubPack()));
        var hub = await provider.DiscoverAsync(new ScenarioDiscoveryContext("p", "p", Source("Company.Hub.Service"), null, null), default);
        hub.Candidates.Select(c => c.Area).Should().Equal("Intake");
        var downstream = await provider.DiscoverAsync(new ScenarioDiscoveryContext("p", "p", Source("Company.Downstream.Api"), null, null), default);
        downstream.Candidates.Select(c => c.Area).Should().BeEquivalentTo(["Intake", "Closing"], "a source that takes part in every journey keeps them all");
    }

    [Fact]
    public async Task Developer_unit_tests_are_explained_when_the_project_has_no_specification_requirements()
    {
        var snapshot = new IqrSourceSnapshot { TestInventory = new SourceTestInventory { Definitions = [new SourceTestDefinition { TestDefinitionId = "t1" }] } };
        var none = await new ExistingTestScenarioProvider().DiscoverAsync(new ScenarioDiscoveryContext("p", "p", snapshot, new CoverageWorkspaceEvidence(), null), default);
        none.Capability.State.Should().Be(DiscoveryCapabilityState.NotApplicable);
        none.Capability.Detail.Should().StartWith("1 automated developer tests found. They are not manual test cases");
        none.Candidates.Should().BeEmpty();
    }
}
