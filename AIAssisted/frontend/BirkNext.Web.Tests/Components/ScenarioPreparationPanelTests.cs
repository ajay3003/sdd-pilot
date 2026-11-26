using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using BirkNext.Web.Components.ManualTesting;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>«Klargjør AC» (adaptive readiness, 2026-11-26): required / optional / not applicable / undecided groups, a real editor per item that
/// saves through the grouping document, «Ikke relevant» needs a reason and is never «Oppfylt», cases are optional, opening never writes.</summary>
public sealed class ScenarioPreparationPanelTests : BunitContext
{
    private const string Spec = "M/specs/001-x/spec.md";
    private readonly Mock<ITestPlanSuggestionsApiService> _api = new();
    private StoryGroupingDocument _doc = new() { ProjectKey = "m2lb", SpecificationPath = Spec, Revision = 3 };
    private readonly List<StoryGroupingChangeRequest> _saved = [];

    public ScenarioPreparationPanelTests()
    {
        Services.AddSingleton(_api.Object);
        _api.Setup(x => x.StoryGroupingAsync("m2lb", It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => (_doc, (string?)null));
        _api.Setup(x => x.ApplyStoryGroupingAsync("m2lb", It.IsAny<string>(), It.IsAny<StoryGroupingChangeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, StoryGroupingChangeRequest r, CancellationToken _) =>
            {
                _saved.Add(r);
                if (r.ExpectedRevision != _doc.Revision) return ((StoryGroupingDocument?)null, (string?)"Grupperingen er endret av noen andre.");
                _doc = StoryGroupingEngine.Apply(_doc, r.Operations, "tester", DateTimeOffset.UtcNow, _ => true);
                return (_doc, null);
            });
    }

    private static DiscoveredScenario Ac(string id = "c1", CoverageUserStory? story = null) => new()
    {
        CandidateId = id, SourceIdentity = "tss-" + id, CapabilityId = "specification", CanonicalKey = $"scenario:M|{id}", Title = "Rapport avvises", Fingerprint = "fp1",
        ExpectedBehaviour = "Rapporten avvises", NumberInStory = 2,
        Template = [new("given", "Given", "et barn uten personkobling"), new("when", "When", "saksbehandler åpner rapporten"), new("then", "Then", "Rapporten avvises")],
        Story = story ?? new CoverageUserStory { Key = "k2", StoryId = "US2", Title = "T", SpecificationPath = Spec },
    };

    private IRenderedComponent<ScenarioPreparationPanel> Render(DiscoveredScenario s, IReadOnlyList<TestCaseDraft>? cases = null) =>
        Render<ScenarioPreparationPanel>(p => p.Add(x => x.ProjectKey, "m2lb").Add(x => x.Scenario, s).Add(x => x.Cases, cases ?? []));

    private static string[] Group(IRenderedComponent<ScenarioPreparationPanel> cut, string g) =>
        [.. cut.FindAll($"[data-testid=spp-group][data-group={g}] [data-testid=spp-item]").Select(i => i.GetAttribute("data-criterion")!)];

    [Fact]
    public void An_AC_without_cases_shows_its_own_preparation_grouped_by_relevance_and_opening_never_writes()
    {
        var cut = Render(Ac());
        cut.WaitForAssertion(() => cut.Find("[data-testid=spp-status]"));
        cut.Find("[data-testid=spp-id]").TextContent.Should().Be("US2 · AC 2");
        cut.Find(".spp-title").TextContent.Should().StartWith("Klargjør AC");
        cut.Find("[data-testid=spp-status]").TextContent.Should().Be("Må vurderes");
        cut.Find("[data-testid=spp-count]").TextContent.Should().Be("0 av 2 påkrevde punkter oppfylt");
        // V5: «0 av 2» is not final while three relevance decisions are open.
        cut.Find("[data-testid=spp-undecided]").TextContent.Should().Be("3 relevansvurderinger gjenstår – antallet påkrevde punkter kan endres.");
        Group(cut, "required").Should().Equal("ExpectedBehaviour", "Verification");
        Group(cut, "undecided").Should().Equal("TestData", "Trigger", "Environment");
        Group(cut, "optional").Should().Equal("CaseReview");
        cut.Find("[data-testid=spp-item][data-criterion=TestData] [data-testid=spp-suggested]").TextContent.Should().StartWith("Foreslått: Påkrevd");
        cut.Find("[data-testid=spp-item][data-criterion=CaseReview] [data-testid=spp-evidence]").TextContent.Should().Contain("kan testes uten");
        cut.Find("[data-testid=spp-item][data-criterion=TestData] [data-testid=spp-act]").TextContent.Should().Be("Angi testdata");
        _api.Verify(x => x.ApplyStoryGroupingAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<StoryGroupingChangeRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Walkthrough_A_optional_test_data_not_applicable_environment_required_verification_saves_and_updates_without_a_case()
    {
        var cut = Render(Ac());
        cut.WaitForAssertion(() => cut.Find("[data-testid=spp-status]"));

        cut.Find("[data-testid=spp-item][data-criterion=TestData] [data-testid=spp-act]").Click();
        cut.Find("[data-testid=spp-applicability][data-value=Optional]").Change(true);
        cut.Find("[data-testid=spp-save]").Click();

        cut.Find("[data-testid=spp-item][data-criterion=Environment] [data-testid=spp-act]").Click();
        cut.Find("[data-testid=spp-applicability][data-value=NotApplicable]").Change(true);
        cut.Find("[data-testid=spp-save]").Click();
        cut.Find("[data-testid=spp-error]").TextContent.Should().Contain("hvorfor", "«Ikke relevant» needs a reason; nothing was sent");
        _saved.Should().HaveCount(1);
        cut.Find("[data-testid=spp-reason-input]").Input("Kan gjennomføres med ordinær testtilgang.");
        cut.Find("[data-testid=spp-save]").Click();

        cut.Find("[data-testid=spp-item][data-criterion=Verification] [data-testid=spp-act]").Click();
        cut.Find("[data-testid=spp-evaluation][data-value=Satisfied]").Change(true);
        cut.Find("[data-testid=spp-evidence-input]").Input("Avvisningen vises i rapportlisten");
        cut.Find("[data-testid=spp-save]").Click();

        _saved.Should().HaveCount(3).And.OnlyContain(r => r.Operations.All(o => o.Kind == StoryGroupingOperationKind.SetScenarioPreparation && o.ScenarioId == "tss-c1" && o.ScenarioFingerprint == "fp1"));
        _saved.Select(r => r.ExpectedRevision).Should().Equal(new[] { 3, 4, 5 }, "each save names the revision it started from");
        Group(cut, "na").Should().Equal("Environment");
        var env = cut.Find("[data-testid=spp-item][data-criterion=Environment]");
        env.QuerySelector("[data-testid=spp-state]")!.TextContent.Should().Be("Ikke relevant");
        env.GetAttribute("data-evaluation").Should().Be("NotEvaluated", "not applicable is never shown as satisfied");
        env.QuerySelector("[data-testid=spp-reason]")!.TextContent.Should().Contain("ordinær testtilgang");
        Group(cut, "optional").Should().Contain("TestData");
        cut.Find("[data-testid=spp-item][data-criterion=Verification] [data-testid=spp-state]").TextContent.Should().Be("Påkrevd · Oppfylt");
        cut.Find("[data-testid=spp-count]").TextContent.Should().Be("1 av 2 påkrevde punkter oppfylt");
        cut.Find("[data-testid=spp-status]").TextContent.Should().Be("Må vurderes", "the trigger's relevance is still undecided and the expected result is not confirmed");
        _api.Verify(x => x.AddManualCaseAsync(It.IsAny<Guid>(), It.IsAny<AddManualTestCaseRequest>(), It.IsAny<CancellationToken>()), Times.Never, "no case is created");
    }

    [Fact]
    public void Suggestions_are_confirmed_explicitly_a_missing_required_item_needs_preparation_and_optional_items_never_block()
    {
        var cut = Render(Ac());
        cut.WaitForAssertion(() => cut.Find("[data-testid=spp-accept-suggestions]"));
        cut.Find("[data-testid=spp-accept-suggestions]").Click();
        _saved.Single().Operations.Select(o => (o.Criterion, o.Applicability)).Should().Equal(
            (PreparationCriterion.TestData, CriterionApplicability.Required), (PreparationCriterion.Trigger, CriterionApplicability.Optional),
            (PreparationCriterion.Environment, CriterionApplicability.Optional));
        cut.FindAll("[data-testid=spp-group][data-group=undecided]").Should().BeEmpty();

        cut.Find("[data-testid=spp-item][data-criterion=TestData] [data-testid=spp-act]").Click();
        cut.Find("[data-testid=spp-evaluation][data-value=Unsatisfied]").Change(true);
        cut.Find("[data-testid=spp-save]").Click();
        cut.Find("[data-testid=spp-status]").TextContent.Should().Be("Trenger klargjøring");
        cut.Find("[data-testid=spp-count]").TextContent.Should().Be("0 av 3 påkrevde punkter oppfylt");
        cut.Find("[data-testid=spp-item][data-criterion=TestData] [data-testid=spp-act]").TextContent.Should().Be("Angi testdata", "an incomplete item keeps its action");

        foreach (var c in new[] { "TestData", "ExpectedBehaviour", "Verification" })
        {
            cut.Find($"[data-testid=spp-item][data-criterion={c}] [data-testid=spp-act]").Click();
            cut.Find("[data-testid=spp-evaluation][data-value=Satisfied]").Change(true);
            cut.Find("[data-testid=spp-save]").Click();
        }
        cut.Find("[data-testid=spp-status]").TextContent.Should().Be("Klar til kjøring", "optional trigger, environment and case review do not block");
        cut.Find("[data-testid=spp-optional-open]").TextContent.Should().Be("3 anbefalte forbedringer", "V5: the group is «Anbefalt før testing»");
    }

    [Fact]
    public void Cancel_discards_the_edit_an_approved_case_is_evidence_not_readiness_and_a_standalone_test_says_Klargjor_scenario()
    {
        var approved = new TestCaseDraft { Source = new ScenarioReference { ScenarioId = "c1" }, Review = TestCaseReviewState.Accepted, Generated = new TestCaseContent { Title = "A", ExpectedResult = "Rapporten avvises" } };
        var cut = Render(Ac(), [approved]);
        cut.WaitForAssertion(() => cut.Find("[data-testid=spp-status]"));
        cut.Find("[data-testid=spp-item][data-criterion=CaseReview] [data-testid=spp-state]").TextContent.Should().Be("Valgfritt · Oppfylt");
        cut.Find("[data-testid=spp-item][data-criterion=ExpectedBehaviour] [data-testid=spp-evidence]").TextContent.Should().Contain("godkjent testtilfelle");
        cut.Find("[data-testid=spp-status]").TextContent.Should().Be("Må vurderes", "an approved case does not make the scenario ready");

        cut.Find("[data-testid=spp-item][data-criterion=Trigger] [data-testid=spp-act]").Click();
        cut.Find("[data-testid=spp-cancel]").Click();
        cut.FindAll("[data-testid=spp-editor]").Should().BeEmpty();
        _saved.Should().BeEmpty();

        var standalone = new DiscoveredScenario { CandidateId = "e1", SourceIdentity = "tss-e1", CapabilityId = "test-specification", CanonicalKey = "testspec:H/docs/H – Testspesifikasjon.md#TEST-E-01",
            Title = "TEST-E-01: Full flyt", Type = ScenarioCandidateType.BusinessE2E, ExpectedBehaviour = "Hendelsen er synlig", Template = [new("when", "When", "PUT /innmating/inngrep/42")] };
        var e2e = Render(standalone);
        e2e.WaitForAssertion(() => e2e.Find("[data-testid=spp-status]"));
        e2e.Find(".spp-title").TextContent.Should().StartWith("Klargjør scenario");
        e2e.Find("[data-testid=spp-id]").TextContent.Should().Be("TEST-E-01");
        _api.Verify(x => x.StoryGroupingAsync("m2lb", "H/docs/H – Testspesifikasjon.md", It.IsAny<CancellationToken>()), Times.Once, "its own document; no story is invented");
        Group(e2e, "required").Should().Equal(new[] { "ExpectedBehaviour", "Verification" }, "no universal mandatory E2E checklist");
    }

    [Fact]
    public void A_save_refused_because_someone_else_saved_first_reloads_and_keeps_the_open_editor()
    {
        var cut = Render(Ac());
        cut.WaitForAssertion(() => cut.Find("[data-testid=spp-status]"));
        cut.Find("[data-testid=spp-item][data-criterion=Verification] [data-testid=spp-act]").Click();
        _doc = _doc with { Revision = 9 };   // another tester saved meanwhile
        cut.Find("[data-testid=spp-evaluation][data-value=Satisfied]").Change(true);
        cut.Find("[data-testid=spp-save]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=spp-error]").TextContent.Should().Contain("ikke lagret"));
        cut.FindAll("[data-testid=spp-editor]").Should().ContainSingle("the tester's open edit is kept to retry");
        cut.Find("[data-testid=spp-save]").Click();
        _saved.Select(r => r.ExpectedRevision).Should().Equal(new[] { 3, 9 }, "the retry uses the reloaded revision — no silent last-write-wins");
    }
}
