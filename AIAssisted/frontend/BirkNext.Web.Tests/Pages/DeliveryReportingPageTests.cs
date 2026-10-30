using BirkNext.DeliveryReporting;
using DeliveryReportingPage = BirkNext.Web.Pages.DeliveryReporting;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Pages;

/// <summary>Delivery Reporting page: its own projects, snapshots and frozen reports only; management-first overview; default story filter.</summary>
public sealed class DeliveryReportingPageTests : BunitContext
{
    internal static readonly Guid ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ReportId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset Monday = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Friday = new(2026, 10, 30, 15, 0, 0, TimeSpan.Zero);
    private readonly FakeApi _api = new();
    private readonly BirkNext.Web.Tests.Components.DeliveryAzureDevOpsImportTests.FakeApi _ado = new();

    public DeliveryReportingPageTests()
    {
        Services.AddSingleton<IDeliveryReportingApiService>(_api);
        Services.AddSingleton<IDeliveryAzureDevOpsApiService>(_ado);
        Services.AddSingleton(new BirkNext.Web.Services.Operations.BackgroundOperationService());
        Services.AddSingleton(new DeliveryReportingSession());
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    internal sealed class FakeApi : IDeliveryReportingApiService
    {
        public TaskCompletionSource? GenerateGate { get; set; }
        public int Generated { get; private set; }
        public int Exports { get; private set; }
        public List<DeliveryProject> Projects { get; } = [new(ProjectId, "M2LB", null, Monday, Friday, 2, 1)];
        public List<DeliverySnapshotSummary> Snapshots { get; } =
        [
            new() { Id = Guid.NewGuid(), ProjectId = ProjectId, Label = "Friday", EffectiveAt = Friday, ImportedAt = Friday, Role = DeliverySnapshotRole.Current, SourceType = DeliverySourceType.ZipUpload, SourceFileName = "m2lb.zip", FeatureCount = 2, TaskCount = 12, CompletedTaskCount = 8, Coverage = DeliveryCoverage.Complete },
            new() { Id = Guid.NewGuid(), ProjectId = ProjectId, Label = "Monday", EffectiveAt = Monday, ImportedAt = Friday, Role = DeliverySnapshotRole.Baseline, SourceType = DeliverySourceType.GitArchive, SourceFileName = "m2lb-mon.zip", FeatureCount = 2, TaskCount = 12, CompletedTaskCount = 4, Coverage = DeliveryCoverage.Complete, ReportCount = 1 },
        ];
        public WeeklyDeliveryReport Report { get; set; } = SampleReport();
        public List<Guid> Deleted { get; } = [];

        public Task<(List<DeliveryProject> Projects, string? Error)> ProjectsAsync(CancellationToken ct = default) => Task.FromResult((Projects, (string?)null));
        public Task<(DeliveryProject? Project, string? Error)> CreateProjectAsync(string name, string? description, CancellationToken ct = default) =>
            Task.FromResult<(DeliveryProject?, string?)>((new(Guid.NewGuid(), name, description, Friday, Friday, 0, 0), null));
        public Task<(DeliveryImportPreview? Preview, string? Error)> PreviewAsync(Guid? projectId, string fileName, Stream content, CancellationToken ct = default) =>
            Task.FromResult<(DeliveryImportPreview?, string?)>((null, "not used"));
        public Task<DeliveryImportCommitResult> CommitAsync(Guid stagingId, DeliveryImportCommitRequest request, CancellationToken ct = default) => Task.FromResult(new DeliveryImportCommitResult(null, null, "not used"));
        public Task DiscardAsync(Guid stagingId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<DeliverySnapshotSummary>> SnapshotsAsync(Guid projectId, CancellationToken ct = default) => Task.FromResult(Snapshots.ToList());
        public Task<DeliverySnapshotDetail?> SnapshotAsync(Guid projectId, Guid snapshotId, CancellationToken ct = default) => Task.FromResult<DeliverySnapshotDetail?>(null);
        public Task<(DeliverySnapshotSummary? Snapshot, string? Error)> UpdateSnapshotAsync(Guid projectId, Guid snapshotId, UpdateDeliverySnapshotRequest request, CancellationToken ct = default) =>
            Task.FromResult<(DeliverySnapshotSummary?, string?)>((null, null));
        public Task<string?> DeleteSnapshotAsync(Guid projectId, Guid snapshotId, CancellationToken ct = default)
        {
            Deleted.Add(snapshotId);
            Snapshots.RemoveAll(s => s.Id == snapshotId);
            return Task.FromResult<string?>(null);
        }
        public Task<DeliveryBaselineSuggestion?> SuggestBaselineAsync(Guid projectId, Guid currentId, CancellationToken ct = default) =>
            Task.FromResult<DeliveryBaselineSuggestion?>(new(Snapshots[^1].Id, "Earliest snapshot in the week of the current snapshot (2026-10-26).", false, [Snapshots[^1].Id]));
        public Task<List<DeliveryReportSummaryRow>> ReportsAsync(Guid projectId, CancellationToken ct = default) =>
            Task.FromResult(new List<DeliveryReportSummaryRow> { new(ReportId, Report.Title, Report.WeekLabel, Monday, Friday, Friday, Report.Coverage, "Monday", "Friday", 1, Report.Attention.Count) });
        public async Task<(WeeklyDeliveryReport? Report, string? Error)> GenerateAsync(GenerateWeeklyReportRequest request, CancellationToken ct = default)
        {
            Generated++;
            if (GenerateGate is { } gate) await gate.Task;
            return (Report, null);
        }
        public Task<WeeklyDeliveryReport?> ReportAsync(Guid projectId, Guid reportId, CancellationToken ct = default) => Task.FromResult<WeeklyDeliveryReport?>(Report);
        public Task<(DeliveryReportExport? Export, string? Error)> ExportAsync(Guid projectId, Guid reportId, DeliveryExportFormat format, bool appendix, bool unchanged, CancellationToken ct = default)
        {
            Exports++;
            return Task.FromResult<(DeliveryReportExport?, string?)>((new("M2LB-Weekly-Delivery-Report-2026-W44.md", "text/markdown", "# report"), null));
        }
        public Task<string?> DeleteReportAsync(Guid projectId, Guid reportId, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    internal static WeeklyDeliveryReport SampleReport() => new()
    {
        ReportId = ReportId, ProjectId = ProjectId, ProjectName = "M2LB", Title = "M2LB Weekly Delivery Status", WeekLabel = "2026-W44",
        PeriodStart = Monday, PeriodEnd = Friday, GeneratedAt = Friday, Coverage = DeliveryCoverage.Complete,
        Baseline = new() { Label = "Monday", EffectiveAt = Monday, SourceType = DeliverySourceType.GitArchive },
        Current = new() { Label = "Friday", EffectiveAt = Friday, SourceType = DeliverySourceType.ZipUpload },
        Metrics = [new("stories-changed", "Stories changed", 1), new("moved-forward", "Moved forward", 1), new("attention", "Needs attention", 0)],
        ExecutiveSummary = ["1 of 2 stories changed between 26 Oct 2026 and 30 Oct 2026."],
        Stories =
        [
            new() { FeatureKey = "frontend::001-child-search", Title = "Child search", Module = "Frontend", BaselineStatus = DeliveryStatus.InProgress, CurrentStatus = DeliveryStatus.InProgress,
                Movement = DeliveryMovement.Progressed, BaselineTaskCompleted = 3, BaselineTaskTotal = 8, CurrentTaskCompleted = 7, CurrentTaskTotal = 8, TaskDelta = 4, TasksCompletedThisPeriod = 4,
                Reasons = ["Progressed: task completion changed from 3/8 to 7/8"],
                Evidence = new() { SpecPath = "Frontend/specs/001-child-search/spec.md", TasksPath = "Frontend/specs/001-child-search/tasks.md", TasksCompleted = ["T004 Implement search"] } },
            new() { FeatureKey = "frontend::002-case-overview", Title = "Case overview", Module = "Frontend", BaselineStatus = DeliveryStatus.InProgress, CurrentStatus = DeliveryStatus.InProgress,
                Movement = DeliveryMovement.NoSignificantChange, BaselineTaskCompleted = 1, BaselineTaskTotal = 4, CurrentTaskCompleted = 1, CurrentTaskTotal = 4, Reasons = ["Unchanged at 1/4 tasks"] },
        ],
        NextWeekFocus = [new("2 features still in progress (closest to finishing first)", ["Child search (7/8)", "Case overview (1/4)"])],
    };

    private IRenderedComponent<DeliveryReportingPage> RenderAt(string query)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo($"delivery-reporting{query}");
        return Render<DeliveryReportingPage>();
    }

    [Fact]
    public void Overview_AnswersWhatChangedThisWeek_WithoutTechnicalDetails()
    {
        var page = RenderAt($"?project={ProjectId}");

        page.WaitForAssertion(() => page.Find("[data-testid=dlr-report]"));
        page.Find("h1").TextContent.Should().Be("M2LB — Weekly Delivery Report");
        page.Find("[data-testid=dlr-meta-period]").TextContent.Should().Contain("26–30 Oct 2026");
        page.Find("[data-testid=dlr-meta-baseline]").TextContent.Should().Contain("26 Oct 2026 · Git archive");
        page.Find("[data-testid=dlr-meta-current]").TextContent.Should().Contain("30 Oct 2026 · ZIP import");
        page.Find("[data-testid=dlr-meta-coverage]").TextContent.Should().Be("Complete");
        page.Find("[data-testid=dlr-metric-stories-changed]").TextContent.Should().Contain("1").And.Contain("Stories changed");
        page.Find("[data-testid=dlr-attention]").TextContent.Should().Contain("Nothing needs attention.");
        page.Find("[data-testid=dlr-focus]").TextContent.Should().Contain("2 features still in progress");
        var overview = page.Find("[data-testid=dlr-summary]").TextContent + page.Find("[data-testid=dlr-cards]").TextContent;
        overview.Should().NotContain("specs/").And.NotContain("fingerprint").And.NotContain("parser");
        page.Find("[data-testid=dlr-export-toggle]").TextContent.Should().Contain("Export");
        page.Find("[data-testid=dlr-export-appendix]").HasAttribute("checked").Should().BeFalse("the evidence appendix is off by default");
        page.Find("[data-testid=dlr-export-unchanged]").HasAttribute("checked").Should().BeFalse("unchanged stories are off by default");
    }

    [Fact]
    public void Stories_HideUnchangedByDefault_AndShowEvidenceOnRequest()
    {
        var page = RenderAt($"?project={ProjectId}&tab=stories");

        page.WaitForAssertion(() => page.FindAll("[data-testid=dlr-story-row]").Should().ContainSingle());
        var row = page.Find("[data-testid=dlr-story-row]");
        row.TextContent.Should().Contain("Child search").And.Contain("Progressed").And.Contain("7 / 8 tasks");
        page.Find("[data-testid=dlr-story-count]").TextContent.Should().Contain("1 of 2 features shown");
        page.FindAll("[data-testid=dlr-story-evidence]").Should().BeEmpty("evidence is secondary: inside the feature detail");

        row.QuerySelector("[data-testid=dlr-feature-open]")!.Click();
        page.Find("[data-testid=dlr-breadcrumb]").TextContent.Should().Contain("Child search");
        page.FindAll("[data-testid=dlr-feature-tab]").Select(t => t.GetAttribute("data-tab")).Should().Equal("overview", "stories", "criteria", "tasks", "changes", "evidence");
        page.FindAll("[data-testid=dlr-feature-tab]").Single(t => t.GetAttribute("data-tab") == "evidence").Click();
        page.Find("[data-testid=dlr-story-evidence]").TextContent.Should().Contain("Frontend/specs/001-child-search/tasks.md").And.Contain("T004 Implement search");
        page.Find("[data-testid=dlr-feature-back]").Click();

        page.Find("[data-testid=dlr-story-filter]").Change("all");
        page.FindAll("[data-testid=dlr-story-row]").Should().HaveCount(2);
    }

    [Fact]
    public void Snapshots_ListOnlyReportingSnapshots_AndDeleteNeedsConfirmation()
    {
        var page = RenderAt($"?project={ProjectId}&tab=snapshots");

        page.WaitForAssertion(() => page.FindAll("[data-testid=dlr-snapshot-row]").Should().HaveCount(2));
        page.Find("[data-testid=dlr-snapshot-table] thead").TextContent.Should().ContainAll("Snapshot", "Effective date", "Imported", "Source", "Features", "Tasks", "Role", "Coverage", "Actions");
        page.FindAll("[data-testid=dlr-snapshot-delete]")[1].Click();
        var dialog = page.Find("[data-testid=dlr-confirm]");
        dialog.GetAttribute("role").Should().Be("alertdialog");
        dialog.TextContent.Should().Contain("Only Delivery Reporting data is removed").And.Contain("keep their saved content");
        _api.Deleted.Should().BeEmpty();
        page.Find("[data-testid=dlr-confirm-yes]").Click();
        page.WaitForAssertion(() => _api.Deleted.Should().ContainSingle());
    }

    [Fact]
    public void NoReportingProject_ShowsCreateProject_NotAnImportProjectRedirect()
    {
        _api.Projects.Clear();
        var page = RenderAt("");

        page.WaitForAssertion(() => page.Find("[data-testid=dlr-no-project]"));
        page.Markup.Should().NotContain("project-import");
        page.Find("[data-testid=dlr-import-open]").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void ImportDialog_IsAModalDialog_WithLabelledFileAndDateFields()
    {
        var page = RenderAt($"?project={ProjectId}&tab=snapshots");
        page.WaitForAssertion(() => page.Find("[data-testid=dlr-import-open]"));

        page.Find("[data-testid=dlr-import-open]").Click();

        var dialog = page.Find("[data-testid=dlr-import-dialog]");
        dialog.GetAttribute("role").Should().Be("dialog");
        dialog.GetAttribute("aria-modal").Should().Be("true");
        page.Find("[data-testid=dlr-import-file]").Closest("label")!.TextContent.Should().Contain("Source file (ZIP)");
        page.Find("[data-testid=dlr-import-effective]").Closest("label")!.TextContent.Should().Contain("Effective snapshot date and time");
        dialog.TextContent.Should().Contain("Need an earlier baseline?");
        page.Find("[data-testid=dlr-import-confirm]").HasAttribute("disabled").Should().BeTrue("nothing is imported before a preview");
    }

    [Fact]
    public void ImportDialog_AzureDevOpsSource_PreviewsTheResolvedCommit_WithItsStateTimeAsEffectiveDate()
    {
        _ado.Connections.Add(new(Guid.NewGuid(), "Bufdir", "bufdir", true, "Connected", Monday));
        _api.Snapshots.Add(new() { Id = Guid.NewGuid(), ProjectId = ProjectId, Label = "ADO", EffectiveAt = Monday, ImportedAt = Friday, Role = DeliverySnapshotRole.AdHoc,
            SourceType = DeliverySourceType.AzureDevOps, SourceFileName = "M2LB@abc1234.zip", AzureDevOps = new("bufdir", "M2LB", "M2LB", "main", "abc1234def", Monday) });
        var page = RenderAt($"?project={ProjectId}&tab=snapshots");
        page.WaitForAssertion(() => page.FindAll("[data-testid=dlr-snapshot-source]").Should().HaveCount(3));
        page.FindAll("[data-testid=dlr-snapshot-source]").Select(s => s.TextContent).Should().Contain(t => t.Contains("Azure DevOps") && t.Contains("M2LB / M2LB / main · abc1234"));

        page.Find("[data-testid=dlr-import-open]").Click();
        page.Find("[data-testid=dlr-source-zip]").HasAttribute("checked").Should().BeTrue("Upload ZIP stays the default");
        page.FindAll("[data-testid=dlr-ado]").Should().BeEmpty();
        page.Find("[data-testid=dlr-source-ado]").Change(true);
        page.FindAll("[data-testid=dlr-import-file]").Should().BeEmpty();
        page.WaitForAssertion(() => page.FindAll("[data-testid=dlr-ado-project] option").Should().HaveCount(3));
        page.Find("[data-testid=dlr-ado-project]").Change("M2LB");
        page.Find("[data-testid=dlr-ado-repository]").Change("M2LB");
        page.Find("[data-testid=dlr-ado-at]").Change(true);
        page.Find("[data-testid=dlr-ado-time]").Change(DeliveryReportingPresentation.LocalInput(Monday));
        page.Find("[data-testid=dlr-ado-resolve]").Click();
        page.Find("[data-testid=dlr-ado-preview]").Click();

        page.Find("[data-testid=dlr-preview-ado]").TextContent.Should().Be("Azure DevOps · M2LB / M2LB / main");
        page.Find("[data-testid=dlr-import-effective]").GetAttribute("value").Should().Be(DeliveryReportingPresentation.LocalInput(Monday), "the requested state time, not the import time");
        page.Find("[data-testid=dlr-import-confirm]").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void Presentation_DefaultFilter_IsChangedPlusAttention()
    {
        var report = SampleReport();
        DeliveryReportingPresentation.Filter(report, "default", null, null).Select(s => s.Title).Should().Equal("Child search");
        DeliveryReportingPresentation.Filter(report, "all", "case", null).Select(s => s.Title).Should().Equal("Case overview");
        DeliveryReportingPresentation.Filter(report, "progressed", null, null).Should().ContainSingle();
        DeliveryReportingPresentation.Filter(report, "new", null, null).Should().BeEmpty();
        DeliveryReportingPresentation.TabOrDefault("nonsense").Should().Be("overview");
    }
}
