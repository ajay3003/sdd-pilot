using BirkNext.ProjectImport;
using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using BirkNext.Web.Components.ManualTesting;
using BirkNext.Web.Components.ProjectImport;
using BirkNext.Web.Services;
using BirkNext.Web.Services.ProjectImport;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Moq;
using S = BirkNext.Web.Services.ScenarioBrowserPresentation;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Manual Testing hardening (2026-11-23): saved-plan management (archive, restore, logical delete with typed confirmation), the explicit application
/// choice at import ("Ny applikasjon" / "Ny versjon av eksisterende applikasjon") and its post-import change summary, and the shared relevance rule
/// for technical user stories and requirements (presentation only; uncertain is never hidden).
/// </summary>
public sealed class ManualTestingHardeningTests : BunitContext
{
    private static readonly Guid A = Guid.Parse("11111111-1111-1111-1111-111111111111"), B = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static TestPlanDraftSummary Plan(Guid id, string name, int revision = 3, TestPlanLifecycle state = TestPlanLifecycle.Active) =>
        new(id, "project:m2lb", name, null, revision, 5, 2, 1, 0, 0, 0, "anna", DateTimeOffset.UtcNow)
        {
            Lifecycle = state, Scenarios = 3, ProjectName = "M2LB", LifecycleChangedAt = state == TestPlanLifecycle.Archived ? DateTimeOffset.UtcNow : null,
            LifecycleChangedBy = state == TestPlanLifecycle.Archived ? "anna" : null,
        };

    private static TestPlanWorkspace Workspace() => new()
    {
        ProjectKey = "m2lb", CanWrite = true,
        Drafts = [Plan(A, "Regresjon M2LB")], ArchivedDrafts = [Plan(B, "Verifisering m2lb-language", 7, TestPlanLifecycle.Archived)],
        PlanStatus = new() { [A] = new PlanSourceStatus { State = "Må gjennomgås", Message = "1 endret" } },
    };

    // ── Saved plans ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Saved_plans_show_what_a_tester_needs_and_archived_plans_are_apart()
    {
        var cut = Render<SavedPlans>(p => p.Add(x => x.Workspace, Workspace()).Add(x => x.Api, new Mock<ITestPlanSuggestionsApiService>().Object));

        var row = cut.Find("[data-testid=mt-plan]");
        row.TextContent.Should().Contain("Regresjon M2LB");
        cut.Find("[data-testid=mt-plan-facts]").TextContent.Should().Contain("M2LB").And.Contain("3 scenarioer").And.Contain("5 testtilfeller").And.Contain("2 godkjent").And.Contain("1 trenger avklaring");
        cut.Find("[data-testid=mt-plan-source]").TextContent.Should().Be("Må gjennomgås");
        cut.Find("[data-testid=mt-plan-open]").GetAttribute("href").Should().Be($"manual-testing/plan?draft={A}");
        cut.Markup.Should().NotContain("Verifisering m2lb-language", "archived plans are not in the active list");

        cut.Find("[data-testid=mt-plans-archived]").Click();
        cut.Find("[data-testid=mt-plan]").TextContent.Should().Contain("Verifisering m2lb-language").And.Contain("arkivert");
        cut.FindAll("[data-testid=mt-plan-archive]").Should().BeEmpty();
    }

    [Fact]
    public void Archive_and_restore_use_the_revision_shown_and_reload_the_list()
    {
        var api = new Mock<ITestPlanSuggestionsApiService>();
        api.Setup(x => x.ArchiveAsync(A, It.IsAny<TestPlanLifecycleRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync((Plan(A, "Regresjon M2LB", 4, TestPlanLifecycle.Archived), (string?)null));
        api.Setup(x => x.UnarchiveAsync(B, It.IsAny<TestPlanLifecycleRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync((Plan(B, "Verifisering m2lb-language", 8), (string?)null));
        var changed = 0;
        var cut = Render<SavedPlans>(p => p.Add(x => x.Workspace, Workspace()).Add(x => x.Api, api.Object).Add(x => x.Changed, () => changed++));

        cut.Find("[data-testid=mt-plan-archive]").Click();
        api.Verify(x => x.ArchiveAsync(A, new TestPlanLifecycleRequest(3, null), It.IsAny<CancellationToken>()));
        cut.Find("[data-testid=mt-plans-message]").TextContent.Should().Contain("arkivert");
        cut.Find("[data-testid=mt-plans-archived]").Click();
        cut.Find("[data-testid=mt-plan-unarchive]").Click();
        api.Verify(x => x.UnarchiveAsync(B, new TestPlanLifecycleRequest(7, null), It.IsAny<CancellationToken>()));
        changed.Should().Be(2);
    }

    [Fact]
    public void Delete_is_only_for_an_archived_plan_and_only_after_typing_its_exact_name()
    {
        var api = new Mock<ITestPlanSuggestionsApiService>();
        api.Setup(x => x.DeleteAsync(B, It.IsAny<DeleteTestPlanRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        var cut = Render<SavedPlans>(p => p.Add(x => x.Workspace, Workspace()).Add(x => x.Api, api.Object).Add(x => x.ShowArchived, true));

        cut.Find("[data-testid=mt-plan-delete]").Click();
        cut.Find("[data-testid=mt-plan-delete-confirm]").TextContent.Should().Contain("Versjonene og eksporterte øyeblikksbilder beholdes");
        cut.Find("[data-testid=mt-plan-delete-yes]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=mt-plan-delete-name]").Input("Verifisering");
        cut.Find("[data-testid=mt-plan-delete-yes]").HasAttribute("disabled").Should().BeTrue("a partial name never confirms");
        cut.Find("[data-testid=mt-plan-delete-name]").Input("Verifisering m2lb-language");
        cut.Find("[data-testid=mt-plan-delete-yes]").Click();

        api.Verify(x => x.DeleteAsync(B, new DeleteTestPlanRequest(7, "Verifisering m2lb-language"), It.IsAny<CancellationToken>()), Times.Once);
        api.Verify(x => x.DeleteAsync(A, It.IsAny<DeleteTestPlanRequest>(), It.IsAny<CancellationToken>()), Times.Never, "nothing else is touched");
    }

    [Fact]
    public void A_reader_without_write_access_sees_plans_but_no_lifecycle_actions()
    {
        var cut = Render<SavedPlans>(p => p.Add(x => x.Workspace, Workspace() with { CanWrite = false }).Add(x => x.Api, new Mock<ITestPlanSuggestionsApiService>().Object));
        cut.FindAll("[data-testid=mt-plan-archive]").Should().BeEmpty();
        cut.Find("[data-testid=mt-plans-archived]").Click();
        cut.FindAll("[data-testid=mt-plan-delete]").Should().BeEmpty();
        cut.FindAll("[data-testid=mt-plan-unarchive]").Should().BeEmpty();
    }

    // ── Application choice at import ─────────────────────────────────────────────────────────────────────────

    private static readonly Guid Staging = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static ProjectImportApplicationOptions Options(bool known = true, string? same = null) => new()
    {
        ArchiveKey = "m2lb-language", ArchiveName = "M2LB-language", ArchiveBasis = "Archive file name", SameIdentityAs = same,
        Applications = known ? [new KnownApplication { Key = "m2lb", Name = "M2LB", LatestArchive = "M2LB.zip", LatestImportedAt = DateTimeOffset.UtcNow, Imports = 1 }] : [],
    };

    [Fact]
    public void With_applications_imported_nothing_is_preselected_and_the_import_waits_for_the_choice()
    {
        var api = new Mock<IProjectImportApiService>();
        api.Setup(x => x.ApplicationOptionsAsync(Staging, It.IsAny<CancellationToken>())).ReturnsAsync(Options());
        api.Setup(x => x.ChooseApplicationAsync(Staging, It.IsAny<ProjectImportApplicationChoice>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, ProjectImportApplicationChoice c, CancellationToken _) => (c with { ApplicationName = "M2LB", Warning = "«M2LB-language.zip» har ingen prosjekter felles med siste import av M2LB (M2LB.zip)." }, (string?)null));
        var required = new List<bool>();
        var cut = Render<ImportApplicationChoice>(p => p.Add(x => x.StagingId, Staging).Add(x => x.Api, api.Object).Add(x => x.ChoiceRequiredChanged, v => required.Add(v)));

        required.Last().Should().BeTrue("nothing is inferred from the file name");
        cut.Find("[data-testid=pi-app-new]").HasAttribute("checked").Should().BeFalse();
        cut.Find("[data-testid=pi-app-version]").HasAttribute("checked").Should().BeFalse();
        cut.Find("[data-testid=pi-app-version]").Change(true);
        required.Last().Should().BeTrue("a version needs its application");
        cut.Find("[data-testid=pi-app-select]").Change("m2lb");

        api.Verify(x => x.ChooseApplicationAsync(Staging, new ProjectImportApplicationChoice(ProjectImportApplicationMode.NewVersion, "m2lb", true), It.IsAny<CancellationToken>()));
        required.Last().Should().BeFalse();
        cut.Find("[data-testid=pi-app-chosen]").TextContent.Should().Contain("ny versjon av M2LB").And.Contain("gjeldende kildegrunnlag");
        cut.Find("[data-testid=pi-app-warning]").TextContent.Should().Contain("ingen prosjekter felles", "a warning, never a refusal");
        cut.Markup.Should().Contain("Ny applikasjon").And.Contain("Ny versjon av eksisterende applikasjon");
    }

    [Fact]
    public void The_first_application_needs_no_choice_and_a_taken_name_is_explained()
    {
        var api = new Mock<IProjectImportApiService>();
        api.Setup(x => x.ApplicationOptionsAsync(Staging, It.IsAny<CancellationToken>())).ReturnsAsync(Options(known: false));
        var required = new List<bool>();
        var cut = Render<ImportApplicationChoice>(p => p.Add(x => x.StagingId, Staging).Add(x => x.Api, api.Object).Add(x => x.ChoiceRequiredChanged, v => required.Add(v)));
        cut.Find("[data-testid=pi-app-first]").TextContent.Should().Contain("M2LB-language");
        required.Last().Should().BeFalse();

        var same = new Mock<IProjectImportApiService>();
        same.Setup(x => x.ApplicationOptionsAsync(Staging, It.IsAny<CancellationToken>())).ReturnsAsync(Options(same: "m2lb") with { ArchiveKey = "m2lb", ArchiveName = "M2LB" });
        var cut2 = Render<ImportApplicationChoice>(p => p.Add(x => x.StagingId, Staging).Add(x => x.Api, same.Object));
        cut2.Find("[data-testid=pi-app-same-name]").TextContent.Should().Contain("samme navn som «M2LB»");
    }

    [Fact]
    public void The_post_import_summary_shows_only_computed_changes_and_plans_to_review()
    {
        var planning = new Mock<ITestPlanSuggestionsApiService>();
        planning.Setup(x => x.ChangesAsync(It.Is<ScenarioDiscoveryRequest>(r => r.ProjectKey == "import:import-abc" && r.SnapshotId == Staging), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new ScenarioChangeSummary { ApplicationKey = "m2lb", Compared = true, PreviousArchives = ["M2LB.zip"], New = 4, Changed = 2, Removed = 1, Unchanged = 900, Conflicts = 0 }, (string?)null));
        planning.Setup(x => x.ProjectAsync("import:import-abc", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new TestPlanWorkspace { PlanStatus = new() { [A] = new PlanSourceStatus { State = "Må gjennomgås" }, [B] = new PlanSourceStatus { State = "Klar" } } }, (string?)null));
        var outcome = new ProjectImportApplicationOutcome
        {
            Mode = ProjectImportApplicationMode.NewVersion, ApplicationKey = "m2lb", ApplicationName = "M2LB", ArchiveKey = "m2lb-language", ArchiveName = "M2LB-language", BaselineSelected = true, Explicit = true,
        };
        var cut = Render<ImportApplicationSummary>(p => p.Add(x => x.Outcome, outcome).Add(x => x.ImportId, "import-abc").Add(x => x.SnapshotId, Staging).Add(x => x.Planning, planning.Object));

        cut.Find("[data-testid=pi-app-result]").TextContent.Should().Be("M2LB (ny versjon)");
        cut.Find("[data-testid=pi-app-archive]").TextContent.Should().Be("M2LB-language");
        cut.FindAll("[data-testid=pi-app-changes]").Should().BeEmpty("discovery runs only when asked");
        cut.Find("[data-testid=pi-app-compare]").Click();
        cut.Find("[data-testid=pi-changes-new]").TextContent.Should().Be("4");
        cut.Find("[data-testid=pi-changes-changed]").TextContent.Should().Be("2");
        cut.Find("[data-testid=pi-changes-removed]").TextContent.Should().Be("1");
        cut.Find("[data-testid=pi-changes-plans]").TextContent.Should().Be("1");
        cut.FindAll("[data-testid=pi-changes-not-compared]").Should().BeEmpty("only shown when something could not be compared");
    }

    // ── Technical relevance (shared rule) ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("User Story 1 – Translate Non-Domain Identifiers to English (Priority: P1)", true)]
    [InlineData("User Story 3 – All Tests Pass After Translation (Priority: P3)", true)]
    [InlineData("User Story 2 - Developers read the code without Norwegian knowledge (Priority: P1)", true)]
    [InlineData("User Story 3 - Storage, configuration and telemetry names are English (Priority: P2)", true)]
    [InlineData("User Story 4 - Governing documents match the new rule (Priority: P2)", true)]
    [InlineData("User Story 1 – User Activated When Assigned to M2LB in Entra (Priority: P1)", false)]
    [InlineData("Brukerhistorie 1 – Overblikk ved innlogging (Prioritet: P1)", false)]
    [InlineData("User Story 1 - Consumers see no contract change (Priority: P1)", false)]
    public void Technical_user_stories_are_about_the_code_base_and_say_nothing_observable(string title, bool technical) =>
        (ManualTestingRelevance.StoryReason(new CoverageUserStory { Key = "k", Title = title }) is not null).Should().Be(technical);

    [Theory]
    [InlineData("System MUST rename all non-domain identifiers to English.", true)]
    [InlineData("The CI pipeline MUST run the unit tests on every commit.", true)]
    [InlineData("System MUST reject a report for a closed period with status 400.", false)]
    [InlineData("The API MUST return 403 when the user lacks the role.", false, "technical-looking, but it defines behaviour a tester observes")]
    [InlineData("System MUST store a valid attendance report.", false)]
    [InlineData(null, false)]
    public void Technical_requirements_are_classified_by_relevance_not_by_words_like_API(string? text, bool technical, string because = "") =>
        (ManualTestingRelevance.RequirementReason(text) is not null).Should().Be(technical, because);

    [Fact]
    public void Requirements_are_filtered_for_the_tester_view_only_when_technical_content_is_hidden()
    {
        List<ProjectRequirementOption> reqs = [new("FR-001", "System MUST store a valid attendance report.", "spec.md"), new("FR-009", "System MUST rename all non-domain identifiers to English.", "spec.md")];
        var none = new Dictionary<string, PreparedRequirementTrace>(StringComparer.OrdinalIgnoreCase);
        ManualTestingPresentation.FilterRequirements(reqs, none, ManualTestingPresentation.AllRequirements, null, showTechnical: false).Select(r => r.RequirementId).Should().Equal("FR-001");
        ManualTestingPresentation.FilterRequirements(reqs, none, ManualTestingPresentation.AllRequirements, null).Should().HaveCount(2, "technical requirements stay available");
    }

    [Fact]
    public void A_scenario_under_a_technical_story_is_hidden_by_default_unless_its_sources_disagree()
    {
        var story = new CoverageUserStory { Key = "s", StoryId = "US1", Title = "User Story 1 – Translate Non-Domain Identifiers to English (Priority: P1)" };
        var plain = new DiscoveredScenario { CandidateId = "c1", CapabilityId = ScenarioOwnership.Specification, CanonicalKey = "scenario:x", Title = "Identifiers are English", Story = story };
        var conflicting = plain with { CandidateId = "c2", Conflicts = [new ScenarioConflict()] };
        S.RelevanceOf(plain, ScenarioOwnership.Of(plain)).Should().Be(S.Relevance.Technical);
        S.RelevanceOf(conflicting, ScenarioOwnership.Of(conflicting)).Should().Be(S.Relevance.Manual, "contradicting sources need a person");
        var business = plain with { CandidateId = "c3", Story = story with { Title = "User Story 1 – User Activated When Assigned to M2LB in Entra (Priority: P1)" }, Title = "User is activated" };
        S.RelevanceOf(business, ScenarioOwnership.Of(business)).Should().Be(S.Relevance.Manual);
    }
}
