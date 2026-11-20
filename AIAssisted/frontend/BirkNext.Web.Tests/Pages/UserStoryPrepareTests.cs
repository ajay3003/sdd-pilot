using BirkNext.TestCoverage;
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
/// Plan &amp; prepare as Feature → User Story → Scenario → Test cases: stories in document order with their number, priority and Independent Test,
/// scenarios under the story they are written under, others under "not under a user story"; the story card's tabs (story text, requirements:
/// named, tasks.md, suggested, confirmed for the story, and the cases' own links read-only); story requirements from the same feature's tasks.md
/// with the requirement suffix kept (FR-027a); Translate all with a preview of what it does. Synthetic project (own wording).
/// </summary>
public sealed class UserStoryPrepareTests : BunitContext
{
    private const string Spec = "SkoleService/specs/001-skoleservice/spec.md";
    private static readonly Guid DraftId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private readonly List<SaveTestPlanDraftRequest> _saves = [];
    private readonly List<TranslatePlanRequest> _translateAll = [];

    private static CoverageUserStory Story(string id, int ordinal, string title, string priority, List<StoryTaskRequirement>? tasks = null) => new()
    {
        Key = CoverageUserStory.KeyOf(Spec, id), StoryId = id, Ordinal = ordinal, Title = $"User Story {id[2..]} - {title} (Priority: {priority})", Priority = priority,
        Description = $"About {title}.", IndependentTest = $"Set up placements and check {title} on its own.", SpecificationPath = Spec, LineStart = 10 * ordinal, LineEnd = 10 * ordinal + 8,
        Scenarios = 3, TaskRequirements = tasks ?? [],
    };

    private static readonly CoverageUserStory Us1 = Story("US1", 1, "Accept a valid report", "P1");
    private static readonly CoverageUserStory Us6 = Story("US6", 6, "Expected population", "P2", [new("FR-027A", "T099", "- [X] T099 [US6] Implement the population rule (FR-027a)", "SkoleService/specs/001-skoleservice/tasks.md", 120)]);

    private static TestCaseDraft Case(string id, string title, CoverageUserStory? story, int? number, string capability = "specification") => new()
    {
        Source = new ScenarioReference { Kind = ScenarioSourceKind.ProjectDiscovery, Owner = "skole", ScenarioId = id, Fingerprint = "f" }, SuiteId = "suite:s",
        Generated = new TestCaseContent
        {
            Title = title, Purpose = "Purpose of " + title, Capability = capability, Story = story, NumberInStory = number, Steps = ["Do it"], StepExpected = ["Done"],
            Template = [new("given", "Given", "a child past compulsory school age"), new("when", "When", "the period locks"), new("then", "Then", "the child is not expected")],
            Evidence = [new("specification", $"{Spec}#{id}", title)],
        },
    };

    private static TestPlanDraft Draft() => new()
    {
        Id = DraftId, GuideKey = "project:skole", ProjectKey = "skole", ProjectName = "SkoleService", PlanName = "SkoleService – test plan", Revision = 3, Suites = [new("suite:s", "S", "area", null)],
        Cases =
        [
            // Discovery order (the specification's): US6 scenario 2 before scenario 3.
            Case("tsc-40", "State institution only", Us6, 2), Case("tsc-42", "Child past compulsory school age", Us6, 3), Case("tsc-7", "Valid report", Us1, 1),
            Case("tsc-x", "Scenario directly under the feature", null, null), Case("tsc-api", "GET /reports", null, null, "api-contract"),
        ],
    };

    private static readonly ProjectRequirementOption[] Options =
    [
        new("FR-027A", "The service MUST NOT keep its own copy of a child's classification.", Spec),
        new("FR-027", "A child MUST be counted as expected only when the child is of compulsory school age; children past compulsory school age are not expected.", Spec),
        new("FR-050", "Operations MUST be alerted when the message queue is unavailable.", Spec),
    ];

    private IRenderedComponent<TestPlanSuggestions> Render(TestPlanDraft draft, TranslationAvailability? translation = null)
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
        api.Setup(x => x.TranslationAsync(It.IsAny<CancellationToken>())).ReturnsAsync(translation ?? new TranslationAvailability(true, "Azure AI Translator", "Translation by Azure AI Translator."));
        api.Setup(x => x.TranslateAllAsync(DraftId, It.IsAny<TranslatePlanRequest>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, TranslatePlanRequest, CancellationToken>((_, r, _) => _translateAll.Add(r))
            .ReturnsAsync((new TranslateScenarioResult(null, ["a"], [], [], "Translated 5 test cases in 5 scenarios to Norwegian (bokmål). Saved; translating approves nothing.", "nb", "Azure AI Translator"), (string?)null));
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
        context.Setup(x => x.GetCurrent()).Returns(new ReviewContext { Specification = new SpecificationSemanticModel { Requirements = [.. Options.Select(o => new SemanticRequirement { Id = o.RequirementId, Text = o.Title })] } });
        Services.AddSingleton(context.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.GetRequiredService<NavigationManager>().NavigateTo($"test-plan-suggestions?draft={DraftId}&area=prepare");
        var cut = Render<TestPlanSuggestions>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=tps-scenarios]"));
        return cut;
    }

    [Fact]
    public void Scenarios_are_shown_under_their_user_story_in_document_order_and_others_apart()
    {
        var cut = Render(Draft());

        cut.Find("[data-testid=tps-feature]").TextContent.Should().Contain("SkoleService · 001-skoleservice");
        var stories = cut.FindAll("[data-testid=tps-story]");
        stories.Select(s => s.GetAttribute("data-kind")).Should().Equal("Story", "Story", "NoStory", "OtherSources");
        stories[0].QuerySelector("[data-testid=tps-story-title]")!.TextContent.Should().Be("User Story 1 - Accept a valid report");
        stories[1].QuerySelector("[data-testid=tps-story-priority]")!.TextContent.Should().Be("P2");
        stories[1].QuerySelector("[data-testid=tps-story-id]")!.TextContent.Should().Be("US6");
        stories[1].QuerySelectorAll("[data-testid=tps-scenario]").Select(s => s.GetAttribute("data-scenario")).Should().Equal(["tsc-40", "tsc-42"], "scenario 2 before scenario 3 of the story");
        stories[2].QuerySelector("[data-testid=tps-story-none]")!.TextContent.Should().Contain("not identified");
        stories[2].QuerySelectorAll("[data-testid=tps-scenario]").Select(s => s.GetAttribute("data-scenario")).Should().Equal("tsc-x");
        stories[3].QuerySelectorAll("[data-testid=tps-scenario]").Select(s => s.GetAttribute("data-scenario")).Should().Equal("tsc-api");
        cut.Find("[data-testid=tps-scenario-summary]").TextContent.Should().Contain("2 user stories");
    }

    [Fact]
    public void The_story_card_shows_the_story_its_Independent_Test_and_source_lines()
    {
        var cut = Render(Draft());
        var us6 = cut.FindAll("[data-testid=tps-story]")[1];
        us6.QuerySelector("[data-testid=tps-story-tab-story]")!.Click();
        var panel = cut.FindAll("[data-testid=tps-story]")[1].QuerySelector("[data-testid=tps-story-details]")!;
        panel.TextContent.Should().Contain("About Expected population.").And.Contain("Set up placements and check Expected population on its own.");
        panel.QuerySelector("[data-testid=tps-story-source]")!.TextContent.Should().Be($"{Spec}#US6 · lines 60–68");
    }

    [Fact]
    public void Story_requirements_come_from_tasks_md_and_suggestions_and_a_confirmation_is_saved_for_the_story_only()
    {
        var cut = Render(Draft());
        cut.FindAll("[data-testid=tps-story]")[1].QuerySelector("[data-testid=tps-story-tab-requirements]")!.Click();
        var panel = cut.FindAll("[data-testid=tps-story]")[1].QuerySelector("[data-testid=tps-story-requirements]")!;
        var items = panel.QuerySelectorAll("[data-testid=tps-story-req]");
        items.Should().Contain(i => i.GetAttribute("data-requirement") == "FR-027A" && i.GetAttribute("data-level") == "Structural");
        items.Single(i => i.GetAttribute("data-requirement") == "FR-027A").QuerySelector("[data-testid=tps-story-req-why]")!.TextContent.Should().Contain("T099 (line 120)").And.Contain("FR-027a");
        items.Should().NotContain(i => i.GetAttribute("data-requirement") == "FR-050");

        cut.Find("[data-testid=tps-story-req][data-requirement=FR-027A] [data-testid=tps-story-req-confirm]").Click();
        cut.Find("[data-testid=tps-story-req-confirmed]").GetAttribute("data-requirement").Should().Be("FR-027A");
        cut.Find("[data-testid=tps-save]").Click();

        cut.WaitForAssertion(() => _saves.Should().ContainSingle());
        _saves[0].StoryRequirementLinks!.Single().Should().Match<StoryRequirementLink>(l => l.StoryKey == Us6.Key && l.Link.RequirementId == "FR-027A" && l.Link.SuggestionLevel == RequirementSuggestionLevel.Structural);
        _saves[0].Cases.Should().OnlyContain(c => c.RequirementLinks == null || c.RequirementLinks.Count == 0, "the story's requirement is not given to its cases");
    }

    [Fact]
    public void Translate_all_shows_what_it_will_do_before_anything_is_sent()
    {
        var cut = Render(Draft());
        cut.Find("[data-testid=tps-plan-show-original]").GetAttribute("aria-pressed").Should().Be("true");
        cut.Find("[data-testid=tps-translate-all]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=tps-translate-all-preview]"));
        cut.Find("[data-testid=tps-ta-scenarios]").TextContent.Should().Be("5");
        cut.Find("[data-testid=tps-ta-cases]").TextContent.Should().Be("5");
        cut.Find("[data-testid=tps-ta-stories]").TextContent.Should().Be("2");
        _translateAll.Should().BeEmpty("nothing is sent before confirming");

        cut.Find("[data-testid=tps-ta-confirm]").Click();
        cut.WaitForAssertion(() => _translateAll.Should().ContainSingle(r => r.BaseRevision == 3 && !r.Overwrite && r.Target == "nb"));
        cut.WaitForAssertion(() => cut.Find("[data-testid=tps-message]").TextContent.Should().Contain("in 5 scenarios"));
        cut.FindAll("[data-testid=tps-plan-show]").Single(b => b.GetAttribute("data-language") == "nb").GetAttribute("aria-pressed").Should().Be("true", "the plan is now shown in Norwegian");
    }

    [Fact]
    public void Without_a_translation_service_Translate_all_links_to_the_settings_and_opens_the_fields()
    {
        var cut = Render(Draft(), new TranslationAvailability(false, null, "Azure AI Translator has no key yet (System Settings → Translation). You can write the translated text by hand."));
        cut.Find("[data-testid=tps-translate-all]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=tps-ta-confirm]"));
        cut.Find("[data-testid=tps-ta-confirm]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=tps-translate-all-help]").TextContent.Should().Contain("System Settings → Translation"));
        _translateAll.Should().BeEmpty();
    }

    [Fact]
    public void Tasks_md_references_keep_the_requirement_suffix_and_only_count_in_the_same_feature_folder()
    {
        const string tasks = "# Tasks\n\n## Phase 6: User Story 6\n\n- [X] T099 [US6] Implement the population rule (FR-027a)\n- [ ] T100 [US6] Coverage counts (FR-029)\n- [ ] T101 [US1] Intake (FR-001)\n";
        var same = new ArtifactExplorerState(WorkspaceArtifactType.Tasks, ExplorerArtifactStatus.Loaded, "skole", "SkoleService",
            [Tasks("SkoleService/specs/001-skoleservice/tasks.md")], Tasks("SkoleService/specs/001-skoleservice/tasks.md"), ExplorerSelectionReason.Explicit, tasks);
        var refs = TestCoveragePresentation.TaskRequirements(same, Spec, "US6");
        refs.Select(r => r.RequirementId).Should().Equal("FR-027A", "FR-029");
        refs[0].TaskId.Should().Be("T099");
        var other = same with { Selected = Tasks("Person/specs/001-person-module/tasks.md") };
        TestCoveragePresentation.TaskRequirements(other, Spec, "US6").Should().BeEmpty("tasks of another feature never link to this story");
        TestCoveragePresentation.NamedRequirements("The story covers FR-8 and FR-027a.").Should().Equal("FR-008", "FR-027A");
    }

    private static ExplorerArtifact Tasks(string path) =>
        new("t1", WorkspaceArtifactType.Tasks, "tasks.md", "tasks.md", path, ExplorerArtifactSource.Workspace, "ProjectImport", 1, 1, "Draft", ExplorerArtifactCurrentness.Current, null);
}
