using BirkNext.Integrations;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>Same PersonPK replay in IQR → Active tests → CDC: scenario choice, prominent limitation, three-message confirmation, bounded result.</summary>
public sealed class ActiveCdcReplayPanelTests : BunitContext
{
    private const string Hub = "m2lb-cdc-dev.BirkM2LB.dbo.Person";
    private const string ReplayId = "person.same-personpk-replay";
    private const string Limitation = "This does not verify database idempotency or duplicate Person handling.";
    private const string PassMeaning = "Runtime continuity after replay was observed: the Event Hub consumer advanced beyond the replay and the following control event without becoming stuck.";
    private const string DoesNotMean = "It does not mean the replay was processed successfully, the control Person was persisted, database idempotency was proven, no outbox duplication occurred, Person row count or overwrite behavior was correct, natural-key duplicates were handled, or Service Bus was verified. A checkpoint past an event shows consumer progression, not successful handling.";
    private readonly FakeApi _api = new();
    private static readonly FrontendAnalysisProfile Profile = new() { Id = "dev-env", Name = "M2LB DEV", EnvironmentType = FrontendEnvironmentType.Development };
    private static readonly IntegrationDefinition Person = new() { Id = "dev:eventhub:birk-cdc:dbo.Person", DisplayName = "BIRK Person CDC", Kind = IntegrationKind.EventHub, Enabled = true, SourceResource = "BirkM2LB.dbo.Person", EndpointOrTopic = Hub };

    private static readonly ActiveCdcScenario Scenario = new()
    {
        Id = ReplayId, Version = "1", Name = "Same PersonPK replay", Category = "Runtime resilience", MessageCount = 3,
        Description = "Sends one synthetic Person, replays the same source identity, then sends another valid Person to check whether the observable consumer path continues.",
        Limitation = Limitation, PassMeaning = PassMeaning, PassDoesNotMean = DoesNotMean,
    };

    public ActiveCdcReplayPanelTests()
    {
        Services.AddSingleton<IActiveCdcTestsApiService>(_api);
        Services.AddSingleton<IReportExportService, ReportExportService>();
        Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<ActiveCdcTestsPanel> RenderReplay()
    {
        var cut = Render<ActiveCdcTestsPanel>(p => p.Add(c => c.Profile, Profile).Add(c => c.Integrations, [Person]).Add(c => c.PollInterval, TimeSpan.FromMilliseconds(10)));
        cut.Find($"[data-testid='act-scenario-{ReplayId}']").Change(ReplayId);
        return cut;
    }

    private void StartReplay(IRenderedComponent<ActiveCdcTestsPanel> cut)
    {
        cut.Find("[data-testid=act-start]").Click();
        cut.Find("[data-testid=act-ack]").Change(true);
        cut.Find("[data-testid=act-confirm-send]").Click();
    }

    [Fact]
    public void ChoosingReplay_ShowsCategoryAndProminentLimitation_AndAsksTheBackendForItsReadiness()
    {
        var cut = RenderReplay();
        _api.Readiness.Last().Should().Be(ReplayId);
        cut.Find("[data-testid=act-scenario-heading]").TextContent.Should().Contain("Same PersonPK replay").And.Contain("Runtime resilience").And.NotContain("Duplicate Person");
        cut.Find("[data-testid=act-limitation]").TextContent.Should().Be(Limitation);
        cut.Find("[data-testid=act-limitation]").GetAttribute("role").Should().Be("note");
        cut.Find("[data-testid=act-start]").TextContent.Should().Be("Send 3 synthetic events…");
        cut.Find("[data-testid=act-start-note]").TextContent.Should().Contain("exact replay A2").And.Contain("Passed requires progression after the replay");
        cut.Find("[data-testid=act-check][data-key=progression-evidence]").TextContent.Should().Contain("Runtime progression evidence");
        cut.Find("[data-testid=act-scenarios] legend").TextContent.Should().Be("CDC scenario");
    }

    [Fact]
    public void Confirmation_ListsTheThreeMessages_AndRequiresAcknowledgement()
    {
        var cut = RenderReplay();
        cut.Find("[data-testid=act-start]").Click();
        var plan = cut.FindAll("[data-testid=act-confirm-messages] li").Select(li => li.TextContent).ToList();
        plan.Should().Equal(["A — PersonPK X (next reserved synthetic key)", "A2 — same PersonPK X, exact replay of A", "B — different PersonPK Y, a following valid Person"]);
        cut.Find("[data-testid=act-confirm]").TextContent.Should().Contain("Persistent synthetic records may remain: Yes").And.Contain(Hub);
        cut.Find("[data-testid=act-confirm-send]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=act-ack]").Change(true);
        cut.Find("[data-testid=act-confirm-send]").Click();
        _api.Started.Single().ScenarioId.Should().Be(ReplayId);
    }

    [Fact]
    public void PassedResult_ShowsMessagesProgressionAndTheBoundedMeaning_WithUnassessableDomainsKeptApart()
    {
        var cut = RenderReplay();
        StartReplay(cut);
        cut.WaitForAssertion(() => cut.Find("[data-testid=act-run]").GetAttribute("data-status").Should().Be("Passed"), TimeSpan.FromSeconds(5));
        cut.Find("[data-testid=act-run-headline]").TextContent.Should().Be("Passed — the consumer advanced past the replay and the control event");
        cut.Find("[data-testid=act-stage][data-kind=ObserveControl]").TextContent.Should().Contain("Consumer advanced past B");
        foreach (var kind in new[] { "ReplayHandled", "ControlHandled" })
            cut.Find($"[data-testid=act-stage][data-kind={kind}]").GetAttribute("data-state").Should().Be("Not assessed", "checkpoint progression is never shown as successful handling");
        cut.Find("[data-testid=act-pass-bounds]").TextContent.Should().Contain(PassMeaning).And.Contain(DoesNotMean);
        cut.FindAll("[data-testid=act-message]").Select(r => (r.GetAttribute("data-label"), r.GetAttribute("data-send"))).Should().Equal([("A", "Observed"), ("A2", "Observed"), ("B", "Observed")]);
        var stages = cut.FindAll("[data-testid=act-stage]");
        stages.Select(s => s.GetAttribute("data-kind")).Should().Equal(ActiveCdcPresentation.ReplayOrder.Select(k => k.ToString()));
        string State(string kind) => stages.Single(s => s.GetAttribute("data-kind") == kind).GetAttribute("data-state")!;
        State("FollowingEventProgression").Should().Be("Observed");
        foreach (var kind in new[] { "PersonPersisted", "DatabaseIdempotency", "PersonRowCount", "OverwriteBehavior", "OutboxDuplication", "ServiceBusDelivered", "SubscriberProcessed" })
            State(kind).Should().Be("Not assessed");
        State("NaturalKeyDuplicate").Should().Be("Not tested");
        cut.Find("[data-testid=act-coverage]").TextContent.Should().Contain("PersonIngestionTests.InnmatingPerson_SamePayloadTwice_ProducesExactlyOnePersonRow").And.Contain("Active CDC coverage");
        cut.FindAll("[data-testid=act-snapshot]").Should().HaveCount(4);
        cut.FindAll("[data-testid=act-fixture]").Should().BeEmpty("the per-message table replaces the single fixture");
        cut.Find("[data-testid=act-stage][data-kind=CorrelatedReplayError]").TextContent.Should().Contain("No correlated replay error evidence was observed").And.NotContain("No errors occurred");
    }

    [Fact]
    public void PartialResult_KeepsTheLimitation_WithoutClaimingThePassMeaning()
    {
        _api.Outcome = ActiveCdcRunStatus.Partial;
        var cut = RenderReplay();
        StartReplay(cut);
        cut.WaitForAssertion(() => cut.Find("[data-testid=act-run]").GetAttribute("data-status").Should().Be("Partial"), TimeSpan.FromSeconds(5));
        cut.Find("[data-testid=act-run-headline]").TextContent.Should().Contain("progression after the replay not assessable");
        cut.Find("[data-testid=act-pass-bounds]").TextContent.Should().NotContain(PassMeaning).And.Contain(DoesNotMean);
    }

    [Fact]
    public void Export_ExplainsPassMeaningAndLimits_ListsMessagesAndCoverage_WithoutPayloads()
    {
        var html = new ReportExportService().ExportActiveCdcRun(_api.Run(ActiveCdcRunStatus.Passed), "Example");
        var body = html[html.IndexOf("<body", StringComparison.Ordinal)..];
        body.Should().Contain("Runtime resilience").And.Contain(Limitation).And.Contain("Pass meaning").And.Contain("database idempotency was proven")
            .And.Contain("advanced beyond the replay").And.Contain("Messages (payloads not stored)").And.Contain("T3 — after B").And.Contain("Developer coverage").And.Contain("Natural-key duplicate").And.Contain("Not tested");
        body.Should().NotContain("\"after\"").And.NotContain("SECRET_SENTINEL_REPLAY_123");
    }

    [Fact]
    public void StoredNormalPersonRun_StillRendersItsPhase1Stages()
    {
        var phase1 = new ActiveCdcRun { RunId = Guid.NewGuid(), Scenario = new() { Id = "person.normal.create", Name = "Normal Person", Version = "1" }, Status = ActiveCdcRunStatus.Partial, CompletedAt = DateTimeOffset.UtcNow,
            Steps = [new() { Kind = ActiveCdcStepKind.PersonPersisted, State = ActiveCdcEvidenceState.NotAssessed }] };
        ActiveCdcPresentation.Stages(phase1).Select(s => s.Kind).Should().Equal(ActiveCdcPresentation.Order);
        ActiveCdcPresentation.Headline(phase1).Should().StartWith("Partial — Event Hub accepted the synthetic event");
    }

    private sealed class FakeApi : IActiveCdcTestsApiService
    {
        public List<string> Readiness { get; } = [];
        public List<ActiveCdcRunRequest> Started { get; } = [];
        public ActiveCdcRunStatus Outcome { get; set; } = ActiveCdcRunStatus.Passed;
        private readonly Guid _runId = Guid.NewGuid();

        public ActiveCdcRun Run(ActiveCdcRunStatus status)
        {
            var progression = status == ActiveCdcRunStatus.Passed ? ActiveCdcEvidenceState.Observed : ActiveCdcEvidenceState.NotAssessed;
            ActiveCdcMessageEvidence M(string label, int pk) => new()
            {
                Label = label, Role = label, SyntheticPersonPk = pk, PayloadSha256 = new string('d', 64), PayloadBytes = 410, SendState = ActiveCdcEvidenceState.Observed,
                AdvancedPartitions = new() { ["0"] = pk - 899_999_989 }, CheckpointState = progression,
            };
            return new ActiveCdcRun
            {
                RunId = _runId, EnvironmentId = "dev-env", EnvironmentName = "M2LB DEV", EnvironmentType = "Development", IntegrationId = Person.Id, Scenario = Scenario,
                Manifest = new ActiveCdcContractManifest { Status = ActiveCdcContractStatus.Compatible, Fingerprint = "fp", DeveloperCoverage = ["PersonIngestionTests.InnmatingPerson_SamePayloadTwice_ProducesExactlyOnePersonRow (integration)"] },
                Destination = new ActiveCdcDestination { EventHub = Hub, NamespaceFqdn = "evhns-m2lb-dev-nwe-001.servicebus.windows.net" },
                Status = status, SendAttempted = true, StartedAt = DateTimeOffset.UtcNow, CompletedAt = status == ActiveCdcRunStatus.Running ? null : DateTimeOffset.UtcNow,
                StatusReason = status == ActiveCdcRunStatus.Passed ? "All required expectations were met." : "No reliable run-specific consumer-progression evidence.",
                Messages = [M("A", 900_000_000), M("A2", 900_000_000), M("B", 900_000_001)],
                CheckpointSnapshots = new[] { "T0 — before A", "T1 — after A", "T2 — after A2", "T3 — after B" }.Select(l => new ActiveCdcCheckpointSnapshot { Label = l, State = ActiveCdcEvidenceState.Observed, Partitions = [new("0", 12, 12)] }).ToList(),
                Steps =
                [
                    new() { Kind = ActiveCdcStepKind.SendA, State = ActiveCdcEvidenceState.Observed, Detail = "Accepted." },
                    new() { Kind = ActiveCdcStepKind.FollowingEventProgression, State = progression, Detail = "Per-partition checkpoint." },
                    new() { Kind = ActiveCdcStepKind.CorrelatedReplayError, State = ActiveCdcEvidenceState.NotAssessed, Detail = "No correlated replay error evidence was observed — this is not a statement that no error occurred." },
                    .. new[] { ActiveCdcStepKind.PersonPersisted, ActiveCdcStepKind.DatabaseIdempotency, ActiveCdcStepKind.PersonRowCount, ActiveCdcStepKind.OverwriteBehavior, ActiveCdcStepKind.OutboxCreated,
                        ActiveCdcStepKind.OutboxDuplication, ActiveCdcStepKind.ServiceBusDelivered, ActiveCdcStepKind.SubscriberProcessed }.Select(k => new ActiveCdcStep { Kind = k, State = ActiveCdcEvidenceState.NotAssessed, Detail = "Not observable." }),
                    new() { Kind = ActiveCdcStepKind.NaturalKeyDuplicate, State = ActiveCdcEvidenceState.NotTested, Detail = "Not tested." },
                ],
                WhatWasTested = ["A — send (PersonPK X): Accepted."], WhatWasNotAssessed = ["Database idempotency — Not assessed"],
            };
        }

        public Task<ActiveCdcReadiness> ReadinessAsync(FrontendAnalysisProfile profile, string integrationId, Guid? snapshotId, string scenarioId, CancellationToken ct = default)
        {
            Readiness.Add(scenarioId);
            return Task.FromResult(new ActiveCdcReadiness
            {
                EnvironmentId = profile.Id, IntegrationId = integrationId, CanRun = true, ObservationSeconds = 180,
                Scenario = scenarioId == ReplayId ? Scenario : new ActiveCdcScenario { Id = scenarioId, Name = "Normal Person", Version = "1" },
                Destination = new ActiveCdcDestination { EventHub = Hub, NamespaceFqdn = "evhns-m2lb-dev-nwe-001.servicebus.windows.net", Approved = true },
                Manifest = new ActiveCdcContractManifest { Status = ActiveCdcContractStatus.Compatible, Fingerprint = "fp" },
                Checks = [new("synthetic-key", "Reserved synthetic PersonPK range", ActiveCdcReadinessState.Ready, "2 unused key(s); this scenario needs 2."),
                    new("progression-evidence", "Runtime progression evidence", ActiveCdcReadinessState.Ready, "Partition positions and checkpoints can be read.")],
            });
        }

        public Task<IReadOnlyList<ActiveEventScenarioDescriptor>> ScenariosAsync(FrontendAnalysisProfile profile, string integrationId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ActiveEventScenarioDescriptor>>(integrationId == Person.Id
                ? [
                    new() { ExtensionId = "m2lb.person", ScenarioId = "person.normal.create", ScenarioVersion = "1", DisplayName = "Normal Person", RequiredTransportType = "EventHub" },
                    new() { ExtensionId = "m2lb.person", ScenarioId = ReplayId, ScenarioVersion = "1", DisplayName = Scenario.Name, Category = Scenario.Category,
                        RequiredTransportType = "EventHub", ExpectedEventCount = 3, ReplayKind = ActiveEventReplayKind.ExactReplay },
                ] : []);

        public Task<(ActiveCdcRun? Run, string? Error)> StartAsync(FrontendAnalysisProfile profile, ActiveCdcRunRequest request, CancellationToken ct = default)
        {
            Started.Add(request);
            return Task.FromResult<(ActiveCdcRun?, string?)>((Run(ActiveCdcRunStatus.Running), null));
        }

        public Task<ActiveCdcRun?> GetAsync(Guid runId, CancellationToken ct = default) => Task.FromResult<ActiveCdcRun?>(Run(Outcome));
        public Task<bool> CancelAsync(Guid runId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<IReadOnlyList<ActiveCdcRunSummary>> HistoryAsync(string environmentId, string? integrationId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ActiveCdcRunSummary>>([]);
    }
}
