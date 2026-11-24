using System.Runtime.CompilerServices;
using System.Text.Json;
using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using BirkNext.Web.Services;
using FluentAssertions;
using S = BirkNext.Web.Services.ScenarioBrowserPresentation;
using W = BirkNext.Web.Services.ScenarioWorkspacePresentation;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Manual testing in focus, on real M2LB acceptance scenarios (the 57 verbatim scenarios of the test-design benchmark: SkoleService, Person,
/// SkoleAdapter and eight more services, with their own user stories and numbers). Business stories a tester works through are shown;
/// stories about the code base itself (AutorisasjonService 001-translate-source-english) wait behind «Vis teknisk innhold».
/// </summary>
public sealed class ManualFocusRealScenarioTests
{
    private sealed record Row(string Id, string Service, string SpecificationPath, StoryRow? Story, int? ScenarioNumber, string Given, string When, string Then, List<string>? ExplicitRequirementIds);
    private sealed record StoryRow(string Id, string Title, string? Priority);

    private static string FixturePath([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "backend", "BirkNext.Api.Tests", "Services", "TestPlanning", "Fixtures", "test-design-benchmark.json"));

    private static readonly Lazy<List<DiscoveredScenario>> Real = new(() =>
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(FixturePath()));
        var rows = doc.RootElement.GetProperty("scenarios").Deserialize<List<Row>>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        return [.. rows.Select(r =>
        {
            var story = r.Story is { } s ? new CoverageUserStory
            {
                Key = CoverageUserStory.KeyOf(r.SpecificationPath, s.Id), StoryId = s.Id, Title = s.Title, Priority = s.Priority, SpecificationPath = r.SpecificationPath,
                Ordinal = int.TryParse(new string(s.Id.Where(char.IsDigit).ToArray()), out var n) ? n : 0,
            } : null;
            return new DiscoveredScenario
            {
                CandidateId = r.Id, ProjectKey = "m2lb", CapabilityId = ScenarioOwnership.Specification, SourceCategory = "specification", Area = r.Service,
                Title = $"{r.Given} — {r.When}", Purpose = r.Then, Type = ScenarioCandidateType.BusinessFunctional, Confidence = CandidateConfidence.ExplicitSpecification,
                ExpectedBehaviour = r.Then, Story = story, NumberInStory = r.ScenarioNumber, RequirementIds = r.ExplicitRequirementIds ?? [],
                Evidence = [new("specification", $"{r.SpecificationPath}#{r.Id}", "Akseptansescenario")],
                Template = [new("given", "Gitt", r.Given), new("when", "Når", r.When), new("then", "Så", r.Then)],
            };
        })];
    });

    private static List<S.Item> Items(string? service = null) =>
        S.Items([.. Real.Value.Where(c => service is null || c.Area == service)], new Dictionary<string, S.Prepared>());

    [Theory]
    [InlineData("Person", new[] { "US1", "US2", "US3" })]
    [InlineData("SkoleAdapter", new[] { "US1", "US2", "US3" })]
    [InlineData("SkoleService", new[] { "US1", "US2", "US3", "US4", "US5", "US6", "US7" })]
    public void Business_stories_of_Person_SkoleAdapter_and_SkoleService_are_manual_testing_work(string service, string[] stories)
    {
        var entries = W.Stories(Items(service), new S.Filter());

        entries.Where(e => e.Story is not null).Select(e => e.Label).Should().BeSubsetOf(stories).And.NotBeEmpty();
        entries.Should().OnlyContain(e => !e.Technical, $"{service}'s stories describe behaviour a tester observes");
        W.RailStories(entries, showTechnical: false, current: null).HiddenTechnical.Should().Be(0);
    }

    [Fact]
    public void Stories_about_the_code_base_wait_behind_the_technical_toggle()
    {
        var entries = W.Stories(Items("Autorisasjon"), new S.Filter());
        var translate = entries.Where(e => e.Story?.SpecificationPath?.Contains("001-translate-source-english", StringComparison.Ordinal) == true).ToList();

        translate.Should().NotBeEmpty();
        translate.Should().OnlyContain(e => e.Technical, "translating identifiers and keeping tests green is developer work, not manual acceptance testing");
        var (shown, hidden) = W.RailStories(entries, showTechnical: false, current: null);
        shown.Should().NotContain(e => translate.Contains(e));
        hidden.Should().Be(translate.Count);
    }

    [Fact]
    public void Every_real_scenario_is_numbered_as_its_specification_numbers_it_and_shows_its_source_words()
    {
        var items = Items();
        foreach (var i in items.Where(i => i.Candidate.Story is not null))
        {
            W.ScenarioLabel(i).Should().Be($"Scenario {i.Candidate.NumberInStory}");
            W.RowTitle(i).Should().NotStartWith(i.Candidate.Story!.StoryId + " — ");
            TestDesignAdvisor.FactsOf(i.Candidate).SourceText.Should().Contain(i.Candidate.Template[0].Value!, "Gitt / Når / Så are the source's own words");
        }
    }

    [Fact]
    public void No_real_scenario_is_ready_to_run_before_a_person_has_prepared_and_approved_it()
    {
        foreach (var c in Real.Value)
        {
            var list = W.Checklist(TestDesignAdvisor.Readiness(TestDesignAdvisor.AsDraft(c)));
            list.Single(x => x.Key == "source").State.Should().NotBe(Tri.Yes, $"{c.CandidateId}: stated in the source is not confirmed");
            list.Single(x => x.Key == "review").State.Should().Be(Tri.No);
            list.Single(x => x.Key == "env").State.Should().Be(Tri.Unknown, "nobody recorded an environment");
        }
    }
}
