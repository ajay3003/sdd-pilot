using System.Text.Json;
using System.Text.RegularExpressions;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;
using BirkNext.Api.Tests.Services.ActiveEventTesting;
using BirkNext.Integrations;
using BirkNext.SourceArchitecture;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.IntegrationJourneys;

/// <summary>
/// Skolenærvær Testing journeys on the shared engine. The evidence BirkNext has today contains no MU, MM, Skolenærværsrapport, Bufdata or
/// Altinn source, contract, executor or observer, so every journey must be truthfully not runnable — without disabling the other journeys.
/// </summary>
public sealed class SkoleNaervaerJourneyPackTests
{
    private static SkoleNaervaerJourneyPack Pack(IntegrationJourneyHarness harness) => new(harness.MessageFlows);

    private static IntegrationDefinition AltinnIntake => new()
    {
        Id = "dev:http:altinn-skolenaervaer", EnvironmentId = ActiveEventTestHarness.Env, DisplayName = "Skolenærvær – Altinn report intake", Kind = IntegrationKind.HttpApi,
        Enabled = true, Producer = "Altinn (TT02)", Consumer = new IntegrationConsumer { DisplayName = "MU" },
    };

    private static IntegrationDefinition PeriodClosed => new()
    {
        Id = "dev:servicebus:skolenaervaer-periode-lukket", EnvironmentId = ActiveEventTestHarness.Env, DisplayName = "Skolenærvær – Period Closed", Kind = IntegrationKind.ServiceBus,
        Enabled = true, EndpointOrTopic = "SkolenaervaersperiodeLukket", Producer = "Skolenærværsrapport", Consumer = new IntegrationConsumer { DisplayName = "Bufdata" },
    };

    private static IntegrationDefinition EducationCdc => new()
    {
        Id = ActiveEventTestHarness.SkoleIntegrationId, EnvironmentId = ActiveEventTestHarness.Env, PlatformId = "dev:eventhub:m2lb", DisplayName = "BIRK Utdanning CDC",
        Kind = IntegrationKind.EventHub, Enabled = true, SourceResource = "BirkM2LB.dbo.Utdanning", EndpointOrTopic = ActiveEventTestHarness.SkoleHub,
        Consumer = new IntegrationConsumer { DisplayName = "SkoleAdapter" },
    };

    [Fact]
    public void Pack_ModelsThreeSeparateJourneys_WithResponsibilitiesFromTheArchitecture()
    {
        var pack = new SkoleNaervaerJourneyPack(new IntegrationJourneyHarness.FakeMessageFlows());
        _ = new IntegrationJourneyPackRegistry([pack]);
        pack.Journeys.Select(journey => journey.JourneyId).Should().Equal("utdanningsdata", "rapportmottak", "periodeavslutning");

        var cdc = pack.Journeys[0];
        cdc.ExecutionMode.Should().Be(JourneyExecutionMode.DelegatedToActiveEvent, "the existing Active Event provider executes the CDC journey");
        cdc.DelegatedProviderId.Should().Be("skolenaervaer.cdc");
        cdc.Steps.Single(step => step.StepId == "utdanning-person").Owner.Should().Be("Utdanning");
        cdc.Steps.Should().NotContain(step => step.Owner == "SkoleAdapter" && step.Kind == JourneyStepKind.IdentityLookup);
        cdc.Steps.Should().NotContain(step => step.Owner == "Altinn", "Altinn is not part of the CDC flow");

        var report = pack.Journeys[1];
        report.Steps.Select(step => step.Owner).Distinct().Should().Equal("External reporter", "Altinn", "MU", "MM", "Skolenærværsrapport");
        report.Steps.Single(step => step.Kind == JourneyStepKind.StructuralValidation).Owner.Should().Be("MM", "XSD validation belongs to the MM intake path");
        report.Steps.Single(step => step.Kind == JourneyStepKind.IdentityLookup).Owner.Should().Be("Skolenærværsrapport");
        report.Steps.Should().NotContain(step => step.Owner == "MM" && step.Kind == JourneyStepKind.IdentityLookup);

        var closure = pack.Journeys[2];
        closure.Steps.Single(step => step.Kind == JourneyStepKind.Publication).Label.Should().Contain("SkolenaervaersperiodeLukket");
        closure.Steps.Select(step => step.StepId).Should().ContainInOrder("publish", "broker", "bufdata-activity", "bufdata-processed", "dataset-verify", "dataset-pii");
    }

    [Fact]
    public void UnsupportedScenarios_AreNotAssessed_WithAReason_AndNoneIsInvented()
    {
        var pack = new SkoleNaervaerJourneyPack(new IntegrationJourneyHarness.FakeMessageFlows());
        var report = pack.Journeys.Single(journey => journey.JourneyId == "rapportmottak");
        report.Scenarios.Where(scenario => scenario.Support == JourneyScenarioSupport.Supported).Select(scenario => scenario.ScenarioId).Should().Equal("valid-report");
        report.Scenarios.Where(scenario => scenario.Support != JourneyScenarioSupport.Supported).Should().OnlyContain(scenario => !string.IsNullOrWhiteSpace(scenario.NotAssessedBecause));
        report.Scenarios.Select(scenario => scenario.ScenarioId).Should().Contain(["invalid-xsd", "person-not-found", "period-locked", "duplicate-report", "retrieval-decrypt-failure"]);
        pack.Journeys.Single(journey => journey.JourneyId == "periodeavslutning").Scenarios.Single(scenario => scenario.ScenarioId == "pii-absent")
            .NotAssessedBecause.Should().StartWith("Not verified");
    }

    [Fact]
    public async Task WithoutConfiguration_EveryJourneyIsNotConfigured_AndNothingCanRun()
    {
        await using var harness = new IntegrationJourneyHarness();
        var pack = (await harness.Service(Pack(harness)).PacksAsync(ActiveEventTestHarness.Env, default)).Single();

        pack.Journeys.Should().OnlyContain(view => view.Readiness == JourneyReadinessState.NotConfigured && !view.CanRun);
        pack.Journeys.Single(view => view.Journey.JourneyId == "rapportmottak").Prerequisites.Single(item => item.Key == "integration:report-intake")
            .Detail.Should().Contain("No configured HttpApi integration was found for Skolenærvær – Altinn report intake");
        pack.Journeys.Single(view => view.Journey.JourneyId == "utdanningsdata").Prerequisites.Single(item => item.Key == "source:SkoleAdapter")
            .Detail.Should().StartWith("Not source verified");
    }

    [Fact]
    public async Task ConfiguredIqrIntegrations_AreReused_ButReadinessStaysTruthfullyBlocked_PerJourney()
    {
        await using var harness = new IntegrationJourneyHarness();
        harness.Active.Catalog.Extra.AddRange([AltinnIntake, PeriodClosed, EducationCdc]);
        harness.Enroll(AltinnIntake.Id);
        harness.Enroll(PeriodClosed.Id);
        var views = (await harness.Service(Pack(harness)).PacksAsync(ActiveEventTestHarness.Env, default)).Single().Journeys.ToDictionary(view => view.Journey.JourneyId);

        var report = views["rapportmottak"];
        report.MatchedIntegrations.Should().ContainSingle(item => item.IntegrationId == AltinnIntake.Id);
        report.Prerequisites.Single(item => item.Key == "integration:report-intake").State.Should().Be(JourneyReadinessState.Ready);
        report.Prerequisites.Single(item => item.Key == "altinn-auth").Detail.Should().StartWith("Authentication not configured");
        report.Prerequisites.Single(item => item.Key == "submission-contract").State.Should().Be(JourneyReadinessState.NotReady);
        report.Prerequisites.Single(item => item.Key == "executor").State.Should().Be(JourneyReadinessState.NotAvailable);
        report.Prerequisites.Single(item => item.Key == "source:MM").Detail.Should().StartWith("Not source verified");
        report.Readiness.Should().Be(JourneyReadinessState.Partial);
        report.CanRun.Should().BeFalse();
        report.Maturity.Configured.Should().BeTrue();
        report.Maturity.SourceVerified.Should().BeFalse();

        var closure = views["periodeavslutning"];
        closure.MatchedIntegrations.Should().ContainSingle(item => item.IntegrationId == PeriodClosed.Id);
        closure.Prerequisites.Single(item => item.Key == "domain-trigger").State.Should().Be(JourneyReadinessState.NotReady);
        closure.Prerequisites.Single(item => item.Key == "event-contract").Detail.Should().Contain("not source verified");
        closure.CanRun.Should().BeFalse();

        var cdc = views["utdanningsdata"];
        cdc.MatchedIntegrations.Should().ContainSingle(item => item.IntegrationId == EducationCdc.Id);
        cdc.Prerequisites.Should().Contain(item => item.Key.StartsWith("active-event:", StringComparison.Ordinal), "CDC readiness is taken from the Active Event provider");
        cdc.CanRun.Should().BeFalse("delegated journeys run from Active Event Testing");
    }

    [Fact]
    public async Task StartingAJourney_IsBlockedAndRecorded_AndDelegatedJourneysAreRefused()
    {
        await using var harness = new IntegrationJourneyHarness();
        harness.Active.Catalog.Extra.AddRange([AltinnIntake, PeriodClosed]);
        harness.Enroll(AltinnIntake.Id);
        harness.Enroll(PeriodClosed.Id);
        var service = harness.Service(Pack(harness));

        var run = await service.StartAsync(new IntegrationJourneyRunRequest
        {
            EnvironmentId = ActiveEventTestHarness.Env, PackId = "skolenaervaer", JourneyId = "rapportmottak", ScenarioId = "valid-report", Confirmed = true,
        }, default);
        run.OverallState.Should().Be(JourneyRunState.Blocked);
        run.StateReason.Should().Contain("Authentication not configured").And.Contain("Submission contract");
        run.Steps.Should().OnlyContain(step => step.State == JourneyStepState.NotAssessed);

        (await service.StartAsync(new IntegrationJourneyRunRequest
        {
            EnvironmentId = ActiveEventTestHarness.Env, PackId = "skolenaervaer", JourneyId = "periodeavslutning", ScenarioId = "close-valid-period", Confirmed = true,
        }, default)).StateReason.Should().Contain("Safe period-closure trigger");

        await service.Invoking(item => item.StartAsync(new IntegrationJourneyRunRequest
        {
            EnvironmentId = ActiveEventTestHarness.Env, PackId = "skolenaervaer", JourneyId = "utdanningsdata", ScenarioId = "active-event-scenarios", Confirmed = true,
        }, default)).Should().ThrowAsync<IntegrationJourneyRequestException>().WithMessage("*Active Event Testing*");

        (await service.HistoryAsync(new IntegrationJourneyHistoryQuery(ActiveEventTestHarness.Env, "skolenaervaer"), default))
            .Should().HaveCount(2).And.OnlyContain(item => item.State == JourneyRunState.Blocked);
    }

    [Fact]
    public async Task SourceAnalysisEvidence_IsReused_ForComponentsAndThePeriodClosedEventContract()
    {
        await using var harness = new IntegrationJourneyHarness();
        harness.Sources.Snapshots.Add(new IqrSourceSnapshot
        {
            Archive = new("skolenaervaer.zip", new string('b', 64), 10),
            Architecture = new ArchitectureSnapshot
            {
                Components = [new() { Id = "c-mm", Name = "Skolenaervaer.MM.Worker" }, new() { Id = "c-rapport", Name = "Skolenaervaersrapport.Api" }],
                MessagingChannels = [new() { Id = "ch-1", Name = "SkolenaervaersperiodeLukket", Type = MessagingChannelType.ServiceBusTopic }],
            },
        });
        var views = (await harness.Service(Pack(harness)).PacksAsync(ActiveEventTestHarness.Env, default)).Single().Journeys.ToDictionary(view => view.Journey.JourneyId);

        views["rapportmottak"].Prerequisites.Single(item => item.Key == "source:MM").State.Should().Be(JourneyReadinessState.Ready);
        views["rapportmottak"].Prerequisites.Single(item => item.Key == "source:MU").State.Should().Be(JourneyReadinessState.NotReady);
        views["periodeavslutning"].Prerequisites.Single(item => item.Key == "event-contract").Detail.Should().StartWith("Source verified");
    }

    [Fact]
    public void PackDefinitions_ContainNoPersonalData()
    {
        var json = JsonSerializer.Serialize(new SkoleNaervaerJourneyPack(new IntegrationJourneyHarness.FakeMessageFlows()).Journeys);
        Regex.IsMatch(json, @"\b\d{11}\b").Should().BeFalse("no national identity numbers");
        Regex.IsMatch(json, @"\b\d{6,}\b").Should().BeFalse("no identifiers or account numbers at all");
        json.Should().NotContain("@", "no contact data");
    }
}
