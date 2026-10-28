using BirkNext.Integrations;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Integration Catalog curation: a relevant proposal is an integration an applicable Test Pack requires, backed by recognized source evidence
/// and not configured yet. Source-discovered relationships (debug panels, internal calls, IaC wiring) are technical evidence and are never
/// promoted by this rule; ambiguity and missing environment values are typed decisions, never auto-resolved. Generic fixtures.
/// </summary>
public sealed class IntegrationProposalCurationTests
{
    private static readonly TestPackSourceRole Mu = new("mu", "MU (Meldingsutveksleren)", "");

    private static IntegrationTemplateView Template(string key, params TestPackSourceCandidate[] candidates) =>
        new(key, $"Pack – {key}", "", IntegrationKind.HttpApi, "pack", "Demo pack")
        {
            Journeys = ["Rapportmottak"], Flow = "Altinn → MU",
            EvidenceSource = candidates.Length == 0 ? null : new TestPackSourceStatus(Mu, TestPackSourceState.AvailableNotBound) { Candidates = candidates },
        };

    private static TestPackSourceCandidate Candidate(string archive, char fp, bool bound = false) => new(Guid.NewGuid(), archive, new string(fp, 64), DateTimeOffset.UnixEpoch) { Bound = bound };

    [Fact]
    public void Only_test_pack_requirements_with_imported_evidence_become_relevant_proposals()
    {
        var proposals = IntegrationPackProposals.For(
        [
            Template("intake", Candidate("Tiltak 2.zip", 'a')),
            Template("no-evidence"),
            Template("configured", Candidate("Tiltak 2.zip", 'a')) with { AlreadyConfigured = ["dev:intake"] },
        ]);

        proposals.Select(p => p.Template.Key).Should().Equal("intake");
        var intake = proposals.Single();
        (intake.Ready, intake.DecisionKind).Should().Be((true, ProposalDecisionKind.None));
        intake.EvidenceItems.Should().Equal("Demo pack Test Pack", "MU (Meldingsutveksleren) source: Tiltak 2.zip");
        intake.Template.Flow.Should().Be("Altinn → MU");
    }

    [Fact]
    public void Ambiguous_evidence_and_missing_values_are_typed_decisions_and_nothing_is_chosen()
    {
        var ambiguous = IntegrationPackProposals.For([Template("intake", Candidate("hub-a.zip", 'a'), Candidate("hub-b.zip", 'b'))]).Single();
        (ambiguous.Ready, ambiguous.DecisionKind, ambiguous.DecisionRole).Should().Be((false, ProposalDecisionKind.ChooseEvidenceSource, "MU (Meldingsutveksleren)"));
        ambiguous.DecisionItems.Should().Equal("hub-a.zip", "hub-b.zip");

        var bound = IntegrationPackProposals.For([Template("intake", Candidate("hub-a.zip", 'a', bound: true), Candidate("hub-b.zip", 'b'))]).Single();
        bound.DecisionKind.Should().Be(ProposalDecisionKind.None, "an IQR binding settles which source is the evidence");

        var values = IntegrationPackProposals.For([Template("cdc", Candidate("M2LB.zip", 'm')) with { NeedsConfiguration = ["endpoint", "source"] }]).Single();
        (values.Ready, values.DecisionKind).Should().Be((false, ProposalDecisionKind.MissingEnvironmentValues));
        values.DecisionItems.Should().HaveCount(2);
    }
}
