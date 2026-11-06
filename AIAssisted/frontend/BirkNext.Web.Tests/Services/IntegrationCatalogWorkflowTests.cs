using BirkNext.Web.Services;
using FluentAssertions;
using Xunit;

namespace BirkNext.Web.Tests.Services;

/// <summary>Integration Catalog guided steps: discovered ≠ accepted ≠ configured ≠ validated; one current step; nothing confirmed for the user.</summary>
public sealed class IntegrationCatalogWorkflowTests
{
    private static IntegrationsSummary Catalog(int configured = 0, int confirm = 0, int configure = 0) => new(configured, configured, configured - confirm - configure, confirm, configure, 0, []);
    private static SuggestionSummary Sources(bool available = true, int total = 7) => new(available, total, 0, 0, 0, 0);
    private static IntegrationPackProposal Proposal(bool ready) =>
        new(new BirkNext.Integrations.IntegrationTemplateView("k" + Guid.NewGuid().ToString("N")[..6], "Skolenærværsrapport → Bufdata", "", BirkNext.Integrations.IntegrationKind.HttpApi, "pack", "Pack"), ready, ready ? null : "Choose the evidence source", []);

    private static GuidedStep Step(IReadOnlyList<GuidedStep> steps, string id) => steps.Single(s => s.Id == id);

    [Fact]
    public void No_source_evidence_points_to_Source_Analysis_and_invents_no_proposal()
    {
        var steps = IntegrationCatalogWorkflow.Derive(Catalog(), [], Sources(available: false), "Not configured");
        Step(steps, "discover").State.Should().Be(GuidedStepState.Current);
        Step(steps, "discover").Href.Should().Be("/source-analysis");
        Step(steps, "review").State.Should().Be(GuidedStepState.Waiting);
        Step(steps, "validate").StatusLabel.Should().Be("Not assessed");
    }

    [Fact]
    public void Three_proposals_with_one_decision_are_reviewed_first_and_nothing_counts_as_configured()
    {
        var steps = IntegrationCatalogWorkflow.Derive(Catalog(), [Proposal(true), Proposal(true), Proposal(false)], Sources(), "Not configured");
        Step(steps, "discover").Explanation.Should().Contain("3 relevant proposals");
        Step(steps, "discover").Evidence.Should().Contain("7 technical relationships").And.Contain("evidence, not proposals");
        Step(steps, "review").State.Should().Be(GuidedStepState.Current);
        Step(steps, "review").Explanation.Should().Contain("2 ready to review · 1 need a decision").And.Contain("Nothing is added for you");
        Step(steps, "save").State.Should().Be(GuidedStepState.Waiting, "a proposal stays a proposal until it is saved");
        Step(steps, "validate").State.Should().Be(GuidedStepState.Waiting);
        steps.Count(s => s.State == GuidedStepState.Current).Should().Be(1);
    }

    [Fact]
    public void Configured_integrations_with_unconfirmed_mappings_need_a_person_and_identity_conflicts_stay_in_Relationship_Review()
    {
        var steps = IntegrationCatalogWorkflow.Derive(Catalog(configured: 4, confirm: 2, configure: 1), [], Sources(), "Not configured");
        var resolve = Step(steps, "resolve");
        resolve.State.Should().Be(GuidedStepState.Current);
        resolve.StatusLabel.Should().Be("3 of 4 need attention");
        resolve.Explanation.Should().Contain("stay unconfirmed until a person confirms them");
        resolve.Evidence.Should().Contain("Relationship Review").And.Contain("never confirms or rejects");
        Step(steps, "save").State.Should().Be(GuidedStepState.Done);
        Step(steps, "validate").State.Should().Be(GuidedStepState.Waiting, "only one step is current");
    }

    [Fact]
    public void Complete_configuration_is_still_not_validated_or_runtime_verified()
    {
        var steps = IntegrationCatalogWorkflow.Derive(Catalog(configured: 3), [], Sources(), "Not verified");
        var validate = Step(steps, "validate");
        validate.State.Should().Be(GuidedStepState.Current);
        validate.StatusLabel.Should().Be("Not validated here");
        validate.Explanation.Should().Contain("Configured is not validated").And.Contain("runtime is verified only from runtime evidence");
        validate.Evidence.Should().Be("Runtime evidence: Not verified.");
        validate.Href.Should().Be("/integration-quality-review");
    }
}
