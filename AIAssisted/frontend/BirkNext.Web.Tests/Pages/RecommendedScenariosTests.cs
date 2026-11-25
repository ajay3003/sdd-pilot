using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using P = BirkNext.Web.Services.TestPlanSuggestionsPresentation;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Plan &amp; prepare → Choose scenarios (2026-11-23): 201 flat A–Z rows were noise for a manual tester. A long list starts on Recommended (the
/// highest-priority user-story scenarios with an expected result), grouped by user story in the specification's order; All is one click away.
/// </summary>
public sealed class RecommendedScenariosTests : BunitContext
{
    private static CoverageUserStory Story(int n, string? priority) => new() { Key = $"spec#US{n}", StoryId = $"US{n}", Ordinal = n, Title = $"Story {n}", Priority = priority };

    private static DiscoveredScenario Scenario(string id, string title, CoverageUserStory? story, int? number, string? expected = "Expected") => new()
    {
        CandidateId = id, ProjectKey = "rec", CapabilityId = "specification", SourceCategory = "Specification", Area = "SkoleService", Title = title, Purpose = "p",
        Type = ScenarioCandidateType.BusinessFunctional, Confidence = CandidateConfidence.SourceDerived, ExpectedBehaviour = expected, CanonicalKey = "scenario:" + id,
        Engine = "test", RuleVersion = "tsd-1", Story = story, NumberInStory = number,
    };

    private static List<DiscoveredScenario> Mixed()
    {
        var us1 = Story(1, "P1"); var us2 = Story(2, "P2"); var us3 = Story(3, "P1");
        List<DiscoveredScenario> list =
        [
            Scenario("a", "A zebra rule", us2, 1),
            Scenario("b", "A second scenario of story 1", us1, 2),
            Scenario("c", "A first scenario of story 1", us1, 1),
            Scenario("d", "A story 3 scenario", us3, 1),
            Scenario("e", "A P1 scenario without an expected result", us1, 3, expected: null),
        ];
        // Edge rules from technical notes: no user story.
        list.AddRange(Enumerable.Range(0, 40).Select(i => Scenario($"x{i:00}", $"A message rule {i:00}", null, null)));
        return list;
    }

    [Fact]
    public void Recommended_is_the_top_priority_story_scenarios_with_an_expected_result()
    {
        var all = Mixed();
        P.Recommended(all).Select(c => c.CandidateId).Should().BeEquivalentTo(["b", "c", "d"], "P1 stories only, and only with an expected result");
        P.RecommendByDefault(all, P.Recommended(all)).Should().BeTrue("a long list that Recommended narrows");
        P.RecommendByDefault(all.Take(5).ToList(), P.Recommended(all.Take(5).ToList())).Should().BeFalse("a short list is shown whole");
        P.Recommended([.. all.Where(c => c.Story is null)]).Should().BeEmpty("without user stories nothing is recommended");
        P.Recommended([Scenario("q", "only P3", Story(9, "P3"), 1)]).Should().ContainSingle("the highest priority present, even when it is not P1");
        P.PriorityRank("P1").Should().BeLessThan(P.PriorityRank("p2"));
        P.PriorityRank(null).Should().Be(int.MaxValue);
    }

    [Fact]
    public void User_story_grouping_follows_the_specification_order_with_unstoried_candidates_last()
    {
        var ordered = P.StoryOrder(Mixed());
        ordered.Take(5).Select(c => c.CandidateId).Should().Equal(["c", "b", "e", "d", "a"], "P1 stories by number (US1 then US3), scenarios by number, then P2");
        var groups = P.Group(ordered, P.CatalogGrouping.Story);
        groups.Select(g => g.Title).Should().Equal(["US1 · Story 1 (P1)", "US3 · Story 3 (P1)", "US2 · Story 2 (P2)", "Not under a user story"]);
        groups[^1].Items.Should().HaveCount(40);
        P.DefaultGrouping(Mixed()).Should().Be(P.CatalogGrouping.Story);
        P.DefaultGrouping(Mixed().Where(c => c.Story is null)).Should().Be(P.CatalogGrouping.Area, "no stories: area/module as before");
    }

    private static DiscoveredScenario Triggered(string id, string title, string? when, CoverageUserStory story, int number) =>
        Scenario(id, title, story, number) with { Steps = when is null ? [] : [new DiscoveredStep(when, "ok")] };

    [Fact]
    public void Scenarios_started_by_another_system_a_schedule_or_a_repeat_are_system_events_and_a_submitted_report_stays_manual()
    {
        // M2LB SkoleService, 2026-11-23: ingestion and published events sat next to user flows in "Manual testing".
        var us = Story(7, "P2");
        var items = new List<DiscoveredScenario>
        {
            Triggered("r", "A valid report — it arrives", "it arrives", us, 1),
            Triggered("i", "A new record — it is ingested", "it is ingested", us, 2),
            Triggered("p", "An unlinked record — Person publishes an event", "Person publishes BarnRegistrert for a child", us, 3),
            Triggered("t", "Accepted reports — the lock time passes", "the lock time passes", us, 4),
            Triggered("d", "The same change delivered twice", null, us, 5),
            Triggered("u", "A user without access — they read a list", "a user without the permission reads any list", us, 6),
        };
        var inventory = P.Classify(items);
        items.Where(c => P.SystemEvent(c, inventory.Of(c))).Select(c => c.CandidateId).Should().BeEquivalentTo(["i", "p", "t", "d"]);
        inventory.SystemEvents.Should().Be(4);
        inventory.Manual.Should().Be(2, "a report that arrives is submitted by a tester; a user reading a list is a person");
        items.Where(c => P.InView(c, inventory.Of(c), P.CandidateView.Manual)).Select(c => c.CandidateId).Should().BeEquivalentTo(["r", "u"]);
        items.Where(c => P.InView(c, inventory.Of(c), P.CandidateView.SystemEvents)).Should().HaveCount(4);
        P.Views.Should().ContainInOrder(P.CandidateView.Manual, P.CandidateView.SystemEvents);
        P.ViewOrDefault("system").Should().Be(P.CandidateView.SystemEvents);
    }

    [Fact]
    public void Variants_of_one_situation_in_one_story_fold_into_one_entry_in_place_of_their_first_member()
    {
        var us3 = Story(3, "P1"); var us5 = Story(5, "P2");
        var items = P.StoryOrder(
        [
            Scenario("a", "A locked period — the lock or export is run again", us3, 2),
            Scenario("b", "A locked period — a new report or correction for it arrives", us3, 3),
            Scenario("c", "Accepted reports in an open period — the lock time passes", us3, 1),
            Scenario("d", "A locked period — something in another story", us5, 1),
            Scenario("e", "A period has locked", us3, 4),
        ]);
        var entries = P.Fold([.. items.Where(c => c.Story == us3)]);
        entries.Select(e => e.Single?.CandidateId ?? $"{e.Situation}×{e.Variants.Count}").Should().Equal(["c", "A locked period×2", "e"]);
        entries[1].Variants.Select(v => v.CandidateId).Should().Equal(["a", "b"]);
        P.Fold([.. items]).Should().HaveCount(4, "the same situation in another story is not a variant of this one");
        P.Trigger(items.First(c => c.CandidateId == "a")).Should().Be("the lock or export is run again");
    }

    [Fact]
    public void A_long_list_starts_on_Recommended_grouped_by_story_and_All_shows_every_candidate()
    {
        var candidates = Mixed();
        var api = new Mock<ITestPlanSuggestionsApiService>();
        api.Setup(a => a.ProjectAsync("rec", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new TestPlanWorkspace { ProjectKey = "rec", GuideKey = "project:rec", CanWrite = true, RuleVersion = "tps-1" }, (string?)null));
        api.Setup(a => a.LastDiscoveryAsync("rec", It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new ScenarioDiscoveryResult { DiscoveryId = "d", ProjectKey = "rec", RuleVersion = "tsd-1", DiscoveredAt = DateTimeOffset.UtcNow, Candidates = candidates }, (string?)null));
        Services.AddSingleton(api.Object);
        Services.AddSingleton(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Projection(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Loaded("Imported", "rec", "Rec")).Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.GetRequiredService<NavigationManager>().NavigateTo("test-plan-suggestions?advanced=1");

        var cut = Render<TestPlanSuggestions>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=tps-recommended-on]").GetAttribute("aria-pressed").Should().Be("true"));
        cut.Find("[data-testid=tps-recommended-on]").TextContent.Should().Contain("3");
        cut.Find("[data-testid=tps-recommend-hint]").TextContent.Should().Contain("P1 user-story scenarios");
        cut.Find("[data-testid=tps-shown-count]").TextContent.Should().StartWith("3 of");
        cut.Find("[data-testid=tps-group-by] option[selected]").GetAttribute("value").Should().Be("Story");
        cut.FindAll("[data-testid^=tps-group-toggle-]").Select(b => b.TextContent).Should().HaveCount(2).And.Satisfy(
            t => t.StartsWith("US1 · Story 1 (P1)"), t => t.StartsWith("US3 · Story 3 (P1)"));

        cut.Find("[data-testid=tps-recommended-off]").Click();
        cut.Find("[data-testid=tps-recommended-off]").GetAttribute("aria-pressed").Should().Be("true");
        cut.Find("[data-testid=tps-recommend-hint]").TextContent.Should().Contain($"all {candidates.Count} candidates");
        cut.FindAll("[data-testid^=tps-group-toggle-]").Select(b => b.TextContent).Last().Should().StartWith("Not under a user story");
    }
}
