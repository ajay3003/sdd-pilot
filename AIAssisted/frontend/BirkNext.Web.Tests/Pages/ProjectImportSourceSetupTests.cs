using BirkNext.Integrations;
using BirkNext.ProjectImport;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.ProjectImport;
using BirkNext.Web.Tests.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using ProjectImportPage = BirkNext.Web.Pages.ProjectImport;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Import Project shows what already exists before asking for another upload: previously imported projects (source-only ones included, the
/// current one excluded), a test pack's source setup rendered from its generic source-role metadata, and an exact-duplicate notice that never
/// chooses for the tester. Reusing an import uploads nothing, creates no snapshot and never touches IQR bindings.
/// </summary>
public sealed class ProjectImportSourceSetupTests : BunitContext
{
    private readonly Mock<IProjectImportApiService> _api = new();
    private readonly Mock<IIntegrationJourneysApiService> _journeys = new();
    private readonly WorkspaceArtifactRepository _repository = new();
    private readonly ProjectImportActivation _activation;
    private readonly Mock<IWorkspaceAutoSaveService> _autoSave = new();
    private SourceSetupView? _setup = new();
    private ProjectImportPreview? _preview;
    private FrontendAnalysisProfile? _profile = new() { Id = "dev", Name = "Dev" };

    public ProjectImportSourceSetupTests()
    {
        _activation = new ProjectImportActivation(_repository);
        _api.Setup(a => a.PreviewAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => (_preview, (SourceUploadFailure?)null));
        _journeys.Setup(j => j.SourceSetupAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ActiveEventApiResult<SourceSetupView>(_setup, _setup is null ? "unavailable" : null));
        _autoSave.Setup(a => a.SaveNowAsync()).ReturnsAsync(true);
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(() => _profile is null ? new FrontendAnalysisContext() : new FrontendAnalysisContext { ActiveProfile = _profile });
        Services.AddSingleton(_api.Object);
        Services.AddSingleton(_journeys.Object);
        Services.AddSingleton(_repository);
        Services.AddSingleton(_activation);
        Services.AddSingleton(context.Object);
        Services.AddSingleton(_autoSave.Object);
        Services.AddSingleton(Mock.Of<ITechnologyCoverageApiService>());
        Services.AddSingleton(WorkspaceSnapshots.Projection(CurrentWorkspaceSnapshot.None()).Object);
        Services.AddSingleton(Mock.Of<IFrontendAnalysisSettingsService>());
        Services.AddScoped<ProjectApplicabilityState>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static readonly Guid MuSnapshot = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid M2lbSnapshot = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OldM2lbSnapshot = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private const string MuSha = "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1";
    private const string M2lbSha = "b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2";
    private const string OldM2lbSha = "c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3";
    private const string DocsSha = "d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4";

    /// <summary>Records an import the way the page does (documents into the repository, source outcome recorded), then closes it.</summary>
    private SddProjectImportRecord Import(string archive, string sha, Guid? snapshot, bool documents, DateTimeOffset importedAt)
    {
        var docs = documents ? new[] { ("shop/specs/001-cart/spec.md", ProjectImportActivationTests.Spec) } : [];
        var preview = ProjectImportActivationTests.Preview(sha, snapshot is not null, docs) with
        {
            Archive = new ProjectImportArchive(archive, sha, 4096, 12), ProjectName = Path.GetFileNameWithoutExtension(archive),
        };
        var commit = ProjectImportActivationTests.Commit(preview, snapshot is null ? ProjectImportSourceState.NotDetected : ProjectImportSourceState.Created, snapshot);
        var record = _activation.Activate(preview, ProjectImportArtifactDiscovery.From(preview), commit with { Provenance = commit.Provenance with { ImportedAt = importedAt } });
        return record;
    }

    private static TestPackSourceRole Role(string key, string label, TestPackSourceRequirement requirement = TestPackSourceRequirement.Required, string? condition = null,
        params string[] journeys) => new(key, label, label)
    {
        Requirement = requirement, RequirementCondition = condition, Journeys = journeys, ExpectedArtifact = $"{label} source archive", Capabilities = [$"{label} capability"],
    };

    private static TestPackSourceCandidate Candidate(Guid id, string archive, string sha, bool bound = false, int daysAgo = 1) =>
        new(id, archive, sha, DateTimeOffset.UtcNow.AddDays(-daysAgo)) { Bound = bound };

    /// <summary>A two-repository pack with an API-described source and optional infrastructure — the shape of the Skolenærvær pack, generic names.</summary>
    private static SourceSetupView Setup(bool bindingsRead = true, IReadOnlyList<TestPackSourceStatus>? sources = null, params SourceSetupSnapshot[] snapshots) => new()
    {
        EnvironmentId = bindingsRead ? "dev" : "", BindingsRead = bindingsRead, Snapshots = snapshots,
        Packs = [new TestPackSourceSetup("demo.pack", "Demo Testing", sources ?? DefaultSources()) { JourneyNames = new Dictionary<string, string> { ["intake"] = "Report intake" } }],
    };

    private static IReadOnlyList<TestPackSourceStatus> DefaultSources() =>
    [
        new(Role("hub", "Message hub", journeys: "intake"), TestPackSourceState.Bound)
        {
            Candidates = [Candidate(MuSnapshot, "Tiltak 2 - hub (1).zip", MuSha, bound: true)], Integrations = ["Demo – report intake"], BoundArchives = ["Tiltak 2 - hub (1).zip"],
            Detail = "Message hub source is bound.", NextAction = "Open IQR Evidence", NextActionTarget = "iqr-evidence",
        },
        new(Role("platform", "Platform", journeys: "intake"), TestPackSourceState.AvailableNotBound)
        {
            Candidates = [Candidate(M2lbSnapshot, "platform.zip", M2lbSha)], AvailableArchives = ["platform.zip"], Integrations = ["Demo – period closed"],
            Detail = "Platform source is available (platform.zip) but not bound where it is needed.", NextAction = "Bind it in IQR Evidence", NextActionTarget = "iqr-evidence",
        },
        new(Role("app", "Demo application", condition: "Required for application metadata."), TestPackSourceState.Missing)
        {
            Detail = "Demo application source is missing.", NextAction = "Import it in Source Analysis", NextActionTarget = "source-analysis",
        },
        new(Role("infra", "Infrastructure as Code", TestPackSourceRequirement.Optional, "Useful only when infrastructure/runtime configuration must be source-verified."),
            TestPackSourceState.NotProvided) { Detail = "Optional — not provided.", NextAction = "Optional", NextActionTarget = "source-analysis" },
    ];

    private static SourceSetupSnapshot Snap(Guid id, string archive, string sha, string? importId = null, int daysAgo = 1, params string[] bound) =>
        new(id, archive, sha, DateTimeOffset.UtcNow.AddDays(-daysAgo), "Partial") { ImportId = importId, BoundIntegrations = bound };

    private IRenderedComponent<ProjectImportPage> RenderPage()
    {
        var cut = Render<ProjectImportPage>();
        cut.WaitForAssertion(() => _journeys.Verify(j => j.SourceSetupAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce()));
        return cut;
    }

    private void ImportHistory()
    {
        var mu = Import("Tiltak 2 - hub (1).zip", MuSha, MuSnapshot, documents: false, DateTimeOffset.UtcNow.AddDays(-3));
        Import("docs-only.zip", DocsSha, null, documents: true, DateTimeOffset.UtcNow.AddDays(-2));
        Import("platform.zip", M2lbSha, M2lbSnapshot, documents: true, DateTimeOffset.UtcNow.AddDays(-1));
        _setup = Setup(true, null,
            Snap(MuSnapshot, "Tiltak 2 - hub (1).zip", MuSha, mu.ImportId, 3, "Demo – report intake"),
            Snap(M2lbSnapshot, "platform.zip", M2lbSha, "import-" + M2lbSha[..16], 1));
    }

    [Fact]
    public void PreviousImports_AreListed_SourceOnlyIncluded_AndTheCurrentProjectIsNotRepeated()
    {
        ImportHistory();
        var cut = RenderPage();

        cut.Find("[data-testid=pi-current-name]").TextContent.Should().Be("platform");
        cut.Find("[data-testid=pi-current-logical]").TextContent.Should().Be("Platform");
        cut.Find("[data-testid=pi-current-snapshot]").TextContent.Should().Contain("22222222").And.Contain("Partial");
        cut.Find("[data-testid=pi-current-bindings]").TextContent.Should().Be("Not bound in IQR");
        var rows = cut.FindAll("[data-testid=pi-previous-row]");
        rows.Select(r => r.QuerySelector("[data-testid=pi-previous-archive]")!.TextContent).Should().Equal("docs-only.zip", "Tiltak 2 - hub (1).zip");
        rows.Should().NotContain(r => r.TextContent.Contains("platform.zip"), "the current project is shown once, above");

        var hub = rows.Single(r => r.TextContent.Contains("Tiltak 2"));
        hub.QuerySelector("[data-testid=pi-previous-logical]")!.TextContent.Should().Be("Message hub");
        hub.QuerySelector("[data-testid=pi-previous-documents]")!.TextContent.Should().Be("No project documents", "a source-only import is history too");
        hub.QuerySelector("[data-testid=pi-previous-bindings]")!.TextContent.Should().Be("Used by 1 IQR integration: Demo – report intake");
        hub.QuerySelector("[data-testid=pi-previous-open-source]")!.GetAttribute("href").Should().Be($"source-analysis?snapshot={MuSnapshot:D}");
        hub.QuerySelector("[data-testid=pi-previous-open-source]")!.TextContent.Should().Be("Open Source Analysis");
        hub.QuerySelector("[data-testid=pi-previous-open-source]")!.GetAttribute("aria-label").Should().Be("Open Tiltak 2 - hub (1) in Source Analysis");
        hub.QuerySelector("[data-testid=pi-previous-use]")!.TextContent.Should().Be("Set as current project");
        hub.QuerySelector("[data-testid=pi-previous-use]")!.GetAttribute("aria-label").Should().Be("Set Tiltak 2 - hub (1) as current project");
        // Compact by default: role, analysis, binding count and date; provenance (archive, fingerprint, snapshot, origin) only behind Details.
        hub.QuerySelector("[data-testid=pi-previous-role]")!.TextContent.Should().Be("Message hub");
        hub.QuerySelector("[data-testid=pi-previous-analysis]").Should().NotBeNull();
        hub.QuerySelector("[data-testid=pi-previous-binding-count]")!.TextContent.Should().Be("1", "a target is selected and the source is bound");
        var details = hub.QuerySelector("[data-testid=pi-previous-details]")!;
        details.HasAttribute("open").Should().BeFalse();
        details.QuerySelector("[data-testid=pi-previous-archive]").Should().NotBeNull("the archive name is provenance");
        details.QuerySelector("code").Should().NotBeNull("the fingerprint is behind Details");
        details.QuerySelector("[data-testid=pi-previous-source]")!.TextContent.Should().StartWith("Snapshot ");
        cut.Find("#pi-previous-heading").TextContent.Should().Be("Previously imported projects and sources");

        var docs = rows.Single(r => r.TextContent.Contains("docs-only.zip"));
        docs.QuerySelector("[data-testid=pi-previous-logical]")!.TextContent.Should().Be("None", "a document-only import never satisfies a source requirement");
        docs.QuerySelector("[data-testid=pi-previous-source]")!.TextContent.Should().Be("No source");
        docs.QuerySelectorAll("[data-testid=pi-previous-open-source]").Should().BeEmpty();
    }

    [Fact]
    public void UseAsCurrent_RestoresThePreviousImport_WithoutUploadSnapshotOrBindingChange()
    {
        ImportHistory();
        var bindingsBefore = System.Text.Json.JsonSerializer.Serialize(_setup!.Snapshots.Select(s => s.BoundIntegrations));
        var cut = RenderPage();

        cut.FindAll("[data-testid=pi-previous-row]").Single(r => r.TextContent.Contains("Tiltak 2")).QuerySelector("[data-testid=pi-previous-use]")!.Click();

        cut.Find("[data-testid=pi-current-archive]").TextContent.Should().Be("Tiltak 2 - hub (1).zip");
        _activation.Current!.SourceSnapshotId.Should().Be(MuSnapshot, "the existing snapshot is reused, never re-created");
        cut.Find("[data-testid=pi-current-open-source]").GetAttribute("href").Should().Be($"source-analysis?snapshot={MuSnapshot:D}");
        cut.FindAll("[data-testid=pi-previous-row]").Select(r => r.QuerySelector("[data-testid=pi-previous-archive]")!.TextContent).Should().Contain("platform.zip").And.NotContain("Tiltak 2 - hub (1).zip");
        _api.Verify(a => a.PreviewAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Never());
        _api.Verify(a => a.CommitAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never());
        _api.Verify(a => a.CommitWithProgressAsync(It.IsAny<Guid>(), It.IsAny<IProgress<ProjectImportCommitEvent>?>(), It.IsAny<CancellationToken>()), Times.Never());
        _journeys.Verify(j => j.StartAsync(It.IsAny<IntegrationJourneyRunRequest>(), It.IsAny<CancellationToken>()), Times.Never());
        System.Text.Json.JsonSerializer.Serialize(_setup.Snapshots.Select(s => s.BoundIntegrations)).Should().Be(bindingsBefore);
        _autoSave.Verify(a => a.SaveNowAsync(), Times.AtLeastOnce(), "the selection is a workspace change and is saved");
    }

    [Fact]
    public void UseAsCurrent_KeepsTheEarlierDocumentSelections()
    {
        var first = Import("platform.zip", M2lbSha, M2lbSnapshot, documents: true, DateTimeOffset.UtcNow.AddDays(-2));
        var selected = _repository.SddLifecycle.Revisions.Where(r => r.ProjectImportId == first.ImportId && r.IsCurrentSelection).Select(r => r.RevisionId).ToList();
        Import("other.zip", OldM2lbSha, null, documents: true, DateTimeOffset.UtcNow.AddDays(-1));

        _activation.UseAsCurrent(first.ImportId).Should().BeTrue();

        _activation.Current!.ImportId.Should().Be(first.ImportId);
        _repository.SddLifecycle.Revisions.Where(r => r.ProjectImportId == first.ImportId && r.IsCurrentSelection).Select(r => r.RevisionId).Should().Equal(selected);
        _repository.Get(WorkspaceArtifactType.Specification).Should().NotBeNull();
        _activation.UseAsCurrent("import-unknown").Should().BeFalse();
    }

    [Fact]
    public void AnExactDuplicate_IsNamed_AndNothingIsChosenUntilTheTesterDecides()
    {
        ImportHistory();
        _preview = ProjectImportActivationTests.Preview(MuSha, source: true) with { Archive = new ProjectImportArchive("hub-renamed.zip", MuSha, 4096, 12) };
        var cut = RenderPage();
        cut.Find("[data-testid=pi-current-import-another]").Click();
        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary([1, 2, 3], "hub-renamed.zip"));
        cut.WaitForAssertion(() => cut.Find("[data-testid=pi-duplicate]"));

        cut.Find("[data-testid=pi-duplicate]").TextContent.Should().Contain("Matching imported source already exists").And.Contain("Tiltak 2 - hub (1).zip");
        cut.Find("[data-testid=pi-duplicate-again-help]").TextContent.Should().Contain("no second history entry").And.Contain("Nothing is deleted");
        cut.Find("[data-testid=pi-import]").HasAttribute("disabled").Should().BeTrue("a duplicate is never imported or reused silently");
        cut.Find("[data-testid=pi-duplicate-import-again]").Click();
        cut.FindAll("[data-testid=pi-duplicate]").Should().BeEmpty();
        cut.Find("[data-testid=pi-import]").HasAttribute("disabled").Should().BeFalse("Import again stays possible");
    }

    [Fact]
    public void UseExisting_ForADuplicate_ReusesTheEarlierImport_AndImportsNothing()
    {
        ImportHistory();
        _preview = ProjectImportActivationTests.Preview(MuSha, source: true);
        var cut = RenderPage();
        cut.Find("[data-testid=pi-current-import-another]").Click();
        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary([1, 2, 3], "again.zip"));
        cut.WaitForAssertion(() => cut.Find("[data-testid=pi-duplicate-use]"));
        cut.Find("[data-testid=pi-duplicate-use]").Click();

        cut.Find("[data-testid=pi-current-archive]").TextContent.Should().Be("Tiltak 2 - hub (1).zip");
        _api.Verify(a => a.CommitWithProgressAsync(It.IsAny<Guid>(), It.IsAny<IProgress<ProjectImportCommitEvent>?>(), It.IsAny<CancellationToken>()), Times.Never());
        _api.Verify(a => a.DiscardAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once(), "the staged duplicate is released");
    }

    [Fact]
    public void SourceSetup_RendersEachLogicalSource_WithRequirementStateAndOwnerAction()
    {
        ImportHistory();
        var cut = RenderPage();
        var setup = cut.Find("[data-testid=pi-source-setup]");
        setup.QuerySelector("h2")!.TextContent.Should().Be("Demo Testing source setup");
        AngleSharp.Dom.IElement Row(string key) => cut.FindAll("[data-testid=pi-setup-source]").Single(r => r.GetAttribute("data-role") == key);

        Row("hub").QuerySelector("[data-testid=pi-setup-requirement]")!.TextContent.Should().Be("Required");
        Row("hub").QuerySelector("[data-testid=pi-setup-state]")!.TextContent.Should().Be("Bound");
        Row("hub").QuerySelector("[data-testid=pi-setup-binding]")!.TextContent.Should().Be("Bound to Demo – report intake");
        Row("hub").QuerySelector("[data-testid=pi-setup-packages]")!.TextContent.Should().Be("Tiltak 2 - hub (1).zip", "the archive name is shown compactly, as provenance");
        Row("hub").QuerySelector("[data-testid=pi-setup-journeys]")!.TextContent.Should().Be("Report intake");
        Row("hub").QuerySelector("[data-testid=pi-setup-action]")!.TextContent.Should().Be("Open source");
        Row("hub").QuerySelector("[data-testid=pi-setup-action]")!.GetAttribute("aria-label").Should().Be("Open Message hub source in Source Analysis");
        Row("hub").QuerySelector("[data-testid=pi-setup-secondary]")!.TextContent.Should().Be("View binding");

        Row("platform").QuerySelector("[data-testid=pi-setup-state]")!.TextContent.Should().Be("Available", "available is not bound; the binding is its own line");
        Row("platform").QuerySelector("[data-testid=pi-setup-binding]")!.TextContent.Should().Be("Binding not configured");
        Row("platform").QuerySelector("[data-testid=pi-setup-packages]")!.TextContent.Should().Be("platform.zip");
        Row("platform").QuerySelector("[data-testid=pi-setup-detail]")!.TextContent.Should().Be("Matching imported source found.");
        Row("platform").QuerySelector("[data-testid=pi-setup-secondary]")!.TextContent.Should().Be("Bind evidence");
        Row("platform").QuerySelector("[data-testid=pi-setup-secondary]")!.GetAttribute("href").Should().Be("/integration-quality-review?view=setup-evidence");
        Row("platform").QuerySelector("[data-testid=pi-setup-secondary]")!.GetAttribute("aria-label").Should().Be("Bind Platform evidence in IQR Evidence");

        Row("app").QuerySelector("[data-testid=pi-setup-state]")!.TextContent.Should().Be("Missing");
        Row("app").QuerySelector("[data-testid=pi-setup-detail]")!.TextContent.Should().Be("No matching imported source found.");
        Row("app").QuerySelector("[data-testid=pi-setup-requirement-text]")!.TextContent.Should().Be("Required for application metadata.");
        Row("app").QuerySelector("[data-testid=pi-setup-action]")!.TextContent.Should().Be("Import source");
        Row("app").QuerySelector("[data-testid=pi-setup-action]")!.GetAttribute("href").Should().Be("project-import?import=source");

        var infra = Row("infra");
        infra.GetAttribute("data-requirement").Should().Be("Optional");
        infra.QuerySelector("[data-testid=pi-setup-state]")!.TextContent.Should().Be("Not provided");
        infra.TextContent.Should().NotContain("Missing").And.NotContain("Required");
    }

    [Fact]
    public void SeveralMatchingSnapshots_AreListedForReview_AndTheBoundOneIsMarkedCanonical()
    {
        _setup = Setup(true,
        [
            new(Role("platform", "Platform"), TestPackSourceState.Bound)
            {
                Candidates = [Candidate(OldM2lbSnapshot, "platform (2).zip", OldM2lbSha, bound: true, daysAgo: 5), Candidate(M2lbSnapshot, "platform.zip", M2lbSha)],
                Detail = "bound", NextActionTarget = "iqr-evidence",
            },
        ]);
        var cut = RenderPage();
        var row = cut.Find("[data-testid=pi-setup-source]");
        row.QuerySelector("[data-testid=pi-setup-packages]")!.TextContent.Should().Be("platform (2).zip · +1 more", "the bound package leads; the others are counted");
        var candidates = row.QuerySelector("[data-testid=pi-setup-candidates]")!.TextContent;
        candidates.Should().Contain("platform (2).zip").And.Contain("Bound (canonical)").And.Contain("platform.zip").And.Contain("Not bound");
    }

    [Fact]
    public void ReviewRequired_ForUnboundMatches_SendsTheTesterToIqrEvidence()
    {
        _setup = Setup(true,
        [
            new(Role("platform", "Platform"), TestPackSourceState.ReviewRequired)
            {
                Candidates = [Candidate(M2lbSnapshot, "platform.zip", M2lbSha), Candidate(OldM2lbSnapshot, "platform (2).zip", OldM2lbSha, daysAgo: 5)],
                Detail = "Several stored snapshots match the Platform source.", NextAction = "Review", NextActionTarget = "iqr-evidence",
            },
        ]);
        var cut = RenderPage();
        cut.Find("[data-testid=pi-setup-state]").TextContent.Should().Be("Ambiguous", "several matching snapshots and none bound: the tester chooses, never the newest");
        cut.Find("[data-testid=pi-setup-packages]").TextContent.Should().Be("2 matching imported sources");
        cut.Find("[data-testid=pi-setup-action]").TextContent.Should().Be("Choose source");
        cut.Find("[data-testid=pi-setup-action]").GetAttribute("href").Should().Be("/integration-quality-review?view=setup-evidence");
    }

    [Fact]
    public void WithoutATargetEnvironment_BindingsAreNotRepeatedOnEveryCard_NorShownAsUnbound()
    {
        _profile = null;
        ImportHistory();
        _setup = _setup! with { BindingsRead = false, EnvironmentId = "" };
        var cut = RenderPage();
        _journeys.Verify(j => j.SourceSetupAsync(null, It.IsAny<CancellationToken>()), Times.AtLeastOnce());
        cut.FindAll("[data-testid=pi-current-bindings], [data-testid=pi-previous-bindings], [data-testid=pi-previous-binding-count]")
            .Should().BeEmpty("bindings belong to a target environment; without one they are neither unbound nor repeated per source");
        cut.Markup.Should().NotContain("Select a target environment to see IQR bindings");
        cut.FindAll("[data-testid=pi-attention-item][data-key=test-environment]").Should().ContainSingle("the environment is said once, under Needs attention");
    }

    [Fact]
    public void ASourceAnalysisUpload_WithoutAnImportRecord_IsHistoryToo()
    {
        _setup = Setup(true, null, Snap(OldM2lbSnapshot, "infra-only.zip", OldM2lbSha) with { InfrastructureFormats = ["Terraform"] });
        var cut = RenderPage();
        var row = cut.Find("[data-testid=pi-previous-row]");
        row.GetAttribute("data-origin").Should().Be("SourceAnalysis");
        row.TextContent.Should().Contain("Source Analysis upload").And.Contain("Terraform");
        row.QuerySelectorAll("[data-testid=pi-previous-use]").Should().BeEmpty("only a recorded Project Import has documents to make current");
    }

    [Fact]
    public void AnotherPack_RendersItsOwnSources_AndTheLocalFixtureNeverAppears()
    {
        ImportHistory();
        _setup = _setup! with
        {
            Packs = [.. _setup.Packs, new TestPackSourceSetup("demo.ledger", "Ledger", [new(Role("ledger", "Ledger"), TestPackSourceState.Missing) { NextActionTarget = "source-analysis" }])],
        };
        var cut = RenderPage();
        cut.FindAll("[data-testid=pi-source-setup]").Select(s => s.GetAttribute("data-pack")).Should().Equal("demo.pack", "demo.ledger");
        cut.Markup.Should().NotContain("skolenaervaer.json").And.NotContain("testdata");
    }

    [Fact]
    public void WithoutSourceSetup_ThePageStillWorks()
    {
        _setup = null;
        Import("platform.zip", M2lbSha, M2lbSnapshot, documents: true, DateTimeOffset.UtcNow);
        var cut = RenderPage();
        cut.FindAll("[data-testid=pi-source-setup]").Should().BeEmpty();
        cut.Find("[data-testid=pi-current-archive]").TextContent.Should().Be("platform.zip");
        cut.FindAll("[data-testid=pi-current-bindings]").Should().BeEmpty();
    }

    // ── Compact cards, progress, explanations, project-scoped artifacts ─────────────────────────────────────────────────────────────

    [Fact]
    public void ProgressLine_IsComputedFromTheActualStates()
    {
        ImportHistory();
        var cut = RenderPage();
        cut.Find("[data-testid=pi-setup-progress]").TextContent.Should()
            .Be("2 of 3 required sources available · 1 needs binding · 1 missing · optional Infrastructure as Code not provided");

        _setup = Setup(true, [new(Role("app", "Demo application"), TestPackSourceState.Missing), new(Role("hub", "Message hub"), TestPackSourceState.Missing)]);
        RenderPage().FindAll("[data-testid=pi-setup-progress]").Last().TextContent.Should().Be("2 required sources missing");
    }

    [Fact]
    public void Cards_AreCompact_WithTechnicalMetadataBehindNamedDetails()
    {
        ImportHistory();
        var cut = RenderPage();
        var platform = cut.FindAll("[data-testid=pi-setup-source]").Single(r => r.GetAttribute("data-role") == "platform");

        var details = platform.QuerySelector("[data-testid=pi-setup-details]")!;
        details.HasAttribute("open").Should().BeFalse();
        details.QuerySelector("summary .visually-hidden")!.TextContent.Should().Be("View Platform source details");
        details.TextContent.Should().Contain("Platform source archive").And.Contain("Platform capability", "expected source and Used for live in Details");
        var primary = platform.TextContent.Replace(details.TextContent, "");
        primary.Should().NotContain("Platform source archive").And.NotContain("Expected").And.NotContain(M2lbSha[..8], "the default card shows no expected text or fingerprint");
        cut.FindAll("[data-testid=pi-setup-details] summary .visually-hidden").Select(e => e.TextContent).Should().OnlyHaveUniqueItems("each Details control is named after its source");
    }

    [Fact]
    public void OptionalSource_IsVisuallySecondary_AndNeverMissing()
    {
        ImportHistory();
        var infra = RenderPage().FindAll("[data-testid=pi-setup-source]").Single(r => r.GetAttribute("data-role") == "infra");

        infra.ClassList.Should().Contain("is-optional");
        infra.QuerySelector("[data-testid=pi-setup-state]")!.ClassList.Should().Contain("pi-state-optional");
        infra.QuerySelector("[data-testid=pi-setup-action]")!.TextContent.Should().Be("Import optional source");
        infra.QuerySelector("[data-testid=pi-setup-action]")!.ClassList.Should().Contain("btn-secondary");
    }

    [Fact]
    public void AnAvailableOptionalIaCSource_ShowsItsArchive_AndWhyItMatches()
    {
        _setup = Setup(true,
        [
            new(Role("infra", "Infrastructure as Code", TestPackSourceRequirement.Optional), TestPackSourceState.Available)
            {
                Candidates = [Candidate(OldM2lbSnapshot, "terraform.zip", OldM2lbSha) with { Technologies = ["Terraform"], MatchedOn = ["Infrastructure as Code: Terraform"] }],
                Detail = "Infrastructure as Code is available in terraform.zip (Terraform). No binding is needed.", NextActionTarget = "source-analysis",
            },
        ]);
        var card = RenderPage().Find("[data-testid=pi-setup-source]");
        card.QuerySelector("[data-testid=pi-setup-state]")!.TextContent.Should().Be("Available");
        card.QuerySelector("[data-testid=pi-setup-packages]")!.TextContent.Should().Be("terraform.zip");
        card.QuerySelectorAll("[data-testid=pi-setup-binding]").Should().BeEmpty("no binding is needed for it");
        card.QuerySelector("[data-testid=pi-setup-action]")!.TextContent.Should().Be("Open source");
        card.QuerySelector("[data-testid=pi-setup-matched]")!.TextContent.Should().Contain("Infrastructure as Code: Terraform");
    }

    [Fact]
    public void MissingSource_SaysWhichNamedPackageWasCheckedAndWhy()
    {
        _setup = Setup(true,
        [
            new(Role("platform", "Platform"), TestPackSourceState.Missing)
            {
                NearMisses = [new TestPackSourceNearMiss(OldM2lbSnapshot, "platform.zip", "It contains none of the expected components or projects: Ledger, Billing.")],
                Detail = "Platform source is missing.",
            },
        ]);
        var card = RenderPage().Find("[data-testid=pi-setup-source]");
        card.QuerySelector("[data-testid=pi-setup-state]")!.TextContent.Should().Be("Missing");
        card.QuerySelector("[data-testid=pi-setup-detail]")!.TextContent.Should().Be("No matching imported source found. platform.zip was checked but does not match.");
        card.QuerySelector("[data-testid=pi-setup-nearmisses]")!.TextContent.Should().Contain("none of the expected components").And.Contain("Ledger");
        card.QuerySelectorAll("[data-testid=pi-setup-packages]").Should().BeEmpty("a non-matching package is never shown as the source");
    }

    [Fact]
    public void ProjectArtifacts_BelongToTheCurrentProject_AndSwitchWithIt()
    {
        ImportHistory();
        var cut = RenderPage();

        var artifacts = cut.Find("[data-testid=pi-current] [data-testid=pi-current-candidates]");
        artifacts.QuerySelector("h2")!.TextContent.Should().Be("Project contents");
        artifacts.GetAttribute("data-import").Should().Be(cut.Find("[data-testid=pi-current-candidates]").GetAttribute("data-import"));
        cut.Find("[data-testid=pi-current-archive]").TextContent.Should().Be("platform.zip", "the contents belong to the current project's archive");
        artifacts.GetAttribute("data-import").Should().Be(_activation.Current!.ImportId);
        cut.FindAll("[data-testid=pi-current-candidates]").Should().ContainSingle("there is no detached global artifact section");

        cut.FindAll("[data-testid=pi-previous-row]").Single(r => r.TextContent.Contains("Tiltak 2")).QuerySelector("[data-testid=pi-previous-use]")!.Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pi-current-name]").TextContent.Should().Be("Tiltak 2 - hub (1)"));
        cut.Find("[data-testid=pi-current-archive]").TextContent.Should().Be("Tiltak 2 - hub (1).zip");
        cut.Find("[data-testid=pi-current-no-artifacts]").TextContent.Should().StartWith("No supported project documents were found.", "the hub import has no documents; platform's documents do not leak");
        _journeys.Verify(j => j.SourceSetupAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.AtLeast(2), "source setup refreshes when the current project changes");
    }

    /// <summary>The real Terraform-iac-meldingsutveksler shape: imported, code analysis "Failed" (no projects), infrastructure evidence analyzed.</summary>
    [Fact]
    public void CurrentProjectCard_KeepsImportAnalysisRoleAndEnvironmentSeparate_ForAFailedCodeAnalysisWithInfrastructureEvidence()
    {
        _profile = null;
        var infra = Import("Terraform-iac-meldingsutveksler.zip", MuSha, MuSnapshot, documents: false, DateTimeOffset.UtcNow);
        _setup = Setup(false,
        [
            new(Role("app", "Demo application"), TestPackSourceState.Missing) { NearMisses = [new(MuSnapshot, "Terraform-iac-meldingsutveksler.zip", "It contains none of the expected components or projects: Demo.")] },
            new(Role("infra", "Infrastructure as Code", TestPackSourceRequirement.Optional) with { RequiresInfrastructureAsCode = true }, TestPackSourceState.Available)
                { Candidates = [Candidate(MuSnapshot, "Terraform-iac-meldingsutveksler.zip", MuSha) with { MatchedOn = ["Infrastructure as Code: Terraform"] }] },
        ],
        new SourceSetupSnapshot(MuSnapshot, "Terraform-iac-meldingsutveksler.zip", MuSha, DateTimeOffset.UtcNow, "Failed")
        {
            ImportId = infra.ImportId, InfrastructureFormats = ["Terraform"], ConfigurationEnvironments = ["QA", "Production"],
            Areas = [new("Application code", "NotDetected"), new("Architecture", "Unsupported"), new("Infrastructure as Code", "Complete"),
                new("Configuration", "Complete"), new("CI/CD", "Partial"), new("Contracts", "Partial")],
        });

        var cut = RenderPage();
        var card = cut.Find("[data-testid=pi-current-card]");

        card.QuerySelector("[data-testid=pi-current-success]")!.TextContent.Should().Contain("Project imported successfully", "a failed code analysis is never a failed import");
        card.QuerySelector("[data-testid=pi-current-source-status]")!.TextContent.Should().Be("Partial · no application code");
        card.QuerySelector("[data-testid=pi-current-source-areas]")!.TextContent.Should().Contain("Infrastructure as Code, Configuration complete").And.Contain("no supported application code");
        card.QuerySelector("[data-testid=pi-current-analysis-details]")!.GetAttribute("href").Should().Be($"source-analysis?snapshot={MuSnapshot:D}", "details reuse Source Analysis");
        card.QuerySelector("[data-testid=pi-current-role]")!.GetAttribute("data-identified").Should().Be("true");
        card.QuerySelector("[data-testid=pi-current-role-status]")!.TextContent.Should().Be("Infrastructure as Code");
        card.QuerySelector("[data-testid=pi-current-environment]")!.TextContent.Should().Contain("Not selected");
        card.TextContent.Should().NotContain(MuSnapshot.ToString()[..8], "no snapshot id or fingerprint in the summary").And.NotContain(MuSha[..12]);
        cut.Find("[data-testid=pi-contents-infrastructure]").TextContent.Should().Be("Terraform detected");
        cut.Find("[data-testid=pi-contents-contracts]").TextContent.Should().Be("Detected · partially analyzed");

        var groups = cut.FindAll("[data-testid=pi-attention-group]");
        groups.Select(g => g.GetAttribute("data-group")).Should().Equal("TestingSetup", "Environment");
        groups[0].TextContent.Should().Contain("Demo application source missing").And.Contain("Terraform-iac-meldingsutveksler.zip was checked but does not match");
        cut.Find("[data-testid=pi-attention-count]").TextContent.Should().Be("· 1 thing needs attention", "the environment is a note, not a problem");
        cut.FindAll("[data-testid=pi-current-open-integrations]").Should().ContainSingle("a testing setup exists");
    }

    [Fact]
    public void ASharedLibrary_IsNotIdentified_WithTheRecognizersReason_AndWithoutATestingSetupNoRoleIsShown()
    {
        var common = Import("M2LB.Common.zip", M2lbSha, M2lbSnapshot, documents: true, DateTimeOffset.UtcNow);
        _setup = Setup(true,
        [
            new(Role("platform", "Platform application"), TestPackSourceState.Missing)
                { NearMisses = [new(M2lbSnapshot, "M2LB.Common.zip", "It contains none of the expected components or projects: Platform.Api.")] },
        ], new SourceSetupSnapshot(M2lbSnapshot, "M2LB.Common.zip", M2lbSha, DateTimeOffset.UtcNow, "Partial") { ImportId = common.ImportId });

        var cut = RenderPage();

        cut.Find("[data-testid=pi-current-role]").GetAttribute("data-identified").Should().Be("false");
        cut.Find("[data-testid=pi-current-role-status]").TextContent.Should().Be("Not identified");
        cut.Find("[data-testid=pi-current-role-detail]").TextContent.Should().Contain("Checked as Platform application: It contains none of the expected components");
        cut.Find("[data-testid=pi-attention-item][data-key='setup-demo.pack-platform']").TextContent.Should().Contain("Platform application source missing");

        _setup = new SourceSetupView { Snapshots = _setup.Snapshots };
        var plain = RenderPage();
        plain.FindAll("[data-testid=pi-current-role]").Should().BeEmpty("a source role is only meaningful against a testing setup");
        plain.FindAll("[data-testid=pi-current-open-integrations], [data-testid=pi-current-open-setup]").Should().BeEmpty("no testing setup: no setup or integration action");
    }

    [Fact]
    public void CurrentProjectCard_ShowsPrimaryFacts_AndKeepsTechnicalDetailsCollapsed()
    {
        ImportHistory();
        var cut = RenderPage();
        var current = cut.Find("[data-testid=pi-current]");

        cut.Find("[data-testid=pi-current-technical]").HasAttribute("open").Should().BeFalse();
        cut.Find("[data-testid=pi-current-technical] [data-testid=pi-current-fingerprint]").Should().NotBeNull("the fingerprint is technical metadata");
        cut.Find("[data-testid=pi-current-technical] [data-testid=pi-current-logical]").TextContent.Should().Be("Platform");
        current.QuerySelector("[data-testid=pi-current-fingerprint]").Should().BeNull("the summary never leads with a fingerprint");
        current.QuerySelector("[data-testid=pi-current-summary]")!.TextContent.Should().NotContainAny("Snapshot", "snapshot", "fingerprint", ".zip");
        cut.FindAll("[data-testid=pi-previous-match]").Select(b => b.TextContent).Should().Contain("Matches the Message hub source requirement");
    }

    // ── Test Pack references: provided by the pack, never an imported source ────────────────────────────────────────────────────────

    private static TestPackSourceStatus Reference(bool available) =>
        new(Role("app", "Demo application/API", journeys: "intake") with { Provider = TestPackSourceProvider.TestPackReference }, available ? TestPackSourceState.ReferenceAvailable : TestPackSourceState.ReferenceUnavailable)
        {
            Reference = new TestPackReferenceView("OpenAPI", "Built-in OpenAPI reference")
            {
                Facts = available ? [new("App identifier", "demo/app") { SemanticKey = "Application.Route" }, new("Form data type", "demo_form_data_type_with_a_very_long_identifier_v2_0_0") { SemanticKey = "Application.FormDataType" }] : [],
                Provenance = available ? ["OpenAPI 3.0.4"] : [], Fingerprint = available ? MuSha : null,
                Limitation = "Reference data supports configuration and test planning. It is not canonical source evidence and does not verify the deployed application.",
                NotProven = available ? ["Client id, client secret, redirect URI or scopes"] : [],
                Diagnostics = available ? [] : ["The reference could not be loaded (InvalidDataException: not valid JSON)."],
            },
            Detail = available ? "Demo application/API is provided by the Test Pack reference." : "The Test Pack reference could not be loaded.",
        };

    private static IReadOnlyList<TestPackSourceStatus> WithReference(bool available) =>
    [
        new(Role("hub", "Message hub", journeys: "intake"), TestPackSourceState.Missing) { Detail = "Missing.", NextAction = "Import it in Source Analysis", NextActionTarget = "source-analysis" },
        new(Role("platform", "Platform", journeys: "intake"), TestPackSourceState.ReviewRequired)
        {
            Candidates = [Candidate(M2lbSnapshot, "platform.zip", M2lbSha), Candidate(OldM2lbSnapshot, "platform-old.zip", OldM2lbSha)], AvailableArchives = ["platform.zip", "platform-old.zip"],
            Detail = "Several snapshots match.", NextAction = "Bind one", NextActionTarget = "iqr-evidence",
        },
        Reference(available),
        new(Role("infra", "Infrastructure as Code", TestPackSourceRequirement.Optional), TestPackSourceState.Available)
        { Candidates = [Candidate(MuSnapshot, "terraform.zip", MuSha)], AvailableArchives = ["terraform.zip"], Detail = "Available.", NextAction = "Open Source Analysis", NextActionTarget = "source-analysis" },
    ];

    private static AngleSharp.Dom.IElement Card(IRenderedComponent<ProjectImportPage> cut, string role) =>
        cut.FindAll("[data-testid=pi-setup-source]").Single(r => r.GetAttribute("data-role") == role);

    [Fact]
    public void ABuiltInReference_IsATestPackReferenceCard_Available_WithoutAnImportAction()
    {
        _setup = Setup(true, WithReference(available: true));
        var cut = RenderPage();
        var card = Card(cut, "app");
        card.GetAttribute("data-provider").Should().Be("TestPackReference");
        card.QuerySelector("[data-testid=pi-setup-requirement]")!.TextContent.Should().Be("Test Pack reference");
        card.QuerySelector("[data-testid=pi-setup-state]")!.TextContent.Should().Be("Available");
        card.QuerySelector("[data-testid=pi-setup-state]")!.ClassList.Should().Contain("pi-state-reference").And.NotContain("pi-state-bound", "a reference is never shown as verified");
        card.QuerySelector("[data-testid=pi-setup-journeys]")!.TextContent.Should().Be("Report intake");
        card.QuerySelector("[data-testid=pi-setup-detail]")!.TextContent.Should().Be("Provides the metadata the Test Pack uses. No separate import required.");
        card.QuerySelectorAll("[data-testid=pi-setup-action], [data-testid=pi-setup-secondary], [data-testid=pi-setup-details]").Should().BeEmpty("nothing to import, bind or match");
        card.TextContent.Should().NotContain("Import").And.NotContain("Missing").And.NotContain("Required");

        var details = card.QuerySelector("[data-testid=pi-setup-reference]")!;
        details.HasAttribute("open").Should().BeFalse("the card stays compact");
        details.QuerySelector("summary")!.TextContent.Should().Contain("View reference details");
        details.QuerySelector("summary .visually-hidden")!.TextContent.Should().Be("View Demo application/API reference details");
        details.QuerySelector("[data-testid=pi-reference-kind]")!.TextContent.Should().Be("OpenAPI");
        details.QuerySelectorAll("[data-testid=pi-reference-fact]").Select(f => f.TextContent).Should().Equal("App identifierdemo/app", "Form data typedemo_form_data_type_with_a_very_long_identifier_v2_0_0");
        details.QuerySelector("[data-testid=pi-reference-limitation]")!.TextContent.Should().Contain("not canonical source evidence");
        details.QuerySelector("[data-testid=pi-reference-not-proven]")!.TextContent.Should().Contain("client secret");
        cut.Markup.Should().NotContain("skolenaervaer.json").And.NotContain("testdata");
    }

    [Fact]
    public void Counts_KeepImportedRequirementsReferencesAndOptionalSourcesApart_AndTheNoteExplainsBoth()
    {
        _setup = Setup(true, WithReference(available: true));
        var cut = RenderPage();
        cut.Find("[data-testid=pi-setup-progress]").TextContent.Should()
            .Be("0 of 2 required sources available · 1 ambiguous · 1 missing · 1 Test Pack reference available · optional Infrastructure as Code available");
        cut.Find("[data-testid=pi-setup-note]").TextContent.Should().Contain("imported source evidence and Test Pack reference metadata")
            .And.Contain("matched by their content").And.Contain("built into BirkNext and do not need to be uploaded");
        cut.FindAll("[data-testid=pi-setup-source]").Select(c => c.GetAttribute("data-role")).Should().Equal(["hub", "platform", "app", "infra"],
            "required imported sources first, then references, then optional sources");
    }

    [Fact]
    public void AnUnavailableReference_SaysSo_WithDiagnostics_NeverAsAMissingImport()
    {
        _setup = Setup(true, WithReference(available: false));
        var cut = RenderPage();
        var card = Card(cut, "app");
        card.QuerySelector("[data-testid=pi-setup-state]")!.TextContent.Should().Be("Unavailable");
        card.QuerySelector("[data-testid=pi-setup-requirement]")!.TextContent.Should().Be("Test Pack reference");
        card.QuerySelector("[data-testid=pi-setup-detail]")!.TextContent.Should().Be("Reference could not be loaded.");
        card.QuerySelector("[data-testid=pi-setup-reference] summary")!.TextContent.Should().Contain("View diagnostics");
        card.QuerySelector("[data-testid=pi-reference-diagnostics]")!.TextContent.Should().Contain("could not be loaded");
        card.QuerySelectorAll("[data-testid=pi-setup-action]").Should().BeEmpty("importing is not the recovery for a built-in reference");
        card.TextContent.Should().NotContain("Missing").And.NotContain("Import source");
        cut.Find("[data-testid=pi-setup-progress]").TextContent.Should().Contain("1 Test Pack reference unavailable");
    }
}
