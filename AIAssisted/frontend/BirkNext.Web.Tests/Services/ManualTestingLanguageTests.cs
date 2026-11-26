using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using BirkNext.Web.Services;
using FluentAssertions;
using S = BirkNext.Web.Services.ScenarioBrowserPresentation;

namespace BirkNext.Web.Tests.Services;

/// <summary>One Manual Testing language (2026-11-26): Norsk shows a stored, current translation; otherwise the original, said honestly; never a
/// stale translation as current; Sammenlign keeps the original beside it; technical identity is never translated.</summary>
public sealed class ManualTestingLanguageTests
{
    private static S.Item Item(ScenarioTranslationView? t) =>
        new(new DiscoveredScenario { CandidateId = "a", Story = new CoverageUserStory { Key = "k", StoryId = "US1", Title = "T" }, NumberInStory = 1 },
            new ScenarioOwnershipInfo(ScenarioPurpose.Unclassified, "spec", TestingWorkspace.ManualTesting, null, default, ""), S.Relevance.Manual, "US1 — A Barn record", "", default!, null,
            new ScenarioWorkItem { CandidateId = "a", Translation = t });

    [Fact]
    public void Norsk_uses_a_current_stored_translation_and_says_so_otherwise()
    {
        var current = new ScenarioTranslationView("US1 — En barnepost", null, null, "posten leveres", null, TranslationOrigin.Machine, true, DateTimeOffset.UtcNow);
        var shown = ManualTestingLanguage.ScenarioTitle(Item(current), "A Barn record", EditorLanguage.Norwegian);
        (shown.Text, shown.Translated, shown.Note).Should().Be(("En barnepost", true, "Maskinoversatt – ikke gjennomgått"));
        ManualTestingLanguage.ScenarioTitle(Item(current), "A Barn record", EditorLanguage.Compare).Original.Should().Be("A Barn record");
        ManualTestingLanguage.ScenarioTitle(Item(current), "A Barn record", EditorLanguage.Original).Text.Should().Be("A Barn record");

        var stale = current with { Current = false };
        var old = ManualTestingLanguage.ScenarioTitle(Item(stale), "A Barn record", EditorLanguage.Norwegian);
        (old.Text, old.Translated).Should().Be(("A Barn record", false), "a translation of an older source is never shown as current");
        old.Note.Should().Contain("utdatert");
        ManualTestingLanguage.ScenarioTitle(Item(null), "A Barn record", EditorLanguage.Norwegian).Note.Should().Be("Norsk oversettelse mangler – viser originalen");
        ManualTestingLanguage.Expected(Item(current), "the record is delivered", EditorLanguage.Norwegian).Text.Should().Be("posten leveres");
    }

    [Fact]
    public void A_story_title_uses_the_stored_translation_only_while_it_matches_the_story()
    {
        var us = new CoverageUserStory { Key = "k1", StoryId = "US1", Title = "User Story 1 - Barn registrations recover" };
        var nb = new StoryTranslation { StoryKey = "k1", Title = "Barneregistreringer gjenopprettes", SourceFingerprint = ScenarioPlanning.StoryFingerprint(us), Origin = TranslationOrigin.Manual };
        ManualTestingLanguage.StoryTitle(us, [nb], EditorLanguage.Norwegian).Should().Match<ManualTestingLanguage.Shown>(x => x.Text == "Barneregistreringer gjenopprettes" && x.Translated);
        ManualTestingLanguage.StoryTitle(us, [nb with { SourceFingerprint = "old" }], EditorLanguage.Norwegian).Should().Match<ManualTestingLanguage.Shown>(x => !x.Translated && x.Note == "Norsk oversettelse er utdatert");
        ManualTestingLanguage.StoryTitle(us, [], EditorLanguage.Norwegian).Text.Should().Be("Barn registrations recover");
    }
}
