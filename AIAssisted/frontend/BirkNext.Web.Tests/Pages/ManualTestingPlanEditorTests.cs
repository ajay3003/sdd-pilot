using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Moq;
using P = BirkNext.Web.Services.PlanPreparePresentation;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// L — the one test-case editor with three views of the same form (2026-11-24): Norsk | Original | Sammenlign. Nothing is translated on open;
/// a missing translation is an empty state with the existing Oversett action; both languages are edited, saved and reviewed the same way;
/// steps are one list (identity, not position, aligns the languages); partial and stale translations are flagged; the user story and the
/// Independent Test's objectives are shown with what the plan appears to cover.
/// </summary>
public sealed partial class ManualTestingPlanTests
{
    // SkoleService/specs/001-skoleservice/spec.md, User Story 1 (verbatim).
    private static readonly CoverageUserStory SkoleUs1 = new()
    {
        Key = "skole-us1", StoryId = "US1", Priority = "P1", Title = "Accept a valid report without keeping the child's national ID",
        Description = "A private institution or a foster home submits a month's attendance for a child. SkoleService looks the child up in Person, discards the child's fødselsnummer or DUF-nummer, checks the report, and stores it against the child's M2LB identity together with the reporter. A meaningless report is rejected with a machine-readable reason; a plausible but inconsistent report is accepted with a quality flag. [US-B1, US-B2, US-B3]",
        WhyThisPriority = "Without accepted reports there is nothing to deliver. This is also where the most sensitive rule lives: the child's national identifier must stop here.",
        IndependentTest = "Submit valid, invalid and inconsistent reports for children known to Person and verify the stored outcome, the rejection reasons, the flags, and that no child national identifier exists anywhere afterwards.",
    };

    private static TestCaseDraft Translated(TestCaseDraft original, TranslationOrigin origin = TranslationOrigin.Machine, string? fingerprint = null,
        string? title = "Send inn en gyldig rapport", string? purpose = "Kontroller innsending", string? expected = "Rapporten lagres",
        List<string>? steps = null, List<string?>? stepExpected = null) =>
        original.WithTranslation(ScenarioPlanning.Norwegian, new TestCaseTranslation
        {
            Title = title, Purpose = purpose, Preconditions = "En gyldig skole", Steps = steps ?? ["Åpne skjemaet", "Send inn rapporten"],
            StepExpected = stepExpected ?? ["Skjemaet vises", null], ExpectedResult = expected,
            SourceFingerprint = fingerprint ?? ScenarioPlanning.SourceFingerprint(original), Origin = origin, Provider = "offline",
        });

    private static string? Language(IRenderedComponent<BirkNext.Web.Pages.ManualTestingPlan> cut) =>
        cut.FindAll("[data-testid=mtp-lang-option]").SingleOrDefault(b => b.GetAttribute("aria-checked") == "true")?.GetAttribute("data-lang");

    private static void Choose(IRenderedComponent<BirkNext.Web.Pages.ManualTestingPlan> cut, EditorLanguage language) =>
        cut.Find($"[data-testid=mtp-lang-option][data-lang={language}]").Click();

    // ── Default language and the empty state ──────────────────────────────────────────────────────────────────

    [Fact]
    public void L01_a_case_with_a_translation_opens_in_norwegian_with_only_the_norwegian_fields()
    {
        Setup(Plan(Translated(Case("c1", "Submit a valid report"))));
        var cut = Open();

        Language(cut).Should().Be(nameof(EditorLanguage.Norwegian));
        cut.Find("[data-testid=mtp-tr-title]").GetAttribute("value").Should().Be("Send inn en gyldig rapport");
        cut.Find("[data-testid=mtp-tr-purpose]").GetAttribute("value").Should().Be("Kontroller innsending");
        cut.FindAll("[data-testid=mtp-title]").Should().BeEmpty("Norsk shows the Norwegian fields only");
        cut.FindAll("[data-testid=mtp-lang-empty]").Should().BeEmpty();
    }

    [Fact]
    public void L02_a_case_without_a_translation_opens_in_the_original_and_nothing_is_translated_on_open()
    {
        Setup();
        var cut = Open();

        Language(cut).Should().Be(nameof(EditorLanguage.Original));
        cut.Find("[data-testid=mtp-title]").GetAttribute("value").Should().Be("Submit a valid report");
        cut.FindAll("[data-testid=mtp-tr-title]").Should().BeEmpty();
        _api.Verify(x => x.TranslateAsync(It.IsAny<Guid>(), It.IsAny<TranslateScenarioRequest>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public void L03_norsk_without_a_translation_is_an_empty_state_whose_action_is_the_existing_translate()
    {
        Setup();
        _api.Setup(x => x.TranslateAsync(PlanId, It.IsAny<TranslateScenarioRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new TranslateScenarioResult(Plan(Translated(Case("c1", "Submit a valid report", story: Story6)), Standard().Cases[1], Standard().Cases[2], Standard().Cases[3]) with { Revision = 4 },
                ["c1"], [], [], "Oversatt 1 testtilfelle."), (string?)null));
        var cut = Open();

        Choose(cut, EditorLanguage.Norwegian);
        var empty = cut.Find("[data-testid=mtp-lang-empty]");
        empty.TextContent.Should().Contain("Ingen norsk oversettelse finnes ennå.");
        _api.Verify(x => x.TranslateAsync(It.IsAny<Guid>(), It.IsAny<TranslateScenarioRequest>(), It.IsAny<CancellationToken>()), Times.Never(), "choosing Norsk never translates");

        cut.Find("[data-testid=mtp-translate-inline]").Click();

        _api.Verify(x => x.TranslateAsync(PlanId, new TranslateScenarioRequest(3, "c1", false, ScenarioPlanning.Norwegian), It.IsAny<CancellationToken>()), Times.Once(),
            "the same request as the Oversett button in Kildegrunnlag");
        Language(cut).Should().Be(nameof(EditorLanguage.Norwegian), "after translating, the case opens in Norwegian");
        cut.Find("[data-testid=mtp-tr-title]").GetAttribute("value").Should().Be("Send inn en gyldig rapport");
    }

    [Fact]
    public void L04_the_empty_state_can_go_back_to_the_original_and_translate_waits_for_a_save()
    {
        Setup();
        var cut = Open();
        cut.Find("[data-testid=mtp-title]").Input("Endret tittel");
        Choose(cut, EditorLanguage.Norwegian);

        cut.Find("[data-testid=mtp-translate-inline]").HasAttribute("disabled").Should().BeTrue("unsaved edits are saved before translating");
        cut.Find("[data-testid=mtp-lang-show-original]").Click();
        Language(cut).Should().Be(nameof(EditorLanguage.Original));
        cut.Find("[data-testid=mtp-title]").GetAttribute("value").Should().Be("Endret tittel", "switching view never drops an edit");
    }

    // ── Compare and shared steps ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void L05_compare_shows_both_languages_side_by_side_for_every_field_and_step()
    {
        Setup(Plan(Translated(Case("c1", "Submit a valid report"))));
        var cut = Open();
        Choose(cut, EditorLanguage.Compare);

        cut.Find("[data-testid=mtp-editor]").ClassList.Should().Contain("is-compare");
        foreach (var (o, n) in new[] { ("mtp-title", "mtp-tr-title"), ("mtp-purpose", "mtp-tr-purpose"), ("mtp-preconditions", "mtp-tr-preconditions"), ("mtp-expected", "mtp-tr-expected") })
        {
            var oe = cut.Find($"[data-testid={o}]");
            var ne = cut.Find($"[data-testid={n}]");
            oe.Closest(".mtp-lrow").Should().BeSameAs(ne.Closest(".mtp-lrow"), $"{o} and {n} are on one row");
        }
        var steps = cut.FindAll("[data-testid=mtp-step]");
        steps.Should().HaveCount(2);
        steps[1].QuerySelector("[data-testid=mtp-step-action]")!.GetAttribute("value").Should().Be("Submit the report");
        steps[1].QuerySelector("[data-testid=mtp-tr-step-action]")!.GetAttribute("value").Should().Be("Send inn rapporten", "a step and its translation are one row");
    }

    [Fact]
    public void L06_steps_are_one_list_a_moved_step_keeps_its_translation_and_a_new_step_exists_in_both_languages()
    {
        Setup(Plan(Translated(Case("c1", "Submit a valid report"))));
        var cut = Open();
        Choose(cut, EditorLanguage.Compare);

        cut.FindAll("[data-testid=mtp-step-down]")[0].Click();
        var steps = cut.FindAll("[data-testid=mtp-step]");
        steps[0].QuerySelector("[data-testid=mtp-step-action]")!.GetAttribute("value").Should().Be("Submit the report");
        steps[0].QuerySelector("[data-testid=mtp-tr-step-action]")!.GetAttribute("value").Should().Be("Send inn rapporten", "alignment is by step identity, not position");

        cut.Find("[data-testid=mtp-step-add]").Click();
        cut.FindAll("[data-testid=mtp-step-action]").Should().HaveCount(3);
        cut.FindAll("[data-testid=mtp-tr-step-action]").Should().HaveCount(3);
    }

    [Fact]
    public void L07_switching_views_keeps_unsaved_edits_in_both_languages_and_the_unsaved_guard_still_applies()
    {
        Setup(Plan(Translated(Case("c1", "Submit a valid report")), Case("c2", "Another case")));
        var cut = Open();
        cut.Find("[data-testid=mtp-tr-title]").Input("Ny norsk tittel");
        Choose(cut, EditorLanguage.Original);
        cut.Find("[data-testid=mtp-title]").Input("New original title");
        Choose(cut, EditorLanguage.Compare);

        cut.Find("[data-testid=mtp-tr-title]").GetAttribute("value").Should().Be("Ny norsk tittel");
        cut.Find("[data-testid=mtp-title]").GetAttribute("value").Should().Be("New original title");
        cut.Find("[data-testid=mtp-save]").HasAttribute("disabled").Should().BeFalse();

        cut.FindAll("[data-testid=mtp-case]").First(e => e.TextContent.Contains("Another case")).Click();
        cut.Find("[data-testid=mtp-unsaved]").TextContent.Should().Contain("ulagrede endringer");
    }

    [Fact]
    public void L08_norwegian_edits_including_purpose_are_saved_as_the_translation_and_the_original_is_untouched()
    {
        var original = Case("c1", "Submit a valid report");
        Setup(Plan(Translated(original)));
        SaveTestPlanDraftRequest? sent = null;
        _api.Setup(x => x.SaveAsync(PlanId, It.IsAny<SaveTestPlanDraftRequest>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, SaveTestPlanDraftRequest, CancellationToken>((_, r, _) => sent = r).ReturnsAsync((Plan(Translated(original)) with { Revision = 4 }, (string?)null));
        var cut = Open();

        cut.Find("[data-testid=mtp-tr-purpose]").Input("Kontroller at en gyldig rapport tas imot");
        cut.FindAll("[data-testid=mtp-tr-step-expected]")[1].Input("Kvittering vises");
        cut.Find("[data-testid=mtp-save]").Click();

        var saved = sent!.Cases.Single(c => c.ScenarioId == "c1");
        saved.Translations![ScenarioPlanning.Norwegian].Purpose.Should().Be("Kontroller at en gyldig rapport tas imot");
        saved.Translations[ScenarioPlanning.Norwegian].StepExpected.Should().Equal("Skjemaet vises", "Kvittering vises");
        saved.Edits.IsEmpty.Should().BeTrue("editing Norwegian never edits the original");
    }

    // ── Status: partial, stale, protected ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void L09_a_partial_translation_is_flagged_per_field_and_never_filled_in()
    {
        Setup(Plan(Translated(Case("c1", "Submit a valid report"), purpose: null, stepExpected: [null, null])));
        var cut = Open();

        cut.Find("[data-testid=mtp-translation-gaps]").TextContent.Should().Contain("2 felter mangler");
        cut.FindAll("[data-testid=mtp-untranslated]").Should().HaveCount(2, "purpose and step 1's expected result are missing (step 2 has none in the original either)");
        cut.Find("[data-testid=mtp-tr-purpose]").GetAttribute("value").Should().BeNullOrEmpty();
        cut.Find("[data-testid=mtp-tr-purpose]").GetAttribute("placeholder").Should().Be("Check Submit a valid report", "the original is a hint, not a value");
    }

    [Fact]
    public void L10_a_translation_of_older_text_is_flagged_and_offers_compare()
    {
        Setup(Plan(Translated(Case("c1", "Submit a valid report"), fingerprint: "older")));
        var cut = Open();

        cut.Find("[data-testid=mtp-translation-state]").TextContent.Should().StartWith("Må gjennomgås");
        cut.Find("[data-testid=mtp-translation-stale]").TextContent.Should().Contain("endret etter at den ble oversatt");
        cut.Find("[data-testid=mtp-translation-compare]").Click();
        Language(cut).Should().Be(nameof(EditorLanguage.Compare));
        cut.FindAll("[data-testid=mtp-translation-compare]").Should().BeEmpty("already comparing");
    }

    [Fact]
    public void L11_a_reviewed_translation_says_it_is_kept_and_a_machine_one_does_not()
    {
        Setup(Plan(Translated(Case("c1", "Submit a valid report"), TranslationOrigin.MachineEdited), Translated(Case("c2", "Other")) ));
        var cut = Open();
        cut.Find("[data-testid=mtp-translation-protected]").TextContent.Should().Contain("Beholdes ved ny maskinoversettelse");
        cut.FindAll("[data-testid=mtp-case]").First(e => e.TextContent.Contains("Other")).Click();
        cut.FindAll("[data-testid=mtp-translation-protected]").Should().BeEmpty();
    }

    [Fact]
    public void L12_a_read_only_plan_shows_both_languages_but_edits_neither()
    {
        Setup(Plan(Translated(Case("c1", "Submit a valid report"))), canWrite: false);
        var cut = Open();
        Choose(cut, EditorLanguage.Compare);

        cut.Find("[data-testid=mtp-tr-title]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=mtp-title]").HasAttribute("disabled").Should().BeTrue();
        cut.FindAll("[data-testid=mtp-step-add]").Should().BeEmpty();
        cut.FindAll("[data-testid=mtp-translate-inline]").Should().BeEmpty();
    }

    // ── User story and test objectives ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void L13_the_user_story_section_shows_priority_why_linked_stories_and_the_independent_test_verbatim()
    {
        Setup(Plan(Case("c1", "Accept a valid report", story: SkoleUs1)));
        var cut = Open();
        var story = cut.Find("[data-testid=mtp-story]");

        story.HasAttribute("open").Should().BeFalse("collapsed by default — the case is the work");
        cut.Find("[data-testid=mtp-story-priority]").TextContent.Should().Be("P1");
        cut.Find("[data-testid=mtp-story-toggle]").Click(); // 2026-11-25: rationale and Independent Test are one click away
        cut.Find("[data-testid=mtp-story-why]").TextContent.Should().Contain("the child's national identifier must stop here");
        cut.FindAll("[data-testid=mtp-story-ref]").Select(e => e.TextContent).Should().Equal("US-B1", "US-B2", "US-B3");
        cut.FindAll("[data-testid=mtp-story-ref]").Select(e => e.TextContent).Should().NotContain("US1");
        cut.Find("[data-testid=mtp-story-independent] .mtp-quote").TextContent.Should().Be(SkoleUs1.IndependentTest);
    }

    [Fact]
    public void L13b_the_user_story_follows_the_editor_language_and_Sammenlign_shows_every_field_in_both_languages()
    {
        var nb = new StoryTranslation
        {
            StoryKey = SkoleUs1.Key, Title = "Godta en gyldig rapport uten å beholde barnets fødselsnummer", Description = "En privat institusjon sender inn en måneds fravær.",
            WhyThisPriority = "Uten godtatte rapporter er det ingenting å levere.", IndependentTest = "Send inn gyldige og ugyldige rapporter og kontroller resultatet.",
            SourceFingerprint = ScenarioPlanning.StoryFingerprint(SkoleUs1), Provider = "offline",
        };
        Setup(Plan(Translated(Case("c1", "Accept a valid report", story: SkoleUs1))) with { StoryTranslations = [nb] });
        var cut = Open();

        Choose(cut, EditorLanguage.Original);
        cut.Find("[data-testid=mtp-story-title]").TextContent.Should().Be(SkoleUs1.Title, "Original is always the source text");
        Choose(cut, EditorLanguage.Norwegian);
        cut.Find("[data-testid=mtp-story-title]").TextContent.Should().Be(nb.Title);
        cut.Find("[data-testid=mtp-story-description]").TextContent.Should().Be(nb.Description);

        Choose(cut, EditorLanguage.Compare);
        cut.Find("[data-testid=mtp-story-toggle]").Click();
        cut.Find("[data-testid=mtp-story-title]").TextContent.Should().Be(nb.Title);
        cut.Find("[data-testid=mtp-story-title-original]").TextContent.Should().Contain(SkoleUs1.Title);
        cut.Find("[data-testid=mtp-story-compare]").TextContent.Should().Contain(nb.Description).And.Contain("A private institution or a foster home");
        cut.Find("[data-testid=mtp-story-why-compare]").TextContent.Should().Contain(nb.WhyThisPriority).And.Contain(SkoleUs1.WhyThisPriority);
        cut.Find("[data-testid=mtp-story-independent-compare]").TextContent.Should().Contain(nb.IndependentTest).And.Contain(SkoleUs1.IndependentTest);
        cut.FindAll("[data-testid=mtp-story-untranslated]").Should().BeEmpty();
    }

    [Fact]
    public void L14_objectives_show_which_cases_appear_to_cover_them_and_what_is_not_found()
    {
        var stored = Case("c1", "Accept a valid report and keep the stored outcome", expected: "The stored outcome shows the report against the child", story: SkoleUs1);
        var rejected = Case("c2", "Reject an invalid report", expected: "The rejection reasons are returned machine-readable", story: SkoleUs1);
        var excluded = Case("c3", "Flags on inconsistent reports", expected: "The flags are set", story: SkoleUs1, review: TestCaseReviewState.Excluded);
        Setup(Plan(stored, rejected, excluded));
        var cut = Open();
        cut.Find("[data-testid=mtp-story-toggle]").Click();

        var rows = cut.FindAll("[data-testid=mtp-objective]");
        rows.Should().HaveCount(7, "three conditions and four verifications");
        var covered = rows.Where(r => r.GetAttribute("data-covered") == "true").Select(r => r.QuerySelector(".mtp-obj-text")!.TextContent).ToList();
        covered.Should().Contain("the stored outcome").And.Contain("the rejection reasons");
        covered.Should().NotContain("the flags", "an excluded case covers nothing");
        rows.Single(r => r.TextContent.Contains("the rejection reasons")).GetAttribute("title").Should().Contain("Reject an invalid report", "the covering cases are named on demand (tooltip)");
        cut.Find("[data-testid=mtp-story-coverage]").TextContent.Should().MatchRegex(@"^\d/7 testmål$");
        rows.Where(r => r.TextContent.Contains("tolket")).Should().NotBeEmpty("split conditions are marked as interpretation");
    }

    // ── Quality guidance ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void L15_quality_guidance_points_at_pronoun_steps_and_vague_results_and_is_silent_for_a_good_case()
    {
        Setup(Plan(Case("c1", "Submit a valid report"), Case("c2", "Vague case", expected: "It works", steps: ["It arrives"])));
        var cut = Open();
        cut.FindAll("[data-testid=mtp-quality]").Should().BeEmpty();

        cut.FindAll("[data-testid=mtp-case]").First(e => e.TextContent.Contains("Vague case")).Click();
        var hints = cut.FindAll("[data-testid=mtp-quality-hint]").Select(h => h.TextContent).ToList();
        hints.Should().Contain(h => h.Contains("Steg 1 begynner med «It»"));
        hints.Should().Contain(h => h.Contains("Samlet forventet resultat er vagt"));
        cut.Find("[data-testid=mtp-save]").HasAttribute("disabled").Should().BeTrue("guidance never blocks or edits");
    }

    [Fact]
    public void L19_linked_requirements_are_the_same_in_every_language_view_and_ids_are_never_translated()
    {
        Setup(Plan(Translated(Case("c1", "Submit a valid report"))));
        var cut = Open();
        string Panel() => cut.Find("[data-testid=mtp-ctx-requirements]").TextContent;

        var norsk = Panel();
        Choose(cut, EditorLanguage.Original);
        Panel().Should().Be(norsk);
        Choose(cut, EditorLanguage.Compare);
        Panel().Should().Be(norsk);
        var id = cut.Find("[data-testid=mtp-req-id]");
        id.TextContent.Should().Be("FR-001");
        id.GetAttribute("translate").Should().Be("no");
        id.GetAttribute("href").Should().Be("artifact-traceability?q=FR-001", "the id opens Requirements Traceability");
        cut.Find("[data-testid=mtp-req-relation]").TextContent.Should().Be("Nevnt i kilden");
    }

    // ── Presentation rules ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void L16_the_form_round_trips_the_translated_purpose_and_an_unchanged_translation_is_not_dirty()
    {
        var c = Translated(Case("c1", "Submit a valid report"));
        var form = P.CaseForm.From(c);

        form.TranslatedPurpose.Should().Be("Kontroller innsending");
        form.IsDirty(c).Should().BeFalse();
        form.ToTranslation(c).Should().BeSameAs(c.Translation(ScenarioPlanning.Norwegian));
        form.TranslatedPurpose = "Nytt formål";
        form.IsDirty(c).Should().BeTrue();
        form.ToTranslation(c)!.Purpose.Should().Be("Nytt formål");
    }

    [Fact]
    public void L17_default_language_and_untranslated_fields_follow_the_form()
    {
        var none = P.CaseForm.From(Case("c1", "Submit a valid report"));
        P.DefaultLanguage(none).Should().Be(EditorLanguage.Original);
        P.UntranslatedFields(none).Should().BeEmpty("no translation is not a partial translation");

        var partial = P.CaseForm.From(Translated(Case("c1", "Submit a valid report"), title: "Tittel", purpose: null, expected: null));
        P.DefaultLanguage(partial).Should().Be(EditorLanguage.Norwegian);
        P.UntranslatedFields(partial).Should().Equal("Formål", "Samlet forventet resultat");
    }

    [Fact]
    public void L18_quality_hints_cover_missing_steps_missing_expected_and_required_test_data()
    {
        var form = P.CaseForm.From(Case("c1", "Empty", expected: null, steps: []));
        form.Steps.Clear();
        form.Preconditions = "";
        form.TestDataNeed = TestDataNeed.Required;

        P.QualityHints(form).Should().Contain(h => h.StartsWith("Ingen teststeg"))
            .And.Contain(h => h.StartsWith("Ingen forventet resultat"))
            .And.Contain(h => h.StartsWith("Testdata kreves"));
    }
}
