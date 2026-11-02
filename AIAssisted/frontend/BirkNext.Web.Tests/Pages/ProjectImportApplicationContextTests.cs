using BirkNext.Integrations;
using BirkNext.ProjectActivity;
using BirkNext.ProjectImport;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.ProjectActivity;
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
/// The user's scenario: M2LB.Common and M2LB are two evidence packages of ONE logical application, M2LB. Whatever the import order, M2LB is
/// the primary application source, M2LB.Common supporting/shared source, the application is named M2LB, its documents are M2LB's, the
/// application-wide setup finds the M2LB source, and each import records one persistent activity event under the application's stable id.
/// The backend's source setup is simulated from what the real archives produce (M2LB: 16 service projects; M2LB.Common: library only).
/// </summary>
public sealed class ProjectImportApplicationContextTests : BunitContext
{
    private readonly Mock<IProjectImportApiService> _api = new();
    private readonly Mock<IIntegrationJourneysApiService> _journeys = new();
    private readonly WorkspaceArtifactRepository _repository = new();
    private readonly FakeActivityApi _activity = new();
    private readonly Queue<ProjectImportPreview> _previews = new();
    private readonly Dictionary<Guid, (ProjectImportCommitResult? Result, SourceUploadFailure? Failure)> _commits = [];
    /// <summary>The snapshots the simulated backend holds: import id → (snapshot, service projects, shared projects).</summary>
    private readonly List<(string ImportId, Guid Snapshot, string Archive, int Services, int Shared)> _stored = [];

    private sealed class FakeActivityApi : IProjectActivityApiService
    {
        public List<ProjectActivityRecordRequest> Recorded { get; } = [];
        public bool Fail { get; set; }
        public Task<IReadOnlyList<ProjectActivityItem>?> ListAsync(string projectContextId, int take = 10, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ProjectActivityItem>?>([.. Recorded.Where(r => r.ProjectContextId == projectContextId).Reverse().Take(take)
                .Select(r => new ProjectActivityItem { ProjectContextId = r.ProjectContextId, Title = r.Title, Type = r.Type, Status = r.Status, Summary = r.Summary })]);
        public Task<ProjectActivityItem?> RecordAsync(ProjectActivityRecordRequest request, CancellationToken ct = default)
        {
            if (Fail) throw new HttpRequestException("backend down");
            if (!Recorded.Any(r => r.ProjectContextId == request.ProjectContextId && r.IdempotencyKey == request.IdempotencyKey)) Recorded.Add(request);
            return Task.FromResult<ProjectActivityItem?>(new ProjectActivityItem { ProjectContextId = request.ProjectContextId, Title = request.Title });
        }
    }

    public ProjectImportApplicationContextTests()
    {
        _api.Setup(a => a.PreviewAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => (_previews.Dequeue(), null));
        _api.Setup(a => a.CommitWithProgressAsync(It.IsAny<Guid>(), It.IsAny<IProgress<ProjectImportCommitEvent>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid staging, IProgress<ProjectImportCommitEvent>? _, CancellationToken _) => _commits[staging]);
        _journeys.Setup(j => j.SourceSetupAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => new ActiveEventApiResult<SourceSetupView>(Setup(), null));
        var autoSave = new Mock<IWorkspaceAutoSaveService>();
        autoSave.Setup(a => a.SaveNowAsync()).ReturnsAsync(true);
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext { ActiveProfile = new FrontendAnalysisProfile { Id = "dev", Name = "Dev" } });
        Services.AddSingleton(_api.Object);
        Services.AddSingleton(_journeys.Object);
        Services.AddSingleton(_repository);
        Services.AddSingleton<IWorkspaceSessionService>(_repository);
        Services.AddSingleton(new ProjectImportActivation(_repository));
        Services.AddSingleton(context.Object);
        Services.AddSingleton(autoSave.Object);
        Services.AddSingleton(Mock.Of<ITechnologyCoverageApiService>());
        Services.AddSingleton(WorkspaceSnapshots.Projection(CurrentWorkspaceSnapshot.None()).Object);
        Services.AddSingleton(Mock.Of<IFrontendAnalysisSettingsService>());
        Services.AddSingleton<IProjectActivityApiService>(_activity);
        Services.AddScoped<ProjectActivityRecorder>();
        Services.AddScoped<ProjectApplicabilityState>();
        Services.AddScoped<BirkNext.Web.Services.Operations.BackgroundOperationService>(_ => new BirkNext.Web.Services.Operations.BackgroundOperationService());
        Services.AddScoped<ProjectImportSession>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static TestPackSourceRole Role(string key, string label) => new(key, label, "") { ExpectedArtifact = $"{label} repository source archive" };

    /// <summary>What the backend reports for the stored snapshots: M2LB recognized by content once its snapshot exists; MU never (no MU imported).</summary>
    private SourceSetupView Setup()
    {
        var m2lb = _stored.FirstOrDefault(s => s.Services > 0);
        var common = _stored.FirstOrDefault(s => s.Services == 0);
        var m2lbStatus = m2lb.ImportId is null
            ? new TestPackSourceStatus(Role("m2lb", "M2LB application"), TestPackSourceState.Missing)
                { NearMisses = common.ImportId is null ? [] : [new(common.Snapshot, common.Archive, "It contains none of the expected components or projects.")] }
            : new TestPackSourceStatus(Role("m2lb", "M2LB application"), TestPackSourceState.AvailableNotBound)
                { Candidates = [new(m2lb.Snapshot, m2lb.Archive, "m", DateTimeOffset.UtcNow) { ImportId = m2lb.ImportId, MatchedOn = ["Project: M2LB.SkoleService.Api"] }] };
        return new SourceSetupView
        {
            Snapshots = [.. _stored.Select(s => new SourceSetupSnapshot(s.Snapshot, s.Archive, "f", DateTimeOffset.UtcNow, "Partial")
                { ImportId = s.ImportId, Areas = [new("Application code", "Complete")], ServiceProjectCount = s.Services, SharedProjectCount = s.Shared })],
            Packs = [new TestPackSourceSetup("skolenaervaer", "Skolenærvær Testing",
            [
                new(Role("mu", "MU (Meldingsutveksleren)"), TestPackSourceState.Missing),
                m2lbStatus,
            ])],
        };
    }

    private static readonly Guid M2lbSnapshot = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private static readonly Guid CommonSnapshot = Guid.Parse("22222222-0000-0000-0000-000000000002");

    private ProjectImportPreview Queue(string name, char sha, Guid snapshot, int services, int shared, int documents, SourceUploadFailure? failure = null)
    {
        var docs = Enumerable.Range(1, documents).Select(i => ($"specs/00{i}-f/spec.md", ProjectImportActivationTests.Spec)).ToArray();
        var preview = ProjectImportActivationTests.Preview(new string(sha, 64), true, docs) with
            { Archive = new ProjectImportArchive($"{name}.zip", new string(sha, 64), 4096, 12), ProjectName = name };
        _previews.Enqueue(preview);
        _commits[preview.StagingId] = failure is null ? (ProjectImportActivationTests.Commit(preview, ProjectImportSourceState.Created, snapshot), null) : (null, failure);
        if (failure is null) _stored.Add((preview.ImportId, snapshot, $"{name}.zip", services, shared));
        return preview;
    }

    private void QueueM2lb() => Queue("M2LB", 'b', M2lbSnapshot, services: 16, shared: 23, documents: 2);
    private void QueueCommon() => Queue("M2LB.Common", 'c', CommonSnapshot, services: 0, shared: 1, documents: 1);

    /// <summary>Imports the queued archives one after another, the way a tester does (Import more evidence after the first).</summary>
    private IRenderedComponent<ProjectImportPage> ImportQueued(int count, IRenderedComponent<ProjectImportPage>? cut = null, string anotherTestId = "pi-current-import-another")
    {
        cut ??= Render<ProjectImportPage>();
        for (var i = 0; i < count; i++)
        {
            if (cut.FindAll("[data-testid=pi-result]").Count > 0) cut.Find("[data-testid=pi-back]").Click();
            if (cut.FindAll($"[data-testid={anotherTestId}]").Count > 0) cut.Find($"[data-testid={anotherTestId}]").Click();
            cut.FindComponents<InputFile>().Single(f => f.Instance.AdditionalAttributes?["data-testid"]?.ToString() == "pi-choose-file")
                .UploadFiles(InputFileContent.CreateFromBinary([1, 2, 3], "x.zip"));
            cut.WaitForAssertion(() => cut.FindAll("[data-testid=pi-preview]").Should().NotBeEmpty());
            cut.Find("[data-testid=pi-import]").Click();
            cut.WaitForAssertion(() => cut.FindAll("[data-testid=pi-result], [data-testid=pi-commit-failure]").Should().NotBeEmpty());
        }
        return cut;
    }

    private (SddApplicationContext App, SddProjectImportRecord M2lb, SddProjectImportRecord Common) Final()
    {
        var lifecycle = _repository.SddLifecycle;
        lifecycle.Applications.Should().ContainSingle("M2LB and M2LB.Common are evidence of one logical application");
        var app = lifecycle.Applications.Single();
        var m2lb = lifecycle.ProjectImports.Single(i => i.ProjectName == "M2LB");
        var common = lifecycle.ProjectImports.Single(i => i.ProjectName == "M2LB.Common");
        return (app, m2lb, common);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CommonAndM2lb_InEitherOrder_AreOneApplicationM2lb_WithM2lbPrimary_AndCommonSupporting(bool commonFirst)
    {
        if (commonFirst) { QueueCommon(); QueueM2lb(); } else { QueueM2lb(); QueueCommon(); }
        var cut = ImportQueued(2);
        cut.Find("[data-testid=pi-back]").Click();

        var (app, m2lb, common) = Final();
        app.Name.Should().Be("M2LB");
        app.NameBasis.Should().Be(ApplicationContexts.PrimarySourceBasis);
        m2lb.ApplicationId.Should().Be(app.Id);
        common.ApplicationId.Should().Be(app.Id, "importing M2LB never drops M2LB.Common, and M2LB.Common never replaces M2LB");
        _repository.SddLifecycle.CurrentApplicationId.Should().Be(app.Id);
        _repository.SddLifecycle.CurrentProjectImportId.Should().Be(m2lb.ImportId, "the application's documents are its primary source's, whatever was imported last");

        cut.Find("[data-testid=pi-application-name]").TextContent.Should().Be("M2LB");
        cut.Find("[data-testid=pi-application-count]").TextContent.Should().Contain("2");
        var roles = cut.FindAll("[data-testid=pi-application-package]").ToDictionary(li => li.GetAttribute("data-import")!, li => li.GetAttribute("data-role"));
        roles[m2lb.ImportId].Should().Be(nameof(PackageEvidenceRole.PrimaryApplicationSource));
        roles[common.ImportId].Should().Be(nameof(PackageEvidenceRole.SupportingSource));
        cut.Find("[data-testid=pi-current-name]").TextContent.Should().Be("M2LB");
        cut.FindAll("[data-testid=pi-current-heading], .pi-current-eyebrow").Select(e => e.TextContent).Should().NotContain("Current project");
        // Application-wide readiness: M2LB found from M2LB.zip; MU stays missing (not imported).
        cut.Find("[data-testid=pi-application-readiness-row][data-role=m2lb] dd").TextContent.Should().Be("Found");
        cut.Find("[data-testid=pi-application-readiness-row][data-role=mu] dd").TextContent.Should().Be("Not found");
        cut.FindAll("[data-testid=pi-attention-item]").Select(i => i.TextContent).Should().NotContain(t => t.Contains("M2LB application source missing"));
        cut.FindAll("[data-testid=pi-attention-item]").Select(i => i.TextContent).Should().Contain(t => t.Contains("MU (Meldingsutveksleren) source missing"));

        // Two import events under the application's stable id (never a name or archive), each referencing its own package.
        _activity.Recorded.Should().HaveCount(2).And.OnlyContain(r => r.ProjectContextId == app.Id && r.Type == ProjectActivityType.ImportCompleted);
        _activity.Recorded.Select(r => r.Title).Should().BeEquivalentTo(["Imported M2LB", "Imported M2LB.Common"]);
        _activity.Recorded.Single(r => r.PackageId == common.ImportId).Summary.Should().StartWith("Supporting/shared source");
        _activity.Recorded.Single(r => r.PackageId == m2lb.ImportId).Summary.Should().StartWith("Primary application source");
        _activity.Recorded.Should().OnlyContain(r => r.Status == ProjectActivityStatus.Imported || r.Status == ProjectActivityStatus.Attention,
            "an import is never Completed/passed/reviewed");
    }

    [Fact]
    public void ImportOrder_DoesNotChangeTheFinalEvidenceModel()
    {
        string Model(bool commonFirst)
        {
            using var other = new ProjectImportApplicationContextTests();
            if (commonFirst) { other.QueueCommon(); other.QueueM2lb(); } else { other.QueueM2lb(); other.QueueCommon(); }
            other.ImportQueued(2);
            var (app, m2lb, common) = other.Final();
            var packages = ApplicationContexts.Packages(other._repository.SddLifecycle, app.Id, other.Setup());
            return $"{app.Name}|{app.NameBasis}|{other._repository.SddLifecycle.CurrentProjectImportId == m2lb.ImportId}|"
                + string.Join(",", packages.OrderBy(p => p.DisplayName).Select(p => $"{p.DisplayName}={p.Role}"));
        }
        Model(true).Should().Be(Model(false)).And.Be("M2LB|PrimarySource|True|M2LB=PrimaryApplicationSource,M2LB.Common=SupportingSource");
    }

    [Fact]
    public void OnlyCommon_IsSupportingSourceOfM2lb_AndTheMissingM2lbSourceIsLegitimate()
    {
        QueueCommon();
        var cut = ImportQueued(1);
        cut.Find("[data-testid=pi-result-role]").TextContent.Should().Be("Supporting/shared source");
        cut.Find("[data-testid=pi-result-application]").TextContent.Should().Be("M2LB");
        cut.Find("[data-testid=pi-result]").TextContent.Should().Contain("Import means BirkNext found and recorded the contents").And.Contain("nothing has been reviewed or tested yet");
        cut.Find("[data-testid=pi-back]").Click();

        var app = _repository.SddLifecycle.Applications.Single();
        app.Name.Should().Be("M2LB");
        app.NameBasis.Should().Be(ApplicationContexts.SharedSourceBasis);
        cut.Find("[data-testid=pi-current-role-note]").TextContent.Should().Contain("This package is not the primary M2LB application source");
        cut.FindAll("[data-testid=pi-attention-item]").Select(i => i.TextContent).Should().Contain(t => t.Contains("M2LB application source missing"));

        // Adding M2LB resolves it; M2LB.Common stays.
        QueueM2lb();
        ImportQueued(1, cut);
        cut.Find("[data-testid=pi-back]").Click();
        cut.FindAll("[data-testid=pi-attention-item]").Select(i => i.TextContent).Should().NotContain(t => t.Contains("M2LB application source missing"));
        cut.FindAll("[data-testid=pi-application-package]").Should().HaveCount(2);
    }

    [Fact]
    public void ImportAnotherApplication_StartsASeparateApplication_WhoseActivityNeverMixesWithM2lb()
    {
        QueueM2lb();
        var cut = ImportQueued(1);
        cut.Find("[data-testid=pi-back]").Click();
        Queue("Orders", 'd', Guid.NewGuid(), services: 2, shared: 0, documents: 1);
        ImportQueued(1, cut, "pi-current-import-application");

        var lifecycle = _repository.SddLifecycle;
        lifecycle.Applications.Should().HaveCount(2);
        var orders = lifecycle.ProjectImports.Single(i => i.ProjectName == "Orders");
        var m2lb = lifecycle.ProjectImports.Single(i => i.ProjectName == "M2LB");
        orders.ApplicationId.Should().NotBe(m2lb.ApplicationId);
        lifecycle.CurrentApplicationId.Should().Be(orders.ApplicationId);
        _activity.Recorded.Single(r => r.Title == "Imported Orders").ProjectContextId.Should().Be(orders.ApplicationId);
        _activity.Recorded.Single(r => r.Title == "Imported M2LB").ProjectContextId.Should().Be(m2lb.ApplicationId);
    }

    [Fact]
    public void AFailedImportIntoAnExistingApplication_IsOneFailedEvent_AndActivityOutageNeverFailsAnImport()
    {
        QueueM2lb();
        var cut = ImportQueued(1);
        cut.Find("[data-testid=pi-back]").Click();
        Queue("M2LB.Common", 'c', CommonSnapshot, 0, 1, 1, new SourceUploadFailure("IMPORT_FAILED", "commit", "The import stopped before a result was returned."));
        ImportQueued(1, cut);
        var failed = _activity.Recorded.Single(r => r.Type == ProjectActivityType.ImportFailed);
        failed.Status.Should().Be(ProjectActivityStatus.Failed);
        failed.ProjectContextId.Should().Be(_repository.SddLifecycle.Applications.Single().Id);

        _activity.Fail = true;
        QueueCommon();
        cut.Find("[data-testid=pi-cancel]").Click();
        ImportQueued(1, cut);
        cut.Find("[data-testid=pi-result]");
        _repository.SddLifecycle.ProjectImports.Should().Contain(i => i.ProjectName == "M2LB.Common", "the import completed although its activity could not be recorded");
    }

    [Fact]
    public void ClearCurrentImport_OnlyStopsShowingThePackage_TheApplicationAndItsEvidenceStay()
    {
        QueueCommon(); QueueM2lb();
        var cut = ImportQueued(2);
        cut.Find("[data-testid=pi-back]").Click();
        var app = _repository.SddLifecycle.CurrentApplicationId;
        cut.Find("[data-testid=pi-close]").Click();
        _repository.SddLifecycle.CurrentProjectImportId.Should().BeNull();
        _repository.SddLifecycle.CurrentApplicationId.Should().Be(app);
        _repository.SddLifecycle.ProjectImports.Should().HaveCount(2).And.OnlyContain(i => i.ApplicationId == app);
    }
}
