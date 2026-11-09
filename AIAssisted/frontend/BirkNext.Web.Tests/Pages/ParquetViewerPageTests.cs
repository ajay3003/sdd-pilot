using BirkNext.Parquet;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Parquet Data Explorer page: a file opens into Overview (metadata labelled as metadata, unreadable columns explained), Data shows the backend's
/// scope text (page-only filters say so), validation shows outcome / scope / failing row numbers, and the comparison shows schema changes and record
/// counts by key. Synthetic fixtures; the page never computes figures itself.
/// </summary>
public sealed class ParquetViewerPageTests : BunitContext
{
    private static readonly Guid Session = Guid.Parse("aaaaaaaa-1111-0000-0000-000000000001");
    private static readonly Guid Baseline = Guid.Parse("bbbbbbbb-1111-0000-0000-000000000002");
    private readonly Mock<IParquetApiService> _api = new();

    public ParquetViewerPageTests()
    {
        _api.Setup(a => a.OpenAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string name, Stream _, CancellationToken _) => (name.Contains("v2") ? Overview(Baseline, name) : Overview(Session, name), null));
        _api.Setup(a => a.RowsAsync(Session, It.IsAny<ParquetRowsRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync((new ParquetRowsResult
        {
            Columns = ["id", "name"], Rows = [["1", "Synthetic child 1"], ["17", null]], RowNumbers = [1, 17], TotalRows = 1000, ScannedRows = 50, MatchingRows = 2,
            Scope = "Filters applied to rows 1–50 only (this page), not to the whole file",
        }, null));
        _api.Setup(a => a.ValidateAsync(Session, It.IsAny<ParquetRuleSet>(), It.IsAny<CancellationToken>())).ReturnsAsync((new ParquetValidation("Rules", 1,
            [new ParquetRuleResult("r1-notnull", ParquetRuleKind.NotNull, "name", ParquetRuleOutcome.Fail, "no nulls", "58 row(s) do not", "Full scan", 1000, [17, 34], "Row numbers of the first violations are listed (values are not).")],
            1000, "Full scan of all 1,000 rows"), null));
        _api.Setup(a => a.CompareAsync(It.IsAny<ParquetCompareRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync((new ParquetComparison
        {
            BaselineSessionId = Baseline, CurrentSessionId = Session, KeyColumn = "id",
            SchemaChanges = [new("school", "Added", null, "BYTE_ARRAY / STRING"), new("flag", "Removed", "BOOLEAN", null)],
            RecordCounts = new() { ["Added"] = 15, ["Removed"] = 10, ["Changed"] = 19, ["Unchanged"] = 971, ["Ambiguous"] = 0 },
            Records = [new("51", ParquetRecordChange.Changed, ["age", "status"])], Limitations = ["Row order is not identity."],
        }, null));
        Services.AddSingleton(_api.Object);
        Services.AddSingleton(new Mock<IFrontendAnalysisContextFactory>().Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static ParquetOverview Overview(Guid id, string name) => new()
    {
        SessionId = id, FileName = name, SizeBytes = 39103, Sha256 = new string('c', 64), Rows = 1000, CreatedBy = "independent writer", SchemaFingerprint = "abcdef0123456789",
        Columns =
        [
            new("id", "INT64", null, false, 0, ParquetFigure.FromMetadata, "1", "1000", ParquetFigure.FromMetadata, ["SNAPPY"], ["PLAIN", "RLE"], 100, 200, true, null),
            new("name", "BYTE_ARRAY", "STRING", true, 58, ParquetFigure.FromMetadata, null, null, ParquetFigure.NotAvailable, ["SNAPPY"], ["PLAIN"], 100, 200, true, null),
            new("tags", "BYTE_ARRAY", "STRING", true, null, ParquetFigure.NotAvailable, null, null, ParquetFigure.NotAvailable, ["SNAPPY"], ["PLAIN"], 0, 0, false, "Repeated values (lists, maps) are not read by this explorer; the schema is shown."),
        ],
        Schema = new ParquetSchemaNode { Children = [new() { Path = "id", Name = "id", PhysicalType = "INT64", Repetition = "REQUIRED", Readable = true }] },
        RowGroups = [new(0, 400, 1, 1), new(1, 400, 1, 1), new(2, 200, 1, 1)], Limitations = ["Statistics shown on Overview come from the file's metadata."],
        OpenedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(2),
    };

    private static InputFileContent File(string name) => InputFileContent.CreateFromBinary([0x50, 0x41, 0x52, 0x31], name);

    private IRenderedComponent<ParquetViewer> OpenPage()
    {
        var cut = Render<ParquetViewer>();
        cut.FindComponent<InputFile>().UploadFiles(File("children_v1.parquet"));
        cut.WaitForAssertion(() => cut.Find("[data-testid=pq-overview]"));
        return cut;
    }

    [Fact]
    public void Opening_a_file_shows_metadata_as_metadata_and_explains_unreadable_columns()
    {
        var cut = OpenPage();
        cut.Find("[data-testid=pq-rows]").TextContent.Should().Be("1,000");
        var columns = cut.FindAll("[data-testid=pq-column]");
        columns.Should().HaveCount(3);
        columns[1].TextContent.Should().Contain("58").And.Contain("not in metadata");
        columns[2].GetAttribute("data-readable").Should().Be("false");
        columns[2].TextContent.Should().Contain("Repeated values");
    }

    [Fact]
    public void Data_shows_the_backends_scope_and_null_cells()
    {
        var cut = OpenPage();
        cut.Find("[data-testid=pq-tab-data]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pq-scope]").TextContent.Should().Contain("this page").And.Contain("2 matching"));
        cut.FindAll("[data-testid=pq-row]")[1].QuerySelector(".pq-null")!.TextContent.Should().Be("null");
    }

    [Fact]
    public void Validation_shows_outcome_scope_and_failing_row_numbers_not_values()
    {
        var cut = OpenPage();
        cut.Find("[data-testid=pq-tab-validation]").Click();
        cut.Find("[data-testid=pq-rule-column]").Change("name");
        cut.Find("[data-testid=pq-rule-add]").Click();
        cut.Find("[data-testid=pq-validate]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pq-result]").GetAttribute("data-outcome").Should().Be("Fail"));
        cut.Find("[data-testid=pq-result]").TextContent.Should().Contain("Full scan").And.Contain("17, 34").And.NotContain("Synthetic child");
        _api.Verify(a => a.ValidateAsync(Session, It.Is<ParquetRuleSet>(r => r.Rules.Single().Kind == ParquetRuleKind.NotNull && r.Rules[0].Column == "name"), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public void Compare_shows_schema_changes_and_record_counts_by_key()
    {
        var cut = OpenPage();
        cut.FindComponents<InputFile>()[1].UploadFiles(File("children_v2.parquet"));
        cut.WaitForAssertion(() => cut.Find("[data-testid=pq-compare-run]"));
        cut.Find("[data-testid=pq-compare-key]").Change("id");
        cut.Find("[data-testid=pq-compare-run]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pq-schema-changes]").TextContent.Should().Contain("school").And.Contain("Added").And.Contain("flag"));
        cut.Find("[data-testid=pq-records-changed]").TextContent.Should().Contain("19");
        cut.Find("[data-testid=pq-record]").TextContent.Should().Contain("51").And.Contain("age, status");
        _api.Verify(a => a.CompareAsync(It.Is<ParquetCompareRequest>(r => r.BaselineSessionId == Baseline && r.CurrentSessionId == Session && r.KeyColumn == "id"), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public void The_comparison_direction_is_stated_and_can_be_swapped()
    {
        var cut = OpenPage();
        cut.FindComponents<InputFile>()[1].UploadFiles(File("children_v2.parquet"));
        cut.WaitForAssertion(() => cut.Find("[data-testid=pq-compare-direction]").TextContent.Should().StartWith("Baseline: children_v2.parquet"));
        cut.Find("[data-testid=pq-compare-swap]").Click();
        cut.Find("[data-testid=pq-compare-direction]").TextContent.Should().StartWith("Baseline: children_v1.parquet");
        cut.Find("[data-testid=pq-compare-run]").Click();
        _api.Verify(a => a.CompareAsync(It.Is<ParquetCompareRequest>(r => r.BaselineSessionId == Session && r.CurrentSessionId == Baseline), It.IsAny<CancellationToken>()));
    }
}
