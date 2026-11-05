using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using BirkNext.Web.Shared.Components.Loading;
using BirkNext.Web.Tests.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// NEXT45 global loading: stale-response protection (LoadSequence), action buttons that cannot be submitted twice (BusyButton), load
/// failures with Retry (LoadError), and the Project Quality Dashboard showing loading — not "Not assessed" or "Nothing needs attention" —
/// while the review status is still being read, and an error with Retry instead of a stuck indicator when it fails.
/// </summary>
public class GlobalLoadingFeedbackTests : BunitContext
{
    // ── LoadSequence ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_newer_load_makes_the_older_ticket_stale_and_cancels_it()
    {
        using var loads = new LoadSequence();
        var first = loads.Begin();
        var second = loads.Begin();

        first.IsCurrent.Should().BeFalse("an older, slower response must never replace the newer one");
        first.Token.IsCancellationRequested.Should().BeTrue("the obsolete request is cancelled");
        second.IsCurrent.Should().BeTrue();
        second.Token.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public void Only_the_newest_load_ends_the_loading_state()
    {
        using var loads = new LoadSequence();
        var first = loads.Begin();
        var second = loads.Begin();

        loads.End(first).Should().BeFalse();
        loads.IsLoading.Should().BeTrue("the newer load still owns the indicator");
        loads.End(second).Should().BeTrue();
        loads.IsLoading.Should().BeFalse("the indicator is removed when the current load ends");
    }

    [Fact]
    public void Unmounting_makes_every_ticket_stale()
    {
        var loads = new LoadSequence();
        var ticket = loads.Begin();
        loads.Dispose();

        ticket.IsCurrent.Should().BeFalse();
        ticket.Token.IsCancellationRequested.Should().BeTrue();
        loads.IsLoading.Should().BeFalse();
        loads.Begin().IsCurrent.Should().BeFalse("nothing may be applied after the component is gone");
    }

    // ── BusyButton ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_busy_action_is_disabled_says_what_it_does_and_ignores_duplicate_clicks()
    {
        var gate = new TaskCompletionSource();
        var calls = 0;
        var cut = Render<BusyButton>(p => p.Add(b => b.Text, "Compare").Add(b => b.BusyText, "Comparing…").Add(b => b.TestId, "cmp")
            .Add(b => b.OnClick, async () => { calls++; await gate.Task; }));

        cut.Find("[data-testid=cmp]").Click();
        var button = cut.Find("[data-testid=cmp]");
        button.HasAttribute("disabled").Should().BeTrue();
        button.GetAttribute("aria-busy").Should().Be("true");
        button.TextContent.Should().Contain("Comparing…");
        cut.FindAll("[data-testid=cmp] .loading-spinner").Should().ContainSingle();

        calls.Should().Be(1);
        gate.SetResult();
        cut.WaitForAssertion(() => cut.Find("[data-testid=cmp]").HasAttribute("disabled").Should().BeFalse());
        cut.Find("[data-testid=cmp]").TextContent.Trim().Should().Be("Compare", "the label returns; the button never implies success");
    }

    [Fact]
    public void A_failed_action_releases_the_button()
    {
        var cut = Render<BusyButton>(p => p.Add(b => b.Text, "Run").Add(b => b.TestId, "run")
            .Add(b => b.OnClick, () => Task.FromException(new InvalidOperationException("boom"))));

        var click = () => cut.Find("[data-testid=run]").Click();
        click.Should().Throw<InvalidOperationException>();
        cut.Find("[data-testid=run]").HasAttribute("disabled").Should().BeFalse("no indicator is left stuck after a failure");
    }

    // ── LoadError ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_load_error_is_an_alert_with_a_retry_that_cannot_be_submitted_twice()
    {
        var gate = new TaskCompletionSource();
        var retries = 0;
        var cut = Render<LoadError>(p => p.Add(e => e.Message, "The API is not reachable.").Add(e => e.TestId, "err")
            .Add(e => e.OnRetry, async () => { retries++; await gate.Task; }));

        cut.Find("[data-testid=err]").GetAttribute("role").Should().Be("alert");
        cut.Find("[data-testid=err-message]").TextContent.Should().Be("The API is not reachable.");
        cut.Find("[data-testid=err-retry]").Click();
        cut.Find("[data-testid=err-retry]").HasAttribute("disabled").Should().BeTrue();
        retries.Should().Be(1);
        gate.SetResult();
        cut.WaitForAssertion(() => cut.Find("[data-testid=err-retry]").HasAttribute("disabled").Should().BeFalse());
    }

    [Fact]
    public void Inline_loading_is_a_polite_status_present_at_once_but_revealed_after_the_threshold()
    {
        var cut = Render<InlineLoading>(p => p.Add(l => l.Text, "Loading project…").Add(l => l.TestId, "ld"));

        var element = cut.Find("[data-testid=ld]");
        element.GetAttribute("role").Should().Be("status");
        element.GetAttribute("aria-live").Should().Be("polite");
        element.ClassList.Should().Contain("loading-delayed").And.NotContain("loading-revealed", "a fast load never flashes");
        cut.FindAll("[data-testid=ld] .loading-spinner").Should().ContainSingle();
    }

    // ── Project Quality Dashboard ─────────────────────────────────────────────────────────────

    private Mock<IWorkflowReadinessService> Dashboard(CurrentWorkspaceSnapshot workspace)
    {
        var service = new Mock<IWorkflowReadinessService>();
        Services.AddSingleton(service.Object);
        Services.AddSingleton(WorkspaceSnapshots.Projection(workspace).Object);
        Services.AddSingleton(new Mock<IReportExportService>().Object);
        Services.AddSingleton(new Mock<IWorkspaceSessionService>().Object);
        Services.AddSingleton(new Mock<IDashboardSnapshotService>().Object);
        Services.AddSingleton(new RuntimeReviewSessionService());
        Services.AddSingleton(new QualityReviewSessionService());
        return service;
    }

    [Fact]
    public void While_the_review_status_loads_the_dashboard_shows_loading_not_a_default_result()
    {
        var workspace = WorkspaceSnapshots.Loaded("M2LB", "import:abc", "M2LB", [.. CurrentWorkspaceSnapshot.WorkflowRoles]);
        var pending = new TaskCompletionSource<WorkflowReadiness>();
        Dashboard(workspace).Setup(s => s.GetReadinessAsync()).Returns(pending.Task);

        var cut = Render<BirkNext.Web.Pages.Dashboard>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=db-readiness-loading]"));
        cut.Find("[data-testid=db-next-step]").GetAttribute("aria-busy").Should().Be("true");
        cut.Find("[data-testid=db-kpis-loading]");
        cut.FindAll("[data-testid=db-kpi]").Should().BeEmpty("KPIs are derived from the review status and are not known yet");
        cut.Find("[data-testid=db-attention-loading]");
        cut.FindAll("[data-testid=db-attention-none]").Should().BeEmpty("'Nothing needs attention' is a result, not a loading state");
        cut.FindAll("[data-testid=db-readiness-row]").Should().BeEmpty();

        pending.SetResult(ReviewDecisionSemanticsTests.Readiness(workspace, []));

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=db-kpi]").Should().NotBeEmpty());
        cut.FindAll("[data-testid=db-readiness-loading]").Should().BeEmpty("the indicator is removed on success");
        cut.FindAll("[data-testid=db-kpis-loading]").Should().BeEmpty();
        cut.FindAll("[data-testid=db-attention-loading]").Should().BeEmpty();
    }

    [Fact]
    public void A_failed_review_status_is_an_error_with_retry_never_a_stuck_indicator()
    {
        var workspace = WorkspaceSnapshots.Loaded("M2LB", "import:abc", "M2LB", [.. CurrentWorkspaceSnapshot.WorkflowRoles]);
        var service = Dashboard(workspace);
        service.SetupSequence(s => s.GetReadinessAsync())
            .ThrowsAsync(new HttpRequestException("connection refused"))
            .ReturnsAsync(ReviewDecisionSemanticsTests.Readiness(workspace, []));

        var cut = Render<BirkNext.Web.Pages.Dashboard>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=db-readiness-error]"));
        cut.FindAll("[data-testid=db-readiness-loading]").Should().BeEmpty("the indicator is removed on failure");
        cut.Find("[data-testid=db-readiness-error-message]").TextContent.Should().Contain("could not be read").And.NotContain("connection refused", "no raw exception text");
        cut.Find("[data-testid=db-attention-unknown]").TextContent.Should().Contain("Not known");
        cut.FindAll("[data-testid=db-attention-none]").Should().BeEmpty();

        cut.Find("[data-testid=db-readiness-error-retry]").Click();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=db-kpi]").Should().NotBeEmpty());
        cut.FindAll("[data-testid=db-readiness-error]").Should().BeEmpty();
        service.Verify(s => s.GetReadinessAsync(), Times.Exactly(2));
    }
}
