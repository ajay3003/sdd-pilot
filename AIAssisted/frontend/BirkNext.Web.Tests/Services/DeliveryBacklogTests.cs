using BirkNext.DeliveryReporting;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>Virtual SDD backlog: statuses stay distinct and come only from explicit evidence; removed features are not delivered; context is the report's.</summary>
public sealed class DeliveryBacklogTests
{
    private static readonly DateTimeOffset Friday = new(2026, 10, 30, 15, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(DeliveryStatus.NotStarted, BacklogStatus.Planned)]
    [InlineData(DeliveryStatus.InProgress, BacklogStatus.InProgress)]
    [InlineData(DeliveryStatus.TasksComplete, BacklogStatus.Implemented)]
    [InlineData(DeliveryStatus.ReadyForTest, BacklogStatus.Implemented)]
    [InlineData(DeliveryStatus.Testing, BacklogStatus.Implemented)]
    [InlineData(DeliveryStatus.Done, BacklogStatus.Accepted)]
    [InlineData(DeliveryStatus.Blocked, BacklogStatus.Blocked)]
    [InlineData(DeliveryStatus.NeedsClarification, BacklogStatus.NeedsClarification)]
    [InlineData(DeliveryStatus.NotAssessed, BacklogStatus.NotAssessed)]
    public void Status_ComesFromExplicitEvidence_NeverInventsVerified(DeliveryStatus status, BacklogStatus expected)
    {
        DeliveryBacklog.Status(status).Should().Be(expected);
        DeliveryBacklog.Status(status).Should().NotBe(BacklogStatus.Verified, "snapshots carry no verification evidence");
    }

    [Fact]
    public void TasksComplete_IsImplemented_NotAccepted_AndSaysSo()
    {
        DeliveryBacklog.Detail(DeliveryStatus.TasksComplete).Should().Contain("not stated as tested or done");
        DeliveryBacklog.Columns.Should().Equal(BacklogStatus.Planned, BacklogStatus.InProgress, BacklogStatus.Implemented, BacklogStatus.Verified, BacklogStatus.Accepted, BacklogStatus.Blocked);
    }

    [Fact]
    public void FromReport_UsesTheCurrentSnapshot_ListsRemovedApart_AndKeepsAzureDevOpsProvenance()
    {
        var ado = new DeliveryAzureDevOpsSource("bufdir", "M2LB", "M2LB", "main", "abc1234def", Friday);
        var report = new WeeklyDeliveryReport
        {
            ReportId = Guid.NewGuid(), ProjectId = Guid.NewGuid(), WeekLabel = "2026-W44",
            Baseline = new() { Label = "Monday" }, Current = new() { Label = "Friday", EffectiveAt = Friday, SourceType = DeliverySourceType.AzureDevOps, AzureDevOps = ado },
            Stories =
            [
                new() { FeatureKey = "a", Title = "Child search", Module = "Frontend", CurrentStatus = DeliveryStatus.TasksComplete, CurrentTaskCompleted = 8, CurrentTaskTotal = 8,
                    Movement = DeliveryMovement.Completed,
                    Hierarchy = new() { Captured = true, CriteriaCurrent = 5, Stories = [new("US1", "Search", DeliveryItemChange.Unchanged), new("US2", "Filter", DeliveryItemChange.Removed)],
                        Phases = [new("phase-1", "Phase 1", DeliveryItemChange.Unchanged)] } },
                new() { FeatureKey = "b", Title = "Case overview", Module = "Frontend", CurrentStatus = DeliveryStatus.Done, CurrentTaskCompleted = 4, CurrentTaskTotal = 4 },
                new() { FeatureKey = "c", Title = "Old export", Module = "Backend", RemovedFeature = true, BaselineStatus = DeliveryStatus.InProgress },
            ],
        };

        var view = DeliveryBacklog.FromReport(report);

        view.Items.Select(i => (i.Title, i.Status)).Should().Equal(("Case overview", BacklogStatus.Accepted), ("Child search", BacklogStatus.Implemented));
        view.Items.Single(i => i.Title == "Child search").Should().Match<BacklogItem>(i => i.Stories == 1 && i.Criteria == 5 && i.Phases == 1 && i.TasksCompleted == 8);
        view.RemovedFeatures.Should().Equal("Old export");
        view.Count(BacklogStatus.Verified).Should().Be(0);
        view.Source.BaselineLabel.Should().Be("Monday");
        DeliveryBacklog.Provenance(view.Source).Should().Be("Azure DevOps · M2LB / M2LB / main · abc1234");
    }

    [Fact]
    public void FromSnapshot_ParsesStatuses_UnknownIsNotAssessed()
    {
        var detail = new DeliverySnapshotDetail(new DeliverySnapshotSummary { Label = "Monday", EffectiveAt = Friday, SourceType = DeliverySourceType.ZipUpload }, ["Frontend"],
            [new("a", "Search", "Frontend", nameof(DeliveryStatus.InProgress), "", 8, 3, null, null), new("b", "Odd", "Frontend", "Something", "", 0, 0, null, null)], []);
        var view = DeliveryBacklog.FromSnapshot(detail);
        view.Items.Select(i => i.Status).Should().Equal(BacklogStatus.NotAssessed, BacklogStatus.InProgress);
        DeliveryBacklog.Provenance(view.Source).Should().Be("ZIP import");
    }
}
