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
        cut.Find("[data-testid=cr-intro]").TextContent.Should().Be("Discover, validate and govern relationships across project evidence.");
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

    // ── NEXT48: governance dashboard + identity-conflict workflow (the M2LB Utdanning ↔ SkoleAdapter case) ──────────────────────────

    static CanonicalEntityView System(string id, string name) =>
        new(Guid.NewGuid(), CanonicalEntityType.System, id, name, name, "declared alias", null, new Dictionary<string, string>(), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    async Task<Mock<ICanonicalInterpretationApiService>> AnalyzedM2lb()
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
        api.Setup(a => a.SummaryAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync((new CanonicalSummaryView("dev", Guid.NewGuid(), DateTimeOffset.UnixEpoch, ["correlation/4"],
            new Dictionary<string, int> { ["Test"] = 1822, ["Requirement"] = 54 }, new Dictionary<string, int> { ["Verifies"] = 1382, ["Contains"] = 1862, ["CandidateSameAs"] = 1 },
            1881, 1361, 1362, 1, new(0, 0, 0, 0, 0), [], [], Decisions: 1, Suggestions: 1361) { NotCounted = 3 }, (string?)null));
        var conflict = new CanonicalRelationView(Guid.NewGuid(), "rel-1", System("utdanning", "Utdanning"), System("skoleadapter", "SkoleAdapter"), CanonicalRelationType.CandidateSameAs,
            RelationResolution.Ambiguous, RelationResolution.Ambiguous, CorrelationConfidence.Low, CorrelationRules.AliasConflictRule, "correlation/4", [], "fp",
            "Alias declared for two entities", RelationReviewState.NotReviewed, null, true);
        var item = new ReviewItemView("item-1", ReviewItemKind.Decision, ReviewItemType.IdentityConflict, 1, "\"M2LB.SkoleAdapter.Worker\" is declared as an alias of more than one entity.",
            ReviewTargetKind.Relation, "rel-1", conflict, null, null, ["Integration Topology"], CorrelationRules.AliasConflictRule, "correlation/4", "Ambiguous", null, DateTimeOffset.UnixEpoch, null);
        api.Setup(a => a.ReviewItemsAsync("dev", It.IsAny<ReviewItemKind>(), It.IsAny<ReviewItemType?>(), 0, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new ReviewQueueView(1, 1361, new Dictionary<ReviewItemType, int> { [ReviewItemType.IdentityConflict] = 1 }, new Dictionary<ReviewItemType, int> { [ReviewItemType.TestLink] = 1361 },
                new CanonicalPage<ReviewItemView>([item], 1, 0, 20), null), (string?)null));
        api.Setup(a => a.DecideAsync("dev", It.IsAny<ReviewDecisionRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(((ReviewDecisionView?)null, (string?)null));
        Services.AddSingleton(api.Object);
        return api;
    }

    [Fact]
    public async Task The_overview_reconciles_relationship_counts_and_leads_to_the_identity_conflict()
    {
        await AnalyzedM2lb();
        var cut = Render<CorrelationReview>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=cr-sum-conflicts]").TextContent.Should().Be("1", "the conflict governance blocks on is never 0 here"));
        cut.Find("[data-testid=cr-sum-entities]").TextContent.Should().Be("1876");
        cut.Find("[data-testid=cr-accounting]").TextContent.Should().Contain("3245 relationships = 1881 established + 1361 candidates + 3 not counted")
            .And.Contain("Identity conflicts are part of the decisions");
        cut.Find("[data-testid=cr-attention]");
        cut.Find("[data-testid=cr-overview-review]").TextContent.Trim().Should().Be("Resolve 1 identity conflict");
        cut.Find("[data-testid=cr-overview-review]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=cr-tab-review]").GetAttribute("aria-selected").Should().Be("true"));
        cut.Find("[data-testid=cr-kind-decisions]").TextContent.Should().StartWith("Required decisions");
        cut.Find("[data-testid=cr-kind-suggestions]").TextContent.Should().StartWith("Optional suggestions");
    }

    [Fact]
    public async Task An_identity_conflict_shows_both_entities_and_needs_a_rationale_to_confirm_or_reject()
    {
        var api = await AnalyzedM2lb();
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(nav.GetUriWithQueryParameters(new Dictionary<string, object?> { ["tab"] = "review", ["type"] = "IdentityConflict" }));
        var cut = Render<CorrelationReview>();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=cr-conflict-side]").Select(s => s.QuerySelector("strong")!.TextContent).Should().Equal("Utdanning", "SkoleAdapter"));
        api.Verify(a => a.ReviewItemsAsync("dev", ReviewItemKind.Decision, ReviewItemType.IdentityConflict, 0, 20, It.IsAny<CancellationToken>()), Times.AtLeastOnce, "the deep link opens the conflict filter");
        cut.Find("[data-testid=cr-conflict-same]").HasAttribute("disabled").Should().BeTrue("no rationale yet");
        cut.Find("[data-testid=cr-conflict-separate]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=cr-defer]").HasAttribute("disabled").Should().BeFalse("deferring needs no rationale");

        cut.Find("[data-testid=cr-rationale]").Input("Utdanning is the external system; SkoleAdapter is our adapter.");
        cut.Find("[data-testid=cr-conflict-separate]").Click();

        cut.WaitForAssertion(() => api.Verify(a => a.DecideAsync("dev", It.Is<ReviewDecisionRequest>(r => r.TargetKey == "rel-1" && r.Outcome == ReviewDecisionOutcome.Rejected
            && r.Notes == "Utdanning is the external system; SkoleAdapter is our adapter."), It.IsAny<CancellationToken>()), Times.Once));
    }

    [Fact]
    public async Task Page_tabs_move_by_keyboard()
    {
        await AnalyzedM2lb();
        var cut = Render<CorrelationReview>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=cr-tab-overview]"));
        cut.Find("[data-testid=cr-tab-overview]").KeyDown("End");
        cut.Find("[data-testid=cr-tab-history]").GetAttribute("aria-selected").Should().Be("true");
        cut.Find("[data-testid=cr-tab-history]").GetAttribute("tabindex").Should().Be("0");
    }

    [Fact]
    public async Task Analyzing_again_keeps_the_Delivery_snapshot_so_the_release_scope_is_not_dropped()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var storage = new ActiveTargetEnvironmentTests.Storage();
        var settings = new FrontendAnalysisSettingsService();
        await settings.LoadAsync(storage);
        settings.Settings.ActiveProfileId = "dev";
        Services.AddSingleton<IJSRuntime>(storage);
        Services.AddSingleton<IFrontendAnalysisSettingsService>(settings);
        Services.AddSingleton<IFrontendAnalysisContextFactory>(ActiveTargetEnvironmentTests.Factory(settings, storage));
        var source = Guid.NewGuid();
        var delivery = Guid.NewGuid();
        var catalog = new Mock<IIntegrationCatalogApiService>();
        catalog.Setup(c => c.ListSourceSnapshotSummariesAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync([Stored(source, "M2LB-language.zip", SourceAnalysisStatus.Ready)]);
        Services.AddSingleton(catalog.Object);
        var api = new Mock<ICanonicalInterpretationApiService>();
        // The latest analysis (from this page) had no Delivery snapshot; an earlier one did — its Delivery snapshot is offered and selected.
        api.Setup(a => a.SnapshotsAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync(((IReadOnlyList<InterpretationSnapshotView>)
        [
            new(Guid.NewGuid(), "dev", DateTimeOffset.UnixEpoch.AddDays(2), ["correlation/4"], [new(BirkNext.Snapshots.SnapshotType.SourceAnalysis, source.ToString())], 1, 10, 10, "c2", [], "Relationship Review", null),
            new(Guid.NewGuid(), "dev", DateTimeOffset.UnixEpoch.AddDays(1), ["correlation/4"],
                [new(BirkNext.Snapshots.SnapshotType.SourceAnalysis, source.ToString()), new(BirkNext.Snapshots.SnapshotType.Delivery, delivery.ToString(), "M2LB-language 4 Nov")], 1, 20, 20, "c1", [], "scope acceptance", null),
        ], (string?)null));
        api.Setup(a => a.DecisionsAsync("dev", null, It.IsAny<CancellationToken>())).ReturnsAsync(([], (string?)null));
        Services.AddSingleton(api.Object);

        var cut = Render<CorrelationReview>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=cr-delivery]").TextContent.Should().Contain("M2LB-language 4 Nov"));
        cut.Find("[data-testid=cr-delivery-include]").HasAttribute("checked").Should().BeTrue();
        cut.Find("[data-testid=cr-interpret-run]").Click();

        cut.WaitForAssertion(() => api.Verify(a => a.InterpretAsync("dev", It.Is<InterpretRequest>(r => r.DeliverySnapshotId == delivery && r.SourceSnapshotIds.SequenceEqual(new[] { source })),
            It.IsAny<CancellationToken>()), Times.Once()));
    }

    // ── NEXT56: grouped review queue with an inspector, side-by-side source evidence, master–detail explorer ──────────────

    static CanonicalEntityView Entity(CanonicalEntityType type, string id, string name, Guid? key = null) =>
        new(key ?? Guid.NewGuid(), type, id, name, name, "explicit id", null, new Dictionary<string, string>(), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    static CanonicalRelationView Relation(CanonicalEntityView source, CanonicalEntityView target, CanonicalRelationType type, RelationResolution resolution, string key) =>
        new(Guid.NewGuid(), key, source, target, type, resolution, resolution, CorrelationConfidence.Medium, "CR-TEST", "correlation/4",
            [new CorrelationEvidence("comment", "AbsenceTests.cs:12", "mentions FR-001")], "fp", "Requirement id mentioned near the test", RelationReviewState.NotReviewed, null, true);

    [Fact]
    public async Task The_identity_conflict_shows_each_entitys_source_evidence_side_by_side_and_is_never_resolved_automatically()
    {
        var api = await AnalyzedM2lb();
        var conflict = (await api.Object.ReviewItemsAsync("dev", ReviewItemKind.Decision, null, 0, 20)).Queue!.Page.Items[0].Relation!;
        CanonicalEntityDetail Detail(CanonicalEntityView e, string declaredBy) => new(e,
            [new CanonicalAliasView(Guid.NewGuid(), e.Id, "M2LB.SkoleAdapter.Worker", AliasState.Explicit, declaredBy, "CR-ALIAS", DateTimeOffset.UnixEpoch)],
            [new CanonicalOccurrenceView("3f2a9c1e-snapshot", $"{e.DisplayName}/topology", "Topology", $"declared in {declaredBy}", DateTimeOffset.UnixEpoch)], [], []);
        api.Setup(a => a.EntityAsync("dev", conflict.Source.Id, It.IsAny<CancellationToken>())).ReturnsAsync((Detail(conflict.Source, "Utdanning/topology.json"), (string?)null));
        api.Setup(a => a.EntityAsync("dev", conflict.Target.Id, It.IsAny<CancellationToken>())).ReturnsAsync((Detail(conflict.Target, "SkoleAdapter/appsettings.json"), (string?)null));
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(nav.GetUriWithQueryParameters(new Dictionary<string, object?> { ["tab"] = "review", ["type"] = "IdentityConflict" }));
        var cut = Render<CorrelationReview>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=cr-conflict-source-name]").TextContent.Should().Be("Utdanning"));
        cut.Find("[data-testid=cr-conflict-target-name]").TextContent.Should().Be("SkoleAdapter");
        cut.Find("[data-testid=cr-conflict-source-aliases]").TextContent.Should().Contain("M2LB.SkoleAdapter.Worker").And.Contain("declared by Utdanning/topology.json");
        cut.Find("[data-testid=cr-conflict-target-aliases]").TextContent.Should().Contain("declared by SkoleAdapter/appsettings.json");
        cut.Find("[data-testid=cr-conflict-source-occurrences]").TextContent.Should().Contain("declared in Utdanning/topology.json");
        cut.FindAll("[data-testid=cr-conflict-source-relation]").Should().BeEmpty("beside a conflict the inspector shows identity and source evidence only");
        api.Verify(a => a.DecideAsync(It.IsAny<string>(), It.IsAny<ReviewDecisionRequest>(), It.IsAny<CancellationToken>()), Times.Never, "showing evidence never resolves the conflict");
        cut.Find("[data-testid=cr-conflict-same]").HasAttribute("disabled").Should().BeTrue("a decision needs a written rationale");
        // The long evidence of both entities is one disclosure between the summary and the decision; rationale and options follow directly.
        var conflictCard = cut.Find("[data-testid=cr-conflict]");
        conflictCard.QuerySelector("[data-testid=cr-conflict-evidence-more] [data-testid=cr-conflict-evidence]").Should().NotBeNull();
        var markup = conflictCard.InnerHtml;
        markup.IndexOf("cr-conflict-evidence-more", StringComparison.Ordinal).Should().BeLessThan(markup.IndexOf("cr-rationale", StringComparison.Ordinal));
        cut.Find("[data-testid=cr-conflict-source-occurrences] summary").TextContent.Should().StartWith("Located in source ·", "an occurrence is a located appearance, not every piece of evidence");
    }

    [Fact]
    public async Task The_review_queue_groups_related_candidates_and_decides_the_question_open_in_the_inspector()
    {
        var api = await AnalyzedM2lb();
        var fr1 = Entity(CanonicalEntityType.Requirement, "FR-001", "FR-001 Register absence");
        var fr2 = Entity(CanonicalEntityType.Requirement, "FR-002", "FR-002 Report absence");
        var test = Entity(CanonicalEntityType.Test, "AbsenceTests.Register", "AbsenceTests.Register");
        var other = Entity(CanonicalEntityType.Test, "ReportTests.Send", "ReportTests.Send");
        ReviewItemView Suggestion(string id, CanonicalRelationView r) => new(id, ReviewItemKind.Suggestion, ReviewItemType.TestLink, 5, "Requirement id mentioned near the test — not coverage until confirmed.",
            ReviewTargetKind.Relation, r.RelationKey, r, null, null, ["Traceability", "Test Coverage"], "CR-TEST", "correlation/4", "Candidate", null, DateTimeOffset.UnixEpoch, null);
        var items = new[]
        {
            Suggestion("s1", Relation(test, fr1, CanonicalRelationType.Verifies, RelationResolution.Candidate, "rel-s1")),
            Suggestion("s2", Relation(other, fr2, CanonicalRelationType.Verifies, RelationResolution.Candidate, "rel-s2")),
            Suggestion("s3", Relation(test, fr2, CanonicalRelationType.Verifies, RelationResolution.Candidate, "rel-s3")),
        };
        api.Setup(a => a.ReviewItemsAsync("dev", ReviewItemKind.Suggestion, It.IsAny<ReviewItemType?>(), 0, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new ReviewQueueView(1, 3, new Dictionary<ReviewItemType, int>(), new Dictionary<ReviewItemType, int> { [ReviewItemType.TestLink] = 3 },
                new CanonicalPage<ReviewItemView>(items, 3, 0, 20), null), (string?)null));
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(nav.GetUriWithQueryParameters(new Dictionary<string, object?> { ["tab"] = "review", ["type"] = "TestLink" }));
        var cut = Render<CorrelationReview>();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=cr-queue-item]").Should().HaveCount(3));
        cut.Find("[data-testid=cr-inspect]").GetAttribute("data-item").Should().Be("s1", "the first question is open");
        cut.Find("[data-testid=cr-group-by]").Change(CorrelationQueueGroups.BySource);
        cut.FindAll("[data-testid=cr-group]").Select(g => g.QuerySelector("strong")!.TextContent).Should().Equal("AbsenceTests.Register", "ReportTests.Send");
        cut.FindAll("[data-testid=cr-group]")[0].QuerySelectorAll("[data-testid=cr-queue-item]").Select(i => i.QuerySelector("[data-testid=cr-item-open]") is not null).Should().HaveCount(2,
            "both candidate links of one test are reviewed together");
        cut.FindAll("[data-testid=cr-item-open]")[1].Click();
        cut.Find("[data-testid=cr-inspect]").GetAttribute("data-item").Should().Be("s3");
        cut.Find("[data-testid=cr-inspect-evidence]").TextContent.Should().Contain("AbsenceTests.cs:12").And.Contain("CR-TEST");
        cut.Find("[data-testid=cr-inspect-affects]").TextContent.Should().Contain("Test Coverage");
        cut.Find("[data-testid=cr-confirm]").Click();
        cut.WaitForAssertion(() => api.Verify(a => a.DecideAsync("dev", It.Is<ReviewDecisionRequest>(r => r.TargetKey == "rel-s3" && r.Outcome == ReviewDecisionOutcome.Confirmed), It.IsAny<CancellationToken>()), Times.Once));
    }

    [Fact]
    public async Task The_explorer_lists_entities_in_pages_beside_an_inspector_with_established_and_candidate_relations_apart()
    {
        var api = await AnalyzedM2lb();
        var fr1 = Entity(CanonicalEntityType.Requirement, "FR-001", "FR-001 Register absence");
        var fr2 = Entity(CanonicalEntityType.Requirement, "FR-002", "FR-002 Report absence");
        var test = Entity(CanonicalEntityType.Test, "AbsenceTests.Register", "AbsenceTests.Register");
        var story = Entity(CanonicalEntityType.UserStory, "US1", "US1 Register absence");
        api.Setup(a => a.EntitiesAsync("dev", It.IsAny<CanonicalEntityType?>(), "FR", 0, 25, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new CanonicalPage<CanonicalEntityView>([fr1, fr2], 30, 0, 25), (string?)null));
        api.Setup(a => a.EntityAsync("dev", It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(((CanonicalEntityDetail?)null, (string?)"not found"));
        api.Setup(a => a.EntityAsync("dev", fr1.Id, It.IsAny<CancellationToken>())).ReturnsAsync((new CanonicalEntityDetail(fr1, [], [],
            [], [Relation(story, fr1, CanonicalRelationType.Contains, RelationResolution.Explicit, "r1"), Relation(test, fr1, CanonicalRelationType.Verifies, RelationResolution.Candidate, "r2")]), (string?)null));
        api.Setup(a => a.EntityAsync("dev", fr2.Id, It.IsAny<CancellationToken>())).ReturnsAsync((new CanonicalEntityDetail(fr2, [], [], [], []), (string?)null));
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(nav.GetUriWithQueryParameters(new Dictionary<string, object?> { ["tab"] = "relationships" }));
        var cut = Render<CorrelationReview>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=cr-explore-text]"));
        cut.Find("[data-testid=cr-explore-text]").Change("FR");
        cut.Find("[data-testid=cr-explore-run]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=cr-explorer-item]").Should().HaveCount(2));
        cut.Find("[data-testid=cr-explorer-count]").TextContent.Should().Be("2 of 30 entities");
        cut.Find("[data-testid=cr-explorer-more]").TextContent.Should().Contain("Load 25 more");
        cut.WaitForAssertion(() => cut.Find("[data-testid=cr-explorer-inspector-name]").TextContent.Should().Be("FR-001 Register absence"));
        cut.Find("[data-testid=cr-explorer-inspector-identity]").TextContent.Should().Be("FR-001");
        var relations = cut.FindAll("[data-testid=cr-explorer-inspector-relation]");
        relations.Select(r => r.GetAttribute("data-counts")).Should().BeEquivalentTo(["true", "false"], "established and candidate relations stay apart");
        cut.FindAll("[data-testid=cr-explorer-inspector-group]").Select(g => g.TextContent).Should().Contain(t => t.Contains("0 established · 1 candidate or not counted"));

        cut.Find("[data-testid=cr-explorer-items]").KeyDown("ArrowDown");
        cut.WaitForAssertion(() => cut.Find("[data-testid=cr-explorer-inspector-name]").TextContent.Should().Be("FR-002 Report absence"));
        cut.Find("[data-testid=cr-explorer-item][data-entity=\"" + fr2.Id + "\"]").GetAttribute("aria-selected").Should().Be("true");
    }

    [Fact]
    public void Grouping_keeps_the_queue_priority_order_and_groups_by_first_occurrence()
    {
        var a = Entity(CanonicalEntityType.Test, "A", "A");
        var b = Entity(CanonicalEntityType.Test, "B", "B");
        var target = Entity(CanonicalEntityType.Requirement, "FR-1", "FR-1");
        ReviewItemView Item(string id, CanonicalEntityView source, ReviewItemType type) => new(id, ReviewItemKind.Decision, type, 1, "r", ReviewTargetKind.Relation, id,
            Relation(source, target, CanonicalRelationType.Verifies, RelationResolution.Candidate, id), null, null, [], "CR", null, "Candidate", null, DateTimeOffset.UnixEpoch, null);
        var items = new[] { Item("1", b, ReviewItemType.IdentityConflict), Item("2", a, ReviewItemType.TestLink), Item("3", b, ReviewItemType.TestLink) };

        CorrelationQueueGroups.Group(items, CorrelationQueueGroups.BySource).Select(g => (g.Label, string.Join(",", g.Items.Select(i => i.ReviewItemId))))
            .Should().Equal(("B", "1,3"), ("A", "2"));
        CorrelationQueueGroups.Group(items, CorrelationQueueGroups.ByType).Select(g => g.Items.Count).Should().Equal(1, 2);
        CorrelationQueueGroups.Group(items, CorrelationQueueGroups.ByTarget).Should().ContainSingle().Which.Items.Should().HaveCount(3);
    }

    // ── NEXT56 follow-up: decision-impact preview, confidence and per-question history ──────────────────────────────────────

    [Fact]
    public async Task The_identity_conflict_previews_what_each_decision_changes_without_deciding()
    {
        var api = await AnalyzedM2lb();
        var conflict = (await api.Object.ReviewItemsAsync("dev", ReviewItemKind.Decision, null, 0, 20)).Queue!.Page.Items[0].Relation!;
        api.Setup(a => a.ImpactAsync("dev", It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((string _, string key, CancellationToken _) =>
            (new CanonicalImpactView
            {
                RootKey = key,
                Entities = [new("e1", "Service", "SkoleService", BirkNext.Interpretation.ImpactLevel.Direct, 1, []), new("e2", "Requirement", "FR-001", BirkNext.Interpretation.ImpactLevel.Potential, 2, [])],
                Tests = [new("t1", "SkoleTests", RetestLevel.ShouldRetest, "candidate")],
            }, (string?)null));
        api.Setup(a => a.DecisionsAsync("dev", "rel-1", It.IsAny<CancellationToken>())).ReturnsAsync(((IReadOnlyList<ReviewDecisionView>)
            [new ReviewDecisionView(Guid.NewGuid(), 7, ReviewTargetKind.Relation, "rel-1", ReviewDecisionOutcome.Deferred, "tester", DateTimeOffset.UnixEpoch, "Waiting for the integration owner", "fp", "correlation/4", null, null, "Ambiguous")], (string?)null));
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(nav.GetUriWithQueryParameters(new Dictionary<string, object?> { ["tab"] = "review", ["type"] = "IdentityConflict" }));
        var cut = Render<CorrelationReview>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=cr-preview-impact]"));
        cut.Find("[data-testid=cr-inspect-confidence]").TextContent.Should().Contain("Confidence: Low").And.Contain(CorrelationRules.AliasConflictRule);
        cut.Find("[data-testid=cr-preview-impact]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=cr-preview-outcome]").Should().HaveCount(3));
        var outcomes = cut.FindAll("[data-testid=cr-preview-outcome]").ToDictionary(o => o.GetAttribute("data-outcome")!);
        outcomes["Confirmed"].TextContent.Should().Contain("Same entity").And.Contain("1 direct, 0 downstream, 1 potential dependents · 1 test");
        outcomes["Confirmed"].GetAttribute("data-governance").Should().Be("true");
        outcomes["Rejected"].TextContent.Should().Contain("Separate entities").And.Contain("Needs a written rationale");
        outcomes["Deferred"].TextContent.Should().Contain("Release readiness stays Blocked by this conflict");
        outcomes["Deferred"].GetAttribute("data-governance").Should().Be("false", "deferring never clears a blocker");
        api.Verify(a => a.ImpactAsync("dev", conflict.Source.Key, It.IsAny<CancellationToken>()), Times.Once);
        api.Verify(a => a.ImpactAsync("dev", conflict.Target.Key, It.IsAny<CancellationToken>()), Times.Once);

        cut.Find("[data-testid=cr-item-history-toggle]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=cr-item-history-entry]").TextContent.Should().Contain("#7").And.Contain("Deferred").And.Contain("Waiting for the integration owner"));
        api.Verify(a => a.DecideAsync(It.IsAny<string>(), It.IsAny<ReviewDecisionRequest>(), It.IsAny<CancellationToken>()), Times.Never, "a preview decides nothing");
    }

    [Fact]
    public void A_test_link_preview_says_confirming_is_test_design_never_execution()
    {
        var fr = Entity(CanonicalEntityType.Requirement, "FR-001", "FR-001");
        var test = Entity(CanonicalEntityType.Test, "T", "AbsenceTests.Register");
        var item = new ReviewItemView("s1", ReviewItemKind.Suggestion, ReviewItemType.TestLink, 5, "r", ReviewTargetKind.Relation, "rel-s1",
            Relation(test, fr, CanonicalRelationType.Verifies, RelationResolution.Candidate, "rel-s1"), null, null, ["Test Coverage"], "CR", null, "Candidate", null, DateTimeOffset.UnixEpoch, null);
        var preview = CorrelationDecisionImpact.Preview(item, null, null).ToDictionary(o => o.Outcome);
        preview[ReviewDecisionOutcome.Confirmed].Consequences.Should().Contain(c => c.Contains("test design, not execution or a pass"));
        preview[ReviewDecisionOutcome.Confirmed].ChangesGovernance.Should().BeFalse("an optional suggestion never changes a gate");
        preview[ReviewDecisionOutcome.Deferred].Consequences.Should().Contain(c => c.Contains("counts nowhere"));
    }
}
