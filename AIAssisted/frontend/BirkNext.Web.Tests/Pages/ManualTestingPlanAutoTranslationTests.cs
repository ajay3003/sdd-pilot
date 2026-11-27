using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using P = BirkNext.Web.Services.PlanPreparePresentation;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Automatic translation in Planlegg og klargjør (2026-11-25): no «Oversett alle»; when the test plan opens, the text with no Norwegian translation
/// yet is translated in the background with the server's «only missing» mode. Existing translations show at once, nothing is requested twice for
/// a plan revision, a failure says so with «Prøv igjen», saving waits for a running translation, and unsaved edits are never lost.
/// </summary>
public sealed partial class ManualTestingPlanTests
{
    private static TestCaseDraft AutoTranslated(TestCaseDraft c, TranslationOrigin origin = TranslationOrigin.Machine) =>
        c.WithTranslation(ScenarioPlanning.Norwegian, ScenarioPlanning.FromFields(c, [.. ScenarioPlanning.TranslatableFields(c).Select(f => f is null ? null : "NB " + f)],
            origin, "Offline", "anna", DateTimeOffset.UtcNow, ScenarioPlanning.Norwegian));

    private static TestPlanDraft FullyTranslated(TestPlanDraft plan) => plan with
    {
        Cases = [.. plan.Cases.Select(c => AutoTranslated(c))],
        StoryTranslations = [.. plan.Cases.Select(c => c.Generated.Story).OfType<CoverageUserStory>().DistinctBy(s => s.Key)
            .Select(s => new StoryTranslation { StoryKey = s.Key, Title = "NB " + s.Title, Origin = TranslationOrigin.Machine })],
    };

    private TaskCompletionSource<(TranslateScenarioResult?, string?)> PendingTranslation()
    {
        var pending = new TaskCompletionSource<(TranslateScenarioResult?, string?)>();
        _api.Setup(x => x.TranslateAllAsync(PlanId, It.IsAny<TranslatePlanRequest>(), It.IsAny<CancellationToken>())).Returns(pending.Task);
        return pending;
    }

    [Fact]
    public void A01_first_opening_translates_only_the_missing_text_in_the_background_and_shows_it_when_it_arrives()
    {
        var plan = Plan(Case("c1", "Submit a valid report"));
        Setup(plan);
        var pending = PendingTranslation();
        var cut = Open();

        cut.FindAll("[data-testid=mtp-translate-all]").Should().BeEmpty("there is no «Oversett alle» any more");
        cut.Find("[data-testid=mtp-auto-translating]").TextContent.Should().Contain("Oversetter");
        cut.Find("[data-testid=mtp-title]").HasAttribute("disabled").Should().BeFalse("editing never waits for the translation");
        _api.Verify(x => x.TranslateAllAsync(PlanId, new TranslatePlanRequest(3, false, ScenarioPlanning.Norwegian, true), It.IsAny<CancellationToken>()), Times.Once);

        pending.SetResult((new TranslateScenarioResult(AutoTranslated(plan.Cases[0]) is var t ? plan with { Revision = 4, Cases = [t] } : null, ["Submit a valid report"], [], [], "ok"), null));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=mtp-auto-translating]").Should().BeEmpty());
        cut.Find("[data-testid=mtp-translation-state]").TextContent.Should().Be("Maskinoversatt — ikke gjennomgått");
        cut.Find("[data-testid=mtp-lang-option][data-lang=Norwegian]").GetAttribute("aria-checked").Should().Be("true", "the new Norwegian text is shown");
    }

    [Fact]
    public void A02_an_existing_translation_is_shown_at_once_and_nothing_is_requested()
    {
        Setup(FullyTranslated(Plan(Case("c1", "Submit a valid report", story: Story6))));
        var cut = Open();

        _api.Verify(x => x.TranslateAllAsync(It.IsAny<Guid>(), It.IsAny<TranslatePlanRequest>(), It.IsAny<CancellationToken>()), Times.Never, "nothing is missing");
        cut.FindAll("[data-testid=mtp-auto-translating]").Should().BeEmpty();
        cut.Find("[data-testid=mtp-lang-option][data-lang=Norwegian]").GetAttribute("aria-checked").Should().Be("true");
    }

    [Fact]
    public void A03_without_a_translation_service_the_original_stays_with_an_honest_note_and_nothing_is_sent()
    {
        Setup(Plan(Case("c1", "Submit a valid report")));
        _api.Setup(x => x.TranslationAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new TranslationAvailability(false, null, "Det lokale oversettelsesprogrammet er ikke installert."));
        var cut = Open();

        cut.Find("[data-testid=mtp-auto-unavailable]").TextContent.Should().Contain("ikke tilgjengelig").And.Contain("originalteksten vises");
        cut.Find("[data-testid=mtp-auto-unavailable]").GetAttribute("title").Should().Contain("ikke installert");
        _api.Verify(x => x.TranslateAllAsync(It.IsAny<Guid>(), It.IsAny<TranslatePlanRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void A04_a_failure_keeps_the_original_says_so_and_Prov_igjen_tries_again()
    {
        Setup(Plan(Case("c1", "Submit a valid report")));
        _api.SetupSequence(x => x.TranslateAllAsync(PlanId, It.IsAny<TranslatePlanRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((TranslateScenarioResult?)null, "No translation service is available right now."))
            .ReturnsAsync((new TranslateScenarioResult(null, [], [], [], "Nothing is missing a translation."), (string?)null));
        var cut = Open();

        cut.WaitForAssertion(() => cut.Find("[data-testid=mtp-auto-failed]").TextContent.Should().StartWith("Kunne ikke oversette"));
        cut.Find("[data-testid=mtp-title]").GetAttribute("value").Should().Be("Submit a valid report", "the original stays visible");
        cut.Find("[data-testid=mtp-auto-retry]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=mtp-auto-failed]").Should().BeEmpty());
        _api.Verify(x => x.TranslateAllAsync(PlanId, It.IsAny<TranslatePlanRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public void A05_reopening_the_page_or_switching_tabs_never_sends_a_second_request_for_the_same_revision()
    {
        Setup(Plan(Case("c1", "Submit a valid report")));
        Services.AddSingleton(new AutoTranslationRegistry()); // the app's one registry per browser session
        var pending = PendingTranslation();
        var first = Open();
        first.Find("[data-testid=mtp-auto-translating]");
        first.Dispose();
        var second = Open();          // reopened while the first request still runs: it is shared
        second.Find("[data-testid=mtp-auto-translating]");
        pending.SetResult((new TranslateScenarioResult(null, [], [], [], "Nothing is missing a translation."), null));
        second.WaitForAssertion(() => second.FindAll("[data-testid=mtp-auto-translating]").Should().BeEmpty());
        second.Dispose();
        var third = Open();           // after it ended: the outcome for this revision is remembered
        third.FindAll("[data-testid=mtp-auto-translating]").Should().BeEmpty();
        _api.Verify(x => x.TranslateAllAsync(PlanId, It.IsAny<TranslatePlanRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void A06_a_save_during_the_translation_is_sent_at_once_and_the_translation_then_retries_on_the_new_revision()
    {
        // 2026-11-27: before, the save waited for the translation (about 11 s in the browser) and was lost when the tester left meanwhile.
        var plan = Plan(Case("c1", "Submit a valid report"));
        Setup(plan);
        var pending = PendingTranslation();
        var sent = new List<SaveTestPlanDraftRequest>();
        _api.Setup(x => x.SaveAsync(PlanId, It.IsAny<SaveTestPlanDraftRequest>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, SaveTestPlanDraftRequest r, CancellationToken _) => sent.Add(r)).ReturnsAsync((plan with { Revision = plan.Revision + 1 }, (string?)null));
        var cut = Open();

        cut.Find("[data-testid=mtp-preconditions]").Input("Skrevet av testeren");
        cut.Find("[data-testid=mtp-save]").Click();
        cut.WaitForAssertion(() => sent.Should().ContainSingle("the save does not wait for the translation"));
        sent[0].BaseRevision.Should().Be(plan.Revision);
        sent[0].Cases.Single().Edits.Preconditions.Should().Be("Skrevet av testeren");
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Lagret."));

        // The server refuses the translation on the old revision: it is asked again (on the saved revision), nothing of the tester's is lost.
        var before = _api.Invocations.Count(i => i.Method.Name == nameof(ITestPlanSuggestionsApiService.TranslateAllAsync));
        pending.SetResult((null, "The proposal was changed by someone else at the same time (another save or an automatic translation). Reload it; your change was not saved."));
        cut.WaitForAssertion(() => _api.Invocations.Count(i => i.Method.Name == nameof(ITestPlanSuggestionsApiService.TranslateAllAsync)).Should().BeGreaterThan(before));
    }

    [Fact]
    public void A06b_when_the_translation_was_saved_first_the_same_change_is_sent_again_on_its_revision_with_its_translations()
    {
        var plan = Plan(Case("c1", "Submit a valid report"));
        Setup(plan);
        var pending = PendingTranslation();
        var sent = new List<SaveTestPlanDraftRequest>();
        _api.Setup(x => x.SaveAsync(PlanId, It.IsAny<SaveTestPlanDraftRequest>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, SaveTestPlanDraftRequest r, CancellationToken _) => sent.Add(r))
            .ReturnsAsync((Guid _, SaveTestPlanDraftRequest r, CancellationToken _) => r.BaseRevision < 4
                ? ((TestPlanDraft?)null, (string?)"The proposal is at revision 4; your copy is revision 3.")
                : (plan with { Revision = 5 }, (string?)null));
        var cut = Open();

        cut.Find("[data-testid=mtp-preconditions]").Input("Skrevet av testeren");
        cut.Find("[data-testid=mtp-save]").Click();
        cut.WaitForAssertion(() => sent.Should().ContainSingle());
        var translated = AutoTranslated(plan.Cases[0]);
        pending.SetResult((new TranslateScenarioResult(plan with { Revision = 4, Cases = [translated] }, ["Submit a valid report"], [], [], "ok"), null));

        cut.WaitForAssertion(() => sent.Should().HaveCount(2));
        sent[1].BaseRevision.Should().Be(4);
        var savedCase = sent[1].Cases.Single();
        savedCase.Edits.Preconditions.Should().Be("Skrevet av testeren", "the tester's edit is not lost");
        savedCase.Translations.Should().ContainKey(ScenarioPlanning.Norwegian, "and the new translation is not wiped by the save");
        cut.FindAll("[data-testid=mtp-error]").Should().BeEmpty();
    }

    [Fact]
    public void A07_an_answer_for_a_plan_the_tester_left_is_not_shown()
    {
        var plan = Plan(Case("c1", "Submit a valid report"));
        Setup(plan);
        var pending = PendingTranslation();
        var cut = Open();
        cut.Dispose();                // the tester navigated away before the answer came
        pending.SetResult((new TranslateScenarioResult(plan with { Revision = 4, Cases = [AutoTranslated(plan.Cases[0])] }, [], [], [], "ok"), null));
        var reopened = Open();
        reopened.FindAll("[data-testid=mtp-auto-failed]").Should().BeEmpty();
    }

    [Fact]
    public void A08_an_outdated_translation_is_marked_Ma_gjennomgas_and_kept_and_nothing_is_requested_for_it()
    {
        var c1 = AutoTranslated(Case("c1", "Submit a valid report"));
        var changed = c1 with { Edits = c1.Edits with { Title = "Submit a valid report (changed)" } };   // the original changed after translation
        Setup(Plan(changed));
        var cut = Open();

        ScenarioPlanning.StateOf(changed).Should().Be(TranslationState.SourceChanged);
        cut.Find("[data-testid=mtp-translation-state]").TextContent.Should().StartWith("Må gjennomgås");
        _api.Verify(x => x.TranslateAllAsync(It.IsAny<Guid>(), It.IsAny<TranslatePlanRequest>(), It.IsAny<CancellationToken>()), Times.Never,
            "an existing translation is never replaced automatically");
        P.MissingTranslation(Plan(changed)).Should().BeFalse();
    }
}
