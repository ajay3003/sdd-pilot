using BirkNext.DeliveryReporting;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>Snapshots list: search over name, file and repository revision; role filter; effective date apart from import date; latest = newest effective.</summary>
public sealed class DeliverySnapshotListTests
{
    private static readonly DateTimeOffset Mon = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);

    private static DeliverySnapshotSummary S(string label, int effectiveDay, int importedDay, DeliverySnapshotRole role, string file = "x.zip", string? commit = null) => new()
    {
        Id = Guid.NewGuid(), Label = label, EffectiveAt = Mon.AddDays(effectiveDay), ImportedAt = Mon.AddDays(importedDay), Role = role, SourceFileName = file, GitCommit = commit,
    };

    [Fact]
    public void Effective_and_imported_order_differ_and_latest_is_the_newest_effective_date()
    {
        var old = S("Monday", 0, 9, DeliverySnapshotRole.Baseline);
        var recent = S("Friday", 4, 5, DeliverySnapshotRole.Current, "M2LB.zip", "abc1234def");
        DeliverySnapshotList.Latest([old, recent]).Should().Be(recent);
        DeliverySnapshotList.Apply([old, recent], "", null, DeliverySnapshotList.ByEffective).Should().Equal(recent, old);
        DeliverySnapshotList.Apply([old, recent], "", null, DeliverySnapshotList.ByImported).Should().Equal([old, recent], "Monday archive was imported last");
        DeliverySnapshotList.Apply([old, recent], "", null, DeliverySnapshotList.ByName).Should().Equal(recent, old);
        DeliverySnapshotList.Apply([old, recent], "abc1234", null, DeliverySnapshotList.ByEffective).Should().Equal(recent);
        DeliverySnapshotList.Apply([old, recent], "m2lb", null, DeliverySnapshotList.ByEffective).Should().Equal(recent);
        DeliverySnapshotList.Apply([old, recent], "", DeliverySnapshotRole.Baseline, DeliverySnapshotList.ByEffective).Should().Equal(old);
    }
}
