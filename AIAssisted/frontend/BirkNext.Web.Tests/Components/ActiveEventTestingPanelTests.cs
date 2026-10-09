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
/// The shared Active Event panel: provider selection (Person CDC, Skolenærvær), backend-decided readiness grouped by prerequisite, the
/// browser's authentication state, explicit confirmation, neutral stages and wording, mixed-provider history and the generic export.
/// </summary>
public sealed class ActiveEventTestingPanelTests : BunitContext
{
    private const string Hub = "m2lb-cdc-dev.BirkM2LB.dbo.Person";
    private readonly FakeApi _api = new();
    private readonly Mock<IReportExportService> _export = new();
    private static readonly FrontendAnalysisProfile Profile = new() { Id = "dev-env", Name = "M2LB DEV", EnvironmentType = FrontendEnvironmentType.Development };
    private static readonly IntegrationDefinition Person = new() { Id = "person", DisplayName = "BIRK Person CDC", Kind = IntegrationKind.EventHub, Enabled = true, SourceResource = "BirkM2LB.dbo.Person", EndpointOrTopic = Hub };

    public ActiveEventTestingPanelTests()
    {
        Services.AddSingleton<IActiveEventsApiService>(_api);
        _export.Setup(e => e.ExportActiveEventRun(It.IsAny<ActiveEventRunResult>(), It.IsAny<string?>())).Returns("<html></html>");
        Services.AddSingleton(_export.Object);
        Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<ActiveEventTestingPanel> Render(bool authenticationConfigured = true)
    {
        Services.AddSingleton(authenticationConfigured ? new ActiveEventAuthenticationState(true, "Configured (test).") : ActiveEventAuthenticationState.NotConfigured);
        return Render<ActiveEventTestingPanel>(p => p.Add(c => c.Profile, Profile).Add(c => c.Integrations, [Person]).Add(c => c.PollInterval, TimeSpan.FromMilliseconds(10)));
    }

    [Fact]
    public void Providers_AreSelectable_AndSkolenaervaerWithoutIntegrationIsNotConfigured()
    {
        var cut = Render();
        cut.FindAll("[data-testid^=act-provider-]").Select(e => e.GetAttribute("data-testid")).Should().Contain(["act-provider-m2lb.person", "act-provider-skolenaervaer.cdc"]);
        cut.Find("[data-testid='act-provider-m2lb.person']").HasAttribute("checked").Should().BeTrue("the provider with an applicable integration is preselected");
        cut.Find("[data-testid=act-scenario-heading]").TextContent.Should().Contain("Normal Person");

        cut.Find("[data-testid='act-provider-skolenaervaer.cdc']").Change("skolenaervaer.cdc");
        cut.Find("[data-testid=act-no-integration]").TextContent.Should().StartWith("No configured Event Hub integration was found for this CDC source");
        var integration = cut.FindAll("[data-testid=act-check]").Single(c => c.GetAttribute("data-key") == "integration");
        integration.GetAttribute("data-state").Should().Be("NotConfigured");
        integration.TextContent.Should().Contain("Not configured");
        cut.FindAll("[data-testid^=act-scenario-skolenaervaer]").Should().NotBeEmpty("the provider's declared scenarios stay visible");
        cut.FindAll("[data-testid=act-not-assessed-pill]").Should().NotBeEmpty();
        cut.Find("[data-testid=act-start]").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void AuthenticationNotConfigured_KeepsReviewVisible_AndDisablesExecution()
    {
        var cut = Render(authenticationConfigured: false);
        var auth = cut.FindAll("[data-testid=act-check]").Single(c => c.GetAttribute("data-key") == "authentication");
        auth.GetAttribute("data-state").Should().Be("NotConfigured");
        auth.TextContent.Should().Contain("Authentication not configured");
        cut.Find("[data-testid=act-start]").HasAttribute("disabled").Should().BeTrue("the backend said CanRun, but the browser cannot obtain a token");
        cut.Find("[data-testid=act-start-note]").TextContent.Should().Contain("authentication is not configured");
        cut.FindAll("[data-testid=act-check-group]").Select(g => g.GetAttribute("data-category")).Should().ContainInOrder("SourceContract", "TrustedEnvironment", "Authentication", "Authorization", "SyntheticData", "DownstreamVerification");
    }

    [Fact]
    public void Run_RequiresConfirmation_SendsOnlyIdsAndChecks_AndShowsNeutralStages()
    {
        var cut = Render();
        cut.Find("[data-testid=act-history]").TriggerEvent("ontoggle", EventArgs.Empty);
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=act-check]").Single(c => c.GetAttribute("data-key") == "authorization").GetAttribute("data-state").Should().Be("Ready"));
        cut.Find("[data-testid=act-start]").Click();

        var facts = cut.Find("[data-testid=act-confirm-facts]").TextContent;
        facts.Should().Contain("M2LB DEV (Development)").And.Contain(Hub).And.Contain("dbo.Person").And.Contain("Normal Person").And.Contain("Create").And.Contain("reserved range");
        cut.Find("[data-testid=act-state-warning]").TextContent.Should().Contain("State-changing");
        cut.Find("[data-testid=act-confirm-send]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=act-ack]").Change(true);
        cut.Find("[data-testid=act-confirm-send]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=act-run]").GetAttribute("data-status").Should().Be("CompletedWithLimitedEvidence"));
        var request = _api.Started.Single();
        request.Should().BeEquivalentTo(new ActiveEventRunRequest
        {
            TargetEnvironmentId = Profile.Id, IntegrationId = Person.Id, ExtensionId = "m2lb.person", ScenarioId = "person.normal.create", Confirmed = true,
            ConfirmedDestination = Hub, ReviewedContractFingerprint = "fp-1", SourceSnapshotId = _api.Snapshot,
        });
        cut.Find("[data-testid=act-run-headline]").TextContent.Should().Be("Transport accepted — downstream result not verified");
        cut.FindAll("[data-testid=act-stage]").Select(s => s.GetAttribute("data-stage")).Should().Contain(["TransportAccepted", "ConsumerContinuityObserved", "DownstreamVerified"]);
        cut.FindAll("[data-testid=act-run] th[scope=col]").Select(h => h.TextContent).Should().NotContain(h => h.Contains("Person"), "shared columns are domain-neutral");
        cut.Find("[data-testid=act-run]").TextContent.Should().NotContain("Person persisted");
        cut.Find("[data-testid=act-event]").TextContent.Should().Contain("PersonPK 900000000", "the provider supplies the identity value shown in the neutral Synthetic identity column");
        cut.Find("[data-testid=act-provider-details]").TextContent.Should().Contain("syntheticBirthDate", "provider-specific values live in the provider section");
        cut.Find("[data-testid=act-export]").Click();
        _export.Verify(e => e.ExportActiveEventRun(It.Is<ActiveEventRunResult>(r => r.RunId == _api.Run.RunId), It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public void History_MixesProviders_FiltersOnTheBackend_AndShowsSignInFailuresAsAuthorization()
    {
        var cut = Render();
        cut.Find("[data-testid=act-history]").TriggerEvent("ontoggle", EventArgs.Empty);
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=act-history-item]").Select(i => i.GetAttribute("data-provider")).Should().Equal("skolenaervaer.cdc", "m2lb.person"));
        cut.Find("[data-testid=act-filter-provider]").Change("skolenaervaer.cdc");
        cut.WaitForAssertion(() => _api.Filters.Last().ExtensionId.Should().Be("skolenaervaer.cdc"));

        _api.HistoryStatus = HttpStatusCode.Unauthorized;
        cut.Find("[data-testid=act-filter-status]").Change("Completed");
        cut.WaitForAssertion(() => cut.Find("[data-testid=act-history-error]").TextContent.Should().Contain("Sign-in required"));
        _api.Filters.Last().Status.Should().Be(ActiveEventRunStatus.Completed);
        cut.FindAll("[data-testid=act-check]").Single(c => c.GetAttribute("data-key") == "authorization").GetAttribute("data-state").Should().Be("Blocked");
        cut.Find("[data-testid=act-start]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=act-sign-in]").GetAttribute("href").Should().Be("authentication/login");
    }

    [Fact]
    public void Export_IsGeneric_WithAProviderSection_AndNoPersonWordingInSharedParts()
    {
        var html = new ReportExportService().ExportActiveEventRun(_api.Run, "Project");
        html.Should().Contain("Active event test").And.Contain("Transport accepted").And.Contain("Consumer continuity").And.Contain("Downstream verified")
            .And.Contain("Provider details — M2LB Person CDC").And.Contain("Resource");
        html.Should().NotContain("Synthetic PersonPK").And.NotContain("Person persisted").And.NotContain("table Person");
        html.Should().NotContain("\"payload\"", "the body is never part of a run");
    }

    private sealed class FakeApi : IActiveEventsApiService
    {
        public Guid Snapshot { get; } = Guid.NewGuid();
        public List<ActiveEventRunRequest> Started { get; } = [];
        public List<ActiveEventHistoryFilter> Filters { get; } = [];
        public HttpStatusCode HistoryStatus { get; set; } = HttpStatusCode.OK;

        private static ActiveEventScenarioDescriptor Normal => new()
        {
            ExtensionId = "m2lb.person", ProviderDisplayName = "M2LB Person CDC", ScenarioId = "person.normal.create", ScenarioVersion = "1", DisplayName = "Normal Person",
            Category = "Transport", ResourceLabel = "dbo.Person", RequiredTransportType = "EventHub", SupportedOperations = [ActiveEventOperation.Create], RequiresDownstreamVerification = true,
            ResultDoesNotMean = "Without a downstream verifier it does not mean the record was stored.",
        };

        private static readonly ActiveEventProviderSummary[] ProviderList =
        [
            new() { ExtensionId = "m2lb.person", DisplayName = "M2LB Person CDC", Resources = ["dbo.Person"], ApplicableIntegrationIds = ["person"], Scenarios = [Normal] },
            new()
            {
                ExtensionId = "skolenaervaer.cdc", DisplayName = "Skolenærvær Testing", Resources = ["dbo.Utdanning", "dbo.ManglendeSkoletilbud"],
                NotApplicableReason = "No configured Event Hub integration was found for this CDC source (dbo.Utdanning / dbo.ManglendeSkoletilbud).",
                Scenarios =
                [
                    new() { ExtensionId = "skolenaervaer.cdc", ScenarioId = "skolenaervaer.utdanning.create", DisplayName = "Utdanning CDC: Valid create", SupportedOperations = [ActiveEventOperation.Create] },
                    new() { ExtensionId = "skolenaervaer.cdc", ScenarioId = "skolenaervaer.utdanning.update", DisplayName = "Utdanning CDC: Update", Support = ActiveEventScenarioSupport.NotAssessed, SupportDetail = "Not assessed (test)." },
                ],
            },
        ];

        public ActiveEventRunResult Run { get; } = new()
        {
            RunId = Guid.NewGuid(), Scenario = Normal, Status = ActiveEventRunStatus.CompletedWithLimitedEvidence, StartedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow,
            Target = new() { EnvironmentDisplayName = "M2LB DEV", EnvironmentType = "Development", IntegrationDisplayName = "BIRK Person CDC", TransportType = "EventHub", Resource = Hub, Endpoint = "evhns-dev.servicebus.windows.net" },
            Events = [new() { EventId = "e1", ExtensionId = "m2lb.person", Operation = ActiveEventOperation.Create, BodyBytes = 300, BodySha256 = new string('a', 64),
                SafeMetadata = new Dictionary<string, string> { ["syntheticIdentity"] = "PersonPK 900000000", ["syntheticBirthDate"] = "2006-09-09", ["role"] = "Synthetic create" } }],
            Evidence =
            [
                new() { Stage = ActiveEventEvidenceStage.SendAttempted, Status = ActiveEventEvidenceStatus.Observed, EventId = "e1" },
                new() { Stage = ActiveEventEvidenceStage.TransportAccepted, Status = ActiveEventEvidenceStatus.Observed, EventId = "e1", Detail = "accepted" },
                new() { Stage = ActiveEventEvidenceStage.ConsumerContinuityObserved, Status = ActiveEventEvidenceStatus.Observed, Detail = "progression only" },
                new() { Stage = ActiveEventEvidenceStage.ConsumerActivityObserved, Status = ActiveEventEvidenceStatus.NotVerified },
                new() { Stage = ActiveEventEvidenceStage.DownstreamVerified, Status = ActiveEventEvidenceStatus.NotVerified, EventId = "e1" },
            ],
            Limitations = ["Persistence is not verified."],
        };

        public Task<ActiveEventApiResult<ActiveEventEnvironmentTrust>> TrustAsync(string environmentId, CancellationToken ct = default) =>
            Task.FromResult(new ActiveEventApiResult<ActiveEventEnvironmentTrust>(new() { EnvironmentId = environmentId, Trusted = true, DisplayName = "M2LB DEV", EnvironmentType = "Development", ExecutionAllowed = true }, null));
        public Task<ActiveEventApiResult<IReadOnlyList<ActiveEventProviderSummary>>> ProvidersAsync(string environmentId, CancellationToken ct = default) =>
            Task.FromResult(new ActiveEventApiResult<IReadOnlyList<ActiveEventProviderSummary>>(ProviderList, null));
        public Task<ActiveEventApiResult<IReadOnlyList<ActiveEventScenarioDescriptor>>> ScenariosAsync(string environmentId, string integrationId, CancellationToken ct = default) =>
            Task.FromResult(new ActiveEventApiResult<IReadOnlyList<ActiveEventScenarioDescriptor>>(integrationId == "person" ? [Normal] : [], null));
        public Task<ActiveEventApiResult<ActiveEventReadiness>> ReadinessAsync(string environmentId, string integrationId, string extensionId, string scenarioId, Guid? snapshotId, CancellationToken ct = default) =>
            Task.FromResult(new ActiveEventApiResult<ActiveEventReadiness>(new()
            {
                CanRun = true, Scenario = Normal, ObservationTimeoutSeconds = 180,
                Target = new() { EnvironmentDisplayName = "M2LB DEV", EnvironmentType = "Development", Resource = Hub, Endpoint = "evhns-dev.servicebus.windows.net", Consumer = "$Default", ConsumerAssumed = true },
                SourceContract = new() { SourceSnapshotId = Snapshot, ContractFingerprint = "fp-1", ContractStatus = "Compatible" },
                SyntheticSummary = new Dictionary<string, string> { ["Synthetic identity"] = "PersonPK from the reserved range 900000000–900000099 (1 key(s))" },
                Checks =
                [
                    new("source-contract", "Source contract", ActiveEventReadinessState.Ready, "Compatible", ActiveEventReadinessCategory.SourceContract),
                    new("environment", "Trusted environment (DEV/QA only)", ActiveEventReadinessState.Ready, "M2LB DEV", ActiveEventReadinessCategory.TrustedEnvironment),
                    new("synthetic-key", "Synthetic test data policy", ActiveEventReadinessState.Ready, "Reserved range.", ActiveEventReadinessCategory.SyntheticData),
                    new("downstream", "Downstream verification", ActiveEventReadinessState.Partial, "No read-only verification exists.", ActiveEventReadinessCategory.DownstreamVerification),
                ],
            }, null));
        public Task<ActiveEventApiResult<ActiveEventRunResult>> StartAsync(ActiveEventRunRequest request, CancellationToken ct = default)
        {
            Started.Add(request);
            return Task.FromResult(new ActiveEventApiResult<ActiveEventRunResult>(Run, null));
        }
        public Task<ActiveEventApiResult<ActiveEventRunResult>> GetAsync(Guid runId, CancellationToken ct = default) => Task.FromResult(new ActiveEventApiResult<ActiveEventRunResult>(Run, null));
        public Task<bool> CancelAsync(Guid runId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<ActiveEventApiResult<IReadOnlyList<ActiveEventRunSummary>>> HistoryAsync(string environmentId, ActiveEventHistoryFilter filter, CancellationToken ct = default)
        {
            Filters.Add(filter);
            if (HistoryStatus != HttpStatusCode.OK)
                return Task.FromResult(new ActiveEventApiResult<IReadOnlyList<ActiveEventRunSummary>>(null, "Sign-in required: active event execution needs an authenticated BirkNext API token.", HistoryStatus));
            IReadOnlyList<ActiveEventRunSummary> items =
            [
                new() { RunId = Guid.NewGuid(), ExtensionId = "skolenaervaer.cdc", ProviderDisplayName = "Skolenærvær Testing", ScenarioName = "Utdanning CDC: Valid create", Status = ActiveEventRunStatus.SafetyBlocked, StartedAt = DateTimeOffset.UtcNow },
                new() { RunId = Run.RunId, ExtensionId = "m2lb.person", ProviderDisplayName = "M2LB Person CDC", ScenarioName = "Normal Person", Status = Run.Status, StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5), SendAttempted = true, EventCount = 1 },
            ];
            return Task.FromResult(new ActiveEventApiResult<IReadOnlyList<ActiveEventRunSummary>>(items.Where(i => filter.ExtensionId is null || i.ExtensionId == filter.ExtensionId).ToList(), null));
        }
    }
}
