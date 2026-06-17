using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

public class ArchitectureExtractorTests
{
    [Fact]
    public void ApiSurface_ExtractsGraphQlAndRestAsApis()
    {
        const string md = """
            # Specification

            ## API Surface
            - GraphQL - consumed by presentation layer
            - REST - ingestion endpoint for external clients
            """;

        var model = Extract(md);

        model.Elements.Should().Contain(e => e.ElementType == ArchElementType.Api && e.Name == "GraphQL");
        model.Elements.Should().Contain(e => e.ElementType == ArchElementType.Api && e.Name == "REST");
    }

    [Fact]
    public void KeyEntities_ExtractsEntitiesAndDataStores()
    {
        const string md = """
            # Specification

            ## Key Entities
            - Person: Any individual relevant to the workflow
            - Barn: Child record in the domain model
            - BarnStatusHistorikk: Historical status records stored for audit
            """;

        var model = Extract(md);

        model.Elements.Should().Contain(e => e.ElementType == ArchElementType.DomainEntity && e.Name == "Person");
        model.Elements.Should().Contain(e => e.ElementType == ArchElementType.DomainEntity && e.Name == "Barn");
        model.Elements.Should().Contain(e =>
            e.Name == "BarnStatusHistorikk"
            && (e.ElementType == ArchElementType.DataStore
                || e.ElementType == ArchElementType.Persistence
                || e.ElementType == ArchElementType.DomainEntity));
    }

    [Fact]
    public void MessagingAndEvents_ExtractsEventsTopicsAndRelationships()
    {
        const string md = """
            # Specification

            ## Messaging / Events
            - Person Module publishes PersonCreated to person.person topic
            - Registration Service publishes ChildRegistered to person.child topic
            """;

        var model = Extract(md);

        model.Elements.Should().Contain(e => e.ElementType == ArchElementType.DomainEvent && e.Name == "PersonCreated");
        model.Elements.Should().Contain(e => e.ElementType == ArchElementType.DomainEvent && e.Name == "ChildRegistered");
        model.Elements.Should().Contain(e => e.ElementType == ArchElementType.Messaging && e.Name == "person.person topic");
        model.Elements.Should().Contain(e => e.ElementType == ArchElementType.Messaging && e.Name == "person.child topic");
        model.Relationships.Should().Contain(r => r.Verb.Contains("publishes") && r.TargetName.Contains("person.person"));
        model.Relationships.Should().Contain(r => r.Verb.Contains("publishes") && r.TargetName.Contains("person.child"));
    }

    [Fact]
    public void Integration_ExtractsAdapterRestAuthorisationAndRelationships()
    {
        const string md = """
            # Specification

            ## Integrations
            - BiRK Adapter sends records to REST ingestion endpoint
            - Authorisation service validates access
            """;

        var model = Extract(md);

        model.Elements.Should().Contain(e =>
            e.Name == "BiRK Adapter"
            && (e.ElementType == ArchElementType.Service || e.ElementType == ArchElementType.IntegrationPoint));
        model.Elements.Should().Contain(e =>
            e.Name.ToLower().Contains("rest")
            && e.ElementType == ArchElementType.Api);
        model.Elements.Should().Contain(e =>
            e.Name.ToLower().Contains("authorisation service")
            && (e.ElementType == ArchElementType.Service || e.ElementType == ArchElementType.Security));
        model.Relationships.Should().Contain(r => r.SourceName.Contains("BiRK Adapter") && r.Verb.Contains("sends"));
        model.Relationships.Should().Contain(r => r.SourceName.Contains("Authorisation service") && r.Verb.Contains("validates"));
    }

    [Fact]
    public void GenericNonDomainSpec_ExtractsArchitectureWithoutDomainSpecificRules()
    {
        const string md = """
            # Commerce Platform

            ## System Overview
            Payment Service calls Order API.
            Inventory Adapter publishes OrderCreated event to Event Bus.

            ## Components
            - Payment Service
            - Order API
            - Inventory Adapter
            - Event Bus
            - OrderCreated event
            """;

        var model = Extract(md);

        model.Elements.Should().Contain(e => e.Name == "Payment Service" && e.ElementType == ArchElementType.Service);
        model.Elements.Should().Contain(e => e.Name == "Order API" && e.ElementType == ArchElementType.Api);
        model.Elements.Should().Contain(e => e.Name == "Inventory Adapter" && e.ElementType == ArchElementType.Service);
        model.Elements.Should().Contain(e => e.Name == "Event Bus" && e.ElementType == ArchElementType.Messaging);
        model.Elements.Should().Contain(e => e.Name == "OrderCreated" && e.ElementType == ArchElementType.DomainEvent);
        model.Relationships.Should().Contain(r => r.SourceName == "Payment Service" && r.TargetName == "Order API");
        model.Relationships.Should().Contain(r => r.SourceName == "Inventory Adapter" && r.TargetName == "Event Bus");
    }

    // ── Pipeline regression tests ─────────────────────────────────────────────

    [Fact]
    public void RawMarkdown_WithKnownTerms_ProducesArchitectureElements()
    {
        // Regression: proves that when rawMarkdown is non-null the extractor finds elements.
        // This is the key pipeline invariant fixed by embedding SpecMarkdown in ExtractionPipelineResult.
        const string spec = """
            # Overview

            ## Architecture

            The Presentation Layer renders data fetched via GraphQL.
            Person Module handles identity resolution and profile data.
            Azure Service Bus routes domain events between services.
            """;

        var tree  = SpecExplorerService.Parse(spec);
        var model = ArchitectureExtractor.Extract(tree, rawMarkdown: spec, candidates: null);

        model.IsEmpty.Should().BeFalse(
            "spec contains GraphQL, Person Module, and Service Bus which match extraction patterns");

        bool found = model.Elements.Any(e => e.ElementType == ArchElementType.Api && e.Name.ToLower().Contains("graphql"))
                  || model.Elements.Any(e => e.ElementType == ArchElementType.Service && e.Name.ToLower().Contains("person module"))
                  || model.Elements.Any(e => e.ElementType == ArchElementType.Messaging && e.Name.ToLower().Contains("service bus"));

        found.Should().BeTrue(
            "at least one of GraphQL (Api), Person Module (Service), or Service Bus (Messaging) must be extracted when rawMarkdown is provided");
    }

    [Fact]
    public void NullRawMarkdown_WithNoStructuredNodes_ProducesEmptyModel()
    {
        // Regression: null rawMarkdown with an empty tree must produce an empty model.
        var tree  = SpecExplorerService.Parse(string.Empty);
        var model = ArchitectureExtractor.Extract(tree, rawMarkdown: null, candidates: null);

        model.IsEmpty.Should().BeTrue("no input means nothing to extract");
    }

    private static ArchitectureModel Extract(string markdown)
    {
        var tree = SpecExplorerService.Parse(markdown);
        return ArchitectureExtractor.Extract(tree, rawMarkdown: markdown, candidates: null);
    }
}
