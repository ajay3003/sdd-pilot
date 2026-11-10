using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.ExecutionEvidence;
using BirkNext.Api.Services.Parquet;
using BirkNext.Api.Services.Parquet.Quality;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.ExecutionEvidence;
using BirkNext.Integrations;
using BirkNext.Parquet;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BirkNext.Api.Tests.Services.Parquet;

/// <summary>
/// Parquet Quality Review against independent fixtures: Fixtures/make_quality_fixtures.py writes synthetic coverage files with the independent
/// writer and records every expected outcome in quality-oracle.json from its own rows (never from BirkNext). The contract is the synthetic
/// Fixtures/quality/coverage-contract.md, retained as a Source Analysis contract document of a fake snapshot.
/// </summary>
public sealed class ParquetQualityTests : IDisposable
{
    private static readonly string Dir = Path.Combine(SourceRoot(), "backend", "BirkNext.Api.Tests", "Services", "Parquet", "Fixtures", "quality");
    private static readonly JsonElement Oracle = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "quality-oracle.json"))).RootElement;
    private static readonly string ContractText = File.ReadAllText(Path.Combine(Dir, "coverage-contract.md"));
    private static readonly string WireText = File.ReadAllText(Path.Combine(Dir, "wire-changes.md"));
    private const string ContractPath = "Delivery/specs/contracts/coverage-contract.md";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "birknext-pq-quality-" + Guid.NewGuid().ToString("N"));
    private readonly AppDbContext _db;
    private readonly ParquetInspectionService _parquet;
    private readonly ParquetQualityService _quality;
    private readonly IqrSourceSnapshot _snapshot;
    private readonly IqrSourceSnapshot _twin;

    public ParquetQualityTests()
    {
        Directory.CreateDirectory(_root);
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"pqq-{Guid.NewGuid():N}").Options);
        _parquet = new ParquetInspectionService(_db, new ExecutionEvidenceStore(_db), new Host(_root));
        _snapshot = new IqrSourceSnapshot { Archive = new("synthetic-delivery.zip", new string('a', 64), 10), AnalyzedAt = DateTimeOffset.UtcNow.AddMinutes(-5) };
        _twin = new IqrSourceSnapshot { Archive = new("synthetic-delivery-copy.zip", new string('b', 64), 10), AnalyzedAt = DateTimeOffset.UtcNow.AddMinutes(-10) };
        _quality = new ParquetQualityService(_db, _parquet, new ExecutionEvidenceStore(_db), new Sources(_snapshot, _twin));
        AddDocument(_snapshot.Id, ContractPath, ContractText);
        AddDocument(_snapshot.Id, "Delivery/specs/wire-changes.md", WireText);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private void AddDocument(Guid snapshot, string path, string text) => _db.SourceContractDocuments.Add(new SourceContractDocumentRecord
    {
        Id = Guid.NewGuid(), SnapshotId = snapshot, Path = path, Text = text, CapturedAt = DateTimeOffset.UtcNow,
        Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant(),
    });

    private sealed class Host(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "BirkNext.Api";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class Sources(params IqrSourceSnapshot[] snapshots) : IReviewSourceEvidenceProvider
    {
        public bool SourceAnalysisEnabled => true;
        public Task<IReadOnlyList<IqrSourceSnapshot>> ListAsync(string environmentId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<IqrSourceSnapshot>>(snapshots);
        public Task<IqrSourceSnapshot?> ResolveAsync(string environmentId, Guid snapshotId, CancellationToken ct = default) => Task.FromResult(snapshots.FirstOrDefault(s => s.Id == snapshotId));
    }

    private static string SourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "shared"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private async Task<ParquetOverview> OpenAsync(string name)
    {
        await using var stream = File.OpenRead(Path.Combine(Dir, name));
        var (overview, error) = await _parquet.OpenAsync(name, stream, stream.Length, default);
        error.Should().BeNull();
        return overview!;
    }

    private static JsonElement Expect(string fixture) => Oracle.GetProperty("fixtures").GetProperty(fixture);
    private static int Int(JsonElement e, string name) => e.GetProperty(name).GetInt32();

    private ContractAssociation Association(params ColumnMapping[] declared) => new()
    {
        ProjectKey = "synthetic-delivery", SnapshotId = _snapshot.Id, ContractKey = "synthetic-coverage", ContractVersion = "1.0.0-draft", DocumentPath = ContractPath,
        ReviewStatus = "Confirmed", ColumnMappings = [.. declared],
    };

    /// <summary>Generated rules, every business rule approved by a test lead (a new rule-set version), plus one own composite-unique rule.</summary>
    private async Task<List<ParquetRule>> ApprovedRulesAsync(Guid session, ContractAssociation association)
    {
        var generated = (await _quality.GenerateAsync(session, association, default)).Generation!;
        var approval = new ParquetRuleSetApproval("synthetic-delivery", "synthetic-coverage", "1.0.0-draft", "Coverage rules",
            [.. generated.Rules.Select(r => r with { Review = RuleReviewState.Active })], "approved for the synthetic acceptance");
        (await _quality.ApproveAsync(session, approval, association, "test lead", default)).Error.Should().BeNull();
        var again = (await _quality.GenerateAsync(session, association, default)).Generation!;
        return [.. again.Rules, new ParquetRule { Id = "own.unique-key", Kind = ParquetRuleKind.Unique, Columns = ["category", "measure", "reason_code"], Name = "One row per category, measure and reason" }];
    }

    // ── Parser and generator ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_contract_document_is_parsed_into_columns_requirements_and_documented_renames_only()
    {
        DatasetContractParser.LooksLikeDatasetContract(ContractText).Should().BeTrue();
        DatasetContractParser.LooksLikeDatasetContract(WireText).Should().BeFalse();
        DatasetContractParser.MentionsVersionChange(WireText, "1.0.0-draft").Should().BeTrue();
        var c = DatasetContractParser.Parse(ContractPath, ContractText, "sha", [("wire.md", WireText)]).Should().ContainSingle().Subject;
        c.Should().Match<DatasetContract>(x => x.Key == "synthetic-coverage" && x.Version == "1.0.0-draft" && x.Governance == ContractGovernance.Draft && x.PredecessorVersion == "0.9.0-utkast");
        c.Columns.Select(x => x.Name).Should().Equal("period", "category", "measure", "reason_code", "count");
        c.Columns.Single(x => x.Name == "category").AllowedValues.Should().BeEquivalentTo("TOTAL", "PRIVAT_INSTITUSJON", "FOSTERHJEM");
        c.Columns.Single(x => x.Name == "period").Format.Should().Be("YYYY-MM");
        c.Columns.Single(x => x.Name == "reason_code").Nullable.Should().BeTrue();
        c.Columns.Single(x => x.Name == "count").Should().Match<DatasetContractColumn>(x => x.PhysicalType == "INT32" && !x.Nullable);
        c.SortKeys.Should().Equal("category", "measure", "reason_code");
        c.Renames.Where(r => r.Kind == "column").Select(r => $"{r.From}->{r.To}").Should().Equal("periode->period");
        c.Renames.Where(r => r.Kind == "value").Select(r => r.From).Should().Contain(["TOTALT", "FORVENTET", "MOTTATT", "AVVIST_IKKE_TILORDNET"]);
        c.Renames.Should().NotContain(r => r.From.Contains("periode={p}"), "a path rename is not a column or value rename");
        c.Requirements.Should().Contain(r => r.Category == "Aggregate consistency" && r.Machine);
        c.Requirements.Should().Contain(r => r.Category == "Confidentiality" && !r.Machine && r.Reason!.Contains("NEEDS DOMAIN DECISION"));
        c.Requirements.Should().Contain(r => r.Text.Contains("percentage") && !r.Machine);
        c.Requirements.Should().Contain(r => r.Category == "Delivery" && r.Text.StartsWith("Immutability") && !r.Machine);
        c.Requirements.Should().Contain(r => r.Id == "manifest" && r.Machine);
    }

    [Fact]
    public void Renames_of_another_artifact_and_non_parquet_tables_are_not_contract_evidence()
    {
        // Shapes taken from the real M2LB documents: a term inventory renames the same words for a database table (not the Parquet file), and a
        // data-model document has a column table for a database entity.
        const string inventory = "| Table | Columns |\n|---|---|\n| `dekningsoppsummering` → `dekning_summary` | `kategori`→`category`, `maal`→`measure`, `antall`→`count` |\n";
        var c = DatasetContractParser.Parse(ContractPath, ContractText, "sha", [("wire.md", WireText), ("inventory.md", inventory)]).Single();
        c.Renames.Where(r => r.Kind == "column").Select(r => r.From).Should().Equal(["periode"], "only renames stated for this contract (its version transition or container) count");
        c.Renames.Should().Contain(r => r.From == "MOTTATT" && r.Source == "wire.md", "the wire-changes block names the version transition");
        const string dataModel = "# Data model (Parquet export uses these rows)\n\n## DekningSummaryRow\n\n| Column | Type | Null | Content |\n|---|---|---|---|\n| `category` | text | no | Category |\n";
        DatasetContractParser.Parse("data-model.md", dataModel, "sha").Should().BeEmpty("a database entity table is not a Parquet dataset contract");
    }

    [Fact]
    public async Task Generated_rules_are_classified_and_business_rules_wait_for_review()
    {
        var o = await OpenAsync("q_valid.parquet");
        var g = (await _quality.GenerateAsync(o.SessionId, Association(), default)).Generation!;
        g.Rules.Should().OnlyContain(r => r.Origin == RuleOrigin.ContractDerived && r.ContractVersion == "1.0.0-draft" && r.Provenance!.StartsWith(ContractPath) && r.RequirementIds.Count == 1);
        g.Rules.Where(r => r.Kind is ParquetRuleKind.ColumnExists or ParquetRuleKind.TypeIs or ParquetRuleKind.NotNull or ParquetRuleKind.AllowedValues or ParquetRuleKind.Pattern
            or ParquetRuleKind.RowGroupCount or ParquetRuleKind.Compression or ParquetRuleKind.Manifest or ParquetRuleKind.NoIdentifierValues or ParquetRuleKind.NoProtectedCanary)
            .Should().OnlyContain(r => r.Review == RuleReviewState.Active);
        g.Rules.Where(r => r.Kind is ParquetRuleKind.SumIdentity or ParquetRuleKind.RollUp or ParquetRuleKind.OnlyWith or ParquetRuleKind.NullUnless or ParquetRuleKind.SortedBy)
            .Should().HaveCount(5).And.OnlyContain(r => r.Review == RuleReviewState.NeedsReview, "unapproved business rules are never enabled automatically");
        g.Rules.Single(r => r.Kind == ParquetRuleKind.RollUp).WhenValues.Should().BeEquivalentTo("RECEIVED_NOT_EXPECTED", "REJECTED_UNATTRIBUTED");
        g.Rules.Single(r => r.Kind == ParquetRuleKind.NoIdentifierValues).Columns.Should().BeEquivalentTo("period", "category", "measure", "reason_code");
        // Without a contract: schema-derived rules only describe the file.
        var generic = (await _quality.GenerateAsync(o.SessionId, null, default)).Generation!;
        generic.Rules.Should().OnlyContain(r => r.Origin == RuleOrigin.SchemaDerived);
        // A client cannot raise a generated rule's review state or origin.
        var forged = g.Rules.Select(r => r with { Review = RuleReviewState.Active, Origin = RuleOrigin.ApprovedBusinessRule }).ToList();
        ParquetQualityService.Reconcile(forged, g.Rules).Where(r => r.Kind == ParquetRuleKind.SumIdentity).Should().OnlyContain(r => r.Review == RuleReviewState.NeedsReview && r.Origin == RuleOrigin.ContractDerived);
        ParquetQualityService.Reconcile([new ParquetRule { Id = "x", Kind = ParquetRuleKind.NotNull, Column = "period", Origin = RuleOrigin.ContractDerived }], g.Rules)
            .First().Origin.Should().Be(RuleOrigin.UserAuthored);
    }

    // ── Independent synthetic oracle ─────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("q_valid.parquet")]
    [InlineData("q_null_required.parquet")]
    [InlineData("q_invalid_enum.parquet")]
    [InlineData("q_duplicate_key.parquet")]
    [InlineData("q_wrong_period.parquet")]
    [InlineData("q_bad_aggregate.parquet")]
    [InlineData("q_added_optional.parquet")]
    [InlineData("q_canary.parquet")]
    [InlineData("q_changed_baseline.parquet")]
    [InlineData("q_wrong_type.parquet")]
    [InlineData("q_missing_column.parquet")]
    [InlineData("q_breaking.parquet")]
    public async Task Every_result_matches_the_independent_oracle(string fixture)
    {
        var e = Expect(fixture);
        var o = await OpenAsync(fixture);
        o.Rows.Should().Be(Int(e, "rows"));
        o.Sha256.Should().Be(e.GetProperty("sha256").GetString());
        var rules = await ApprovedRulesAsync(o.SessionId, Association());
        var (review, error) = await _quality.ReviewAsync(o.SessionId, new ParquetQualityRequest { Association = Association(), Rules = new ParquetRuleSet { Name = "acceptance", Rules = rules } }, default);
        error.Should().BeNull();
        review!.Schema.Compatibility.ToString().Should().Be(e.GetProperty("schema").GetString());
        var results = review.Validation!.Results;
        ParquetRuleResult R(ParquetRuleKind kind, string? column = null) => results.Single(r => r.Kind == kind && (column is null || r.Column == column));
        void Count(ParquetRuleResult r, JsonElement expected)
        {
            if (expected.ValueKind == JsonValueKind.Null) { r.Outcome.Should().Be(ParquetRuleOutcome.Inconclusive, $"{r.RuleId}: a column is missing"); return; }
            r.AffectedRows.Should().Be(expected.GetInt32(), r.RuleId);
            r.Outcome.Should().Be(expected.GetInt32() == 0 ? ParquetRuleOutcome.Pass : ParquetRuleOutcome.Fail, r.RuleId);
        }
        if (e.GetProperty("schema").GetString() != "Incompatible" || fixture == "q_wrong_type.parquet")
        {
            Count(R(ParquetRuleKind.NotNull, "category"), e.GetProperty("nullCategory"));
            Count(R(ParquetRuleKind.AllowedValues, "measure"), e.GetProperty("invalidMeasure"));
            Count(R(ParquetRuleKind.Pattern, "period"), e.GetProperty("badPeriod"));
            Count(R(ParquetRuleKind.NullUnless, "reason_code"), e.GetProperty("reasonOutsideRejected"));
            Count(R(ParquetRuleKind.OnlyWith), e.GetProperty("totalOnlyOutsideTotal"));
            Count(R(ParquetRuleKind.SumIdentity), e.GetProperty("sumBadGroups"));
            Count(R(ParquetRuleKind.RollUp), e.GetProperty("rollBadGroups"));
            Count(results.Single(r => r.RuleId == "own.unique-key"), e.GetProperty("duplicateKeys"));
            Count(R(ParquetRuleKind.NoIdentifierValues), e.GetProperty("identifierRows"));
            if (e.GetProperty("sorted").ValueKind == JsonValueKind.True) R(ParquetRuleKind.SortedBy).Outcome.Should().Be(ParquetRuleOutcome.Pass);
            if (e.GetProperty("sorted").ValueKind == JsonValueKind.False) R(ParquetRuleKind.SortedBy).Outcome.Should().Be(ParquetRuleOutcome.Fail);
        }
        R(ParquetRuleKind.RowGroupCount).Outcome.Should().Be(Int(e, "rowGroups") == 1 ? ParquetRuleOutcome.Pass : ParquetRuleOutcome.Fail);
        R(ParquetRuleKind.Compression).Outcome.Should().Be(e.GetProperty("codec").GetString() == "SNAPPY" ? ParquetRuleOutcome.Pass : ParquetRuleOutcome.Fail);
        R(ParquetRuleKind.Manifest).Outcome.Should().Be(ParquetRuleOutcome.Inconclusive, "no manifest attached");
        if (e.TryGetProperty("missing", out var missing))
            foreach (var m in missing.EnumerateArray())
            {
                review.Schema.Findings.Should().Contain(f => f.Column == m.GetString() && f.Kind == SchemaFindingKind.Missing);
                review.Coverage.Single(c => c.RequirementId.EndsWith("#col-" + m.GetString())).Status.Should().Be(RequirementStatus.CoveredFail, "the column-exists rule fails");
                review.Coverage.Single(c => c.Category == "Aggregate consistency").Status.Should().Be(RequirementStatus.Blocked, "rules needing the missing column cannot run");
            }
        if (e.TryGetProperty("typeMismatch", out var types))
            foreach (var t in types.EnumerateArray()) review.Schema.Findings.Should().Contain(f => f.Column == t.GetString() && f.Kind == SchemaFindingKind.PhysicalType);
        if (e.TryGetProperty("unexpected", out var unexpected))
            foreach (var u in unexpected.EnumerateArray()) review.Schema.Findings.Should().Contain(f => f.Column == u.GetString() && f.Kind == SchemaFindingKind.Unexpected && f.Severity == "Warning");
        // The quality dimensions are separate: never one green PASS.
        review.Dimensions.Select(d => d.Name).Should().Equal("File readable", "Schema compatible", "Data valid", "Business rules verified", "E2E evidence verified");
        review.Dimensions[4].State.Should().Be(QualityState.NotAssessed);
        review.Coverage.Single(c => c.Text.Contains("percentage")).Status.Should().Be(RequirementStatus.NotSupported);
        review.Coverage.Single(c => c.Text.Contains("Kode 6/7")).Should().Match<RequirementCoverage>(c => c.Status == RequirementStatus.NotAssessed && c.Reason.StartsWith("NEEDS DOMAIN DECISION"));
        // Contract aggregate (exact pivot) equals the oracle's sums.
        if (e.TryGetProperty("sumByCategoryMeasure", out var sums) && review.Schema.Compatibility != SchemaCompatibility.Incompatible)
            foreach (var cell in sums.EnumerateObject())
            {
                // The oracle writes a missing category as Python's None; BirkNext labels it (null).
                var parts = cell.Name.Split('|');
                var row = parts[0] == "None" ? "(null)" : parts[0];
                review.Statistics!.Pivot.Single(p => p.Row == row && p.Column == parts[1]).Value.Should().Be(cell.Value.GetInt32());
            }
    }

    [Fact]
    public async Task Business_rules_are_not_assessed_until_approved()
    {
        var o = await OpenAsync("q_bad_aggregate.parquet");
        var g = (await _quality.GenerateAsync(o.SessionId, Association(), default)).Generation!;
        var review = (await _quality.ReviewAsync(o.SessionId, new ParquetQualityRequest { Association = Association(), Rules = new ParquetRuleSet { Name = "g", Rules = g.Rules } }, default)).Result!;
        review.Validation!.Results.Should().NotContain(r => r.Kind == ParquetRuleKind.SumIdentity, "an unapproved business rule is not run");
        review.Coverage.Single(c => c.Category == "Aggregate consistency").Should().Match<RequirementCoverage>(c => c.Status == RequirementStatus.NotAssessed && c.Review == "Needs review");
        review.Dimensions.Single(d => d.Name == "Business rules verified").State.Should().Be(QualityState.NotAssessed);
        review.NextAction.Should().Be("Review findings");
        // After approval the same failure is a covered failure with a drill-down to the inconsistent group.
        var approved = await ApprovedRulesAsync(o.SessionId, Association());
        approved.Single(r => r.Kind == ParquetRuleKind.SumIdentity).Origin.Should().Be(RuleOrigin.ApprovedBusinessRule);
        var after = (await _quality.ReviewAsync(o.SessionId, new ParquetQualityRequest { Association = Association(), Rules = new ParquetRuleSet { Name = "a", Rules = approved } }, default)).Result!;
        var sum = after.Validation!.Results.Single(r => r.Kind == ParquetRuleKind.SumIdentity);
        sum.Outcome.Should().Be(ParquetRuleOutcome.Fail);
        sum.Details.Should().ContainSingle(d => d.StartsWith("category=FOSTERHJEM"));
        sum.DrillFilters.Should().ContainSingle(f => f.Column == "category" && f.Operator == ParquetFilterOperator.In && f.Values!.Contains("FOSTERHJEM"));
        after.Coverage.Single(c => c.Category == "Aggregate consistency").Status.Should().Be(RequirementStatus.CoveredFail);
        await ApprovedRulesAsync(o.SessionId, Association());
        (await _quality.RuleSetsAsync("synthetic-delivery", "synthetic-coverage", "1.0.0-draft", default)).Select(v => v.Version).Should().Equal([2, 1], "every approval is a new immutable version");
    }

    // ── Discovery and mappings ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Discovery_distinguishes_candidate_ambiguous_unresolved_and_authoritative()
    {
        var valid = await OpenAsync("q_valid.parquet");
        var d = (await _quality.DiscoverAsync(valid.SessionId, null, default)).Discovery!;
        d.Outcome.Should().Be(ContractMatchKind.CandidateMapping, "a full schema match is grounded but not proof");
        d.Candidates.Single().Should().Match<ContractCandidate>(c => c.Confidence == "Medium" && c.MatchedColumns == 5 && c.Source.SnapshotId == _snapshot.Id);

        var other = await OpenAsync("q_other_dataset.parquet");
        (await _quality.DiscoverAsync(other.SessionId, null, default)).Discovery!.Outcome.Should().Be(ContractMatchKind.Unresolved);

        var legacy = await OpenAsync("q_legacy.parquet");
        var ld = (await _quality.DiscoverAsync(legacy.SessionId, null, default)).Discovery!;
        var lc = ld.Candidates.Single();
        lc.DocumentedMappings.Select(m => $"{m.Actual}->{m.Expected}").Should().Equal(Expect("q_legacy.parquet").GetProperty("documentedMappings").EnumerateArray().Select(x => x.GetString()));
        lc.UnmappedExpected.Should().BeEquivalentTo("category", "measure", "reason_code", "count");
        lc.Reasons.Should().Contain(r => r.StartsWith("Not explained by the source"));

        // The same document in a second project: ambiguous until the tester chooses the project.
        AddDocument(_twin.Id, ContractPath, ContractText);
        await _db.SaveChangesAsync();
        var amb = (await _quality.DiscoverAsync(valid.SessionId, null, default)).Discovery!;
        amb.Outcome.Should().Be(ContractMatchKind.Ambiguous);
        amb.Candidates.Select(c => c.Source.ProjectKey).Should().BeEquivalentTo("synthetic-delivery", "synthetic-delivery-copy");
        (await _quality.DiscoverAsync(valid.SessionId, "synthetic-delivery", default)).Discovery!.Outcome.Should().Be(ContractMatchKind.CandidateMapping);

        // A manifest that names this exact file and contract version makes the association authoritative.
        _parquet.AttachManifest(valid.SessionId, $$"""{"period":"2026-10","contractVersion":"1.0.0-draft","files":[{"name":"data.parquet","sha256":"{{valid.Sha256}}","rowCount":{{valid.Rows}},"bytes":{{valid.SizeBytes}}}]}""").Error.Should().BeNull();
        var auth = (await _quality.DiscoverAsync(valid.SessionId, "synthetic-delivery", default)).Discovery!;
        auth.Outcome.Should().Be(ContractMatchKind.AuthoritativeContract);
        var rules = await ApprovedRulesAsync(valid.SessionId, Association());
        var review = (await _quality.ReviewAsync(valid.SessionId, new ParquetQualityRequest { Association = Association(), Rules = new ParquetRuleSet { Name = "m", Rules = rules } }, default)).Result!;
        review.Validation!.Results.Single(r => r.Kind == ParquetRuleKind.Manifest).Outcome.Should().Be(ParquetRuleOutcome.Pass);
    }

    [Fact]
    public async Task A_legacy_file_validates_only_through_declared_mappings_and_documented_value_renames()
    {
        var o = await OpenAsync("q_legacy.parquet");
        var bare = Association();
        var (plain, _) = await _quality.ReviewAsync(o.SessionId, new ParquetQualityRequest { Association = bare, Rules = new ParquetRuleSet { Name = "g", Rules = (await _quality.GenerateAsync(o.SessionId, bare, default)).Generation!.Rules } }, default);
        plain!.Schema.Compatibility.Should().Be(SchemaCompatibility.Incompatible, "as delivered, the legacy names do not match the contract");
        plain.Schema.Findings.Should().Contain(f => f.Kind == SchemaFindingKind.Mapped && f.ActualColumn == "periode");
        (await _quality.ResolveAsync(o, Association(new ColumnMapping("kategori", "nonexistent", "Declared")), default)).Error.Should().Contain("no column nonexistent");
        (await _quality.ResolveAsync(o, Association(new ColumnMapping("kategori", "period", "Declared")), default)).Error.Should().Contain("already mapped");

        var mapped = Association(new("kategori", "category", "Declared"), new("maal", "measure", "Declared"), new("aarsakskode", "reason_code", "Declared"), new("antall", "count", "Declared"));
        var rules = await ApprovedRulesAsync(o.SessionId, mapped);
        var review = (await _quality.ReviewAsync(o.SessionId, new ParquetQualityRequest { Association = mapped, Rules = new ParquetRuleSet { Name = "m", Rules = rules } }, default)).Result!;
        review.Schema.Compatibility.Should().Be(SchemaCompatibility.Compatible);
        review.Dimensions.Single(d => d.Name == "Schema compatible").State.Should().Be(QualityState.Partial, "compatible only through declared mappings");
        var oracle = Expect("q_legacy.parquet").GetProperty("mappedOracle");
        var results = review.Validation!.Results;
        results.Single(r => r.Kind == ParquetRuleKind.AllowedValues && r.Column == "measure").AffectedRows.Should().Be(oracle.GetProperty("invalidMeasure").GetInt32(), "values translate through the documented renames");
        results.Single(r => r.Kind == ParquetRuleKind.SumIdentity).AffectedRows.Should().Be(oracle.GetProperty("sumBadGroups").GetInt32());
        results.Single(r => r.Kind == ParquetRuleKind.RollUp).AffectedRows.Should().Be(oracle.GetProperty("rollBadGroups").GetInt32());
        results.Single(r => r.Kind == ParquetRuleKind.Compression).Outcome.Should().Be(ParquetRuleOutcome.Fail, "the legacy file is uncompressed; the contract fixes Snappy");
        review.Validation.Scope.Should().Contain("kategori→category");
        review.Statistics!.Pivot.Should().Contain(p => p.Row == "TOTAL" && p.Column == "EXPECTED");
    }

    // ── Confidentiality ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Canary_and_identifier_scans_count_without_revealing_values_and_absence_is_not_proof()
    {
        var o = await OpenAsync("q_canary.parquet");
        var e = Expect("q_canary.parquet");
        var canary = Oracle.GetProperty("canary").GetString()!;
        var rules = new ParquetRuleSet { Name = "c", Rules = [new() { Id = "canary", Kind = ParquetRuleKind.NoProtectedCanary }, new() { Id = "ids", Kind = ParquetRuleKind.NoIdentifierValues }] };
        var none = _parquet.Validate(o.SessionId, rules, new QualityContext { CanaryCatalogAvailable = true }, default).Validation!;
        none.Results.Single(r => r.RuleId == "canary").Outcome.Should().Be(ParquetRuleOutcome.Inconclusive, "no approved canary to look for");
        var v = _parquet.Validate(o.SessionId, rules, new QualityContext { Canaries = [canary], CanaryCatalogAvailable = true }, default).Validation!;
        v.Results.Single(r => r.RuleId == "canary").Should().Match<ParquetRuleResult>(r => r.Outcome == ParquetRuleOutcome.Fail && r.AffectedRows == Int(e, "canaryRows"));
        v.Results.Single(r => r.RuleId == "ids").AffectedRows.Should().Be(Int(e, "identifierRows"));
        JsonSerializer.Serialize(v).Should().NotContain(canary).And.NotContain("01017099999");
        var clean = await OpenAsync("q_valid.parquet");
        _parquet.Validate(clean.SessionId, rules, new QualityContext { Canaries = [canary], CanaryCatalogAvailable = true }, default).Validation!.Results.Single(r => r.RuleId == "canary")
            .Explanation.Should().Contain("not proof");
    }

    // ── Evidence, correlation, report ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Evidence_stores_no_row_values_and_correlation_is_graded()
    {
        var o = await OpenAsync("q_valid.parquet");
        var baseline = await OpenAsync("q_changed_baseline.parquet");
        var rules = await ApprovedRulesAsync(o.SessionId, Association());
        var store = new ExecutionEvidenceStore(_db);
        var runOther = Guid.NewGuid();
        await store.CreateAsync(new ExecutionRunSummary { RunId = runOther, Name = "other env", EnvironmentId = "env-b", StartedAt = DateTimeOffset.UtcNow, State = ExecutionState.Passed }, [], default);
        var unrelated = Guid.NewGuid();
        await store.CreateAsync(new ExecutionRunSummary { RunId = unrelated, Name = "unrelated", EnvironmentId = "env-a", StartedAt = DateTimeOffset.UtcNow.AddDays(-30), State = ExecutionState.Passed }, [], default);
        for (var i = 0; i < 501; i++)
            await store.CreateAsync(new ExecutionRunSummary { RunId = Guid.NewGuid(), Name = $"newer {i}", EnvironmentId = "env-a", StartedAt = DateTimeOffset.UtcNow.AddMinutes(-i), State = ExecutionState.Passed }, [], default);
        var producing = Guid.NewGuid();
        await store.CreateAsync(new ExecutionRunSummary { RunId = producing, Name = "export coverage", EnvironmentId = "env-a", StartedAt = DateTimeOffset.UtcNow, State = ExecutionState.Passed },
            [new ExecutionCallRecord { CallId = Guid.NewGuid(), Sequence = 1, State = ExecutionState.Passed, Reason = $"manifest sha256 {o.Sha256}" }], default);
        var candidate = Guid.NewGuid();
        await store.CreateAsync(new ExecutionRunSummary { RunId = candidate, Name = "same source", EnvironmentId = "env-a", StartedAt = DateTimeOffset.UtcNow, State = ExecutionState.Passed, SourceSnapshotId = _snapshot.Id }, [], default);

        ParquetEvidenceRequest Req(Guid? run, bool manual = false, string? reason = null) =>
            new(o.SessionId, "env-a", new ParquetRuleSet { Name = "r", Rules = rules }, baseline.SessionId, null, run, "note") { Association = Association(), KeyColumns = ["category", "measure", "reason_code"], ManualLink = manual, ManualReason = reason };
        (await _quality.SaveEvidenceAsync(Req(Guid.NewGuid()), "tester", default)).Error.Should().Contain("does not exist");
        (await _quality.SaveEvidenceAsync(Req(runOther), "tester", default)).Error.Should().Contain("another Target Environment");
        (await _quality.SaveEvidenceAsync(Req(unrelated), "tester", default)).Error.Should().Contain("manual, unverified");
        (await _quality.SaveEvidenceAsync(Req(unrelated, manual: true), "tester", default)).Error.Should().Contain("reason");
        var manual = (await _quality.SaveEvidenceAsync(Req(unrelated, true, "exported by hand"), "tester", default)).Evidence!;
        manual.Correlation!.Level.Should().Be(CorrelationLevel.ManualUnverified, "a run older than the newest 500 is still found");
        manual.Dimensions.Single(d => d.Name == "E2E evidence verified").State.Should().Be(QualityState.NotAssessed);
        (await _quality.SaveEvidenceAsync(Req(candidate), "tester", default)).Evidence!.Correlation!.Level.Should().Be(CorrelationLevel.Candidate);
        var confirmed = (await _quality.SaveEvidenceAsync(Req(producing), "tester", default)).Evidence!;
        confirmed.Correlation!.Level.Should().Be(CorrelationLevel.Confirmed);
        confirmed.Dimensions.Single(d => d.Name == "E2E evidence verified").State.Should().Be(QualityState.Pass);
        confirmed.Association!.Should().Match<ContractAssociation>(a => a.ReviewStatus == "Confirmed" && a.ReviewedBy == "tester" && a.SnapshotId == _snapshot.Id && a.DocumentSha256.Length == 64);
        confirmed.Coverage.Should().NotBeEmpty();
        confirmed.Baseline!.Comparable.Should().BeTrue();
        confirmed.Comparison!.RecordCounts["Changed"].Should().BeGreaterThan(0);
        confirmed.Comparison.Records.Should().OnlyContain(r => r.Key == "", "record keys are row values and are never stored");
        confirmed.Validation!.Results.Should().OnlyContain(r => r.Details.Count == 0 && r.DrillFilters.Count == 0);

        var stored = (await _db.ParquetInspections.SingleAsync(r => r.Id == confirmed.Id)).DocumentJson;
        stored.Should().NotContain("MISSING_REFERENCE").And.NotContain("INVALID_MESSAGE", "reason codes are not contract-enumerated values and stay out of evidence");
        stored.Should().Contain("PRIVAT_INSTITUSJON", "contract-enumerated category counts are kept");

        // The session expires; history and the report stay.
        _parquet.Close(o.SessionId);
        var report = ParquetQualityService.Report((await _parquet.EvidenceAsync("env-a", confirmed.Id, default))!);
        for (var i = 1; i <= 14; i++) report.Should().Contain($"## {i}. ");
        report.Should().Contain("cannot re-run validation").And.Contain("Confirmed").And.Contain("draft").And.NotContain("MISSING_REFERENCE");
        (await _quality.SaveEvidenceAsync(Req(producing), "tester", default)).Error.Should().Contain("expired");
    }

    [Fact]
    public async Task Baseline_classification_separates_regression_change_and_unrelated_datasets()
    {
        var valid = await OpenAsync("q_valid.parquet");
        var bad = await OpenAsync("q_bad_aggregate.parquet");
        var changed = await OpenAsync("q_changed_baseline.parquet");
        var rules = await ApprovedRulesAsync(valid.SessionId, Association());
        var quality = new ParquetQualityRequest { Association = Association(), Rules = new ParquetRuleSet { Name = "b", Rules = rules } };
        var regression = (await _quality.BaselineAsync(new ParquetBaselineRequest { CurrentSessionId = bad.SessionId, BaselineSessionId = valid.SessionId, Quality = quality }, default)).Result!.Assessment;
        regression.Overall.Should().Be(BaselineClass.Regression);
        regression.Findings.Should().Contain(f => f.Area == "Rule" && f.Class == BaselineClass.Regression);
        var change = (await _quality.BaselineAsync(new ParquetBaselineRequest { CurrentSessionId = changed.SessionId, BaselineSessionId = valid.SessionId, Quality = quality }, default)).Result!.Assessment;
        change.Overall.Should().Be(BaselineClass.Inconclusive, "changed counts are not a regression without an approved expectation");
        change.Findings.Should().Contain(f => f.Area == "Aggregate").And.NotContain(f => f.Class == BaselineClass.Regression);
        var improvement = (await _quality.BaselineAsync(new ParquetBaselineRequest { CurrentSessionId = valid.SessionId, BaselineSessionId = bad.SessionId, Quality = quality }, default)).Result!.Assessment;
        improvement.Findings.Should().Contain(f => f.Class == BaselineClass.Improvement);
        // A saved baseline of another dataset is never compared as a regression.
        var foreign = new ParquetInspectionEvidence { EnvironmentId = "env-a", FileName = "other.parquet", Sha256 = new string('c', 64), Rows = 3, SavedAt = DateTimeOffset.UtcNow, SavedBy = "x",
            DocumentVersion = 2, Association = Association() with { ContractKey = "another-dataset" } };
        _db.ParquetInspections.Add(new ParquetInspectionRecord { Id = foreign.Id, EnvironmentId = "env-a", FileName = foreign.FileName, SavedAt = foreign.SavedAt, DocumentJson = JsonSerializer.Serialize(foreign, new JsonSerializerOptions(JsonSerializerDefaults.Web)) });
        await _db.SaveChangesAsync();
        var unrelated = (await _quality.BaselineAsync(new ParquetBaselineRequest { CurrentSessionId = valid.SessionId, BaselineEvidenceId = foreign.Id, EnvironmentId = "env-a", Quality = quality }, default)).Result!.Assessment;
        unrelated.Should().Match<BaselineAssessment>(a => !a.Comparable && a.Overall == BaselineClass.NotComparable);
        (await _quality.ApproveBaselineAsync("env-a", foreign.Id, "lead", default)).Evidence!.BaselineApprovedBy.Should().Be("lead");
    }

    // ── Sessions ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Live_sessions_are_capped_and_recent_sessions_are_listed_until_they_expire()
    {
        var opened = new List<ParquetOverview>();
        for (var i = 0; i < ParquetInspectionService.MaxLiveSessions + 2; i++) opened.Add(await OpenAsync("q_valid.parquet"));
        Directory.GetFiles(Path.Combine(_root, "App_Data", "parquet-sessions"), "*.parquet").Should().HaveCount(ParquetInspectionService.MaxLiveSessions);
        opened.Count(x => _parquet.Session(x.SessionId) is null).Should().Be(2, "the oldest sessions were closed to stay within the cap");
        _parquet.Session(opened[^1].SessionId).Should().NotBeNull();
        var closed = opened.First(x => _parquet.Session(x.SessionId) is null);
        var last = opened[^1].SessionId;
        _parquet.Sessions([closed.SessionId, last]).Should().ContainSingle(x => x.SessionId == last);
        _parquet.AttachManifest(opened[^1].SessionId, "[1,2]").Error.Should().Contain("not a JSON object");
        _parquet.AttachManifest(opened[^1].SessionId, "{").Error.Should().Contain("not valid JSON");
    }
}
