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
/// Message Verification UI: the scenario's own inputs drive the form, the shared execution gate decides whether it can run, the run goes to
/// the existing journey start endpoint, and the stored record is shown — journey by system, assertions, correlation and missing evidence.
/// Evidence stages are never shown green, HTTP 200 is never an overall pass, and earlier verifications reopen unchanged.
/// </summary>
public sealed class MessageVerificationPanelTests : BunitContext
{
    private readonly FakeApi _api = new();

    public MessageVerificationPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IIntegrationJourneysApiService>(_api);
        Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        Services.AddSingleton<IReportExportService>(new ReportExportService());
        Services.AddSingleton<IExecutionAccessApiService>(new FixedAccess(new ExecutionAccessView(ExecutionAccessMode.LocalTrusted, true, "Local trusted execution.")));
        Services.AddSingleton(ActiveEventAuthenticationState.NotConfigured);
    }

    private IRenderedComponent<MessageVerificationPanel> Render() => Render<MessageVerificationPanel>(p => p.Add(c => c.EnvironmentId, "dev-env"));

    [Fact]
    public void TheEmptyState_ShowsTheProviders_AndTheScenariosOwnChoices_AndNeedsATargetBeforeItCanRun()
    {
        var cut = Render();

        cut.FindAll("[data-testid=mvp-provider]").Select(item => item.TextContent).Should().Contain(text => text.Contains("BirkNext run history"))
            .And.Contain(text => text.Contains("MM outcome events"));
        // A compact provider grid: availability (Available / Not ready / Not available) and the reason, never a verdict on the message.
        cut.FindAll("[data-testid=mvp-providers] th[role=columnheader]").Select(th => th.TextContent).Should().Equal("Evidence provider", "Availability", "Reason / detail");
        cut.FindAll("[data-testid=mvp-provider-state]").Select(state => state.TextContent).Should().OnlyContain(state => state == "Available" || state == "Not ready" || state == "Not available");
        cut.Find("[data-testid=mvp-provider][data-state=NotConfigured] [data-testid=mvp-provider-state]").TextContent.Should().Be("Not ready", "configured-but-missing evidence is not a failure");
        cut.Find("[data-testid=mvp-read-only]").TextContent.Should().Contain("never sends, submits or changes a message");
        // The form is grouped: what to verify, which identifiers, which observation window.
        cut.FindAll("[data-testid=mvp-form] legend").Select(l => l.TextContent).Should().Equal("Verification target", "Identifiers", "Observation");
        cut.Find("[data-testid=mvp-group-identifiers] [data-testid=mvp-instance]").Should().NotBeNull();
        cut.Find("[data-testid=mvp-group-identifiers] [data-testid=mvp-message]").Should().NotBeNull();
        cut.Find("[data-testid=mvp-group-target] [data-testid=mvp-source-run]").Should().NotBeNull();
        cut.Find("[data-testid=mvp-group-observation] [data-testid=mvp-window]").Should().NotBeNull();
        cut.Find("[data-testid=mvp-verify]").GetAttribute("aria-describedby").Should().NotBeNullOrEmpty("the disabled state explains why");
        cut.FindAll("[data-testid=mvp-expected] option").Select(item => item.GetAttribute("value")).Should().Equal("valid-report", "person-not-found", "invalid-xsd");
        cut.Find("[data-testid=mvp-verify]").HasAttribute("disabled").Should().BeTrue("no run, instance or message id is given");
        cut.Find("[data-testid=mvp-note]").TextContent.Should().StartWith("Choose an existing run");
        cut.Find("[data-testid=mvp-history-empty]").TextContent.Should().Contain("No message verification");
        _api.Started.Should().BeEmpty();
    }

    [Fact]
    public void WithoutExecutionAccess_NothingCanBeStarted()
    {
        Services.AddSingleton<IExecutionAccessApiService>(new FixedAccess(new ExecutionAccessView(ExecutionAccessMode.None, false, "Sign in to run.")));
        var cut = Render();
        cut.Find("[data-testid=mvp-message]").Change(Record.Message.ToString("D"));

        cut.Find("[data-testid=mvp-verify]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=mvp-note]").TextContent.Should().StartWith("Not available: Sign in to run.");
    }

    [Fact]
    public void AnExistingRun_IsVerifiedThroughTheJourneyEngine_AndTheResultKeepsEvidenceApartFromVerdicts()
    {
        var cut = Render();
        cut.Find("[data-testid=mvp-source-run]").Change(FakeApi.SourceRunId.ToString("D"));
        cut.Find("[data-testid=mvp-form]").Submit();

        var request = _api.Started.Single();
        request.ScenarioId.Should().Be("message-verification");
        request.JourneyId.Should().Be("rapportmottak");
        request.Confirmed.Should().BeTrue();
        request.Inputs.Should().Contain("sourceRun", FakeApi.SourceRunId.ToString("D")).And.Contain("expectedOutcome", "valid-report").And.Contain("observationWindow", "72")
            .And.NotContainKey("altinnInstance");
        cut.Find("[data-testid=mvp-result]").GetAttribute("data-verdict").Should().Be("NotEvaluated");
        cut.Find("[data-testid=mvp-verdict]").TextContent.Should().Be("Not evaluated", "Mottatt is receipt, not a business pass");
        cut.FindAll("[data-testid=mvp-system]").Select(item => item.GetAttribute("data-system")).Should().Equal("Altinn", "MM", "Skoletjenesten", "Period and coverage", "Bufdata");
        cut.FindAll("[data-testid=mvp-stage]").Should().OnlyContain(item => !item.ClassList.Contains("mvp-pill-ready"), "an observed stage is never shown as a pass");
        cut.FindAll("[data-testid=mvp-assertion]").Select(item => item.GetAttribute("data-verdict")).Should().Equal("Passed", "NotEvaluated");
        cut.Find("[data-testid=mvp-correlations]").TextContent.Should().Contain("Explicit mapping observed").And.Contain("Stated by the tester (not verified)");
        cut.Find("[data-testid=mvp-missing] summary").TextContent.Should().Be("Missing evidence (1)");
        cut.Find("[data-testid=mvp-evidence]").HasAttribute("open").Should().BeFalse();
        cut.FindAll("[data-testid=mvp-history-row]").Should().HaveCount(1, "the new verification is listed from the existing run history");
    }

    [Fact]
    public void AMessageIdLookup_SendsOnlyTheId_AndTheInstanceIdIsClearedAfterUse()
    {
        var cut = Render();
        cut.Find("[data-testid=mvp-instance]").Change("6a1b2c3d-0000-4000-8000-000000000001");
        cut.Find("[data-testid=mvp-message]").Change(Record.Message.ToString("D"));
        cut.Find("[data-testid=mvp-expected]").Change("invalid-xsd");
        cut.Find("[data-testid=mvp-form]").Submit();

        _api.Started.Single().Inputs.Should().Contain("messageId", Record.Message.ToString("D")).And.Contain("expectedOutcome", "invalid-xsd");
        cut.Find("[data-testid=mvp-instance]").GetAttribute("value").Should().BeEmpty("the full instance id is not kept in the browser");
    }

    [Fact]
    public void AccessDenied_IsShownAsBlocked_WithItsProvider_AndTheNextAction()
    {
        _api.Next = Record.Run(Record.Denied());
        var cut = Render();
        cut.Find("[data-testid=mvp-message]").Change(Record.Message.ToString("D"));
        cut.Find("[data-testid=mvp-form]").Submit();

        cut.Find("[data-testid=mvp-verdict]").TextContent.Should().Be("Blocked");
        cut.Find("[data-testid=mvp-record-providers] [data-provider=mm-telemetry]").GetAttribute("data-state").Should().Be("AccessDenied");
        cut.Find("[data-testid=mvp-record-providers] [data-provider=mm-telemetry]").TextContent.Should().Contain("Access denied");
        cut.Find("[data-testid=mvp-missing]").TextContent.Should().Contain("Grant BirkNext's backend identity read access");
        cut.FindAll("[data-testid=mvp-stage][data-stage=AccessDenied]").Should().ContainSingle();
    }

    [Fact]
    public void AnEarlierVerification_Reopens_AsRecorded_AndRefreshPrefillsANewOneInsteadOfOverwriting()
    {
        _api.History = [new(Record.VerificationId, "skolenaervaer", "rapportmottak", "message-verification", "dev-env", JourneyRunState.Partial, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 3, 15, 0)];
        var cut = Render();
        cut.Find("[data-testid=mvp-open]").Click();

        _api.Opened.Should().Equal(Record.VerificationId);
        cut.Find("[data-testid=mvp-result]").TextContent.Should().Contain(Record.Message.ToString("D"));
        cut.Find("[data-testid=mvp-open]").GetAttribute("aria-pressed").Should().Be("true");
        cut.Find("[data-testid=mvp-refresh]").Click();
        cut.Find("[data-testid=mvp-message]").GetAttribute("value").Should().Be(Record.Message.ToString("D"));
        _api.Started.Should().BeEmpty("refresh only prepares a new verification; nothing runs until it is confirmed");
    }

    [Fact]
    public void TheExport_UsesTheSharedReportTemplate_WithSafeEscapedEvidence_AndEveryTableInAScrollRegion()
    {
        var run = Record.Run(Record.Delivered() with { VerdictReason = "<script>alert(1)</script>" });
        var html = new ReportExportService().ExportMessageVerification(run, "Project");

        html.Should().Contain("Processing journey").And.Contain("Test assertions").And.Contain("Correlation").And.Contain("Missing evidence").And.Contain("Evidence providers");
        html.Should().Contain("HTTP 200 (Mottatt) is receipt, never a business pass").And.Contain("Skoletjenesten accepted the report");
        html.Should().NotContain("<script>alert(1)</script>").And.NotContainAny("Bearer ", "Aktoer");
        System.Text.RegularExpressions.Regex.Matches(html, "<table>").Count.Should().Be(System.Text.RegularExpressions.Regex.Matches(html, "<div class=\"table-scroll\" role=\"region\"").Count);

        _api.Next = run;
        var cut = Render();
        cut.Find("[data-testid=mvp-message]").Change(Record.Message.ToString("D"));
        cut.Find("[data-testid=mvp-form]").Submit();
        cut.Find("[data-testid=mvp-export]").Click();
        JSInterop.Invocations.Should().Contain(item => item.Identifier == "downloadHtmlFile");
    }

    [Fact]
    public void WithoutTheScenario_ItSaysSo_AndOffersNothing()
    {
        _api.WithScenario = false;
        var cut = Render();
        cut.Find("[data-testid=mvp-unavailable]").TextContent.Should().Contain("not available");
        cut.FindAll("[data-testid=mvp-verify]").Should().BeEmpty();
    }

    // ── Fixtures ────────────────────────────────────────────────────────────────────────────────────────────────

    internal static class Record
    {
        public static readonly Guid Message = Guid.Parse("8c0f6a2e-1b2c-4d3e-8f40-0123456789ab");
        public static readonly Guid VerificationId = Guid.Parse("11111111-2222-4333-8444-555555555555");

        public static MessageVerificationRecord Delivered() => new()
        {
            RuleVersion = "1", ExpectedScenario = "valid-report", ExpectedScenarioName = "Valid report", TargetKey = $"message:{Message:D}", TargetLabel = $"MU message {Message:D}",
            MessageId = Message.ToString("D"), WindowHours = 72, EvaluatedAt = DateTimeOffset.Parse("2026-10-14T09:00:00Z"),
            Verdict = MessageVerificationVerdict.NotEvaluated, VerdictReason = "Not evaluated: the evidence does not prove Skoletjenesten accepted the report (Godkjent).",
            Providers =
            [
                new("birknext-history", "BirkNext run history (Altinn steps)", "Altinn", MessageEvidenceProviderState.Available, "Run found.", "Recorded run.", DateTimeOffset.UtcNow, "BirkNext operator"),
                new("mm-telemetry", "MM outcome events", "MM", MessageEvidenceProviderState.Available, "1 record.", "MM outcome event.", DateTimeOffset.UtcNow, "MM team"),
            ],
            Observations =
            [
                new("Altinn", MessageEvidenceStage.Confirmed, "Altinn confirmed the instance completed.", "birknext-history", DateTimeOffset.UtcNow, "BirkNext run id (exact)", [], "Not MU receipt."),
                new("MM", MessageEvidenceStage.MmProcessingObserved, "MM outcome Mottatt, attempt 1.", "mm-telemetry", DateTimeOffset.UtcNow, "MuMeldingId (exact)", [new("MM outcome", "Mottatt")], ""),
                new("Skoletjenesten", MessageEvidenceStage.NotObserved, "No record.", "skole-telemetry", null, "MeldingId", [], "Not observed is not failed."),
                new("Period and coverage", MessageEvidenceStage.NotEvaluated, "Needs an approved GraphQL client.", "skole-graphql", null, "—", [], ""),
                new("Bufdata", MessageEvidenceStage.NotEvaluated, "No provider.", "bufdata", null, "—", [], ""),
            ],
            Correlations =
            [
                new("MU message id", Message.ToString("D"), "MM queue message id (KoeMeldingId)", "koe-1", "mm-telemetry", DateTimeOffset.UtcNow, "Both ids on the same MM outcome event", MessageCorrelationCertainty.ExplicitMappingObserved, ""),
                new("Altinn instance", "inst-6a1b2c3d", "MU message id", Message.ToString("D"), "—", null, "Supplied by the tester", MessageCorrelationCertainty.TesterAsserted, "Not verified."),
            ],
            Assertions =
            [
                new("mm-delivered", "MM delivered the report to Skoletjenesten", "MM outcome Mottatt", "Mottatt", MessageVerificationVerdict.Passed, "MM outcome event"),
                new("skole-accepted", "Skoletjenesten accepted the report (Godkjent)", "Godkjent", "Stored only in its database.", MessageVerificationVerdict.NotEvaluated, "No read surface"),
            ],
            MissingEvidence = [new("Skoletjenesten internal outcome", "Stored only in its database.", "A read contract", "Skole team", "Agree a read surface.")],
        };

        public static MessageVerificationRecord Denied() => Delivered() with
        {
            Verdict = MessageVerificationVerdict.Blocked, VerdictReason = "Blocked: evidence access is missing.",
            Providers = [new("mm-telemetry", "MM outcome events", "MM", MessageEvidenceProviderState.AccessDenied, "The backend identity may not read this telemetry (HTTP 403).", "MM outcome event.", DateTimeOffset.UtcNow, "MM team")],
            Observations = [new("MM", MessageEvidenceStage.AccessDenied, "The backend identity may not read this telemetry (HTTP 403).", "mm-telemetry", null, "MuMeldingId", [], "")],
            Assertions = [new("mm-delivered", "MM delivered the report to Skoletjenesten", "MM outcome Mottatt", "No evidence (AccessDenied)", MessageVerificationVerdict.Blocked, "MM outcome event")],
            MissingEvidence = [new("MM outcome events", "HTTP 403.", "Read access", "MM team", "Grant BirkNext's backend identity read access to the telemetry source.")],
        };

        public static IntegrationJourneyRun Run(MessageVerificationRecord record, Guid? id = null) => new()
        {
            RunId = id ?? VerificationId, PackId = "skolenaervaer", JourneyId = "rapportmottak", ScenarioId = "message-verification", ScenarioName = "Message verification (read-only)",
            EnvironmentId = "dev-env", OverallState = JourneyRunState.Partial, StartedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow, MessageVerification = record,
        };
    }

    private sealed class FixedAccess(ExecutionAccessView view) : IExecutionAccessApiService
    {
        public Task<ExecutionAccessView> GetAsync(CancellationToken ct = default) => Task.FromResult(view);
    }

    private sealed class FakeApi : IIntegrationJourneysApiService
    {
        public static readonly Guid SourceRunId = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");
        public bool WithScenario { get; set; } = true;
        public List<IntegrationJourneyRunRequest> Started { get; } = [];
        public List<Guid> Opened { get; } = [];
        public IntegrationJourneyRun? Next { get; set; }
        public List<IntegrationJourneyRunSummary> History { get; set; } = [];

        private IntegrationJourneyPackView Pack()
        {
            JourneyScenarioDescriptor[] scenarios = WithScenario
                ?
                [
                    new("valid-report", "Valid report", "Submission.", JourneyScenarioSupport.Supported),
                    new("message-verification", "Message verification (read-only)", "Read-only.", JourneyScenarioSupport.Supported, StateChanging: false, Inputs:
                    [
                        new("expectedOutcome", "Expected outcome", JourneyScenarioInputKind.Choice, true, "", [new("valid-report", "Valid report"), new("person-not-found", "Unknown child"), new("invalid-xsd", "Invalid XML")]),
                        new("observationWindow", "Observation window", JourneyScenarioInputKind.Choice, true, "", [new("24", "Last 24 hours"), new("72", "Last 72 hours"), new("168", "Last 168 hours")]),
                    ]),
                ]
                : [new("valid-report", "Valid report", "Submission.", JourneyScenarioSupport.Supported)];
            return new IntegrationJourneyPackView
            {
                PackId = "skolenaervaer", DisplayName = "Skolenærvær Testing", EnvironmentId = "dev-env", EnvironmentTrusted = true,
                Journeys =
                [
                    new IntegrationJourneyView
                    {
                        Journey = new IntegrationJourneyDefinition { PackId = "skolenaervaer", JourneyId = "rapportmottak", DisplayName = "Rapportmottak", Scenarios = scenarios },
                        RunnableScenarios = WithScenario ? ["message-verification"] : [], ReadinessSummary = "Ready.",
                        Prerequisites =
                        [
                            new("evidence:mm-telemetry", "MM outcome events (telemetry)", JourneyPrerequisiteCategory.Observation, JourneyReadinessState.NotConfigured,
                                "No enabled Integration Quality Review integration names MM.", Mandatory: false, Scenarios: ["message-verification"]),
                        ],
                    },
                ],
            };
        }

        public Task<ActiveEventApiResult<IReadOnlyList<IntegrationJourneyPackView>>> PacksAsync(string environmentId, CancellationToken ct = default) =>
            Task.FromResult(new ActiveEventApiResult<IReadOnlyList<IntegrationJourneyPackView>>([Pack()], null, HttpStatusCode.OK));
        public Task<ActiveEventApiResult<ArchitectureRuleReport>> RulesAsync(string environmentId, string packId, CancellationToken ct = default) =>
            Task.FromResult(new ActiveEventApiResult<ArchitectureRuleReport>(null, "Not found.", HttpStatusCode.NotFound));
        public Task<ActiveEventApiResult<IntegrationJourneyRun>> StartAsync(IntegrationJourneyRunRequest request, CancellationToken ct = default)
        {
            Started.Add(request);
            var run = Next ?? Record.Run(Record.Delivered(), Guid.NewGuid());
            History.Add(new(run.RunId, "skolenaervaer", "rapportmottak", "message-verification", "dev-env", run.OverallState, run.StartedAt, run.CompletedAt, 3, 15, 0));
            return Task.FromResult(new ActiveEventApiResult<IntegrationJourneyRun>(run, null, HttpStatusCode.OK));
        }
        public Task<ActiveEventApiResult<IntegrationJourneyRun>> GetAsync(Guid runId, CancellationToken ct = default)
        {
            Opened.Add(runId);
            return Task.FromResult(new ActiveEventApiResult<IntegrationJourneyRun>(Record.Run(Record.Delivered(), runId), null, HttpStatusCode.OK));
        }
        public Task<ActiveEventApiResult<IReadOnlyList<IntegrationJourneyRunSummary>>> HistoryAsync(string environmentId, string packId, string? journeyId, JourneyRunState? state, CancellationToken ct = default) =>
            Task.FromResult(new ActiveEventApiResult<IReadOnlyList<IntegrationJourneyRunSummary>>(
                [.. History, new IntegrationJourneyRunSummary(SourceRunId, "skolenaervaer", "rapportmottak", "valid-report", "dev-env", JourneyRunState.Partial, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 6, 15, 1)],
                null, HttpStatusCode.OK));
    }
}
