using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Tests for table cross-reference correctness in Specification Explorer.
/// Verifies matching, deduplication, and reference semantics.
/// </summary>
public sealed class SpecExplorerTableRefTests
{
    [Fact]
    public void ParseTable_ExtractsSpecRefsFromAllCells()
    {
        const string spec = @"
# Requirements
- FR-001: First requirement
- FR-002: Second requirement

| Feature | Reference |
|---------|-----------|
| Login | FR-001 |
| Logout | FR-002 |
";

        var tree = SpecExplorerService.Parse(spec);

        // Find table rows
        var tableRows = GetAllNodes(tree.Roots)
            .Where(n => n.NodeType == SpecNodeType.TableRow)
            .ToList();

        tableRows.Should().HaveCount(2, "Should have 2 table rows");

        // First row should reference FR-001
        tableRows[0].LinkedSpecItemIds.Should().Contain("FR-001");

        // Second row should reference FR-002
        tableRows[1].LinkedSpecItemIds.Should().Contain("FR-002");
    }

    [Fact]
    public void ExtractSpecRefs_NormalizesNumbersToThreeDigits()
    {
        const string spec = @"
# Requirements
- FR-1: One digit
- FR-10: Two digits
- FR-100: Three digits
- FR-1000: Four digits

| Item | Refs |
|------|------|
| A | FR-1 |
| B | FR-10 |
| C | FR-100 |
| D | FR-1000 |
";

        var tree = SpecExplorerService.Parse(spec);
        var tableRows = GetAllNodes(tree.Roots)
            .Where(n => n.NodeType == SpecNodeType.TableRow)
            .ToList();

        tableRows[0].LinkedSpecItemIds.Should().Contain("FR-001");
        tableRows[1].LinkedSpecItemIds.Should().Contain("FR-010");
        tableRows[2].LinkedSpecItemIds.Should().Contain("FR-100");
        tableRows[3].LinkedSpecItemIds.Should().Contain("FR-1000");
    }

    [Fact]
    public void ExtractSpecRefs_DeduplicatesWithinSameRow()
    {
        const string spec = @"
# Requirements
- FR-001: Test

| Item | References |
|------|------------|
| A | FR-001 FR-001 FR-001 |
";

        var tree = SpecExplorerService.Parse(spec);
        var tableRow = GetAllNodes(tree.Roots)
            .First(n => n.NodeType == SpecNodeType.TableRow);

        // Should only have one instance of FR-001, not three
        tableRow.LinkedSpecItemIds.Should().HaveCount(1);
        tableRow.LinkedSpecItemIds.Should().Contain("FR-001");
    }

    [Fact]
    public void ExtractSpecRefs_SupportsCaseInsensitiveExtraction()
    {
        const string spec = @"
# Requirements
- FR-001: Test

| Item | References |
|------|------------|
| A | fr-001 |
| B | FR-001 |
| C | Fr-001 |
";

        var tree = SpecExplorerService.Parse(spec);
        var tableRows = GetAllNodes(tree.Roots)
            .Where(n => n.NodeType == SpecNodeType.TableRow)
            .ToList();

        // All rows should reference FR-001 (normalized to uppercase)
        foreach (var row in tableRows)
        {
            row.LinkedSpecItemIds.Should().Contain("FR-001");
        }
    }

    [Fact]
    public void ExtractSpecRefs_SupportsMultipleRefFormats()
    {
        const string spec = @"
# Requirements
- FR-001: Functional
- NFR-001: Non-functional
- SC-001: Success Criterion
- US-001: User Story
- REQ-001: Generic requirement

| Item | Refs |
|------|------|
| A | FR-001 NFR-001 SC-001 US-001 REQ-001 |
";

        var tree = SpecExplorerService.Parse(spec);
        var tableRow = GetAllNodes(tree.Roots)
            .First(n => n.NodeType == SpecNodeType.TableRow);

        tableRow.LinkedSpecItemIds.Should().Contain("FR-001");
        tableRow.LinkedSpecItemIds.Should().Contain("NFR-001");
        tableRow.LinkedSpecItemIds.Should().Contain("SC-001");
        tableRow.LinkedSpecItemIds.Should().Contain("US-001");
        tableRow.LinkedSpecItemIds.Should().Contain("REQ-001");
    }

    [Fact]
    public void ExtractSpecRefs_HandlesCommaSeparatedRefs()
    {
        const string spec = @"
# Requirements
- FR-001: First
- FR-002: Second

| Item | References |
|------|------------|
| A | FR-001, FR-002 |
";

        var tree = SpecExplorerService.Parse(spec);
        var tableRow = GetAllNodes(tree.Roots)
            .First(n => n.NodeType == SpecNodeType.TableRow);

        // Comma-separated refs should both be extracted
        tableRow.LinkedSpecItemIds.Should().Contain("FR-001");
        tableRow.LinkedSpecItemIds.Should().Contain("FR-002");
    }

    [Fact]
    public void ExactMatching_DoesNotMatchPartialIds()
    {
        const string spec = @"
# Requirements
- FR-001: Item one
- FR-010: Item ten

| Item | Reference |
|------|-----------|
| A | FR-001 |
| B | FR-010 |
";

        var tree = SpecExplorerService.Parse(spec);
        var tableRows = GetAllNodes(tree.Roots)
            .Where(n => n.NodeType == SpecNodeType.TableRow)
            .ToList();

        // Row A should only have FR-001, not FR-010
        var rowA = tableRows.First(r => r.LinkedSpecItemIds.Contains("FR-001"));
        rowA.LinkedSpecItemIds.Should().Contain("FR-001");
        rowA.LinkedSpecItemIds.Should().NotContain("FR-010");

        // Row B should only have FR-010, not FR-001
        var rowB = tableRows.First(r => r.LinkedSpecItemIds.Contains("FR-010"));
        rowB.LinkedSpecItemIds.Should().Contain("FR-010");
        rowB.LinkedSpecItemIds.Should().NotContain("FR-001");
    }

    [Fact]
    public void TableParsing_EmptyTableWithNoRefs_CreatesNodes()
    {
        const string spec = @"
# Data

| Column1 | Column2 |
|---------|---------|
| Value1 | Value2 |
";

        var tree = SpecExplorerService.Parse(spec);
        var tableRow = GetAllNodes(tree.Roots)
            .FirstOrDefault(n => n.NodeType == SpecNodeType.TableRow);

        tableRow.Should().NotBeNull("Table row should exist");
        tableRow!.LinkedSpecItemIds.Should().BeEmpty("Row with no spec refs should have empty list");
    }

    [Fact]
    public void MultipleTablesWithSameRef_AllRowsAreIncluded()
    {
        const string spec = @"
# Requirements
- FR-001: Shared requirement

## Table One
| Feature | Link |
|---------|------|
| LoginForm | FR-001 |

## Table Two
| Component | Link |
|-----------|------|
| AuthService | FR-001 |
";

        var tree = SpecExplorerService.Parse(spec);
        var tableRows = GetAllNodes(tree.Roots)
            .Where(n => n.NodeType == SpecNodeType.TableRow)
            .ToList();

        // Both rows should reference FR-001
        var rowsWithFr001 = tableRows.Where(r => r.LinkedSpecItemIds.Contains("FR-001")).ToList();
        rowsWithFr001.Should().HaveCount(2, "FR-001 should be referenced in both tables");
    }

    private static List<SpecNode> GetAllNodes(IEnumerable<SpecNode> nodes)
    {
        var result = new List<SpecNode>();
        foreach (var node in nodes)
        {
            result.Add(node);
            result.AddRange(GetAllNodes(node.Children));
        }
        return result;
    }
}
