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
/// Payload &amp; Mapping Flow UI: each stage shows its own evidence level and source in stage order; expected is never shown as observed, a
/// difference only when both sides exist, payloads only masked (technical payload collapsed, rendered as text), and mapping rows carry
/// their provenance. A recorded run shows only what it recorded.
/// </summary>
public sealed class PayloadFlowPanelTests : BunitContext
{
    private const string Mask = "•••••••••••";

    public PayloadFlowPanelTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private IRenderedComponent<PayloadFlowPanel> Render(PayloadFlowView flow) => Render<PayloadFlowPanel>(p => p.Add(c => c.Flow, flow));

    [Fact]
    public void ADryRun_ShowsAltinnGenerated_AndEveryDownstreamStageExpected_InStageOrder()
    {
        var cut = Render(Flow(PayloadFlowMode.DryRun, Altinn(FlowStageEvidenceLevel.Generated, "Generated · not submitted", "Generated payload (not submitted)"), Downstream(FlowStageEvidenceLevel.Expected)));

        cut.Find("[data-testid=pff]").GetAttribute("data-mode").Should().Be("DryRun");
        var pipeline = cut.FindAll("[data-testid=pff-pipeline-stage]");
        pipeline.Select(item => item.GetAttribute("data-stage")).Should().Equal("altinn", "mu", "mm", "report");
        pipeline.Select(item => item.GetAttribute("data-level")).Should().Equal("Generated", "Expected", "Expected", "Expected");
        pipeline[0].TextContent.Should().Contain("Generated").And.Contain("not submitted");
        pipeline[1].TextContent.Should().Contain("Expected").And.Contain("runtime not observed").And.NotContain("Observed in");
        cut.FindAll("[data-testid=pff-stage]").Select(item => item.GetAttribute("data-stage")).Should().Equal("altinn", "mu", "mm", "report");
    }

    [Fact]
    public void ALiveRunBeforeSubmission_IsNotEvaluated_AndShowsTheDiagnostic()
    {
        var altinn = Altinn(FlowStageEvidenceLevel.NotEvaluated, "Not generated in this run: it stopped before the form-data step", "No payload recorded for this run") with
        {
            Payload = [], TechnicalPayload = null, Mappings = [], Diagnostic = "Reporter authenticated: Not executed: the contract is incomplete.",
        };
        var cut = Render(Flow(PayloadFlowMode.Run, altinn, Downstream(FlowStageEvidenceLevel.NotObserved)));
        var stage = cut.FindAll("[data-testid=pff-stage]")[0];
        stage.GetAttribute("data-level").Should().Be("NotEvaluated");
        stage.QuerySelector("[data-testid=pff-diagnostic]")!.TextContent.Should().Contain("contract is incomplete");
        stage.QuerySelector("[data-testid=pff-technical]").Should().BeNull();
        stage.TextContent.Should().Contain("Not evaluated").And.Contain("this run did not get far enough to say");
    }

    [Fact]
    public void ASuccessfulSubmission_IsConfirmedInAltinn_AndDownstreamIsNotObserved_NotFailed()
    {
        var cut = Render(Flow(PayloadFlowMode.Run, Altinn(FlowStageEvidenceLevel.Confirmed, "Generated and submitted · Altinn confirmed the instance completed", "Generated and submitted (the same body)"),
            Downstream(FlowStageEvidenceLevel.NotObserved, "Current expected contract and configuration — not recorded during this run")));

        var levels = cut.FindAll("[data-testid=pff-pipeline-stage]").Select(item => item.GetAttribute("data-level")).ToList();
        levels.Should().Equal("Confirmed", "NotObserved", "NotObserved", "NotObserved");
        cut.FindAll("[data-testid=pff-pipeline-stage]").Skip(1).Should().OnlyContain(item => item.TextContent.Contains("Not observed") && !item.TextContent.Contains("Failed"));
        cut.FindAll("[data-testid=pff-stage]")[1].TextContent.Should().Contain("no runtime evidence for this stage; this is not a failure");
        cut.FindAll("[data-testid=pff-expectation-basis]").Should().HaveCount(3).And.OnlyContain(item => item.TextContent.StartsWith("Current expected contract"));
        cut.Find("[data-testid=pff-payload-state]").TextContent.Should().Be("Generated and submitted (the same body)");
    }

    [Fact]
    public void AFailedAltinnSubmission_IsFailedOnlyForAltinn()
    {
        var altinn = Altinn(FlowStageEvidenceLevel.Failed, "Generated and sent · Altinn did not store it", "Generated and submitted (the same body)") with { Diagnostic = "HTTP 400 Altinn refused storing the form data." };
        var cut = Render(Flow(PayloadFlowMode.Run, altinn, Downstream(FlowStageEvidenceLevel.NotObserved)));
        cut.FindAll("[data-testid=pff-pipeline-stage]").Select(item => item.GetAttribute("data-level")).Should().Equal("Failed", "NotObserved", "NotObserved", "NotObserved");
        cut.Find("[data-testid=pff-diagnostic]").TextContent.Should().Contain("HTTP 400");
    }

    [Fact]
    public void ThePayload_IsMasked_TheTechnicalPayloadIsCollapsedText_AndCopyCopiesOnlyTheMaskedText()
    {
        var cut = Render(Flow(PayloadFlowMode.DryRun, Altinn(FlowStageEvidenceLevel.Generated, "Generated · not submitted", "Generated payload (not submitted)"), Downstream(FlowStageEvidenceLevel.Expected)));
        var technical = cut.Find("[data-testid=pff-technical]");
        technical.HasAttribute("open").Should().BeFalse("the technical payload is collapsed by default");
        technical.QuerySelector("pre")!.TextContent.Should().Contain("<Fodselsnummer>" + Mask + "</Fodselsnummer>");
        technical.QuerySelector("pre")!.InnerHtml.Should().Contain("&lt;Fodselsnummer&gt;", "payload XML is rendered as text, never as markup");
        cut.Find("[data-testid=pff-field][data-path='Klient.Identifikator.Fodselsnummer']").TextContent.Should().Contain("synthetic-child-1 (masked)");
        cut.Markup.Should().NotMatchRegex(@"(?<!\d)\d{11}(?!\d)", "no identifier appears anywhere");

        cut.Find("[data-testid=pff-copy]").Click();
        JSInterop.VerifyInvoke("navigator.clipboard.writeText").Arguments.Single().Should().Be(XmlPayload);
    }

    [Fact]
    public void TheExpectedMapping_ShowsProvenanceAndTransformations_AndDownstreamSaysWhyNoMappingIsShown()
    {
        var cut = Render(Flow(PayloadFlowMode.DryRun, Altinn(FlowStageEvidenceLevel.Generated, "Generated · not submitted", "Generated payload (not submitted)"), Downstream(FlowStageEvidenceLevel.Expected)));
        var rows = cut.FindAll("[data-testid=pff-mapping-row]");
        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(row => row.GetAttribute("data-provenance") == "Configured" && row.TextContent.Contains("Configured"));
        rows[1].GetAttribute("data-status").Should().Be("Transformed");
        rows[1].TextContent.Should().Contain("Transformation: number → integer");
        cut.FindAll("[data-testid=pff-mapping-note]").Skip(1).Should().OnlyContain(note => note.TextContent.StartsWith("No field mapping is shown"));
    }

    [Fact]
    public void ContractInformation_ShowsEachFactWithItsSource()
    {
        var cut = Render(Flow(PayloadFlowMode.DryRun, Altinn(FlowStageEvidenceLevel.Generated, "Generated · not submitted", "Generated payload (not submitted)"), Downstream(FlowStageEvidenceLevel.Expected)));
        var contract = cut.FindAll("[data-testid=pff-contract]")[0];
        contract.TextContent.Should().Contain("Data type").And.Contain("form_v2").And.Contain("Live discovered (public TT02 metadata)");
        var mu = cut.FindAll("[data-testid=pff-contract]")[1];
        mu.TextContent.Should().Contain("Expected namespace").And.Contain("Not known");
        cut.FindAll("[data-testid=pff-validation]")[0].TextContent.Should().Contain("XML schema validation (XSD)").And.Contain("Not evaluated");
    }

    [Fact]
    public void ADifference_IsOfferedOnlyWhenBothSidesExist()
    {
        var cut = Render(Flow(PayloadFlowMode.DryRun, Altinn(FlowStageEvidenceLevel.Generated, "Generated · not submitted", "Generated payload (not submitted)"), Downstream(FlowStageEvidenceLevel.Expected)));
        cut.FindAll("[data-testid=pff-view]").Should().BeEmpty("nothing was observed, so there is nothing to compare");
        cut.Find("[data-testid=pff-no-observed]").TextContent.Should().Contain("no difference is shown");

        var observedMu = Downstream(FlowStageEvidenceLevel.Observed)[0] with
        {
            Payload = [new("periode.fra", "Periode fra", "2026-09-01", "Expected MU contract")],
            ObservedPayload = [new("periode.fra", "Periode fra", "2026-09-01", "Observed runtime payload"), new("barn.verdi", "Barn", "synthetic-child-1 (masked)", "Observed runtime payload")],
        };
        cut = Render(Flow(PayloadFlowMode.Run, Altinn(FlowStageEvidenceLevel.Confirmed, "Generated and submitted", "Generated and submitted (the same body)"), [observedMu, .. Downstream(FlowStageEvidenceLevel.NotObserved).Skip(1)]));
        var view = cut.Find("[data-testid=pff-view]");
        view.QuerySelectorAll("button").Select(button => button.TextContent).Should().Equal("Expected", "Observed", "Difference");
        view.QuerySelectorAll("button")[2].Click();
        var difference = cut.Find("[data-testid=pff-difference]").TextContent;
        difference.Should().Contain("Unchanged").And.Contain("Added");
    }

    [Fact]
    public void AnOldRecordedRun_ShowsOnlyWhatItRecorded()
    {
        var altinn = Altinn(FlowStageEvidenceLevel.Confirmed, "Generated and submitted · Altinn confirmed the instance completed", "No payload recorded for this run") with
        {
            Payload = [], TechnicalPayload = null, Mappings = [], MappingNote = "Not recorded for this run (runs recorded before payload capture existed, or the run stopped before the form-data step). Nothing is reconstructed.",
            Contract = [new("Contract used", "Not recorded for this run", "—")],
        };
        var cut = Render(Flow(PayloadFlowMode.Run, altinn, Downstream(FlowStageEvidenceLevel.NotObserved, "Current expected contract and configuration — not recorded during this run")));
        cut.Find("[data-testid=pff-payload-state]").TextContent.Should().Be("No payload recorded for this run");
        cut.FindAll("[data-testid=pff-technical]").Should().BeEmpty();
        cut.Find("[data-testid=pff-mapping-note]").TextContent.Should().Contain("Nothing is reconstructed");
        cut.Find("[data-testid=pff]").GetAttribute("data-mode").Should().Be("Run");
    }

    [Fact]
    public void APartialResult_ShowsTheObservedStage_AndKeepsTheOthersNotObserved_WithTheTimeline()
    {
        var mu = Downstream(FlowStageEvidenceLevel.Observed)[0] with { Summary = "Observed in this run", Runtime = "MU receives the message: Telemetry: stored" };
        var flow = Flow(PayloadFlowMode.Run, Altinn(FlowStageEvidenceLevel.Confirmed, "Generated and submitted", "Generated and submitted (the same body)"), [mu, .. Downstream(FlowStageEvidenceLevel.NotObserved).Skip(1)]) with
        {
            Timeline = [new("Form data stored in Altinn", DateTimeOffset.Parse("2026-10-13T10:00:00Z"), "Altinn response"), new("Meldingsutveksleren (MU) observed", DateTimeOffset.Parse("2026-10-13T10:01:00Z"), "Observed"),
                new("Meldingsutvekslermottaket (MM) observed", null, "Not observed")],
        };
        var cut = Render(flow);
        cut.FindAll("[data-testid=pff-pipeline-stage]").Select(item => item.GetAttribute("data-level")).Should().Equal("Confirmed", "Observed", "NotObserved", "NotObserved");
        var timeline = cut.FindAll("[data-testid=pff-timeline] li");
        timeline.Select(item => item.GetAttribute("data-observed")).Should().Equal("true", "true", "false");
        timeline[2].TextContent.Should().Contain("Not observed").And.NotContain("UTC");
    }

    [Fact]
    public void TheJourneyPanel_ShowsTheDryRunFlow_AndOpensARecordedRunsFlowFromHistory()
    {
        var api = new FlowApi();
        Services.AddSingleton<IIntegrationJourneysApiService>(api);
        var export = new Mock<IReportExportService>();
        Services.AddSingleton(export.Object);
        Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        Services.AddSingleton(new ActiveEventAuthenticationState(true, "Configured (test)."));
        var cut = Render<IntegrationJourneyPanel>(p => p.Add(c => c.Profile, new FrontendAnalysisProfile { Id = "dev-env", Name = "DEV", EnvironmentType = FrontendEnvironmentType.Development }));

        cut.Find("[data-testid=ijp-tab-history]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=ijp-history-flow]").Should().ContainSingle());
        cut.Find("[data-testid=ijp-history-flow]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=ijp-history-flow-view] [data-testid=pff]").GetAttribute("data-mode").Should().Be("Run"));
        api.FlowRequests.Should().ContainSingle();
        cut.Find("[data-testid=ijp-history-flow]").GetAttribute("aria-pressed").Should().Be("true");
    }

    // ── Fixtures (values masked as the backend sends them) ─────────────────────────────────────────────────────────────

    private const string XmlPayload = "<Skolenaervaer xmlns=\"urn:example\">\n  <Klient><Identifikator><Fodselsnummer>" + Mask + "</Fodselsnummer></Identifikator></Klient>\n  <AntallTimerEtterPlanen>100</AntallTimerEtterPlanen>\n</Skolenaervaer>";

    private static PayloadFlowView Flow(PayloadFlowMode mode, FlowStageEvidence altinn, IReadOnlyList<FlowStageEvidence> downstream) => new()
    {
        Mode = mode, RunId = mode == PayloadFlowMode.Run ? Guid.NewGuid() : null, Stages = [altinn, .. downstream],
        Timeline = [new("Generated", null, mode == PayloadFlowMode.DryRun ? "Generated in memory (dry run; not timestamped)" : "Not recorded"), new("MU observed", null, "Not observed")],
        Notes = ["Expected is not Observed."],
    };

    private static FlowStageEvidence Altinn(FlowStageEvidenceLevel level, string summary, string payloadState) => new()
    {
        StageId = "altinn", DisplayName = "Altinn", Sequence = 1, Level = level, Summary = summary, Runtime = "Nothing sent (dry run)", EvidenceSource = "Local generated payload + live Altinn app contract",
        PayloadState = payloadState,
        Payload = [new("Klient.Identifikator.Fodselsnummer", "Barnets fødselsnummer", "synthetic-child-1 (masked)", "Test input (synthetic, masked)"),
                   new("AntallTimerEtterPlanen", "Antall timer etter planen", "100", "Test input"),
                   new("Meldingshode.Id", "Meldingshode.Id", "Not supplied by BirkNext", "App's form schema (required)")],
        TechnicalPayload = new JourneyPayloadCapture { Format = "xml", ContentType = "application/xml", DataType = "form_v2", ByteCount = 312, MaskedContent = XmlPayload, Note = "Generated in memory for the dry run and not sent." },
        Contract = [new("Data type", "form_v2", "Live discovered (public TT02 metadata)"), new("Task", "Task_1 · Utfylling · data", "TT02 deployed OpenAPI contract")],
        Validation = [new("XML schema validation (XSD)", "Valid", "Not evaluated — BirkNext does not validate the XML document against an XSD", "—")],
        Mappings =
        [
            new("Test input", "childIdentifier", "synthetic-child-1 (masked)", "Altinn FormData", "Klient.Identifikator.Fodselsnummer", "synthetic-child-1 (masked)", PayloadMappingStatus.Matched, PayloadProvenance.Configured),
            new("Test input", "plannedHours", "100", "Altinn FormData", "AntallTimerEtterPlanen", "100", PayloadMappingStatus.Transformed, PayloadProvenance.Configured, "number → integer (the app's schema declares an integer)"),
        ],
        MappingNote = "Test input → Altinn FormData, from BirkNext's form binding (configured).",
        Correlation = [new("Reporter", "reporter-a (alias; identifier not shown)", "Run input"), new("Altinn instance", "Not created (dry run)", "—")],
    };

    private static IReadOnlyList<FlowStageEvidence> Downstream(FlowStageEvidenceLevel level, string? basis = null) =>
        new[] { ("mu", "Meldingsutveksleren (MU)", "Expected namespace"), ("mm", "Meldingsutvekslermottaket (MM)", "Input contract namespace"), ("report", "Skolenærværsrapport (Skoletjenesten)", "Operation") }
            .Select((stage, index) => new FlowStageEvidence
            {
                StageId = stage.Item1, DisplayName = stage.Item2, Sequence = index + 2, Level = level,
                Summary = level == FlowStageEvidenceLevel.Observed ? "Observed in this run" : "Expected · runtime not observed",
                Runtime = level == FlowStageEvidenceLevel.Expected ? "Not observed — a dry run sends nothing" : "Not observed — BirkNext has no runtime evidence source connected for this stage",
                EvidenceSource = "Configuration, source-derived contracts and the documented journey design (no runtime source)", ExpectationBasis = basis,
                PayloadState = "Expected representation only — no payload observed",
                Contract = [new(stage.Item3, "Not known — no XSD is bound to the message flow", "—")],
                MappingNote = "No field mapping is shown: BirkNext has no observed, configured, source-derived or documented field mapping for this hop.",
                Correlation = [new("Correlation", "Not observed", "—")],
            }).ToArray();

    private sealed class FlowApi : IIntegrationJourneysApiService
    {
        public List<Guid> FlowRequests { get; } = [];
        private readonly Guid _runId = Guid.NewGuid();

        public Task<ActiveEventApiResult<IReadOnlyList<IntegrationJourneyPackView>>> PacksAsync(string environmentId, CancellationToken ct = default) =>
            Task.FromResult(new ActiveEventApiResult<IReadOnlyList<IntegrationJourneyPackView>>([IntegrationJourneyPanelTests.SamplePack()], null, HttpStatusCode.OK));
        public Task<ActiveEventApiResult<ArchitectureRuleReport>> RulesAsync(string environmentId, string packId, CancellationToken ct = default) =>
            Task.FromResult(new ActiveEventApiResult<ArchitectureRuleReport>(null, "Not found.", HttpStatusCode.NotFound));
        public Task<ActiveEventApiResult<IntegrationJourneyRun>> StartAsync(IntegrationJourneyRunRequest request, CancellationToken ct = default) =>
            Task.FromResult(new ActiveEventApiResult<IntegrationJourneyRun>(null, "Not available.", HttpStatusCode.BadRequest));
        public Task<ActiveEventApiResult<IntegrationJourneyRun>> GetAsync(Guid runId, CancellationToken ct = default) =>
            Task.FromResult(new ActiveEventApiResult<IntegrationJourneyRun>(null, "Not found.", HttpStatusCode.NotFound));
        public Task<ActiveEventApiResult<IReadOnlyList<IntegrationJourneyRunSummary>>> HistoryAsync(string environmentId, string packId, string? journeyId, JourneyRunState? state, CancellationToken ct = default) =>
            Task.FromResult(new ActiveEventApiResult<IReadOnlyList<IntegrationJourneyRunSummary>>(
                [new IntegrationJourneyRunSummary(_runId, packId, "rapportmottak", "valid-report", environmentId, JourneyRunState.Partial, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 6, 15, 2)], null, HttpStatusCode.OK));
        public Task<ActiveEventApiResult<PayloadFlowView>> PayloadFlowAsync(Guid runId, CancellationToken ct = default)
        {
            FlowRequests.Add(runId);
            return Task.FromResult(new ActiveEventApiResult<PayloadFlowView>(Flow(PayloadFlowMode.Run, Altinn(FlowStageEvidenceLevel.Confirmed, "Generated and submitted", "Generated and submitted (the same body)"),
                Downstream(FlowStageEvidenceLevel.NotObserved)) with { RunId = runId }, null, HttpStatusCode.OK));
        }
    }
}
