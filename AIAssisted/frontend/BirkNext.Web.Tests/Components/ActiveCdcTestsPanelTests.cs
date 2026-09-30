using BirkNext.Integrations;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>IQR → Active tests → CDC panel: backend-decided readiness, explicit confirmation, live stages, conservative result, history and export.</summary>
public sealed class ActiveCdcTestsPanelTests : BunitContext
{
    private const string Hub = "m2lb-cdc-dev.BirkM2LB.dbo.Person";
    private readonly FakeApi _api = new();
    private static readonly FrontendAnalysisProfile Profile = new() { Id = "dev-env", Name = "M2LB DEV", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = "https://m2lbdev.bufetat.no/" };
    private static readonly IntegrationDefinition Person = new() { Id = "dev:eventhub:birk-cdc:dbo.Person", DisplayName = "BIRK Person CDC", Kind = IntegrationKind.EventHub, Enabled = true, SourceResource = "BirkM2LB.dbo.Person", EndpointOrTopic = Hub };
    private static readonly IntegrationDefinition Barn = Person with { Id = "dev:eventhub:birk-cdc:dbo.Barn", DisplayName = "BIRK Barn CDC", SourceResource = "BirkM2LB.dbo.Barn" };

    public ActiveCdcTestsPanelTests()
    {
        Services.AddSingleton<IActiveCdcTestsApiService>(_api);
        Services.AddSingleton<IReportExportService, ReportExportService>();
        Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<ActiveCdcTestsPanel> Render(int pollMs = 10) => Render<ActiveCdcTestsPanel>(p => p
        .Add(c => c.Profile, Profile).Add(c => c.Integrations, [Barn, Person]).Add(c => c.PollInterval, TimeSpan.FromMilliseconds(pollMs)));

    [Fact]
    public void Panel_IsLabelledActive_OffersOnlyPersonCdc_AndShowsBackendReadiness()
    {
        var cut = Render();
        cut.Find("[data-testid=act-notice]").TextContent.Should().Be(ActiveCdcLabels.ActiveTestNotice);
        cut.Find(".act-subheading").TextContent.Should().Contain("(person.normal.create v1)");
        cut.FindAll("#act-integration option").Select(o => o.GetAttribute("value")).Should().Equal([Person.Id], "only a Person CDC integration fits the Normal Person scenario");
        cut.Find("[data-testid=act-destination]").TextContent.Should().Contain(Hub).And.Contain("$Default (configured assumption)");
        var checks = cut.FindAll("[data-testid=act-check]");
        checks.Single(c => c.GetAttribute("data-key") == "sender-rights").TextContent.Should().Contain("Unknown");
        checks.Single(c => c.GetAttribute("data-key") == "person-verification").TextContent.Should().Contain("Optional");
        cut.Find("[data-testid=act-start]").HasAttribute("disabled").Should().BeFalse();
        _api.Readiness.Single().Should().Be((Profile.Id, Person.Id, (Guid?)null));
    }

    [Fact]
    public void BlockedReadiness_DisablesTheSend_AndSaysNothingIsSent()
    {
        _api.Ready = _api.Ready with { CanRun = false, Checks = [new("environment", "Environment (DEV/QA only)", ActiveCdcReadinessState.Blocked, "Automation is not permitted against a Production environment.")] };
        var cut = Render();
        cut.Find("[data-testid=act-start]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=act-start-note]").TextContent.Should().Contain("Nothing is sent while any check is blocked");
        cut.Find("[data-testid=act-check]").GetAttribute("data-state").Should().Be("Blocked");
    }

    [Fact]
    public void Send_RequiresExplicitConfirmation_ThenShowsLiveStagesAndAConservativeResult()
    {
        var cut = Render();
        cut.Find("[data-testid=act-start]").Click();
        var confirm = cut.Find("[data-testid=act-confirm]");
        confirm.TextContent.Should().Contain(Hub).And.Contain("cannot be recalled");
        cut.Find("[data-testid=act-confirm-send]").HasAttribute("disabled").Should().BeTrue("the acknowledgement is required");
        _api.Started.Should().BeEmpty();
        cut.Find("[data-testid=act-ack]").Change(true);
        cut.Find("[data-testid=act-confirm-send]").Click();

        var request = _api.Started.Single();
        (request.ConfirmedSend, request.ConfirmedEventHub, request.ScenarioId, request.IntegrationId).Should().Be((true, Hub, "person.normal.create", Person.Id));
        cut.WaitForAssertion(() => cut.Find("[data-testid=act-run]").GetAttribute("data-status").Should().Be("Partial"), TimeSpan.FromSeconds(5));
        cut.Find("[data-testid=act-run-headline]").TextContent.Should().Contain("Person persistence not assessed");
        var stages = cut.FindAll("[data-testid=act-stage]");
        stages.Should().HaveCount(Enum.GetValues<ActiveCdcStepKind>().Length, "every stage is listed, reached or not");
        stages.Single(s => s.GetAttribute("data-kind") == "EventHubSend").GetAttribute("data-state").Should().Be("Observed");
        foreach (var kind in new[] { "PersonPersisted", "OutboxCreated", "ServiceBusDelivered", "SubscriberProcessed" })
            stages.Single(s => s.GetAttribute("data-kind") == kind).GetAttribute("data-state").Should().Be("Not assessed");
        cut.Markup.Should().NotContain(">Passed<", "Phase 1 never shows a pass");
        cut.Find("[data-testid=act-fixture]").TextContent.Should().Contain("900000000").And.Contain("(not stored)");
        cut.Find("[data-testid=act-not-assessed]").HasAttribute("open").Should().BeTrue();
        cut.Find(".act-table").GetAttribute("tabindex").Should().Be("0");
    }

    [Fact]
    public void RunningRun_OffersCancel_AndStatesThatCancelCannotUnsend()
    {
        _api.Hold = true;
        var cut = Render(pollMs: 1000);
        cut.Find("[data-testid=act-start]").Click();
        cut.Find("[data-testid=act-ack]").Change(true);
        cut.Find("[data-testid=act-confirm-send]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=act-cancel-note]").TextContent.Should().Be(ActiveCdcLabels.CancelNotice));
        cut.Find("[data-testid=act-stage][data-kind=PersonPersisted]").GetAttribute("data-state").Should().Be("Pending");
        cut.Find("[data-testid=act-cancel]").Click();
        cut.WaitForAssertion(() => _api.Cancelled.Should().ContainSingle());
        _api.Hold = false;
        cut.WaitForAssertion(() => cut.Find("[data-testid=act-run]").GetAttribute("data-status").Should().Be("Cancelled"), TimeSpan.FromSeconds(5));
        cut.Find("[data-testid=act-run-headline]").TextContent.Should().Contain("had already been sent");
    }

    [Fact]
    public void BackendRefusal_IsShown_AndHistoryOpensStoredRuns()
    {
        _api.Refusal = "Confirm that one synthetic event is sent to the named non-production Event Hub.";
        _api.History = [new(_api.Completed.RunId, Person.Id, "person.normal.create", ActiveCdcRunStatus.Partial, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, true, "abcdef0123456789")];
        var cut = Render();
        cut.Find("[data-testid=act-start]").Click();
        cut.Find("[data-testid=act-ack]").Change(true);
        cut.Find("[data-testid=act-confirm-send]").Click();
        cut.Find("[data-testid=act-error]").TextContent.Should().Be(_api.Refusal);
        cut.Find("[data-testid=act-history-item]").TextContent.Should().Contain("Partial").And.Contain("event sent").And.Contain("abcdef0123456789");
        cut.Find("[data-testid=act-history-item] button").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=act-run]").GetAttribute("data-status").Should().Be("Partial"));
        cut.Find("[data-testid=act-export]").Click();
        JSInterop.Invocations.Should().Contain(i => i.Identifier == "downloadHtmlFile");
    }

    [Fact]
    public void NoClient_RendersNothing_SoOlderHostsAreUnaffected()
    {
        using var bare = new BunitContext();
        bare.Services.AddSingleton<IReportExportService, ReportExportService>();
        bare.Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        var cut = bare.Render<ActiveCdcTestsPanel>(p => p.Add(c => c.Profile, Profile).Add(c => c.Integrations, [Person]));
        cut.Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void Export_CarriesStagesFixtureBindingAndLimitations_WithoutPayloadOrPass()
    {
        var html = new ReportExportService().ExportActiveCdcRun(_api.Completed, "Example");
        var body = html[html.IndexOf("<body", StringComparison.Ordinal)..];
        body.Should().Contain(ActiveCdcLabels.ActiveTestNotice).And.Contain("Partial").And.Contain("Stages (each is separate evidence)")
            .And.Contain("Person persisted").And.Contain("Not assessed").And.Contain("payload not stored").And.Contain("Source contract binding")
            .And.Contain("fp0123456789abcd").And.Contain("Passed requires a verified read-only observation");
        body.Should().NotContain("\"after\"").And.NotContain("SECRET_SENTINEL_CDC_123");
    }

    private sealed class FakeApi : IActiveCdcTestsApiService
    {
        public List<(string Env, string Integration, Guid? Snapshot)> Readiness { get; } = [];
        public List<ActiveCdcRunRequest> Started { get; } = [];
        public List<Guid> Cancelled { get; } = [];
        public string? Refusal { get; set; }
        public volatile bool Hold;
        public IReadOnlyList<ActiveCdcRunSummary> History { get; set; } = [];

        public ActiveCdcReadiness Ready { get; set; } = new()
        {
            EnvironmentId = "dev-env", IntegrationId = Person.Id, CanRun = true, ObservationSeconds = 180,
            Scenario = new ActiveCdcScenario { Id = "person.normal.create", Version = "1", Name = "Normal Person", Description = "One synthetic Person create." },
            Destination = new ActiveCdcDestination { EventHub = Hub, NamespaceFqdn = "evhns-m2lb-dev-nwe-001.servicebus.windows.net", ConsumerGroup = "$Default", ConsumerGroupAssumed = true, Approved = true },
            Manifest = new ActiveCdcContractManifest { Status = ActiveCdcContractStatus.Compatible, Fingerprint = "fp0123456789abcd", SourceSnapshotId = Guid.NewGuid() },
            Checks =
            [
                new("environment", "Environment (DEV/QA only)", ActiveCdcReadinessState.Ready, "Development — non-production."),
                new("sender-rights", "Event Hubs Data Sender right", ActiveCdcReadinessState.Unknown, "Not probed."),
                new("person-verification", "Person persisted verification", ActiveCdcReadinessState.Optional, "No reliable read-only Person verification exists."),
            ],
        };

        public ActiveCdcRun Completed { get; } = new()
        {
            RunId = Guid.NewGuid(), EnvironmentId = "dev-env", EnvironmentName = "M2LB DEV", EnvironmentType = "Development", IntegrationId = Person.Id, IntegrationName = Person.DisplayName,
            Scenario = new ActiveCdcScenario { Id = "person.normal.create", Version = "1", Name = "Normal Person", PassCriterion = "Passed requires a verified read-only observation of the synthetic PersonId." },
            Manifest = new ActiveCdcContractManifest { Status = ActiveCdcContractStatus.Compatible, Fingerprint = "fp0123456789abcd" },
            Destination = new ActiveCdcDestination { EventHub = Hub, NamespaceFqdn = "evhns-m2lb-dev-nwe-001.servicebus.windows.net" },
            Fixture = new ActiveCdcFixtureSummary { SyntheticPersonPk = 900_000_000, Marker = "BIRKNEXT-TEST-ABC", PayloadSha256 = new string('c', 64), PayloadBytes = 400, Fields = ["PersonPK"] },
            Status = ActiveCdcRunStatus.Partial, StatusReason = "Event Hub accepted the synthetic event.", SendAttempted = true, StartedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow,
            Steps =
            [
                new() { Kind = ActiveCdcStepKind.EventHubSend, State = ActiveCdcEvidenceState.Observed, Detail = "Accepted.", Source = "SDK" },
                new() { Kind = ActiveCdcStepKind.PersonPersisted, State = ActiveCdcEvidenceState.NotAssessed, Detail = "No verification path." },
            ],
            WhatWasTested = ["Event Hub accepted the event: Accepted."], WhatWasNotAssessed = ["Person persisted — Not assessed: No verification path."], Limitations = ["Source compatibility is not deployment correlation."],
        };

        public Task<ActiveCdcReadiness> ReadinessAsync(FrontendAnalysisProfile profile, string integrationId, Guid? snapshotId, CancellationToken ct = default)
        {
            Readiness.Add((profile.Id, integrationId, snapshotId));
            return Task.FromResult(Ready);
        }

        public Task<(ActiveCdcRun? Run, string? Error)> StartAsync(FrontendAnalysisProfile profile, ActiveCdcRunRequest request, CancellationToken ct = default)
        {
            Started.Add(request);
            if (Refusal is not null) return Task.FromResult<(ActiveCdcRun?, string?)>((null, Refusal));
            return Task.FromResult<(ActiveCdcRun?, string?)>((Completed with { Status = ActiveCdcRunStatus.Running, CompletedAt = null, SendAttempted = true, Steps = [Completed.Steps[0]] }, null));
        }

        public Task<ActiveCdcRun?> GetAsync(Guid runId, CancellationToken ct = default) => Task.FromResult<ActiveCdcRun?>(
            Hold ? Completed with { Status = ActiveCdcRunStatus.Running, CompletedAt = null, Steps = [Completed.Steps[0]] }
            : Cancelled.Count > 0 ? Completed with { Status = ActiveCdcRunStatus.Cancelled, CancellationRequested = true } : Completed);

        public Task<bool> CancelAsync(Guid runId, CancellationToken ct = default) { Cancelled.Add(runId); return Task.FromResult(true); }

        public Task<IReadOnlyList<ActiveCdcRunSummary>> HistoryAsync(string environmentId, string? integrationId, CancellationToken ct = default) => Task.FromResult(History);
    }
}
