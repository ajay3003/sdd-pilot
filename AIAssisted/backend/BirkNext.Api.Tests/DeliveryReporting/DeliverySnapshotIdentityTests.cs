using BirkNext.Api.Data.DeliveryReporting;
using BirkNext.Api.Services.DeliveryReporting;
using BirkNext.DeliveryReporting;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static BirkNext.Api.Tests.DeliveryReporting.DeliveryReportingFixtures;

namespace BirkNext.Api.Tests.DeliveryReporting;

/// <summary>
/// Delivery snapshot identity: snapshots are found by id only; a new import is no longer named "{role} {date}" (a second same-day import would otherwise
/// look identical); same-effective-date comparisons say what is known (import order or Azure DevOps commit order) without claiming source chronology.
/// </summary>
public sealed class DeliverySnapshotIdentityTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    static DeliveryReportingDbContext Db() => new(new DbContextOptionsBuilder<DeliveryReportingDbContext>().UseInMemoryDatabase($"dsi-{Guid.NewGuid():N}").Options);

    [Fact]
    public async Task Two_same_day_imports_without_a_name_get_a_neutral_name_and_distinct_ids()
    {
        await using var db = Db();
        var clock = new Clock(Friday);
        var store = new DeliverySnapshotStore(db, clock);
        var imports = new DeliveryReportingImportService(db, new DeliveryImportStagingStore(TimeProvider.System), clock);
        var project = (await store.CreateProjectAsync(new("M2LB", null), default)).Project!.Id;

        async Task<DeliverySnapshotSummary> Import(byte[] zip)
        {
            var preview = (await imports.PreviewAsync(project, "M2LB.zip", zip, default)).Preview!;
            var outcome = await imports.CommitAsync(preview.StagingId, new DeliveryImportCommitRequest
                { ProjectId = project, EffectiveAt = Friday, Role = DeliverySnapshotRole.Current, ConfirmDuplicate = true }, default);
            return outcome.Result.Snapshot!;
        }
        var first = await Import(Zip(ChildSearch(8, 3)));
        clock.Now = Friday.AddMinutes(5);
        var second = await Import(Zip(ChildSearch(8, 7)));

        first.Id.Should().NotBe(second.Id);
        first.Label.Should().Be("M2LB", "the role and date are metadata, never baked into the name");
        second.Label.Should().Be("M2LB");
        first.Role.Should().Be(DeliverySnapshotRole.Current);
        // Same effective date: imported later first.
        (await store.SnapshotsAsync(project, default)).Select(s => s.Id).Should().Equal([second.Id, first.Id]);
    }

    static DeliveryReportSnapshotRef Ref(DateTimeOffset imported, DeliveryAzureDevOpsSource? ado = null) =>
        new() { SnapshotId = Guid.NewGuid(), EffectiveAt = Friday, ImportedAt = imported, AzureDevOps = ado };

    [Fact]
    public void Same_effective_date_wording_separates_import_order_from_source_order()
    {
        var text = WeeklyReportBuilder.SameEffectiveDateWarning(Ref(Friday), Ref(Friday.AddMinutes(5)));
        text.Should().StartWith("Same effective date. Source revision order cannot be confirmed from the effective dates.")
            .And.Contain("imported later in BirkNext; import order is not proof of a newer source revision");
        WeeklyReportBuilder.SameEffectiveDateWarning(Ref(Friday.AddMinutes(5)), Ref(Friday)).Should().Contain("imported earlier");
    }

    [Fact]
    public void A_same_day_pair_with_fewer_tasks_keeps_both_the_reduction_warning_and_the_same_day_caveat()
    {
        var baseline = Content(Parse(ChildSearch(8, 3)), Friday);
        var current = Content(Parse(ChildSearch(6, 3)), Friday);
        current = current with { Ref = current.Ref with { ImportedAt = Friday.AddMinutes(5) } };
        var preview = WeeklyReportBuilder.Preview(baseline, current, TimeZoneInfo.Utc);
        preview.Direction.Should().Be(DeliveryDirectionState.InventoryReduced);
        preview.Warnings.Should().Contain(w => w.Contains("fewer")).And.Contain(w => w.StartsWith("Same effective date.") && w.Contains("imported later"));
    }

    [Fact]
    public void The_preview_pairs_a_feature_whose_module_path_changed_exactly_like_the_report()
    {
        // Real-data shape (M2LB → M2LB-language): module Tjeneste renamed TjenesteService, its features kept their folders; one new feature.
        static DeliveryContentFeature F(string module, string folder, int position) =>
            new() { Key = $"{module.ToLowerInvariant()}::{folder}", Folder = folder, Module = module, Title = folder, Position = position };
        static DeliverySnapshotContent C(DateTimeOffset at, params DeliveryContentFeature[] features) =>
            new() { Ref = new() { SnapshotId = Guid.NewGuid(), EffectiveAt = at, ImportedAt = at }, Coverage = DeliveryCoverage.Complete, Features = [.. features] };
        var baseline = C(Monday, F("Tjeneste", "001-tjenestemodul", 0), F("Frontend", "002-search", 1));
        var current = C(Friday, F("TjenesteService", "001-tjenestemodul", 0), F("Frontend", "002-search", 1), F("TjenesteService", "004-placements", 2));

        var preview = WeeklyReportBuilder.Preview(baseline, current, TimeZoneInfo.Utc);
        var report = DeliverySnapshotComparison.Compare(baseline, current);

        preview.MissingFeatures.Should().BeEmpty("the report pairs the moved feature, so the preview must not call it missing");
        preview.NewFeatures.Should().Equal(["004-placements"]);
        report.FeaturesRemoved.Should().Be(preview.MissingFeatures.Count);
        report.FeaturesAdded.Should().Be(preview.NewFeatures.Count);
    }

    [Fact]
    public void Azure_devops_commit_times_clarify_same_day_order_and_a_reversed_pair_is_said()
    {
        DeliveryAzureDevOpsSource Commit(string id, int minute) => new("org", "proj", "M2LB", "main", id, Friday.AddMinutes(minute));
        WeeklyReportBuilder.SameEffectiveDateWarning(Ref(Friday, Commit("aaaaaaa1", 1)), Ref(Friday, Commit("bbbbbbb2", 9)))
            .Should().Contain("commit (bbbbbbb) is later than the baseline's (aaaaaaa)");
        WeeklyReportBuilder.SameEffectiveDateWarning(Ref(Friday, Commit("aaaaaaa1", 9)), Ref(Friday, Commit("bbbbbbb2", 1)))
            .Should().Contain("is earlier than the baseline's").And.Contain("check the direction");
    }

    [Fact]
    public async Task The_baseline_suggestion_never_picks_between_two_same_day_candidates_by_name_or_order()
    {
        await using var db = Db();
        var store = new DeliverySnapshotStore(db, new Clock(Friday));
        var project = (await store.CreateProjectAsync(new("M2LB", null), default)).Project!.Id;
        var source = new DeliveryReportSourceRecord { Id = Guid.NewGuid(), ProjectId = project, SourceType = nameof(DeliverySourceType.ZipUpload), OriginalFileName = "M2LB.zip", Fingerprint = "f", ImportedAt = Friday };
        db.ReportSources.Add(source);
        DeliverySnapshotRecord S(string label, string role, DateTimeOffset effective, int minute) => new()
        {
            Id = Guid.NewGuid(), ProjectId = project, ReportSourceId = source.Id, Label = label, EffectiveAt = effective.ToUniversalTime(), ImportedAt = Friday.AddMinutes(minute),
            Role = role, CoverageStatus = nameof(DeliveryCoverage.Complete), ParseStatus = "Parsed",
        };
        var a = S("Current 2026-10-27", "Current", Monday.AddDays(1), 1);
        var b = S("Current 2026-10-27", "Current", Monday.AddDays(1), 2);
        var current = S("Current 2026-10-30", "Current", Friday, 3);
        db.Snapshots.AddRange(a, b, current);
        await db.SaveChangesAsync();

        var suggestion = (await store.SuggestBaselineAsync(project, current.Id, TimeZoneInfo.Utc, default))!;
        suggestion.SnapshotId.Should().BeNull("two snapshots share the day and neither is marked baseline: the person chooses");
        suggestion.Candidates.Should().BeEquivalentTo([a.Id, b.Id]);
    }
}
