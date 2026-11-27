using BirkNext.TestPlanning;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>«Se endringer» (2026-11-25): what changed between two stored revisions, per case and field.</summary>
public sealed class PlanRevisionDiffTests
{
    private static TestCaseDraft Case(string id, string title, TestCaseReviewState review = TestCaseReviewState.Generated) => new()
    {
        Source = new ScenarioReference { Kind = ScenarioSourceKind.ProjectDiscovery, Owner = "p", ScenarioId = id }, Review = review,
        Generated = new TestCaseContent { Title = title, Steps = ["Open"], StepExpected = ["Opens"], ExpectedResult = "Stored" },
    };

    [Fact]
    public void Added_removed_changed_fields_and_approval_are_named()
    {
        var old = new TestPlanDraft { PlanName = "Plan", Cases = [Case("a", "Valid report"), Case("b", "Old case")] };
        var a2 = Case("a", "Valid report", TestCaseReviewState.Accepted) with { Edits = new TestCaseEdits { Steps = ["Open", "Send"], StepExpected = ["Opens", "Sent"], Preconditions = "QA" } };
        var @new = new TestPlanDraft { PlanName = "Plan 2", Cases = [a2, Case("c", "New case")] };

        var changes = PlanRevisionDiff.Compare(old, @new).Select(c => (c.CaseTitle, c.Text)).ToList();

        changes.Should().Contain((null, "Navnet er endret fra «Plan» til «Plan 2»."));
        changes.Should().Contain(("New case", "Testtilfelle lagt til.")).And.Contain(("Old case", "Testtilfelle fjernet."));
        changes.Should().Contain(("Valid report", "Endret: forutsetninger, teststeg, forventet resultat per steg."));
        changes.Should().Contain(c => c.CaseTitle == "Valid report" && c.Text.StartsWith("Status: "));
        PlanRevisionDiff.Compare(old, old).Should().BeEmpty();
    }

    [Fact]
    public void A_change_to_the_before_you_start_fields_is_named_and_the_plan_source_is_found_in_any_evidence_reference()
    {
        var a = Case("a", "Barnet finnes ikke");
        var b = a with { Edits = new TestCaseEdits { ObservationMethod = "I oversikten", BlockedBy = "Venter på data" } };
        PlanRevisionDiff.Compare(new TestPlanDraft { Cases = [a] }, new TestPlanDraft { Cases = [b] }).Single().Text.Should().Be("Endret: slik ser du resultatet, hindring.");

        // A scenario without a user story whose first evidence is a requirement id (the real «Barnet finnes ikke»).
        var noStory = a with { Generated = a.Generated with { Evidence = [new("requirement", "r-ukjent-barn"), new("specification", "SkoleService/specs/001-skoleservice/spec.md#ukjent-barn")] } };
        PlanPreparePresentation.PlanSourceLine(new TestPlanDraft { Cases = [noStory] }).Should().Be("Inneholder testtilfeller fra SkoleService (001-skoleservice).");
    }

    [Fact]
    public void A_guide_scenario_without_a_specification_file_comes_from_the_specification_folder_of_the_contract_it_cites()
    {
        // The real «Barnet finnes ikke» (2026-11-26): no story, no «spec.md#…» reference — but it cites SkoleService's own contract.
        var guide = Case("a", "Barnet finnes ikke");
        guide = guide with { Generated = guide.Generated with { Evidence =
        [
            new("guide-scenario", "skolenaervaer/manglende-barn"),
            new("source-evidence", "M2LB.zip (AIAssisted/testdata; archived 2026-10-28):SkoleService/specs/001-skoleservice/contracts/rapportinntak.md:lines 46, 51, 54"),
            new("source-evidence", "M2LB.zip (AIAssisted/testdata; archived 2026-10-28):SkoleService/src/M2LB.SkoleService.Api/Rest/ReportIntake/ReportIntakeHandler.cs:lines 52"),
        ] } };
        PlanPreparePresentation.PlanSourceLine(new TestPlanDraft { Cases = [guide] }).Should().Be("Inneholder testtilfeller fra SkoleService (001-skoleservice).");
        var codeOnly = guide with { Generated = guide.Generated with { Evidence = [guide.Generated.Evidence[0], guide.Generated.Evidence[2]] } };
        PlanPreparePresentation.PlanSourceLine(new TestPlanDraft { Cases = [codeOnly] }).Should().BeNull("source code never names a specification, and the title is never used");
    }

    // -- FUN-016 (manual E2E 2026-11-27): revision 8 «1 scenario lagt til uten testtilfelle»; «Se endringer» said «Ingen endringer i testtilfellene». --

    private static readonly List<TestSuiteDraft> Suites = [new("s-k", "Komponent", "template", null), new("s-e", "Ende til ende", "template", null)];

    [Fact]
    public void FUN016_a_scenario_added_without_a_case_is_a_change_with_its_identity_and_testgruppe()
    {
        var v7 = new TestPlanDraft { Suites = Suites, Cases = [Case("ac4", "A method comment written in Norwegian") with { SuiteId = "s-k" }] };
        var v8 = v7 with { PlannedScenarios = [new PlannedScenario { ScenarioId = "teste01", Title = "TEST-E-01: Inngrep-hendelse fra adapter til GraphQL", SuiteId = "s-e" }] };
        var changes = PlanRevisionDiff.Compare(v7, v8);
        changes.Should().ContainSingle().Which.Should().Be(new PlanRevisionDiff.Change("TEST-E-01: Inngrep-hendelse fra adapter til GraphQL",
            "Scenario lagt til i «Ende til ende» uten testtilfelle.", PlanRevisionDiff.ScenarioKind));
    }

    [Fact]
    public void FUN016_moving_and_removing_scenario_occurrences_and_preparing_a_case_are_named_separately_from_case_changes()
    {
        var item = new PlannedScenario { ScenarioId = "ac4", Title = "AC4 tittel", StoryId = "US1", NumberInStory = 4, SuiteId = "s-k" };
        var before = new TestPlanDraft { Suites = Suites, PlannedScenarios = [item, new PlannedScenario { ScenarioId = "x", Title = "Fjernes", SuiteId = "s-e" }] };
        var after = before with { PlannedScenarios = [item with { SuiteId = "s-e" }], Cases = [Case("ac4", "AC4 tittel") with { SuiteId = "s-e" }] };
        var changes = PlanRevisionDiff.Compare(before, after).Select(c => (c.Kind, c.CaseTitle, c.Text)).ToList();
        changes.Should().Contain((PlanRevisionDiff.ScenarioKind, "US1 · AC 4 — AC4 tittel", "Scenario flyttet fra «Komponent» til «Ende til ende»."));
        changes.Should().Contain((PlanRevisionDiff.ScenarioKind, "Fjernes", "Scenario fjernet fra «Ende til ende»."));
        changes.Should().Contain((PlanRevisionDiff.ScenarioKind, "US1 · AC 4 — AC4 tittel", "Testtilfelle forberedt for scenarioet (scenarioet står i samme testgruppe)."));
        changes.Should().Contain((PlanRevisionDiff.CaseKind, "AC4 tittel", "Testtilfelle lagt til."));
        PlanRevisionDiff.Compare(after, after).Should().BeEmpty("the same revision has no changes");
    }
}

