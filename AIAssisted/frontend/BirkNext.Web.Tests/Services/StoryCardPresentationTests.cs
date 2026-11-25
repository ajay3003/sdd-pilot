using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using BirkNext.Web.Services;
using FluentAssertions;
using P = BirkNext.Web.Services.PlanPreparePresentation;
using W = BirkNext.Web.Services.ScenarioWorkspacePresentation;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// The user story card and scenario titles in Manual testing (2026-11-25): a scenario is named by its acceptance criterion ("AC 3 — …"), never
/// "US1 — …" (the story card beside it names the story); the story is shown in Norwegian only from the plan's stored translation, marked stale
/// when the story changed after it was translated, and falls back to the original (never translated on the fly).
/// </summary>
public sealed class StoryCardPresentationTests
{
    private static readonly CoverageUserStory Us = new()
    {
        Key = CoverageUserStory.KeyOf("SkoleService/specs/001-skoleservice/spec.md", "us1"), StoryId = "US1", Ordinal = 1,
        Title = "User Story 1 - Accept a valid report (Priority: P1)", Priority = "P1", SpecificationPath = "SkoleService/specs/001-skoleservice/spec.md",
        Description = "Reporters send attendance reports.", WhyThisPriority = "Without valid reports nothing else works.", IndependentTest = "Submit a valid report.",
    };

    private static StoryTranslation Nb(string? fingerprint = null) => new()
    {
        StoryKey = Us.Key, Language = ScenarioPlanning.Norwegian, Title = "Brukerhistorie 1 - Godta en gyldig rapport (Prioritet: P1)",
        Description = "Rapportører sender fraværsrapporter.", WhyThisPriority = "Uten gyldige rapporter virker ingenting annet.", IndependentTest = "Send en gyldig rapport.",
        SourceFingerprint = fingerprint ?? ScenarioPlanning.StoryFingerprint(Us), Provider = "test",
    };

    private static TestPlanDraft Plan(params StoryTranslation[] translations) => new() { StoryTranslations = translations.Length == 0 ? null : [.. translations] };

    [Fact]
    public void Norwegian_shows_the_stored_translation_with_a_clean_title()
    {
        var nb = P.StoryIn(Plan(Nb()), Us, norwegian: true);
        nb.Translated.Should().BeTrue();
        nb.Stale.Should().BeFalse();
        nb.Title.Should().Be("Godta en gyldig rapport", "the «Brukerhistorie 1 -» prefix and «(Prioritet: P1)» suffix are shown as chips, not in the title");
        nb.Description.Should().Be("Rapportører sender fraværsrapporter.");
        nb.Why.Should().StartWith("Uten gyldige");
        nb.IndependentTest.Should().Be("Send en gyldig rapport.");
    }

    [Fact]
    public void Without_a_translation_Norwegian_falls_back_to_the_original_and_says_it_is_not_translated()
    {
        var shown = P.StoryIn(Plan(), Us, norwegian: true);
        shown.Translated.Should().BeFalse();
        shown.Title.Should().Be("Accept a valid report");
        shown.Description.Should().Be(Us.Description);
        P.StoryIn(null, Us, norwegian: true).Translated.Should().BeFalse("no plan = no translation, never one made on the fly");
    }

    [Fact]
    public void A_story_changed_after_translation_is_marked_stale_and_Original_is_always_the_source()
    {
        P.StoryIn(Plan(Nb(fingerprint: "old")), Us, norwegian: true).Should().Match<P.StoryText>(t => t.Translated && t.Stale);
        P.StoryIn(Plan(Nb()), Us, norwegian: false).Should().Match<P.StoryText>(t => !t.Translated && t.Title == "Accept a valid report" && t.Description == Us.Description);
    }

    [Fact]
    public void A_translation_of_another_language_or_story_is_not_used()
    {
        P.StoryIn(Plan(Nb() with { Language = "sv" }), Us, norwegian: true).Translated.Should().BeFalse();
        P.StoryIn(Plan(Nb() with { StoryKey = "other#us2" }), Us, norwegian: true).Translated.Should().BeFalse();
        P.StoryIn(Plan(Nb() with { Title = " ", Description = null }), Us, norwegian: true).Translated.Should().BeFalse("an empty translation is no translation");
    }

    [Fact]
    public void The_scenario_heading_uses_the_acceptance_criterion_never_the_story_prefix()
    {
        var c = new TestCaseDraft { Generated = new TestCaseContent { Title = "US1 — Valid report accepted", Story = Us, NumberInStory = 3 } };
        P.ScenarioHeading(c).Should().Be("AC 3 — Valid report accepted");

        var loose = new TestCaseDraft { Generated = new TestCaseContent { Title = "Report travels to the registry" } };
        P.ScenarioHeading(loose).Should().Be("Report travels to the registry", "a scenario outside a story keeps its own title");
    }

    [Fact]
    public void Story_titles_lose_their_prefix_and_priority_in_both_languages()
    {
        W.CleanStoryTitle("User Story 2 - Protect graded children (Priority: P2)").Should().Be("Protect graded children");
        W.CleanStoryTitle("Brukerhistorie 2 – Beskytt graderte barn (Prioritet: P2)").Should().Be("Beskytt graderte barn");
        W.CleanStoryTitle("Plain title").Should().Be("Plain title");
        W.AcLabel(4).Should().Be("AC 4");
    }
}
