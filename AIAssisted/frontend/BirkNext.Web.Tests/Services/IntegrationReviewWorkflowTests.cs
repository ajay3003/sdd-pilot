using BirkNext.Integrations;
using BirkNext.Web.Services;
using FluentAssertions;
using Xunit;

namespace BirkNext.Web.Tests.Services;

/// <summary>IQR guided steps: static review readiness, configuration completeness and active verification readiness stay three answers.</summary>
public sealed class IntegrationReviewWorkflowTests
{
    private static IntegrationReviewSubject Subject(string id) => new(id, id, IntegrationKind.EventHub, IntegrationReviewCapability.EventStream);

    private static IReadOnlyList<GuidedStep> Derive(IntegrationReviewReadiness r, int open = 0, IReadOnlyList<IntegrationReviewPrerunPresentation.AttentionItem>? attention = null,
        int? total = 3, int? ready = 0, bool? allowed = false)
    {
        var light = r.Subjects.Select((s, i) => new IntegrationReviewLightPresentation.Item(s, i >= open, i >= open ? "Resolved" : "Consumer mapping needs confirmation", null)).ToList();
        return IntegrationReviewWorkflow.Derive(new(r, light, attention ?? [], total, ready, allowed, key => $"integration-quality-review?view={key}", "/catalog"));
    }

    private static GuidedStep Step(IReadOnlyList<GuidedStep> steps, string id) => steps.Single(s => s.Id == id);

    [Fact]
    public void No_integration_in_scope_points_to_the_catalog_and_assesses_nothing()
    {
        var steps = Derive(new IntegrationReviewReadiness { CanRun = false, Reasons = ["No enabled integration."] });
        Step(steps, "select").State.Should().Be(GuidedStepState.Current);
        Step(steps, "select").Href.Should().Be("/catalog");
        Step(steps, "resolve").StatusLabel.Should().Be("Not assessed");
        Step(steps, "static").State.Should().Be(GuidedStepState.Waiting);
    }

    [Fact]
    public void Nine_open_decisions_of_twenty_five_come_first_but_the_static_review_stays_runnable_with_limitations()
    {
        var r = new IntegrationReviewReadiness { CanRun = true, Headline = "Can run with limitations", Reasons = ["No contract reference is configured."],
            Subjects = [.. Enumerable.Range(1, 25).Select(i => Subject($"i{i}"))] };
        var steps = Derive(r, open: 9, attention: [new("mappings", "Consumer mappings", "9 need confirmation", "Review mappings", "/catalog&focus=mappings"),
            new("contracts", "Contracts", "Not configured", "Manage contracts", "/catalog&focus=contracts")]);
        var resolve = Step(steps, "resolve");
        resolve.State.Should().Be(GuidedStepState.Current);
        resolve.StatusLabel.Should().Be("9 need your decision");
        resolve.Explanation.Should().Contain("16 of 25 resolved from evidence");
        resolve.Evidence.Should().Contain("never confirm a mapping");
        resolve.Href.Should().BeNull("the attention list below already links to the mappings; no duplicate action");
        Step(steps, "contracts").StatusLabel.Should().Be("Contracts: Not configured");
        Step(steps, "contracts").State.Should().Be(GuidedStepState.Waiting, "one current step");
        var review = Step(steps, "static");
        review.StatusLabel.Should().Be(IntegrationReviewPrerunPresentation.CanRunWithLimitations);
        review.Explanation.Should().Contain("can run now with 1 limitation").And.Contain("never sends a message");
        review.DisabledReason.Should().BeNull("limitations never block the static review");
    }

    [Fact]
    public void A_genuine_prerequisite_blocks_the_static_review_with_its_reason()
    {
        var steps = Derive(new IntegrationReviewReadiness { CanRun = false, Reasons = ["The integration is disabled."], Subjects = [Subject("a")] });
        var review = Step(steps, "static");
        review.State.Should().Be(GuidedStepState.Blocked);
        review.StatusLabel.Should().Be(IntegrationReviewPrerunPresentation.CannotRun);
        review.DisabledReason.Should().Be("The integration is disabled.");
    }

    [Fact]
    public void Active_verification_is_blocked_until_the_environment_allows_execution_and_never_done_from_this_page()
    {
        var r = new IntegrationReviewReadiness { CanRun = true, Headline = "Ready", Subjects = [Subject("a")] };
        Step(Derive(r, total: 3, ready: 0, allowed: false), "verify").State.Should().Be(GuidedStepState.Blocked);
        Step(Derive(r, total: 3, ready: 0, allowed: false), "verify").StatusLabel.Should().Be("0 of 3 journeys executable (environment-wide)");
        var allowed = Step(Derive(r, total: 3, ready: 2, allowed: true), "verify");
        allowed.State.Should().Be(GuidedStepState.Waiting);
        allowed.Explanation.Should().Contain("explicit confirmation").And.Contain("nothing runs from this page");
        Step(Derive(r, total: null, ready: null, allowed: null), "verify").StatusLabel.Should().Be("Not known");
        Derive(r).Select(s => s.Id).Should().Equal("select", "evidence", "resolve", "contracts", "static", "verify");
        Step(Derive(r), "static").State.Should().Be(GuidedStepState.Current, "everything before it is settled");
    }

    [Fact]
    public void Limitations_without_listed_reasons_never_read_as_zero_limitations()
    {
        var r = new IntegrationReviewReadiness { CanRun = true, Headline = "Can run with limitations", Subjects = [Subject("a")] };
        var review = Step(Derive(r), "static");
        review.Explanation.Should().NotContain("0 limitations").And.Contain("limitations (see what needs attention below)");
    }

    [Fact]
    public void Resolved_is_not_runtime_verified()
    {
        var r = new IntegrationReviewReadiness { CanRun = true, Headline = "Ready", Subjects = [Subject("a"), Subject("b")] };
        var resolve = Step(Derive(r, open: 0), "resolve");
        resolve.State.Should().Be(GuidedStepState.Done);
        resolve.Explanation.Should().Contain("resolved is not runtime verified");
        Step(Derive(r), "evidence").Evidence.Should().Be("Runtime evidence: no source configured.");
    }
}
