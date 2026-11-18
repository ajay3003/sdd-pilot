using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// The synthetic data model the browser Data Model Quality scenario imports (Playwright <c>SyntheticProjects.SpecificationArchive</c>) yields
/// exactly these findings from the shipped rules. The browser test asserts the same list, so a rule change shows up here first, with the
/// rule that moved, rather than as a bare count mismatch in a browser run.
/// </summary>
public sealed class DataModelSyntheticFindingsTests
{
    private const string Model = """
        # Data Model: Library loans

        ## Table: member

        | Column | Type | Nullable | Description |
        |--------|------|----------|-------------|
        | id | uuid | no | Primary key |
        | name |  | no | Display name |
        | email | string | no | Contact address |
        | phone |  | yes | Contact phone |
        | card_number |  | no | Library card |

        ## Table: loan

        | Column | Type | Nullable | Description |
        |--------|------|----------|-------------|
        | id | uuid | no | Primary key |
        | member_id | uuid | no | Borrowing member |
        | book_number |  | no | Catalogue number |
        | due_date |  | no | Return date |
        """;

    [Fact]
    public void TheSyntheticModel_YieldsExactlyTheKnownFindings()
    {
        var findings = new DataModelAnalysisService().Parse(Model).Findings.Select(f => $"{f.EntityName}|{f.Description}").ToList();
        findings.Should().BeEquivalentTo(
        [
            "member|Column \"name\" is missing a type.",
            "member|Column \"email\" appears to contain sensitive data but has no classification annotation.",
            "member|Column \"phone\" is missing a type.",
            "member|Column \"phone\" appears to contain sensitive data but has no classification annotation.",
            "member|Column \"card_number\" is missing a type.",
            "member|Column \"card_number\" appears to contain sensitive data but has no classification annotation.",
            "loan|Column \"member_id\" looks like a foreign key but no relationship is defined for it.",
            "loan|Column \"book_number\" is missing a type.",
            "loan|Column \"due_date\" is missing a type.",
        ]);
    }

    /// <summary>Defect 2026-11-18: a data row whose first cell is name, field, column or property was skipped as a repeated header row.</summary>
    [Theory]
    [InlineData("name")]
    [InlineData("field")]
    [InlineData("column")]
    [InlineData("property")]
    public void AColumnNamedLikeAHeaderWord_IsKept(string column)
    {
        var doc = new DataModelAnalysisService().Parse($"""
            # Data Model

            ## Table: item

            | Column | Type | Nullable | Description |
            |--------|------|----------|-------------|
            | id | uuid | no | Primary key |
            | {column} | text | no | A value |
            """);
        doc.Entities.Single().Columns.Select(c => c.Name).Should().Equal("id", column);
    }

    [Fact]
    public void ARepeatedHeaderInsideTheTable_IsStillSkipped()
    {
        var doc = new DataModelAnalysisService().Parse("""
            # Data Model

            ## Table: item

            | Name | Type | Description |
            |------|------|-------------|
            | id | uuid | Primary key |
            | Name | Type | Description |
            | title | text | Title |
            """);
        doc.Entities.Single().Columns.Select(c => c.Name).Should().Equal("id", "title");
    }
}
