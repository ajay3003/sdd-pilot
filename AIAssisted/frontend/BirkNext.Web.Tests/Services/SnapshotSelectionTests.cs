using BirkNext.DeliveryReporting;
using BirkNext.Snapshots;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>Snapshot selection identity: the SnapshotId is the value; labels are unique presentation built from metadata (effective date, import time,
/// source, short id only as last resort); role/pointer names are badges; generated "{role} {date}" names are never shown as names; a legacy label
/// selection resolves only when exactly one snapshot matches.</summary>
public sealed class SnapshotSelectionTests
{
    static readonly DateTimeOffset Day = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
    static SnapshotSelectionOption O(string id, int importedMinute, string? source = "M2LB", string? summary = null, params string[] pointers) =>
        new(id, "Delivery", Day, Day.AddHours(13).AddMinutes(importedMinute), source, summary, pointers);

    [Fact]
    public void Same_effective_date_options_are_disambiguated_by_import_time_then_source_then_short_id()
    {
        var labels = SnapshotSelectionLabels.Labels([O("a1", 6), O("b2", 11), O("c3", 11, "M2LB.Common"), O("d4-0000-9A1F", 11, "M2LB.Common")], TimeZoneInfo.Utc);
        labels.Values.Should().OnlyHaveUniqueItems();
        labels["a1"].Should().Be("4 Nov 2026 · imported 4 Nov 13:06");
        labels["b2"].Should().Be("4 Nov 2026 · imported 4 Nov 13:11 · M2LB");
        labels["c3"].Should().EndWith("· M2LB.Common · …C3");
        labels["d4-0000-9A1F"].Should().EndWith("· M2LB.Common · …9A1F", "the short id is only the last resort");
    }

    [Fact]
    public void A_unique_effective_date_needs_no_import_time_and_pointers_and_summary_follow_as_text()
    {
        var other = new SnapshotSelectionOption("x", "Delivery", Day.AddDays(-3), Day.AddDays(-3), "M2LB", null, []);
        var labels = SnapshotSelectionLabels.Labels([O("a", 6, summary: "38 features · 2038/2142 tasks", pointers: "Current"), other], TimeZoneInfo.Utc);
        labels["a"].Should().Be("4 Nov 2026 · 38 features · 2038/2142 tasks · Current");
        labels["x"].Should().Be("1 Nov 2026");
    }

    [Fact]
    public void Sorting_is_newest_effective_then_newest_import_then_id_regardless_of_input_order()
    {
        SnapshotSelectionOption[] input = [O("b", 6), O("a", 11), O("c", 11), new("old", "Delivery", Day.AddDays(-1), Day.AddDays(1), null, null, [])];
        SnapshotSelectionLabels.Sort(input).Select(o => o.SnapshotId).Should().Equal("a", "c", "b", "old");
        SnapshotSelectionLabels.Sort(input.Reverse()).Select(o => o.SnapshotId).Should().Equal("a", "c", "b", "old");
    }

    [Theory]
    [InlineData("Current 2026-11-04", true)]
    [InlineData("Baseline 2026-11-03", true)]
    [InlineData("ad hoc 2026-11-03", true)]
    [InlineData("M2LB-language (parser v4)", false)]
    [InlineData("Friday", false)]
    public void Generated_role_date_names_are_recognised_and_given_names_are_kept(string name, bool generated) =>
        SnapshotSelectionLabels.IsGeneratedRoleDateName(name).Should().Be(generated);

    [Fact]
    public void A_legacy_label_selection_resolves_only_when_exactly_one_snapshot_matches()
    {
        (string, string)[] snapshots = [("a", "Current 2026-11-04"), ("b", "Current 2026-11-04"), ("c", "Baseline 2026-11-04")];
        SnapshotSelectionLabels.ResolveLegacyLabel("Baseline 2026-11-04", snapshots).Should().Be(("c", (string?)null));
        var ambiguous = SnapshotSelectionLabels.ResolveLegacyLabel("Current 2026-11-04", snapshots);
        ambiguous.SnapshotId.Should().BeNull("two snapshots match: never the first");
        ambiguous.Problem.Should().Be("2 snapshots match the saved legacy label \"Current 2026-11-04\". Select the intended snapshot.");
        SnapshotSelectionLabels.ResolveLegacyLabel("Gone", snapshots).SnapshotId.Should().BeNull();
    }

    static DeliverySnapshotSummary D(Guid id, string label, DeliverySnapshotRole role, DateTimeOffset effective, int importedMinute) => new()
    {
        Id = id, Label = label, Role = role, EffectiveAt = effective, ImportedAt = effective.AddHours(13).AddMinutes(importedMinute), SourceFileName = "M2LB.zip",
        FeatureCount = 38, TaskCount = 2142, CompletedTaskCount = 2038,
    };

    [Fact]
    public void Delivery_current_is_the_newest_snapshot_imported_as_current_and_older_ones_say_what_they_are()
    {
        var older = D(Guid.NewGuid(), "Current 2026-11-04", DeliverySnapshotRole.Current, Day, 6);
        var newer = D(Guid.NewGuid(), "Current 2026-11-04", DeliverySnapshotRole.Current, Day, 11);
        var baseline = D(Guid.NewGuid(), "Baseline 2026-11-04", DeliverySnapshotRole.Baseline, Day, -30);
        List<DeliverySnapshotSummary> list = [older, baseline, newer];
        DeliveryReportingPresentation.CurrentPointer(list).Should().Be(newer.Id);
        DeliveryReportingPresentation.Pointers(older, newer.Id).Should().Equal("earlier current import");
        DeliveryReportingPresentation.Pointers(baseline, newer.Id).Should().Equal("imported as baseline");
        var options = DeliveryReportingPresentation.SnapshotOptions(list);
        options.Values.Should().OnlyHaveUniqueItems();
        options[newer.Id].Should().EndWith("2038/2142 tasks · Current");
        DeliveryReportingPresentation.GivenName(older).Should().BeNull("\"Current 2026-11-04\" is a generated role-date name, not a name");
        DeliveryReportingPresentation.GivenName(older with { Label = "M2LB-language (parser v4)" }).Should().Be("M2LB-language (parser v4)");
    }
}
