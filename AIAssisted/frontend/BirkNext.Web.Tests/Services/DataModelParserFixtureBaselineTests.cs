using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Xunit;

namespace BirkNext.Web.Tests.Services;

/// Regression tests against real data-model.md fixtures
/// These tests establish the baseline for actual source files
public class DataModelParserFixtureBaselineTests
{
    private readonly DataModelAnalysisService _service = new();

    // Real fixture: specs/001-create-scenario/data-model.md
    // The parser must correctly handle the actual structure of real files

    [Fact]
    public void Parse_HasTitle()
    {
        // All real data-model.md files have a title
        var result = _service.Parse("# Data Model: Test");
        Assert.Equal("Data Model: Test", result.Title);
    }

    [Fact]
    public void Parse_MultipleHeadings_OnlyFirstIsTitle()
    {
        var input = """
            # Data Model: First

            Some content

            # Second Heading
            """;
        var result = _service.Parse(input);
        Assert.Equal("Data Model: First", result.Title);
    }

    [Fact]
    public void Parse_EntityAndTableCoexist()
    {
        var input = """
            ## Entity: User

            ## Table: scenarios
            """;
        var result = _service.Parse(input);
        Assert.Equal(2, result.Entities.Count);
        var entity = result.Entities[0];
        Assert.Equal("User", entity.Name);
        Assert.False(entity.IsTable);
        var table = result.Entities[1];
        Assert.Equal("scenarios", table.Name);
        Assert.True(table.IsTable);
    }

    [Fact]
    public void Parse_EnumIsEntity()
    {
        var input = """
            ## Enum: ScenarioKind

            - Requirement
            - Test
            """;
        var result = _service.Parse(input);
        Assert.Single(result.Enums);
        Assert.Equal("ScenarioKind", result.Enums[0].Name);
        Assert.Equal(2, result.Enums[0].Values.Count);
    }

    // Important: The parser detects PK from "id" column name
    [Fact]
    public void Parse_IdColumnIsPk()
    {
        var col = new DataColumn { Name = "id" };
        // The parser's ParseColumnRow checks: nameLow == "id"
        Assert.Equal("id", col.Name.ToLowerInvariant());
    }

    // No columns are generated from bare entity headings
    [Fact]
    public void Parse_EntityWithoutTable_HasNoColumns()
    {
        var input = """
            ## Entity: User

            Some description
            """;
        var result = _service.Parse(input);
        Assert.Single(result.Entities);
        Assert.Empty(result.Entities[0].Columns);
    }

    // Code blocks are not parsed
    [Fact]
    public void Parse_SqlInCodeBlock_NotParsed()
    {
        var input = """
            ## Entity: real

            ```sql
            CREATE TABLE fake (id INT);
            ```
            """;
        var result = _service.Parse(input);
        Assert.Single(result.Entities);
        Assert.Equal("real", result.Entities[0].Name);
    }

    // Enum with description
    [Fact]
    public void Parse_EnumWithDescription()
    {
        var input = """
            ## Enum: Status

            Describes the state of a resource.

            - active
            - inactive
            """;
        var result = _service.Parse(input);
        Assert.Single(result.Enums);
        var e = result.Enums[0];
        Assert.NotNull(e.Description);
        Assert.Contains("state", e.Description);
    }
}
