using BirkNext.Api.Services.IntegrationSuites;
using BirkNext.Api.Services.Specifications;
using BirkNext.IntegrationSuites;
using FluentAssertions;
using Xunit;

namespace BirkNext.Api.Tests.Services.IntegrationSuites;

/// <summary>
/// Gap ↔ specification: a Kode 6/7 question that the project's specification already answers is proposed with that requirement (never closed by
/// itself); a requirement that names the matter as open (OQ-nn) keeps the question open; a tester's confirmation or rejection decides. Found by the
/// K67 audit: BirkNext reported intake, Bufdata figures and reclassification as "needs domain decision" although the specification answers them.
/// </summary>
public sealed class SpecificationAnswerTests
{
    // A synthetic specification in the usual spec-kit format (own wording; not copied from any real project).
    private const string Spec = """
        # Feature Specification: School attendance intake

        ## Requirements

        ### Functional Requirements

        - **FR-002**: When the person lookup answers without a classification, the report MUST be held until the lookup succeeds.
        - **FR-008**: Reports for Kode 6/7 children MUST be processed like any other report: validated, stored and versioned. Visibility is
          evaluated on every read.
        - **FR-008a**: The service MUST NOT keep its own copy of a child's classification; it is read from Person every time it is needed and
          frozen at lock, where it decides delivery and counting only.
        - **FR-008b**: Rows for children who are Kode 6/7 at lock MUST NOT be part of the Bufdata dataset; their reports stay stored. Whether
          and how they are delivered later is open (OQ-26).
        - **FR-009**: Whether Kode 6/7 children are part of the expected population and the coverage figures MUST be a configuration option,
          off by default.
        - **FR-020**: A withdrawn report MUST close its chain.
        """;

    private static List<BirkNext.Specifications.SpecificationRequirementEntry> Catalog() =>
        SpecificationCatalogService.Read([new SpecificationCatalogService.Document("SkoleService/specs/001-intake/spec.md", Spec)]).Entries;

    private static Dictionary<string, List<SuiteSpecificationLink>> Links(Dictionary<string, string?>? decisions = null) =>
        IntegrationSuiteComposer.SpecificationLinksFor(Catalog(), decisions ?? []);

    [Fact]
    public void Suffixed_requirement_ids_are_separate_requirements()
    {
        Catalog().Select(e => e.RequirementId).Should().Contain(["FR-008", "FR-008A", "FR-008B", "FR-009"], "the parser upper-cases a suffix");
    }

    [Fact]
    public void Each_question_is_matched_to_the_requirement_that_answers_it()
    {
        var links = Links();

        links["security:intake-rule"].Should().ContainSingle(l => l.Answer == SuiteSpecificationAnswer.Answers)
            .Which.RequirementId.Should().Be("FR-008", "it says a Kode 6/7 report is processed like any other");
        links["security:intake-rule"].First().RequirementId.Should().Be("FR-008", "the answering requirement ranks first; FR-008B (open) only mentions stored reports");
        links["security:bufdata-aggregate"].Select(l => l.RequirementId).Should().Contain("FR-009");
        links["security:reclassification"].Select(l => l.RequirementId).Should().Contain("FR-008A");
        links["security:intake-rule"].First().Basis.Should().Contain("Not a decision until a tester confirms it");
        links["security:intake-rule"].First().SpecificationPath.Should().Be("SkoleService/specs/001-intake/spec.md");
    }

    [Fact]
    public void A_requirement_that_names_the_matter_as_open_keeps_the_question_open()
    {
        var periodLock = Links()["security:period-lock"];

        periodLock.First().Should().Match<SuiteSpecificationLink>(l => l.RequirementId == "FR-008B" && l.Answer == SuiteSpecificationAnswer.OpenInSpecification && l.OpenQuestion == "OQ-26");
        IntegrationSuiteComposer.Clarify("Are rows ever delivered?", null, null, periodLock).State.Should().Be(SuiteClarification.OpenInSpecification);
    }

    [Fact]
    public void A_requirement_that_only_mentions_Kode_6_7_is_not_proposed()
    {
        // FR-020 (withdrawal) says nothing about Kode 6/7, FR-002 nothing about a classified child: neither answers the correction question.
        Links()["security:correction"].Should().BeEmpty();
        IntegrationSuiteComposer.Clarify("How are corrections handled?", null, null, []).State.Should().Be(SuiteClarification.NotClarified);
    }

    [Fact]
    public void A_proposal_is_never_a_decision_and_a_confirmation_or_a_rejection_is_recorded_by_a_tester()
    {
        var intake = Links()["security:intake-rule"];
        IntegrationSuiteComposer.Clarify("What happens at intake?", null, null, intake).State.Should().Be(SuiteClarification.ProposedFromSpecification);

        var confirmed = IntegrationSuiteComposer.Clarify("What happens at intake?", "Acceptable",
            $"{IntegrationSuiteComposer.SpecificationNotePrefix}FR-008 (SkoleService/specs/001-intake/spec.md) answers this question.", intake);
        confirmed.State.Should().Be(SuiteClarification.ConfirmedFromSpecification);
        confirmed.Detail.Should().Contain("FR-008");
        IntegrationSuiteComposer.Clarify("What happens at intake?", "Acceptable", "Domain owner: stored like any other report", intake).State
            .Should().Be(SuiteClarification.ReviewedRule);

        // A rejected link is no longer proposed; the question stays open.
        var rejected = Links(new() { [IntegrationSuiteComposer.RejectedLinkKey("security:intake-rule", "FR-008")] = "NotApplicable" });
        rejected["security:intake-rule"].Should().NotContain(l => l.RequirementId == "FR-008");
        IntegrationSuiteComposer.Clarify("What happens at intake?", null, null, rejected["security:intake-rule"]).State.Should().NotBe(SuiteClarification.ConfirmedFromSpecification);
    }

    [Fact]
    public void Without_a_specification_catalog_nothing_is_proposed()
    {
        IntegrationSuiteComposer.SpecificationLinksFor([], new Dictionary<string, string?>()).Values.Should().OnlyContain(l => l.Count == 0);
    }

    [Theory]
    [InlineData("Whether and how they are delivered later is open (OQ-26).", "OQ-26")]
    [InlineData("This is to be decided by the domain owner.", "\"to be decided\"")]
    [InlineData("Dette avklares senere.", "\"avklares senere\"")]
    [InlineData("Reports MUST be processed like any other report.", null)]
    [InlineData("The opening hours are fixed.", null)]
    public void An_open_question_is_recognised_in_the_wording(string text, string? expected)
    {
        SpecificationAnswerMatcher.OpenMarker(text).Should().Be(expected);
    }
}
