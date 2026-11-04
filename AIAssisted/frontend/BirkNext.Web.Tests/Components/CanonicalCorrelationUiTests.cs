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
        var ambiguous = R(E(CanonicalEntityType.Test, "m2lb|T.A", "T.A"), E(CanonicalEntityType.Requirement, "skoleservice/001/FR-001", "FR-001"),
            CanonicalRelationType.Verifies, RelationResolution.Ambiguous, rule: CorrelationRules.TestScopeAmbiguityRule);
        var item = new ReviewItemView("relation:1", ReviewItemKind.Decision, ReviewItemType.TestLink, 3, ambiguous.Explanation, ReviewTargetKind.Relation, ambiguous.RelationKey,
            ambiguous, null, null, ["Traceability", "Test Coverage"], ambiguous.RuleId, "correlation/3", "Ambiguous", null, DateTimeOffset.UnixEpoch, null);
        var api = new Mock<ICanonicalInterpretationApiService>();
        api.Setup(a => a.ReviewItemsAsync("dev", ReviewItemKind.Decision, null, 0, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new ReviewQueueView(1, 1361, new Dictionary<ReviewItemType, int> { [ReviewItemType.TestLink] = 1 }, new Dictionary<ReviewItemType, int>(),
                new CanonicalPage<ReviewItemView>([item], 1, 0, 20), null), (string?)null));
        api.Setup(a => a.SnapshotsAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync(([], (string?)null));
        api.Setup(a => a.DecisionsAsync("dev", null, It.IsAny<CancellationToken>())).ReturnsAsync(([], (string?)null));
        api.Setup(a => a.SummaryAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync((new CanonicalSummaryView("dev", Guid.NewGuid(), DateTimeOffset.UnixEpoch, ["correlation/2"],
            new Dictionary<string, int>(), new Dictionary<string, int>(), 10, 1, 1, 0, new(70, 84, 81, 88, 14),
            [new(Guid.NewGuid(), "Terraform-iac-meldingsutveksler", ["Infrastructure", "Contracts"], [])], []), (string?)null));
        Services.AddSingleton(api.Object);

        var cut = Render<CorrelationReview>();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=cr-queue-item]").Should().ContainSingle());
        cut.Find("[data-testid=cr-scope]").TextContent.Should().Contain("dev");
        cut.FindAll("[data-testid=cr-source]").Should().ContainSingle();
        cut.Find("[data-testid=cr-queue-item]").GetAttribute("data-rule").Should().Be(CorrelationRules.TestScopeAmbiguityRule);
        cut.Find("[data-testid=cr-queue-item] .ssb-label").TextContent.Should().Be("Ambiguous");
        cut.Find("[data-testid=cr-decision-count]").TextContent.Should().Be("1 decision");
        cut.Find("[data-testid=cr-kind-suggestions]").TextContent.Should().Contain("1361");
        cut.Find("[data-testid=cr-affects]").TextContent.Should().Contain("Traceability").And.Contain("Test Coverage");
        cut.WaitForAssertion(() => cut.Find("[data-testid=cr-sum-contracts]").TextContent.Should()
            .Contain("70 logical").And.Contain("84 versions").And.Contain("81 distinct contents").And.Contain("88 occurrences"));
        cut.Find("[data-testid=cr-sum-package]").TextContent.Should().Contain("Infrastructure").And.Contain("Contracts");
    }

    [Fact]
    public async Task One_ambiguity_is_one_question_and_choosing_an_alternative_confirms_exactly_that_relation()
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
        catalog.Setup(c => c.ListSourceSnapshotSummariesAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync([]);
        Services.AddSingleton(catalog.Object);
        var test = E(CanonicalEntityType.Test, "m2lb|T.A", "ContractTests.Fr001");
        var a = R(test, E(CanonicalEntityType.Requirement, "skoleservice/001/FR-001", "FR-001 Lagre"), CanonicalRelationType.Verifies, RelationResolution.Ambiguous, rule: CorrelationRules.TestScopeAmbiguityRule);
        var b = R(test, E(CanonicalEntityType.Requirement, "autorisasjon/002/FR-001", "FR-001 Tilgang"), CanonicalRelationType.Verifies, RelationResolution.Ambiguous, rule: CorrelationRules.TestScopeAmbiguityRule);
        var item = new ReviewItemView("ambiguity:x", ReviewItemKind.Decision, ReviewItemType.TestLink, 3, a.Explanation, ReviewTargetKind.Relation, a.RelationKey, a, null, null,
            ["Traceability"], a.RuleId, "correlation/3", "Ambiguous", null, DateTimeOffset.UnixEpoch, null, [a, b]);
        var api = new Mock<ICanonicalInterpretationApiService>();
        api.Setup(x => x.ReviewItemsAsync("dev", ReviewItemKind.Decision, null, 0, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new ReviewQueueView(1, 0, new Dictionary<ReviewItemType, int> { [ReviewItemType.TestLink] = 1 }, new Dictionary<ReviewItemType, int>(),
                new CanonicalPage<ReviewItemView>([item], 1, 0, 20), null), (string?)null));
        api.Setup(x => x.SnapshotsAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync(([], (string?)null));
        api.Setup(x => x.DecisionsAsync("dev", null, It.IsAny<CancellationToken>())).ReturnsAsync(([], (string?)null));
        api.Setup(x => x.DecideAsync("dev", It.IsAny<ReviewDecisionRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(((ReviewDecisionView?)null, (string?)null));
        Services.AddSingleton(api.Object);

        var cut = Render<CorrelationReview>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=cr-queue-item]").Should().ContainSingle("two alternatives of one test are one question"));
        cut.FindAll("[data-testid=cr-choose]").Should().HaveCount(2);
        cut.FindAll("[data-testid=cr-choose]")[1].Click();
        api.Verify(x => x.DecideAsync("dev", It.Is<ReviewDecisionRequest>(r => r.TargetKey == b.RelationKey && r.Outcome == ReviewDecisionOutcome.Confirmed), It.IsAny<CancellationToken>()), Times.Once());
        api.Verify(x => x.DecideAsync("dev", It.Is<ReviewDecisionRequest>(r => r.TargetKey == a.RelationKey), It.IsAny<CancellationToken>()), Times.Never());
    }
}
