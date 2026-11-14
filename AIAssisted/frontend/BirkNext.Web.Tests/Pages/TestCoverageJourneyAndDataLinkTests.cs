using BirkNext.ManualReview;
using BirkNext.Scopes;
using BirkNext.SourceEvidence;
using BirkNext.TestCoverage;
using BirkNext.TestData;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Test Coverage: a journey candidate shows both chains as its evidence and the reviewer's rationale is recorded and shown; decisions are read
/// from the shared store when the page loads; a journey named by two overlapping scopes counts once; QA tests show each Test Data asset with its
/// bound revision and readiness, an unresolved revision is never counted as in scope, and "Request or link Test Data" opens Test Data with the
/// QA test as a project-scoped consumer.
/// </summary>
public sealed class TestCoverageJourneyAndDataLinkTests : BunitContext
{
    private readonly Mock<ITestCoverageApiService> _api = new();
    private static readonly Guid Current = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid ScopeA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid ScopeB = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");
    private const string SourceJourney = "journey:adapter>api>events>notifier";
    private const string Declared = "project.import-1:order-notification";
    private static readonly string[] Chain = ["Orders.Adapter", "Orders.Api", "order-events", "Orders.Notifier"];
    private TestCoverageReviewRequest? _sent;

    public TestCoverageJourneyAndDataLinkTests()
    {
        var context = new Mock<IReviewContextProvider>();
        context.Setup(x => x.GetCurrent()).Returns(new ReviewContext());
        Services.AddSingleton(context.Object);
        Services.AddSingleton<IWorkspaceSessionService>(new WorkspaceArtifactRepository());
        Services.AddSingleton(_api.Object);
        Services.AddSingleton(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Projection(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Loaded("Imported", "import-1", "Journey fixture")).Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
        _api.Setup(x => x.HistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _api.Setup(x => x.SourcesAsync(It.IsAny<CancellationToken>())).ReturnsAsync((new ReviewSourceOptions
        {
            Snapshots = [new ReviewSourceSnapshot { SnapshotId = Current, RepositoryKey = "journey-fixture", Repository = "journey-fixture", ArchiveName = "journey-fixture.zip", Fingerprint = new string('d', 64),
                AnalyzedAt = DateTimeOffset.UtcNow, SourceStatus = "Ready", Latest = true, HasConsumerEvidence = true }],
        }, (string?)null));
    }

    private static ScopeSelectionMember Member(ScopeMemberKind kind, string id, params Guid[] scopes) =>
        new(new ScopeMember { Kind = kind, TargetId = id, Label = id }, [.. scopes.Select(s => new ScopeContribution(s, s == ScopeA ? "Orders" : "Release", 1))]);

    /// <summary>The declared journey and the Test Data asset are named by BOTH scopes (overlapping scopes).</summary>
    private static ScopeSelection Selection() => new()
    {
        ProjectKey = "import-1", Scopes = [new(ScopeA, "Orders", 1), new(ScopeB, "Release", 1)],
        Members = [Member(ScopeMemberKind.Journey, Declared, ScopeA, ScopeB), Member(ScopeMemberKind.TestDataAsset, "tda_customer", ScopeA, ScopeB)],
    };

    private static QaTestCandidate Qa(string id, string title, CoverageDataNeed need, params CoverageTestDataLink[] links) =>
        new() { Id = id, Title = title, Source = "QA automated test (Orders.Acceptance.Tests)", Ownership = TestOwnership.QaAutomated, DataNeed = need, TestData = [.. links] };

    private static TestCoverageReviewResult Result(params QaTestCandidate[] qa) => new()
    {
        CompletedAt = DateTimeOffset.UtcNow, ProjectName = "Journey fixture", Current = new(Current, "journey-fixture", "journey-fixture.zip", new string('d', 64), DateTimeOffset.UtcNow),
        Inventory = new CoverageInventory { TestsDiscovered = 3, TestsAnalyzed = 3, ExecutionEvidence = TestCoverageText.NoExecution, CodeCoverage = TestCoverageText.NoPercentage },
        QaTests = [.. qa],
        Journeys =
        [
            new Journey { Id = SourceJourney, Title = string.Join(" → ", Chain), Weakest = ConnectionEvidence.StronglySupported,
                AssociationCandidates = [new JourneyAssociationCandidate(Declared, "Order notification", "Equal", "Same declared component chain (structure only; needs review).", Chain, Chain)] },
            new Journey { Id = "journey:adapter>api>audit>auditor", Title = "Orders.Adapter → Orders.Api → order-audit → Orders.Auditor", Weakest = ConnectionEvidence.StronglySupported },
        ],
    };

    private static ManualReviewDecisionView Decision(ManualReviewDecisionValue value, string? note = null) => new()
    {
        Id = Guid.NewGuid(), Module = ManualReviewModules.JourneyAssociation, ScopeKey = "import-1", SubjectKey = SourceJourney, AppliesTo = Declared,
        Reviewer = "Kari", Decision = value, DecidedAt = DateTimeOffset.UtcNow, Current = true, Note = note,
    };

    private static Mock<IManualReviewApiService> Manual(params ManualReviewDecisionView[] decisions)
    {
        var manual = new Mock<IManualReviewApiService>();
        manual.Setup(x => x.ListAsync(ManualReviewModules.JourneyAssociation, "import-1", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ManualReviewDecisionView>?)decisions.ToList());
        return manual;
    }

    private IRenderedComponent<TestCoverageReview> Render(TestCoverageReviewResult result, Mock<IManualReviewApiService> manual, bool scoped = true)
    {
        Services.AddSingleton(manual.Object);
        var scopes = new Mock<IScopeRegistryApiService>();
        scopes.Setup(x => x.ListAsync("import-1", It.IsAny<CancellationToken>())).ReturnsAsync(([
            new ScopeSummary { Id = ScopeA, Name = "Orders", State = ScopeState.Approved, ApprovedRevision = 1, CurrentRevision = 1 },
            new ScopeSummary { Id = ScopeB, Name = "Release", State = ScopeState.Approved, ApprovedRevision = 1, CurrentRevision = 1 }], (string?)null));
        scopes.Setup(x => x.SelectionAsync("import-1", It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>())).ReturnsAsync((Selection(), (string?)null));
        Services.AddSingleton(scopes.Object);
        _api.Setup(x => x.RunAsync(It.IsAny<TestCoverageReviewRequest>(), It.IsAny<CancellationToken>()))
            .Callback<TestCoverageReviewRequest, CancellationToken>((request, _) => _sent = request)
            .ReturnsAsync((result, (string?)null));
        if (scoped) Services.GetRequiredService<NavigationManager>().NavigateTo($"test-coverage-review?scope={ScopeA}&scope={ScopeB}");
        var cut = Render<TestCoverageReview>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=tco-run]").HasAttribute("disabled").Should().BeFalse());
        cut.Find("[data-testid=tco-run]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=tco-result]").Should().ContainSingle());
        if (scoped) cut.WaitForAssertion(() => cut.FindAll("[data-testid=tco-scope-summary]").Should().ContainSingle());
        return cut;
    }

    private static string Text(IRenderedComponent<TestCoverageReview> cut, string testId) => cut.Find($"[data-testid={testId}]").TextContent.Trim();

    [Fact]
    public void A_candidate_shows_both_chains_and_the_rationale_is_recorded_and_shown_with_the_confirmation()
    {
        var manual = Manual();
        ManualReviewDecisionRequest? sent = null;
        manual.Setup(x => x.RecordAsync(It.IsAny<ManualReviewDecisionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ManualReviewDecisionRequest, CancellationToken>((request, _) => sent = request)
            .ReturnsAsync((ManualReviewDecisionRequest request, CancellationToken _) => ((ManualReviewDecisionView?)Decision(request.Decision, request.Note), (string?)null));
        var cut = Render(Result(), manual);

        cut.Find("[data-testid=tco-tab-journeys]").Click();
        cut.Find("[data-testid=tco-declare-journey]").GetAttribute("href").Should().Be("integration-journeys/declare");
        var candidate = cut.Find("[data-testid=tco-journey-candidate]");
        candidate.QuerySelector("[data-testid=tco-candidate-source-chain]")!.TextContent.Should().Contain("Orders.Adapter → Orders.Api → order-events → Orders.Notifier");
        candidate.QuerySelector("[data-testid=tco-candidate-declared-chain]")!.TextContent.Should().Contain("Orders.Adapter → Orders.Api → order-events → Orders.Notifier");
        cut.FindAll("[data-testid=tco-journey-candidate]").Should().ContainSingle("the similar audit journey has no candidate");

        cut.Find("[data-testid=tco-association-reviewer]").Input("Kari");
        cut.Find("[data-testid=tco-association-rationale]").Input("Same adapter, API, topic and notifier as the declared journey.");
        cut.Find("[data-testid=tco-association-confirm]").Click();

        cut.WaitForAssertion(() => sent.Should().NotBeNull());
        (sent!.SubjectKey, sent.AppliesTo, sent.Decision, sent.Note).Should().Be((SourceJourney, Declared, ManualReviewDecisionValue.Acceptable, "Same adapter, API, topic and notifier as the declared journey."));
        ManualReviewRules.Validate(sent).Should().BeNull();
        cut.WaitForAssertion(() => cut.Find("[data-testid=tco-journey-association-note]").TextContent.Should().Contain("Same adapter"));
        Text(cut, "tco-scope-in-journeys").Should().Be("1", "the confirmed journey is named by two overlapping scopes and counts once");
        Text(cut, "tco-scope-unconfirmed-journeys").Should().Be("1", "the similar audit journey has no candidate: reported as not confirmed, never counted in scope");
    }

    [Fact]
    public void Recorded_decisions_are_read_from_the_store_on_load_and_the_run_names_the_project()
    {
        var manual = Manual(Decision(ManualReviewDecisionValue.Acceptable, "Reviewed in the release meeting."));
        var cut = Render(Result(), manual);

        manual.Verify(x => x.ListAsync(ManualReviewModules.JourneyAssociation, "import-1", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        _sent!.ProjectKey.Should().Be("import-1", "project-declared journeys and project-scoped Test Data references are read for this project");
        Text(cut, "tco-scope-in-journeys").Should().Be("1");
        cut.Find("[data-testid=tco-tab-journeys]").Click();
        cut.Find("[data-testid=tco-journey-association]").GetAttribute("data-kind").Should().Be("Confirmed");
        cut.Find("[data-testid=tco-journey-association-note]").TextContent.Should().Contain("Reviewed in the release meeting.");
        cut.FindAll("[data-testid=tco-journey-candidate]").Should().BeEmpty();
    }

    [Fact]
    public void A_rejected_association_is_excluded_and_its_rationale_is_kept()
    {
        var cut = Render(Result(), Manual(Decision(ManualReviewDecisionValue.IssueFound, "Notifier belongs to another journey.")));

        Text(cut, "tco-scope-in-journeys").Should().Be("0");
        cut.Find("[data-testid=tco-tab-journeys]").Click();
        cut.Find("[data-testid=tco-journey-association]").GetAttribute("data-kind").Should().Be("Rejected");
        cut.Find("[data-testid=tco-journey-association-note]").TextContent.Should().Contain("another journey");
    }

    private static readonly CoverageTestDataLink Resolved = new("tda_customer", "order-customer", "abcdef0123456789", "Test Data consumer reference (Order customer)", "Resolved");

    [Fact]
    public void Qa_tests_show_asset_revision_and_readiness_and_one_asset_counts_once_across_tests_and_scopes()
    {
        var result = Result(
            Qa("t-place", "OrderAcceptanceTests.PlaceOrder_NotifiesCustomer", CoverageDataNeed.Linked, Resolved),
            Qa("t-history", "OrderAcceptanceTests.ListOrders_ReturnsHistory", CoverageDataNeed.Linked, Resolved with { Source = "Test Data consumer reference (History)" }),
            Qa("t-cancel", "OrderAcceptanceTests.CancelOrder_RejectsShippedOrder", CoverageDataNeed.RequiredButMissing) with { DataNeedBasis = "Test Data requirement \"Shipped order\" (JSON payload) is registered for this test, but no asset is bound." },
            Qa("t-other", "OrderAcceptanceTests.Other", CoverageDataNeed.NotAssessed));
        var cut = Render(result, Manual());

        Text(cut, "tco-scope-in-test-data").Should().Be("1", "one asset used by two tests and named by two scopes counts once");
        Text(cut, "tco-scope-unconfirmed-test-data").Should().Be("0");
        cut.Find("[data-testid=tco-tab-duplicates]").Click();
        var needs = cut.FindAll("[data-testid=tco-qa-data-need]").ToDictionary(e => e.GetAttribute("data-test")!, e => e);
        needs["t-place"].GetAttribute("data-state").Should().Be(nameof(CoverageDataNeed.Linked));
        var item = needs["t-place"].QuerySelector("[data-testid=tco-qa-data-link-item]")!;
        (item.GetAttribute("data-asset"), item.GetAttribute("data-resolved")).Should().Be(("tda_customer", "true"));
        item.QuerySelector("[data-testid=tco-qa-data-revision]")!.TextContent.Should().Be("revision abcdef01…");
        item.QuerySelector("[data-testid=tco-qa-data-readiness]")!.TextContent.Should().Be("Resolved");
        needs["t-cancel"].TextContent.Should().Contain("Required but missing").And.Contain("Shipped order");
        needs["t-other"].TextContent.Should().Contain("Not assessed");
        cut.FindAll("[data-testid=tco-asset]").Should().ContainSingle().Which.GetAttribute("data-unresolved").Should().Be("false");
    }

    [Fact]
    public void An_unresolved_revision_is_shown_as_needing_review_and_never_counted_in_scope()
    {
        var stale = Resolved with { State = "NeedsReview", StateReason = "The asset is deprecated." };
        var cut = Render(Result(Qa("t-place", "PlaceOrder", CoverageDataNeed.LinkNeedsReview, stale)), Manual());

        Text(cut, "tco-scope-in-test-data").Should().Be("0", "an unresolved revision is never in scope");
        Text(cut, "tco-scope-unconfirmed-test-data").Should().Be("1");
        cut.Find("[data-testid=tco-tab-duplicates]").Click();
        var need = cut.Find("[data-testid=tco-qa-data-need]");
        need.GetAttribute("data-state").Should().Be(nameof(CoverageDataNeed.LinkNeedsReview));
        need.TextContent.Should().Contain("needs review").And.NotContain("Linked (");
        need.QuerySelector("[data-testid=tco-qa-data-link-item]")!.GetAttribute("data-resolved").Should().Be("false");
        need.QuerySelector("[data-testid=tco-qa-data-readiness]")!.TextContent.Should().Contain("Unresolved").And.Contain("deprecated");
        cut.Find("[data-testid=tco-asset]").GetAttribute("data-unresolved").Should().Be("true");
    }

    [Fact]
    public void Request_or_link_opens_test_data_with_the_qa_test_as_a_project_scoped_consumer()
    {
        var cut = Render(Result(Qa("Orders.Acceptance.Tests::PlaceOrder", "OrderAcceptanceTests.PlaceOrder_NotifiesCustomer", CoverageDataNeed.NotAssessed)), Manual(), scoped: false);

        cut.Find("[data-testid=tco-tab-duplicates]").Click();
        cut.Find("[data-testid=tco-qa-data-type]").Change(nameof(TestDataDataType.JsonPayload));
        var href = cut.Find("[data-testid=tco-qa-data-link]").GetAttribute("href")!;
        href.Should().StartWith("test-data?consumer=");
        href.Should().Contain("consumer=" + Uri.EscapeDataString("import-1/Orders.Acceptance.Tests::PlaceOrder"))
            .And.Contain("consumerType=" + Uri.EscapeDataString(CoverageTestDataConsumers.ConsumerType))
            .And.Contain("consumerName=" + Uri.EscapeDataString("OrderAcceptanceTests.PlaceOrder_NotifiesCustomer"))
            .And.Contain("dataType=JsonPayload");
    }
}
