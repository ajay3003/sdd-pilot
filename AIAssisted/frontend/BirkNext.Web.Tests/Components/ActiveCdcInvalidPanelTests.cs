using BirkNext.Integrations;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>Invalid Person → valid Person in IQR → Active tests → CDC: fault-resilience card, reviewed invalid fixture, two-message confirmation, bounded results.</summary>
public sealed class ActiveCdcInvalidPanelTests : BunitContext
{
    private const string Hub = "m2lb-cdc-dev.BirkM2LB.dbo.Person";
    private const string InvalidId = "person.invalid-then-valid";
    private const string Limitation = "This verifies consumer continuity, not correct handling or persistence of either message.";
    private const string PassMeaning = "A controlled invalid Person CDC event was followed by a valid Person CDC event, and the observable Event Hub consumer advanced beyond the valid control event without becoming stuck.";
    private const string DoesNotMean = "This does not prove that the invalid event was rejected for the correct reason or that the valid Person was persisted.";
    private const string Condition = "PersonPK missing from a structurally valid Person create (valid payload, op \"c\", source.table Person, after object).";
    private readonly FakeApi _api = new();
    private static readonly FrontendAnalysisProfile Profile = new() { Id = "dev-env", Name = "M2LB DEV", EnvironmentType = FrontendEnvironmentType.Development };
    private static readonly IntegrationDefinition Person = new() { Id = "dev:eventhub:birk-cdc:dbo.Person", DisplayName = "BIRK Person CDC", Kind = IntegrationKind.EventHub, Enabled = true, SourceResource = "BirkM2LB.dbo.Person", EndpointOrTopic = Hub };

    private static readonly ActiveCdcScenario Scenario = new()
    {
        Id = InvalidId, Version = "1", Name = "Invalid Person → valid Person", Category = "Fault resilience", MessageCount = 2,
        Description = "Sends one controlled invalid Person CDC event followed by a valid synthetic Person event to check whether the observable consumer continues advancing.",
        Limitation = Limitation, PassMeaning = PassMeaning, PassDoesNotMean = DoesNotMean, InvalidFixture = "person.missing-personpk v1", InvalidCondition = Condition,
    };

    public ActiveCdcInvalidPanelTests()
    {
        Services.AddSingleton<IActiveCdcTestsApiService>(_api);
        Services.AddSingleton<IReportExportService, ReportExportService>();
        Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<ActiveCdcTestsPanel> RenderInvalid()
    {
        var cut = Render<ActiveCdcTestsPanel>(p => p.Add(c => c.Profile, Profile).Add(c => c.Integrations, [Person]).Add(c => c.PollInterval, TimeSpan.FromMilliseconds(10)));
        cut.Find($"[data-testid='act-scenario-{InvalidId}']").Change(InvalidId);
        return cut;
    }

    private IRenderedComponent<ActiveCdcTestsPanel> Completed(ActiveCdcRunStatus outcome)
    {
        _api.Outcome = outcome;
        var cut = RenderInvalid();
        cut.Find("[data-testid=act-start]").Click();
        cut.Find("[data-testid=act-ack]").Change(true);
        cut.Find("[data-testid=act-confirm-send]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=act-run]").GetAttribute("data-status").Should().Be(outcome.ToString()), TimeSpan.FromSeconds(5));
        return cut;
    }

    [Fact]
    public void Card_ShowsFaultResilience_TheLimitation_AndTheInvalidFixtureReadiness()
    {
        var cut = RenderInvalid();
        _api.Readiness.Last().Should().Be(InvalidId);
        cut.FindAll("[data-testid=act-scenarios] input[type=radio]").Should().HaveCount(3);
        cut.Find("[data-testid=act-scenario-heading]").TextContent.Should().Contain("Invalid Person → valid Person").And.Contain("Fault resilience").And.NotContainAny(["poison", "error handling"]);
        cut.Find("[data-testid=act-limitation]").TextContent.Should().Be(Limitation);
        cut.Find("[data-testid=act-check][data-key=invalid-fixture]").TextContent.Should().Contain("Reviewed");
        cut.Find("[data-testid=act-start]").TextContent.Should().Be("Send 2 synthetic events…");
        cut.Find("[data-testid=act-start-note]").TextContent.Should().Contain("reviewed invalid event I").And.Contain("advance past V after I");
    }

    [Fact]
    public void SwitchingScenariosWhileLoading_StaysKeyboardOperable_AndDropsTheStaleAnswer()
    {
        var replayAnswer = new TaskCompletionSource();
        _api.Holds["person.same-personpk-replay"] = replayAnswer;
        var cut = Render<ActiveCdcTestsPanel>(p => p.Add(c => c.Profile, Profile).Add(c => c.Integrations, [Person]));
        cut.Find("[data-testid='act-scenario-person.same-personpk-replay']").Change("person.same-personpk-replay");
        cut.Find("[data-testid=act-scenarios]").HasAttribute("disabled").Should().BeFalse("loading readiness must not take the radio group away from a keyboard user");
        cut.Find($"[data-testid='act-scenario-{InvalidId}']").Change(InvalidId);
        cut.WaitForAssertion(() => cut.Find("[data-testid=act-check][data-key=invalid-fixture]"));
        replayAnswer.SetResult();
        cut.WaitForAssertion(() => _api.Released.Should().Contain("person.same-personpk-replay"));
        cut.Render();
        cut.Find("[data-testid=act-scenario-heading]").TextContent.Should().Contain("Invalid Person → valid Person", "the late replay answer is dropped");
        cut.FindAll("[data-testid=act-check][data-key=invalid-fixture]").Should().HaveCount(1);
    }

    [Fact]
    public void Confirmation_ExplainsBothMessages_WithTheSourceDerivedCondition_NotARawPayload()
    {
        var cut = RenderInvalid();
        cut.Find("[data-testid=act-start]").Click();
        var plan = cut.FindAll("[data-testid=act-confirm-messages] li").Select(li => li.TextContent).ToList();
        plan.Should().HaveCount(2);
        plan[0].Should().StartWith("I — controlled invalid Person CDC (person.missing-personpk v1)").And.Contain("Table Person, operation create").And.Contain("PersonPK missing");
        plan[1].Should().StartWith("V — valid synthetic Person control");
        cut.Find("[data-testid=act-confirm]").TextContent.Should().Contain("Persistent synthetic control Person may remain: Yes").And.NotContain("\"after\"").And.NotContain("{");
        cut.Find("[data-testid=act-confirm-send]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=act-ack]").Change(true);
        cut.Find("[data-testid=act-confirm-send]").Click();
        _api.Started.Single().ScenarioId.Should().Be(InvalidId);
    }

    [Fact]
    public void Passed_ShowsContinuityAndKeepsHandlingPersistenceAndFaultQueueNotAssessed()
    {
        var cut = Completed(ActiveCdcRunStatus.Passed);
        cut.Find("[data-testid=act-run-headline]").TextContent.Should().Be("Passed — the consumer advanced past the valid control after the invalid event");
        cut.Find("[data-testid=act-pass-bounds]").TextContent.Should().Contain(PassMeaning).And.Contain(DoesNotMean);
        var rows = cut.FindAll("[data-testid=act-message]");
        rows.Select(r => r.GetAttribute("data-label")).Should().Equal(["I", "V"]);
        rows[0].TextContent.Should().Contain("None (missing by design)").And.Contain("PersonPK missing");
        rows[1].TextContent.Should().Contain("900000000");
        var stages = cut.FindAll("[data-testid=act-stage]");
        stages.Select(s => s.GetAttribute("data-kind")).Should().Equal(ActiveCdcPresentation.InvalidOrder.Select(k => k.ToString()));
        string State(string kind) => stages.Single(s => s.GetAttribute("data-kind") == kind).GetAttribute("data-state")!;
        State("ConsumerContinuity").Should().Be("Observed");
        foreach (var kind in new[] { "InvalidHandledCorrectly", "InvalidDiagnostic", "FaultQueueOutcome", "ConsumerRetry", "DatabaseEffects", "ValidControlHandled", "PersonPersisted", "OutboxCreated", "ServiceBusDelivered", "SubscriberProcessed" })
            State(kind).Should().Be("Not assessed", kind);
        cut.Find("[data-testid=act-stage][data-kind=ObserveInvalid]").TextContent.Should().Contain("Consumer advanced past invalid event position");
        cut.Find("[data-testid=act-coverage]").TextContent.Should().Contain("consumer-continuity path after a controlled invalid Person event").And.Contain("MapperReturnsNull");
        cut.Find("[data-testid=act-invalid-fixture]").TextContent.Should().Contain("person.missing-personpk v1").And.Contain("Reviewed");
        cut.Find("[data-testid=act-tested]").TextContent.Should().Contain("What was tested");
        cut.Find("[data-testid=act-not-assessed]").TextContent.Should().Contain("Invalid event rejected for the correct reason");
        cut.Markup.Should().NotContain("No errors occurred").And.NotContain("no retry occurred");
    }

    [Theory]
    [InlineData(ActiveCdcRunStatus.Partial, "Partial — both messages sent; consumer continuity not assessable")]
    [InlineData(ActiveCdcRunStatus.Inconclusive, "Inconclusive — a send outcome or the continuity evidence is uncertain")]
    [InlineData(ActiveCdcRunStatus.Failed, "Failed — a bounded expectation of the invalid → valid sequence was not met")]
    public void OtherOutcomes_HaveTheirOwnHeadline_AndNeverClaimThePassMeaning(ActiveCdcRunStatus outcome, string headline)
    {
        var cut = Completed(outcome);
        cut.Find("[data-testid=act-run-headline]").TextContent.Should().Be(headline);
        cut.Find("[data-testid=act-pass-bounds]").TextContent.Should().NotContain(PassMeaning).And.Contain(DoesNotMean);
    }

    [Fact]
    public void Export_ExplainsTheBoundedContinuityMeaning_AndTheReviewedInvalidFixture()
    {
        var html = new ReportExportService().ExportActiveCdcRun(_api.Run(ActiveCdcRunStatus.Passed), "Example");
        var body = html[html.IndexOf("<body", StringComparison.Ordinal)..];
        body.Should().Contain("Fault resilience").And.Contain(Limitation).And.Contain("Invalid fixture: person.missing-personpk v1").And.Contain("Review: Reviewed")
            .And.Contain("None (missing by design)").And.Contain("Invalid event rejected for the correct reason").And.Contain("Fault queue outcome").And.Contain(DoesNotMean);
        body.Should().NotContain("\"after\"").And.NotContain("SECRET_SENTINEL_INVALID_123");
    }

    private sealed class FakeApi : IActiveCdcTestsApiService
    {
        public List<string> Readiness { get; } = [];
        public List<ActiveCdcRunRequest> Started { get; } = [];
        public ActiveCdcRunStatus Outcome { get; set; } = ActiveCdcRunStatus.Passed;
        public Dictionary<string, TaskCompletionSource> Holds { get; } = [];
        public List<string> Released { get; } = [];
        private readonly Guid _runId = Guid.NewGuid();

        public ActiveCdcRun Run(ActiveCdcRunStatus status)
        {
            var continuity = status == ActiveCdcRunStatus.Passed ? ActiveCdcEvidenceState.Observed : status == ActiveCdcRunStatus.Partial ? ActiveCdcEvidenceState.NotAssessed : ActiveCdcEvidenceState.NotObserved;
            ActiveCdcMessageEvidence M(string label, int? pk, string condition = "") => new()
            {
                Label = label, Role = label == "I" ? "Controlled invalid Person CDC" : "Valid synthetic Person control", SyntheticPersonPk = pk, InvalidCondition = condition,
                PayloadSha256 = new string('f', 64), PayloadBytes = 380, SendState = ActiveCdcEvidenceState.Observed, AdvancedPartitions = new() { ["0"] = label == "I" ? 11 : 12 }, CheckpointState = continuity,
            };
            return new ActiveCdcRun
            {
                RunId = _runId, EnvironmentId = "dev-env", EnvironmentName = "M2LB DEV", EnvironmentType = "Development", IntegrationId = Person.Id, Scenario = Scenario,
                Manifest = new ActiveCdcContractManifest { Status = ActiveCdcContractStatus.Compatible, Fingerprint = "fp", InvalidFixture = "person.missing-personpk v1", InvalidFixtureStatus = "Reviewed",
                    InvalidFixtureDetail = "Reviewed against archive c850a1b2813b…", DeveloperCoverage = ["CdcRouterTests.RouteAsync_BarnNHjemmstedskommune_MapperReturnsNull_Discards (unit, related)"] },
                Destination = new ActiveCdcDestination { EventHub = Hub, NamespaceFqdn = "evhns-m2lb-dev-nwe-001.servicebus.windows.net" },
                Status = status, SendAttempted = true, StartedAt = DateTimeOffset.UtcNow, CompletedAt = status == ActiveCdcRunStatus.Running ? null : DateTimeOffset.UtcNow,
                StatusReason = "Bounded continuity result.",
                Messages = [M("I", null, Condition), M("V", 900_000_000)],
                CheckpointSnapshots = new[] { "T0 — before I", "T1 — after invalid I", "T2 — after valid V" }.Select(l => new ActiveCdcCheckpointSnapshot { Label = l, State = ActiveCdcEvidenceState.Observed, Partitions = [new("0", 12, 12)] }).ToList(),
                Steps =
                [
                    new() { Kind = ActiveCdcStepKind.SendInvalid, State = ActiveCdcEvidenceState.Observed, Detail = "Accepted." },
                    new() { Kind = ActiveCdcStepKind.ObserveInvalid, State = continuity, Detail = "Checkpoint progression only." },
                    new() { Kind = ActiveCdcStepKind.ConsumerContinuity, State = continuity, Detail = "Per-partition checkpoint." },
                    .. new[] { ActiveCdcStepKind.InvalidHandledCorrectly, ActiveCdcStepKind.InvalidDiagnostic, ActiveCdcStepKind.FaultQueueOutcome, ActiveCdcStepKind.ConsumerRetry, ActiveCdcStepKind.DatabaseEffects,
                        ActiveCdcStepKind.ValidControlHandled, ActiveCdcStepKind.PersonPersisted, ActiveCdcStepKind.OutboxCreated, ActiveCdcStepKind.ServiceBusDelivered, ActiveCdcStepKind.SubscriberProcessed }
                        .Select(k => new ActiveCdcStep { Kind = k, State = ActiveCdcEvidenceState.NotAssessed, Detail = "Not observable from BirkNext." }),
                ],
                WhatWasTested = ["I — invalid Person send (PersonPK missing): Accepted."],
                WhatWasNotAssessed = ["Invalid event rejected for the correct reason — Not assessed: Not observable from BirkNext.", "Fault queue outcome — Not assessed: Not observable from BirkNext."],
            };
        }

        public async Task<ActiveCdcReadiness> ReadinessAsync(FrontendAnalysisProfile profile, string integrationId, Guid? snapshotId, string scenarioId, CancellationToken ct = default)
        {
            Readiness.Add(scenarioId);
            if (Holds.TryGetValue(scenarioId, out var hold)) { await hold.Task; Released.Add(scenarioId); }
            return Answer(profile, integrationId, scenarioId);
        }

        private static ActiveCdcReadiness Answer(FrontendAnalysisProfile profile, string integrationId, string scenarioId) => (new ActiveCdcReadiness
            {
                EnvironmentId = profile.Id, IntegrationId = integrationId, CanRun = true, ObservationSeconds = 180,
                Scenario = scenarioId == InvalidId ? Scenario : new ActiveCdcScenario { Id = scenarioId, Name = "Normal Person", Version = "1" },
                Destination = new ActiveCdcDestination { EventHub = Hub, NamespaceFqdn = "evhns-m2lb-dev-nwe-001.servicebus.windows.net", Approved = true },
                Manifest = new ActiveCdcContractManifest { Status = ActiveCdcContractStatus.Compatible, Fingerprint = "fp", InvalidFixtureStatus = "Reviewed" },
                Checks = scenarioId == InvalidId
                    ? [new("invalid-fixture", "Invalid fixture", ActiveCdcReadinessState.Ready, "Reviewed — person.missing-personpk v1."),
                        new("progression-evidence", "Runtime progression evidence", ActiveCdcReadinessState.Ready, "Partition positions and checkpoints can be read.")]
                    : [new("progression-evidence", "Runtime progression evidence", ActiveCdcReadinessState.Ready, "Partition positions and checkpoints can be read.")],
            });

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
