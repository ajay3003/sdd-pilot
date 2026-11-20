using BirkNext.TestPlanning;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Explorers;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using C = BirkNext.Web.Services.ScenarioCentricPresentation;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Plan &amp; prepare by scenario: scenarios are the parents (one open at a time) with their test cases beneath; a tester adds or duplicates a case
/// under a scenario, sees suggested requirements with their reason and evidence strength and confirms or rejects them, translates a scenario
/// (or writes Norwegian by hand when no service is configured) without losing the original, and always sees whether the plan is saved.
/// Synthetic project shaped like M2LB's SkoleService specification (own wording).
/// </summary>
public sealed class ScenarioCentricPrepareTests : BunitContext
{
    private const string Spec = "SkoleService/specs/001-skoleservice/spec.md";
    private static readonly Guid DraftId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private readonly List<SaveTestPlanDraftRequest> _saves = [];
    private readonly List<AddManualTestCaseRequest> _added = [];
    private readonly List<TranslateScenarioRequest> _translated = [];

    private static readonly ProjectRequirementOption[] Options =
    [
        new("FR-012", "The service MUST reject a report whose reporting period is not open.", Spec),
        new("FR-027", "A child MUST be counted as expected only when the child is placed in the period, is of compulsory school age and has an education relationship overlapping the period; children past compulsory school age are not expected.", Spec),
        new("FR-029", "An accepted report for a child outside the expected population MUST be delivered and counted as received, not expected.", Spec),
        new("FR-050", "Operations MUST be alerted when the message queue is unavailable for more than ten minutes.", Spec),
    ];

    private static TestCaseDraft Scenario42() => new()
    {
        Source = new ScenarioReference { Kind = ScenarioSourceKind.ProjectDiscovery, Owner = "skole", ScenarioId = "tsc-42", Fingerprint = "f" }, SuiteId = "suite:area:skoleservice",
        Generated = new TestCaseContent
        {
            Title = "Child past compulsory school age when the period locks", Purpose = "Confirm that the child is not expected", Capability = "specification",
            Preconditions = "a child past compulsory school age",
            Steps = ["Prepare the starting situation in the test environment: a child past compulsory school age", "Carry out or trigger: the period locks"],
            StepExpected = ["The starting situation is in place before the action (check the prepared data and state).", "the child is not expected; an accepted report for the child is counted as received, not expected"],
            ExpectedResult = "the child is not expected; an accepted report for the child is counted as received, not expected", ExpectedBasis = ExpectedResultBasis.Specification,
            Template = [new("given", "Starting situation (Given)", "a child past compulsory school age"), new("when", "Action (When)", "the period locks"),
                new("then", "Expected outcome (Then)", "the child is not expected; an accepted report for the child is counted as received, not expected")],
            Evidence = [new("specification", $"{Spec}#scenario-42", "Child past compulsory school age")],
        },
    };

    private static TestCaseDraft Written(string id, string title, string? scenario) => new()
    {
        Source = new ScenarioReference { Kind = ScenarioSourceKind.Manual, Owner = "skole", ScenarioId = id }, SuiteId = "suite:area:skoleservice", ScenarioKey = scenario,
        ScenarioTitle = scenario is null ? null : "Child past compulsory school age when the period locks",
        Generated = new TestCaseContent { Title = title }, Edits = new TestCaseEdits { Steps = ["Lock the period without a report"], StepExpected = ["The child is not expected"] },
    };

    private static TestCaseDraft Other() => new()
    {
        Source = new ScenarioReference { Kind = ScenarioSourceKind.ProjectDiscovery, Owner = "skole", ScenarioId = "tsc-7", Fingerprint = "f" }, SuiteId = "suite:area:skoleservice",
        Generated = new TestCaseContent { Title = "A valid report is accepted", Purpose = "Accept a valid report", Capability = "specification", Steps = ["Submit the report"], StepExpected = ["Accepted"] },
    };

    private static TestPlanDraft Draft(params TestCaseDraft[] cases) => new()
    {
        Id = DraftId, GuideKey = "project:skole", ProjectKey = "skole", ProjectName = "SkoleService", PlanName = "SkoleService – test plan", Revision = 4,
        Suites = [new("suite:area:skoleservice", "SkoleService", "area", null)], Cases = [.. cases],
    };

    private IRenderedComponent<TestPlanSuggestions> Render(TestPlanDraft draft, TranslationAvailability? translation = null, TestPlanDraft? translatedDraft = null, string query = "",
        string? translateError = null)
    {
        Services.AddSingleton(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Projection(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Loaded("Imported", "skole", "SkoleService")).Object);
        var api = new Mock<ITestPlanSuggestionsApiService>();
        api.Setup(x => x.ProjectAsync("skole", It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync((new TestPlanWorkspace
        {
            ProjectKey = "skole", GuideKey = "project:skole", CanWrite = true, RuleVersion = "tps-1",
            Drafts = [new(DraftId, "project:skole", draft.PlanName, null, draft.Revision, draft.Cases.Count, 0, 0, 0, 1, 1, "t", DateTimeOffset.UtcNow)],
        }, (string?)null));
        api.Setup(x => x.DraftAsync(DraftId, It.IsAny<CancellationToken>())).ReturnsAsync((draft, (string?)null));
        api.Setup(x => x.SaveAsync(DraftId, It.IsAny<SaveTestPlanDraftRequest>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, SaveTestPlanDraftRequest, CancellationToken>((_, r, _) => _saves.Add(r))
            .ReturnsAsync((Guid _, SaveTestPlanDraftRequest r, CancellationToken _) => (draft with { Revision = r.BaseRevision + 1 }, (string?)null));
        api.Setup(x => x.AddManualCaseAsync(DraftId, It.IsAny<AddManualTestCaseRequest>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, AddManualTestCaseRequest, CancellationToken>((_, r, _) => _added.Add(r))
            .ReturnsAsync((Guid _, AddManualTestCaseRequest r, CancellationToken _) => (draft with
            {
                Revision = draft.Revision + 1, Cases = [.. draft.Cases, Written("manual-new", r.Title, r.ScenarioKey) with { DuplicatedFrom = r.CopyFrom }],
            }, (string?)null));
        api.Setup(x => x.TranslationAsync(It.IsAny<CancellationToken>())).ReturnsAsync(translation ?? new TranslationAvailability(false, null, "Azure AI Translator has no key yet (System Settings → Translation). You can write the translated text by hand."));
        api.Setup(x => x.TranslateAsync(DraftId, It.IsAny<TranslateScenarioRequest>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, TranslateScenarioRequest, CancellationToken>((_, r, _) => _translated.Add(r))
            .ReturnsAsync(() => translateError is not null ? ((TranslateScenarioResult?)null, translateError)
                : (new TranslateScenarioResult(translatedDraft, ["Child past compulsory school age when the period locks"], [], [], "Translated 1 test case to Norwegian (bokmål). Review the text; translating approves nothing.", "nb", "Azure AI Translator"), (string?)null));
        api.Setup(x => x.VariantsAsync(DraftId, "tsc-42", It.IsAny<CancellationToken>())).ReturnsAsync(([new TestCaseVariantProposal("clause:1", "Child past compulsory school age — Even with an upper secondary education relationship",
            "Business-rule combination", "The expected outcome states a further condition: \"even with an upper secondary education relationship\".", "An upper secondary education relationship", null, "Even with an upper secondary education relationship.")], (string?)null));
        Services.AddSingleton(api.Object);

        var artifact = new ExplorerArtifact("a1", WorkspaceArtifactType.Specification, "spec.md", "spec.md", Spec, ExplorerArtifactSource.Workspace, "ProjectImport", 1, 1, "Draft", ExplorerArtifactCurrentness.Current, null);
        var explorers = new Mock<IArtifactExplorerContext>();
        explorers.Setup(c => c.GetStateAsync(It.IsAny<WorkspaceArtifactType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkspaceArtifactType role, CancellationToken _) => role == WorkspaceArtifactType.Specification
                ? new ArtifactExplorerState(role, ExplorerArtifactStatus.Loaded, "skole", "SkoleService", [artifact], artifact, ExplorerSelectionReason.Explicit, "# Spec\n")
                : new ArtifactExplorerState(role, ExplorerArtifactStatus.Empty, "skole", "SkoleService", []));
        Services.AddSingleton(explorers.Object);
        Services.AddSingleton<IWorkspaceSessionService>(new WorkspaceArtifactRepository());
        var context = new Mock<IReviewContextProvider>();
        context.Setup(x => x.GetCurrent()).Returns(new ReviewContext
        {
            Specification = new SpecificationSemanticModel { Requirements = [.. Options.Select(o => new SemanticRequirement { Id = o.RequirementId, Text = o.Title })] },
        });
        Services.AddSingleton(context.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.GetRequiredService<NavigationManager>().NavigateTo($"test-plan-suggestions?draft={DraftId}&area=prepare{query}");
        var cut = Render<TestPlanSuggestions>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=tps-scenarios]"));
        return cut;
    }

    private static AngleSharp.Dom.IElement Scenario(IRenderedComponent<TestPlanSuggestions> cut, string key) => cut.FindAll("[data-testid=tps-scenario]").Single(s => s.GetAttribute("data-scenario") == key);

    [Fact]
    public void Scenarios_are_parents_of_their_cases_one_open_at_a_time_and_a_case_without_a_scenario_is_shown_unresolved()
    {
        var cut = Render(Draft(Scenario42(), Other(), Written("manual-a", "No report submitted", "tsc-42"), Written("manual-old", "Written before scenarios", null)));

        cut.FindAll("[data-testid=tps-scenario]").Select(s => s.GetAttribute("data-scenario")).Should().Equal("tsc-42", "tsc-7", "");
        var s42 = Scenario(cut, "tsc-42");
        s42.GetAttribute("data-open").Should().Be("true", "the first scenario is open");
        s42.QuerySelector("[data-testid=tps-scenario-counts]")!.TextContent.Should().Be("2 test cases · 2 need review");
        s42.QuerySelector("[data-testid=tps-scenario-toggle]")!.GetAttribute("aria-expanded").Should().Be("true");
        s42.QuerySelector("[data-testid=tps-scenario-translate]")!.GetAttribute("aria-label").Should().Be("Translate scenario and test cases to Norwegian (bokmål)", "English → Norwegian bokmål is the default");
        s42.QuerySelector("[data-testid=tps-scenario-overview]")!.TextContent.Should().Contain("Starting situation").And.Contain("a child past compulsory school age").And.Contain($"{Spec}#scenario-42");
        s42.QuerySelectorAll("[data-case]").Select(c => c.GetAttribute("data-case")).Should().Equal("tsc-42", "manual-a");
        Scenario(cut, "tsc-7").QuerySelectorAll("[data-case]").Should().BeEmpty("a closed scenario shows only its header");

        Scenario(cut, "").QuerySelector("[data-testid=tps-scenario-toggle]")!.Click();
        Scenario(cut, "tsc-42").GetAttribute("data-open").Should().Be("false", "one scenario is open at a time");
        cut.Find("[data-testid=tps-scenario-unresolved]").TextContent.Should().Contain("none is assigned");
        Scenario(cut, "").QuerySelectorAll("[data-case]").Select(c => c.GetAttribute("data-case")).Should().Equal("manual-old");
    }

    [Fact]
    public void A_tester_adds_a_case_under_the_scenario_is_warned_of_a_likely_duplicate_and_duplicates_a_case()
    {
        var cut = Render(Draft(Scenario42(), Written("manual-a", "No report submitted", "tsc-42")));

        cut.Find("[data-testid=tps-scenario-add]").Click();
        cut.Find("[data-testid=tps-scenario-add-confirm]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=tps-scenario-add-hint]").TextContent.Should().Contain("starting situation");
        cut.Find("[data-testid=tps-scenario-add-title]").Input("No report submitted");
        cut.Find("[data-testid=tps-scenario-add-duplicate]").TextContent.Should().Contain("No report submitted");
        cut.Find("[data-testid=tps-scenario-add-title]").Input("Upper secondary education relationship exists");
        cut.FindAll("[data-testid=tps-scenario-add-duplicate]").Should().BeEmpty();
        cut.Find("[data-testid=tps-scenario-add-confirm]").Click();

        cut.WaitForAssertion(() => _added.Should().ContainSingle());
        _added[0].Should().Match<AddManualTestCaseRequest>(r => r.ScenarioKey == "tsc-42" && r.Title == "Upper secondary education relationship exists" && r.CopyFrom == null);
        cut.WaitForAssertion(() => cut.Find("[data-testid=tps-message]").TextContent.Should().Contain("added under the scenario"));

        cut.Find("[data-testid=tps-case-tsc-42] .tps-case-head").Click();
        cut.Find("[data-testid=tps-duplicate-tsc-42]").Click();
        cut.WaitForAssertion(() => _added.Should().HaveCount(2));
        _added[1].Should().Match<AddManualTestCaseRequest>(r => r.CopyFrom == "tsc-42" && r.ScenarioKey == "tsc-42");
    }

    [Fact]
    public void Suggested_requirements_show_why_and_from_where_and_only_a_confirmed_link_is_saved()
    {
        var cut = Render(Draft(Scenario42()), query: "&case=tsc-42");

        var suggestions = cut.FindAll("[data-testid=tps-suggestion]");
        suggestions.Select(s => s.GetAttribute("data-requirement")).Should().Contain("FR-027").And.NotContain("FR-050").And.NotContain("FR-012");
        var fr027 = suggestions.First(s => s.GetAttribute("data-requirement") == "FR-027");
        fr027.GetAttribute("data-level").Should().Be("Semantic");
        fr027.QuerySelector("[data-testid=tps-suggestion-level]")!.TextContent.Should().Be("C · Same behaviour described");
        fr027.QuerySelector("[data-testid=tps-suggestion-why]")!.TextContent.Should().Contain("compulsory school").And.Contain(Spec);
        cut.FindAll("[data-testid=tps-link]").Should().BeEmpty("a suggestion is not a link");
        cut.Find("[data-testid=tps-link-search-all-tsc-42]").HasAttribute("open").Should().BeFalse("the full catalog is secondary");

        fr027.QuerySelector("[data-testid=tps-suggestion-link]")!.Click();
        var other = cut.FindAll("[data-testid=tps-suggestion]").Where(s => s.GetAttribute("data-requirement") != "FR-027").ToList();
        other.Should().NotBeEmpty();
        var rejected = other[0].GetAttribute("data-requirement")!;
        other[0].QuerySelector("[data-testid=tps-suggestion-reject]")!.Click();
        cut.FindAll("[data-testid=tps-suggestion]").Should().NotContain(s => s.GetAttribute("data-requirement") == rejected || s.GetAttribute("data-requirement") == "FR-027");
        cut.Find("[data-testid=tps-save]").Click();

        cut.WaitForAssertion(() => _saves.Should().ContainSingle());
        var part = _saves[0].Cases.Single();
        part.RequirementLinks!.Single().Should().Match<ManualRequirementLink>(l => l.RequirementId == "FR-027" && l.Scope == null && l.SuggestionLevel == RequirementSuggestionLevel.Semantic && l.SuggestionReason!.Contains("compulsory"));
        part.RejectedSuggestions!.Single().RequirementId.Should().Be(rejected);
    }

    [Fact]
    public void Without_any_translation_choice_one_clear_message_links_to_the_settings_and_the_fields_open_to_write_by_hand()
    {
        var cut = Render(Draft(Scenario42()), query: "&case=tsc-42");

        cut.Find("[data-testid=tps-scenario-translate]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=tps-message]").TextContent.Should().StartWith("No translation service is available.")
            .And.Contain("Azure AI Translator has no key").And.Contain("Norwegian (bokmål) fields are open").And.NotContain("You can write the translated"));
        _translated.Should().BeEmpty("nothing is sent when no service is available");
        cut.Find("[data-testid=tps-translate-help]").TextContent.Should().Contain("No translation was made").And.Contain("write the Norwegian (bokmål) text");
        cut.Find("[data-testid=tps-translate-settings]").GetAttribute("href").Should().Be("admin/system-settings?section=translation");
        cut.Find("[data-testid=tps-scenario-language]").TextContent.Should().Be("Norwegian (bokmål)");
        cut.Find("[data-testid=tps-tr-tsc-42]").TextContent.Should().Contain("Original: Child past compulsory school age when the period locks");
        cut.Find("[data-testid=tps-tr-title-tsc-42]").Change("Barn over opplæringspliktig alder når perioden låses");
        cut.Find("[data-testid=tps-tr-step-tsc-42-1]").Change("Utfør eller utløs: perioden låses");
        cut.Find("[data-testid=tps-save]").Click();

        cut.WaitForAssertion(() => _saves.Should().ContainSingle());
        var nb = _saves[0].Cases.Single().Translations!["nb"];
        nb.Title.Should().Be("Barn over opplæringspliktig alder når perioden låses");
        nb.Steps.Should().Equal("", "Utfør eller utløs: perioden låses");
        _saves[0].Cases.Single().Edits.Title.Should().BeNull("the original is not touched");
    }

    [Fact]
    public void When_every_service_fails_on_translate_the_reason_is_shown_with_the_settings_link_and_nothing_changes()
    {
        var cut = Render(Draft(Scenario42()), new TranslationAvailability(true, "Azure AI Translator", "Translation by Azure AI Translator."), query: "&case=tsc-42",
            translateError: "No translation service is available right now. Azure AI Translator could not be reached. The offline fallback failed too: The LibreTranslate server at 127.0.0.1:5050 could not be reached or answered unexpectedly. Is it running? Set up Azure AI Translator or start the local LibreTranslate server in System Settings → Translation. Nothing was changed; you can write the Norwegian (bokmål) text by hand meanwhile.");

        cut.Find("[data-testid=tps-scenario-translate]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=tps-message]").TextContent.Should().Contain("Is it running?").And.Contain("Nothing was changed"));
        cut.Find("[data-testid=tps-translate-settings]").GetAttribute("href").Should().Be("admin/system-settings?section=translation");
        cut.FindAll("[data-testid=tps-tr-tsc-42]").Should().ContainSingle("the fields open to write by hand");
        _saves.Should().BeEmpty();
    }

    [Fact]
    public void Translating_shows_the_translated_text_and_the_original_stays_one_click_away()
    {
        var translated = Scenario42();
        translated = translated.WithTranslation("nb", ScenarioPlanning.FromFields(translated, ["Barn over opplæringspliktig alder", "Bekreft at barnet ikke er forventet", "et barn over opplæringspliktig alder",
            "Klargjør startsituasjonen", "Utfør eller utløs: perioden låses", "Startsituasjonen er på plass", "barnet er ikke forventet", "barnet er ikke forventet"], TranslationOrigin.Machine, "Azure AI Translator", "anna", DateTimeOffset.UtcNow));
        var cut = Render(Draft(Scenario42()), new TranslationAvailability(true, "Azure AI Translator", "Translation by Azure AI Translator."), Draft(translated) with { Revision = 5 }, "&case=tsc-42");

        cut.Find("[data-testid=tps-scenario-translate]").Click();

        cut.WaitForAssertion(() => _translated.Should().ContainSingle(r => r.ScenarioKey == "tsc-42" && r.BaseRevision == 4 && !r.Overwrite && r.Target == "nb"));
        cut.WaitForAssertion(() => cut.Find("[data-testid=tps-scenario-title]").TextContent.Should().Be("Barn over opplæringspliktig alder"));
        cut.Find("[data-testid=tps-scenario-original]").TextContent.Should().Be("Child past compulsory school age when the period locks");
        cut.Find("[data-testid=tps-tr-state]").TextContent.Should().Contain("Machine translation (Azure AI Translator)").And.Contain("not reviewed");
        cut.Find("[data-testid=tps-case-review-state]").TextContent.Should().NotContain("Approved", "translating approves nothing");
        cut.Find("[data-testid=tps-case-language-state]").TextContent.Should().Be("Norwegian (bokmål)");

        cut.Find("[data-testid=tps-scenario-language-toggle]").Click();
        cut.Find("[data-testid=tps-scenario-title]").TextContent.Should().Be("Child past compulsory school age when the period locks");
        cut.FindAll("[data-testid=tps-tr-tsc-42]").Should().BeEmpty();
        cut.Find("[data-testid=tps-scenario-language-toggle]").TextContent.Should().Be("Show Norwegian (bokmål)");
    }

    [Fact]
    public void With_several_configured_languages_the_tester_chooses_which_one_to_translate_into()
    {
        var cut = Render(Draft(Scenario42()), new TranslationAvailability(true, "Azure AI Translator", "Translation by Azure AI Translator.", "en", ["nb", "sv"]), Draft(Scenario42()) with { Revision = 5 }, "&case=tsc-42");
        cut.Find("[data-testid=tps-scenario-translate]").Click(); // loads the configured languages (and translates into the default)
        cut.WaitForAssertion(() => _translated.Should().ContainSingle(r => r.Target == "nb"));

        cut.Find("[data-testid=tps-scenario-target]").Change("sv");
        cut.Find("[data-testid=tps-scenario-translate]").GetAttribute("aria-label").Should().Be("Translate scenario and test cases to Swedish");
        cut.Find("[data-testid=tps-scenario-translate]").Click();
        cut.WaitForAssertion(() => _translated.Should().HaveCount(2));
        _translated[1].Target.Should().Be("sv");
    }

    [Fact]
    public void Save_draft_says_why_it_is_disabled_and_every_case_shows_its_separate_states()
    {
        var cut = Render(Draft(Scenario42()), query: "&case=tsc-42");

        var save = cut.Find("[data-testid=tps-save]");
        save.HasAttribute("disabled").Should().BeTrue();
        save.GetAttribute("title").Should().Contain("every change is saved");
        cut.Find("[data-testid=tps-save-state]").TextContent.Should().Be("All changes saved · version 4");
        cut.Find("[data-testid=tps-case-content-state]").TextContent.Should().Be("Saved");
        cut.Find("[data-testid=tps-case-export-state]").TextContent.Should().Be("No Azure DevOps id known");
        cut.Find("[data-testid=tps-case-generated]").TextContent.Should().Be("Generated — not reviewed");

        cut.Find("[data-testid=tps-edit-observation-tsc-42]").Change("Read the frozen population report for the period");
        cut.Find("[data-testid=tps-save]").HasAttribute("disabled").Should().BeFalse();
        cut.FindAll("[data-testid=tps-save-state]").Should().Contain(e => e.TextContent == "Unsaved changes");
        cut.Find("[data-testid=tps-case-content-state]").TextContent.Should().Be("Unsaved changes");
    }

    [Fact]
    public void Suggest_more_test_cases_offers_only_evidence_backed_variants_and_adds_one_on_request()
    {
        var cut = Render(Draft(Scenario42()));
        cut.Find("[data-testid=tps-scenario-suggest]").Click();
        var variant = cut.Find("[data-testid=tps-variant]");
        variant.TextContent.Should().Contain("Business-rule combination").And.Contain("even with an upper secondary education relationship");
        _added.Should().BeEmpty("a proposal is not a case");
        variant.QuerySelector("[data-testid=tps-variant-add]")!.Click();
        cut.WaitForAssertion(() => _added.Should().ContainSingle(r => r.Variant == "clause:1" && r.ScenarioKey == "tsc-42"));
    }

    [Fact]
    public void The_testability_checklist_is_ready_only_for_what_the_case_states_and_approval_never_makes_it_ready()
    {
        var c = Scenario42();
        var items = C.Checklist(c);
        items.Single(i => i.Id == "observation").State.Should().Be(C.Check.NeedsAction);
        items.Single(i => i.Id == "observation").Detail.Should().Be(ScenarioPlanning.NeedsVerificationMethod);
        items.Single(i => i.Id == "environment").State.Should().Be(C.Check.NotAssessed);
        items.Single(i => i.Id == "data").State.Should().Be(C.Check.NotAssessed);
        C.Checklist(c with { Review = TestCaseReviewState.Accepted }).Select(i => i.State).Should().Equal(items.Select(i => i.State), "approving changes no testability fact");
        var described = C.Checklist(c with { Edits = new TestCaseEdits { ObservationMethod = "Frozen population report", EnvironmentPrerequisites = "QA", Roles = "Tester role", TestDataNeed = TestDataNeed.NotRequired } });
        described.Single(i => i.Id == "observation").State.Should().Be(C.Check.Ready);
        described.Single(i => i.Id == "data").State.Should().Be(C.Check.NotApplicable);
    }
}
