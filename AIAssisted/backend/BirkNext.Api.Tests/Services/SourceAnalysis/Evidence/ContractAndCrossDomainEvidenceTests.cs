using BirkNext.Api.Services.SourceAnalysis.Evidence;
using BirkNext.SourceArchitecture;
using BirkNext.SourceDomains;
using FluentAssertions;
using static BirkNext.Api.Tests.Services.SourceAnalysis.Evidence.SourceEvidenceFixtures;

namespace BirkNext.Api.Tests.Services.SourceAnalysis.Evidence;

/// <summary>Contracts as reusable source evidence (typed adapters, producer/consumer by ownership, drift classes) and the stage-3 cross-domain
/// links: exact identities are strong, type-only matches Inferred, unmatched references neutral Unresolved — never "missing" or "failed".</summary>
public sealed class ContractAndCrossDomainEvidenceTests
{
    private const string Sdl = """
        type Query { order(id: ID!): Order  orders: [Order!]! }
        type Mutation { placeOrder(input: PlaceOrderInput!): Order }
        type Order { id: ID! total: Float status: String! }
        input PlaceOrderInput { sku: String! quantity: Int! }
        """;

    [Fact]
    public void Openapi_yaml_is_read_partially_and_attributed_to_the_owning_api_component()
    {
        var c = Analyze(SourceEvidenceFixtures.Acme()).Contracts.Contracts.Single(x => x.Type == SourceContractType.OpenApi);
        c.Name.Should().Be("Acme Ordering API");
        c.Version.Should().Be("1.2.0");
        c.Operations.Select(o => o.Name).Should().BeEquivalentTo(["GET /orders/{id}", "POST /orders"]);
        c.Types.Single(t => t.Name == "Order").Fields.Should().BeEquivalentTo([new ContractField("id", "string", true), new ContractField("total", "number", false)]);
        c.ParseSupport.Should().Be(DomainSupport.Partial);
        c.Producer.Should().Be("Web");
        c.RuntimeState.Should().Be(SourceDomainText.ContractNotVerified);
    }

    [Fact]
    public void Graphql_sdl_is_supported_and_operation_documents_are_consumer_side()
    {
        var e = Analyze(
            ("src/Shop.Api/Shop.Api.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="HotChocolate.AspNetCore" Version="14.0.0" /></ItemGroup></Project>"""),
            ("src/Shop.Api/schema.graphql", Sdl),
            ("src/Shop.Web/Shop.Web.csproj", """<Project Sdk="Microsoft.NET.Sdk.BlazorWebAssembly"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="StrawberryShake.Blazor" Version="14.0.0" /></ItemGroup></Project>"""),
            ("src/Shop.Web/GraphQL/Orders.graphql", "query GetOrder($id: ID!) { order(id: $id) { id total } }"),
            ("src/Shop.Web/GraphQL/.graphqlrc.json", """{ "schema": "schema.graphql", "extensions": { "strawberryShake": { "name": "ShopClient", "url": "https://api.shop.example/graphql" } } }"""),
            ("docs/schema.graphql", Sdl)).Contracts;
        var schema = e.Contracts.Single(c => c.File == "src/Shop.Api/schema.graphql");
        schema.Type.Should().Be(SourceContractType.GraphQlSchema);
        schema.ParseSupport.Should().Be(DomainSupport.Supported);
        schema.Operations.Select(o => o.Name).Should().BeEquivalentTo(["query order", "query orders", "mutation placeOrder"]);
        schema.Types.Single(t => t.Name == "Order").Fields.Should().Contain(new ContractField("status", "String!", true));
        var operations = e.Contracts.Single(c => c.Type == SourceContractType.GraphQlOperations);
        operations.Operations.Single().Should().Match<ContractOperation>(o => o.Name == "query GetOrder" && o.Parameters.Contains("$id"));
        operations.ConsumerHints.Should().NotBeEmpty();
        e.Contracts.Should().Contain(c => c.Type == SourceContractType.GeneratedClient && c.Name.StartsWith("Strawberry Shake"));
        e.Contracts.Single(c => c.File == "docs/schema.graphql").Should().Match<SourceContract>(c => c.Producer == null && c.EvidenceState == ArchitectureEvidenceState.Inferred);
    }

    [Fact]
    public void Contract_changes_are_classified_without_calling_them_breaking()
    {
        SourceContract Contract(params ContractField[] fields) => new()
        {
            Id = "openapi:api.json", Name = "api", Type = SourceContractType.OpenApi, Operations = [new("GET /a", "GET", "/a", [])], Types = [new("Order", "object", [.. fields])],
        };
        var before = Contract(new("id", "string", true), new("total", "number", false), new("legacy", "string", false));
        var after = Contract(new("id", "integer", true), new("total", "number", true), new("note", "string", false), new("sku", "string", true)) with
        { Operations = [new("GET /a", "GET", "/a", []), new("POST /a", "POST", "/a", [])] };
        var classes = SourceEvidenceDiff.ContractChanges(before, after).Select(c => c.ContractChange).ToList();
        classes.Should().BeEquivalentTo([ContractChangeClass.OperationAdded, ContractChangeClass.TypeChanged, ContractChangeClass.RequirednessChanged,
            ContractChangeClass.FieldAddedOptional, ContractChangeClass.FieldAddedRequired, ContractChangeClass.FieldRemoved]);
        SourceEvidenceDiff.Label.Should().Contain("not runtime or deployment drift");
    }

    [Fact]
    public void Cross_domain_links_use_exact_identities_and_keep_unmatched_references_neutral()
    {
        var x = Analyze(SourceEvidenceFixtures.Acme()).CrossDomain;
        x.Links.Should().Contain(l => l.Type == SourceEvidenceLinkType.ApplicationUsesInfrastructureResource && l.ToLabel == "Service Bus topic orders-created" && l.State == ArchitectureEvidenceState.StronglySupported);
        x.Links.Should().Contain(l => l.Type == SourceEvidenceLinkType.ConfigurationReferencesInfrastructureResource && l.FromLabel.StartsWith("ConnectionStrings:Orders")
            && l.ToId == "Infrastructure/azurerm_postgresql_flexible_server.db" && l.State == ArchitectureEvidenceState.StronglySupported);
        x.Links.Should().Contain(l => l.Type == SourceEvidenceLinkType.ApplicationUsesDatastore && l.State == ArchitectureEvidenceState.Inferred);
        x.Links.Should().Contain(l => l.Type == SourceEvidenceLinkType.PipelineDeploysInfrastructure && l.State == ArchitectureEvidenceState.Confirmed);
        x.Links.Should().Contain(l => l.Type == SourceEvidenceLinkType.PipelineRunsTests && l.ToLabel == "Acme.Ordering.UnitTests");
        x.Links.Should().Contain(l => l.Type == SourceEvidenceLinkType.ContractProducedByComponent && l.ToLabel == "Web");
        x.Links.Should().Contain(l => l.Type == SourceEvidenceLinkType.TelemetryConfiguredForComponent && l.ToLabel.Contains("appi-acme-ordering") && l.State == ArchitectureEvidenceState.Inferred);
        var unresolved = x.Links.Single(l => l.ToLabel == "inventory-changed");
        unresolved.Resolved.Should().BeFalse();
        unresolved.State.Should().Be(ArchitectureEvidenceState.Unresolved);
        unresolved.Basis.Should().Contain("may be managed elsewhere").And.NotContainAny("Missing", "failed", "Failed");
        x.Status.Should().Be(SourceDomainStatus.Partial);
    }

    [Fact]
    public void Environment_mapping_is_confirmed_only_by_an_explicit_link_and_runtime_telemetry_is_never_assessed()
    {
        var x = Analyze(SourceEvidenceFixtures.Acme()).CrossDomain;
        x.EnvironmentMappings.Single(m => m.Kind == SourceEnvironmentKind.QA).State.Should().Be(ArchitectureEvidenceState.Confirmed, "the pipeline passes qa.tfvars explicitly");
        x.EnvironmentMappings.Single(m => m.Kind == SourceEnvironmentKind.Production).State.Should().Be(ArchitectureEvidenceState.Inferred, "names agree, nothing links them");
        x.ObservabilityLayers.Select(l => l.Layer).Should().Equal("Application code", "Configuration", "Infrastructure", "CI/CD", "Runtime telemetry");
        x.ObservabilityLayers.Single(l => l.Layer == "Infrastructure").State.Should().Be("Detected");
        x.ObservabilityLayers.Single(l => l.Layer == "Runtime telemetry").State.Should().Be(SourceDomainText.RuntimeNotAssessed);
    }

    [Fact]
    public void Without_iac_application_references_are_not_counted_as_unresolved_infrastructure()
    {
        var x = Analyze(SourceEvidenceFixtures.Acme().Where(f => !f.Path.StartsWith("Infrastructure/")).ToArray()).CrossDomain;
        x.Links.Should().NotContain(l => l.Type == SourceEvidenceLinkType.ApplicationUsesInfrastructureResource || l.Type == SourceEvidenceLinkType.ConfigurationReferencesInfrastructureResource);
        x.Limitations.Should().Contain(l => l.Contains("not counted as unresolved"));
        x.ObservabilityLayers.Single(l => l.Layer == "Infrastructure").State.Should().Be("Not detected in selected source");
    }
}
