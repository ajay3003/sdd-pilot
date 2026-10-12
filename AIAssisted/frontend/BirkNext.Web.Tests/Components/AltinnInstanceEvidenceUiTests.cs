using System.Net;
using BirkNext.Integrations;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Read-only Altinn instance evidence in the shared journey panel: typed references (no URL, host, app id or token field), a read-only
/// confirmation, per-scenario run gating (a read can run while submissions are blocked), and observed facts that keep Altinn's storage path
/// apart from MU blobs and never claim a pass.
/// </summary>
public sealed class AltinnInstanceEvidenceUiTests : BunitContext
{
    private readonly FakeJourneys _journeys = new();
    private static readonly FrontendAnalysisProfile Profile = new() { Id = "dev-env", Name = "DEV", EnvironmentType = FrontendEnvironmentType.Development };

    public AltinnInstanceEvidenceUiTests()
    {
        Services.AddSingleton<IIntegrationJourneysApiService>(_journeys);
        Services.AddSingleton(Mock.Of<IReportExportService>());
        Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        Services.AddSingleton(new ActiveEventAuthenticationState(true, "Configured (test)."));
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<IntegrationJourneyPanel> Panel() => Render<IntegrationJourneyPanel>(p => p.Add(c => c.Profile, Profile).Add(c => c.InitialJourneyId, "rapportmottak"));

    [Fact]
    public void TheReadOnlyScenario_TakesOnlyTypedReferences_AndRunsWhileSubmissionsAreBlocked()
    {
        var cut = Panel();
        cut.Find("[data-testid=ijp-start]").HasAttribute("disabled").Should().BeTrue("the selected submission scenario is not runnable");
        cut.Find("[data-testid=ijp-run-note]").TextContent.Should().StartWith("Not runnable");

        cut.Find("[data-testid=ijp-run-scenario]").Change("instance-evidence");
        var inputs = cut.FindAll("[data-testid=ijp-input]");
        inputs.Select(item => item.GetAttribute("data-key")).Should().Equal("instanceOwnerPartyId", "instanceGuid");
        cut.FindAll("[data-testid=ijp-inputs] input").Should().OnlyContain(input => input.GetAttribute("type") == "text" && input.GetAttribute("maxlength") == "64");
        cut.Markup.Should().NotContainEquivalentOf("type=\"url\"").And.NotContainEquivalentOf("token").And.NotContainEquivalentOf("host");

        cut.Find("[data-key=instanceOwnerPartyId] input").Change("50012345");
        cut.Find("[data-key=instanceGuid] input").Change("6a1b2c3d-0000-4000-8000-000000000001");
        cut.Find("[data-testid=ijp-start]").HasAttribute("disabled").Should().BeFalse();
        cut.Find("[data-testid=ijp-start]").Click();
        cut.Find("[data-testid=ijp-confirm-read-only]").TextContent.Should().Contain("Read-only").And.Contain("submits or changes nothing");
    }

    [Fact]
    public void ObservedEvidence_ShowsProcessAndDataFacts_KeepsAltinnStorageApartFromMu_AndNeverSaysPassed()
    {
        var cut = Panel();
        cut.Find("[data-testid=ijp-run-scenario]").Change("instance-evidence");
        cut.Find("[data-key=instanceOwnerPartyId] input").Change("50012345");
        cut.Find("[data-key=instanceGuid] input").Change("6a1b2c3d-0000-4000-8000-000000000001");
        cut.Find("[data-testid=ijp-start]").Click();
        cut.Find("[data-testid=ijp-ack]").Change(true);
        cut.Find("[data-testid=ijp-confirm-run]").Click();

        _journeys.Started.Single().Inputs.Should().BeEquivalentTo(new Dictionary<string, string>
            { ["instanceOwnerPartyId"] = "50012345", ["instanceGuid"] = "6a1b2c3d-0000-4000-8000-000000000001" });
        var lastRun = cut.Find("[data-testid=ijp-last-run]");
        lastRun.GetAttribute("data-state").Should().Be("Partial");
        cut.FindAll("[data-testid=ijp-last-run-step]").Select(row => row.GetAttribute("data-state")).Should().Contain("Observed").And.NotContain("Verified");
        var facts = string.Join(" | ", cut.FindAll("[data-testid=ijp-step-facts]").Select(item => item.TextContent));
        facts.Should().Contain("Current task").And.Contain("Task_1").And.Contain("Can complete task").And.Contain("Altinn storage path (not an MU blob)")
            .And.Contain("buf/skolenaervaer/{guid}/data/{guid}");
        lastRun.TextContent.Should().NotContain("Passed").And.NotContain("50012345").And.Contain("Not submitted");
    }

    private sealed class FakeJourneys : IIntegrationJourneysApiService
    {
        public List<IntegrationJourneyRunRequest> Started { get; } = [];

        private static IntegrationJourneyPackView Pack => new()
        {
            PackId = "skolenaervaer", DisplayName = "Skolenærvær Testing", EnvironmentId = "dev-env", EnvironmentTrusted = true,
            Journeys =
            [
                new IntegrationJourneyView
                {
                    Journey = new IntegrationJourneyDefinition
                    {
                        PackId = "skolenaervaer", JourneyId = "rapportmottak", DisplayName = "Rapportmottak", ExecutionMode = JourneyExecutionMode.JourneyRunner,
                        Steps = [new("altinn-instance", "Altinn instance created", JourneyStepKind.ExternalSubmission, "Altinn", "Instance")],
                        Scenarios =
                        [
                            new("valid-report", "E2E-01 / MU-01 · Valid report", "Typed report.", JourneyScenarioSupport.Supported),
                            new("instance-evidence", "Altinn instance evidence (read-only)", "Reads one instance from Altinn Storage.", JourneyScenarioSupport.Supported, StateChanging: false,
                                Inputs:
                                [
                                    new("instanceOwnerPartyId", "Instance owner party id", JourneyScenarioInputKind.Reference, true, "Never stored."),
                                    new("instanceGuid", "Instance GUID", JourneyScenarioInputKind.Reference, true, "Short reference only."),
                                ]),
                        ],
                    },
                    Readiness = JourneyReadinessState.Partial, CanRun = true, RunnableScenarios = ["instance-evidence"],
                    ReadinessSummary = "8 of 11 mandatory prerequisites are ready. Runnable now: Altinn instance evidence (read-only).",
                },
            ],
        };

        public Task<ActiveEventApiResult<IReadOnlyList<IntegrationJourneyPackView>>> PacksAsync(string environmentId, CancellationToken ct = default) =>
            Task.FromResult(new ActiveEventApiResult<IReadOnlyList<IntegrationJourneyPackView>>([Pack], null, HttpStatusCode.OK));
        public Task<ActiveEventApiResult<ArchitectureRuleReport>> RulesAsync(string environmentId, string packId, CancellationToken ct = default) =>
            Task.FromResult(new ActiveEventApiResult<ArchitectureRuleReport>(null, "n/a", HttpStatusCode.NotFound));
        public Task<ActiveEventApiResult<IntegrationJourneyRun>> StartAsync(IntegrationJourneyRunRequest request, CancellationToken ct = default)
        {
            Started.Add(request);
            return Task.FromResult(new ActiveEventApiResult<IntegrationJourneyRun>(new IntegrationJourneyRun
            {
                RunId = Guid.NewGuid(), PackId = request.PackId, JourneyId = request.JourneyId, ScenarioId = request.ScenarioId, OverallState = JourneyRunState.Partial,
                StateReason = "0 of 15 mandatory steps verified; the rest were accepted/observed only or not verified.",
                InputSummary = ["Altinn instance inst-6a1b2c3d (read-only Storage read; the party id is not stored)."],
                Steps =
                [
                    new() { StepId = "altinn-validate", Label = "Altinn app validation", State = JourneyStepState.Observed, Evidence = "HTTP 200 · can complete task: yes",
                        Reason = "Altinn recorded a validation of the current task. Observed only.",
                        Facts = [new("Current task", "Task_1"), new("Altinn task type", "data"), new("Can complete task", "yes")] },
                    new() { StepId = "altinn-data", Label = "Report form data stored", State = JourneyStepState.Observed, Evidence = "HTTP 200 · 1 data element(s)",
                        Reason = "Data elements observed (metadata only).",
                        Facts = [new("data-7b2c3d4e · data type", "model"), new("data-7b2c3d4e · Altinn storage path (not an MU blob)", "buf/skolenaervaer/{guid}/data/{guid}")] },
                    new() { StepId = "altinn-submit", Label = "Report submitted in Altinn", State = JourneyStepState.NotVerified, Evidence = "HTTP 200 · process not ended",
                        Reason = "The process has not ended; current task: Task_1. Not submitted." },
                ],
            }, null, HttpStatusCode.OK));
        }
        public Task<ActiveEventApiResult<IntegrationJourneyRun>> GetAsync(Guid runId, CancellationToken ct = default) =>
            Task.FromResult(new ActiveEventApiResult<IntegrationJourneyRun>(null, "Not found.", HttpStatusCode.NotFound));
        public Task<ActiveEventApiResult<IReadOnlyList<IntegrationJourneyRunSummary>>> HistoryAsync(string environmentId, string packId, string? journeyId, JourneyRunState? state, CancellationToken ct = default) =>
            Task.FromResult(new ActiveEventApiResult<IReadOnlyList<IntegrationJourneyRunSummary>>([], null, HttpStatusCode.OK));
    }
}
