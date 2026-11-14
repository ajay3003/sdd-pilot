using System.Text.Json;
using BirkNext.Api.Data.DeliveryReporting;
using BirkNext.Api.Services.DeliveryReporting;
using BirkNext.DeliveryReporting;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;
using static BirkNext.Api.Tests.DeliveryReporting.DeliveryReportingFixtures;

namespace BirkNext.Api.Tests.DeliveryReporting;

/// <summary>
/// Shared scope provenance of frozen delivery reports: recorded additively inside the frozen report document (no schema change), returned exactly
/// (scope ids, revision numbers and revision ids), exported in Markdown and HTML, and absent — said in words — for reports generated before scope
/// support. Two provenances compare like-for-like only when they name the same scopes at the same revisions.
/// </summary>
public sealed class DeliveryScopeProvenanceTests
{
    private static readonly TimeZoneInfo Oslo = TimeZoneInfo.FindSystemTimeZoneById("Europe/Oslo");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Guid ScopeA = Guid.NewGuid(), RevisionA3 = Guid.NewGuid(), RevisionA2 = Guid.NewGuid();

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static DeliveryScopeProvenance Provenance(int revision, Guid? revisionId) =>
        new() { Scopes = [new(ScopeA, "Release 1", revision, revisionId)], MembershipResolution = DeliveryScopeProvenance.DefaultMembershipResolution };

    private static WeeklyDeliveryReport Report()
    {
        var b = Content(Parse(ChildSearch(8, 3)), Monday, "Monday baseline");
        var c = Content(Parse(ChildSearch(8, 7)), Friday, "Friday current");
        return WeeklyReportBuilder.Build(Guid.NewGuid(), Guid.NewGuid(), "M2LB", Friday.AddHours(1), b, c, DeliverySnapshotComparison.Compare(b, c), Oslo);
    }

    private static DeliveryReportingDbContext Db(string name) => new(new DbContextOptionsBuilder<DeliveryReportingDbContext>().UseInMemoryDatabase(name).Options);

    private static DeliveryWeeklyReportRecord Record(WeeklyDeliveryReport report, string contentJson) => new()
    {
        Id = report.ReportId, ProjectId = report.ProjectId, ComparisonId = Guid.NewGuid(), Title = report.Title, WeekLabel = report.WeekLabel, PeriodStart = report.PeriodStart,
        PeriodEnd = report.PeriodEnd, GeneratedAt = report.GeneratedAt, CoverageStatus = report.Coverage.ToString(), ContentVersion = report.Version, ContentJson = contentJson,
    };

    private static WeeklyReportService Service(DeliveryReportingDbContext db) =>
        new(db, new DeliverySnapshotStore(db, new Clock(Friday)), new Clock(Friday), new ConfigurationBuilder().Build());

    [Fact]
    public async Task A_report_with_provenance_is_persisted_in_its_document_and_returned_exactly()
    {
        var name = $"prov-{Guid.NewGuid():N}";
        var created = Friday.AddHours(2);
        var report = Report() with { ScopeProvenance = DeliveryScopeProvenanceText.Normalize(Provenance(3, RevisionA3), created) };
        await using (var db = Db(name))
        {
            db.WeeklyReports.Add(Record(report, JsonSerializer.Serialize(report, Json)));
            await db.SaveChangesAsync();
        }

        await using var read = Db(name);
        var loaded = await Service(read).GetAsync(report.ProjectId, report.ReportId, default);

        loaded!.ScopeProvenance.Should().NotBeNull();
        loaded.ScopeProvenance!.Scopes.Should().ContainSingle().Which.Should().Be(new DeliveryScopeProvenanceEntry(ScopeA, "Release 1", 3, RevisionA3));
        loaded.ScopeProvenance.MembershipResolution.Should().Be("approved revisions; canonical key match type+module+feature+id");
        loaded.ScopeProvenance.CreatedAt.Should().Be(created);
    }

    [Fact]
    public async Task A_report_generated_before_scope_support_loads_and_says_provenance_was_not_recorded()
    {
        var name = $"prov-legacy-{Guid.NewGuid():N}";
        var report = Report();
        var legacyJson = JsonSerializer.Serialize(report, Json);
        legacyJson.Should().NotContain("scopeProvenance", "a report without provenance writes no field (old documents look the same)");
        await using (var db = Db(name))
        {
            db.WeeklyReports.Add(Record(report, legacyJson));
            await db.SaveChangesAsync();
        }

        await using var read = Db(name);
        var service = Service(read);
        var loaded = await service.GetAsync(report.ProjectId, report.ReportId, default);
        loaded!.ScopeProvenance.Should().BeNull();
        DeliveryScopeProvenanceText.Describe(loaded.ScopeProvenance).Should().Be("Scope provenance not recorded (created before scope support)");
        (await service.ListAsync(report.ProjectId, default)).Should().ContainSingle();

        var export = await service.ExportAsync(report.ProjectId, report.ReportId, DeliveryExportFormat.Markdown, new(IncludeEvidenceAppendix: true), default);
        export!.Content.Should().Contain("**Scope provenance:** Scope provenance not recorded (created before scope support)");
    }

    [Fact]
    public void Exports_carry_the_provenance_in_the_header_and_exact_ids_in_the_evidence_appendix()
    {
        var report = Report() with { ScopeProvenance = DeliveryScopeProvenanceText.Normalize(Provenance(3, RevisionA3), Friday) };

        var md = DeliveryReportExporter.Export(report, DeliveryExportFormat.Markdown, new()).Content;
        md.Should().Contain("**Scope provenance:** Release 1 rev 3 · approved revisions; canonical key match type+module+feature+id");
        md.Should().NotContain(RevisionA3.ToString(), "ids only in the evidence appendix (management-first default)");

        var mdAppendix = DeliveryReportExporter.Export(report, DeliveryExportFormat.Markdown, new(IncludeEvidenceAppendix: true)).Content;
        mdAppendix.Should().Contain($"Shared scope: Release 1: scope {ScopeA}, revision 3 (revision id {RevisionA3})");

        var html = DeliveryReportExporter.Export(report, DeliveryExportFormat.Html, new(IncludeEvidenceAppendix: true)).Content;
        html.Should().Contain("<dt>Scope provenance</dt><dd>Release 1 rev 3 ").And.Contain("approved revisions; canonical key match type+module+feature+id</dd>");
        html.Should().Contain($"revision id {RevisionA3}");
    }

    [Fact]
    public void Normalize_stamps_server_time_drops_unusable_entries_and_never_invents_a_revision_id()
    {
        var at = Friday.AddDays(1);
        var input = new DeliveryScopeProvenance
        {
            Scopes = [new(ScopeA, " Release 1 ", 3, null), new(ScopeA, "dup", 4, Guid.NewGuid()), new(Guid.Empty, "x", 1, null), new(Guid.NewGuid(), "no revision", 0, null)],
            MembershipResolution = " ", CreatedAt = DateTimeOffset.MinValue,
        };
        var stored = DeliveryScopeProvenanceText.Normalize(input, at)!;
        stored.Scopes.Should().ContainSingle().Which.Should().Be(new DeliveryScopeProvenanceEntry(ScopeA, "Release 1", 3, null));
        stored.CreatedAt.Should().Be(at);
        stored.MembershipResolution.Should().Be(DeliveryScopeProvenance.DefaultMembershipResolution);
        DeliveryScopeProvenanceText.Normalize(new DeliveryScopeProvenance(), at).Should().BeNull();
        DeliveryScopeProvenanceText.Normalize(null, at).Should().BeNull();
    }

    [Fact]
    public void Comparison_is_like_for_like_only_for_the_same_scope_revisions()
    {
        var same = DeliveryScopeProvenanceText.Compare(Provenance(3, RevisionA3), Provenance(3, RevisionA3));
        same.LikeForLike.Should().BeTrue();
        same.Notice.Should().StartWith("Same scope revision");

        var different = DeliveryScopeProvenanceText.Compare(Provenance(2, RevisionA2), Provenance(3, RevisionA3));
        different.Match.Should().Be(DeliveryScopeProvenanceMatch.DifferentRevisions);
        different.LikeForLike.Should().BeFalse();
        different.Notice.Should().Be("Compared deliveries used different scope revisions: A Release 1 rev 2 vs B Release 1 rev 3 — membership may differ; percentages are not like-for-like");

        DeliveryScopeProvenanceText.Compare(Provenance(3, RevisionA2), Provenance(3, RevisionA3)).LikeForLike.Should().BeFalse("same number, different revision id");
        DeliveryScopeProvenanceText.Compare(null, Provenance(3, RevisionA3), "Baseline", "Current").Notice.Should().Be("Scope provenance not recorded for Baseline");
        DeliveryScopeProvenanceText.Compare(Provenance(3, RevisionA3), null).Match.Should().Be(DeliveryScopeProvenanceMatch.MissingSecond);
        DeliveryScopeProvenanceText.Compare(null, null).LikeForLike.Should().BeFalse();
    }
}
