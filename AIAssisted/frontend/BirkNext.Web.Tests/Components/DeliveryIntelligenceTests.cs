using Bunit;
using BirkNext.DeliveryReporting;
using BirkNext.Web.Components;
using BirkNext.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static BirkNext.Web.Tests.Pages.DeliveryReportingPageTests;
using DeliveryReportingPage = BirkNext.Web.Pages.DeliveryReporting;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// NEXT53 shared Delivery Intelligence: one calculation layer and one component set for Delivery Overview (Project Leader) and Delivery Reports
/// (Scrum Master). Checklist progress, declared status and verification stay apart; tasks are only shown on request; large backlogs are paged.
/// </summary>
public sealed class DeliveryIntelligenceTests : BunitContext
{
    private static DeliveryStoryRow Row(string key, string title, DeliveryStatus? before, DeliveryStatus now, int done, int total,
        bool isNew = false, bool removed = false, bool changed = false, bool attention = false) => new()
    {
        FeatureKey = key, Title = title, Module = "Frontend", BaselineStatus = before, CurrentStatus = now, CurrentTaskCompleted = done, CurrentTaskTotal = total,
        BaselineTaskCompleted = isNew ? 0 : Math.Max(0, done - 1), BaselineTaskTotal = isNew ? 0 : total, NewFeature = isNew, RemovedFeature = removed,
        Movement = changed || isNew ? DeliveryMovement.Progressed : removed ? DeliveryMovement.Removed : DeliveryMovement.NoSignificantChange,
        HasActiveAttention = attention, Attention = attention ? ["US2 has no traceable tasks."] : [],
        Hierarchy = new()
        {
            Captured = true,
            Stories = [new("US1", "Search for a child", DeliveryItemChange.Unchanged) { Description = "As a case worker I want to find a child by name.", Criteria = [new("AC1", "Results within 2s", DeliveryItemChange.Unchanged)] }],
            Tasks = [new("T001", "T001", "Build the search box", done > 0, DeliveryTaskChange.Unchanged)],
            Phases = [new("P1", "Phase 1: Setup", DeliveryItemChange.Unchanged) { TaskTotal = total, TaskCompleted = done }],
        },
    };

    private static WeeklyDeliveryReport Report() => SampleReport() with
    {
        Stories =
        [
            Row("f::001-done", "Checklist done feature", DeliveryStatus.InProgress, DeliveryStatus.TasksComplete, 8, 8, changed: true),
            Row("f::002-declared", "Declared done feature", DeliveryStatus.Done, DeliveryStatus.Done, 4, 4),
            Row("f::003-new", "Brand new feature", null, DeliveryStatus.InProgress, 1, 5, isNew: true),
            Row("f::004-gone", "Removed feature", DeliveryStatus.InProgress, DeliveryStatus.InProgress, 2, 6, removed: true),
            Row("f::005-review", "Feature with a gap", DeliveryStatus.InProgress, DeliveryStatus.InProgress, 3, 6, attention: true),
        ],
    };

    // ── Shared calculations ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_complete_checklist_and_a_stated_done_are_never_verified_completion()
    {
        var report = Report();
        var cards = DeliveryIntelligence.Cards(DeliveryBacklog.FromReport(report), report);

        cards.Should().OnlyContain(c => !c.Verified && c.Evidence == DeliveryIntelligence.NoVerification);
        cards.Single(c => c.FeatureKey == "f::001-done").StatusLabel.Should().Be("Task checklist complete");
        cards.Single(c => c.FeatureKey == "f::002-declared").StatusLabel.Should().Be("Stated done");
        cards.Single(c => c.FeatureKey == "f::001-done").Progress.Should().Be("Checklist 8 / 8 (100%)");
        cards.Should().NotContain(c => c.FeatureKey == "f::004-gone", "a removed feature is not part of the current backlog");
        DeliveryIntelligence.Cards(DeliveryBacklog.FromReport(report), report, includeRemoved: true).Single(c => c.FeatureKey == "f::004-gone").Removed.Should().BeTrue();
    }

    [Fact]
    public void Both_pages_share_one_definition_of_verified_completion()
    {
        var report = Report();
        var overview = DeliveryDashboard.Current(DeliveryBacklog.FromReport(report)).Single(k => k.Key == "verified");
        var reports = DeliveryIntelligence.Comparison(report).Single(k => k.Key == "verified");

        overview.Value.Should().Be("Not assessed");
        reports.Value.Should().Be("Not assessed");
        reports.Info.Should().Be(overview.Info).And.Be(DeliveryDashboard.VerifiedInfo);
    }

    [Fact]
    public void Comparison_keeps_scope_change_declared_and_checklist_apart()
    {
        var kpis = DeliveryIntelligence.Comparison(Report()).ToDictionary(k => k.Key, k => k.Value);

        kpis["scope"].Should().Be("4 → 4", "baseline has the removed feature, current has the new one");
        kpis["new"].Should().Be("1");
        kpis["removed"].Should().Be("1");
        kpis["changed"].Should().Be("1");
        kpis["declared"].Should().Be("1 → 1", "an explicit Status: Done, counted as a declaration");
        kpis["attention"].Should().Be("0", "attention without a feature-attached item is not counted");
    }

    [Fact]
    public void Change_board_groups_by_change_and_never_fills_the_evidence_lane_from_checkboxes()
    {
        var report = Report();
        var lanes = DeliveryIntelligence.ChangeBoard(report, DeliveryIntelligence.Cards(DeliveryBacklog.FromReport(report), report, includeRemoved: true))
            .ToDictionary(l => l.Key);

        lanes.Keys.Should().Equal("new", "changed", "removed", "verified", "checklist-complete", "review");
        lanes["new"].Features.Select(f => f.FeatureKey).Should().Equal("f::003-new");
        lanes["removed"].Features.Select(f => f.FeatureKey).Should().Equal("f::004-gone");
        lanes["changed"].Features.Select(f => f.FeatureKey).Should().Equal("f::001-done");
        lanes["checklist-complete"].Features.Select(f => f.FeatureKey).Should().Equal(["f::001-done"], "only a checklist completed this period; a feature done in the baseline is not new progress");
        lanes["verified"].Features.Should().BeEmpty();
        lanes["verified"].NotAssessed.Should().StartWith("Not assessed");
        lanes["review"].Features.Select(f => f.FeatureKey).Should().Equal("f::005-review");
    }

    [Fact]
    public void Attention_required_lists_only_what_exists_with_where_to_act()
    {
        var report = Report() with { ScopeChanges = [new("New", "f::003-new", "Brand new feature", "Frontend", "New since the baseline")] };
        var entries = DeliveryIntelligence.AttentionRequired(DeliveryBacklog.FromReport(report), report, scopeCandidates: 2);

        entries.Select(e => e.Key).Should().Equal("attention", "scope-change", "candidates", "evidence");
        entries.Single(e => e.Key == "attention").Filter.Should().Be("attention=attention");
        entries.Single(e => e.Key == "evidence").Detail.Should().Contain("completion cannot be confirmed");
        entries.Single(e => e.Key == "candidates").Detail.Should().Contain("not counted until confirmed");
    }

    // ── Shared components ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Feature_list_pages_large_backlogs_instead_of_rendering_every_item()
    {
        var cards = Enumerable.Range(0, 1600).Select(i => new DeliveryFeatureCard($"m::{i:0000}", $"Feature {i}", "Module", null, BacklogStatus.InProgress, "In progress",
            DeliveryDashboardTone.Progress, "Checklist 1 / 2 (50%)", 50, DeliveryIntelligence.NoVerification, DeliveryDashboardTone.Neutral, "", 0, null, "—", DeliveryDashboardTone.Neutral, 4)).ToList();
        var list = Render<DeliveryFeatureList>(p => p.Add(x => x.Cards, cards));

        list.FindAll("[data-testid=dfl-card]").Should().HaveCount(25);
        list.Find("[data-testid=dfl-show-more]").Click();
        list.FindAll("[data-testid=dfl-card]").Should().HaveCount(50);

        var deep = Render<DeliveryFeatureList>(p => p.Add(x => x.Cards, cards).Add(x => x.SelectedKey, "m::0900"));
        deep.FindAll("[data-testid=dfl-card]").Should().HaveCount(901, "a deep-linked feature is always reachable");
        deep.FindAll("[data-testid=dfd]").Should().ContainSingle("only the selected feature is open");
    }

    [Fact]
    public void Feature_detail_shows_business_facts_first_and_tasks_only_under_technical_details()
    {
        var report = Report();
        var card = DeliveryIntelligence.Cards(DeliveryBacklog.FromReport(report), report).Single(c => c.FeatureKey == "f::005-review");
        string? opened = null;
        var detail = Render<DeliveryFeatureDetail>(p => p.Add(x => x.Card, card).Add(x => x.Row, report.Stories.Single(s => s.FeatureKey == card.FeatureKey))
            .Add(x => x.OnOpenTechnical, (string k) => opened = k));

        detail.Find("[data-testid=dfd-objective]").TextContent.Should().Contain("find a child by name");
        detail.Find("[data-testid=dfd-evidence]").TextContent.Should().Be(DeliveryIntelligence.NoVerification);
        detail.Find("[data-testid=dfd-attention]").TextContent.Should().Contain("US2 has no traceable tasks.");
        detail.Find("[data-testid=dfd-next]").TextContent.Should().Contain("Link implementation or QA verification evidence");
        var technical = detail.Find("[data-testid=dfd-technical]");
        technical.HasAttribute("open").Should().BeFalse("tasks, phases and ids are on request");
        technical.QuerySelector("[data-testid=dfd-tasks]").Should().NotBeNull();
        detail.Find("[data-testid=dfd-tasks]").Closest("[data-testid=dfd-technical]").Should().NotBeNull();
        detail.Find("[data-testid=dfd-open-technical]").Click();
        opened.Should().Be("f::005-review");
    }

    // ── Pages ────────────────────────────────────────────────────────────────────────────────────────────────────────

    private FakeApi Api()
    {
        var api = new FakeApi { Report = Report() };
        Services.AddSingleton<IDeliveryReportingApiService>(api);
        Services.AddSingleton<IDeliveryAzureDevOpsApiService>(new DeliveryAzureDevOpsImportTests.FakeApi());
        Services.AddSingleton(new BirkNext.Web.Services.Operations.BackgroundOperationService());
        Services.AddSingleton(new DeliveryReportingSession());
        JSInterop.Mode = JSRuntimeMode.Loose;
        return api;
    }

    [Fact]
    public void Delivery_Overview_is_a_dashboard_without_a_default_task_hierarchy()
    {
        Api();
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"delivery-overview?project={ProjectId}&work=All");
        var page = Render<BirkNext.Web.Pages.DeliveryOverview>();
        page.WaitForAssertion(() => page.Find("[data-testid=dd-attention-required]"));

        page.FindAll("[data-testid=dlr-workspace]").Should().BeEmpty("tasks are only in Technical details");
        page.FindAll("[data-testid=dws-split], .dws-split").Should().BeEmpty("no permanent narrow split panel");
        page.FindAll("[data-testid=do-sections] a").Select(a => a.TextContent.Trim()).Should().HaveCount(4).And.Contain(s => s.StartsWith("Technical details"));
        page.Find("[data-testid=dd-kpi][data-key=verified] .dd-kpi-value").TextContent.Should().Be("Not assessed");
        page.FindAll("[data-testid=dd-module-bar]").Should().NotBeEmpty("a genuine chart of checklist progress by module");

        page.Find("[data-testid=dd-att-entry][data-key=attention] [data-testid=dd-att-go]").GetAttribute("href").Should().Contain("section=features").And.Contain("attention=attention");
        nav.NavigateTo(nav.GetUriWithQueryParameter("section", "features"));
        page.WaitForAssertion(() => page.FindAll("[data-testid=dfl-card]").Should().HaveCount(4));
        page.FindAll("[data-testid=dfl-open]")[0].Click();
        nav.Uri.Should().Contain("f=", "the open feature is deep-linkable");
        page.WaitForAssertion(() => page.FindAll("[data-testid=dfd]").Should().ContainSingle());
        page.Find("[data-testid=dfd] [data-testid=dfd-technical]").HasAttribute("open").Should().BeFalse();
    }

    [Fact]
    public void Delivery_Reports_change_board_groups_changes_and_opens_the_shared_feature_detail()
    {
        Api();
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"delivery-reporting?project={ProjectId}&tab=changes");
        var page = Render<DeliveryReportingPage>();

        page.WaitForAssertion(() => page.FindAll("[data-testid=dcb-lane]").Should().HaveCount(6));
        page.Find("[data-testid=dcb-lane][data-key=verified] [data-testid=dcb-not-assessed]").TextContent.Should().StartWith("Not assessed");
        page.Find("[data-testid=dcb-lane][data-key=new] [data-testid=dcb-count]").TextContent.Should().Be("1");
        page.Find("[data-testid=dcb-lane][data-key=new] .dcb-item").Click();
        nav.Uri.Should().Contain("tab=stories").And.Contain("f=f%3A%3A003-new");
        page.WaitForAssertion(() => page.Find("[data-testid=dfl-card].is-open [data-testid=dfd]").GetAttribute("data-mode").Should().Be("comparison"));
    }

    [Fact]
    public void Delivery_Reports_summary_uses_the_shared_comparison_figures()
    {
        Api();
        Services.GetRequiredService<NavigationManager>().NavigateTo($"delivery-reporting?project={ProjectId}&tab=overview");
        var page = Render<DeliveryReportingPage>();

        page.WaitForAssertion(() => page.Find("[data-testid=dlr-comparison]"));
        page.Find("[data-testid=dlr-comparison] [data-key=checklist] .dd-kpi-label").TextContent.Should().StartWith("Task checklist");
        page.FindAll("[data-testid=dlr-change-bar]").Should().NotBeEmpty();
        page.Find("[data-testid=dlr-trend]").TextContent.Should().Contain("not a trend");
        page.Find("[data-testid=dlr-generate-more]").HasAttribute("open").Should().BeFalse("generating another report is secondary once a report is shown");
    }

    [Fact]
    public void Delivery_Overview_load_failure_shows_the_error_with_retry_never_an_empty_dashboard()
    {
        var api = Api();
        api.ProjectsError = "Delivery Reporting is not reachable.";
        Services.GetRequiredService<NavigationManager>().NavigateTo($"delivery-overview?project={ProjectId}");
        var page = Render<BirkNext.Web.Pages.DeliveryOverview>();

        page.WaitForAssertion(() => page.Find("[data-testid=do-load-error]").TextContent.Should().Contain("not reachable"));
        page.FindAll("[data-testid=dd-kpi]").Should().BeEmpty();
        page.FindAll("[data-testid=do-no-project]").Should().BeEmpty("a failed read is not an empty project list");
        api.ProjectsError = null;
        page.Find("[data-testid=do-load-error-retry]").Click();
        page.WaitForAssertion(() => page.Find("[data-testid=dd-kpi][data-key=features]"));
    }

    // ── NEXT53 follow-up: timeline, report history navigation, role defaults, quality evidence ladder ──────────────────────

    private static DeliveryReportSummaryRow Summary(Guid id, int day, string scope = "") =>
        new(id, $"Report {day}", $"2026-W{day}", new DateTimeOffset(2026, 10, day, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, day + 4, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, day + 4, 12, 0, 0, TimeSpan.Zero), DeliveryCoverage.Complete, "Monday", "Friday", day, 1) { ScopeLabel = scope };

    [Fact]
    public void The_timeline_lists_whole_project_reports_in_time_order_and_history_navigation_stays_within_one_kind_of_report()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid(), scoped = Guid.NewGuid();
        var rows = new[] { Summary(c, 19), Summary(a, 5), Summary(scoped, 12, "M2LB-language"), Summary(b, 12) };

        var points = DeliveryIntelligenceHistory.Timeline(rows, b);
        points.Select(p => p.ReportId).Should().Equal([a, b, c], "scoped reports are not points of the project timeline");
        points.Single(p => p.Current).ReportId.Should().Be(b);

        var (previous, next) = DeliveryIntelligenceHistory.Neighbours(rows, b);
        previous!.Id.Should().Be(a);
        next!.Id.Should().Be(c);
        DeliveryIntelligenceHistory.Neighbours(rows, scoped).Should().Be(((DeliveryReportSummaryRow?)null, (DeliveryReportSummaryRow?)null), "a scoped report has no whole-project neighbours");
    }

    [Fact]
    public void Milestones_are_only_what_the_report_states_this_period()
    {
        var milestones = DeliveryIntelligenceHistory.Milestones(Report());
        milestones.Select(m => (m.FeatureKey, m.Kind)).Should().Equal([("f::001-done", "Checklist completed")],
            "a feature stated done in the baseline already is no milestone of this period");
        DeliveryIntelligenceHistory.Milestones(null).Should().BeEmpty();
    }

    [Fact]
    public void The_quality_ladder_never_counts_candidates_as_coverage_and_execution_stays_not_assessed()
    {
        var summary = new ScopeSummary(3, 1, 12, 4, 0, new Dictionary<QualityCategory, int>(),
            new Dictionary<ScopeCoverage, int> { [ScopeCoverage.AutomatedCoverage] = 2, [ScopeCoverage.CandidateCoverageOnly] = 5, [ScopeCoverage.NoKnownCoverage] = 4, [ScopeCoverage.NotAssessed] = 1 }, 0, 0);
        var ladder = DeliveryIntelligenceHistory.QualityLadder(summary).ToDictionary(r => r.Key);

        ladder.Keys.Should().Equal("criteria", "linked", "candidate", "none", "not-assessed", "executed", "passed");
        ladder["criteria"].Value.Should().Be("12");
        ladder["linked"].Value.Should().Be("2");
        ladder["candidate"].Meaning.Should().Contain("not coverage until confirmed");
        ladder["executed"].Value.Should().Be("Not assessed");
        ladder["passed"].Value.Should().Be("Not assessed");

        ScopeCoverage? filtered = null;
        var cut = Render<DeliveryQualityLadder>(p => p.Add(x => x.Rows, DeliveryIntelligenceHistory.QualityLadder(summary)).Add(x => x.OnFilter, (ScopeCoverage? c) => filtered = c));
        cut.Find("[data-testid=dql-row][data-key=candidate] .dql-filter").Click();
        filtered.Should().Be(ScopeCoverage.CandidateCoverageOnly, "a rung filters the scope list");
        cut.FindAll("[data-testid=dql-row][data-key=executed] .dql-filter").Should().BeEmpty("execution is not assessed, so there is nothing to filter");
    }

    [Theory]
    [InlineData("ProjectLeader", "overview", "overview")]
    [InlineData("ScrumMaster", "features", "changes")]
    [InlineData("TestLead", "risks", "scope")]
    public void A_remembered_role_picks_the_default_view_and_never_changes_the_figures(string role, string overviewSection, string reportsTab)
    {
        var parsed = Enum.Parse<DeliveryRole>(role);
        DeliveryIntelligenceHistory.DefaultOverviewSection(parsed).Should().Be(overviewSection);
        DeliveryIntelligenceHistory.DefaultReportsTab(parsed).Should().Be(reportsTab);

        Api();
        JSInterop.Setup<string?>("localStorage.getItem", "birknext:delivery-role").SetResult(role);
        Services.GetRequiredService<NavigationManager>().NavigateTo($"delivery-overview?project={ProjectId}&work=All");
        var page = Render<BirkNext.Web.Pages.DeliveryOverview>();
        page.WaitForAssertion(() => page.Find($"[data-testid=do-section-{overviewSection}]").GetAttribute("aria-current").Should().Be("page"));
        page.Find("[data-testid=do-role-select]").GetAttribute("value").Should().Be(role);
    }

    [Fact]
    public void Delivery_Reports_navigates_report_history_and_the_overview_shows_the_timeline()
    {
        var api = Api();
        Guid older = Guid.NewGuid(), newer = Guid.NewGuid();
        api.ReportRows = [Summary(older, 5), Summary(ReportId, 12), Summary(newer, 19)];
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"delivery-reporting?project={ProjectId}&tab=overview&report={ReportId}");
        var page = Render<DeliveryReportingPage>();

        page.WaitForAssertion(() => page.Find("[data-testid=dlr-report-nav]"));
        page.Find("[data-testid=dlr-report-prev]").GetAttribute("href").Should().Contain($"report={older}").And.Contain("tab=overview");
        page.Find("[data-testid=dlr-report-next]").GetAttribute("href").Should().Contain($"report={newer}");

        nav.NavigateTo($"delivery-overview?project={ProjectId}&work=All");
        var overview = Render<BirkNext.Web.Pages.DeliveryOverview>();
        overview.WaitForAssertion(() => overview.FindAll("[data-testid=dtl-point]").Should().HaveCount(3));
        overview.FindAll("[data-testid=dtl-point]").Select(p => p.GetAttribute("data-current")).Should().Equal(["false", "true", "false"],
            "the point marked current is the report the overview shows (the fake API serves the sample report for any id)");
        overview.Find("[data-testid=dtl-milestone]").GetAttribute("data-kind").Should().Be("Checklist completed");
    }

    [Fact]
    public void A_report_without_neighbours_of_its_scope_still_says_so_and_links_all_reports()
    {
        var api = Api();
        api.ReportRows = [Summary(ReportId, 12), Summary(Guid.NewGuid(), 19, "M2LB-language")];
        Services.GetRequiredService<NavigationManager>().NavigateTo($"delivery-reporting?project={ProjectId}&tab=overview&report={ReportId}");
        var page = Render<DeliveryReportingPage>();
        page.WaitForAssertion(() => page.Find("[data-testid=dlr-report-only]").TextContent.Should().Be("The only report of this comparison"));
        page.Find("[data-testid=dlr-report-history-link]").GetAttribute("href").Should().Contain("tab=history");
        page.FindAll("[data-testid=dlr-report-prev], [data-testid=dlr-report-next]").Should().BeEmpty("a scoped report is not a neighbour of a whole-project report");
    }
}
