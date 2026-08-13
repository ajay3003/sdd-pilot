using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Xunit;

namespace BirkNext.Web.Tests.Services;

/// Minimal regression tests for Data Model parser
/// Focuses on actual supported syntax from real data-model.md files
public class DataModelParserRegressionTests
{
    private readonly DataModelAnalysisService _service = new();

    // ── Baseline: empty/minimal input ──────────────────────────────────

    [Fact]
    public void Parse_EmptyInput_ReturnsEmptyDocument()
    {
        var result = _service.Parse("");
        Assert.NotNull(result);
        Assert.Empty(result.Entities);
    }

    [Fact]
    public void Parse_OnlyTitle_ExtractsTitle()
    {
        var result = _service.Parse("# My Data Model");
        Assert.Equal("My Data Model", result.Title);
    }

    // ── Entities and tables ────────────────────────────────────────────

    [Fact]
    public void Parse_SimpleEntity_CreatedFromH2()
    {
        var result = _service.Parse("## Entity: User");
        Assert.Single(result.Entities);
        Assert.Equal("User", result.Entities[0].Name);
        Assert.False(result.Entities[0].IsTable);
    }

    [Fact]
    public void Parse_SimpleTable_CreatedFromH2()
    {
        var result = _service.Parse("## Table: users");
        Assert.Single(result.Entities);
        Assert.Equal("users", result.Entities[0].Name);
        Assert.True(result.Entities[0].IsTable);
    }

    // ── Enums ──────────────────────────────────────────────────────────

    [Fact]
    public void Parse_EnumWithValues_Extracted()
    {
        var input = """
            ## Enum: Status

            - active
            - inactive
            """;
        var result = _service.Parse(input);
        Assert.Single(result.Enums);
        Assert.Equal("Status", result.Enums[0].Name);
        Assert.Equal(2, result.Enums[0].Values.Count);
    }

    // ── Findings: core rules ───────────────────────────────────────────

    [Fact]
    public void Parse_NoEntities_GeneratesInfoFinding()
    {
        var result = _service.Parse("## Overview\n\nSome text");
        Assert.NotEmpty(result.Findings);
        Assert.True(result.Findings.Any(f =>
            f.Severity == DataModelSeverity.Info &&
            f.Description.Contains("persistent entities")));
    }

    // ── Sensitivity ────────────────────────────────────────────────────

    [Fact]
    public void IsSensitiveColumn_PasswordField_Detected()
    {
        var col = new DataColumn { Name = "password" };
        Assert.True(_service.IsSensitiveColumn(col));
    }

    [Fact]
    public void IsSensitiveColumn_EmailField_Detected()
    {
        var col = new DataColumn { Name = "email" };
        Assert.True(_service.IsSensitiveColumn(col));
    }

    [Fact]
    public void IsSensitiveColumn_NormalField_NotDetected()
    {
        var col = new DataColumn { Name = "user_name" };
        Assert.False(_service.IsSensitiveColumn(col));
    }

    // ── Filtering ──────────────────────────────────────────────────────

    [Fact]
    public void FilterEntities_ByName_Works()
    {
        var entities = new[]
        {
            new DataEntity { Name = "Users" },
            new DataEntity { Name = "Orders" },
        };
        var result = _service.FilterEntities(entities, "user").ToList();
        Assert.Single(result);
    }

    [Fact]
    public void FilterRelationships_BySource_Works()
    {
        var rels = new[]
        {
            new DataRelationship { Source = "users.id", Target = "orders.user_id" },
            new DataRelationship { Source = "orders.id", Target = "items.order_id" },
        };
        var result = _service.FilterRelationships(rels, "users").ToList();
        Assert.Single(result);
    }

    [Fact]
    public void FilterIndexes_ByName_Works()
    {
        var indexes = new[]
        {
            new DataIndex { Name = "ix_users_email" },
            new DataIndex { Name = "ix_orders_date" },
        };
        var result = _service.FilterIndexes(indexes, "email").ToList();
        Assert.Single(result);
    }

    // ── Code block safety ──────────────────────────────────────────────

    [Fact]
    public void Parse_CodeBlockContent_NotParsedAsEntity()
    {
        var input = """
            ## Table: real_table

            ```sql
            CREATE TABLE fake_table (id INT);
            ```
            """;
        var result = _service.Parse(input);
        Assert.Single(result.Entities);
        Assert.Equal("real_table", result.Entities[0].Name);
    }

    // ── Real fixture 001: create-scenario ──────────────────────────────

    [Fact]
    public void Parse_Spec001_ScenariosTable_Baseline()
    {
        var input = """
            ## Table: scenarios

            | id | title | created_at |
            |----|-------|-----------|
            """;

        var result = _service.Parse(input);
        Assert.Single(result.Entities);
        var entity = result.Entities[0];
        Assert.Equal("scenarios", entity.Name);
        Assert.True(entity.IsTable);
    }
}
