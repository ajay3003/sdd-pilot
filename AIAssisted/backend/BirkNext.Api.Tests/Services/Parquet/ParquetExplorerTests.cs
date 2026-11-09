using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Services.ExecutionEvidence;
using BirkNext.Api.Services.Parquet;
using BirkNext.ExecutionEvidence;
using BirkNext.Parquet;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BirkNext.Api.Tests.Services.Parquet;

/// <summary>
/// The Parquet explorer against independent fixtures (Fixtures/make_fixtures.py: a standard-library Python writer that knows nothing of BirkNext's
/// reader) and the oracle that writer computed from its own data. Covers metadata, logical types, values across row groups, data page v2 + GZIP,
/// dictionary pages, Snappy copies, page-only vs full-scan filters, exact profile, rule validation, keyed comparison, refusals and sessions.
/// </summary>
public sealed class ParquetExplorerTests : IDisposable
{
    private static readonly string FixtureDir = Path.Combine(SourceRoot(), "backend", "BirkNext.Api.Tests", "Services", "Parquet", "Fixtures");
    private static readonly JsonElement Oracle = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureDir, "oracle.json"))).RootElement;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "birknext-parquet-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AppDbContext _db;
    private readonly ParquetInspectionService _service;

    public ParquetExplorerTests()
    {
        Directory.CreateDirectory(_root);
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"pq-{Guid.NewGuid():N}").Options);
        _service = new ParquetInspectionService(_db, new ExecutionEvidenceStore(_db), new Host(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private sealed class Host(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "BirkNext.Api";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static string SourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "shared"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private async Task<ParquetOverview> OpenAsync(string name)
    {
        await using var stream = File.OpenRead(Path.Combine(FixtureDir, name));
        var (overview, error) = await _service.OpenAsync(name, stream, stream.Length, default);
        error.Should().BeNull();
        return overview!;
    }

    private static JsonElement V1 => Oracle.GetProperty("children_v1");

    [Fact]
    public async Task Metadata_schema_and_logical_types_match_the_independent_writer()
    {
        var o = await OpenAsync("children_v1.parquet");
        o.Rows.Should().Be(V1.GetProperty("rows").GetInt64());
        o.RowGroups.Select(g => g.Rows).Should().Equal(V1.GetProperty("rowGroups").EnumerateArray().Select(e => e.GetInt64()));
        o.Columns.Select(c => c.Path).Should().Equal(V1.GetProperty("columns").EnumerateArray().Select(e => e.GetString()));
        o.CreatedBy.Should().Contain("independent oracle");
        o.KeyValueMetadata.Should().Contain(new ParquetKeyValue("dataset", "children"));
        o.Columns.Should().OnlyContain(c => c.Readable && c.Codecs.SequenceEqual(new[] { "SNAPPY" }));
        o.Columns.Single(c => c.Path == "status").Encodings.Should().Contain("RLE_DICTIONARY");
        var types = o.Columns.ToDictionary(c => c.Path, c => c.LogicalType);
        types["name"].Should().Be("STRING"); types["birth_date"].Should().Be("DATE"); types["registered_at"].Should().Be("TIMESTAMP(MICROS, UTC)"); types["amount"].Should().Be("DECIMAL(10,2)");
        o.Columns.Single(c => c.Path == "id").Optional.Should().BeFalse();
        foreach (var (column, nulls) in V1.GetProperty("nullCounts").EnumerateObject().Select(p => (p.Name, p.Value.GetInt64())))
            o.Columns.Single(c => c.Path == column).Should().Match<ParquetColumnSummary>(c => c.NullCount == nulls && c.NullCountFigure == ParquetFigure.FromMetadata, column);
        o.Columns.Single(c => c.Path == "age").Should().Match<ParquetColumnSummary>(c => c.Min == V1.GetProperty("ageMin").GetInt32().ToString() && c.Max == V1.GetProperty("ageMax").GetInt32().ToString());
        o.Schema.Children.Should().HaveCount(9);
        o.SchemaFingerprint.Should().HaveLength(16);
    }

    [Fact]
    public async Task Values_decode_exactly_across_row_groups_with_type_aware_formatting()
    {
        var o = await OpenAsync("children_v1.parquet");
        var first = _service.Rows(o.SessionId, new ParquetRowsRequest { Offset = 0, Count = 2, Columns = ["id", "name", "age", "status", "birth_date", "registered_at", "amount", "score", "flag"] }, default).Result!;
        var row1 = V1.GetProperty("row1");
        first.Rows[0].Should().Equal("1", row1.GetProperty("name").GetString(), row1.GetProperty("age").GetInt32().ToString(), row1.GetProperty("status").GetString(),
            V1.GetProperty("birthDateRow1").GetString(), V1.GetProperty("registeredAtRow1").GetString(), V1.GetProperty("amountRow1").GetString(), "22.321", "false");
        first.Scope.Should().Be("Rows 1–2 of 1000");
        var across = _service.Rows(o.SessionId, new ParquetRowsRequest { Offset = 398, Count = 4, Columns = ["id", "name"] }, default).Result!;
        across.RowNumbers.Should().Equal(399, 400, 401, 402);
        across.Rows.Select(r => r[0]).Should().Equal("399", "400", "401", "402");
        across.Rows[2][1].Should().Be(V1.GetProperty("row401").GetProperty("name").GetString());
        var last = _service.Rows(o.SessionId, new ParquetRowsRequest { Offset = 999, Count = 50, Columns = ["id", "status"] }, default).Result!;
        last.Rows.Should().ContainSingle().Which.Should().Equal("1000", V1.GetProperty("row1000").GetProperty("status").GetString());
        var nullName = _service.Rows(o.SessionId, new ParquetRowsRequest { Offset = 16, Count = 1, Columns = ["id", "name"] }, default).Result!;
        nullName.Rows[0].Should().Equal("17", null);
    }

    [Fact]
    public async Task Data_page_v2_with_gzip_and_a_changed_schema_is_read()
    {
        var o = await OpenAsync("children_v2.parquet");
        var v2 = Oracle.GetProperty("children_v2");
        o.Rows.Should().Be(v2.GetProperty("rows").GetInt64());
        o.Columns.Select(c => c.Path).Should().Equal(v2.GetProperty("columns").EnumerateArray().Select(e => e.GetString()));
        o.Columns.Should().OnlyContain(c => c.Codecs.SequenceEqual(new[] { "GZIP" }) && c.Readable);
        var page = _service.Rows(o.SessionId, new ParquetRowsRequest { Offset = 598, Count = 4, Columns = ["id", "school"] }, default).Result!;
        page.Rows.Should().HaveCount(4);
        page.Rows.Select(r => r[0]).Should().OnlyContain(v => long.Parse(v!) > 0);
    }

    [Fact]
    public async Task Page_filters_are_labelled_page_only_and_full_scans_count_every_row()
    {
        var o = await OpenAsync("children_v1.parquet");
        var page = _service.Rows(o.SessionId, new ParquetRowsRequest { Count = 50, Columns = ["id", "status"], Filters = [new("status", ParquetFilterOperator.Equals, "ACTIVE")] }, default).Result!;
        page.Scope.Should().Contain("this page").And.Contain("not to the whole file");
        page.ScannedRows.Should().Be(50);
        var full = _service.Rows(o.SessionId, new ParquetRowsRequest { Count = 10, Columns = ["id", "status"], Filters = [new("status", ParquetFilterOperator.Equals, "ACTIVE")], FullScan = true }, default).Result!;
        full.MatchingRows.Should().Be(V1.GetProperty("statusCounts").GetProperty("ACTIVE").GetInt64());
        full.ScannedRows.Should().Be(1000);
        full.Rows.Should().HaveCount(10).And.OnlyContain(r => r[1] == "ACTIVE");
        full.Truncated.Should().BeTrue();
        Count(o, new("name", ParquetFilterOperator.IsNull)).Should().Be(V1.GetProperty("nullCounts").GetProperty("name").GetInt64());
        Count(o, new("age", ParquetFilterOperator.GreaterThan, "15")).Should().Be(V1.GetProperty("ageOver15").GetInt64());
        Count(o, new("status", ParquetFilterOperator.In, Values: ["ACTIVE", "MOVED"])).Should().Be(320 + 324);
        Count(o, new("birth_date", ParquetFilterOperator.Between, "2000-01-01", "2100-01-01")).Should().Be(1000);
        Count(o, new("age", ParquetFilterOperator.GreaterThan, "not a number")).Should().Be(0, "an unreadable comparison value never matches");
    }

    private long Count(ParquetOverview o, ParquetFilter filter) =>
        _service.Rows(o.SessionId, new ParquetRowsRequest { Count = 1, Columns = ["id"], Filters = [filter], FullScan = true }, default).Result!.MatchingRows!.Value;

    [Fact]
    public async Task The_profile_is_an_exact_full_scan()
    {
        var o = await OpenAsync("children_v1.parquet");
        var profile = _service.Profile(o.SessionId, null, default).Profile!;
        profile.ScannedRows.Should().Be(1000);
        foreach (var (column, nulls) in V1.GetProperty("nullCounts").EnumerateObject().Select(p => (p.Name, p.Value.GetInt64())))
            profile.Columns.Single(c => c.Column == column).Nulls.Should().Be(nulls, column);
        profile.Columns.Single(c => c.Column == "id").Distinct.Should().Be(V1.GetProperty("distinctIds").GetInt64());
        profile.Columns.Single(c => c.Column == "status").Distinct.Should().Be(3);
        profile.Columns.Single(c => c.Column == "age").Should().Match<ParquetColumnProfile>(c => c.Min == "0" && c.Max == "18" && c.DistinctFigure == "Exact");
    }

    [Fact]
    public async Task Rules_report_outcome_scope_and_rows_examined_never_values()
    {
        var o = await OpenAsync("children_v1.parquet");
        var rules = new ParquetRuleSet
        {
            Name = "children", Version = 2,
            Rules =
            [
                new() { Id = "id-unique", Kind = ParquetRuleKind.Unique, Column = "id" },
                new() { Id = "name-required", Kind = ParquetRuleKind.NotNull, Column = "name" },
                new() { Id = "status-values", Kind = ParquetRuleKind.AllowedValues, Column = "status", Values = ["ACTIVE", "INACTIVE", "MOVED"] },
                new() { Id = "age-range", Kind = ParquetRuleKind.Range, Column = "age", Min = "0", Max = "18" },
                new() { Id = "count", Kind = ParquetRuleKind.RecordCount, Expected = "1000" },
                new() { Id = "birth-date-type", Kind = ParquetRuleKind.TypeIs, Column = "birth_date", Expected = "DATE" },
                new() { Id = "school-exists", Kind = ParquetRuleKind.ColumnExists, Column = "school" },
            ],
        };
        var v = _service.Validate(o.SessionId, rules, default).Validation!;
        var r = v.Results.ToDictionary(x => x.RuleId);
        r["id-unique"].Outcome.Should().Be(ParquetRuleOutcome.Pass);
        r["name-required"].Should().Match<ParquetRuleResult>(x => x.Outcome == ParquetRuleOutcome.Fail && x.Actual == "58 row(s) do not" && x.FailingRowNumbers.SequenceEqual(new long[] { 17, 34, 51, 68, 85 }) && x.RowsExamined == 1000);
        r["status-values"].Outcome.Should().Be(ParquetRuleOutcome.Pass);
        r["age-range"].Outcome.Should().Be(ParquetRuleOutcome.Pass);
        r["count"].Should().Match<ParquetRuleResult>(x => x.Outcome == ParquetRuleOutcome.Pass && x.Scope.Contains("metadata"));
        r["birth-date-type"].Outcome.Should().Be(ParquetRuleOutcome.Pass);
        r["school-exists"].Outcome.Should().Be(ParquetRuleOutcome.Fail);
        v.Scope.Should().Be("Full scan of all 1,000 rows");
    }

    [Fact]
    public async Task Comparison_by_key_matches_the_oracle_and_without_a_key_stays_schema_level()
    {
        var a = await OpenAsync("children_v1.parquet");
        var b = await OpenAsync("children_v2.parquet");
        var diff = Oracle.GetProperty("diff_v1_v2_by_id");
        var keyed = _service.Compare(a.SessionId, b.SessionId, "id", default).Comparison!;
        keyed.SchemaChanges.Should().Contain(c => c.Path == "school" && c.Change == "Added").And.Contain(c => c.Path == "flag" && c.Change == "Removed");
        keyed.RecordCounts["Added"].Should().Be(diff.GetProperty("added").GetInt64());
        keyed.RecordCounts["Removed"].Should().Be(diff.GetProperty("removed").GetInt64());
        keyed.RecordCounts["Changed"].Should().Be(diff.GetProperty("changed").GetInt64());
        keyed.RecordCounts["Unchanged"].Should().Be(diff.GetProperty("unchanged").GetInt64());
        keyed.Records.Where(r => r.Change == ParquetRecordChange.Changed).Select(r => r.Key).Should().Contain(diff.GetProperty("changedIdsFirst5").EnumerateArray().Select(e => e.GetInt64().ToString()));
        keyed.Records.First(r => r.Change == ParquetRecordChange.Changed).ChangedFields.Should().Contain("status");
        var unkeyed = _service.Compare(a.SessionId, b.SessionId, null, default).Comparison!;
        unkeyed.RecordCounts.Should().BeEmpty();
        unkeyed.Limitations.Should().Contain(l => l.Contains("record-level differences are not available"));
    }

    [Fact]
    public async Task Nested_groups_are_shown_and_unsupported_codecs_are_reported_not_guessed()
    {
        var nested = await OpenAsync("nested_schema.parquet");
        var address = nested.Schema.Children.Single(c => c.Name == "address");
        address.IsGroup.Should().BeTrue();
        address.Children.Select(c => c.Path).Should().Equal("address.street", "address.city");
        address.Children.Should().OnlyContain(c => !c.Readable && c.NotReadableReason!.Contains("no column chunk"));
        nested.Columns.Where(c => c.Readable).Select(c => c.Path).Should().Equal("id", "name");
        var zstd = await OpenAsync("zstd_declared.parquet");
        zstd.Columns.Should().OnlyContain(c => !c.Readable && c.NotReadableReason!.Contains("ZSTD"));
        _service.Rows(zstd.SessionId, new ParquetRowsRequest { Columns = ["id"] }, default).Error.Should().Contain("ZSTD");
    }

    [Theory]
    [InlineData("bad_magic.parquet", "magic")]
    [InlineData("truncated.parquet", "footer")]
    [InlineData("bad_footer_length.parquet", "footer length")]
    [InlineData("empty.parquet", "empty")]
    public async Task Corrupt_files_are_refused_with_a_reason_and_leave_no_session(string name, string reason)
    {
        await using var stream = File.OpenRead(Path.Combine(FixtureDir, name));
        var (overview, error) = await _service.OpenAsync(name, stream, stream.Length, default);
        overview.Should().BeNull();
        error.Should().Contain(reason);
        error.Should().NotContain(_root, "no internal path in an error");
        (Directory.Exists(Path.Combine(_root, "App_Data", "parquet-sessions")) ? Directory.GetFiles(Path.Combine(_root, "App_Data", "parquet-sessions")) : []).Should().BeEmpty();
    }

    [Fact]
    public async Task Sessions_use_their_own_names_and_closing_removes_only_the_temporary_copy()
    {
        var original = Path.Combine(FixtureDir, "children_v1.parquet");
        var before = File.ReadAllBytes(original);
        await using (var stream = File.OpenRead(original))
        {
            var (overview, _) = await _service.OpenAsync("..\\..\\evil<script>.parquet", stream, stream.Length, default);
            overview!.FileName.Should().Be("evilscript.parquet", "a file name is display text only (path segments and markup characters removed)");
            var stored = Directory.GetFiles(Path.Combine(_root, "App_Data", "parquet-sessions"));
            stored.Should().HaveCount(2).And.OnlyContain(p => Path.GetFileName(p).StartsWith(overview.SessionId.ToString("N")));
            _service.Close(overview.SessionId).Should().BeTrue();
            Directory.GetFiles(Path.Combine(_root, "App_Data", "parquet-sessions")).Should().BeEmpty();
            _service.Rows(overview.SessionId, new ParquetRowsRequest(), default).Error.Should().Contain("expired");
        }
        File.ReadAllBytes(original).Should().Equal(before, "the source file is never modified");
    }

    [Fact]
    public async Task Saved_evidence_keeps_results_and_fingerprints_but_no_row_values_and_links_only_existing_runs()
    {
        var o = await OpenAsync("children_v1.parquet");
        var rules = new ParquetRuleSet { Name = "children", Rules = [new() { Id = "name-required", Kind = ParquetRuleKind.NotNull, Column = "name" }] };
        (await _service.SaveEvidenceAsync(new ParquetEvidenceRequest(o.SessionId, "env-a", rules, null, null, Guid.NewGuid(), null), "tester", default)).Error.Should().Contain("not an Execution Evidence run");
        var run = Guid.NewGuid();
        await new ExecutionEvidenceStore(_db).CreateAsync(new ExecutionRunSummary { RunId = run, Name = "produce children", EnvironmentId = "env-a", StartedAt = DateTimeOffset.UtcNow, State = ExecutionState.Passed }, [], default);
        var (evidence, error) = await _service.SaveEvidenceAsync(new ParquetEvidenceRequest(o.SessionId, "env-a", rules, null, null, run, "nightly export"), "tester", default);
        error.Should().BeNull();
        evidence!.Should().Match<ParquetInspectionEvidence>(e => e.Rows == 1000 && e.SchemaFingerprint == o.SchemaFingerprint && e.LinkedRunId == run && e.Validation!.Results[0].Outcome == ParquetRuleOutcome.Fail);
        var stored = (await _db.ParquetInspections.SingleAsync()).DocumentJson;
        stored.Should().NotContain("Synthetic child").And.NotContain("ACTIVE");
        _service.Close(o.SessionId);
        (await _service.EvidenceAsync("env-a", default)).Should().ContainSingle("evidence outlives the session");
        (await _service.DeleteEvidenceAsync("env-other", evidence.Id, default)).Should().BeFalse();
        (await _service.DeleteEvidenceAsync("env-a", evidence.Id, default)).Should().BeTrue();
    }

    [Fact]
    public void Malformed_compressed_and_level_data_fail_with_a_format_error()
    {
        var bad = () => Snappy.Decompress(new byte[] { 10, 0x02, 0xFF, 0xFF }, 10);
        bad.Should().Throw<ParquetFormatException>();
        var declared = () => Snappy.Decompress(new byte[] { 5, 0x00, (byte)'a' }, 99);
        declared.Should().Throw<ParquetFormatException>().WithMessage("*declared size*");
        ParquetFile.Hybrid(new byte[] { 8 << 1, 1 }, 1, 8).Should().OnlyContain(v => v == 1);
        ParquetFile.Hybrid(new byte[] { (1 << 1) | 1, 0b1010_1010 }, 1, 8).Should().Equal(0, 1, 0, 1, 0, 1, 0, 1);
        var truncated = () => ParquetFile.Hybrid(new byte[] { (1 << 1) | 1 }, 1, 8);
        truncated.Should().Throw<ParquetFormatException>();
    }
}
