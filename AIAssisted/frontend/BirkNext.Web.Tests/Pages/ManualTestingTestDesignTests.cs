using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Moq;
using S = BirkNext.Web.Services.ScenarioBrowserPresentation;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// M — Testdesign og klargjøring in Planlegg og klargjør (2026-11-24): approach, readiness, verification, test data and proposals in the
/// existing editor; accept = the existing add-from-variant call (no duplicate cases), reject/defer saved with the plan; the same panel in
/// every language view; nothing is approved, executed or rewritten. Real SkoleService wording.
/// </summary>
public sealed partial class ManualTestingPlanTests
{
    private const string Inconsistent = "it is accepted and flagged \"inconsistent hour totals\".";

    private static TestCaseDraft ThresholdScenario(TestCaseReviewState review = TestCaseReviewState.Generated) => Case("c1", "US1 scenario 4", expected: Inconsistent, review: review) with
    {
        Generated = Case("c1", "US1 scenario 4", expected: Inconsistent).Generated with
        {
            Preconditions = "a report whose absence hours sum differs from planned minus actual hours by more than 0.5 hours",
            Steps = ["Send the report in as a private institution or a foster home."], StepExpected = [Inconsistent],
            Template = [new("given", "Given", "a report whose absence hours sum differs from planned minus actual hours by more than 0.5 hours"), new("when", "When", "it arrives"), new("then", "Then", Inconsistent)],
            OpenQuestions = ["How the report is sent in the test environment is not stated in the source — clarify before execution."],
            Story = Story6,
        },
    };

    [Fact]
    public void M01_the_case_shows_its_approach_readiness_verification_and_source_grounded_proposals()
    {
        Setup(Plan(ThresholdScenario()));
        var cut = Open();

        cut.FindAll("[data-testid=tdp-approach]").Select(a => a.GetAttribute("data-approach")).Should().Contain("ManualTechnical").And.NotContain("Manual");
        cut.Find("[data-testid=tdp-readiness-level]").TextContent.Should().Be("Klar til forberedelse");
        cut.FindAll("[data-testid=tdp-check]").Single(c => c.TextContent.Contains("Utløser")).GetAttribute("data-satisfied").Should().Be("Unknown");
        cut.Find("[data-testid=tdp-point-method]").TextContent.Should().Be("Verifisering kreves — metode ikke bekreftet");
        var proposals = cut.FindAll("[data-testid=tdp-proposal]");
        proposals.Should().HaveCount(3).And.OnlyContain(p => p.TextContent.Contains("strengt større enn 0.5"));
        cut.FindAll("[data-testid=tdp-proposal-expected]").Count(e => e.TextContent.Contains("ikke oppgitt i kilden")).Should().Be(2, "only the side the source states has an expected result");
    }

    [Fact]
    public void M02_accepting_a_proposal_uses_the_existing_variant_path_and_opens_the_new_draft()
    {
        var scenario = ThresholdScenario();
        Setup(Plan(scenario));
        AddManualTestCaseRequest? sent = null;
        var added = Case("m9", "US1 scenario 4 — grenseverdi: like innenfor 0.5", kind: ScenarioSourceKind.Manual, scenarioKey: "c1");
        _api.Setup(x => x.AddManualCaseAsync(PlanId, It.IsAny<AddManualTestCaseRequest>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, AddManualTestCaseRequest, CancellationToken>((_, r, _) => sent = r).ReturnsAsync((Plan(scenario, added) with { Revision = 4 }, (string?)null));
        var cut = Open();

        var proposal = cut.FindAll("[data-testid=tdp-proposal]").First(p => p.TextContent.Contains("like innenfor"));
        proposal.QuerySelector("[data-testid=tdp-accept]")!.Click();

        sent!.Variant.Should().StartWith("boundary:");
        sent.ScenarioKey.Should().Be("c1");
        sent.BaseRevision.Should().Be(3);
        cut.Find("[data-testid=mtp-title]").GetAttribute("value").Should().Be("US1 scenario 4 — grenseverdi: like innenfor 0.5", "the new draft opens for editing");
        _api.Verify(x => x.SaveAsync(It.IsAny<Guid>(), It.IsAny<SaveTestPlanDraftRequest>(), It.IsAny<CancellationToken>()), Times.Never(), "accepting approves nothing");
    }

    [Fact]
    public void M03_rejecting_saves_a_decision_and_hides_the_proposal_while_everything_else_is_sent_as_stored()
    {
        var scenario = ThresholdScenario() with { Edits = new TestCaseEdits { Title = "Edited by Anna" } };
        Setup(Plan(scenario));
        SaveTestPlanDraftRequest? sent = null;
        _api.Setup(x => x.SaveAsync(PlanId, It.IsAny<SaveTestPlanDraftRequest>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, SaveTestPlanDraftRequest, CancellationToken>((_, r, _) => sent = r)
            .ReturnsAsync(() => (Plan(scenario) with { Revision = 4, DesignDecisions = sent!.DesignDecisions }, (string?)null));
        var cut = Open();
        var key = cut.FindAll("[data-testid=tdp-proposal]")[0].GetAttribute("data-key");

        cut.FindAll("[data-testid=tdp-reject]")[0].Click();

        sent!.DesignDecisions.Should().ContainSingle(d => d.ProposalKey == key && d.Decision == TestDesignDecisions.Rejected && d.ScenarioKey == "c1");
        sent.Cases.Single(c => c.ScenarioId == "c1").Edits.Title.Should().Be("Edited by Anna");
        cut.FindAll("[data-testid=tdp-proposal]").Should().HaveCount(2);
        cut.Find("[data-testid=tdp-proposals-rejected]").TextContent.Should().Contain("1 avviste");
    }

    [Fact]
    public void M04_deferring_keeps_the_proposal_visible_and_marked()
    {
        var scenario = ThresholdScenario();
        var key = ScenarioPlanning.Variants(scenario, [])[0].Key;
        Setup(Plan(scenario) with { DesignDecisions = [new TestDesignDecision { ScenarioKey = "c1", ProposalKey = key, Decision = TestDesignDecisions.Deferred, By = "anna" }] });
        var cut = Open();

        var first = cut.FindAll("[data-testid=tdp-proposal]")[0];
        first.QuerySelector("[data-testid=tdp-decision]")!.TextContent.Should().Be("Utsatt av anna");
        first.QuerySelector("[data-testid=tdp-defer]").Should().BeNull();
        cut.FindAll("[data-testid=tdp-proposal]").Should().HaveCount(3);
    }

    [Fact]
    public void M04b_when_every_proposal_is_rejected_the_count_of_hidden_proposals_stays_visible()
    {
        var scenario = ThresholdScenario();
        var decisions = ScenarioPlanning.Variants(scenario, []).Select(v => new TestDesignDecision { ScenarioKey = "c1", ProposalKey = v.Key, Decision = TestDesignDecisions.Rejected, By = "anna" }).ToList();
        Setup(Plan(scenario) with { DesignDecisions = decisions });
        var cut = Open();

        cut.FindAll("[data-testid=tdp-proposal]").Should().BeEmpty();
        cut.Find("[data-testid=tdp-proposals-rejected]").TextContent.Should().Contain($"{decisions.Count} avviste");
    }

    [Fact]
    public void M05_the_panel_is_the_same_in_every_language_view_and_needs_no_new_request()
    {
        Setup(Plan(Translated(ThresholdScenario())));
        var cut = Open();
        string Panel() => cut.Find("[data-testid=tdp]").TextContent;

        var norsk = Panel();
        Choose(cut, EditorLanguage.Original);
        Panel().Should().Be(norsk);
        Choose(cut, EditorLanguage.Compare);
        Panel().Should().Be(norsk);
        _api.Verify(x => x.LastDiscoveryAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once(), "switching language never re-reads the source");
    }

    [Fact]
    public void M06_a_read_only_plan_shows_the_advice_but_offers_no_decisions()
    {
        Setup(Plan(ThresholdScenario()), canWrite: false);
        var cut = Open();
        cut.FindAll("[data-testid=tdp-proposal]").Should().NotBeEmpty();
        cut.FindAll("[data-testid=tdp-accept]").Should().BeEmpty();
    }

    [Fact]
    public void M06_unsaved_edits_block_decisions_but_the_draft_stays_savable()
    {
        Setup(Plan(ThresholdScenario()));
        var writable = Open();
        writable.Find("[data-testid=mtp-title]").Input("Endret");
        writable.FindAll("[data-testid=tdp-accept]").Should().BeEmpty("save first: a decision never mixes with unsaved edits");
        writable.Find("[data-testid=mtp-save]").HasAttribute("disabled").Should().BeFalse("the draft stays savable");
    }

    // ── Finn og forstå: approach as compact information and an optional filter ─────────────────────────────────────

    [Fact]
    public void M07_the_approach_filter_narrows_without_replacing_the_queues()
    {
        var manual = new DiscoveredScenario { CandidateId = "a", Title = "Search", CapabilityId = ScenarioOwnership.Specification, ExpectedBehaviour = "a list of matching children appears",
            Steps = [new DiscoveredStep("they type a child's name in the search field", null)], Preconditions = ["the caseworker is on the child search page"] };
        var arrival = new DiscoveredScenario { CandidateId = "b", Title = "Valid report", CapabilityId = ScenarioOwnership.Specification,
            ExpectedBehaviour = "it is accepted, stored against the child's M2LB identity, and the child's fødselsnummer is not stored, published or logged",
            Steps = [new DiscoveredStep("Send the report in as a private institution or a foster home.", null)],
            Template = [new("when", "When", "it arrives")] };
        var items = S.Items([manual, arrival], new Dictionary<string, ScenarioWorkItem>());

        S.Apply(items, new S.Filter(All: true, Approach: ExecutionApproach.Manual)).Shown.Select(i => i.Id).Should().Equal("a");
        S.Apply(items, new S.Filter(All: true, Approach: ExecutionApproach.ManualTechnical)).Shown.Select(i => i.Id).Should().Equal("b");
        S.Apply(items, new S.Filter(All: true)).Shown.Should().HaveCount(2, "no approach filter hides nothing");
        S.AdvancedFilterCount(new S.Filter(Approach: ExecutionApproach.Manual)).Should().Be(1);
        S.ResetFilters(new S.Filter(Queue: ScenarioWorkQueue.Recommended, Approach: ExecutionApproach.Manual)).Should().Be(new S.Filter(Queue: ScenarioWorkQueue.Recommended), "the queue stays");
        S.Queues.Select(q => q.Label).Should().Equal("Anbefalt å forberede", "Trenger avklaring", "Allerede forberedt", "Kildegrunnlag endret", "Alle scenarioer");
    }

    [Fact]
    public void M08_the_approach_filter_stays_fast_on_an_M2LB_sized_discovery_and_is_computed_once_per_scenario()
    {
        var candidates = Enumerable.Range(0, 600).Select(i => new DiscoveredScenario
        {
            CandidateId = $"s{i}", Title = $"Scenario {i}", CapabilityId = ScenarioOwnership.Specification,
            ExpectedBehaviour = i % 3 == 0 ? "a list of matching children appears" : "it is accepted, stored against the child's M2LB identity, and the national identifier is not logged",
            Steps = [new DiscoveredStep(i % 3 == 0 ? "they type a child's name in the search field" : "Send the report in as a private institution.", null)],
            Template = [new("when", "When", i % 3 == 0 ? "they type a child's name" : "it arrives")],
        }).ToList();
        var items = S.Items(candidates, new Dictionary<string, ScenarioWorkItem>());
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var manual = S.Apply(items, new S.Filter(All: true, Approach: ExecutionApproach.Manual)).Shown.Count;
        var first = sw.ElapsedMilliseconds;
        foreach (var a in Enum.GetValues<ExecutionApproach>()) S.Apply(items, new S.Filter(All: true, Approach: a));
        sw.Stop();

        manual.Should().Be(200);
        // The property itself, not wall-clock time (a timing budget failed under full-suite CPU load while the test alone takes ~0.1 s):
        // each scenario is classified once and every later filter reads the same classification.
        items.Should().OnlyContain(i => ReferenceEquals(i.Suitability, i.Suitability));
        var before = items.Select(i => i.Suitability).ToList();
        S.Apply(items, new S.Filter(All: true, Approach: ExecutionApproach.AutomatedRecommended));
        items.Select(i => i.Suitability).Should().Equal(before, (a, b) => ReferenceEquals(a, b), "a later filter reuses the per-item classification");
        first.Should().BeLessThan(10_000, "a guard against pathological slowness only; alone this is ~0.1 s");
    }
}
