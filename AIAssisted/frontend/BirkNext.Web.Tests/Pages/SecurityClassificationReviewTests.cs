using BirkNext.Integrations;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Security Classification page: levels from source, the pipeline with each stage's source and runtime state, the authorization matrix
/// with unauthorized and authorized controls apart (anti-disclosure shown as such), test types, counts as consistency only, the test context
/// (DEV/QA, synthetic ids, mutation disabled), tokens per run only, Production refused, export and history.
/// </summary>
public sealed class SecurityClassificationReviewTests : BunitContext
{
    private sealed class FakeApi : IClassificationReviewApiService
    {
        public ClassificationOverview Overview { get; set; } = new();
        public ClassificationReviewResult? Result { get; set; }
        public ClassificationRunRequest? LastRun { get; private set; }
        public ClassificationTestContext? Saved { get; private set; }
        public List<string> Analyzed { get; } = [];
        public Task<ClassificationOverview> OverviewAsync(string environmentId, CancellationToken ct = default) => Task.FromResult(Overview);
        public Task<(ClassificationSourceEvidence? Evidence, string? Error)> AnalyzeAsync(string environmentId, IReadOnlyList<(string FileName, Stream Content)> archives, CancellationToken ct = default)
        {
            Analyzed.AddRange(archives.Select(a => a.FileName));
            return Task.FromResult<(ClassificationSourceEvidence?, string?)>((Source(), null));
        }
        public Task<(ClassificationTestContext? Context, string? Error)> SaveContextAsync(string environmentId, ClassificationTestContext context, CancellationToken ct = default)
        {
            Saved = context;
            Overview = Overview with { Context = context };
            return Task.FromResult<(ClassificationTestContext?, string?)>((context, null));
        }
        public Task<ClassificationReviewResult> RunAsync(string environmentId, ClassificationRunRequest request, CancellationToken ct = default)
        {
            LastRun = request;
            Overview = Overview with { Latest = Result, History = [new(Result!.RunId, Result.CompletedAt, Result.Overall, Result.Findings.Count, Result.Live.Observations.Count)] };
            return Task.FromResult(Result!);
        }
        public Task<ClassificationReviewResult?> GetRunAsync(Guid runId, CancellationToken ct = default) => Task.FromResult(Result?.RunId == runId ? Result : null);
    }

    private static readonly List<ClassificationLevel> Levels =
    [
        new() { Nivaa = 0, Verdi = "Ingen" }, new() { Nivaa = 1, Verdi = "SkjultAdresse" },
        new() { Nivaa = 2, Verdi = "Kode7", BiRKKode = "Kode 7", ElementsKode = "K1", KreverGradertTilgang = true },
        new() { Nivaa = 3, Verdi = "Kode6", BiRKKode = "Kode 6", ElementsKode = "K2", KreverGradertTilgang = true },
    ];

    private static ClassificationSourceEvidence Source() => new()
    {
        EnvironmentId = "dev", AnalyzedAt = DateTimeOffset.UtcNow, Detected = true, Levels = Levels, Archives = [new SourceArchive("M2LB (1).zip", new string('a', 64), 300)],
        Facts = [new() { Id = "cdc-deserializer", Area = ClassificationArea.Pipeline, Title = "Production deserialization", State = ClassificationState.IssueDetected }],
    };

    private static ClassificationReviewResult Result() => new()
    {
        RunId = Guid.NewGuid(), EnvironmentId = "dev", CompletedAt = DateTimeOffset.UtcNow, Overall = ClassificationOverall.IssueDetected, Levels = Levels,
        Summary =
        [
            new(ClassificationArea.Model, "Classification model", ClassificationState.SourceVerified, "Source evidence only"),
            new(ClassificationArea.Pipeline, "CDC propagation", ClassificationState.IssueDetected, "Source evidence only"),
            new(ClassificationArea.GraphQL, "GraphQL protection", ClassificationState.NotTested, "No evidence"),
        ],
        Pipeline =
        [
            new() { Stage = ClassificationPipelineStage.Debezium, Title = "Debezium", Source = ClassificationState.SourceVerified, SourceDetail = "Sikkerhetsnivå observed in the payload." },
            new() { Stage = ClassificationPipelineStage.PersonAdapterDeserialization, Title = "Person Adapter deserialization", Source = ClassificationState.IssueDetected, SourceDetail = "sets the classification to the constant 0." },
            new() { Stage = ClassificationPipelineStage.GuardInput, Title = "CdcEvent.Sikkerhetsnivaa (guard input)", Source = ClassificationState.IssueDetected, SourceDetail = "Expected: the row's level. Source path: 0 for every event." },
            new() { Stage = ClassificationPipelineStage.Guard, Title = "SecurityClassificationGuard", Source = ClassificationState.SourceVerified, SourceDetail = "Implemented." },
        ],
        Live = new ClassificationLiveEvidence
        {
            State = IntegrationEvidenceState.Available, Reason = "5 safe GraphQL query observation(s).",
            Observations =
            [
                new() { Nivaa = 2, Identity = ClassificationIdentity.Unauthorized, Surface = ClassificationSurface.DirectProfile, TestType = ClassificationTestType.Negative, State = ClassificationState.Pass, Expected = "no data", Observed = "null" },
                new() { Nivaa = 2, Identity = ClassificationIdentity.Unauthorized, Surface = ClassificationSurface.Search, TestType = ClassificationTestType.Negative, State = ClassificationState.Pass },
                new() { Nivaa = 2, Identity = ClassificationIdentity.Authorized, Surface = ClassificationSurface.DirectProfile, TestType = ClassificationTestType.Functional, State = ClassificationState.Fail, Detail = "Positive control failed" },
                new() { Nivaa = 2, Identity = ClassificationIdentity.Authorized, Surface = ClassificationSurface.Search, TestType = ClassificationTestType.Functional, State = ClassificationState.Pass },
            ],
        },
        Checks =
        [
            new() { CheckId = "cdc-deserializer", Area = ClassificationArea.Pipeline, TestType = ClassificationTestType.Static, Title = "Production deserialization", State = ClassificationState.IssueDetected, Provenance = IntegrationEvidenceSource.SourceCode, Locations = [new("PersonAdapter/src/Worker/Workers/CdcProcessorWorker.cs", 301)] },
            new() { CheckId = "metric-runtime", Area = ClassificationArea.Observability, TestType = ClassificationTestType.NonFunctional, Title = "Rejection metric at runtime", State = ClassificationState.NotAvailable, Detail = "not 0 rejections", Provenance = IntegrationEvidenceSource.ApplicationInsights },
            new() { CheckId = "count-2", Area = ClassificationArea.Consistency, TestType = ClassificationTestType.DataConsistency, Title = "Level 2 count", State = ClassificationState.Observed, Provenance = IntegrationEvidenceSource.Configuration },
        ],
        Findings = [new() { RuleId = "cdc-classification-constant", Severity = ClassificationSeverity.High, Area = ClassificationArea.Pipeline, Title = "Security classification is reset to a constant in the production CDC path", Detail = "Sikkerhetsnivaa: 0", Recommendation = "Read it from the envelope." }],
        SourceCounts = new() { System = "BiRK", CapturedAt = DateTimeOffset.UtcNow, Provenance = "approved query", Counts = new() { [2] = 11, [3] = 57 } },
        TargetCounts = new() { System = "M2LB", CapturedAt = DateTimeOffset.UtcNow, Provenance = "approved query", Counts = new() { [2] = 11, [3] = 57 } },
        CountComparisons = [new() { Nivaa = 2, Source = 11, Target = 11, State = CountComparisonState.Match, Detail = "Data consistency only" }, new() { Nivaa = 3, Source = 57, Target = 57, State = CountComparisonState.Match }],
        TestCoverage =
        [
            new() { Scenario = "SecurityClassificationGuard rejects level 2/3", State = RepositoryTestCoverageState.UnitOnly, Tests = ["SecurityClassificationGuardTests.Evaluate_LevelTwoOrThree_ReturnsRejected"], Note = "Guard unit coverage" },
            new() { Scenario = "Raw Debezium payload → production deserializer → guard", State = RepositoryTestCoverageState.Missing },
        ],
        ProposedTests = [new() { Name = "RawDebeziumLevel2_ReachesSecurityClassificationGuard", Purpose = "Level 2 reaches the guard.", Code = "[Fact] public void RawDebeziumLevel2_ReachesSecurityClassificationGuard() { }" }],
        Context = new() { Environment = "DEV" },
        Missing = ["A synthetic level 3 (Kode6) test child in the approved context."],
    };

    private readonly FakeApi _api = new();
    private FrontendEnvironmentType _environment = FrontendEnvironmentType.Development;

    public SecurityClassificationReviewTests()
    {
        Services.AddSingleton<IClassificationReviewApiService>(_api);
        Services.AddSingleton<IReportExportService, ReportExportService>();
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(() => new FrontendAnalysisContext { ActiveProfile = new FrontendAnalysisProfile { Id = "dev", Name = "Dev", EnvironmentType = _environment } });
        Services.AddSingleton(context.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<SecurityClassificationReview> Page() => Render<SecurityClassificationReview>();

    [Fact]
    public void PreRunShowsContextMissingAndMutationDisabled()
    {
        _api.Overview = new ClassificationOverview { Source = Source() };

        var cut = Page();

        cut.Find("[data-testid='sc-source']").TextContent.Should().Contain("M2LB (1).zip").And.Contain("4 classification levels");
        cut.FindAll("[data-testid='sc-context-level']").Should().HaveCount(4).And.OnlyContain(l => l.TextContent.Contains("Missing"));
        cut.Find("[data-testid='sc-mutation']").TextContent.Should().Be("Disabled");
        cut.Find("[data-testid='sc-live-readiness']").TextContent.Should().Contain("Not run").And.Contain("source and configuration checks only");
    }

    [Fact]
    public void ProductionIsRefused()
    {
        _environment = FrontendEnvironmentType.Production;
        _api.Overview = new ClassificationOverview { Source = Source(), Context = new ClassificationTestContext { Environment = "DEV", ApprovedByTestLead = true, GraphQlEndpoint = "https://x.test/graphql", TestChildren = [new() { Nivaa = 2, BarnRegistreringId = Guid.NewGuid() }] } };

        var cut = Page();

        cut.Find("[data-testid='sc-setup']").TextContent.Should().Contain("Production is never tested");
        cut.Find("[data-testid='sc-live-readiness']").TextContent.Should().Contain("Not run").And.Contain("Production is never tested");
    }

    [Fact]
    public void RunSendsTokensOnceAndClearsThem()
    {
        _api.Overview = new ClassificationOverview { Source = Source() };
        _api.Result = Result();
        var cut = Page();
        cut.Find("[data-testid='sc-run-options'] button").Click();
        cut.Find("[data-testid='sc-token-unauthorized']").Change("tok-unauth");
        cut.Find("[data-testid='sc-token-authorized']").Change("tok-auth");

        cut.Find("[data-testid='sc-run']").Click();

        _api.LastRun!.UnauthorizedToken.Should().Be("tok-unauth");
        _api.LastRun.EnvironmentType.Should().Be("Development");
        cut.Find("[data-testid='sc-token-unauthorized']").GetAttribute("value").Should().BeNullOrEmpty();
        cut.Find("[data-testid='sc-token-unauthorized']").GetAttribute("type").Should().Be("password");
        cut.Find("[data-testid='sc-overall']").TextContent.Should().Be("Issue detected");
    }

    [Fact]
    public void ResultShowsModelPipelineStagesSeparatelyAndFindings()
    {
        _api.Overview = new ClassificationOverview { Source = Source(), Latest = Result() };

        var cut = Page();

        cut.FindAll("[data-testid='sc-level']").Select(l => l.TextContent).Should().Contain(t => t.Contains("Level 2 — Kode7 / Kode 7 / K1") && t.Contains("requires graded access"));
        cut.FindAll("[data-testid='sc-stage']").Should().HaveCount(4);
        cut.Find("[data-testid='sc-stage'][data-stage='GuardInput']").TextContent.Should().Contain("Issue detected").And.Contain("Runtime: Not tested").And.Contain("Source path: 0");
        cut.Find("[data-testid='sc-stage'][data-stage='Guard']").TextContent.Should().Contain("Source verified");
        cut.Find("[data-testid='sc-finding']").TextContent.Should().Contain("High").And.Contain("reset to a constant");
        cut.Find("[data-testid='sc-summary']").TextContent.Should().NotContain("Pass");
    }

    [Fact]
    public void MatrixSeparatesIdentitiesAndLabelsAntiDisclosure()
    {
        _api.Overview = new ClassificationOverview { Source = Source(), Latest = Result() };

        var cut = Page();

        var row = cut.Find("[data-testid='sc-matrix-row'][data-level='2']");
        var cells = row.QuerySelectorAll("td").Select(c => c.TextContent.Trim()).ToList();
        cells[0].Should().Be("Pass — anti-disclosure");
        cells[1].Should().Be("Not tested");
        cells[2].Should().Be("Pass");
        cells[5].Should().Be("Fail");
        cut.Find("[data-testid='sc-matrix-row'][data-level='3']").TextContent.Should().Contain("Not tested");
        cut.Find("[data-testid='sc-browser']").TextContent.Should().Contain("Not tested").And.Contain("would not prove server authorization");
        cut.FindAll("[data-testid='sc-matrix-table'] thead th").Select(h => h.TextContent).Should().Contain(h => h.StartsWith("Unauthorized")).And.Contain(h => h.StartsWith("Authorized"));
    }

    [Fact]
    public void CountsAreConsistencyAndAuthorizationIsNotInferred()
    {
        _api.Overview = new ClassificationOverview { Source = Source(), Latest = Result() };

        var cut = Page();

        cut.FindAll("[data-testid='sc-count-row']").Should().HaveCount(2).And.OnlyContain(r => r.TextContent.Contains("Match"));
        cut.Find("[data-testid='sc-count-note']").TextContent.Should().Be("Authorization result: not inferred from counts.");
        cut.Find("[data-testid='sc-consistency']").TextContent.Should().Contain("BiRK").And.Contain("M2LB");
    }

    [Fact]
    public void ChecksAreGroupedByTestTypeAndRepositoryTestsStandApart()
    {
        _api.Overview = new ClassificationOverview { Source = Source(), Latest = Result() };

        var cut = Page();

        cut.FindAll("[data-testid='sc-check-group']").Select(g => g.GetAttribute("data-type")).Should().Equal("Static", "NonFunctional", "DataConsistency");
        cut.Find("[data-testid='sc-checks']").TextContent.Should().Contain("Static security").And.Contain("Non-functional security").And.Contain("Data consistency").And.Contain("not 0 rejections");
        cut.FindAll("[data-testid='sc-coverage-item']").Select(i => i.TextContent).Should().Contain(t => t.Contains("Unit only")).And.Contain(t => t.Contains("Missing") && t.Contains("Raw Debezium"));
        cut.Find("[data-testid='sc-proposed']").TextContent.Should().Contain("RawDebeziumLevel2_ReachesSecurityClassificationGuard");
    }

    [Fact]
    public void ContextFormSavesSyntheticIdsAndRejectsInvalidValues()
    {
        _api.Overview = new ClassificationOverview { Source = Source() };
        var cut = Page();
        cut.Find("[data-testid='sc-configure']").Click();

        cut.FindAll("[data-testid='sc-context-environment'] option").Select(o => o.GetAttribute("value")).Should().Equal("", "DEV", "QA");
        cut.Find("[data-testid='sc-context-child-id-2']").Change("not-a-guid");
        cut.Find("[data-testid='sc-context-save']").Click();
        cut.Find("[data-testid='sc-context-error']").TextContent.Should().Contain("not a GUID");

        var id = Guid.NewGuid();
        cut.Find("[data-testid='sc-context-child-id-2']").Change(id.ToString());
        cut.Find("[data-testid='sc-context-unauthorized']").Change("ola@bufdir.no");
        cut.Find("[data-testid='sc-context-save']").Click();
        cut.Find("[data-testid='sc-context-error']").TextContent.Should().Contain("no e-mail");

        cut.Find("[data-testid='sc-context-unauthorized']").Change("Saksbehandler uten gradert tilgang");
        cut.Find("[data-testid='sc-context-environment']").Change("DEV");
        cut.Find("[data-testid='sc-context-save']").Click();
        _api.Saved!.TestChildren.Should().ContainSingle().Which.BarnRegistreringId.Should().Be(id);
        _api.Saved.Environment.Should().Be("DEV");
        cut.Find("[data-testid='sc-context-level'][data-level='2']").TextContent.Should().Contain("Configured");
    }

    [Fact]
    public void UploadAnalyzesArchives()
    {
        var cut = Page();

        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary([1, 2], "M2LB (1).zip"));

        _api.Analyzed.Should().Equal("M2LB (1).zip");
        cut.Find("[data-testid='sc-status']").TextContent.Should().Contain("4 classification levels").And.Contain("1 source issue");
    }

    [Fact]
    public void ExportContainsTheSectionsAndNoTokens()
    {
        _api.Overview = new ClassificationOverview { Source = Source(), Latest = Result() };
        var cut = Page();

        cut.Find("[data-testid='sc-export']").Click();

        JSInterop.Invocations.Should().Contain(i => i.Identifier == "downloadHtmlFile");
        var html = new ReportExportService().ExportClassificationReview(Result());
        html.Should().Contain("Classification model").And.Contain("CDC pipeline").And.Contain("Authorization").And.Contain("Classification distribution").And.Contain("Missing evidence").And.Contain("Pass — anti-disclosure");
        html.Should().NotContainAny("tok-unauth", "Bearer ");
    }

    [Fact]
    public void PresentationRules()
    {
        ClassificationPresentation.Cell(null).Should().Be("Not tested");
        ClassificationPresentation.Tone(ClassificationState.SourceVerified).Should().NotBe(ClassificationPresentation.Tone(ClassificationState.Pass));
        ClassificationPresentation.Tone(ClassificationState.NoIndicatorsObserved).Should().Be("muted");
        ClassificationPresentation.LiveReadiness(new ClassificationTestContext { Environment = "QA", ApprovedByTestLead = true, GraphQlEndpoint = "https://x", TestChildren = [new() { Nivaa = 2 }] }, "Development", true).CanRun.Should().BeFalse();
        ClassificationPresentation.LiveReadiness(new ClassificationTestContext { Environment = "DEV", ApprovedByTestLead = true, GraphQlEndpoint = "https://x", TestChildren = [new() { Nivaa = 2 }] }, "Development", true).CanRun.Should().BeTrue();
    }
}
