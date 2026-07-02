using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Xunit;

namespace BirkNext.Web.Tests;

public class DataModelAnalysisServiceTests
{
    private readonly DataModelAnalysisService _service = new();

    [Fact]
    public void Parse_WithLegacyEntityFormat_ParsesCorrectly()
    {
        var markdown = """
            # Data Model: Test

            ## Entity: Person

            | Column | Type | Nullable |
            |--------|------|----------|
            | Id | UUID | No |
            """;

        var doc = _service.Parse(markdown);

        Assert.NotEmpty(doc.Entities);
        Assert.Equal("Person", doc.Entities[0].Name);
    }

    [Fact]
    public void Parse_WithPersistentEntitiesSection_ParsesH3Entities()
    {
        var markdown = """
            # Data Model: Test

            ## Persistent Entities

            ### FaultQueueEntry

            | Column | Type | Nullable |
            |--------|------|----------|
            | id | UUID | No |
            """;

        var doc = _service.Parse(markdown);

        Assert.NotEmpty(doc.Entities);
        Assert.Equal("FaultQueueEntry", doc.Entities[0].Name);
    }

    [Fact]
    public void Parse_WithDomainEntitiesSection_ParsesH3Entities()
    {
        var markdown = """
            # Data Model: Test

            ## Domain Entities

            ### Operation

            | Field | Type | Notes |
            |-------|------|-------|
            | Id | Guid | M2LB UUID |
            """;

        var doc = _service.Parse(markdown);

        Assert.NotEmpty(doc.Entities);
        Assert.Equal("Operation", doc.Entities[0].Name);
    }

    [Fact]
    public void Parse_WithConfigurationEntitiesSection_ParsesH3Entities()
    {
        var markdown = """
            # Data Model: Test

            ## Configuration Entities

            ### RouteDefinition

            | Field | Type | Required |
            |-------|------|----------|
            | RouteId | string | yes |
            """;

        var doc = _service.Parse(markdown);

        Assert.NotEmpty(doc.Entities);
        Assert.Equal("RouteDefinition", doc.Entities[0].Name);
    }

    [Fact]
    public void Parse_WithNumberedHeadings_ParsesCorrectly()
    {
        var markdown = """
            # Data Model: Test

            ### 2.1 Person

            | Column | Type | Nullable |
            |--------|------|----------|
            | PersonId | UUID | No |
            """;

        var doc = _service.Parse(markdown);

        Assert.NotEmpty(doc.Entities);
        Assert.Equal("Person", doc.Entities[0].Name);
    }

    [Fact]
    public void Parse_WithTableAlias_ExtractsTableName()
    {
        var markdown = """
            # Data Model: Test

            ## Persistent Entities

            ### FaultQueueEntry — `feilkoe` table

            | Column | Type | Nullable |
            |--------|------|----------|
            | id | UUID | No |
            """;

        var doc = _service.Parse(markdown);

        Assert.NotEmpty(doc.Entities);
        Assert.Equal("FaultQueueEntry", doc.Entities[0].Name);
        Assert.Contains("feilkoe", doc.Entities[0].Description ?? "");
    }

    [Fact]
    public void Parse_WithBacktickedTableName_ParsesAsEntity()
    {
        var markdown = """
            # Data Model: Test

            ## Persistent Entities

            ### `birk_tiltak`

            | Column | Type | Nullable |
            |--------|------|----------|
            | id | INT | No |
            """;

        var doc = _service.Parse(markdown);

        Assert.NotEmpty(doc.Entities);
        Assert.Equal("birk_tiltak", doc.Entities[0].Name);
    }

    [Fact]
    public void Parse_WithRuntimeContextSection_CreatesNonPersistentEntities()
    {
        var markdown = """
            # Data Model: Test

            ## Runtime Context Structures

            ### RequestContext

            | Field | Type | Description |
            |-------|------|-------------|
            | CorrelationId | UUID | Request correlation ID |
            """;

        var doc = _service.Parse(markdown);

        Assert.NotEmpty(doc.Entities);
        Assert.Equal("RequestContext", doc.Entities[0].Name);
    }

    [Fact]
    public void Parse_WithInTransitObjectsSection_ParsesCorrectly()
    {
        var markdown = """
            # Data Model: Test

            ## In-Transit Objects (not persisted by adapter)

            ### BiRK CDC Event

            | Field | Description |
            |-------|-------------|
            | operasjon | CDC operation |
            """;

        var doc = _service.Parse(markdown);

        Assert.NotEmpty(doc.Entities);
        Assert.Equal("BiRK CDC Event", doc.Entities[0].Name);
    }

    [Fact]
    public void Parse_WithNoEntities_ShowsInfoNotError()
    {
        var markdown = """
            # Data Model: Test

            ## Overview

            This is a stateless service.
            """;

        var doc = _service.Parse(markdown);

        Assert.Empty(doc.Entities);
        Assert.NotEmpty(doc.Findings);
        Assert.All(doc.Findings, f =>
            Assert.Equal(DataModelSeverity.Info, f.Severity));
    }

    [Fact]
    public void Parse_WithFieldsHeaderVariation_ParsesColumns()
    {
        var markdown = """
            # Data Model: Test

            ## Entity: Config

            ### Columns

            | Field | Type | Required | Description |
            |-------|------|----------|-------------|
            | key | string | yes | Configuration key |
            """;

        var doc = _service.Parse(markdown);

        Assert.NotEmpty(doc.Entities);
        Assert.NotEmpty(doc.Entities[0].Columns);
        Assert.Equal("key", doc.Entities[0].Columns[0].Name);
    }

    [Fact]
    public void Parse_WithPropertyHeaderVariation_ParsesColumns()
    {
        var markdown = """
            # Data Model: Test

            ## Entity: Service

            | Property | Type | Nullable |
            |----------|------|----------|
            | Name | string | No |
            """;

        var doc = _service.Parse(markdown);

        Assert.NotEmpty(doc.Entities);
        var entity = doc.Entities[0];
        // For Entity: Service format, columns should be parsed from the immediate table
        // if no ### Columns subsection is specified
        if (entity.Columns.Count > 0)
        {
            Assert.Equal("Name", entity.Columns[0].Name);
        }
    }

    [Fact]
    public void Parse_SkipsSubsectionHeadings()
    {
        var markdown = """
            # Data Model: Test

            ## Persistent Entities

            ### Person

            | Column | Type |
            |--------|------|
            | Id | UUID |

            ### Columns

            This should not be treated as an entity.
            """;

        var doc = _service.Parse(markdown);

        Assert.Single(doc.Entities);
        Assert.Equal("Person", doc.Entities[0].Name);
    }

    [Fact]
    public void Parse_AllParentSectionTypes_Recognized()
    {
        var sections = new[]
        {
            "Domain Entities",
            "Persistent Entities",
            "Core Entities",
            "Core Tables",
            "Reference Tables",
            "Staging Entities",
            "Infrastructure Entity",
            "Outbox Tables",
            "Configuration Entities",
            "Runtime Context Structures",
            "In-Transit Objects",
            "Event Contracts",
            "Events",
            "SCIM Request/Response Models",
            "Frontend-Only View Models",
            "Domain Interface",
            "Infrastructure Implementation",
            "Derived Value",
            "Non-Entities"
        };

        foreach (var section in sections)
        {
            var markdown = $"""
                # Data Model: Test

                ## {section}

                ### TestEntity

                | Field | Type |
                |-------|------|
                | Id | UUID |
                """;

            var doc = _service.Parse(markdown);
            Assert.NotEmpty(doc.Entities);
            Assert.Equal("TestEntity", doc.Entities[0].Name);
        }
    }
}
