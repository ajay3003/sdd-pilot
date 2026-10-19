using System.Text.Json;
using System.Text.RegularExpressions;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Tests.Services.AltinnTesting;
using BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;
using BirkNext.Api.Tests.Services.ActiveEventTesting;
using BirkNext.Integrations;
using BirkNext.SourceArchitecture;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.IntegrationJourneys;

/// <summary>
/// Skolenærvær Testing journeys on the shared engine. The evidence BirkNext has contains no MU, MM, Skolenærværsrapport or Bufdata source,
/// contract or observer; Rapportmottak's Altinn steps become runnable only with a complete app contract and configuration (see
/// AltinnTestingTests). Without them every journey is truthfully not runnable — without disabling the other journeys.
/// </summary>
public sealed class SkoleNaervaerJourneyPackTests
{
    private static SkoleNaervaerJourneyPack Pack(IntegrationJourneyHarness harness)
    {
        var kit = new AltinnTestKit();
        return new(harness.MessageFlows, kit.Policy, kit.Contracts(harness.MessageFlows, harness.Sources));
    }

    private static SkoleNaervaerJourneyPack Pack() => Pack(new IntegrationJourneyHarness());

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
        var pack = Pack();
        _ = new IntegrationJourneyPackRegistry([pack]);
        pack.Journeys.Select(journey => journey.JourneyId).Should().Equal("utdanningsdata", "rapportmottak", "periodeavslutning");

        var cdc = pack.Journeys[0];
        cdc.ExecutionMode.Should().Be(JourneyExecutionMode.DelegatedToActiveEvent, "the existing Active Event provider executes the CDC journey");
        cdc.DelegatedProviderId.Should().Be("skolenaervaer.cdc");
        cdc.Steps.Single(step => step.StepId == "utdanning-person").Owner.Should().Be("Utdanning");
        cdc.Steps.Should().NotContain(step => step.Owner == "SkoleAdapter" && step.Kind == JourneyStepKind.IdentityLookup);
        cdc.Steps.Should().NotContain(step => step.Owner == "Altinn", "Altinn is not part of the CDC flow");

        var report = pack.Journeys[1];
        report.Steps.Select(step => step.Owner).Distinct().Should().Equal("Synthetic reporter (TestID)", "Altinn", "MU", "MM", "Skolenærværsrapport");
        report.Steps.Select(step => step.StepId).Should().ContainInOrder("altinn-auth", "altinn-instance", "altinn-data", "altinn-validate", "altinn-submit", "altinn-receipt", "mu-receive");
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
        var pack = Pack();
        var report = pack.Journeys.Single(journey => journey.JourneyId == "rapportmottak");
        report.Scenarios.Where(scenario => scenario.Support == JourneyScenarioSupport.Supported).Select(scenario => scenario.ScenarioId).Should().Equal("valid-report", "person-not-found", "overlapping-report", "instance-evidence", "message-verification");
        report.Scenarios.Where(scenario => scenario.Support != JourneyScenarioSupport.Supported).Should().OnlyContain(scenario => !string.IsNullOrWhiteSpace(scenario.NotAssessedBecause));
        report.Scenarios.Select(scenario => scenario.ScenarioId).Should().Contain(["correction", "multiple-reporters", "invalid-xsd", "period-locked", "duplicate-report", "retrieval-decrypt-failure"]);
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
        report.Prerequisites.Single(item => item.Key == "altinn-auth").Detail.Should().Contain("Authentication type is not ID-porten");
        report.Prerequisites.Single(item => item.Key == "reporter-auth").State.Should().Be(JourneyReadinessState.NotConfigured);
        report.Prerequisites.Single(item => item.Key == "submission-contract").State.Should().Be(JourneyReadinessState.NotReady);
        report.Prerequisites.Single(item => item.Key == "executor").State.Should().Be(JourneyReadinessState.NotAvailable, "no Altinn executor is registered in this harness");
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
            Inputs = new Dictionary<string, string>
            {
                ["reporter"] = "interactive-session", ["identitySource"] = "registered", ["childIdentifier"] = SyntheticPersonIdentifier.Generate(), ["periodFrom"] = "2026-10-01", ["periodTo"] = "2026-10-31",
                ["plannedHours"] = "100", ["actualHours"] = "90",
            },
        }, null, default);
        run.OverallState.Should().Be(JourneyRunState.Blocked);
        run.StateReason.Should().Contain("Authentication type is not ID-porten").And.Contain("Altinn app contract");
        run.Steps.Should().OnlyContain(step => step.State == JourneyStepState.NotAssessed);

        (await service.StartAsync(new IntegrationJourneyRunRequest
        {
            EnvironmentId = ActiveEventTestHarness.Env, PackId = "skolenaervaer", JourneyId = "periodeavslutning", ScenarioId = "close-valid-period", Confirmed = true,
        }, null, default)).StateReason.Should().Contain("Safe period-closure trigger");

        await service.Invoking(item => item.StartAsync(new IntegrationJourneyRunRequest
        {
            EnvironmentId = ActiveEventTestHarness.Env, PackId = "skolenaervaer", JourneyId = "utdanningsdata", ScenarioId = "active-event-scenarios", Confirmed = true,
        }, null, default)).Should().ThrowAsync<IntegrationJourneyRequestException>().WithMessage("*Active Event Testing*");

        (await service.HistoryAsync(new IntegrationJourneyHistoryQuery(ActiveEventTestHarness.Env, "skolenaervaer"), default))
            .Should().HaveCount(2).And.OnlyContain(item => item.State == JourneyRunState.Blocked);
    }

    [Fact]
    public async Task SourceAnalysisEvidence_IsReused_ForComponentsAndThePeriodClosedEventContract()
    {
        await using var harness = new IntegrationJourneyHarness();
        var snapshot = new IqrSourceSnapshot
        {
            Id = Guid.NewGuid(), Archive = new("skolenaervaer.zip", new string('b', 64), 10),
            Architecture = new ArchitectureSnapshot
            {
                Components = [new() { Id = "c-mm", Name = "Skolenaervaer.MM.Worker" }, new() { Id = "c-rapport", Name = "Skolenaervaersrapport.Api" }],
                MessagingChannels = [new() { Id = "ch-1", Name = "SkolenaervaersperiodeLukket", Type = MessagingChannelType.ServiceBusTopic }],
            },
        };
        harness.Sources.Snapshots.Add(snapshot);
        async Task<Dictionary<string, IntegrationJourneyView>> ViewsAsync() =>
            (await harness.Service(Pack(harness)).PacksAsync(ActiveEventTestHarness.Env, default)).Single().Journeys.ToDictionary(view => view.Journey.JourneyId);

        // Stored but not bound: found, named, never source verified.
        var unbound = await ViewsAsync();
        unbound["rapportmottak"].Prerequisites.Single(item => item.Key == "source:MM").State.Should().Be(JourneyReadinessState.Partial);
        unbound["periodeavslutning"].Prerequisites.Single(item => item.Key == "event-contract").State.Should().Be(JourneyReadinessState.Partial);
        unbound["periodeavslutning"].Prerequisites.Single(item => item.Key == "event-contract").Detail.Should().StartWith("Not source verified").And.Contain("not bound");
        unbound["periodeavslutning"].Maturity.SourceVerified.Should().BeFalse("an unbound snapshot never makes a journey source verified");

        // Bound in IQR: the same evidence is now authoritative.
        harness.Bindings.Bind("any-integration", snapshot);
        var bound = await ViewsAsync();
        bound["rapportmottak"].Prerequisites.Single(item => item.Key == "source:MM").State.Should().Be(JourneyReadinessState.Ready);
        bound["rapportmottak"].Prerequisites.Single(item => item.Key == "source:MU").State.Should().Be(JourneyReadinessState.NotReady);
        bound["periodeavslutning"].Prerequisites.Single(item => item.Key == "event-contract").Detail.Should().StartWith("Source verified").And.Contain("bound in Integration Quality Review");
    }

    [Fact]
    public async Task SourceEvidence_CountsEverySnapshot_SoSeparatelyUploadedRepositoriesAddUp()
    {
        await using var harness = new IntegrationJourneyHarness();
        IqrSourceSnapshot Repo(string archive, char hash, params string[] components) => new()
        {
            Id = Guid.NewGuid(), Archive = new(archive, new string(hash, 64), 10),
            Architecture = new ArchitectureSnapshot { Components = [.. components.Select((name, index) => new ArchitectureComponent { Id = $"{archive}-{index}", Name = name })] },
        };
        harness.Sources.Snapshots.Add(Repo("meldingsutveksleren.zip", 'a', "Meldingsutveksleren.Api"));
        harness.Sources.Snapshots.Add(Repo("meldingsutvekslermottak.zip", 'c', "M2LB.Meldingsutvekslermottak.Host"));
        var report = (await harness.Service(Pack(harness)).PacksAsync(ActiveEventTestHarness.Env, default)).Single().Journeys.Single(view => view.Journey.JourneyId == "rapportmottak");

        report.Prerequisites.Single(item => item.Key == "source:MU").Detail.Should().Be("Not source verified: component Meldingsutveksleren.Api is in snapshot meldingsutveksleren.zip, which is not bound in Integration Quality Review → Evidence.");
        report.Prerequisites.Single(item => item.Key == "source:MM").State.Should().Be(JourneyReadinessState.Partial,
            "MM's own project name counts and it is found in its own archive, but an unbound snapshot is never source verified");
        report.Prerequisites.Single(item => item.Key == "source:Skolenærværsrapport").State.Should().Be(JourneyReadinessState.NotReady, "nothing is matched by a shared word such as 'Skole'");

        // Separately uploaded repositories add up once each is bound (one binding per integration).
        harness.Bindings.Bind("integration-a", harness.Sources.Snapshots[0]);
        harness.Bindings.Bind("integration-b", harness.Sources.Snapshots[1]);
        var bound = (await harness.Service(Pack(harness)).PacksAsync(ActiveEventTestHarness.Env, default)).Single().Journeys.Single(view => view.Journey.JourneyId == "rapportmottak");
        bound.Prerequisites.Single(item => item.Key == "source:MU").Detail.Should().Be("Source verified: component Meldingsutveksleren.Api in snapshot meldingsutveksleren.zip (bound in Integration Quality Review).");
        bound.Prerequisites.Single(item => item.Key == "source:MM").Detail.Should().Be("Source verified: component M2LB.Meldingsutvekslermottak.Host in snapshot meldingsutvekslermottak.zip (bound in Integration Quality Review).");
    }

    [Fact]
    public async Task SourceEvidence_PrefersTheSnapshotBoundInIqr_AndSaysSo_WithoutChangingReadiness()
    {
        await using var harness = new IntegrationJourneyHarness();
        IqrSourceSnapshot Repo(string archive, params string[] components) => new()
        {
            Id = Guid.NewGuid(), Archive = new(archive, new string('d', 64), 10),
            Architecture = new ArchitectureSnapshot { Components = [.. components.Select((name, index) => new ArchitectureComponent { Id = $"{archive}-{index}", Name = name })] },
        };
        var unbound = Repo("newer-unbound.zip", "SkoleAdapter.Worker");
        var bound = Repo("bound.zip", "SkoleAdapter.Worker");
        harness.Sources.Snapshots.AddRange([unbound, bound]);
        harness.Bindings.Bind("any-integration", bound);
        var view = (await harness.Service(Pack(harness)).PacksAsync(ActiveEventTestHarness.Env, default)).Single().Journeys.Single(item => item.Journey.JourneyId == "utdanningsdata");
        var adapter = view.Prerequisites.Single(item => item.Key == "source:SkoleAdapter");
        adapter.State.Should().Be(JourneyReadinessState.Ready);
        adapter.Detail.Should().Be("Source verified: component SkoleAdapter.Worker in snapshot bound.zip (bound in Integration Quality Review).");
    }

    [Fact]
    public async Task PeriodClosure_IsOperatorOnly_NoScenarioClaimsToRun_AndTheReasonStatesTheVerifiedLockLifecycle()
    {
        await using var harness = new IntegrationJourneyHarness();
        harness.Active.Catalog.Extra.Add(PeriodClosed);
        harness.Enroll(PeriodClosed.Id);
        var service = harness.Service(Pack(harness));
        var closure = (await service.PacksAsync(ActiveEventTestHarness.Env, default)).Single().Journeys.Single(view => view.Journey.JourneyId == "periodeavslutning");

        closure.Journey.Scenarios.Should().OnlyContain(item => item.Support == JourneyScenarioSupport.NotAssessed, "no safe trigger exists, so nothing in this journey can run");
        var close = closure.Journey.Scenarios.Single(item => item.ScenarioId == "close-valid-period");
        close.NotAssessedBecause.Should().StartWith("Not assessed — operator required.").And.Contain("only postpones").And.Contain("never locks a period");
        closure.Prerequisites.Single(item => item.Key == "domain-trigger").Detail.Should().StartWith("Operator required.");
        closure.Journey.Scenarios.Where(item => item.ScenarioId is "event-emitted" or "bufdata-receives" or "pii-absent")
            .Should().OnlyContain(item => item.NotAssessedBecause!.Contains("Klargjort") && item.NotAssessedBecause.Contains("not runtime verified"));
        closure.CanRun.Should().BeFalse();

        var run = await service.StartAsync(new IntegrationJourneyRunRequest
        {
            EnvironmentId = ActiveEventTestHarness.Env, PackId = "skolenaervaer", JourneyId = "periodeavslutning", ScenarioId = "close-valid-period", Confirmed = true,
        }, null, default);
        run.OverallState.Should().Be(JourneyRunState.Blocked);
        run.StateReason.Should().Contain("operator required");
        run.Steps.Should().OnlyContain(step => step.State == JourneyStepState.NotAssessed);
    }

    [Fact]
    public void PackDefinitions_ContainNoPersonalData()
    {
        var json = JsonSerializer.Serialize(Pack().Journeys);
        Regex.IsMatch(json, @"\b\d{11}\b").Should().BeFalse("no national identity numbers");
        Regex.IsMatch(json, @"\b\d{6,}\b").Should().BeFalse("no identifiers or account numbers at all");
        json.Should().NotContain("@", "no contact data");
    }

    [Fact]
    public async Task Periodeavslutning_StepsAreIndependent_AndAMissingObserverIsNotAssessed_NeverFailed()
    {
        await using var harness = new IntegrationJourneyHarness();
        harness.Active.Catalog.Extra.Add(PeriodClosed);
        harness.Enroll(PeriodClosed.Id);
        var closure = (await harness.Service(Pack(harness)).PacksAsync(ActiveEventTestHarness.Env, default)).Single().Journeys.Single(view => view.Journey.JourneyId == "periodeavslutning");

        closure.Journey.Steps.Select(step => step.StepId).Should().Equal("period-close", "dataset-prepare", "publish", "broker", "bufdata-activity", "bufdata-processed", "dataset-verify", "dataset-pii");
        closure.StepStatus.Select(step => step.StepId).Should().Equal(closure.Journey.Steps.Select(step => step.StepId), "every step reports its own status");
        closure.StepStatus.Should().OnlyContain(step => (step.State == JourneyStepState.NotAssessed || step.State == JourneyStepState.Configured) && !step.Observable && !step.Verifiable,
            "without an observer a step is at most Configured (its integration exists) — never observed, failed or verified");
        closure.StepStatus.Where(step => step.StepId is "publish" or "broker" or "bufdata-activity").Should().OnlyContain(step => step.State == JourneyStepState.Configured,
            "configured ≠ observed: the Service Bus integration is configured, nothing was seen on it");
        closure.StepStatus.Single(step => step.StepId == "bufdata-processed").State.Should().Be(JourneyStepState.NotAssessed, "Bufdata processed is never inferred from the broker");
        closure.StepStatus.Should().NotContain(step => step.State == JourneyStepState.UnexpectedResult || step.State == JourneyStepState.NotVerified);
        // Service Bus accepted ≠ Bufdata processed: the broker step and Bufdata's processing are separate steps with separate owners.
        var broker = closure.Journey.Steps.Single(step => step.StepId == "broker");
        var processed = closure.Journey.Steps.Single(step => step.StepId == "bufdata-processed");
        broker.Owner.Should().NotBe(processed.Owner);
        broker.Kind.Should().NotBe(processed.Kind);
    }

    [Fact]
    public async Task Utdanningsdata_IsDelegated_AndStaysBlockedUntilTrustIntegrationSourceAndProviderAreAllPresent()
    {
        await using var harness = new IntegrationJourneyHarness();
        var untrusted = (await harness.Service(Pack(harness)).PacksAsync("unknown-env", default)).Single();
        untrusted.EnvironmentTrusted.Should().BeFalse("an environment not enrolled in the backend trusted configuration is never trusted");
        var education = untrusted.Journeys.Single(view => view.Journey.JourneyId == "utdanningsdata");
        education.Journey.ExecutionMode.Should().Be(JourneyExecutionMode.DelegatedToActiveEvent);
        education.Journey.DelegatedProviderId.Should().Be("skolenaervaer.cdc");
        education.CanRun.Should().BeFalse();
        education.Prerequisites.Should().Contain(item => item.Key.StartsWith("integration:", StringComparison.Ordinal) && item.State == JourneyReadinessState.NotConfigured,
            "the education CDC integration is not configured");
        education.Prerequisites.Where(item => item.Key.StartsWith("source:", StringComparison.Ordinal)).Should().OnlyContain(item => item.State != JourneyReadinessState.Ready,
            "without Source Analysis evidence SkoleAdapter and Utdanning are not source verified");
        education.Maturity.SourceVerified.Should().BeFalse();
        education.Maturity.Configured.Should().BeFalse();
    }
}
