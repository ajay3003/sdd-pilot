using BirkNext.DeliveryReporting;
using DeliveryReportingPage = BirkNext.Web.Pages.DeliveryReporting;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using static BirkNext.Web.Tests.Pages.DeliveryReportingPageTests;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Delivery Reports with "Filter by scope": Generate offers an explicit mode (Scope context by default, or Filter by scope); a filtered report shows its
/// included / excluded / unresolved counts and names the figures it cannot restrict; Report history compares two frozen reports from the URL
/// (?compare=baseline,current) with percentages only like-for-like, mismatches named, and drill-down links to each report.
/// </summary>
public sealed class DeliveryScopeFilterPageTests : BunitContext
{
    private static readonly Guid ScopeA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid RevisionA3 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");
    private static readonly Guid Second = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private readonly ReportsApi _api = new();
    private readonly DeliveryReportingSession _session = new();

    /// <summary>The page test fake with several distinct frozen reports (ReportAsync by id); comparison uses the interface's shared-rule default.</summary>
    private sealed class ReportsApi : IDeliveryReportingApiService
    {
        public FakeApi Inner { get; } = new();
        public Dictionary<Guid, WeeklyDeliveryReport> Reports { get; } = new() { [ReportId] = SampleReport() };
        public Task<(List<DeliveryProject> Projects, string? Error)> ProjectsAsync(CancellationToken ct = default) => Inner.ProjectsAsync(ct);
        public Task<(DeliveryProject? Project, string? Error)> CreateProjectAsync(string name, string? description, CancellationToken ct = default) => Inner.CreateProjectAsync(name, description, ct);
        public Task<(DeliveryImportPreview? Preview, string? Error)> PreviewAsync(Guid? projectId, string fileName, Stream content, CancellationToken ct = default) => Inner.PreviewAsync(projectId, fileName, content, ct);
        public Task<DeliveryImportCommitResult> CommitAsync(Guid stagingId, DeliveryImportCommitRequest request, CancellationToken ct = default) => Inner.CommitAsync(stagingId, request, ct);
        public Task DiscardAsync(Guid stagingId, CancellationToken ct = default) => Inner.DiscardAsync(stagingId, ct);
        public Task<List<DeliverySnapshotSummary>> SnapshotsAsync(Guid projectId, CancellationToken ct = default) => Inner.SnapshotsAsync(projectId, ct);
        public Task<DeliverySnapshotDetail?> SnapshotAsync(Guid projectId, Guid snapshotId, CancellationToken ct = default) => Inner.SnapshotAsync(projectId, snapshotId, ct);
        public Task<(DeliverySnapshotSummary? Snapshot, string? Error)> UpdateSnapshotAsync(Guid projectId, Guid snapshotId, UpdateDeliverySnapshotRequest request, CancellationToken ct = default) =>
            Inner.UpdateSnapshotAsync(projectId, snapshotId, request, ct);
        public Task<string?> DeleteSnapshotAsync(Guid projectId, Guid snapshotId, CancellationToken ct = default) => Inner.DeleteSnapshotAsync(projectId, snapshotId, ct);
        public Task<DeliveryBaselineSuggestion?> SuggestBaselineAsync(Guid projectId, Guid currentId, CancellationToken ct = default) => Inner.SuggestBaselineAsync(projectId, currentId, ct);
        public Task<List<DeliveryReportSummaryRow>> ReportsAsync(Guid projectId, CancellationToken ct = default) =>
            Task.FromResult(Reports.Values.OrderByDescending(r => r.GeneratedAt).Select(r => new DeliveryReportSummaryRow(r.ReportId, r.Title, r.WeekLabel, r.PeriodStart, r.PeriodEnd,
                r.GeneratedAt, r.Coverage, r.Baseline.Label, r.Current.Label, r.Stories.Count(s => s.Changed), r.Attention.Count) { DisplayTitle = DeliveryReportTitles.Display(r) }).ToList());
        public Task<(WeeklyDeliveryReport? Report, string? Error)> GenerateAsync(GenerateWeeklyReportRequest request, CancellationToken ct = default) => Inner.GenerateAsync(request, ct);
        public Task<WeeklyDeliveryReport?> ReportAsync(Guid projectId, Guid reportId, CancellationToken ct = default) => Task.FromResult(Reports.GetValueOrDefault(reportId));
        public Task<(DeliveryReportExport? Export, string? Error)> ExportAsync(Guid projectId, Guid reportId, DeliveryExportFormat format, bool appendix, bool unchanged, CancellationToken ct = default) =>
            Inner.ExportAsync(projectId, reportId, format, appendix, unchanged, ct);
        public Task<string?> DeleteReportAsync(Guid projectId, Guid reportId, CancellationToken ct = default) => Inner.DeleteReportAsync(projectId, reportId, ct);
    }

    public DeliveryScopeFilterPageTests()
    {
        Services.AddSingleton<IDeliveryReportingApiService>(_api);
        Services.AddSingleton<IDeliveryAzureDevOpsApiService>(new BirkNext.Web.Tests.Components.DeliveryAzureDevOpsImportTests.FakeApi());
        Services.AddSingleton(new BirkNext.Web.Services.Operations.BackgroundOperationService());
        Services.AddSingleton(_session);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static DeliveryScopeProvenance Selection(int revision = 3, bool filtered = false, DeliveryScopeFilterCounts? counts = null) => new()
    {
        Scopes = [new(ScopeA, "Release 1", revision, RevisionA3)], MembershipResolution = DeliveryScopeProvenance.DefaultMembershipResolution, ProjectKey = "import-1",
        Mode = filtered ? DeliveryScopeMode.Filter : DeliveryScopeMode.Context, Filtered = filtered, Counts = counts,
    };

    private static DeliveryScopeFilterCounts Counts() => new()
    {
        Included = 3, Excluded = 9, MembersMatched = 3, MembersUnresolved = 2, IncludedFeatures = 1, NotScopeResolvable = ["Tasks completed this period", "Shared & foundational tasks"],
    };

    private static WeeklyDeliveryReport FilteredReport() => SampleReport() with
    {
        ReportId = Second, GeneratedAt = SampleReport().GeneratedAt.AddHours(1), Stories = [SampleReport().Stories[0]],
        Metrics = [new("stories-changed", "Stories changed", 1), new("moved-forward", "Moved forward", 1)], ScopeProvenance = Selection(filtered: true, counts: Counts()),
    };

    private IRenderedComponent<DeliveryReportingPage> RenderAt(string query)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo($"delivery-reporting{query}");
        return Render<DeliveryReportingPage>();
    }

    // ── Generate: explicit mode ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Generate_defaults_to_scope_context_and_sends_filter_by_scope_only_when_chosen()
    {
        _session.SetScopeProvenance(ProjectId, Selection());
        var page = RenderAt($"?project={ProjectId}&tab=overview");

        page.WaitForAssertion(() => page.Find("[data-testid=dlr-generate-scope-mode-context]").HasAttribute("checked").Should().BeTrue());
        page.Find("[data-testid=dlr-generate-scope-provenance]").GetAttribute("data-mode").Should().Be(nameof(DeliveryScopeMode.Context));
        page.Find("[data-testid=dlr-generate-run]").Click();
        page.WaitForAssertion(() => _api.Inner.LastGenerate.Should().NotBeNull());
        _api.Inner.LastGenerate!.ScopeProvenance!.Mode.Should().Be(DeliveryScopeMode.Context, "the default never silently narrows a report");

        page.Find("[data-testid=dlr-generate-scope-mode-filter]").Change(true);
        page.WaitForAssertion(() => page.Find("[data-testid=dlr-generate-scope-provenance]").GetAttribute("data-mode").Should().Be(nameof(DeliveryScopeMode.Filter)));
        page.Find("[data-testid=dlr-generate-scope-provenance]").TextContent.Should().Contain("Filter by scope: Release 1 rev 3").And.Contain(DeliveryScopeProvenanceText.NotScopeResolvable);
        page.Find("[data-testid=dlr-generate-run]").Click();
        page.WaitForAssertion(() => _api.Inner.LastGenerate!.ScopeProvenance!.Mode.Should().Be(DeliveryScopeMode.Filter));
        _api.Inner.LastGenerate!.ScopeProvenance!.Scopes.Single().RevisionId.Should().Be(RevisionA3);
        _api.Inner.LastGenerate.ScopeProvenance.ProjectKey.Should().Be("import-1");
    }

    // ── A filtered report ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_filtered_report_shows_its_counts_and_names_figures_that_are_not_scope_resolvable()
    {
        _api.Reports.Clear();
        _api.Reports[Second] = FilteredReport();
        var page = RenderAt($"?project={ProjectId}&tab=overview&report={Second}");

        page.WaitForAssertion(() => page.Find("[data-testid=dlr-scope-provenance]").GetAttribute("data-filtered").Should().Be("true"));
        page.Find("[data-testid=dlr-scope-provenance]").TextContent.Should().Contain("Filtered by scope: Release 1 rev 3");
        page.Find("[data-testid=dlr-scope-filter-counts]").TextContent.Should()
            .Contain("3 included · 9 excluded (outside scope) · 2 scope members unresolved (named by the scope, not in this Delivery population)");
        page.Find("[data-testid=dlr-not-scope-resolvable]").TextContent.Should().Contain("Tasks completed this period").And.Contain("Shared & foundational tasks");
        page.Find("[data-testid=dlr-comparison]").QuerySelectorAll("[data-testid=dd-kpi]").Single(k => k.GetAttribute("data-key") == "checklist").TextContent.Should().Contain(DeliveryScopeProvenanceText.NotScopeResolvable)
            .And.NotContain("%", "a feature-wide checklist is never shown under a scope");
    }

    [Fact]
    public void A_filtered_report_with_no_applicable_items_says_so_instead_of_zero_figures()
    {
        _api.Reports.Clear();
        _api.Reports[Second] = FilteredReport() with
        {
            Metrics = [], Stories = [],
            ScopeProvenance = Selection(filtered: true, counts: new() { Included = 0, Excluded = 12, MembersUnresolved = 4 }),
        };
        var page = RenderAt($"?project={ProjectId}&tab=overview&report={Second}");

        page.WaitForAssertion(() => page.Find("[data-testid=dlr-scope-filter]").GetAttribute("data-no-items").Should().Be("true"));
        page.Find("[data-testid=dlr-scope-filter-empty]").TextContent.Should().StartWith(DeliveryScopeProvenanceText.NoApplicableItems);
        page.Find("[data-testid=dlr-comparison]").QuerySelectorAll("[data-testid=dd-kpi]").Should().ContainSingle().Which.TextContent.Should().Contain(DeliveryScopeProvenanceText.NoApplicableItems).And.NotContain("0%");
    }

    // ── Report vs report ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Two_like_for_like_reports_from_the_url_show_changed_figures_with_percentages_and_links_to_each_report()
    {
        var later = SampleReport() with
        {
            ReportId = Second, GeneratedAt = SampleReport().GeneratedAt.AddDays(7),
            Metrics = [new("stories-changed", "Stories changed", 2), new("moved-forward", "Moved forward", 1), new("attention", "Needs attention", 0)],
        };
        _api.Reports[Second] = later;
        var page = RenderAt($"?project={ProjectId}&tab=history&compare={ReportId},{Second}");

        page.WaitForAssertion(() => page.Find("[data-testid=dlr-compare-result]").GetAttribute("data-like-for-like").Should().Be("true"));
        var metric = page.FindAll("[data-testid=dlr-compare-metric]").Should().ContainSingle().Subject;
        metric.GetAttribute("data-key").Should().Be("stories-changed");
        metric.TextContent.Should().Contain("1 → 2").And.Contain("+1");
        page.Find("[data-testid=dlr-compare-percent]").TextContent.Trim().Should().Be("+100.0 %");
        page.FindAll("[data-testid=dlr-compare-mismatch]").Should().BeEmpty();
        page.FindAll("[data-testid=dlr-compare-open]").Select(a => a.GetAttribute("href")).Should()
            .Contain(h => h!.Contains($"report={ReportId}")).And.Contain(h => h!.Contains($"report={Second}"));
        page.Find("[data-testid=dlr-compare-scope]").GetAttribute("data-match").Should().Be(nameof(DeliveryScopeProvenanceMatch.MissingBoth), "legacy reports: provenance not recorded");
        page.FindAll("[data-testid=dlr-compare-side]").Should().HaveCount(2).And.OnlyContain(s => s.TextContent.Contains(DeliveryScopeProvenanceText.NotRecorded));
    }

    [Fact]
    public void A_whole_project_report_compared_with_a_filtered_one_names_the_mismatch_and_shows_no_percentage()
    {
        _api.Reports[Second] = FilteredReport();
        var page = RenderAt($"?project={ProjectId}&tab=history&compare={ReportId},{Second}");

        page.WaitForAssertion(() => page.Find("[data-testid=dlr-compare-result]").GetAttribute("data-like-for-like").Should().Be("false"));
        page.Find("[data-testid=dlr-compare-mismatch]").TextContent.Should().Be("Different scope modes: whole population vs filtered by scope.");
        page.FindAll("[data-testid=dlr-compare-percent]").Should().BeEmpty();
        page.Find("[data-testid=dlr-compare-summary]").TextContent.Should().Contain(DeliveryReportComparer.NotLikeForLikeText);
        page.FindAll("[data-testid=dlr-compare-note]").Select(n => n.TextContent).Should().Contain(n => n.Contains("Not in the current report") || n.Contains(DeliveryScopeProvenanceText.NotScopeResolvable));
    }

    [Fact]
    public void Choosing_two_reports_keeps_the_comparison_in_the_url()
    {
        _api.Reports[Second] = SampleReport() with { ReportId = Second, GeneratedAt = SampleReport().GeneratedAt.AddDays(7) };
        var page = RenderAt($"?project={ProjectId}&tab=history");
        page.WaitForElement("[data-testid=dlr-compare]");
        page.FindAll("[data-testid=dlr-compare-result]").Should().BeEmpty();

        page.Find("[data-testid=dlr-compare-baseline]").Change(ReportId.ToString());
        page.Find("[data-testid=dlr-compare-current]").Change(Second.ToString());

        var nav = Services.GetRequiredService<NavigationManager>();
        page.WaitForAssertion(() => Uri.UnescapeDataString(nav.Uri).Should().Contain($"compare={ReportId},{Second}"));
        page.WaitForAssertion(() => page.Find("[data-testid=dlr-compare-result]"));
        page.Find("[data-testid=dlr-compare-clear]").Click();
        page.WaitForAssertion(() => Uri.UnescapeDataString(nav.Uri).Should().NotContain("compare="));
    }
}
