using BirkNext.Api.Services.TestPlanning;
using BirkNext.TestCoverage;
using FluentAssertions;
using Xunit;
using K = BirkNext.Api.Services.TestPlanning.AcceptanceScenarioInterpreter.ActionKind;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// The shared interpretation of user stories and acceptance scenarios (2026-11-24), on real wording from the M2LB specifications (quoted verbatim
/// from AIAssisted/testdata/M2LB.zip). Asserts structure and meaning — action kind, resolved subject, preserved values and negations, what stays
/// unresolved — never just that some text appears.
/// </summary>
public sealed class AcceptanceScenarioInterpreterTests
{
    // SkoleService/specs/001-skoleservice/spec.md, User Story 1.
    private static readonly CoverageUserStory SkoleUs1 = new()
    {
        Key = "SkoleService/specs/001-skoleservice/spec.md#US1", StoryId = "US1", Ordinal = 1, Priority = "P1",
        Title = "Accept a valid report without keeping the child's national ID",
        Description = "A private institution or a foster home submits a month's attendance for a child. SkoleService looks the child up in Person, discards the child's fødselsnummer or DUF-nummer, checks the report, and stores it against the child's M2LB identity together with the reporter. A meaningless report is rejected with a machine-readable reason; a plausible but inconsistent report is accepted with a quality flag. [US-B1, US-B2, US-B3]",
        WhyThisPriority = "Without accepted reports there is nothing to deliver. This is also where the most sensitive rule lives: the child's national identifier must stop here.",
        IndependentTest = "Submit valid, invalid and inconsistent reports for children known to Person and verify the stored outcome, the rejection reasons, the flags, and that no child national identifier exists anywhere afterwards.",
        SpecificationPath = "SkoleService/specs/001-skoleservice/spec.md",
    };

    // ── User story ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Linked_story_references_are_kept_apart_from_the_story_id()
    {
        StoryInterpretation.ReferencedStories(SkoleUs1.Description).Should().Equal("US-B1", "US-B2", "US-B3");
        StoryInterpretation.ReferencedStories(SkoleUs1.Description).Should().NotContain(SkoleUs1.StoryId!, "US1 is not an alias of US-B1");
        StoryInterpretation.ReferencedStories("No references here.").Should().BeEmpty();
    }

    [Fact]
    public void Independent_test_objectives_are_conditions_to_exercise_and_things_to_verify()
    {
        var objectives = StoryInterpretation.Objectives(SkoleUs1.IndependentTest);

        objectives.Where(o => o.Kind == StoryObjectiveKind.Condition).Select(o => o.Text).Should().Equal(
            "valid reports for children known to Person", "invalid reports for children known to Person", "inconsistent reports for children known to Person");
        objectives.Where(o => o.Kind == StoryObjectiveKind.Verification).Select(o => o.Text).Should().Equal(
            "the stored outcome", "the rejection reasons", "the flags", "no child national identifier exists anywhere afterwards");
        objectives.Should().OnlyContain(o => o.Source == SkoleUs1.IndependentTest, "the original wording is kept with every objective");
        objectives.Where(o => o.Kind == StoryObjectiveKind.Condition).Should().OnlyContain(o => o.NeedsReview, "splitting the conditions is an interpretation");
        objectives.Where(o => o.Kind == StoryObjectiveKind.Verification).Should().OnlyContain(o => !o.NeedsReview, "an explicit comma list");
    }

    [Fact]
    public void Text_that_is_not_a_recognisable_list_is_kept_whole_and_marked_for_review()
    {
        var objectives = StoryInterpretation.Objectives("A tester can open the dashboard on a laptop.");

        objectives.Should().ContainSingle().Which.Should().Be(new StoryTestObjective("A tester can open the dashboard on a laptop", StoryObjectiveKind.Verification,
            "A tester can open the dashboard on a laptop.", NeedsReview: true));
        StoryInterpretation.Objectives(null).Should().BeEmpty();
    }

    // ── Acceptance scenarios ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_report_that_arrives_is_sent_in_by_the_sender_the_story_names_and_how_stays_unresolved()
    {
        var r = AcceptanceScenarioInterpreter.Interpret(
            "a report for a child Person knows, with valid hours, dates within the open period and valid absence codes",
            "it arrives",
            "it is accepted, stored against the child's M2LB identity with BirkID and reporter, and the child's fødselsnummer/DUF-nummer is not stored, published or logged",
            SkoleUs1);

        r.Kind.Should().Be(K.Arrival);
        r.Action.Should().Be("Send the report in as a private institution or a foster home.");
        r.Action.Should().NotContain("it arrives", "a pronoun is never an instruction");
        r.Preconditions.Should().Be("a report for a child Person knows, with valid hours, dates within the open period and valid absence codes", "the Given stays verbatim");
        r.TestDataRequirement.Should().Contain("valid hours").And.Contain("open period").And.Contain("valid absence codes");
        r.Expected.Should().Equal(
            "the report is accepted, stored against the child's M2LB identity with BirkID and reporter",
            "the child's fødselsnummer/DUF-nummer is not stored, published or logged");
        r.Unresolved.Should().Contain(u => u.StartsWith("How the report is sent in the test environment is not stated", StringComparison.Ordinal));
        r.Unresolved.Should().Contain(u => u.Contains("fødselsnummer/DUF-nummer is not stored, published or logged") && u.Contains("access"),
            "the negative assertion is kept and its verification access is open");
        r.Variants.Should().BeEmpty();
    }

    [Fact]
    public void Two_conditions_in_one_then_stay_apart_as_a_variant()
    {
        // SkoleService US1 scenario 5.
        var r = AcceptanceScenarioInterpreter.Interpret("a report with no absence breakdown where planned hours exceed actual hours", "it arrives",
            "it is accepted and flagged \"missing absence breakdown\"; where planned equals actual, it is accepted with no flag", SkoleUs1);

        r.Expected.Should().Equal("the report is accepted and flagged \"missing absence breakdown\"");
        r.Variants.Should().Equal("where planned equals actual, it is accepted with no flag");
        r.Preconditions.Should().Contain("planned hours exceed actual hours", "the inequality is kept exactly");
    }

    [Fact]
    public void An_explicitly_named_arrival_keeps_its_subject()
    {
        // SkoleService US1 scenario 9 and SkoleAdapter ("the change arrives"), the latter without a sender in its story.
        AcceptanceScenarioInterpreter.Interpret("the same message delivered twice", "the second copy arrives", "nothing changes and the same outcome is returned", SkoleUs1)
            .Action.Should().Be("Send the second copy in as a private institution or a foster home.");
        var adapter = AcceptanceScenarioInterpreter.Interpret("a new utdanningsforhold in BiRK", "the change arrives",
            "SkoleService receives a create with a stable record identity, the child reference, from date, to date (or open-ended), education type and school type in SkoleService's code lists.", null);
        adapter.Kind.Should().Be(K.Arrival);
        adapter.Action.Should().Be("Make the change arrive (a new utdanningsforhold in BiRK).", "no publishing mechanism is invented");
        adapter.Unresolved.Should().ContainSingle(u => u.StartsWith("How the change is sent in the test environment is not stated", StringComparison.Ordinal));
    }

    [Fact]
    public void They_is_the_person_in_the_given_and_a_person_acting_needs_nothing_invented()
    {
        // Person/specs/001-person-module: role and permission kept, the search is the action.
        var person = AcceptanceScenarioInterpreter.Interpret("a caseworker with `Person:SøkBarn` for an org unit", "they search by partial name",
            "they receive a paginated summary list containing only children in that org unit at security level 0 or 1, within acceptable response time.", null);
        person.Kind.Should().Be(K.PersonActs);
        person.Action.Should().Be("As the caseworker: search by partial name.");
        person.Preconditions.Should().Contain("`Person:SøkBarn`", "the permission is part of the precondition");
        person.Expected.Should().Equal("the caseworker receive a paginated summary list containing only children in that org unit at security level 0 or 1, within acceptable response time.");
        person.Unresolved.Should().BeEmpty();

        // Frontend/specs/002-child-search-profile: a UI action maps directly.
        AcceptanceScenarioInterpreter.Interpret("the caseworker is on the child search page", "they type a child's name in the search field",
            "a list of matching children appears with name, age, birth date, and current case status", null)
            .Action.Should().Be("As the caseworker: type a child's name in the search field.");

        // Tjeneste/specs/001-tjenestemodul-m01: a two-word role leading the clause.
        var tjeneste = AcceptanceScenarioInterpreter.Interpret("a child has two active placements registered in the system", "a case worker queries for that child's services",
            "both placements are returned, sorted with the most recent first, each including type, status, and all relevant dates.", null);
        tjeneste.Kind.Should().Be(K.PersonActs);
        tjeneste.Action.Should().Be("A case worker queries for that child's services.");
    }

    [Fact]
    public void They_without_a_person_in_the_given_is_flagged()
    {
        var r = AcceptanceScenarioInterpreter.Interpret("a child exists", "they search by name", "the child is found", null);

        r.Unresolved.Should().Contain("Who acts (\"they\") is not stated in the scenario — clarify the role.");
    }

    [Fact]
    public void System_processing_is_triggered_and_observed_and_its_mechanism_stays_open()
    {
        // MeldingsutvekslerReceiver: "the intake processes it" — triggering the message is distinct from observing the receiver.
        var receiver = AcceptanceScenarioInterpreter.Interpret("a valid skolenærvær v2.0.1 message", "the intake processes it",
            "SkoleService receives one request containing the child's identifier and its type, the reporter's type and identity, period dates, planned and actual hours, absence lines, message id, send time and message-chain information.", null);
        receiver.Kind.Should().Be(K.SystemProcessing);
        receiver.Action.Should().Be("The intake processes the valid skolenærvær v2.0.1 message (the action that makes this happen in the test environment must be specified).", "the event as stated and the missing action said plainly — never a vague «trigger and observe»");
        receiver.Unresolved.Should().ContainSingle(u => u.StartsWith("How this processing is triggered and observed", StringComparison.Ordinal));
        receiver.Expected.Should().ContainSingle("one assertion listing many fields is never split at its commas");

        // Revisjon: the exact path template and "(no transformation)" are kept.
        var revisjon = AcceptanceScenarioInterpreter.Interpret("a valid `LeseloggHendelse` with a unique `hendelsesId` is published to the `leselogg` queue",
            "M2LB.Revisjon processes the event",
            "the file `{year}/{month}/{day}/{hendelsesId}.json` exists in Blob Storage with content identical to the received event (no transformation).", null);
        revisjon.Kind.Should().Be(K.SystemProcessing);
        revisjon.Expected.Should().Equal("the file `{year}/{month}/{day}/{hendelsesId}.json` exists in Blob Storage with content identical to the received event (no transformation).");
        revisjon.Unresolved.Should().Contain(u => u.Contains("no transformation"), "a negation inside an assertion still needs a way to check it");
    }

    [Fact]
    public void Numbers_and_codes_are_never_reworded()
    {
        // SkoleService US1 scenario 3.
        var r = AcceptanceScenarioInterpreter.Interpret(
            "a report with an absence code outside the accepted codes configured for its schema version (default for v2.0.1: `1`, `2`, `3`, `4`, `5`, `9`), or a zero-padded code such as `01`",
            "it arrives", "it is accepted, the code is delivered as received, and the report is flagged \"unknown absence code\"", SkoleUs1);

        r.TestDataRequirement.Should().Contain("`1`, `2`, `3`, `4`, `5`, `9`").And.Contain("`01`");
        r.Expected.Should().Equal("the report is accepted, the code is delivered as received", "the report is flagged \"unknown absence code\"");
    }
}
