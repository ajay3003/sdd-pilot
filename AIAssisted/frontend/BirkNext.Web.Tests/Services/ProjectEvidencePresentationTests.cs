using BirkNext.Web.Models;
using BirkNext.Web.Services.ProjectEvidence;
using FluentAssertions;
using Xunit;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Target Environment → Project presentation: one explicit decision from the same counts as before ("Ready with limitations" when every required
/// role is bound but one is Partial — never "Ready"), Partial explained beside Needs review 0, short reasons, explicit provenance badges and
/// context-specific actions.
/// </summary>
public sealed class ProjectEvidencePresentationTests
{
    private static EvidenceRoleRow Row(string key, string label, EvidenceRoleState state, bool required = true, ProjectEvidenceCardinality cardinality = ProjectEvidenceCardinality.Single) =>
        new(new EnvironmentProjectEvidenceBinding
        {
            RoleKey = key, Label = label, Requirement = required ? ProjectEvidenceRequirement.Required : ProjectEvidenceRequirement.Optional, Cardinality = cardinality,
        }, state, [], [], []);

    private static ProjectEvidenceView View(params EvidenceRoleRow[] rows) =>
        new("dev", "Development", new EnvironmentProject { ProjectName = "M2LB" }, null, rows, [], [], [])
        { TargetApplication = new TargetApplicationIdentity { Name = "M2LB" } };

    [Fact]
    public void All_required_bound_with_one_partial_is_ready_with_limitations_and_explains_partial_beside_no_review()
    {
        var view = View(Row("application-source", "Application source", EvidenceRoleState.Partial), Row("documentation", "Documentation", EvidenceRoleState.Ready),
            Row("infrastructure", "Infrastructure evidence", EvidenceRoleState.Ready, required: false));

        var decision = ProjectEvidencePresentation.Decision(view);
        decision.Label.Should().Be("Ready with limitations");
        decision.Explanation.Should().Be("All required evidence roles are bound. 1 required role has partial source coverage. No manual decisions are required.");
        ProjectEvidencePresentation.ProjectState(view).Should().Be("Partial — all required roles are bound, but 1 required role has incomplete evidence coverage");

        var cards = ProjectEvidencePresentation.Cards(view, view.TargetApplication, "Detected automatically · Very high confidence");
        cards.Select(c => (c.Key, c.Value)).Should().Equal(("application", "M2LB"), ("project", "Partial"), ("required", "2 / 2 bound"), ("optional", "1 available"), ("review", "0"));
        cards.Single(c => c.Key == "required").Detail.Should().Be("1 ready · 1 partial");
    }

    [Theory]
    [InlineData(EvidenceRoleState.Ready, "Ready")]
    [InlineData(EvidenceRoleState.Missing, "Incomplete")]
    [InlineData(EvidenceRoleState.Conflict, "Needs review")]
    [InlineData(EvidenceRoleState.NeedsDecision, "Needs review")]
    public void The_decision_follows_the_role_states(EvidenceRoleState state, string expected) =>
        ProjectEvidencePresentation.Decision(View(Row("application-source", "Application source", state))).Label.Should().Be(expected);

    [Fact]
    public void Reasons_are_short_with_the_full_diagnostic_separate_and_actions_are_context_specific()
    {
        var partial = Row("application-source", "Application source", EvidenceRoleState.Partial);
        ProjectEvidencePresentation.ShortReason(partial).Should().Be("Partial — application source coverage is incomplete");
        ProjectEvidencePresentation.Action(partial).Should().Be("Resolve partial coverage");
        ProjectEvidencePresentation.Action(Row("documentation", "Documentation", EvidenceRoleState.Ready)).Should().Be("Change source");
        ProjectEvidencePresentation.Action(Row("infrastructure", "Infrastructure", EvidenceRoleState.Ready, cardinality: ProjectEvidenceCardinality.Multiple)).Should().Be("Add supporting source");
        ProjectEvidencePresentation.Action(Row("infrastructure", "Infrastructure", EvidenceRoleState.Conflict)).Should().Be("Review association");
        ProjectEvidencePresentation.Action(Row("documentation", "Documentation", EvidenceRoleState.Missing)).Should().Be("Select source");
    }

    [Theory]
    [InlineData("User", "Confirmed", "Confirmed by you")]
    [InlineData("ProjectSelection", "Confirmed", "Bound from Project setup")]
    [InlineData("Detected", "Auto-associated", "Associated automatically")]
    [InlineData("TestPack", "Needs confirmation", "Suggested by Test Pack")]
    public void Provenance_is_an_explicit_badge_that_keeps_its_full_origin(string boundBy, string badge, string origin)
    {
        var p = ProjectEvidencePresentation.Provenance(new ProjectEvidenceSourceReference { BoundBy = boundBy });
        (p.Label, p.Origin).Should().Be((badge, origin));
    }
}
