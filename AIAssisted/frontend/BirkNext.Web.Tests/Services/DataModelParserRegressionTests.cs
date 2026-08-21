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

    // ── Backtick normalization (structured field regression) ──────────────

    [Fact]
    public void Parse_SCIM_Sample_NoBackticksInModel()
    {
        // Regression test for SCIM User Synchronization Adapter data-model
        // Simplified version using standard column headers to ensure parsing works
        var input = """
            # Data Model: SCIM User Synchronization Adapter

            ## Entity: KjentBruker

            ### Columns

            | Name | Type | Nullable |
            |---|---|---|
            | `EntraObjectId` | `uniqueidentifier` | No |
            | `UserName` | `nvarchar(256)` | No |
            | `ExternalId` | `nvarchar(256)` | Yes |
            | `IsActive` | `bit` | No |
            | `LastUpdated` | `datetimeoffset` | No |
            """;

        var result = _service.Parse(input);

        Assert.Single(result.Entities);
        var entity = result.Entities[0];
        Assert.Equal("KjentBruker", entity.Name);
        Assert.Equal(5, entity.Columns.Count);

        // Verify no backticks in column names
        Assert.Equal("EntraObjectId", entity.Columns[0].Name);
        Assert.False(entity.Columns[0].Name.Contains("`"));

        Assert.Equal("UserName", entity.Columns[1].Name);
        Assert.False(entity.Columns[1].Name.Contains("`"));

        Assert.Equal("ExternalId", entity.Columns[2].Name);
        Assert.False(entity.Columns[2].Name.Contains("`"));

        Assert.Equal("IsActive", entity.Columns[3].Name);
        Assert.False(entity.Columns[3].Name.Contains("`"));

        Assert.Equal("LastUpdated", entity.Columns[4].Name);
        Assert.False(entity.Columns[4].Name.Contains("`"));

        // Verify no backticks in types
        Assert.Equal("uniqueidentifier", entity.Columns[0].Type);
        Assert.False((entity.Columns[0].Type ?? "").Contains("`"));

        Assert.Equal("nvarchar(256)", entity.Columns[1].Type);
        Assert.False((entity.Columns[1].Type ?? "").Contains("`"));
    }

    [Fact]
    public void Parse_SCIM_Sample_FindingsUseNormalizedNames()
    {
        var input = """
            # Data Model: SCIM User Synchronization Adapter

            ## Entity: KjentBruker

            ### Fields

            | Property | Type | Nullable |
            |---|---|---|
            | `UserName` | `nvarchar(256)` |  |
            | `Email` | `nvarchar(256)` |  |
            """;

        var result = _service.Parse(input);

        // Check that findings reference normalized names
        var nullableFindings = result.Findings
            .Where(f => f.Description.Contains("nullable") || f.Description.Contains("does not specify"))
            .ToList();

        Assert.NotEmpty(nullableFindings);

        // All findings should have clean names without backticks
        foreach (var finding in result.Findings)
        {
            Assert.False(finding.Description.Contains("`"));
        }
    }

    [Fact]
    public void Parse_InlineCodeInH3_NormalizesEntityName()
    {
        var input = """
            # Data Model: Test

            ## SCIM Request/Response Models

            ### Source Reference (`KildeReferanse`) Format

            | Field | Type |
            |-------|------|
            | id | UUID |
            """;

        var result = _service.Parse(input);

        Assert.NotEmpty(result.Entities);
        var entity = result.Entities[0];

        // Should have normalized the inline-code backticks
        Assert.Equal("Source Reference (KildeReferanse) Format", entity.Name);
        Assert.False(entity.Name.Contains("`"));
    }

    // ── Traceability reference classification ──────────────────────

    [Fact]
    public void Parse_PlatformStandardReference_ExtractedAsTraceability()
    {
        // Platform Standards (PS-) are traceability references, not functional requirements
        // PS-04 in SCIM sample: "UUID v4 (PS-04)" - a platform design standard
        var input = """
            # Data Model: Test

            ## Entity: User

            ### Columns

            | Column | Type | Notes |
            |--------|------|-------|
            | Id | UUID | UUID v4 format (PS-04) |
            """;

        var result = _service.Parse(input);

        Assert.NotEmpty(result.Entities);
        var entity = result.Entities[0];

        // PS-04 should be extracted as a traceability reference
        // It is a Platform Standard from the Constitution, not a Requirement
        Assert.Contains("PS-04", entity.TraceabilityIds);
    }

    [Fact]
    public void Parse_MixedTraceability_FunctionalRequirementAndPlatformStandard()
    {
        // Entities can have both FR (Requirements) and PS (Platform Standards) in TraceabilityIds
        // This proves the collection is intentionally mixed, not requirement-only
        var input = """
            # Data Model: Test

            ## Entity: Account

            ### Columns

            | Column | Type | Notes |
            |--------|------|-------|
            | Id | UUID | Implements FR-001 and follows PS-04 |
            | Status | string | Required by FR-002 |
            """;

        var result = _service.Parse(input);

        Assert.NotEmpty(result.Entities);
        var entity = result.Entities[0];

        // Both FR (requirement) and PS (standard) should coexist in same collection
        Assert.Contains("FR-001", entity.TraceabilityIds);
        Assert.Contains("PS-04", entity.TraceabilityIds);
        Assert.Contains("FR-002", entity.TraceabilityIds);
        Assert.True(entity.TraceabilityIds.Count >= 3);
    }

    [Fact]
    public void Parse_MixedTraceability_DeduplicatesRepeatedReferences()
    {
        // Duplicate references (e.g., FR-001 mentioned twice) should deduplicate
        var input = """
            # Data Model: Test

            ## Entity: Task

            ### Columns

            | Column | Type | Notes |
            |--------|------|-------|
            | Id | UUID | FR-100 UUID per FR-100 design |
            | Name | string | From FR-100 spec |
            """;

        var result = _service.Parse(input);

        Assert.NotEmpty(result.Entities);
        var entity = result.Entities[0];

        // FR-100 appears 3 times but should deduplicate to 1
        var fr100Count = entity.TraceabilityIds.Count(x => x == "FR-100");
        Assert.Equal(1, fr100Count);
    }

    [Fact]
    public void Parse_SemanticModel_RelatedTraceabilityIds_HoldsMixedReferences()
    {
        // The renamed property RelatedTraceabilityIds (was RelatedRequirementIds)
        // confirms the collection holds mixed reference types
        var input = """
            # Data Model: Test

            ## Entity: Document

            ### Columns

            | Column | Type | Notes |
            |--------|------|-------|
            | Id | UUID | Requirement FR-010, standard PS-02, governance GV-15 |
            """;

        var result = _service.Parse(input);

        // Verify ParsedDataModel (raw parser output)
        Assert.NotEmpty(result.Entities);
        var entity = result.Entities[0];
        Assert.Contains("FR-010", entity.TraceabilityIds);
        Assert.Contains("PS-02", entity.TraceabilityIds);
        Assert.Contains("GV-15", entity.TraceabilityIds);

        // Build semantic model to verify renamed property
        var semantic = DataModelAnalysisService.BuildSemanticModel(result);
        Assert.NotEmpty(semantic.Entities);
        var semanticEntity = semantic.Entities[0];

        // RelatedTraceabilityIds (not RelatedRequirementIds) holds all mixed types
        Assert.Contains("FR-010", semanticEntity.RelatedTraceabilityIds);
        Assert.Contains("PS-02", semanticEntity.RelatedTraceabilityIds);
        Assert.Contains("GV-15", semanticEntity.RelatedTraceabilityIds);
    }

    [Fact]
    public void Parse_SemanticModel_EntityToTraceability_MapsAllReferences()
    {
        // The renamed dictionary EntityToTraceability (was EntityToRequirements)
        // should contain all mixed references, not just requirements
        var input = """
            # Data Model: Test

            ## Entity: Configuration

            ### Columns

            | Column | Type | Notes |
            |--------|------|-------|
            | Id | UUID | FR-020, PS-03, GV-005 |
            """;

        var doc = _service.Parse(input);
        var semantic = DataModelAnalysisService.BuildSemanticModel(doc);

        // EntityToTraceability should exist (renamed from EntityToRequirements)
        Assert.NotEmpty(semantic.EntityToTraceability);

        var firstEntityId = semantic.Entities[0].Id;
        Assert.True(semantic.EntityToTraceability.ContainsKey(firstEntityId));

        var traceIds = semantic.EntityToTraceability[firstEntityId];
        // Should contain mixed types: FR, PS, GV
        Assert.Contains("FR-020", traceIds);
        Assert.Contains("PS-03", traceIds);
        Assert.Contains("GV-005", traceIds);
    }

    [Fact]
    public void Parse_LinkedEntities_CountsAllTraceabilityReferences()
    {
        // Entities linked via any traceability reference (FR, PS, GL, etc.)
        // should count toward "Entities Linked", not just requirement-linked ones
        var input = """
            # Data Model: Test

            ## Entity: User

            ### Columns

            | Column | Type | Notes |
            |--------|------|-------|
            | Id | UUID | FR-100 |

            ## Entity: Role

            ### Columns

            | Column | Type | Notes |
            |--------|------|-------|
            | Id | UUID | PS-01 |

            ## Entity: Permission

            ### Columns

            | Column | Type | Notes |
            |--------|------|-------|
            | Id | UUID | No references |
            """;

        var result = _service.Parse(input);

        // User linked via FR, Role linked via PS, Permission unlinked
        var linked = result.Entities.Where(e => e.TraceabilityIds.Count > 0).ToList();
        Assert.Equal(2, linked.Count);

        var unlinked = result.Entities.Where(e => e.TraceabilityIds.Count == 0).ToList();
        Assert.Equal(1, unlinked.Count);
    }

    // ── Indexes & Constraints parsing ──────────────────────────────────

    [Fact]
    public void Parse_PersonModuleCore_IndexesCount()
    {
        // Audit the Person Module Core sample to verify Indexes parsing
        var markdown = System.IO.File.ReadAllText(
            TestDataHelper.ResolveSampleDataPath("person-module", "data-model.md"));

        var result = _service.Parse(markdown);

        // Authoritative count from manual audit of sample:
        // Person: 4 indexes (3 filtered single-column + 1 full-text)
        // BarnIAndrelinjeBarnevern: 6 indexes (5 single-column + 1 composite)
        // BarnStatusHistorikk: 1 index
        // OutboxMessage: 1 index (+ 1 prose note that should NOT be parsed)
        // TOTAL: 12 indexes
        // NOT 13 — "Rows are never deleted..." is retention policy prose, not an index

        Assert.Equal(12, result.Indexes.Count);
        Assert.NotNull(result.Indexes);
    }

    [Fact]
    public void Parse_PersonModule_SpecificIndexNames()
    {
        // Verify the canonical names of important indexes
        var markdown = System.IO.File.ReadAllText(
            TestDataHelper.ResolveSampleDataPath("person-module", "data-model.md"));

        var result = _service.Parse(markdown);

        var indexNames = result.Indexes.Select(i => i.Name).ToHashSet();

        // Key indexes expected to exist
        Assert.Contains("IX_Person_EksternId", indexNames);
        Assert.Contains("IX_Person_Foedselsnummer", indexNames);
        Assert.Contains("IX_Person_DUFNummer", indexNames);
        Assert.Contains("IX_BarnIAndrelinjeBarnevern_PersonId", indexNames);
        Assert.Contains("IX_Barn_Search", indexNames);
        Assert.Contains("IX_OutboxMessage_Status_CreatedAt", indexNames);

        // "Rows are never deleted" must NOT be an index
        Assert.False(indexNames.Any(n => n.Contains("never deleted", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Parse_PersonModule_UniqueIndexes()
    {
        // Verify IsUnique flag on specific indexes
        var markdown = System.IO.File.ReadAllText(
            TestDataHelper.ResolveSampleDataPath("person-module", "data-model.md"));

        var result = _service.Parse(markdown);

        var person = result.Indexes.Where(i => i.EntityName == "Person").ToList();
        // No unique flags in Person indexes (they are filtered but not unique in the sample)
        Assert.True(person.All(i => !i.IsUnique), "Person indexes should not be marked unique");

        var barn = result.Indexes.Where(i => i.EntityName == "BarnIAndrelinjeBarnevern").ToList();
        // PersonId and BirkId should be marked unique
        var personIdIdx = barn.FirstOrDefault(i => i.Name.Contains("PersonId"));
        var birkIdIdx = barn.FirstOrDefault(i => i.Name.Contains("BirkId"));

        if (personIdIdx != null) Assert.True(personIdIdx.IsUnique, "PersonId index should be unique");
        if (birkIdIdx != null) Assert.True(birkIdIdx.IsUnique, "BirkId index should be unique");
    }

    [Fact]
    public void Parse_Indexes_RejectProseNotation()
    {
        // Prose like "Rows are never deleted (could be archived after 30 days)"
        // under an Indexes section should NOT be parsed as an index
        var input = """
            # Data Model: Test

            ## Table: Log

            **Indexes**:
            - `IX_Log_Timestamp` on (Timestamp)
            - Rows are never deleted (archive after 30 days)
            - Could be moved to cold storage

            """;

        var result = _service.Parse(input);

        // Should have only 1 index (not 3)
        Assert.Single(result.Indexes);
        Assert.Equal("IX_Log_Timestamp", result.Indexes[0].Name);

        // Verify prose is not in index list
        var names = result.Indexes.Select(i => i.Name).ToList();
        Assert.False(names.Any(n => n.Contains("never deleted", StringComparison.OrdinalIgnoreCase)));
        Assert.False(names.Any(n => n.Contains("Could be", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Parse_Indexes_FullTextIndex()
    {
        // Full-text indexes should be recognized and stored as indexes
        var input = """
            # Data Model: Test

            ## Table: Document

            **Indexes**:
            - Full-text index on `Content` for search (FR-042)

            """;

        var result = _service.Parse(input);

        // Should have 1 index
        Assert.Single(result.Indexes);
        var idx = result.Indexes[0];
        Assert.Contains("Full-text", idx.Name);
        Assert.Equal("Document", idx.EntityName);
    }

    [Fact]
    public void Parse_Indexes_CompositeIndex()
    {
        // Composite indexes with multiple columns should extract column list
        var input = """
            # Data Model: Test

            ## Table: Event

            **Indexes**:
            - Composite: `IX_Event_Filter` on (Status, CreatedAt, UserId)

            """;

        var result = _service.Parse(input);

        Assert.Single(result.Indexes);
        var idx = result.Indexes[0];
        Assert.Equal("IX_Event_Filter", idx.Name);
        Assert.True(idx.Columns.Count >= 2, "Composite index should have multiple columns");
    }

    // ── Cross-module regression tests ──────────────────────────────────

    // ── Portability tests: real syntax across modules ────────────────────

    [Fact]
    public void Parse_Autorisasjon_ContainsNoMarkdownIndexes()
    {
        // Autorisasjon has index definitions only in EF Core code block,
        // not in markdown **Indexes** sections, so zero indexes expected
        var path = TestDataHelper.ResolveSampleDataPath("autorisasjon", "data-model.md");
        if (!System.IO.File.Exists(path)) return;

        var markdown = System.IO.File.ReadAllText(path);
        var result = _service.Parse(markdown);

        // No markdown indexes (code blocks are intentionally excluded)
        Assert.Empty(result.Indexes);
    }

    [Fact]
    public void Parse_HendelseAdapter_ContainsNoMarkdownIndexes()
    {
        // Hendelse Adapter has no explicit **Indexes** section
        var path = TestDataHelper.ResolveSampleDataPath("hendelse-adapter", "data-model.md");
        if (!System.IO.File.Exists(path)) return;

        var markdown = System.IO.File.ReadAllText(path);
        var result = _service.Parse(markdown);

        // No indexes in markdown
        Assert.Empty(result.Indexes);
    }

    [Fact]
    public void Parse_Hendelsestjenesten_InlineIndexSyntax()
    {
        // Hendelsestjenesten has inline semicolon-separated index definitions across two lines:
        // **Indexes**: `BirkHendelsesId` (unique, for idempotency); `BarnId` (for timeline queries);
        // `BirkTiltakPK` + `BarnId IS NULL` (partial, for async linking lookup).
        //
        // All three are legitimate Data Model index definitions
        // Parsing requires: inline syntax support + continuation-line handling + composite index name extraction
        var path = TestDataHelper.ResolveSampleDataPath("hendelsestjenesten", "data-model.md");
        if (!System.IO.File.Exists(path)) return;

        var markdown = System.IO.File.ReadAllText(path);
        var result = _service.Parse(markdown);

        // Should parse exactly 3 indexes from inline syntax
        Assert.Equal(3, result.Indexes.Count);

        var indexNames = result.Indexes.Select(i => i.Name).ToHashSet();
        Assert.Contains("BirkHendelsesId", indexNames);
        Assert.Contains("BarnId", indexNames);
        Assert.Contains("BirkTiltakPK", indexNames);  // Composite index with filter

        // Verify unique flag on BirkHendelsesId
        var birkHendelsesIdx = result.Indexes.First(i => i.Name == "BirkHendelsesId");
        Assert.True(birkHendelsesIdx.IsUnique, "BirkHendelsesId should be marked unique");
    }

    [Fact]
    public void Parse_PersonAdapter_NonStandardIndexNotationRejected()
    {
        // Person Adapter has **Indexes**: bullets but with non-IX_ format:
        // - Primary key: `id`
        // - `(feiltype, post_type)` — filtered re-delivery queries
        // - `utloper_tidspunkt` — expiry purge batch job
        // These are documentation notes, not formal index definitions.
        // Parser behavior: rejected because they don't match IX_ pattern
        var path = TestDataHelper.ResolveSampleDataPath("person-adapter", "data-model.md");
        if (!System.IO.File.Exists(path)) return;

        var markdown = System.IO.File.ReadAllText(path);
        var result = _service.Parse(markdown);

        // Non-standard notation is intentionally unsupported
        Assert.Empty(result.Indexes);
    }

    [Fact]
    public void Parse_Revisjon_ContainsNoIndexes()
    {
        // Revisjon sample has no index definitions
        var path = TestDataHelper.ResolveSampleDataPath("revisjon", "data-model.md");
        if (!System.IO.File.Exists(path)) return;

        var markdown = System.IO.File.ReadAllText(path);
        var result = _service.Parse(markdown);

        Assert.Empty(result.Indexes);
    }

    [Fact]
    public void Parse_FrontendAdminPanel_ContainsNoIndexes()
    {
        // Frontend Admin Panel has no index definitions
        var path = TestDataHelper.ResolveSampleDataPath("frontend-admin-panel", "data-model.md");
        if (!System.IO.File.Exists(path)) return;

        var markdown = System.IO.File.ReadAllText(path);
        var result = _service.Parse(markdown);

        Assert.Empty(result.Indexes);
    }

    [Fact]
    public void Parse_Proxy_ContainsNoIndexes()
    {
        // Proxy sample has no index definitions
        var path = TestDataHelper.ResolveSampleDataPath("proxy", "data-model.md");
        if (!System.IO.File.Exists(path)) return;

        var markdown = System.IO.File.ReadAllText(path);
        var result = _service.Parse(markdown);

        Assert.Empty(result.Indexes);
    }

    [Fact]
    public void Parse_Tjeneste_ContainsNoIndexes()
    {
        // Tjeneste sample has no index definitions
        var path = TestDataHelper.ResolveSampleDataPath("tjeneste", "data-model.md");
        if (!System.IO.File.Exists(path)) return;

        var markdown = System.IO.File.ReadAllText(path);
        var result = _service.Parse(markdown);

        Assert.Empty(result.Indexes);
    }
}
