using BirkNext.ManualReview;
using BirkNext.Scopes;
using BirkNext.SourceEvidence;
using BirkNext.TestCoverage;
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
/// The shared scope filter on Test Coverage &amp; Overlap Review: items are tied to the selected scopes only by stable identity, combined totals
/// count each item once (however many scopes name it), the whole-project numbers stay visible, and an item whose identity cannot be proven is
/// reported as "Scope association not confirmed" — never counted as in scope, never silently dropped.
/// </summary>
public sealed class TestCoverageScopeFilterTests : BunitContext
{
    private readonly Mock<ITestCoverageApiService> _api = new();
    private static readonly Guid Current = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid ScopeA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid ScopeB = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");

    public TestCoverageScopeFilterTests()
    {
        var context = new Mock<IReviewContextProvider>();
        context.Setup(x => x.GetCurrent()).Returns(new ReviewContext());
        Services.AddSingleton(context.Object);
        Services.AddSingleton<IWorkspaceSessionService>(new WorkspaceArtifactRepository());
        Services.AddSingleton(_api.Object);
        Services.AddSingleton(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Projection(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Loaded("Imported", "import-1", "Northwind Orders", WorkspaceArtifactType.Specification)).Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
        _api.Setup(x => x.HistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _api.Setup(x => x.SourcesAsync(It.IsAny<CancellationToken>())).ReturnsAsync((new ReviewSourceOptions
        {
            Snapshots = [new ReviewSourceSnapshot { SnapshotId = Current, RepositoryKey = "northwind", Repository = "northwind", ArchiveName = "northwind.zip", Fingerprint = new string('c', 64),
                AnalyzedAt = DateTimeOffset.UtcNow, SourceStatus = "Ready", Latest = true, HasConsumerEvidence = true }],
        }, (string?)null));
    }

    private static ScopeSelectionMember Member(ScopeMemberKind kind, string id, params Guid[] scopes) =>
        new(new ScopeMember { Kind = kind, TargetId = id, Label = id }, [.. scopes.Select(s => new ScopeContribution(s, s == ScopeA ? "Orders" : "Release", 1))]);

    /// <summary>FR-001 named with its full module/feature path (confirmed), FR-002 only by an unscoped key (not confirmed), FR-003 by no scope.
    /// Flow "orders-e2e" named by both scopes; "flow-elsewhere" named but absent from the review; one Integration Journey; one Test Data asset.</summary>
    private static ScopeSelection Selection() => new()
    {
        ProjectKey = "import-1", Scopes = [new(ScopeA, "Orders", 1), new(ScopeB, "Release", 1)],
        Members =
        [
            Member(ScopeMemberKind.Requirement, "Requirement|orders/001-orders/FR-001", ScopeA),
            Member(ScopeMemberKind.Requirement, "Requirement|FR-002", ScopeB),
            Member(ScopeMemberKind.CriticalE2EFlow, "orders-e2e", ScopeA, ScopeB),
            Member(ScopeMemberKind.CriticalE2EFlow, "flow-elsewhere", ScopeB),
            Member(ScopeMemberKind.Journey, "orders-pack:submit", ScopeA),
            Member(ScopeMemberKind.TestDataAsset, "tda_order", ScopeA),
        ],
    };

    private static BehaviorCoverage Req(string id) => new() { Id = "req:" + id, Title = $"{id} — requirement {id}", RequirementIds = [id], Status = "Partly covered", ForTesters = "Review." };

    private static TestCoverageReviewResult Result() => new()
    {
        CompletedAt = DateTimeOffset.UtcNow, ProjectName = "Northwind Orders", Current = new(Current, "northwind", "northwind.zip", new string('c', 64), DateTimeOffset.UtcNow),
        SpecificationArtifacts = [new CoverageArtifactRef("spec-1", "orders/specs/001-orders/spec.md", "orders", "fp")],
        Inventory = new CoverageInventory { TestsDiscovered = 3, TestsAnalyzed = 3, ExecutionEvidence = TestCoverageText.NoExecution, CodeCoverage = TestCoverageText.NoPercentage },
        Tests =
        [
            new CoverageTest { TestId = "t1", Name = "Create", Project = "Orders.Tests", RequirementIds = ["FR-001"] },
            new CoverageTest { TestId = "t2", Name = "CreateTwice", Project = "Orders.Tests", RequirementIds = ["FR-001", "FR-002"] },
            new CoverageTest { TestId = "t3", Name = "Helper", Project = "Orders.Tests" },
        ],
        QaTests =
        [
            new QaTestCandidate { Id = "e2e:orders-e2e", Title = "Submit order", Source = "Critical E2E flow", Ownership = TestOwnership.QaAutomated },
            new QaTestCandidate { Id = "e2e:other-flow", Title = "Other flow", Source = "Critical E2E flow", Ownership = TestOwnership.QaAutomated },
            new QaTestCandidate { Id = "AS-3", Title = "Cancel order", Source = "Acceptance scenario in the specification", RequirementIds = ["FR-003"] },
        ],
        Journeys = [new Journey { Id = "journey:a>b", Title = "Orders.Api → orders-out", Weakest = ConnectionEvidence.StronglySupported }],
        Behaviors = [Req("FR-001"), Req("FR-002"), Req("FR-003")],
        Overlaps =
        [
            new TestOverlap { Id = "o1", QaTestId = "e2e:orders-e2e", QaTitle = "Submit order", Kind = OverlapKind.HighConfidenceOverlap, Recommendation = "Review.", RemainingRisk = "None known." },
            new TestOverlap { Id = "o2", QaTestId = "AS-3", QaTitle = "Cancel order", Kind = OverlapKind.HighConfidenceOverlap, Recommendation = "Review.", RemainingRisk = "None known." },
        ],
    };

    private IRenderedComponent<TestCoverageReview> RenderScoped(TestCoverageReviewResult? result = null, ScopeSelection? selection = null, Mock<IManualReviewApiService>? manual = null)
    {
        if (manual is not null) Services.AddSingleton(manual.Object);
        var scopes = new Mock<IScopeRegistryApiService>();
        scopes.Setup(x => x.ListAsync("import-1", It.IsAny<CancellationToken>())).ReturnsAsync(([
            new ScopeSummary { Id = ScopeA, Name = "Orders", State = ScopeState.Approved, ApprovedRevision = 1, CurrentRevision = 1 },
            new ScopeSummary { Id = ScopeB, Name = "Release", State = ScopeState.Approved, ApprovedRevision = 1, CurrentRevision = 1 }], (string?)null));
        scopes.Setup(x => x.SelectionAsync("import-1", It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>())).ReturnsAsync((selection ?? Selection(), (string?)null));
        Services.AddSingleton(scopes.Object);
        _api.Setup(x => x.RunAsync(It.IsAny<TestCoverageReviewRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync((result ?? Result(), (string?)null));
        Services.GetRequiredService<NavigationManager>().NavigateTo($"test-coverage-review?scope={ScopeA}&scope={ScopeB}");
        var cut = Render<TestCoverageReview>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=tco-run]").HasAttribute("disabled").Should().BeFalse());
        cut.Find("[data-testid=tco-run]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=tco-scope-summary]").Should().ContainSingle());
        return cut;
    }

    private static string Text(IRenderedComponent<TestCoverageReview> cut, string testId) => cut.Find($"[data-testid={testId}]").TextContent.Trim();

    [Fact]
    public void Scoped_totals_are_deduplicated_and_the_whole_project_stays_visible()
    {
        var cut = RenderScoped();

        Text(cut, "tco-scope-in-requirements").Should().Be("1", "only FR-001 is named with the same module/feature/id path");
        Text(cut, "tco-scope-unconfirmed-requirements").Should().Be("1", "FR-002 is named only by an unscoped id");
        Text(cut, "tco-scope-whole-requirements").Should().Be("3");
        Text(cut, "tco-scope-in-tests").Should().Be("2", "t2 references two scoped requirements and is still one test");
        Text(cut, "tco-scope-whole-tests").Should().Be("3");
        Text(cut, "tco-scope-in-flows").Should().Be("1", "a flow named by both scopes counts once");
        Text(cut, "tco-scope-whole-flows").Should().Be("2");
        Text(cut, "tco-scope-note-flows").Should().Contain("1 named flow is not in this review");
        Text(cut, "tco-scope-dedup").Should().Contain("each item is counted once").And.Contain("1 member is named by more than one scope");
        cut.Find("[data-testid=tco-card-tests]").TextContent.Should().Contain("3", "the whole-project overview cards are unchanged");
    }

    [Fact]
    public void Items_without_a_provable_identity_are_reported_as_not_confirmed_never_as_in_scope()
    {
        var cut = RenderScoped();

        Text(cut, "tco-scope-in-journeys").Should().Be("0", "source journeys and Integration Journey ids share no stable identity");
        Text(cut, "tco-scope-unconfirmed-journeys").Should().Be("1");
        Text(cut, "tco-scope-in-test-data").Should().Be("0");
        Text(cut, "tco-scope-note-test-data").Should().Contain("not linked to any QA test").And.Contain("not assessed for Test Data");

        cut.Find("[data-testid=tco-tab-requirements]").Click();
        var items = cut.FindAll("[data-testid=tco-behavior]");
        items.Select(b => b.QuerySelector("h3")!.TextContent).Should().HaveCount(2).And.NotContain(t => t.Contains("FR-003"), "no scope names FR-003");
        cut.FindAll("[data-testid=tco-behavior-scope-unconfirmed]").Should().ContainSingle().Which.TextContent.Should().Be(TestCoverageScopePresentation.NotConfirmedLabel);

        cut.Find("[data-testid=tco-tab-journeys]").Click();
        cut.Find("[data-testid=tco-journey-scope-unconfirmed]").TextContent.Should().Be(TestCoverageScopePresentation.NotConfirmedLabel);

        cut.Find("[data-testid=tco-tab-duplicates]").Click();
        cut.FindAll("[data-testid=tco-overlap]").Should().ContainSingle("only the overlap of the scoped Critical E2E flow is tied to the scope")
            .Which.TextContent.Should().Contain("Submit order");
    }

    // ── Journey association (exact id or reviewer-confirmed only) and Test Data identity ─────────────────────────────────────

    private const string SourceJourney = "journey:a>b";
    private const string ScopeJourney = "orders-pack:submit";

    private static TestCoverageReviewResult WithCandidate() => Result() with
    {
        Journeys = [new Journey { Id = SourceJourney, Title = "Orders.Api → orders-out", Weakest = ConnectionEvidence.StronglySupported,
            AssociationCandidates = [new JourneyAssociationCandidate(ScopeJourney, "Submit order", "Equal", "Same declared component chain (structure only; needs review).")] }],
    };

    private static ManualReviewDecisionView Decision(ManualReviewDecisionValue value, DateTimeOffset at) => new()
    {
        Id = Guid.NewGuid(), Module = ManualReviewModules.JourneyAssociation, ScopeKey = "import-1", SubjectKey = SourceJourney, AppliesTo = ScopeJourney,
        Reviewer = "Kari", Decision = value, DecidedAt = at, Current = true,
    };

    private static Mock<IManualReviewApiService> Manual(params ManualReviewDecisionView[] decisions)
    {
        var manual = new Mock<IManualReviewApiService>();
        manual.Setup(x => x.ListAsync(ManualReviewModules.JourneyAssociation, "import-1", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ManualReviewDecisionView>?)decisions.ToList());
        return manual;
    }

    [Fact]
    public void A_confirmed_association_counts_the_journey_in_scope()
    {
        var cut = RenderScoped(WithCandidate(), manual: Manual(Decision(ManualReviewDecisionValue.Acceptable, DateTimeOffset.UtcNow)));

        Text(cut, "tco-scope-in-journeys").Should().Be("1");
        Text(cut, "tco-scope-unconfirmed-journeys").Should().Be("0");
        cut.Find("[data-testid=tco-tab-journeys]").Click();
        cut.Find("[data-testid=tco-journey-scope-in]").GetAttribute("data-reason").Should().Be(nameof(JourneyScopeReason.ConfirmedAssociation));
        cut.Find("[data-testid=tco-journey-association]").GetAttribute("data-kind").Should().Be("Confirmed");
        cut.FindAll("[data-testid=tco-journey-candidate]").Should().BeEmpty("a decided candidate is shown as its decision, not as a proposal");
    }

    [Fact]
    public void A_structural_candidate_is_shown_for_review_but_never_counted()
    {
        var cut = RenderScoped(WithCandidate(), manual: Manual());

        Text(cut, "tco-scope-in-journeys").Should().Be("0");
        Text(cut, "tco-scope-unconfirmed-journeys").Should().Be("1");
        cut.Find("[data-testid=tco-tab-journeys]").Click();
        var candidate = cut.Find("[data-testid=tco-journey-candidate]");
        candidate.GetAttribute("data-target").Should().Be(ScopeJourney);
        candidate.QuerySelector("[data-testid=tco-candidate-label]")!.TextContent.Should().Be(TestCoverageScopePresentation.CandidateLabel);
        cut.Find("[data-testid=tco-journey-scope-reason]").GetAttribute("data-reason").Should().Be(nameof(JourneyScopeReason.CandidateNotReviewed));
        cut.Find("[data-testid=tco-association-confirm]").HasAttribute("disabled").Should().BeTrue("a reviewer name is required");
    }

    [Fact]
    public void A_rejected_association_is_never_counted_even_after_an_earlier_confirmation()
    {
        var now = DateTimeOffset.UtcNow;
        var cut = RenderScoped(WithCandidate(), manual: Manual(Decision(ManualReviewDecisionValue.Acceptable, now.AddMinutes(-5)), Decision(ManualReviewDecisionValue.IssueFound, now)));

        Text(cut, "tco-scope-in-journeys").Should().Be("0", "the latest decision for the pair is a rejection");
        Text(cut, "tco-scope-unconfirmed-journeys").Should().Be("1");
        cut.Find("[data-testid=tco-tab-journeys]").Click();
        cut.Find("[data-testid=tco-journey-scope-reason]").GetAttribute("data-reason").Should().Be(nameof(JourneyScopeReason.Rejected));
        cut.Find("[data-testid=tco-journey-association]").GetAttribute("data-kind").Should().Be("Rejected");
    }

    [Fact]
    public void Confirming_a_candidate_records_the_decision_for_the_source_journey_and_the_integration_journey()
    {
        var manual = Manual();
        ManualReviewDecisionRequest? sent = null;
        manual.Setup(x => x.RecordAsync(It.IsAny<ManualReviewDecisionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ManualReviewDecisionRequest, CancellationToken>((request, _) => sent = request)
            .ReturnsAsync((ManualReviewDecisionRequest request, CancellationToken _) => ((ManualReviewDecisionView?)Decision(request.Decision, DateTimeOffset.UtcNow), (string?)null));
        var cut = RenderScoped(WithCandidate(), manual: manual);

        cut.Find("[data-testid=tco-tab-journeys]").Click();
        cut.Find("[data-testid=tco-association-reviewer]").Input("Kari");
        cut.Find("[data-testid=tco-association-confirm]").Click();

        cut.WaitForAssertion(() => sent.Should().NotBeNull());
        sent!.Module.Should().Be(ManualReviewModules.JourneyAssociation);
        (sent.ScopeKey, sent.SubjectKey, sent.AppliesTo, sent.Decision, sent.Reviewer).Should().Be(("import-1", SourceJourney, ScopeJourney, ManualReviewDecisionValue.Acceptable, "Kari"));
        ManualReviewRules.Validate(sent).Should().BeNull();
        cut.WaitForAssertion(() => Text(cut, "tco-scope-in-journeys").Should().Be("1"));
    }

    [Fact]
    public void Rejecting_a_candidate_records_issue_found_with_a_note()
    {
        var manual = Manual();
        ManualReviewDecisionRequest? sent = null;
        manual.Setup(x => x.RecordAsync(It.IsAny<ManualReviewDecisionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ManualReviewDecisionRequest, CancellationToken>((request, _) => sent = request)
            .ReturnsAsync((ManualReviewDecisionRequest request, CancellationToken _) => ((ManualReviewDecisionView?)Decision(request.Decision, DateTimeOffset.UtcNow), (string?)null));
        var cut = RenderScoped(WithCandidate(), manual: manual);

        cut.Find("[data-testid=tco-tab-journeys]").Click();
        cut.Find("[data-testid=tco-association-reviewer]").Input("Kari");
        cut.Find("[data-testid=tco-association-reject]").Click();

        cut.WaitForAssertion(() => sent.Should().NotBeNull());
        (sent!.SubjectKey, sent.AppliesTo, sent.Decision).Should().Be((SourceJourney, ScopeJourney, ManualReviewDecisionValue.IssueFound));
        ManualReviewRules.Validate(sent).Should().BeNull("a rejection carries the required note");
        Text(cut, "tco-scope-in-journeys").Should().Be("0");
    }

    [Fact]
    public void Test_data_assets_are_linked_by_id_and_counted_once_across_scopes_and_tests()
    {
        var link = new CoverageTestDataLink("tda_order", "order", "fp-1", "Critical E2E flow definition");
        var result = Result() with
        {
            QaTests =
            [
                new QaTestCandidate { Id = "e2e:orders-e2e", Title = "Submit order", Source = "Critical E2E flow", Ownership = TestOwnership.QaAutomated, TestData = [link], DataNeed = CoverageDataNeed.Linked },
                new QaTestCandidate { Id = "e2e:other-flow", Title = "Other flow", Source = "Critical E2E flow", Ownership = TestOwnership.QaAutomated,
                    TestData = [link, new CoverageTestDataLink("tda_other", null, null, "Test Plan draft")], DataNeed = CoverageDataNeed.Linked },
                new QaTestCandidate { Id = "AS-3", Title = "Cancel order", Source = "Acceptance scenario in the specification", RequirementIds = ["FR-003"] },
                new QaTestCandidate { Id = "e2e:cancel", Title = "Cancel flow", Source = "Critical E2E flow", DataNeed = CoverageDataNeed.RequiredButMissing, DataNeedBasis = "Describes required test data but links no Test Data asset." },
            ],
        };
        var selection = Selection() with
        {
            Members = [.. Selection().Members.Where(m => m.Member.Kind != ScopeMemberKind.TestDataAsset), Member(ScopeMemberKind.TestDataAsset, "tda_order", ScopeA, ScopeB)],
        };
        var cut = RenderScoped(result, selection);

        Text(cut, "tco-scope-in-test-data").Should().Be("1", "tda_order is named by two scopes and used by two tests, and counts once");
        Text(cut, "tco-scope-whole-test-data").Should().Be("2");
        cut.Find("[data-testid=tco-tab-duplicates]").Click();
        var assets = cut.FindAll("[data-testid=tco-asset]");
        assets.Select(a => a.GetAttribute("data-asset")).Should().Equal("tda_order", "tda_other");
        var order = assets[0];
        order.GetAttribute("data-in-scope").Should().Be("true");
        order.TextContent.Should().Contain("Submit order").And.Contain("Other flow");
        order.QuerySelector("[data-testid=tco-asset-scopes]")!.TextContent.Should().Contain("Orders").And.Contain("Release");
        assets[1].GetAttribute("data-in-scope").Should().Be("false");

        var needs = cut.FindAll("[data-testid=tco-qa-data-need]").ToDictionary(e => e.GetAttribute("data-test")!, e => e);
        needs["e2e:orders-e2e"].TextContent.Should().Contain("Linked (tda_order)");
        needs["e2e:cancel"].TextContent.Should().Contain("Required but missing");
        needs["AS-3"].GetAttribute("data-state").Should().Be(nameof(CoverageDataNeed.NotAssessed));
        needs["AS-3"].TextContent.Should().Contain("Not assessed", "no link is never read as 'no data needed'");
    }

    [Fact]
    public void Clearing_the_scope_restores_every_item()
    {
        var cut = RenderScoped();
        cut.Find("[data-testid=scf-clear]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=tco-scope-summary]").Should().BeEmpty());
        cut.Find("[data-testid=tco-tab-requirements]").Click();
        cut.FindAll("[data-testid=tco-behavior]").Should().HaveCount(3);
        cut.FindAll("[data-testid=tco-behavior-scope-unconfirmed]").Should().BeEmpty();
    }

    [Fact]
    public void The_requirement_rule_needs_the_full_module_feature_id_path()
    {
        var selection = Selection();
        TestCoverageScopePresentation.Requirement("FR-001", selection, "orders", "001-orders").Association.Should().Be(CoverageScopeAssociation.InScope);
        TestCoverageScopePresentation.Requirement("fr-1", selection, "orders", "001-orders").Association.Should().Be(CoverageScopeAssociation.InScope, "the shared requirement id normalization applies to both sides");
        TestCoverageScopePresentation.Requirement("FR-001", selection, null, null).Association.Should().Be(CoverageScopeAssociation.NotConfirmed, "without a module/feature the path cannot be proven");
        TestCoverageScopePresentation.Requirement("FR-001", selection, "billing", "002-billing").Association.Should().Be(CoverageScopeAssociation.NotConfirmed, "another path is reported, not silently excluded");
        TestCoverageScopePresentation.Requirement("FR-003", selection, "orders", "001-orders").Association.Should().Be(CoverageScopeAssociation.OutOfScope);
        TestCoverageScopePresentation.SpecificationPath(Result()).Should().Be(("orders", "001-orders"));
    }
}
