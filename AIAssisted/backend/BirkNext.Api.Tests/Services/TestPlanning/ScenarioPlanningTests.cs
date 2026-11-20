using BirkNext.TestPlanning;
using FluentAssertions;
using Xunit;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// Scenario-centric planning rules shared by the server and the browser: cases grouped under their scenario (never by guesswork), duplicates
/// proposed not merged, technical identifiers protected through translation, export language never mixed, requirement suggestions by evidence
/// strength (A explicit, B structural, C same behaviour, D weak) with the reason, and test-case variants only from the scenario's own evidence.
/// Synthetic fixtures in own wording, shaped like an SDD specification (user story → acceptance scenario, functional requirements apart).
/// </summary>
public sealed class ScenarioPlanningTests
{
    private const string Spec = "SkoleService/specs/001-skoleservice/spec.md";

    private static TestCaseDraft Discovered(string id, string title, string? given = null, string? when = null, string? then = null, params string[] requirementIds) => new()
    {
        Source = new ScenarioReference { Kind = ScenarioSourceKind.ProjectDiscovery, Owner = "import:p1", ScenarioId = id },
        Generated = new TestCaseContent
        {
            Title = title, Purpose = then is null ? "" : $"Confirm that {then}", Preconditions = given, Capability = "specification",
            Steps = when is null ? [] : [$"Carry out or trigger: {when}"], StepExpected = when is null ? [] : [then], ExpectedResult = then,
            Template = [new("given", "Starting situation (Given)", given), new("when", "Action (When)", when), new("then", "Expected outcome (Then)", then)],
            Requirements = [.. requirementIds.Select(r => new TestCaseRequirement(r, r, null))],
            Evidence = [new("specification", $"{Spec}#scenario-42", title)],
        },
    };

    private static TestCaseDraft Manual(string id, string title, string? scenario = null) => new()
    {
        Source = new ScenarioReference { Kind = ScenarioSourceKind.Manual, Owner = "import:p1", ScenarioId = id }, ScenarioKey = scenario,
        Generated = new TestCaseContent { Title = title },
    };

    // ── Hierarchy ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Cases_are_grouped_under_their_scenario_and_a_case_without_one_is_never_assigned()
    {
        var s42 = Discovered("tsc-42", "Child past compulsory school age when the period locks", "a child past compulsory school age", "the period locks", "the child is not expected");
        var s7 = Discovered("tsc-7", "Valid report is accepted");
        var plan = new TestPlanDraft { Cases = [s42, Manual("manual-old", "Written before scenarios"), s7, Manual("manual-a", "No report submitted", "tsc-42"), Manual("manual-b", "Valid report submitted", "tsc-42")] };

        var groups = ScenarioPlanning.Groups(plan);

        groups.Select(g => g.Key).Should().Equal("tsc-42", "tsc-7", "");
        groups[0].Primary.Should().BeSameAs(s42);
        groups[0].Cases.Select(c => c.Source.ScenarioId).Should().Equal("tsc-42", "manual-a", "manual-b");
        groups[1].Cases.Should().ContainSingle();
        groups[2].Unresolved.Should().BeTrue();
        groups[2].Cases.Single().Source.ScenarioId.Should().Be("manual-old", "an older hand-written case is shown as unresolved, not attached to a scenario");
    }

    [Fact]
    public void A_scenario_whose_own_case_left_the_plan_keeps_its_cases_under_the_stored_title()
    {
        var plan = new TestPlanDraft { Cases = [Manual("manual-a", "No report submitted", "tsc-42") with { ScenarioTitle = "Child past compulsory school age" }] };
        var g = ScenarioPlanning.Groups(plan).Single();
        g.Key.Should().Be("tsc-42");
        g.Primary.Should().BeNull();
        g.Title.Should().Be("Child past compulsory school age");
    }

    [Fact]
    public void A_likely_duplicate_is_proposed_and_a_distinct_variant_is_not()
    {
        var existing = Manual("manual-a", "No report submitted for the child") with { Edits = new TestCaseEdits { Preconditions = "A child past compulsory school age", StepExpected = ["The child is not expected"], Steps = ["Lock the period"] } };
        ScenarioPlanning.LikelyDuplicates("No report submitted for the child", "A child past compulsory school age", ["The child is not expected"], [existing]).Should().ContainSingle();
        ScenarioPlanning.LikelyDuplicates("Accepted report is delivered", "A valid report exists for the child", ["Counted as received, not expected"], [existing]).Should().BeEmpty();
    }

    // ── Translation ─────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("FR-027 applies; see SkoleService/specs/001-skoleservice/spec.md")]
    [InlineData("Send POST to https://localhost/api/reports and expect 202")]
    [InlineData("The field `childId` and utdanningsforhold_id stay; status ACCEPTED")]
    [InlineData("Call ReportService.Lock() and read periodLockedAt")]
    public void Technical_identifiers_survive_translation_unchanged(string text)
    {
        var (masked, tokens) = ScenarioPlanning.Protect(text);
        tokens.Should().NotBeEmpty();
        foreach (var t in tokens) masked.Should().NotContain(t);
        // A translator that only changes words outside the placeholders.
        var translated = masked.Replace("applies", "gjelder").Replace("see", "se").Replace("and expect", "og forvent").Replace("stay", "beholdes").Replace("Call", "Kall").Replace("and read", "og les");
        var restored = ScenarioPlanning.Restore(translated, tokens);
        restored.Should().NotBeNull();
        foreach (var t in tokens) restored.Should().Contain(t);
    }

    [Fact]
    public void A_translation_that_loses_or_invents_a_placeholder_is_not_used()
    {
        var (masked, tokens) = ScenarioPlanning.Protect("FR-027 and FR-029 apply");
        ScenarioPlanning.Restore(masked.Replace("⟦1⟧", ""), tokens).Should().BeNull("an identifier was lost");
        ScenarioPlanning.Restore(masked + " ⟦7⟧", tokens).Should().BeNull("an identifier was invented");
        ScenarioPlanning.Restore(masked.Replace("⟦1⟧", "⟦0⟧"), tokens).Should().BeNull("an identifier was duplicated");
        ScenarioPlanning.Protect("The system MUST NOT mark the report ACCEPTED").Tokens.Should().Equal("ACCEPTED");
    }

    [Fact]
    public void Norwegian_export_uses_one_language_per_case_and_says_when_the_original_is_used()
    {
        var c = Discovered("tsc-42", "Child past school age", "a child past compulsory school age", "the period locks", "the child is not expected");
        var complete = c with
        {
            Norwegian = ScenarioPlanning.FromFields(c, ["Barn over skolealder", "Formål", "et barn over opplæringspliktig alder", "Utløs: perioden låses", "barnet er ikke forventet", "barnet er ikke forventet"],
                TranslationOrigin.Machine, "fake", "tester", DateTimeOffset.UnixEpoch),
        };
        ScenarioPlanning.StateOf(complete).Should().Be(TranslationState.Current);
        var nb = ScenarioPlanning.Localized(complete, ScenarioPlanning.Norwegian);
        nb.Norwegian.Should().BeTrue();
        nb.Title.Should().Be("Barn over skolealder");
        nb.Steps.Should().Equal("Utløs: perioden låses");
        nb.StepExpected.Should().Equal("barnet er ikke forventet");

        ScenarioPlanning.Localized(c, ScenarioPlanning.Norwegian).Should().Match<(string, string?, List<string>, List<string?>, bool Norwegian, string? Note)>(x => !x.Norwegian && x.Note!.Contains("original language"));
        var partly = complete with { Norwegian = complete.Norwegian! with { StepExpected = [null] } };
        ScenarioPlanning.Localized(partly, ScenarioPlanning.Norwegian).Norwegian.Should().BeFalse("a Norwegian step without its result would mix languages");
        ScenarioPlanning.Localized(complete, null).Title.Should().Be("Child past school age");

        var changed = complete with { Edits = new TestCaseEdits { Title = "Child past compulsory school age — lock" } };
        ScenarioPlanning.StateOf(changed).Should().Be(TranslationState.SourceChanged, "the original changed after the translation was made");
        ScenarioPlanning.Localized(changed, ScenarioPlanning.Norwegian).Note.Should().Contain("older original");
    }

    // ── Requirement suggestions ───────────────────────────────────────────────────────────────────────────────

    private static readonly List<ProjectRequirementOption> Requirements =
    [
        new("FR-012", "The service MUST reject a report whose reporting period is not open.", Spec),
        new("FR-027", "A child MUST be counted as expected in a period only when the child is placed in a foster home or private institution during the period, is of compulsory school age, and has an education relationship overlapping the period; children past compulsory school age are not expected.", Spec),
        new("FR-029", "Coverage MUST place each expected child in exactly one of received, rejected only or missing; an accepted report for a child outside the expected population MUST be delivered and counted as received, not expected.", Spec),
        new("FR-050", "Operations MUST be alerted when the message queue is unavailable for more than ten minutes.", Spec),
        new("FR-061", "Requirements for User Story 6 are reviewed by the domain owner before each delivery.", Spec),
        new("FR-027", "A different module's FR-027: invoices MUST be numbered.", "Billing/specs/001-billing/spec.md"),
    ];

    private static TestCaseDraft Scenario42() => Discovered("tsc-42", "Child past compulsory school age when the period locks",
        "a child past compulsory school age", "the period locks",
        "the child is not expected, even with an upper secondary education relationship; an accepted report for the child is delivered and counted as received, not expected");

    [Fact]
    public void A_scenario_that_names_no_requirement_gets_the_requirements_that_state_the_same_behaviour_with_the_reason()
    {
        var c = Scenario42();
        var suggestions = ScenarioPlanning.SuggestRequirements(ScenarioPlanning.ScenarioText(c), [], ScenarioPlanning.StructuralReferences(c), Requirements);

        var semantic = suggestions.Where(s => s.Level == RequirementSuggestionLevel.Semantic).ToList();
        semantic.Select(s => s.RequirementId).Should().Contain("FR-027").And.Contain("FR-029");
        semantic.Should().OnlyContain(s => s.SpecificationPath == Spec, "the same id in another module's specification is another requirement");
        semantic.First(s => s.RequirementId == "FR-027").Reason.Should().Contain("compulsory school");
        suggestions.Should().NotContain(s => s.RequirementId == "FR-050" || s.RequirementId == "FR-012", "unrelated requirements are not suggested");
    }

    [Fact]
    public void An_explicit_id_is_level_A_and_a_requirement_naming_the_story_is_level_B()
    {
        var c = Scenario42() with { Generated = Scenario42().Generated with { Evidence = [new("specification", $"{Spec}#US6-3", "x")] } };
        var suggestions = ScenarioPlanning.SuggestRequirements(ScenarioPlanning.ScenarioText(c), ["FR-012"], ScenarioPlanning.StructuralReferences(c), Requirements);
        suggestions.First().Should().Match<RequirementSuggestion>(s => s.RequirementId == "FR-012" && s.Level == RequirementSuggestionLevel.Explicit);
        suggestions.Should().Contain(s => s.RequirementId == "FR-061" && s.Level == RequirementSuggestionLevel.Structural && s.Reason.Contains("User Story 6"));
    }

    [Fact]
    public void Linked_or_rejected_requirements_are_not_suggested_again_and_nothing_is_suggested_without_a_catalog()
    {
        var c = Scenario42();
        var excluded = new[] { ScenarioPlanning.Key("FR-027", Spec), ScenarioPlanning.Key("FR-029", Spec) };
        ScenarioPlanning.SuggestRequirements(ScenarioPlanning.ScenarioText(c), [], [], Requirements, excluded).Should().NotContain(s => s.SpecificationPath == Spec && (s.RequirementId == "FR-027" || s.RequirementId == "FR-029"));
        ScenarioPlanning.SuggestRequirements(ScenarioPlanning.ScenarioText(c), [], [], []).Should().BeEmpty();
        ScenarioPlanning.SuggestRequirements("Log in as a librarian", [], [], Requirements).Should().BeEmpty("no requirement shares the behaviour");
    }

    // ── Variants ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Variants_come_only_from_the_scenarios_own_evidence()
    {
        var c = Scenario42();
        var variants = ScenarioPlanning.Variants(c, [c]);
        variants.Should().NotBeEmpty();
        variants.Should().Contain(v => v.Basis.Contains("even with an upper secondary education relationship") && v.Dimension == "Business-rule combination");
        variants.Should().OnlyContain(v => v.Basis.Length > 0);

        var plain = Discovered("tsc-1", "Valid report is accepted", "a valid report", "the report is submitted", "the report is accepted");
        ScenarioPlanning.Variants(plain, [plain]).Should().BeEmpty("no failure case, further condition or error response is stated");

        var added = Manual("manual-x", variants[0].Title, "tsc-42");
        ScenarioPlanning.Variants(c, [c, added]).Should().NotContain(v => v.Key == variants[0].Key, "a variant already written is not proposed again");
    }
}
