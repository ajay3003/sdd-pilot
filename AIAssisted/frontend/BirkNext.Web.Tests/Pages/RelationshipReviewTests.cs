using BirkNext.Governance;
using BirkNext.Integrations;
using BirkNext.Interpretation;
using BirkNext.Snapshots;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using BirkNext.Web.Services.ProjectEvidence;
using BirkNext.Web.Services.ProjectImport;
using BirkNext.Web.Tests.Services;
using Bunit;
using Microsoft.AspNetCore.Components;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>Relationship Review (formerly Correlation Review): the application context, the real per-source analysis outcome, Project Evidence as
/// the default selection, and "Not analyzed" / "Governance not assessed" instead of zeros before any analysis.</summary>
public sealed class RelationshipReviewTests : BunitContext
{
    static IqrSourceSnapshotSummary Stored(Guid id, string file, SourceAnalysisStatus status, int minutes = 0) =>
        new() { Id = id, Archive = new(file, "sha", 10), Status = status, AnalyzedAt = DateTimeOffset.UnixEpoch.AddMinutes(minutes) };

    static SourceSetupSnapshot Setup(Guid id, params (string Area, string Status)[] areas) =>
        new(id, "x.zip", "fp", DateTimeOffset.UnixEpoch, "Ready") { Areas = [.. areas.Select(a => new SourceSetupArea(a.Area, a.Status))] };

    static PackageAssociation Association(Guid snapshot, EvidenceAssociationState state) => new(
        new ProjectEvidencePackage($"import-{snapshot}", "pkg", "pkg.zip", "sha", DateTimeOffset.UnixEpoch, snapshot, [], new Dictionary<WorkspaceArtifactType, int>(), 0, [], [], [], null),
        state, PackageEvidenceRole.Infrastructure, "");

    [Fact]
    public void Outcome_comes_from_the_areas_the_snapshot_contains_never_from_its_existence()
    {
        Guid terraform = Guid.NewGuid(), app = Guid.NewGuid(), broken = Guid.NewGuid(), old = Guid.NewGuid();
        var setup = new SourceSetupView
        {
            Snapshots =
            [
                // Infrastructure-only repository: no application code is not a failure.
                Setup(terraform, ("Application code", "NotDetected"), ("Infrastructure as Code", "Complete")),
                // The code analyzer says Ready, but architecture is partial: the snapshot is partially analyzed, not "analyzed".
                Setup(app, ("Application code", "Complete"), ("Architecture", "Partial")),
                Setup(broken, ("Application code", "Failed"), ("Configuration", "Failed")),
            ],
        };
        var rows = RelationshipReviewSources.Of(
            [Stored(terraform, "tf.zip", SourceAnalysisStatus.Failed, 1), Stored(app, "app.zip", SourceAnalysisStatus.Ready, 2), Stored(broken, "b.zip", SourceAnalysisStatus.Ready, 3),
             Stored(old, "old.zip", SourceAnalysisStatus.Partial, 4)], setup, null);

        rows.Single(r => r.Id == terraform).Outcome.Should().Be(SnapshotProcessingStatus.Completed);
        rows.Single(r => r.Id == app).OutcomeLabel.Should().Be("Partially analyzed");
        rows.Single(r => r.Id == app).Detail.Should().Be("Architecture: Partial");
        rows.Single(r => r.Id == broken).OutcomeLabel.Should().Be("Analysis failed");
        rows.Single(r => r.Id == old).Outcome.Should().Be(SnapshotProcessingStatus.Partial, "without per-area results the code analyzer's status is used");
        rows.Select(r => r.Id).Should().Equal([old, broken, app, terraform], "newest first");
        RelationshipReviewSources.DefaultSelection(rows).Should().BeEquivalentTo([terraform, app, old], "a failed analysis is never selected by default");
    }

    [Fact]
    public void Default_selection_is_the_sources_Project_Evidence_associates()
    {
        Guid mine = Guid.NewGuid(), review = Guid.NewGuid(), other = Guid.NewGuid();
        var evidence = new ProjectEvidenceView(null, null, null, null, [], [], [], [])
        {
            Associations = [Association(mine, EvidenceAssociationState.Associated), Association(review, EvidenceAssociationState.NeedsReview), Association(other, EvidenceAssociationState.NotAssociated)],
        };
        var rows = RelationshipReviewSources.Of(
            [Stored(mine, "m.zip", SourceAnalysisStatus.Ready), Stored(review, "r.zip", SourceAnalysisStatus.Ready), Stored(other, "o.zip", SourceAnalysisStatus.Ready)], null, evidence);

        rows.Where(r => r.Associated).Select(r => r.Id).Should().Equal([mine]);
        RelationshipReviewSources.DefaultSelection(rows).Should().Equal([mine], "needs-review and unrelated packages are never analyzed by default");
    }

    [Fact]
    public async Task Before_any_analysis_counts_read_Not_analyzed_and_governance_is_not_assessed()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var storage = new ActiveTargetEnvironmentTests.Storage();
        var settings = new FrontendAnalysisSettingsService();
        await settings.LoadAsync(storage);
        settings.Settings.ActiveProfileId = "dev";
        settings.Settings.Profiles.Single(p => p.Id == "dev").TargetApplication = new TargetApplicationIdentity { Name = "M2LB" };
        Services.AddSingleton<IJSRuntime>(storage);
        Services.AddSingleton<IFrontendAnalysisSettingsService>(settings);
        Services.AddSingleton<IFrontendAnalysisContextFactory>(ActiveTargetEnvironmentTests.Factory(settings, storage));
        Guid ready = Guid.NewGuid(), failed = Guid.NewGuid();
        var catalog = new Mock<IIntegrationCatalogApiService>();
        catalog.Setup(c => c.ListSourceSnapshotSummariesAsync("dev", It.IsAny<CancellationToken>()))
            .ReturnsAsync([Stored(ready, "M2LB.zip", SourceAnalysisStatus.Ready), Stored(failed, "Broken.zip", SourceAnalysisStatus.Ready)]);
        Services.AddSingleton(catalog.Object);
        var journeys = new Mock<IIntegrationJourneysApiService>();
        journeys.Setup(j => j.SourceSetupAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync(new ActiveEventApiResult<SourceSetupView>(new SourceSetupView
        {
            Snapshots = [Setup(ready, ("Application code", "Complete")), Setup(failed, ("Application code", "Failed"))],
        }, null));
        Services.AddSingleton(journeys.Object);
        var api = new Mock<ICanonicalInterpretationApiService>();
        api.Setup(a => a.ReviewItemsAsync("dev", ReviewItemKind.Decision, null, 0, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new ReviewQueueView(0, 0, new Dictionary<ReviewItemType, int>(), new Dictionary<ReviewItemType, int>(), new CanonicalPage<ReviewItemView>([], 0, 0, 20), null), (string?)null));
        api.Setup(a => a.SnapshotsAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync(([], (string?)null));
        api.Setup(a => a.DecisionsAsync("dev", null, It.IsAny<CancellationToken>())).ReturnsAsync(([], (string?)null));
        Services.AddSingleton(api.Object);

        var cut = Render<CorrelationReview>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=cr-not-analyzed]").TextContent.Should().Contain("Not analyzed").And.Contain("M2LB"));
        cut.Find("[data-testid=cr-not-analyzed]").TextContent.Should().NotContain("0", "no count is shown as zero before an analysis");
        cut.Find("[data-testid=cr-application]").TextContent.Should().Be("M2LB", "the context is the Target Application, not the environment name");
        cut.Find("[data-testid=cr-intro]").TextContent.Should().Contain("Your decisions are shared across");
        cut.WaitForAssertion(() => cut.Find("[data-testid=cr-source-outcomes]").TextContent.Should().Contain("1 analyzed").And.Contain("1 analysis failed"));
        cut.Find("[data-testid=cr-source-summary]").TextContent.Should().StartWith("1 of 2 sources selected");
        cut.Find("[data-testid=cr-customize]").Click();
        cut.FindAll("[data-testid=cr-source-status]").Select(e => e.TextContent).Should().BeEquivalentTo(["Analyzed", "Analysis failed"]);

        cut.Find("[data-testid=cr-tab-review]").Click();
        cut.Find("[data-testid=cr-queue-empty]").TextContent.Should().StartWith("Not analyzed");
        cut.Find("[data-testid=cr-tab-relationships]").Click();
        cut.FindAll("[data-testid=cr-explore-not-analyzed]").Should().ContainSingle();
        cut.Find("[data-testid=cr-tab-history]").Click();
        cut.Find("[data-testid=cr-governance-not-assessed]").TextContent.Should()
            .Contain("Governance not assessed — Analyze project relationships before evaluating governance rules.");
        api.Verify(a => a.GovernanceAsync(It.IsAny<string>(), It.IsAny<GateAction>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()), Times.Never(),
            "governance is not evaluated without an analysis");
    }

    [Fact]
    public async Task Analyze_relationships_sends_the_selected_sources_with_the_new_trigger_name()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var storage = new ActiveTargetEnvironmentTests.Storage();
        var settings = new FrontendAnalysisSettingsService();
        await settings.LoadAsync(storage);
        settings.Settings.ActiveProfileId = "dev";
        Services.AddSingleton<IJSRuntime>(storage);
        Services.AddSingleton<IFrontendAnalysisSettingsService>(settings);
        Services.AddSingleton<IFrontendAnalysisContextFactory>(ActiveTargetEnvironmentTests.Factory(settings, storage));
        var id = Guid.NewGuid();
        var catalog = new Mock<IIntegrationCatalogApiService>();
        catalog.Setup(c => c.ListSourceSnapshotSummariesAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync([Stored(id, "M2LB.zip", SourceAnalysisStatus.Ready)]);
        Services.AddSingleton(catalog.Object);
        var api = new Mock<ICanonicalInterpretationApiService>();
        api.Setup(a => a.SnapshotsAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync(([], (string?)null));
        api.Setup(a => a.DecisionsAsync("dev", null, It.IsAny<CancellationToken>())).ReturnsAsync(([], (string?)null));
        Services.AddSingleton(api.Object);

        var cut = Render<CorrelationReview>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=cr-interpret-run]").HasAttribute("disabled").Should().BeFalse());
        cut.Find("[data-testid=cr-interpret-run]").Click();

        cut.WaitForAssertion(() => api.Verify(a => a.InterpretAsync("dev", It.Is<InterpretRequest>(r => r.SourceSnapshotIds.SequenceEqual(new[] { id }) && r.Trigger == "Relationship Review"),
            It.IsAny<CancellationToken>()), Times.Once()));
    }

    [Fact]
    public async Task A_tab_opens_from_the_query_string()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var storage = new ActiveTargetEnvironmentTests.Storage();
        var settings = new FrontendAnalysisSettingsService();
        await settings.LoadAsync(storage);
        settings.Settings.ActiveProfileId = "dev";
        Services.AddSingleton<IJSRuntime>(storage);
        Services.AddSingleton<IFrontendAnalysisSettingsService>(settings);
        Services.AddSingleton<IFrontendAnalysisContextFactory>(ActiveTargetEnvironmentTests.Factory(settings, storage));
        Services.AddSingleton(Mock.Of<IIntegrationCatalogApiService>());
        var api = new Mock<ICanonicalInterpretationApiService>();
        api.Setup(a => a.SnapshotsAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync(([], (string?)null));
        api.Setup(a => a.DecisionsAsync("dev", null, It.IsAny<CancellationToken>())).ReturnsAsync(([], (string?)null));
        Services.AddSingleton(api.Object);

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(nav.GetUriWithQueryParameter("tab", "history"));
        var cut = Render<CorrelationReview>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=cr-tab-history]").GetAttribute("aria-selected").Should().Be("true"));
        cut.FindAll("[data-testid=cr-history]").Should().ContainSingle();
    }
}
