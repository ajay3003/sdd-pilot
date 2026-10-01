using BirkNext.Dependencies;
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
/// (DEV/QA, synthetic ids, mutation disabled), tokens per run only, Production refused, export and history. Source evidence comes from Source
/// Analysis snapshots chosen on the page (no upload here), bound to each run by exact snapshot id.
/// </summary>
public sealed class SecurityClassificationReviewTests : BunitContext
{
    private sealed class FakeApi : IClassificationReviewApiService
    {
        public ClassificationOverview Overview { get; set; } = new();
        public ClassificationReviewResult? Result { get; set; }
        public ClassificationRunRequest? LastRun { get; private set; }
        public ClassificationTestContext? Saved { get; private set; }
        public Task<ClassificationOverview> OverviewAsync(string environmentId, CancellationToken ct = default) => Task.FromResult(Overview);
        public List<ClassificationSnapshotOption> Snapshots { get; set; } = [AppOld, App, Shared];
        public List<RelatedSourceCandidate> Candidates { get; set; } = [];
        public List<ClassificationSnapshotOption> Newer { get; set; } = [];
        public string? ScopeError { get; set; }
        public List<ClassificationSourceScopeRequest?> ScopeRequests { get; } = [];
        /// <summary>Mirrors the backend: read-only, the exact snapshots asked for, related sources only when asked for.</summary>
        public Task<ClassificationScopeOptions> SourceScopeAsync(string environmentId, ClassificationSourceScopeRequest? scope, CancellationToken ct = default)
        {
            ScopeRequests.Add(scope);
            var options = new ClassificationScopeOptions { Snapshots = Snapshots, Coverage = ClassificationSourceCoverage.Rows(null) };
            if (scope is null) return Task.FromResult(options);
            if (ScopeError is not null) return Task.FromResult(options with { Candidates = Candidates, Error = ScopeError });
            var selected = new[] { scope.PrimarySnapshotId }.Concat(scope.RelatedSnapshotIds).Select(id => Snapshots.First(s => s.SnapshotId == id)).ToList();
            var notIncluded = Candidates.Where(c => selected.Skip(1).All(s => s.RepositoryKey != c.RepositoryKey)).ToList();
            var sourceScope = new ClassificationSourceScope
            {
                Primary = Entry(selected[0]), Related = selected.Skip(1).Select(Entry).ToList(), ExcludedSuggestions = scope.ExcludedSuggestions,
                Limitations = notIncluded.Select(c => $"Related source detected but not included in this review scope: {c.Repository} (snapshot available).").ToList(),
            };
            var evidence = Source() with { Scope = sourceScope };
            return Task.FromResult(options with { Candidates = Candidates, Scope = sourceScope, Evidence = evidence, Coverage = ClassificationSourceCoverage.Rows(evidence), Newer = Newer });
        }
        public Task<(ClassificationTestContext? Context, string? Error)> SaveContextAsync(string environmentId, ClassificationTestContext context, CancellationToken ct = default)
        {
            Saved = context;
            Overview = Overview with { Context = context };
            return Task.FromResult<(ClassificationTestContext?, string?)>((context, null));
        }
        public int Cleared { get; private set; }
        public bool LoseContextOnRun { get; set; }
        public Task ClearContextAsync(string environmentId, CancellationToken ct = default)
        {
            Cleared++;
            Overview = Overview with { Context = new() };
            return Task.CompletedTask;
        }
        public Task<ClassificationReviewResult> RunAsync(string environmentId, ClassificationRunRequest request, CancellationToken ct = default)
        {
            LastRun = request;
            if (RejectRun is { } why) throw new InvalidOperationException(why);
            if (LoseContextOnRun) Overview = Overview with { Context = new() }; // e.g. the backend restarted: memory is gone
            Overview = Overview with { Latest = Result, History = [new(Result!.RunId, Result.CompletedAt, Result.Overall, Result.Findings.Count, Result.Live.Observations.Count)] };
            return Task.FromResult(Result!);
        }
        public string? RejectRun { get; set; }
        public Task<ClassificationReviewResult?> GetRunAsync(Guid runId, CancellationToken ct = default) => Task.FromResult(Result?.RunId == runId ? Result : null);
    }

    // ── Source Analysis snapshots (generic identities; the M2LB names are pilot data only) ─────────────────────────

    private static ClassificationSnapshotOption Snapshot(string id, string repository, string archive, string fingerprint, string at, string status, bool latest, bool evidence = true) => new()
    {
        SnapshotId = Guid.Parse(id), RepositoryKey = repository.ToLowerInvariant(), Repository = repository, IdentityBasis = "Archive file name", ArchiveName = archive,
        Fingerprint = fingerprint + new string('0', 64 - fingerprint.Length), AnalyzedAt = DateTimeOffset.Parse(at), SourceStatus = status, Latest = latest,
        HasClassificationEvidence = evidence, EvidenceNote = evidence ? null : "Analyzed before security classification evidence was captured. Analyze the archive again in Source Analysis to create a new snapshot.",
    };
    private static readonly ClassificationSnapshotOption AppOld = Snapshot("bbbbbbbb-0000-0000-0000-000000000001", "M2LB", "M2LB (1).zip", "0ld0ld00", "2026-09-30T07:21:00Z", "Partial", latest: false);
    private static readonly ClassificationSnapshotOption App = Snapshot("bbbbbbbb-0000-0000-0000-000000000002", "M2LB", "M2LB _2_.zip", "c850a1b2", "2026-10-01T12:30:00Z", "Partial", latest: true);
    private static readonly ClassificationSnapshotOption Shared = Snapshot("bbbbbbbb-0000-0000-0000-000000000003", "Shared.Security", "Shared.Security.zip", "a91c0000", "2026-10-01T09:00:00Z", "Ready", latest: true);

    private static SourceScopeEntry Entry(ClassificationSnapshotOption s) => new()
    {
        SnapshotId = s.SnapshotId, RepositoryKey = s.RepositoryKey, Repository = s.Repository, ArchiveName = s.ArchiveName, Fingerprint = s.Fingerprint, AnalyzedAt = s.AnalyzedAt, SourceStatus = s.SourceStatus,
    };

    private static readonly RelatedSourceCandidate SharedCandidate = new()
    {
        RepositoryKey = "shared.security", Repository = "Shared.Security", State = RelatedSourceState.SnapshotAvailable, Reason = "Declares a classification type the primary source uses.",
        Evidence = [new("Type reference", "Declares CdcEvent, which the primary source's classification path constructs but does not declare (exact type name).", 0, [])],
        MatchingSnapshotIds = [Shared.SnapshotId],
    };

    private static ClassificationSourceRef Ref(ClassificationSnapshotOption s, string file, int line) => new() { SnapshotId = s.SnapshotId, Repository = s.Repository, Fingerprint = s.Fingerprint, Locations = [new(file, line)] };

    private static readonly List<ClassificationLevel> Levels =
    [
        new() { Nivaa = 0, Verdi = "Ingen" }, new() { Nivaa = 1, Verdi = "SkjultAdresse" },
        new() { Nivaa = 2, Verdi = "Kode7", BiRKKode = "Kode 7", ElementsKode = "K1", KreverGradertTilgang = true },
        new() { Nivaa = 3, Verdi = "Kode6", BiRKKode = "Kode 6", ElementsKode = "K2", KreverGradertTilgang = true },
    ];

    private static ClassificationSourceEvidence Source() => new()
    {
        EnvironmentId = "dev", AnalyzedAt = DateTimeOffset.UtcNow, Detected = true, Levels = Levels,
        Facts =
        [
            new() { Id = "model-reference", Area = ClassificationArea.Model, Title = "Classification reference data", State = ClassificationState.SourceVerified, Sources = [Ref(App, "Person/src/SikkerhetsnivaaType.cs", 4)] },
            new() { Id = "cdc-deserializer", Area = ClassificationArea.Pipeline, Title = "Production deserialization", State = ClassificationState.IssueDetected, Locations = [new("PersonAdapter/src/CdcProcessorWorker.cs", 301)], Sources = [Ref(App, "PersonAdapter/src/CdcProcessorWorker.cs", 301)] },
            new() { Id = "cdc-field-name", Area = ClassificationArea.Pipeline, Title = "Payload field \"Sikkerhetsnivå\"", State = ClassificationState.SourceVerified, Sources = [Ref(App, "PersonAdapter/src/ChildRegistrationMapper.cs", 88)] },
            new() { Id = "guard-logic", Area = ClassificationArea.Guard, Title = "SecurityClassificationGuard", State = ClassificationState.SourceVerified, Sources = [Ref(App, "PersonAdapter/src/SecurityClassificationGuard.cs", 12)] },
            new() { Id = "access-profile-antidisclosure", Area = ClassificationArea.DirectAccess, Title = "Unauthorized classified child is indistinguishable from nonexistent", State = ClassificationState.SourceVerified, Sources = [Ref(App, "Person/src/BarnProfileService.cs", 40)] },
            new() { Id = "graphql-hentBarn", Area = ClassificationArea.GraphQL, Title = "GraphQL hentBarn authorization", State = ClassificationState.SourceVerified, Sources = [Ref(App, "Person/src/Resolvers.cs", 9)] },
        ],
        TestCoverage = [new() { Scenario = "SecurityClassificationGuard rejects level 2/3", State = RepositoryTestCoverageState.UnitOnly, Repositories = ["M2LB"] }],
        Limitations = ["Syntax-only analysis: behaviour of referenced libraries whose source was not uploaded is not assessed."],
    };

    private static ClassificationReviewResult Result() => new()
    {
        RunId = Guid.NewGuid(), EnvironmentId = "dev", CompletedAt = DateTimeOffset.UtcNow, Overall = ClassificationOverall.IssueDetected, Levels = Levels,
        SourceScope = new ClassificationSourceScope { Primary = Entry(App) },
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
        Findings = [new() { RuleId = "cdc-classification-constant", Severity = ClassificationSeverity.High, Area = ClassificationArea.Pipeline, Title = "Security classification is reset to a constant in the production CDC path", Detail = "Sikkerhetsnivaa: 0", Recommendation = "Read it from the envelope.", Sources = [Ref(App, "PersonAdapter/src/CdcProcessorWorker.cs", 301)] }],
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
    private readonly FeatureVisibilityService _flags = new();

    public SecurityClassificationReviewTests()
    {
        Services.AddSingleton<IClassificationReviewApiService>(_api);
        Services.AddSingleton(_flags);
        Services.AddSingleton<IReportExportService, ReportExportService>();
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(() => new FrontendAnalysisContext { ActiveProfile = new FrontendAnalysisProfile { Id = "dev", Name = "Dev", EnvironmentType = _environment } });
        Services.AddSingleton(context.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<SecurityClassificationReview> Page() => Render<SecurityClassificationReview>();

    /// <summary>The page with the current Source Analysis snapshot chosen as the primary source (what a test lead does first).</summary>
    private IRenderedComponent<SecurityClassificationReview> PageWithSource()
    {
        var cut = Page();
        cut.Find("[data-testid='sc-primary']").Change(App.SnapshotId.ToString());
        return cut;
    }

    [Fact]
    public void PreRunShowsContextMissingAndMutationDisabled()
    {
        _api.Overview = new ClassificationOverview();

        var cut = PageWithSource();

        cut.Find("[data-testid='sc-primary-summary']").TextContent.Should().Contain("M2LB _2_.zip").And.Contain("c850a1b2…").And.Contain("Partial");
        cut.FindAll("[data-testid='sc-context-level']").Should().HaveCount(4).And.OnlyContain(l => l.TextContent.Contains("Missing"));
        cut.Find("[data-testid='sc-mutation']").TextContent.Should().Be("Disabled");
        cut.Find("[data-testid='sc-live-readiness']").TextContent.Should().Contain("Not run").And.Contain("source and configuration checks only");
    }

    [Fact]
    public void ProductionIsRefused()
    {
        _environment = FrontendEnvironmentType.Production;
        _api.Overview = new ClassificationOverview { Context = new ClassificationTestContext { Environment = "DEV", ApprovedByTestLead = true, GraphQlEndpoint = "https://x.test/graphql", TestChildren = [new() { Nivaa = 2, BarnRegistreringId = Guid.NewGuid() }] } };

        var cut = Page();

        cut.Find("[data-testid='sc-setup']").TextContent.Should().Contain("Production is never tested");
        cut.Find("[data-testid='sc-live-readiness']").TextContent.Should().Contain("Not run").And.Contain("Production is never tested");
    }

    [Fact]
    public void RunSendsTokensOnceAndClearsThem()
    {
        _api.Overview = new ClassificationOverview();
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
        _api.Overview = new ClassificationOverview { Latest = Result() };

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
        _api.Overview = new ClassificationOverview { Latest = Result() };

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
        _api.Overview = new ClassificationOverview { Latest = Result() };

        var cut = Page();

        cut.FindAll("[data-testid='sc-count-row']").Should().HaveCount(2).And.OnlyContain(r => r.TextContent.Contains("Match"));
        cut.Find("[data-testid='sc-count-note']").TextContent.Should().Be("Authorization result: not inferred from counts.");
        cut.Find("[data-testid='sc-consistency']").TextContent.Should().Contain("BiRK").And.Contain("M2LB");
    }

    [Fact]
    public void ChecksAreGroupedByTestTypeAndRepositoryTestsStandApart()
    {
        _api.Overview = new ClassificationOverview { Latest = Result() };

        var cut = Page();

        cut.FindAll("[data-testid='sc-check-group']").Select(g => g.GetAttribute("data-type")).Should().Equal("Static", "NonFunctional", "DataConsistency");
        cut.Find("[data-testid='sc-checks']").TextContent.Should().Contain("Static security").And.Contain("Non-functional security").And.Contain("Data consistency").And.Contain("not 0 rejections");
        cut.FindAll("[data-testid='sc-coverage-item']").Select(i => i.TextContent).Should().Contain(t => t.Contains("Unit only")).And.Contain(t => t.Contains("Missing") && t.Contains("Raw Debezium"));
        cut.Find("[data-testid='sc-proposed']").TextContent.Should().Contain("RawDebeziumLevel2_ReachesSecurityClassificationGuard");
    }

    [Fact]
    public void ContextFormSavesSyntheticIdsAndRejectsInvalidValues()
    {
        _api.Overview = new ClassificationOverview();
        var cut = PageWithSource();
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
    public void NewReviewSetupHasNoSourceUploadControl()
    {
        var cut = PageWithSource();

        cut.FindComponents<InputFile>().Should().BeEmpty("Source Analysis owns source upload");
        cut.FindAll("input[type='file']").Should().BeEmpty();
        cut.Markup.Should().NotContainAny("Analyze M2LB source", "M2LB source archive", "Upload repository archive", "Choose source archive", "Re-analyze");
        cut.FindAll("[data-testid='sc-needed-item']").Select(i => i.TextContent).Should().NotContain(t => t.Contains("archive", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExportContainsTheSectionsAndNoTokens()
    {
        _api.Overview = new ClassificationOverview { Latest = Result() };
        var cut = Page();

        cut.Find("[data-testid='sc-export']").Click();

        JSInterop.Invocations.Should().Contain(i => i.Identifier == "downloadHtmlFile");
        var html = new ReportExportService().ExportClassificationReview(Result());
        html.Should().Contain("Classification model").And.Contain("change-capture pipeline").And.Contain("Authorization").And.Contain("Classification distribution").And.Contain("Missing evidence").And.Contain("Pass — anti-disclosure");
        html.Should().Contain("Source scope").And.Contain(App.Fingerprint).And.Contain("M2LB _2_.zip").And.Contain("No related source included").And.Contain("does not prove runtime enforcement");
        html.Should().Contain("M2LB c850a1b2…", "a source finding names its snapshot");
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

    // ── Status semantics and "What's needed to complete this review" ────────────────────────────────────────────────

    private static ClassificationReviewResult WithScopeStages(ClassificationReviewResult r) => r with
    {
        Pipeline =
        [
            new() { Stage = ClassificationPipelineStage.BiRK, Title = "BiRK", Source = ClassificationState.NotAssessedHere, SourceDetail = "BiRK source filtering of Kode 6/7 is documented as the primary protection layer, but that implementation is outside the analyzed source." },
            new() { Stage = ClassificationPipelineStage.EventHub, Title = "Event Hub", Source = ClassificationState.NotAssessedHere, SourceDetail = "Event Hub is part of the classification pipeline. Transport/runtime evidence is assessed in Integration Quality Review." },
            .. r.Pipeline,
        ],
        Summary =
        [
            .. r.Summary,
            new(ClassificationArea.Observability, "Observability", ClassificationState.Partial, "birk.kode67.rejections is defined in source; runtime telemetry evidence is unavailable.")
            {
                Parts = [new("Metric definition", ClassificationState.SourceVerified, "birk.kode67.rejections is defined in source."), new("Runtime telemetry", ClassificationState.NotAvailable, "Runtime telemetry evidence is unavailable (not 0).")],
            },
        ],
    };

    [Fact]
    public void NeededCardIsDirectlyBelowTheSummaryAndListsCurrentPrerequisites()
    {
        _api.Overview = new ClassificationOverview { Latest = Result() };

        var cut = Page();

        var order = cut.FindAll("section[data-testid]").Select(e => e.GetAttribute("data-testid")).ToList();
        order.IndexOf("sc-needed").Should().Be(order.IndexOf("sc-summary") + 1);
        order.IndexOf("sc-needed").Should().BeLessThan(order.IndexOf("sc-model")).And.BeLessThan(order.IndexOf("sc-pipeline"));
        var card = cut.Find("[data-testid='sc-needed']");
        card.GetAttribute("data-count").Should().Be("10");
        cut.Find("[data-testid='sc-needed-count']").TextContent.Should().Be("10 items");
        cut.Find("[data-testid='sc-needed-lead']").TextContent.Should().Contain("cannot run yet").And.Contain("not security findings");
        var missing = cut.FindAll("[data-testid='sc-needed-item'][data-status='Missing'], [data-testid='sc-needed-item'][data-status='NotAvailable']").Select(i => i.GetAttribute("data-item")).ToList();
        missing.Should().Equal("context-environment", "context-endpoint", "context-approval", "child-0", "child-1", "child-2", "child-3", "identity-unauthorized", "identity-authorized", "telemetry");
        card.TextContent.Should().Contain("Approved DEV/QA environment").And.Contain("Kode6 / Kode 6 / K2").And.NotContain("archive");
        cut.FindAll("[data-testid='sc-needed-group']").Select(g => g.GetAttribute("data-group")).Should().Equal("SourceEvidence", "RequiredForLiveChecks", "RuntimeEvidence", "Secondary");
        cut.Find("[data-testid='sc-needed-configure']").TextContent.Should().Be("Configure test context");
    }

    [Fact]
    public void ReadinessGroupsSourceRuntimeContextAndRuntimeEvidenceWithExactStatuses()
    {
        _api.Overview = new ClassificationOverview { Latest = Result() };
        var cut = Page();

        string Status(string id) => cut.Find($"[data-item='{id}']").GetAttribute("data-status")!;
        Status("source").Should().Be("Ready");
        cut.Find("[data-item='source']").TextContent.Should().Contain("M2LB · c850a1b2…");
        Status("source-model").Should().Be("Ready");
        Status("source-cdc").Should().Be("Ready");
        Status("source-authorization").Should().Be("Ready");
        Status("context-environment").Should().Be("Missing");
        Status("counts").Should().Be("Ready", "the run supplied aligned counts");
        Status("telemetry").Should().Be("NotAvailable");
        cut.Find("[data-item='telemetry']").TextContent.Should().Contain("Not available").And.Contain("never 0");
        Status("browser").Should().Be("NotAssessed");
        cut.FindAll("[data-testid='sc-needed-item'] .sc-badge").Select(b => b.TextContent).Should().OnlyContain(s => new[] { "Ready", "Partial", "Missing", "Not assessed", "Not available", "Disabled" }.Contains(s),
            "prerequisites are never Pass or Fail");
        cut.Find("[data-testid='sc-needed-count']").TextContent.Should().Be("10 items", "secondary and ready rows are listed but not counted");
        cut.FindAll("[data-testid='sc-needed'] [role='region']").Should().HaveCount(4).And.OnlyContain(r => r.GetAttribute("tabindex") == "0");
    }

    [Fact]
    public void ConfiguringTheTestContextFromTheCardRemovesItsItems()
    {
        _api.Overview = new ClassificationOverview { Latest = Result() };
        var cut = Page();

        cut.Find("[data-testid='sc-needed-configure']").Click();
        cut.Find("[data-testid='sc-context-form']");
        cut.Find("[data-testid='sc-context-environment']").Change("DEV");
        cut.Find("[data-testid='sc-context-endpoint']").Change("https://person.dev.example.test/graphql");
        cut.Find("[data-testid='sc-context-child-id-2']").Change(Guid.NewGuid().ToString());
        cut.Find("[data-testid='sc-context-child-id-3']").Change(Guid.NewGuid().ToString());
        cut.Find("[data-testid='sc-context-unauthorized']").Change("Saksbehandler uten gradert tilgang");
        cut.Find("[data-testid='sc-context-authorized']").Change("Saksbehandler med gradert tilgang");
        cut.Find("[data-testid='sc-context-approved']").Change(true);
        cut.Find("[data-testid='sc-context-save']").Click();

        cut.Find("[data-testid='sc-needed']").GetAttribute("data-count").Should().Be("3");
        cut.FindAll("[data-testid='sc-needed-item'][data-status='Missing'], [data-testid='sc-needed-item'][data-status='NotAvailable']").Select(i => i.GetAttribute("data-item")).Should().Equal("child-0", "child-1", "telemetry");
        cut.Find("[data-testid='sc-orientation']").TextContent.Should().Contain("Missing evidence: 3");
    }

    [Fact]
    public void MissingEvidenceNeverChangesTheFindingCount()
    {
        _api.Overview = new ClassificationOverview { Latest = Result() };
        var cut = Page();

        cut.Find("[data-testid='sc-orientation']").TextContent.Should().Be("1 finding(s) · 1 high/critical · Runtime checks: 4 observation(s) · Missing evidence: 10");
        cut.FindAll("[data-testid='sc-finding']").Should().ContainSingle();
    }

    [Fact]
    public void CompletePrerequisitesShowACompactPositiveState()
    {
        var full = new ClassificationTestContext
        {
            Environment = "DEV", GraphQlEndpoint = "https://person.dev.example.test/graphql", ApprovedByTestLead = true, UnauthorizedIdentityLabel = "u", AuthorizedIdentityLabel = "a",
            TestChildren = [.. Enumerable.Range(0, 4).Select(i => new ClassificationTestChild { Nivaa = i, BarnRegistreringId = Guid.NewGuid() })],
        };
        var result = Result() with { Checks = [.. Result().Checks.Where(c => c.CheckId != "metric-runtime"), new() { CheckId = "metric-runtime", Area = ClassificationArea.Observability, State = ClassificationState.Observed, Provenance = IntegrationEvidenceSource.ApplicationInsights }] };
        _api.Overview = new ClassificationOverview { Latest = result, Context = full };

        var cut = Page();

        var card = cut.Find("[data-testid='sc-needed']");
        card.GetAttribute("data-count").Should().Be("0");
        card.TextContent.Should().Contain("Review prerequisites complete").And.NotContain("0 items");
        cut.FindAll("[data-testid='sc-needed-count']").Should().BeEmpty();
    }

    [Fact]
    public void HistoricalReviewShowsItsRecordedMissingEvidence()
    {
        var older = Result() with
        {
            CompletedAt = DateTimeOffset.UtcNow.AddDays(-1),
            MissingItems = [new() { Id = "child-3", Group = ClassificationMissingGroup.RequiredForLiveChecks, Title = "Synthetic level 3 test child (Kode6 / Kode 6 / K2)", ConfiguresContext = true }],
        };
        var latest = Result();
        _api.Result = older;
        _api.Overview = new ClassificationOverview { Latest = latest, History = [new(latest.RunId, latest.CompletedAt, latest.Overall, 1, 4), new(older.RunId, older.CompletedAt, older.Overall, 1, 4)] };
        var cut = Page();

        cut.Find("[data-testid='sc-history']").Change(older.RunId.ToString());

        cut.Find("[data-testid='sc-needed']").GetAttribute("data-count").Should().Be("1");
        cut.Find("[data-testid='sc-needed-recorded']").TextContent.Should().Contain("not recalculated against today's configuration");
        cut.FindAll("[data-testid='sc-needed-item']").Select(i => i.GetAttribute("data-item")).Should().Equal("child-3");
        cut.FindAll("[data-testid='sc-needed-configure']").Should().BeEmpty();
    }

    [Fact]
    public void LegacyRunWithoutStructuredItemsFallsBackToItsRecordedList()
    {
        var older = Result() with { CompletedAt = DateTimeOffset.UtcNow.AddDays(-2) };
        var latest = Result();
        _api.Result = older;
        _api.Overview = new ClassificationOverview { Latest = latest, History = [new(older.RunId, older.CompletedAt, older.Overall, 1, 4)] };
        var cut = Page();

        cut.Find("[data-testid='sc-history']").Change(older.RunId.ToString());

        cut.FindAll("[data-testid='sc-needed-item']").Select(i => i.TextContent).Should().Equal("A synthetic level 3 (Kode6) test child in the approved context.");
    }

    [Fact]
    public void BiRKAndEventHubSayNotAssessedHereWithTheIqrLink()
    {
        _api.Overview = new ClassificationOverview { Latest = WithScopeStages(Result()) };
        var cut = Page();

        var birk = cut.Find("[data-testid='sc-stage'][data-stage='BiRK']");
        birk.QuerySelector("[data-testid='sc-stage-source']")!.TextContent.Should().Be("Source: Not assessed here");
        birk.QuerySelector("[data-testid='sc-stage-source']")!.GetAttribute("title").Should().Be("This stage is relevant to the end-to-end flow but is not evaluated by this review.");
        birk.QuerySelector("[data-testid='sc-stage-runtime']")!.TextContent.Should().Be("Runtime: Not tested");
        birk.TextContent.Should().NotContain("Not applicable");
        var hub = cut.Find("[data-testid='sc-stage'][data-stage='EventHub']");
        hub.QuerySelector("[data-testid='sc-stage-source']")!.TextContent.Should().Be("Evidence: Not assessed here");
        var link = hub.QuerySelector("[data-testid='sc-eventhub-link']")!;
        link.GetAttribute("href").Should().Be("integration-quality-review");
        link.GetAttribute("aria-label").Should().Be("View Event Hub evidence in Integration Quality Review");
        cut.FindAll("[data-testid='sc-eventhub-link']").Should().ContainSingle();
    }

    [Fact]
    public void ObservabilityShowsPartialWithDefinitionAndTelemetryParts()
    {
        _api.Overview = new ClassificationOverview { Latest = WithScopeStages(Result()) };
        var cut = Page();

        var row = cut.Find("[data-testid='sc-summary-row'][data-area='Observability']");
        row.TextContent.Should().Contain("Partial").And.NotContain("Configured").And.Contain("runtime telemetry evidence is unavailable");
        cut.Find("[data-part='Metric definition']").TextContent.Should().Contain("Source verified");
        cut.Find("[data-part='Runtime telemetry']").TextContent.Should().Contain("Not available").And.NotContain("0");
    }

    [Fact]
    public void WhatIsMissingIsListedOnceOnly()
    {
        _api.Overview = new ClassificationOverview { Latest = Result() };
        var cut = Page();

        cut.FindAll("[data-testid='sc-missing']").Should().BeEmpty();
        cut.Find("[data-testid='sc-missing-pointer']").TextContent.Should().Contain("What’s needed to complete this review");
        cut.FindAll("[data-testid='sc-needed']").Should().ContainSingle();
    }

    [Fact]
    public void BeforeAnyReviewTheCardFollowsTheSetup()
    {
        _api.Overview = new ClassificationOverview();
        var cut = Page();

        var order = cut.FindAll("section[data-testid]").Select(e => e.GetAttribute("data-testid")).ToList();
        order.Should().Equal("sc-source-evidence", "sc-coverage-summary", "sc-setup", "sc-needed", "sc-run-card");
        cut.Find("[data-item='counts']").GetAttribute("data-status").Should().Be("Missing", "no run has provided count evidence yet");
    }

    // ── Temporary (in-memory) test context ──────────────────────────────────────────────────────────────────────────

    private static ClassificationTestContext Ready() => new()
    {
        Environment = "DEV", GraphQlEndpoint = "https://person.dev.example.test/graphql", ApprovedByTestLead = true, UpdatedAt = DateTimeOffset.UtcNow,
        UnauthorizedIdentityLabel = "IDENTITY-SENTINEL", AuthorizedIdentityLabel = "IDENTITY-SENTINEL-2",
        TestChildren = [new() { Nivaa = 2, BarnRegistreringId = Guid.Parse("5e0711e1-c41d-4a2b-9c3d-000000000002"), BirkId = "BIRK-SENTINEL" }],
    };

    [Fact]
    public void TemporaryContextSaysInMemoryOnlyAndStartsNotConfigured()
    {
        _api.Overview = new ClassificationOverview();
        var cut = Page();

        cut.Find("[data-testid='sc-context-status']").TextContent.Should().Be("Not configured");
        cut.Find("[data-testid='sc-context-memory']").TextContent.Should().Contain("In memory only — this data is not saved to the database");
        cut.FindAll("[data-testid='sc-context-clear']").Should().BeEmpty();
    }

    [Fact]
    public void ConfiguredContextIsReadyShowsNoValuesAndClearsToNotConfigured()
    {
        _api.Overview = new ClassificationOverview { Context = Ready() };
        var cut = Page();

        cut.Find("[data-testid='sc-context-status']").TextContent.Should().Be("Ready");
        var card = cut.Find("[data-testid='sc-setup']").TextContent;
        card.Should().NotContain("IDENTITY-SENTINEL").And.NotContain("BIRK-SENTINEL").And.NotContain("5e0711e1");

        cut.Find("[data-testid='sc-context-clear']").Click();

        _api.Cleared.Should().Be(1);
        cut.Find("[data-testid='sc-context-status']").TextContent.Should().Be("Not configured");
        cut.Find("[data-testid='sc-status']").TextContent.Should().Contain("cleared from memory").And.Contain("Stored reviews are unchanged");
    }

    [Fact]
    public void KeepInMemorySetsReadyWithoutBrowserStorage()
    {
        _api.Overview = new ClassificationOverview();
        var cut = PageWithSource();

        cut.Find("[data-testid='sc-configure']").Click();
        cut.Find("[data-testid='sc-context-environment']").Change("DEV");
        cut.Find("[data-testid='sc-context-child-id-2']").Change("5e0711e1-c41d-4a2b-9c3d-000000000002");
        cut.Find("[data-testid='sc-context-child-birk-2']").Change("BIRK-SENTINEL");
        cut.Find("[data-testid='sc-context-unauthorized']").Change("IDENTITY-SENTINEL");
        cut.Find("[data-testid='sc-context-save']").TextContent.Should().Be("Keep in memory");
        cut.Find("[data-testid='sc-context-save']").Click();

        cut.Find("[data-testid='sc-context-status']").TextContent.Should().Be("Ready");
        cut.Find("[data-testid='sc-status']").TextContent.Should().Contain("not saved to the database");
        JSInterop.Invocations.Should().NotContain(i => i.Identifier.Contains("localStorage") || i.Identifier.Contains("sessionStorage") || i.Identifier.Contains("indexedDB"));
        JSInterop.Invocations.SelectMany(i => i.Arguments).Select(a => a?.ToString() ?? "").Should().NotContain(a => a.Contains("SENTINEL") || a.Contains("5e0711e1"));
    }

    [Fact]
    public void LostContextAfterRunAsksToConfigureAgain()
    {
        _api.Overview = new ClassificationOverview { Context = Ready() };
        _api.Result = Result();
        _api.LoseContextOnRun = true;
        var cut = Page();

        cut.Find("[data-testid='sc-run']").Click();

        cut.Find("[data-testid='sc-error']").TextContent.Should().Be("Test context is no longer available. Configure temporary test context again.");
        cut.Find("[data-testid='sc-context-status']").TextContent.Should().Be("Not configured");
    }

    [Fact]
    public void ExportCarriesOnlyAContextSummaryEvenForLegacyRunFixtures()
    {
        var legacy = Result() with { Context = Ready(), ContextSummary = null };
        var current = Result() with { ContextSummary = ClassificationContextSummary.From(Ready()) };

        foreach (var html in new[] { new ReportExportService().ExportClassificationReview(legacy), new ReportExportService().ExportClassificationReview(current) })
        {
            html.Should().NotContain("IDENTITY-SENTINEL").And.NotContain("BIRK-SENTINEL").And.NotContain("5e0711e1").And.NotContain("person.dev.example.test");
            html.Should().Contain("Temporary test context: DEV").And.Contain("unauthorized identity configured").And.Contain("level(s) 2").And.Contain("Context values are not recorded");
        }
    }

    // ── Source evidence from Source Analysis snapshots ──────────────────────────────────────────────────────────────

    [Fact]
    public void NoSnapshotAvailablePointsToSourceAnalysisAndKeepsRuntimeContextConfigurable()
    {
        _api.Snapshots = [];
        var cut = Page();

        var empty = cut.Find("[data-testid='sc-source-empty']");
        empty.TextContent.Should().Contain("No source snapshot available").And.Contain("source snapshots managed by Source Analysis");
        cut.Find("[data-testid='sc-open-source-analysis']").GetAttribute("href").Should().Be("source-analysis");
        cut.FindAll("input[type='file']").Should().BeEmpty();
        cut.Find("[data-item='source']").GetAttribute("data-status").Should().Be("Missing");
        cut.Find("[data-item='source-model']").GetAttribute("data-status").Should().Be("NotAssessed", "nothing to assess is not counted as missing");
        cut.Find("[data-testid='sc-configure']").Click();
        cut.Find("[data-testid='sc-context-form']").Should().NotBeNull("runtime context stays independently configurable");
        cut.Find("[data-testid='sc-run-source']").TextContent.Should().Contain("Not run — no source snapshot selected");
    }

    [Fact]
    public void NotSelectedOffersTheCurrentSnapshotWithoutSelectingIt()
    {
        var cut = Page();

        cut.Find("[data-testid='sc-source-state']").TextContent.Should().Be("Not selected");
        _api.ScopeRequests.Should().Equal(new ClassificationSourceScopeRequest?[] { null }, "nothing is selected silently");
        cut.Find("[data-testid='sc-current-snapshot']").TextContent.Should().Contain("M2LB").And.Contain("M2LB _2_.zip").And.Contain("c850a1b2…");
        cut.FindAll("[data-testid='sc-coverage-row']").Select(r => r.TextContent).Should().OnlyContain(t => t.Contains("Not assessed"));
        cut.FindAll("[data-testid='sc-primary'] option").Select(o => o.TextContent).Should().Contain(o => o.Contains("M2LB (1).zip") && !o.Contains("latest")).And.Contain(o => o.Contains("M2LB _2_.zip") && o.Contains("latest"));
        cut.FindAll("[data-testid='sc-primary'] optgroup").Select(g => g.GetAttribute("label")).Should().Equal("M2LB", "Shared.Security");

        cut.Find("[data-testid='sc-use-current']").Click();

        _api.ScopeRequests.Last()!.PrimarySnapshotId.Should().Be(App.SnapshotId);
        cut.Find("[data-testid='sc-source-state']").TextContent.Should().Be("Selected");
        cut.Find("[data-testid='sc-primary-fingerprint']").GetAttribute("title").Should().Be(App.Fingerprint);
        cut.Find("[data-testid='sc-primary-status']").TextContent.Should().Contain("Partial").And.Contain("not a security result");
        cut.Find("[data-testid='sc-coverage-row'][data-row='model']").TextContent.Should().Contain("Detected").And.Contain("4 level(s)");
        cut.Find("[data-testid='sc-coverage-row'][data-row='runtime']").TextContent.Should().Contain("Not assessed");
        cut.Find("[data-testid='sc-coverage-note']").TextContent.Should().Be("Source evidence shows implementation/configuration paths only. It does not prove runtime enforcement.");
        cut.FindAll("[data-testid='sc-coverage-row'] .sc-badge").Select(b => b.TextContent).Should().NotContain(new[] { "Pass", "Fail", "Secure", "Protected" });
    }

    [Fact]
    public void ViewSourceEvidenceGroupsFactsWithTheirSnapshot()
    {
        var cut = PageWithSource();

        cut.Find("[data-testid='sc-source-details'] button").Click();

        cut.FindAll("[data-testid='sc-source-group']").Select(g => g.GetAttribute("data-group")).Should().Equal("Classification model", "Propagation path", "Authorization", "GraphQL / access path");
        cut.Find("[data-fact='cdc-deserializer']").TextContent.Should().Contain("Issue detected").And.Contain("Source: M2LB c850a1b2…").And.Contain("CdcProcessorWorker.cs:301");
        cut.Find("[data-testid='sc-source-tests']").TextContent.Should().Contain("Unit only").And.Contain("M2LB");
        cut.Find("[data-testid='sc-source-limitations']").TextContent.Should().Contain("Syntax-only");
    }

    [Fact]
    public void PreviousScopeIsKeptAndANewerSnapshotIsOnlyOffered()
    {
        _api.Overview = new ClassificationOverview { Latest = Result() with { SourceScope = new ClassificationSourceScope { Primary = Entry(AppOld) } } };
        _api.Newer = [App];
        var cut = Page();

        _api.ScopeRequests.Single()!.PrimarySnapshotId.Should().Be(AppOld.SnapshotId, "the previous review's scope is proposed, never the latest snapshot");
        cut.Find("[data-testid='sc-primary']").GetAttribute("value").Should().Be(AppOld.SnapshotId.ToString());
        cut.FindAll("[data-testid='sc-current-snapshot']").Should().BeEmpty();
        var notice = cut.Find("[data-testid='sc-newer-snapshot']");
        notice.TextContent.Should().Contain("Newer source snapshot available").And.Contain("0ld0ld00…").And.Contain("c850a1b2…").And.Contain("not switched automatically");

        cut.Find("[data-testid='sc-keep-current']").Click();
        cut.FindAll("[data-testid='sc-newer-snapshot']").Should().BeEmpty();
        _api.ScopeRequests.Should().HaveCount(1, "keeping the current snapshot changes nothing");
    }

    [Fact]
    public void ReviewChangeSwitchesOnlyWhenAsked()
    {
        _api.Overview = new ClassificationOverview { Latest = Result() with { SourceScope = new ClassificationSourceScope { Primary = Entry(AppOld) } } };
        _api.Newer = [App];
        var cut = Page();

        cut.Find("[data-testid='sc-review-newer']").Click();

        _api.ScopeRequests.Last()!.PrimarySnapshotId.Should().Be(App.SnapshotId);
        cut.Find("[data-testid='sc-status']").TextContent.Should().Contain("c850a1b2…");
    }

    [Fact]
    public void RelatedSourceIsSuggestedWithEvidenceButNotIncluded()
    {
        _api.Candidates = [SharedCandidate];
        var cut = PageWithSource();

        var candidate = cut.Find("[data-testid='sc-related-candidate']");
        candidate.TextContent.Should().Contain("Related source detected: Shared.Security").And.Contain("Suggested").And.Contain("Snapshot available");
        cut.Find("[data-testid='sc-related-reason']").TextContent.Should().Be("Declares a classification type the primary source uses.");
        cut.Find("[data-testid='sc-related-evidence']").TextContent.Should().Contain("Type reference").And.Contain("CdcEvent");
        candidate.TextContent.Should().Contain("a91c0000…");
        cut.Find("[data-testid='sc-related-count']").TextContent.Should().Contain("None included");
        _api.ScopeRequests.Last()!.RelatedSnapshotIds.Should().BeEmpty("a suggestion is never included automatically");
        cut.Find("[data-testid='sc-scope-incomplete']").TextContent.Should().Contain("Review scope may be incomplete").And.Contain("Shared.Security");
        cut.Find("[data-testid='sc-related-include']").TextContent.Should().Contain("Include in review").And.Contain("Shared.Security");
    }

    [Fact]
    public void IncludeAndRemoveRelatedSourceBindTheExactSnapshot()
    {
        _api.Candidates = [SharedCandidate];
        _api.Result = Result();
        var cut = PageWithSource();

        cut.Find("[data-testid='sc-related-include']").Click();

        _api.ScopeRequests.Last()!.RelatedSnapshotIds.Should().Equal(Shared.SnapshotId);
        cut.Find("[data-testid='sc-related-included']").TextContent.Should().Contain("Included");
        cut.Find("[data-testid='sc-related-count']").TextContent.Should().Contain("1 included");
        cut.FindAll("[data-testid='sc-scope-incomplete']").Should().BeEmpty();
        cut.Find("[data-testid='sc-run-source']").TextContent.Should().Contain("M2LB · c850a1b2…").And.Contain("Shared.Security · a91c0000…");

        cut.Find("[data-testid='sc-run']").Click();
        _api.LastRun!.SourceScope!.PrimarySnapshotId.Should().Be(App.SnapshotId);
        _api.LastRun.SourceScope.RelatedSnapshotIds.Should().Equal(Shared.SnapshotId);

        cut.Find("[data-testid='sc-related-remove']").Click();
        _api.ScopeRequests.Last()!.RelatedSnapshotIds.Should().BeEmpty();
    }

    [Fact]
    public void ContinueWithoutRecordsTheExclusion()
    {
        _api.Candidates = [SharedCandidate];
        _api.Result = Result();
        var cut = PageWithSource();

        cut.Find("[data-testid='sc-continue-without']").Click();

        _api.ScopeRequests.Last()!.ExcludedSuggestions.Should().Equal("Shared.Security");
        cut.Find("[data-testid='sc-continued-without']").TextContent.Should().Contain("recorded as a scope limitation");
        cut.Find("[data-testid='sc-run']").Click();
        _api.LastRun!.SourceScope!.ExcludedSuggestions.Should().Equal("Shared.Security");
    }

    [Fact]
    public void RelatedSourceWithoutSnapshotPointsToSourceAnalysis()
    {
        _api.Candidates = [SharedCandidate with { State = RelatedSourceState.SnapshotUnavailable, MatchingSnapshotIds = [], Reason = "Referenced from source, but no analyzed snapshot of this source is available." }];
        var cut = PageWithSource();

        cut.Find("[data-testid='sc-related-state']").TextContent.Should().Be("No analyzed snapshot");
        cut.Find("[data-testid='sc-related-missing']").TextContent.Should().Contain("No analyzed source snapshot").And.Contain("recorded as a limitation");
        cut.Find("[data-testid='sc-related-open-source-analysis']").GetAttribute("href").Should().Be("source-analysis");
        cut.FindAll("[data-testid='sc-related-include']").Should().BeEmpty("nothing to include, and nothing uploaded here");
    }

    [Fact]
    public void RunWithoutSourceIsRuntimeOnlyAndNeverSendsALatestSnapshot()
    {
        _api.Result = Result() with { SourceScope = null };
        var cut = Page();

        cut.Find("[data-testid='sc-run']").Click();

        _api.LastRun!.SourceScope.Should().BeNull();
        cut.Find("[data-testid='sc-result-scope']").TextContent.Should().Contain("No source snapshot was selected");
    }

    [Fact]
    public void ScopeErrorBlocksTheRunUntilRepaired()
    {
        _api.Overview = new ClassificationOverview { Latest = Result() with { SourceScope = new ClassificationSourceScope { Primary = Entry(AppOld) } } };
        _api.ScopeError = "Source snapshot bbbbbbbb-0000-0000-0000-000000000001 is unavailable in Source Analysis. Repair the source scope; nothing is substituted.";
        var cut = Page();

        cut.Find("[data-testid='sc-scope-error']").TextContent.Should().Contain("nothing is substituted");
        cut.Find("[data-testid='sc-source-state']").TextContent.Should().Be("Needs repair");
        cut.Find("[data-testid='sc-run']").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid='sc-run-blocked']").TextContent.Should().Contain("Repair the source scope");
    }

    [Fact]
    public void RejectedRunShowsTheBackendReason()
    {
        _api.Result = Result();
        _api.RejectRun = "M2LB (c850a1b2…): Analyzed before security classification evidence was captured.";
        var cut = PageWithSource();

        cut.Find("[data-testid='sc-run']").Click();

        cut.Find("[data-testid='sc-error']").TextContent.Should().Contain("Analyzed before security classification evidence was captured");
    }

    [Fact]
    public void ResultShowsItsExactSourceScopeAndSourceFindingProvenance()
    {
        _api.Overview = new ClassificationOverview
        {
            Latest = Result() with { SourceScope = new ClassificationSourceScope { Primary = Entry(App), Related = [Entry(Shared)], Limitations = ["Related source detected but not included in this review scope: Other (no analyzed snapshot)."] } },
        };
        var cut = Page();

        cut.Find("[data-testid='sc-result-scope']").TextContent.Should().Contain("Primary M2LB · c850a1b2…").And.Contain("Related Shared.Security · a91c0000…");
        cut.Find("[data-testid='sc-result-scope-limitations']").TextContent.Should().Contain("Related source detected but not included");
        cut.Find("[data-testid='sc-finding-source']").TextContent.Should().Contain("M2LB c850a1b2…").And.Contain("not an observed runtime breach");
    }

    [Fact]
    public void LegacyArchiveReviewStaysReadableWithoutAnInventedSnapshot()
    {
        var legacy = Result() with { SourceScope = null, SourceArchives = [new SourceArchive("M2LB (1).zip", new string('e', 64), 300)], Findings = [Result().Findings[0] with { Sources = [] }] };
        _api.Result = legacy;
        _api.Overview = new ClassificationOverview { Latest = Result(), History = [new(legacy.RunId, legacy.CompletedAt, legacy.Overall, 1, 4)] };
        var cut = Page();

        cut.Find("[data-testid='sc-history']").Change(legacy.RunId.ToString());

        cut.Find("[data-testid='sc-result-scope']").TextContent.Should().Contain("Legacy source input: M2LB (1).zip (sha256 eeeeeeee…)").And.Contain("no snapshot id");
        cut.FindAll("[data-testid='sc-finding-source']").Should().BeEmpty("no provenance is fabricated");
        cut.Find("[data-testid='sc-primary']").GetAttribute("value").Should().Be(App.SnapshotId.ToString(), "opening history does not change the scope for the next run");
        new ReportExportService().ExportClassificationReview(legacy).Should().Contain("Legacy source input");
    }

    [Fact]
    public void SourceAnalysisDisabledHidesSnapshotsButKeepsHistory()
    {
        _flags.ApplyLocalFlags(new FeatureVisibilityDto { SourceAnalysis = false });
        _api.Overview = new ClassificationOverview { Latest = Result() };
        _api.Result = Result();
        var cut = Page();

        cut.Find("[data-testid='sc-source-disabled']").TextContent.Should().Contain("Source Analysis is disabled").And.Contain("Stored reviews remain readable");
        cut.Find("[data-testid='sc-source-state']").TextContent.Should().Be("Source Analysis disabled");
        _api.ScopeRequests.Should().BeEmpty("feature visibility is not bypassed");
        cut.Find("[data-testid='sc-result-scope']").TextContent.Should().Contain("c850a1b2…", "historical provenance stays intact");
        cut.Find("[data-testid='sc-run']").Click();
        _api.LastRun!.SourceScope.Should().BeNull();
    }

    [Fact]
    public void SourceEvidenceAndRuntimeContextAreSeparateSections()
    {
        var cut = PageWithSource();

        var source = cut.Find("[data-testid='sc-source-evidence']");
        var runtime = cut.Find("[data-testid='sc-setup']");
        source.QuerySelector("h2")!.TextContent.Should().StartWith("Source evidence");
        runtime.QuerySelector("h2")!.TextContent.Should().Be("Runtime test context");
        source.QuerySelectorAll("[data-testid='sc-context-form'], [data-testid='sc-temp-context'], [data-testid='sc-token-unauthorized']").Should().BeEmpty();
        runtime.QuerySelectorAll("[data-testid='sc-primary'], [data-testid='sc-coverage-summary']").Should().BeEmpty();
        cut.Find("[data-testid='sc-primary']").ParentElement!.TagName.Should().Be("LABEL", "the snapshot selector has a visible label");
        cut.Find("[data-testid='sc-refresh-source']").TextContent.Should().Be("Refresh source evidence");
        cut.Find("[data-testid='sc-change-snapshot']").TextContent.Should().Be("Change source snapshot");
        cut.Markup.Should().NotContain("Repository tests (M2LB)");
    }

    [Fact]
    public void RefreshSourceEvidenceRereadsTheSameScope()
    {
        var cut = PageWithSource();
        var before = _api.ScopeRequests.Count;

        cut.Find("[data-testid='sc-refresh-source']").Click();

        _api.ScopeRequests.Should().HaveCount(before + 1);
        _api.ScopeRequests.Last()!.PrimarySnapshotId.Should().Be(App.SnapshotId, "the same exact snapshot — never a newer one");
    }
}
