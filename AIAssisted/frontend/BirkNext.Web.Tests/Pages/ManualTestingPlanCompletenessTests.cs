using BirkNext.TestPlanning;
using Bunit;
using FluentAssertions;
using Moq;
using P = BirkNext.Web.Services.PlanPreparePresentation;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Workspace completeness in Planlegg og klargjør (2026-11-25): what the existing readiness needs from a person (how the result is observed, access
/// and environment, a hindrance) can now be written and saved; a recorded hindrance blocks the case with its reason (a specification dependency
/// never does); test data shows approval and environment as the catalogue records them; the case's history is shown; read-only and retry are explicit.
/// </summary>
public sealed partial class ManualTestingPlanTests
{
    [Fact]
    public void W01_the_fields_readiness_needs_are_saved_as_the_testers_own_text_and_change_nothing_else()
    {
        var c1 = Case("c1", "Submit a valid report", story: Story6) with { Edits = new TestCaseEdits { Title = "Send inn", TestKind = ScenarioTestKind.Manual } };
        Setup(Plan(c1));
        SaveTestPlanDraftRequest? sent = null;
        _api.Setup(x => x.SaveAsync(PlanId, It.IsAny<SaveTestPlanDraftRequest>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, SaveTestPlanDraftRequest r, CancellationToken _) => sent = r)
            .ReturnsAsync((Guid _, SaveTestPlanDraftRequest r, CancellationToken _) => (Plan(c1 with { Edits = r.Cases.Single().Edits }) with { Revision = 4 }, (string?)null));
        var cut = Open();

        cut.Find("[data-testid=mtp-before]").TextContent.Should().Contain("Før du starter").And.Contain("Slik ser du resultatet").And.Contain("Tilgang og miljø").And.Contain("Hindring");
        cut.Find("[data-testid=mtp-observation]").Input("Rapporten vises i listen over mottatte rapporter");
        cut.Find("[data-testid=mtp-environment]").Input("QA, rolle Saksbehandler");
        cut.Find("[data-testid=mtp-blocked-by]").Input("Barnet finnes ikke i Person-QA ennå (Person-teamet)");
        cut.Find("[data-testid=mtp-save]").Click();

        sent!.Cases.Single().Edits.Should().BeEquivalentTo(c1.Edits with
        {
            ObservationMethod = "Rapporten vises i listen over mottatte rapporter", EnvironmentPrerequisites = "QA, rolle Saksbehandler", BlockedBy = "Barnet finnes ikke i Person-QA ennå (Person-teamet)",
        }, "title, type and every other field are sent as they were");
        cut.Find("[data-testid=mtp-blocked-by]").GetAttribute("value").Should().Be("Barnet finnes ikke i Person-QA ennå (Person-teamet)");
    }

    [Fact]
    public void W02_a_recorded_hindrance_blocks_the_case_with_its_reason_and_the_recorded_access_satisfies_its_check()
    {
        var open = Case("c1", "Submit a valid report") with { Review = TestCaseReviewState.Accepted };
        var before = TestDesignAdvisor.Readiness(open);
        before.Level.Should().NotBe(ReadinessLevel.Blocked);
        before.Checks.Single(x => x.Name == "Tilgang og miljø").Satisfied.Should().Be(Tri.Unknown);
        before.Checks.Should().NotContain(x => x.Name == "Hindring", "nothing recorded = no hindrance");

        var recorded = open with { Edits = new TestCaseEdits { EnvironmentPrerequisites = "QA", BlockedBy = "Venter på testdata fra Person-teamet" } };
        var after = TestDesignAdvisor.Readiness(recorded);
        after.Level.Should().Be(ReadinessLevel.Blocked);
        after.Checks.Single(x => x.Name == "Hindring").Should().Match<ReadinessCheck>(x => x.Satisfied == Tri.No && x.Detail == "Venter på testdata fra Person-teamet");
        after.Checks.Single(x => x.Name == "Tilgang og miljø").Satisfied.Should().Be(Tri.Yes);
        var list = BirkNext.Web.Services.ScenarioWorkspacePresentation.Checklist(after);
        list[0].Should().Match<BirkNext.Web.Services.ScenarioWorkspacePresentation.ChecklistItem>(i => i.Key == "hindrance" && i.State == Tri.No && i.Detail == "Venter på testdata fra Person-teamet",
            "the tester sees why the case is blocked, first");
        BirkNext.Web.Services.ScenarioWorkspacePresentation.Checklist(before).Should().NotContain(i => i.Key == "hindrance");
    }

    [Fact]
    public void W03_test_data_shows_approval_and_environment_as_recorded_and_points_to_registration_when_there_is_none()
    {
        P.TestDataOptionLabel(new TestDataOption("a1", "barn-ukjent", "Barn som ikke finnes i Person", "Json", "Approved", "Valid", null, null, null, "QA"))
            .Should().Be("barn-ukjent — Barn som ikke finnes i Person · godkjent · miljø: QA");
        P.TestDataOptionLabel(new TestDataOption("a2", "rapport", null, "Json", "Draft", "Unknown", null, null, null, null))
            .Should().Be("rapport · ikke godkjent · miljø ikke oppgitt", "a catalogue entry is not proof the data exists in QA");

        Setup();
        _api.Setup(x => x.ProjectAsync("library", It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync((new TestPlanWorkspace
        {
            ProjectKey = "library", GuideKey = "project:library", CanWrite = true,
            Drafts = [new TestPlanDraftSummary(PlanId, "project:library", "p", null, 3, 4, 0, 0, 0, 0, 0, "anna", DateTimeOffset.UtcNow)], TestData = [],
        }, (string?)null));
        var cut = Open();
        cut.Find("[data-testid=mtp-testdata-none]").TextContent.Should().Contain("Registrer testdata");
    }

    [Fact]
    public void W04_the_cases_history_shows_who_changed_or_reviewed_it_newest_first_in_plain_words()
    {
        var t0 = new DateTimeOffset(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);
        var c1 = Case("c1", "Submit a valid report") with
        {
            EditHistory = [new TestCaseEditEvent("anna", t0, ["Steps", "Manual or technical"])],
            ReviewHistory = [new TestCaseReviewEvent(TestCaseReviewState.NeedsClarification, "bo", "Hvilken rolle?", t0.AddHours(1))],
        };
        var history = P.CaseHistory(c1);
        history.Select(h => h.By).Should().Equal("bo", "anna");
        history[1].What.Should().Be("Endret: teststeg, testtype (manuell/teknisk)");
        history[0].What.Should().Contain("Hvilken rolle?");

        Setup(Plan(c1));
        var cut = Open();
        cut.FindAll("[data-testid=mtp-history-item]").Should().HaveCount(2);
        cut.Find("[data-testid=mtp-history]").HasAttribute("open").Should().BeFalse("history is on demand");
    }

    [Fact]
    public void W05_a_reader_is_told_the_plan_is_read_only_and_a_failed_load_offers_a_retry()
    {
        Setup(canWrite: false);
        var cut = Open();
        cut.Find("[data-testid=mtp-read-only]").TextContent.Should().Contain("lesetilgang");
        cut.Find("[data-testid=mtp-observation]").HasAttribute("disabled").Should().BeTrue();

        var failing = new ManualTestingPlanTests();
        failing.Setup();
        failing._api.Setup(x => x.DraftAsync(PlanId, It.IsAny<CancellationToken>())).ReturnsAsync(((TestPlanDraft?)null, "Planen kunne ikke lastes."));
        var failed = failing.Open();
        failed.Find("[data-testid=mtp-error]").TextContent.Should().Contain("kunne ikke lastes");
        failed.Find("[data-testid=mtp-retry]").TextContent.Should().Be("Last inn på nytt");
    }
}
