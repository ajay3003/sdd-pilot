using BirkNext.Integrations;
using BirkNext.Interpretation;
using BirkNext.Web.Components;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using BirkNext.Web.Tests.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>The shared correlation UI: statuses through the shared vocabulary, candidates never shown as confirmed, review actions sent to the
/// shared registry, and the project scope always passed.</summary>
public sealed class CanonicalCorrelationUiTests : BunitContext
{
    static CanonicalEntityView E(CanonicalEntityType type, string key, string name) =>
        new(Guid.NewGuid(), type, key, name, name, "test", null, new Dictionary<string, string>(), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    static CanonicalRelationView R(CanonicalEntityView from, CanonicalEntityView to, CanonicalRelationType type, RelationResolution resolution,
        RelationReviewState review = RelationReviewState.NotReviewed, string rule = "CR-M-CANDIDATE") =>
        new(Guid.NewGuid(), $"{from.Key}->{type}->{to.Key}", from, to, type, resolution,
            review == RelationReviewState.Confirmed ? RelationResolution.Confirmed : review == RelationReviewState.Rejected ? RelationResolution.Rejected : resolution,
            CorrelationConfidence.Low, rule, "correlation/1", [new("test", "tests/A.cs#T.A")], "fp", "Looks related by naming only.", review, null, true);

    [Theory]
    [InlineData(RelationResolution.Explicit, RelationReviewState.NotReviewed, SemanticStatus.Confirmed, "Explicit")]
    [InlineData(RelationResolution.Derived, RelationReviewState.NotReviewed, SemanticStatus.Confirmed, "Derived")]
    [InlineData(RelationResolution.Candidate, RelationReviewState.NotReviewed, SemanticStatus.Inferred, "Candidate")]
    [InlineData(RelationResolution.Candidate, RelationReviewState.Deferred, SemanticStatus.Inferred, "Candidate · deferred")]
    [InlineData(RelationResolution.Ambiguous, RelationReviewState.NotReviewed, SemanticStatus.NeedsReview, "Ambiguous")]
    [InlineData(RelationResolution.Candidate, RelationReviewState.Confirmed, SemanticStatus.Confirmed, "Confirmed by reviewer")]
    [InlineData(RelationResolution.Candidate, RelationReviewState.Rejected, SemanticStatus.Removed, "Rejected")]
    [InlineData(RelationResolution.Candidate, RelationReviewState.RevalidationRequired, SemanticStatus.NeedsReview, "Revalidation required")]
    [InlineData(RelationResolution.Explicit, RelationReviewState.Conflict, SemanticStatus.Conflict, "Conflict")]
    public void Relation_states_map_onto_the_shared_vocabulary(RelationResolution resolution, RelationReviewState review, SemanticStatus status, string label)
    {
        var r = R(E(CanonicalEntityType.Test, "t", "T"), E(CanonicalEntityType.Requirement, "FR-001", "FR-001"), CanonicalRelationType.Verifies, resolution, review);
        var mapped = CorrelationPresentation.Status(r);
        mapped.Status.Should().Be(status);
        mapped.Label.Should().Be(label);
        mapped.Explanation.Should().NotBeNullOrWhiteSpace();
        StatusVocabulary.Of(status).Key.Should().Be(status, "only existing vocabulary terms are used");
    }

    [Fact]
    public void Panel_shows_the_candidate_as_inferred_and_sends_a_scoped_decision()
    {
        var test = E(CanonicalEntityType.Test, "m2lb|T.A", "T.A");
        var req = E(CanonicalEntityType.Requirement, "FR-002", "FR-002");
        var candidate = R(test, req, CanonicalRelationType.Verifies, RelationResolution.Candidate);
        var api = new Mock<ICanonicalInterpretationApiService>();
        api.Setup(a => a.EntitiesAsync("dev", CanonicalEntityType.Test, "T.A", 0, 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new CanonicalPage<CanonicalEntityView>([test], 1, 0, 3), (string?)null));
        api.Setup(a => a.EntityAsync("dev", test.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new CanonicalEntityDetail(test, [], [], [candidate], []), (string?)null));
        api.Setup(a => a.DecideAsync("dev", It.IsAny<ReviewDecisionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new ReviewDecisionView(Guid.NewGuid(), 1, ReviewTargetKind.Relation, candidate.RelationKey, ReviewDecisionOutcome.Rejected, "local user",
                DateTimeOffset.UnixEpoch, null, "fp", "correlation/1", null, null, "{}"), (string?)null));
        Services.AddSingleton(api.Object);

        var cut = Render<CanonicalCorrelationPanel>(p => p.Add(x => x.Scope, "dev").Add(x => x.EntityType, CanonicalEntityType.Test).Add(x => x.Search, "T.A"));

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=ccp-relation]").Should().ContainSingle());
        var row = cut.Find("[data-testid=ccp-relation]");
        row.GetAttribute("data-counts").Should().Be("false", "a candidate is never counted as traceability");
        row.QuerySelector(".ssb-label")!.TextContent.Should().Be("Candidate");
        row.QuerySelector(".ssb")!.GetAttribute("data-status").Should().Be(nameof(SemanticStatus.Inferred));

        row.QuerySelectorAll("button").Single(b => b.TextContent == "Reject").Click();
        api.Verify(a => a.DecideAsync("dev", It.Is<ReviewDecisionRequest>(r => r.TargetKind == ReviewTargetKind.Relation && r.TargetKey == candidate.RelationKey
            && r.Outcome == ReviewDecisionOutcome.Rejected), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Panel_without_a_project_asks_for_one_and_calls_nothing()
    {
        var api = new Mock<ICanonicalInterpretationApiService>(MockBehavior.Strict);
        Services.AddSingleton(api.Object);
        var cut = Render<CanonicalCorrelationPanel>(p => p.Add(x => x.Scope, (string?)null));
        cut.Find("[data-testid=ccp-no-scope]").TextContent.Should().Contain("Select a project");
    }

    [Fact]
    public void Panel_reports_a_backend_error_with_retry_instead_of_an_empty_state()
    {
        var api = new Mock<ICanonicalInterpretationApiService>();
        api.Setup(a => a.EntitiesAsync(It.IsAny<string>(), It.IsAny<CanonicalEntityType?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((CanonicalPage<CanonicalEntityView>?)null, "Correlation review request failed (500)."));
        Services.AddSingleton(api.Object);
        var cut = Render<CanonicalCorrelationPanel>(p => p.Add(x => x.Scope, "dev"));
        cut.WaitForAssertion(() => cut.Find("[data-testid=ccp-error]").TextContent.Should().Contain("failed (500)"));
        cut.FindAll("[data-testid=ccp-empty]").Should().BeEmpty();
    }

    [Fact]
    public void Proposed_alias_offers_review_and_sends_an_alias_decision()
    {
        var mu = E(CanonicalEntityType.System, "meldingsutveksleren", "Meldingsutveksleren");
        var api = new Mock<ICanonicalInterpretationApiService>();
        api.Setup(a => a.EntitiesAsync("dev", CanonicalEntityType.System, null, 0, 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new CanonicalPage<CanonicalEntityView>([mu], 1, 0, 3), (string?)null));
        api.Setup(a => a.EntityAsync("dev", mu.Id, It.IsAny<CancellationToken>())).ReturnsAsync((new CanonicalEntityDetail(mu,
            [new(Guid.NewGuid(), mu.Id, "MU", AliasState.Proposed, "catalog ↔ profile", "CR-K-ACRONYM", DateTimeOffset.UnixEpoch),
             new(Guid.NewGuid(), mu.Id, "Buf.Meldingsutveksler.Service", AliasState.Explicit, "domain profile", "CR-K-DECLARED", DateTimeOffset.UnixEpoch)], [], [], []), (string?)null));
        api.Setup(a => a.DecideAsync("dev", It.IsAny<ReviewDecisionRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(((ReviewDecisionView?)null, (string?)null));
        Services.AddSingleton(api.Object);

        var cut = Render<CanonicalCorrelationPanel>(p => p.Add(x => x.Scope, "dev").Add(x => x.EntityType, CanonicalEntityType.System));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=ccp-alias]").Should().HaveCount(2));
        var aliases = cut.FindAll("[data-testid=ccp-alias]");
        aliases.Single(a => a.TextContent.Contains("Buf.Meldingsutveksler")).QuerySelectorAll("button").Should().BeEmpty("a declared alias needs no review");
        aliases.Single(a => a.TextContent.StartsWith("MU")).QuerySelector(".ssb-label")!.TextContent.Should().Be("Proposed");
        aliases.Single(a => a.TextContent.StartsWith("MU")).QuerySelectorAll("button").Single(b => b.TextContent == "Confirm").Click();
        api.Verify(a => a.DecideAsync("dev", It.Is<ReviewDecisionRequest>(r => r.TargetKind == ReviewTargetKind.Alias
            && r.TargetKey == CorrelationPrecedence.AliasTargetKey(mu.Key, "MU") && r.Outcome == ReviewDecisionOutcome.Confirmed), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Correlation_review_page_is_scoped_to_the_active_project_and_lists_the_queue()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var storage = new ActiveTargetEnvironmentTests.Storage();
        var settings = new FrontendAnalysisSettingsService();
        await settings.LoadAsync(storage);
        settings.Settings.ActiveProfileId = "dev";
        Services.AddSingleton<IJSRuntime>(storage);
        Services.AddSingleton<IFrontendAnalysisSettingsService>(settings);
        Services.AddSingleton<IFrontendAnalysisContextFactory>(ActiveTargetEnvironmentTests.Factory(settings, storage));
        var catalog = new Mock<IIntegrationCatalogApiService>();
        catalog.Setup(c => c.ListSourceSnapshotSummariesAsync("dev", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new IqrSourceSnapshotSummary { Id = Guid.NewGuid(), Archive = new("M2LB.zip", "abc", 10), AnalyzedAt = DateTimeOffset.UnixEpoch }]);
        Services.AddSingleton(catalog.Object);
        var candidate = R(E(CanonicalEntityType.Feature, "a/1", "Absence reporting"), E(CanonicalEntityType.Capability, "candidate:absence-report", "Absence Report"),
            CanonicalRelationType.Implements, RelationResolution.Candidate, rule: CorrelationRules.IntentRule);
        var api = new Mock<ICanonicalInterpretationApiService>();
        api.Setup(a => a.ReviewQueueAsync("dev", null, 0, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new CanonicalPage<CanonicalRelationView>([candidate], 1, 0, 20), (string?)null));
        api.Setup(a => a.SnapshotsAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync(([], (string?)null));
        api.Setup(a => a.DecisionsAsync("dev", null, It.IsAny<CancellationToken>())).ReturnsAsync(([], (string?)null));
        Services.AddSingleton(api.Object);

        var cut = Render<CorrelationReview>();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=cr-queue-item]").Should().ContainSingle());
        cut.Find("[data-testid=cr-scope]").TextContent.Should().Contain("dev");
        cut.FindAll("[data-testid=cr-source]").Should().ContainSingle();
        cut.Find("[data-testid=cr-queue-item]").GetAttribute("data-rule").Should().Be(CorrelationRules.IntentRule);
        cut.Find("[data-testid=cr-queue-item] .ssb-label").TextContent.Should().Be("Candidate");
    }
}
