using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using FluentAssertions;
using Xunit;
using C = BirkNext.TestCoverage.CoverageClarification;
using D = BirkNext.Api.Services.TestPlanning.TestScenarioDiscoveryEngine;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// Specification clarifications in Discover test scenarios (2026-11-22): a clarification about a candidate's requirement, scenario or story is shown
/// on it and on the prepared case; an open one (or an inline [NEEDS CLARIFICATION] marker) is an open question; one that names no requirement is
/// counted in the source's summary; a candidate without clarifications keeps its fingerprint (prepared cases are not flagged as changed).
/// </summary>
public sealed class DiscoveryClarificationTests
{
    private static CoverageWorkspaceEvidence Spec(params C[] clarifications) => new()
    {
        SpecificationAvailable = true,
        Requirements = [new("FR-001", "FR-001: The system MUST store a report.", null), new("FR-002", "FR-002: The system MUST reject a duplicate report.", null),
            new("FR-003", "FR-003: Reports MUST be kept for [NEEDS CLARIFICATION: retention period not specified].", null)],
        AcceptanceScenarios = [new QaTestCandidate { Id = "US1-1", Title = "Valid report", Given = "a valid report", When = "it is sent", Then = "it is stored", RequirementIds = ["FR-001"] }],
        Clarifications = [.. clarifications],
    };

    private static List<DiscoveredScenario> Discover(CoverageWorkspaceEvidence workspace) => [.. D.FromSpecification("p", workspace).Candidates];

    [Fact]
    public void An_open_clarification_about_a_requirement_is_shown_and_becomes_an_open_question()
    {
        var c = Discover(Spec(new C("Q-1", "What is a duplicate for FR-002?", null, C.Open, null, ["FR-002"]))).Single(x => x.RequirementIds.Contains("FR-002"));
        c.Clarifications.Should().ContainSingle().Which.Question.Should().Be("What is a duplicate for FR-002?");
        c.OpenQuestions.Should().Contain("Open clarification in the specification: What is a duplicate for FR-002?");
        D.Content(c).Clarifications.Should().ContainSingle("the prepared case carries it too");
    }

    [Fact]
    public void A_documented_or_resolved_answer_is_shown_without_an_open_question()
    {
        var list = Discover(Spec(
            new C("Q-1", "Is a duplicate the same report id?", "Yes, same id and period.", C.Documented, null, ["FR-002"]),
            new C("Q-2", "Must the stored report be encrypted?", "Yes", C.Resolved, "Encrypted at rest (decision 2026-10-01).", ["US1-1"])));
        var duplicate = list.Single(x => x.RequirementIds.Contains("FR-002"));
        duplicate.Clarifications.Single().State.Should().Be(C.Documented);
        duplicate.OpenQuestions.Should().NotContain(q => q.Contains("clarification", StringComparison.OrdinalIgnoreCase));
        list.Single(x => x.CanonicalKey.EndsWith("|US1-1")).Clarifications.Single().Resolution.Should().StartWith("Encrypted at rest", "a scenario id links too");
    }

    [Fact]
    public void An_inline_NEEDS_CLARIFICATION_marker_is_an_open_question_of_its_own_case()
    {
        var c = Discover(Spec()).Single(x => x.RequirementIds.Contains("FR-003"));
        c.OpenQuestions.Should().Contain("The specification marks this as NEEDS CLARIFICATION: retention period not specified");
    }

    [Fact]
    public void A_clarification_that_names_no_requirement_is_counted_in_the_summary_and_attached_nowhere()
    {
        var output = D.FromSpecification("p", Spec(new C("Q-9", "Which municipalities take part?", null, C.Open, null, [])));
        output.Capability.Detail.Should().Contain("1 clarification of the specification names no requirement");
        output.Candidates.Should().OnlyContain(x => x.Clarifications.Count == 0);
    }

    [Fact]
    public void Candidates_without_clarifications_keep_their_fingerprint()
    {
        var before = Discover(Spec()).ToDictionary(x => x.CandidateId, x => x.Fingerprint);
        var after = Discover(Spec(new C("Q-1", "What is a duplicate?", null, C.Open, null, ["FR-002"])));
        after.Where(x => !x.RequirementIds.Contains("FR-002")).Should().OnlyContain(x => before[x.CandidateId] == x.Fingerprint);
        after.Single(x => x.RequirementIds.Contains("FR-002")).Fingerprint.Should().NotBe(before[after.Single(x => x.RequirementIds.Contains("FR-002")).CandidateId],
            "a new clarification changes the case it concerns");
    }
}
