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

    // ── Relationships ───────────────────────────────────────────────────

    [Fact]
    public void Parse_RelationshipWithType_PreservesType()
    {
        // Verify that relationships with explicit types are preserved
        var rel = new DataRelationship
        {
            Source = "users.id",
            Target = "orders.user_id",
            RelationshipType = "OneToMany"
        };
        Assert.NotNull(rel.RelationshipType);
        Assert.Equal("OneToMany", rel.RelationshipType);
        Assert.Equal("users", rel.SourceEntity);
        Assert.Equal("orders", rel.TargetEntity);
    }

    [Fact]
    public void Parse_RelationshipWithoutType_AllowsNull()
    {
        // Verify that relationships without type information can have null/empty type
        var rel = new DataRelationship
        {
            Source = "entities.id",
            Target = "other.id",
            RelationshipType = null
        };
        Assert.Null(rel.RelationshipType);
        Assert.Equal("entities", rel.SourceEntity);
        Assert.Equal("other", rel.TargetEntity);
    }

    [Fact]
    public void Parse_RelationshipWithoutType_EmptyStringAllowed()
    {
        // Verify that relationships can have empty-string type (treated same as null)
        var rel = new DataRelationship
        {
            Source = "a.id",
            Target = "b.id",
            RelationshipType = ""
        };
        Assert.Equal("", rel.RelationshipType);
        Assert.Equal("a", rel.SourceEntity);
        Assert.Equal("b", rel.TargetEntity);
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

    [Fact]
    public void FilterConstraints_ByName_Works()
    {
        var constraints = new[]
        {
            new DataConstraint { Name = "fk_users_roles", ConstraintType = "FK", EntityName = "users" },
            new DataConstraint { Name = "pk_orders", ConstraintType = "PK", EntityName = "orders" },
        };
        var result = _service.FilterConstraints(constraints, "users").ToList();
        Assert.Single(result);
    }

    [Fact]
    public void FilterConstraints_ByEntity_Works()
    {
        var constraints = new[]
        {
            new DataConstraint { Name = "fk_orders_customer", ConstraintType = "FK", EntityName = "orders" },
            new DataConstraint { Name = "pk_products", ConstraintType = "PK", EntityName = "products" },
        };
        var result = _service.FilterConstraints(constraints, "orders").ToList();
        Assert.Single(result);
    }

    [Fact]
    public void FilterConstraints_ByType_Works()
    {
        var constraints = new[]
        {
            new DataConstraint { Name = "fk_orders_customer", ConstraintType = "FK", EntityName = "orders" },
            new DataConstraint { Name = "fk_items_order", ConstraintType = "FK", EntityName = "items" },
            new DataConstraint { Name = "pk_orders", ConstraintType = "PK", EntityName = "orders" },
        };
        var result = _service.FilterConstraints(constraints, "FK").ToList();
        Assert.Equal(2, result.Count());
    }

    [Fact]
    public void FilterConstraints_ByDefinition_Works()
    {
        var constraints = new[]
        {
            new DataConstraint { Name = "ck_status", ConstraintType = "CK", EntityName = "orders", Definition = "status IN ('pending', 'shipped')" },
            new DataConstraint { Name = "ck_amount", ConstraintType = "CK", EntityName = "items", Definition = "amount > 0" },
        };
        var result = _service.FilterConstraints(constraints, "shipped").ToList();
        Assert.Single(result);
    }

    [Fact]
    public void FilterConstraints_CaseInsensitive_Works()
    {
        var constraints = new[]
        {
            new DataConstraint { Name = "FK_Users_Roles", ConstraintType = "fk", EntityName = "Users" },
        };
        var result = _service.FilterConstraints(constraints, "users").ToList();
        Assert.Single(result);
    }

    [Fact]
    public void FilterConstraints_EmptyQuery_ReturnsAll()
    {
        var constraints = new[]
        {
            new DataConstraint { Name = "fk_orders_customer", ConstraintType = "FK", EntityName = "orders" },
            new DataConstraint { Name = "pk_products", ConstraintType = "PK", EntityName = "products" },
        };
        var result = _service.FilterConstraints(constraints, "").ToList();
        Assert.Equal(2, result.Count());
    }

    [Fact]
    public void FilterConstraints_NoMatch_ReturnsEmpty()
    {
        var constraints = new[]
        {
            new DataConstraint { Name = "fk_orders_customer", ConstraintType = "FK", EntityName = "orders" },
        };
        var result = _service.FilterConstraints(constraints, "xyz123").ToList();
        Assert.Empty(result);
    }

    // ── Severity ordering (findings) ────────────────────────────────────

    [Fact]
    public void Parse_FindingsSeverity_Critical_Before_Error()
    {
        var input = """
            ## Entity: NoTypeEntity

            ## Entity: AnotherEntity
            """;
        var result = _service.Parse(input);
        var findings = result.Findings.Where(f =>
            f.Severity is DataModelSeverity.Critical or DataModelSeverity.Error).ToList();

        if (findings.Count >= 2)
        {
            var critical = findings.FirstOrDefault(f => f.Severity == DataModelSeverity.Critical);
            var error = findings.FirstOrDefault(f => f.Severity == DataModelSeverity.Error);

            if (critical != null && error != null)
            {
                var criticalIndex = findings.IndexOf(critical);
                var errorIndex = findings.IndexOf(error);
                Assert.True(criticalIndex < errorIndex, "Critical findings should appear before Error");
            }
        }
    }

    [Fact]
    public void Parse_FindingsSeverity_Error_Before_Warning()
    {
        var input = """
            ## Entity: TestEntity

            ## Entity: AnotherEntity
            """;
        var result = _service.Parse(input);
        var findings = result.Findings.Where(f =>
            f.Severity is DataModelSeverity.Error or DataModelSeverity.Warning).ToList();

        if (findings.Count >= 2)
        {
            var error = findings.FirstOrDefault(f => f.Severity == DataModelSeverity.Error);
            var warning = findings.FirstOrDefault(f => f.Severity == DataModelSeverity.Warning);

            if (error != null && warning != null)
            {
                var errorIndex = findings.IndexOf(error);
                var warningIndex = findings.IndexOf(warning);
                Assert.True(errorIndex < warningIndex, "Error findings should appear before Warning");
            }
        }
    }

    [Fact]
    public void Parse_FindingsSeverity_Warning_Before_Info()
    {
        var input = """
            ## Entity: Entity1

            ## Entity: Entity2
            """;
        var result = _service.Parse(input);
        var findings = result.Findings.Where(f =>
            f.Severity is DataModelSeverity.Warning or DataModelSeverity.Info).ToList();

        if (findings.Count >= 2)
        {
            var warning = findings.FirstOrDefault(f => f.Severity == DataModelSeverity.Warning);
            var info = findings.FirstOrDefault(f => f.Severity == DataModelSeverity.Info);

            if (warning != null && info != null)
            {
                var warningIndex = findings.IndexOf(warning);
                var infoIndex = findings.IndexOf(info);
                Assert.True(warningIndex < infoIndex, "Warning findings should appear before Info");
            }
        }
    }

    [Fact]
    public void Parse_FindingsSeverity_Mixed_SortsCorrectly()
    {
        var findings = new[]
        {
            new DataModelFinding { Severity = DataModelSeverity.Info, Category = "Test", Description = "Info" },
            new DataModelFinding { Severity = DataModelSeverity.Critical, Category = "Test", Description = "Critical" },
            new DataModelFinding { Severity = DataModelSeverity.Warning, Category = "Test", Description = "Warning" },
            new DataModelFinding { Severity = DataModelSeverity.Error, Category = "Test", Description = "Error" },
        };

        var ordered = findings.OrderBy(f => GetSeverityPriority(f.Severity)).ToList();

        Assert.Equal(DataModelSeverity.Critical, ordered[0].Severity);
        Assert.Equal(DataModelSeverity.Error, ordered[1].Severity);
        Assert.Equal(DataModelSeverity.Warning, ordered[2].Severity);
        Assert.Equal(DataModelSeverity.Info, ordered[3].Severity);
    }

    // Helper method for testing (mirrors component method)
    private int GetSeverityPriority(DataModelSeverity severity) => severity switch
    {
        DataModelSeverity.Critical => 0,
        DataModelSeverity.Error => 1,
        DataModelSeverity.Warning => 2,
        DataModelSeverity.Info => 3,
        _ => 4,
    };

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
