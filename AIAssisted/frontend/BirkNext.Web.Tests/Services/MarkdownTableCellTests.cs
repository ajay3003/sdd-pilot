using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Regression (Playwright regression recovery, task 7): table rows dropped every empty cell, so one empty cell shifted every later cell one
/// column to the left — an empty Type made the Nullable value the type and the description the nullability.
/// </summary>
public sealed class MarkdownTableCellTests
{
    [Theory]
    [InlineData("| phone |  | yes | Contact phone |", new[] { "phone", "", "yes", "Contact phone" })]
    [InlineData("| a | b |", new[] { "a", "b" })]
    [InlineData("| a | | c |", new[] { "a", "", "c" })]
    [InlineData("|  | x |", new[] { "", "x" })]
    public void Empty_cells_inside_a_row_keep_their_column(string row, string[] expected)
    {
        var token = MarkdownTokenizer.Tokenize(row).Single(t => t.Kind == MarkdownTokenKind.TableRow);
        token.TableCells.Should().Equal(expected);
    }

    [Fact]
    public void A_column_without_a_type_is_reported_and_its_nullability_is_read_from_its_own_column()
    {
        var document = new DataModelAnalysisService().Parse("""
            # Data Model

            ## Table: member

            | Column | Type | Nullable | Description |
            |--------|------|----------|-------------|
            | id | uuid | no | Primary key |
            | phone |  | yes | Contact phone |
            """);

        var phone = document.Entities.Single(e => e.Name == "member").Columns.Single(c => c.Name == "phone");
        phone.Type.Should().BeNull("the Type cell is empty");
        phone.Nullable.Should().BeTrue("the Nullable column says yes");
        document.Findings.Should().Contain(f => f.Description == "Column \"phone\" is missing a type.");
        document.Findings.Should().NotContain(f => f.Description.Contains("\"phone\" does not specify nullable"));
    }
}
