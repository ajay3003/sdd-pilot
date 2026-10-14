using System.Net;
using BirkNext.Integrations;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// The shared integration-journey panel: overview per journey (configuration, source evidence, readiness, last run, verification), one tab per
/// backend-declared journey, delegated journeys pointing to Active tests, steps as separate evidence, truthful blocked readiness,
/// architecture rules, history and export. Pack/journey/step labels come from the backend contract.
/// </summary>
public sealed class IntegrationJourneyPanelTests : BunitContext
{
    private readonly FakeApi _api = new();
    private readonly Mock<IReportExportService> _export = new();
    private static readonly FrontendAnalysisProfile Profile = new() { Id = "dev-env", Name = "DEV", EnvironmentType = FrontendEnvironmentType.Development };

    public IntegrationJourneyPanelTests()
    {
        Services.AddSingleton<IIntegrationJourneysApiService>(_api);
        _export.Setup(e => e.ExportIntegrationJourneys(It.IsAny<IntegrationJourneyPackView>(), It.IsAny<ArchitectureRuleReport?>(), It.IsAny<IReadOnlyList<IntegrationJourneyRunSummary>>(), It.IsAny<string?>()))
            .Returns("<html></html>");
        Services.AddSingleton(_export.Object);
        Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<IntegrationJourneyPanel> Render(bool authenticationConfigured = false)
    {
        Services.AddSingleton(authenticationConfigured ? new ActiveEventAuthenticationState(true, "Configured (test).") : ActiveEventAuthenticationState.NotConfigured);
        return Render<IntegrationJourneyPanel>(p => p.Add(c => c.Profile, Profile));
    }

    [Fact]
    public void Overview_ShowsEveryJourneyWithItsOwnReadiness()
    {
        var cut = Render();
        cut.Find("[data-testid=ijp-pack-title]").TextContent.Should().Be("Skolenærvær Testing");
        cut.FindAll("[data-testid^=ijp-tab-]").Select(tab => tab.TextContent).Should().Equal("Overview", "Scenario capability", "Utdanningsdata", "Rapportmottak", "Periodeavslutning", "Architecture rules", "History");
        var rows = cut.FindAll("[data-testid=ijp-overview-row]");
        rows.Select(row => row.GetAttribute("data-readiness")).Should().Equal("Partial", "NotReady", "NotConfigured");
        rows[1].TextContent.Should().Contain("Not source verified").And.Contain("Not verified (no run)");
        cut.Find("[data-testid=ijp-trust]").GetAttribute("data-trusted").Should().Be("true");
    }

    [Fact]
    public void JourneyTab_ShowsStepsAsSeparateEvidence_PrerequisitesAndNotAssessedScenarios()
    {
        var cut = Render(authenticationConfigured: true);
        cut.Find("[data-testid=ijp-tab-rapportmottak]").Click();

        cut.Find("[data-testid=ijp-journey]").GetAttribute("data-journey").Should().Be("rapportmottak");
        cut.FindAll("[data-testid=ijp-step]").Select(step => step.GetAttribute("data-step")).Should().Equal("altinn-submit", "mm-xsd", "report-person");
        cut.FindAll("[data-testid=ijp-step]")[1].TextContent.Should().Contain("MM").And.Contain("Structural validation");
        var auth = cut.FindAll("[data-testid=ijp-prerequisite]").Single(item => item.GetAttribute("data-key") == "altinn-auth");
        auth.GetAttribute("data-state").Should().Be("NotConfigured");
        auth.TextContent.Should().Contain("Authentication not configured");
        cut.FindAll("[data-testid=ijp-scenario]").Single(item => item.GetAttribute("data-scenario") == "invalid-xsd").GetAttribute("data-support").Should().Be("NotAssessed");
        cut.Find("[data-testid=ijp-start]").HasAttribute("disabled").Should().BeTrue("the backend says the journey cannot run");
        cut.Find("[data-testid=ijp-run-note]").TextContent.Should().StartWith("Not runnable");
        cut.Find("[data-testid=ijp-maturity]").TextContent.Should().Contain("Source verified: no").And.Contain("Runtime verified: no");
    }

    [Fact]
    public void DelegatedJourney_PointsToActiveTests_AndOffersNoSecondRunner()
    {
        var cut = Render(authenticationConfigured: true);
        cut.Find("[data-testid=ijp-tab-utdanningsdata]").Click();
        cut.Find("[data-testid=ijp-delegated]").TextContent.Should().Contain("Active tests").And.Contain("skolenaervaer.cdc");
        cut.FindAll("[data-testid=ijp-start]").Should().BeEmpty();
    }

    [Fact]
    public void WithoutAuthentication_ExecutionIsDisabled_AndReviewStaysVisible()
    {
        _api.Pack = _api.Pack with { Journeys = [.. _api.Pack.Journeys.Select(view => view with { CanRun = true, Readiness = JourneyReadinessState.Ready })] };
        var cut = Render(authenticationConfigured: false);
        cut.Find("[data-testid=ijp-tab-rapportmottak]").Click();
        cut.Find("[data-testid=ijp-start]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=ijp-run-note]").TextContent.Should().StartWith("Execution not available").And.Contain("Authentication not configured");
    }

    [Fact]
    public async Task LocalTrustedExecution_FromTheBackend_EnablesRun_WithoutEntra_AndShowsNoSignInState()
    {
        Services.AddSingleton<IExecutionAccessApiService>(new FixedAccess(new ExecutionAccessView(ExecutionAccessMode.LocalTrusted, true,
            "Local trusted execution: loopback-only Development backend, request from the configured BirkNext frontend.")));
        _api.Pack = _api.Pack with { Journeys = [.. _api.Pack.Journeys.Select(view => view with { CanRun = true, Readiness = JourneyReadinessState.Ready })] };
        var cut = Render(authenticationConfigured: false);
        cut.Find("[data-testid=ijp-tab-rapportmottak]").Click();

        cut.Find("[data-testid=ijp-start]").HasAttribute("disabled").Should().BeFalse("the backend reported execution access");
        cut.Find("[data-testid=ijp-run-note]").TextContent.Should().StartWith("Ready").And.NotContain("Authentication not configured");
        cut.Markup.Should().NotContain("Entra").And.NotContain("Sign in with").And.NotContain("authentication/login");
        cut.Find("[data-testid=ijp-start]").Click();
        cut.Find("[data-testid=ijp-ack]").Change(true);
        await cut.Find("[data-testid=ijp-confirm-run]").ClickAsync(new MouseEventArgs());
        _api.Started.Should().ContainSingle("one click (after the confirmation) starts the run; the backend re-checks the permission");
    }

    [Fact]
    public async Task ResolvedDefaults_ArePreselected_AndSent_ButAnExplicitChooseIsRespected()
    {
        Services.AddSingleton<IExecutionAccessApiService>(new FixedAccess(new ExecutionAccessView(ExecutionAccessMode.LocalTrusted, true, "Local trusted execution (test).")));
        JourneyScenarioInput[] inputs =
        [
            new("reporter", "Reporter", JourneyScenarioInputKind.Choice, true, "", [new("reporter-a", "Reporter A"), new("interactive-session", "Interactive")], Default: "reporter-a"),
            new("child", "Approved synthetic child", JourneyScenarioInputKind.Choice, false, "", [new("synthetic-child-1", "Synthetic child 1"), new("synthetic-child-2", "Synthetic child 2")],
                Default: "synthetic-child-1"),
        ];
        _api.Pack = _api.Pack with
        {
            Journeys = [.. _api.Pack.Journeys.Select(view => view.Journey.JourneyId != "rapportmottak" ? view : view with
            {
                CanRun = true, Readiness = JourneyReadinessState.Ready, RunnableScenarios = ["valid-report"],
                Journey = view.Journey with { Scenarios = [.. view.Journey.Scenarios.Select(item => item.ScenarioId == "valid-report" ? item with { Inputs = inputs } : item)] },
            })],
        };
        var cut = Render();
        cut.Find("[data-testid=ijp-tab-rapportmottak]").Click();

        cut.Find("[data-key=reporter] select").GetAttribute("value").Should().Be("reporter-a", "the only approved reporter is resolved, not typed");
        cut.Find("[data-key=child] select").GetAttribute("value").Should().Be("synthetic-child-1");
        cut.Find("[data-key=child] select").Change("");
        cut.Find("[data-testid=ijp-start]").Click();
        cut.Find("[data-testid=ijp-ack]").Change(true);
        await cut.Find("[data-testid=ijp-confirm-run]").ClickAsync(new MouseEventArgs());

        var sent = _api.Started.Should().ContainSingle().Subject.Inputs;
        sent.Should().Contain("reporter", "reporter-a").And.NotContainKey("child", "the tester chose \"Choose…\"; a default never overrides that");
    }

    [Fact]
    public void ABackendDenial_KeepsRunDisabled_WithTheBackendsReason()
    {
        Services.AddSingleton<IExecutionAccessApiService>(new FixedAccess(new ExecutionAccessView(ExecutionAccessMode.None, false,
            "Execution is not permitted for this session. Local trusted execution needs a loopback connection to the backend.")));
        _api.Pack = _api.Pack with { Journeys = [.. _api.Pack.Journeys.Select(view => view with { CanRun = true, Readiness = JourneyReadinessState.Ready })] };
        var cut = Render(authenticationConfigured: false);
        cut.Find("[data-testid=ijp-tab-rapportmottak]").Click();

        cut.Find("[data-testid=ijp-start]").HasAttribute("disabled").Should().BeTrue("the frontend never overrides a backend denial");
        cut.Find("[data-testid=ijp-run-note]").TextContent.Should().Contain("needs a loopback connection");
    }

    private sealed class FixedAccess(ExecutionAccessView view) : IExecutionAccessApiService
    {
        public Task<ExecutionAccessView> GetAsync(CancellationToken ct = default) => Task.FromResult(view);
    }

    [Fact]
    public async Task ReadyJourney_RequiresExplicitConfirmation_BeforeTheBackendIsCalled()
    {
        _api.Pack = _api.Pack with { Journeys = [.. _api.Pack.Journeys.Select(view => view with { CanRun = true, Readiness = JourneyReadinessState.Ready })] };
        var cut = Render(authenticationConfigured: true);
        cut.Find("[data-testid=ijp-tab-rapportmottak]").Click();
        cut.Find("[data-testid=ijp-start]").Click();
        cut.Find("[data-testid=ijp-confirm]").TextContent.Should().Contain("may change downstream state");
        cut.Find("[data-testid=ijp-confirm-run]").HasAttribute("disabled").Should().BeTrue();
        _api.Started.Should().BeEmpty();

        cut.Find("[data-testid=ijp-ack]").Change(true);
        await cut.Find("[data-testid=ijp-confirm-run]").ClickAsync(new MouseEventArgs());
        _api.Started.Should().ContainSingle().Which.Should().Match<IntegrationJourneyRunRequest>(request => request.Confirmed && request.JourneyId == "rapportmottak" && request.ScenarioId == "valid-report");
        cut.WaitForAssertion(() => cut.Find("[data-testid=ijp-last-run]").GetAttribute("data-state").Should().Be("Blocked"));
    }

    [Fact]
    public void ArchitectureRules_AndHistory_AreLoadedFromTheBackend()
    {
        var cut = Render(authenticationConfigured: true);
        cut.Find("[data-testid=ijp-tab-rules]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=ijp-rule]").Should().HaveCount(2));
        cut.FindAll("[data-testid=ijp-rule]").Select(rule => rule.GetAttribute("data-outcome")).Should().Equal("NotAssessed", "PotentialDeviation");
        cut.Markup.Should().Contain("MM/Program.cs:42");

        cut.Find("[data-testid=ijp-tab-history]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=ijp-history-row]").Should().ContainSingle());
        cut.Find("[data-testid=ijp-history]").TextContent.Should().Contain("Rapportmottak").And.Contain("0 of 3 steps verified");
    }

    [Fact]
    public void HistoryWithoutAuthentication_ExplainsWhy()
    {
        _api.HistoryStatus = HttpStatusCode.Unauthorized;
        var cut = Render(authenticationConfigured: false);
        cut.Find("[data-testid=ijp-tab-history]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=ijp-history-error]").TextContent.Should().Contain("Authentication not configured"));
    }

    [Fact]
    public void TabsSupportArrowKeys()
    {
        var cut = Render();
        cut.Find("[data-testid=ijp-tab-overview]").KeyDown(new KeyboardEventArgs { Key = "ArrowRight" });
        cut.Find("[data-testid=ijp-tabpanel]").GetAttribute("data-tab").Should().Be("scenarios");
        cut.Find("[data-testid=ijp-tab-scenarios]").KeyDown(new KeyboardEventArgs { Key = "ArrowRight" });
        cut.Find("[data-testid=ijp-tabpanel]").GetAttribute("data-tab").Should().Be("utdanningsdata");
        cut.Find("[data-testid=ijp-tab-utdanningsdata]").GetAttribute("aria-selected").Should().Be("true");
        cut.Find("[data-testid=ijp-tab-utdanningsdata]").KeyDown(new KeyboardEventArgs { Key = "End" });
        cut.Find("[data-testid=ijp-tabpanel]").GetAttribute("data-tab").Should().Be("history");
    }

    [Fact]
    public void Export_UsesTheSharedExportService()
    {
        var cut = Render();
        cut.Find("[data-testid=ijp-export]").Click();
        cut.WaitForAssertion(() => _export.Verify(e => e.ExportIntegrationJourneys(It.Is<IntegrationJourneyPackView>(p => p.PackId == "skolenaervaer"), It.IsAny<ArchitectureRuleReport?>(),
            It.IsAny<IReadOnlyList<IntegrationJourneyRunSummary>>(), It.IsAny<string?>()), Times.Once));
    }

    [Fact]
    public void ScenarioCapability_ShowsEveryScenarioStatus_KeepsExecutionApartFromVerification_AndShowsTheEvidenceLimitations()
    {
        _api.Pack = CapabilityPack();
        var cut = Render();
        cut.Find("[data-testid=ijp-tab-scenarios]").Click();

        var cards = cut.FindAll("[data-testid=ijp-capability]");
        // Not-assessed scenarios fold into their collapsed group after the others.
        cards.Select(card => card.GetAttribute("data-status")).Should().Equal("Runnable", "ConditionallyRunnable", "OperatorRequired", "Blocked", "NotAssessed");
        cards.Select(card => card.QuerySelector("[data-testid=ijp-capability-status]")!.TextContent)
            .Should().Equal("Runnable", "Conditionally runnable", "Operator required", "Blocked", "Not assessed");
        var runnable = cards[0];
        runnable.QuerySelector("[data-testid=ijp-capability-execution]")!.TextContent.Should().Be("Available (after confirmation)");
        runnable.QuerySelector("[data-testid=ijp-capability-verification]")!.TextContent.Should().StartWith("Partial —", "a runnable scenario is not verifiable beyond what is declared");
        cards[2].QuerySelector("[data-testid=ijp-capability-execution]")!.TextContent.Should().Be("Not by BirkNext (operator)");
        cut.FindAll("[data-testid=ijp-capability-details]").Should().OnlyContain(details => !details.HasAttribute("open"), "requirements and gaps stay collapsed");
        cards[3].QuerySelector("[data-testid=ijp-capability-details]")!.TextContent.Should().Contain("Unmet prerequisites").And.Contain("Altinn app contract");
        var notAssessed = cut.Find("[data-testid=ijp-not-assessed]");
        notAssessed.HasAttribute("open").Should().BeFalse();
        notAssessed.QuerySelector("summary")!.TextContent.Should().Be("Not assessed (1)");
        cut.Find("[data-testid=ijp-capability-summary]").TextContent.Should().Be("1 runnable · 1 conditionally runnable · 1 operator required · 1 not assessed · 1 blocked");
        cut.Markup.Should().NotContain("Passed");
        cut.FindAll("[data-testid=ijp-evidence-limitation]").Select(item => item.GetAttribute("data-key")).Should().Equal("altinn-tt02", "period-lock");
        cut.Find("[data-testid=ijp-evidence-limitation][data-key=altinn-tt02]").TextContent.Should().Contain("Blocked: ID-porten TEST configuration incomplete").And.Contain("Owner:");
        cut.FindAll("button").Should().NotContain(button => button.TextContent.Contains("Run", StringComparison.Ordinal), "the capability view starts nothing");
    }

    [Fact]
    public void CdcScenarios_AreGroupedBySourceTable_WithAStatusSummary_AndTheNotAssessedOnesCollapsed()
    {
        var profile = new JourneyScenarioCapabilityProfile("BiRK CDC → Event Hub → SkoleAdapter", "Active Event Testing", "Event Hub integration", "Reserved synthetic keys",
            "Approved test hub", JourneyResultVerification.Partial, "Transport only", "SkoleAdapter source", "SkoleAdapter team", "Supply the source");
        JourneyScenarioCapability Row(string table, string kind, JourneyScenarioCapabilityStatus status) => new($"skolenaervaer.{table}.{kind}", $"{kind}", $"dbo.{table}", status, "detail", [], profile);
        var pack = SamplePack();
        _api.Pack = pack with
        {
            Journeys = [.. pack.Journeys.Select(view => view.Journey.JourneyId != "utdanningsdata" ? view : view with
            {
                ScenarioCapabilities =
                [
                    Row("Utdanning", "create", JourneyScenarioCapabilityStatus.ConditionallyRunnable), Row("Utdanning", "update", JourneyScenarioCapabilityStatus.NotAssessed),
                    Row("Utdanning", "delete", JourneyScenarioCapabilityStatus.NotAssessed),
                    Row("ManglendeSkoletilbud", "create", JourneyScenarioCapabilityStatus.ConditionallyRunnable), Row("ManglendeSkoletilbud", "update", JourneyScenarioCapabilityStatus.NotAssessed),
                ],
            })],
        };
        var cut = Render();
        cut.Find("[data-testid=ijp-tab-scenarios]").Click();

        var groups = cut.FindAll("[data-testid=ijp-capabilities][data-journey=utdanningsdata] [data-testid=ijp-capability-group]");
        groups.Select(group => group.GetAttribute("data-resource")).Should().Equal("dbo.Utdanning", "dbo.ManglendeSkoletilbud");
        groups[0].QuerySelector("h5")!.TextContent.Should().Be("Source table dbo.Utdanning");
        groups[0].QuerySelector("[data-testid=ijp-capability-summary]")!.TextContent.Should().Be("1 conditionally runnable · 2 not assessed");
        groups[0].QuerySelector("[data-testid=ijp-not-assessed] summary")!.TextContent.Should().Be("Not assessed (2)");
        groups[0].QuerySelectorAll("[data-testid=ijp-capability]").Select(card => card.QuerySelector(".ijp-capability-name")!.TextContent).Should().Equal("create", "update", "delete");
        groups[1].QuerySelector("[data-testid=ijp-capability-summary]")!.TextContent.Should().Be("1 conditionally runnable · 1 not assessed");
        cut.FindAll("[data-testid=ijp-capability-verification]").Should().OnlyContain(item => item.TextContent.StartsWith("Partial"), "grouping never changes what can be verified");
    }

    internal static IntegrationJourneyPackView CapabilityPack()
    {
        var profile = new JourneyScenarioCapabilityProfile("Altinn TT02 → MU → MM → Skoletjenesten", "Integration journey runner", "ID-porten TEST client", "Approved synthetic reporter",
            "PRIV role", JourneyResultVerification.Partial, "Altinn steps only", "MU and MM observers", "BirkNext operator", "Complete the configuration");
        JourneyScenarioCapability Row(string id, JourneyScenarioCapabilityStatus status, params string[] blockers) => new(id, id, null, status, $"{status} detail", blockers, profile);
        var pack = SamplePack();
        return pack with
        {
            Journeys = [.. pack.Journeys.Select(view => view.Journey.JourneyId != "rapportmottak" ? view : view with
            {
                ScenarioCapabilities =
                [
                    Row("valid-report", JourneyScenarioCapabilityStatus.Runnable),
                    Row("person-not-found", JourneyScenarioCapabilityStatus.ConditionallyRunnable, "Report intake: not configured."),
                    Row("invalid-xsd", JourneyScenarioCapabilityStatus.NotAssessed),
                    Row("close-valid-period", JourneyScenarioCapabilityStatus.OperatorRequired),
                    Row("overlapping-report", JourneyScenarioCapabilityStatus.Blocked, "Altinn app contract: incomplete."),
                ],
            })],
            EvidenceLimitations =
            [
                new("altinn-tt02", "Altinn TT02 submission", "Blocked: ID-porten TEST configuration incomplete", "Missing: Client secret.", "BirkNext operator"),
                new("period-lock", "Period lock", "Operator required", "Skoletjenesten locks periods itself.", "Skole operations"),
            ],
        };
    }

    internal static IntegrationJourneyPackView SamplePack()
    {
        static IntegrationJourneyView View(string id, string name, JourneyReadinessState readiness, JourneyExecutionMode mode, JourneyStepDefinition[] steps, JourneyPrerequisite[] prerequisites,
            JourneyScenarioDescriptor[] scenarios) => new()
        {
            Journey = new IntegrationJourneyDefinition
            {
                PackId = "skolenaervaer", JourneyId = id, DisplayName = name, Description = $"{name} journey.", ExecutionMode = mode,
                DelegatedProviderId = mode == JourneyExecutionMode.DelegatedToActiveEvent ? "skolenaervaer.cdc" : null, Steps = steps, Scenarios = scenarios, EvidenceBasis = "Documented.",
            },
            Readiness = readiness, ReadinessSummary = "1 of 6 mandatory prerequisites are ready.", Prerequisites = prerequisites,
            StepStatus = steps.Select(step => new JourneyStepStatus(step.StepId, false, false, false, JourneyStepState.NotAssessed, "No executor or observer is registered for this step.")).ToArray(),
            Maturity = new JourneyMaturity(false, false, false, false, ""),
        };

        return new IntegrationJourneyPackView
        {
            PackId = "skolenaervaer", PackVersion = "1", DisplayName = "Skolenærvær Testing", Description = "Three journeys.", EnvironmentId = "dev-env", EnvironmentTrusted = true,
            EnvironmentDetail = "Backend-owned trusted environment.",
            Journeys =
            [
                View("utdanningsdata", "Utdanningsdata", JourneyReadinessState.Partial, JourneyExecutionMode.DelegatedToActiveEvent,
                    [new("event-hub", "Change event delivered to Event Hub", JourneyStepKind.BrokerDelivery, "Event Hub", "Accepted", IntegrationKey: "education-cdc")],
                    [new("source:SkoleAdapter", "SkoleAdapter source", JourneyPrerequisiteCategory.SourceEvidence, JourneyReadinessState.NotReady, "Not source verified.")],
                    [new("active-event-scenarios", "Active Event scenarios", "Run from Active tests.", JourneyScenarioSupport.Supported)]),
                View("rapportmottak", "Rapportmottak", JourneyReadinessState.NotReady, JourneyExecutionMode.JourneyRunner,
                    [new("altinn-submit", "Submit report to the Altinn test environment", JourneyStepKind.ExternalSubmission, "External reporter", "Sent"),
                     new("mm-xsd", "MM validates the report against the XSD", JourneyStepKind.StructuralValidation, "MM", "Validated"),
                     new("report-person", "Skolenærværsrapport performs the Person lookup", JourneyStepKind.IdentityLookup, "Skolenærværsrapport", "Looked up")],
                    [new("altinn-auth", "Altinn authentication", JourneyPrerequisiteCategory.Authentication, JourneyReadinessState.NotConfigured, "Authentication not configured: no token source."),
                     new("source:MM", "MM source", JourneyPrerequisiteCategory.SourceEvidence, JourneyReadinessState.NotReady, "Not source verified.")],
                    [new("valid-report", "Valid report", "One synthetic report.", JourneyScenarioSupport.Supported),
                     new("invalid-xsd", "Invalid XSD", "Reviewed invalid payload.", JourneyScenarioSupport.NotAssessed, "Not assessed: no reviewed fixture.")]),
                View("periodeavslutning", "Periodeavslutning", JourneyReadinessState.NotConfigured, JourneyExecutionMode.JourneyRunner,
                    [new("publish", "SkolenaervaersperiodeLukket published to Service Bus", JourneyStepKind.Publication, "Skolenærværsrapport", "Published")],
                    [new("integration:period-closed", "Skolenærvær – Period Closed", JourneyPrerequisiteCategory.Integration, JourneyReadinessState.NotConfigured, "No configured ServiceBus integration was found.")],
                    [new("close-valid-period", "Close valid period", "Close one synthetic period.", JourneyScenarioSupport.Supported)]),
            ],
        };
    }

    private sealed class FakeApi : IIntegrationJourneysApiService
    {
        public IntegrationJourneyPackView Pack { get; set; } = SamplePack();
        public List<IntegrationJourneyRunRequest> Started { get; } = [];
        public HttpStatusCode HistoryStatus { get; set; } = HttpStatusCode.OK;

        public Task<ActiveEventApiResult<IReadOnlyList<IntegrationJourneyPackView>>> PacksAsync(string environmentId, CancellationToken ct = default) =>
            Task.FromResult(new ActiveEventApiResult<IReadOnlyList<IntegrationJourneyPackView>>([Pack], null, HttpStatusCode.OK));

        public Task<ActiveEventApiResult<ArchitectureRuleReport>> RulesAsync(string environmentId, string packId, CancellationToken ct = default) =>
            Task.FromResult(new ActiveEventApiResult<ArchitectureRuleReport>(new ArchitectureRuleReport(packId, Guid.NewGuid(), "source.zip",
            [
                new("rapport-person", "Skolenærværsrapport owns the Person lookup", "Skolenærværsrapport", "Person", ArchitectureRuleExpectation.MustDependOn, ArchitectureRuleOutcome.NotAssessed,
                    "Not source verified: no Skolenærværsrapport component is present in this snapshot.", [], "Source Analysis snapshot source.zip"),
                new("mm-no-person", "MM performs no Person lookup", "MM", "Person", ArchitectureRuleExpectation.MustNotDependOn, ArchitectureRuleOutcome.PotentialDeviation,
                    "Potential deviation: review the cited source.", [new("MM/Program.cs", 42, "AddHttpClient<IPersonClient>", "Http dependency")], "Source Analysis snapshot source.zip"),
            ], ["Rules are evaluated on source structure only."]), null, HttpStatusCode.OK));

        public Task<ActiveEventApiResult<IntegrationJourneyRun>> StartAsync(IntegrationJourneyRunRequest request, CancellationToken ct = default)
        {
            Started.Add(request);
            return Task.FromResult(new ActiveEventApiResult<IntegrationJourneyRun>(new IntegrationJourneyRun
            {
                RunId = Guid.NewGuid(), PackId = request.PackId, JourneyId = request.JourneyId, ScenarioId = request.ScenarioId, OverallState = JourneyRunState.Blocked,
                StateReason = "Blocked before execution.", Steps = [new() { StepId = "altinn-submit", Label = "Submit", State = JourneyStepState.NotAssessed, Reason = "Not executed." }],
            }, null, HttpStatusCode.OK));
        }

        public Task<ActiveEventApiResult<IntegrationJourneyRun>> GetAsync(Guid runId, CancellationToken ct = default) =>
            Task.FromResult(new ActiveEventApiResult<IntegrationJourneyRun>(null, "Not found.", HttpStatusCode.NotFound));

        public Task<ActiveEventApiResult<IReadOnlyList<IntegrationJourneyRunSummary>>> HistoryAsync(string environmentId, string packId, string? journeyId, JourneyRunState? state, CancellationToken ct = default) =>
            Task.FromResult(HistoryStatus == HttpStatusCode.OK
                ? new ActiveEventApiResult<IReadOnlyList<IntegrationJourneyRunSummary>>(
                    [new IntegrationJourneyRunSummary(Guid.NewGuid(), packId, "rapportmottak", "valid-report", environmentId, JourneyRunState.Blocked, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, 3, 1)], null, HttpStatusCode.OK)
                : new ActiveEventApiResult<IReadOnlyList<IntegrationJourneyRunSummary>>(null, "Sign-in required.", HistoryStatus));
    }
}
