using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Xunit;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// A Task Alignment result held in the session (or a persisted run a page read) is never shown after a local data reset: once the browser
/// knows a newer reset epoch (its own reset, another tab's reset detected on a write, or a current-state read), the held result is dropped.
/// Without a known epoch change nothing is dropped.
/// </summary>
public sealed class TaskAlignmentResetEpochTests
{
    [Fact]
    public void HeldResult_IsDropped_WhenTheResetEpochAdvances()
    {
        var epoch = new LocalDataResetEpoch();
        epoch.Observe(3);
        var session = new TaskAlignmentSessionService(epoch);
        session.SaveResult(new AlignmentReport(), "Shop", "spec", "tasks");
        session.Report.Should().NotBeNull();

        epoch.Observe(4);

        session.Report.Should().BeNull("the result was produced before the local data reset");
        session.Snapshot.Should().BeNull();
        session.GetCurrentness("Shop", "spec", "tasks").Should().Be(TaskAlignmentCurrentness.NotRun);
    }

    [Fact]
    public void HeldResult_IsKept_WhileTheEpochIsUnchanged_OrFirstBecomesKnown()
    {
        var epoch = new LocalDataResetEpoch();
        var session = new TaskAlignmentSessionService(epoch);
        session.SaveResult(new AlignmentReport(), "Shop", "spec", "tasks");

        epoch.Observe(2); // first current-state read after the result: the epoch becomes known, not changed
        session.Report.Should().NotBeNull();
        epoch.Observe(2);
        session.HasCurrentResult("Shop", "spec", "tasks").Should().BeTrue();

        epoch.Observe(3);
        session.Report.Should().BeNull();
    }

    [Fact]
    public void ResultTakenAfterTheReset_IsKept()
    {
        var epoch = new LocalDataResetEpoch();
        epoch.Observe(1);
        var session = new TaskAlignmentSessionService(epoch);
        session.SaveResult(new AlignmentReport(), "Shop", "spec", "tasks");
        epoch.Observe(2);
        session.Report.Should().BeNull();

        session.SaveResult(new AlignmentReport(), "Shop", "spec", "tasks");
        session.Report.Should().NotBeNull("a new analysis after the reset belongs to the new epoch");
    }

    [Fact]
    public void WithoutTheEpochService_TheSessionBehavesAsBefore()
    {
        var session = new TaskAlignmentSessionService();
        session.SaveResult(new AlignmentReport(), "Shop", "spec", "tasks");
        session.Report.Should().NotBeNull();
        session.ResetEpoch.Should().BeNull();
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData(null, 2, false)]
    [InlineData(2, null, false)]
    [InlineData(2, 2, false)]
    [InlineData(2, 3, true)]
    public void IsFromEarlierReset_RequiresBothEpochsKnownAndDifferent(int? loadedUnder, int? current, bool expected) =>
        TaskAlignmentSessionService.IsFromEarlierReset(loadedUnder, current).Should().Be(expected);
}
