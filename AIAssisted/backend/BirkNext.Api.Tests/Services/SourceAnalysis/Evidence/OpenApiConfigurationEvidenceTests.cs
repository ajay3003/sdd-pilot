using System.Security.Cryptography;
using BirkNext.SourceArchitecture;
using BirkNext.SourceDomains;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.SourceAnalysis.Evidence;

/// <summary>
/// OpenAPI as configuration evidence. The Altinn app fixture <c>AIAssisted/testdata/skolenaervaer.json</c> is read-only, local test evidence:
/// it is read from disk (never fetched), packed in memory into a source archive and analyzed by the generic Source Analysis pipeline. Every
/// expectation below is a value present in that file. Where the fixture is not on disk (it is not committed), the fixture tests return early;
/// the synthetic documents below cover the same generic rules everywhere.
/// </summary>
public sealed class OpenApiConfigurationEvidenceTests
{
    internal const string FixtureSha256 = "3f8c6cb5008d66d675da7044602581b996837e996588d977b50f8890fc72e535";

    /// <summary>The read-only fixture's text, or null where it is not on disk. Its hash is pinned: a changed fixture fails instead of drifting.</summary>
    internal static string? Fixture()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "testdata", "skolenaervaer.json");
            if (!File.Exists(path)) path = Path.Combine(directory.FullName, "AIAssisted", "testdata", "skolenaervaer.json");
            if (!File.Exists(path)) continue;
            var bytes = File.ReadAllBytes(path);
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant().Should().Be(FixtureSha256, "the fixture is read-only evidence and is never modified");
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        return null;
    }

    private static List<SourceConfigurationValue> Values(params (string Path, string Content)[] files) =>
        SourceEvidenceFixtures.Analyze(files).ConfigurationValues!.Values;

    private static string?[] Of(IEnumerable<SourceConfigurationValue> values, string key) => values.Where(value => value.SemanticKey == key).Select(value => value.Value).ToArray();

    // ── The Altinn app fixture ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "LocalFixture")]
    public void TheFixture_IsRecognizedAsOpenApi_AndDeclaresItsIdentityServersAndRoute()
    {
        if (Fixture() is not { } text) return;
        var domains = SourceEvidenceFixtures.Analyze(("skolenaervaer.json", text));
        var contract = domains.Contracts.Contracts.Should().ContainSingle(item => item.Type == SourceContractType.OpenApi).Subject;
        contract.Name.Should().Be("Altinn 3 App API for School attendance");
        contract.Version.Should().Be("8.12.7.264");

        var values = domains.ConfigurationValues!.Values;
        values.Should().OnlyContain(value => value.EvidenceKind == SourceConfigurationEvidenceKind.OpenApi && value.Artifact == "skolenaervaer.json",
            "every value keeps the artifact that supplied it");
        Of(values, SourceConfigurationKeys.ApplicationDisplayName).Should().Equal("Altinn 3 App API for School attendance");
        Of(values, SourceConfigurationKeys.ApplicationVersion).Should().Equal("8.12.7.264");

        var test = values.Should().ContainSingle(value => value.SemanticKey == "Environment.Server.Test").Subject;
        test.Value.Should().Be("https://buf.apps.tt02.altinn.no");
        test.Environment.Should().Be(SourceEnvironmentRole.Test);
        test.Location.Should().Be("servers[1]");
        test.Basis.Should().Contain("TT02 server", "the role comes from the server's own description");
        Of(values, "Environment.Server.Production").Should().Equal("https://buf.apps.altinn.no");
        Of(values, "Environment.Server.Local").Should().Equal("http://local.altinn.cloud");

        var route = values.Should().ContainSingle(value => value.SemanticKey == SourceConfigurationKeys.ApplicationRoute).Subject;
        route.Value.Should().Be("buf/skolenaervaer");
        route.Confidence.Should().Be(ArchitectureEvidenceState.StronglySupported, "the route's first segment is also the first label of the declared servers");
        route.Basis.Should().StartWith("13 of 15 paths start with /buf/skolenaervaer/");
        values.Should().NotContain(value => value.SemanticKey.Contains("Org", StringComparison.Ordinal), "org and app are not split without evidence that says which is which");
    }

    [Fact]
    [Trait("Category", "LocalFixture")]
    public void TheFixture_DeclaresTheFormDataTypeItsXmlContentTypeAndTheProcessTask_WithoutProcessBpmn()
    {
        if (Fixture() is not { } text) return;
        var domains = SourceEvidenceFixtures.Analyze(("skolenaervaer.json", text));
        var values = domains.ConfigurationValues!.Values;

        Of(values, SourceConfigurationKeys.ApplicationDataType).Should().BeEquivalentTo(["ref-data-as-pdf", "bufdir_barnevern_tiltak_skolenaervaer_v2_0_0_altinn"]);
        var form = values.Should().ContainSingle(value => value.SemanticKey == SourceConfigurationKeys.ApplicationFormDataType).Subject;
        form.Value.Should().Be("bufdir_barnevern_tiltak_skolenaervaer_v2_0_0_altinn");
        form.Attributes.Should().Contain("TaskId", "Task_1").And.Contain("MimeTypes", "application/xml");
        var formRow = values.Single(value => value.SemanticKey == SourceConfigurationKeys.ApplicationDataType && value.Value == form.Value);
        formRow.Attributes.Should().Contain("Type", "FormData (AutoCreate)").And.Contain("AllowedNumber", "1");

        // application/xml from the data-types table AND structurally from the create-instance multipart encoding: two artifacts' locations agree.
        var contentType = SourceConfigurationResolver.Resolve(values).Should().ContainSingle(item => item.SemanticKey == SourceConfigurationKeys.PayloadContentType).Subject;
        contentType.Value.Should().Be("application/xml");
        contentType.State.Should().Be(SourceConfigurationResolutionState.Agreed);
        contentType.Candidates.Single().Provenance.Select(item => item.Location).Should().Contain(location => location.Contains("encoding.bufdir_barnevern_tiltak_skolenaervaer_v2_0_0_altinn"));

        var task = values.Should().ContainSingle(value => value.SemanticKey == SourceConfigurationKeys.ApplicationTaskId).Subject;
        task.Value.Should().Be("Task_1");
        task.Attributes.Should().Contain("Name", "Utfylling").And.Contain("Type", "data");
        Of(values, SourceConfigurationKeys.ApplicationTaskName).Should().Equal("Utfylling");

        // process.bpmn regression: the archive holds only the OpenAPI document, and the Altinn app evidence still has its task.
        domains.FileRoles.SelectMany(role => role.Examples).Should().NotContain(path => path.EndsWith(".bpmn", StringComparison.OrdinalIgnoreCase));
        var app = domains.Contracts.AltinnApps.Should().ContainSingle().Subject;
        app.AppId.Should().Be("buf/skolenaervaer");
        app.HasApplicationMetadata.Should().BeFalse("no applicationmetadata.json was imported");
        app.FormDataTypeId.Should().Be("bufdir_barnevern_tiltak_skolenaervaer_v2_0_0_altinn");
        app.FormDataTaskId.Should().Be("Task_1");
        app.AllowedContentTypes.Should().Equal("application/xml");
        app.ProcessTasks.Should().Equal(new BirkNext.Integrations.AltinnProcessTask("Task_1", "data"));
        app.Notes.Should().NotContain(note => note.Contains("process.bpmn", StringComparison.Ordinal) && note.StartsWith("No ", StringComparison.Ordinal),
            "no artifact is required when an imported one already provides the value");
        app.Documents.Should().Equal("skolenaervaer.json");
    }

    [Fact]
    [Trait("Category", "LocalFixture")]
    public void TheFixture_DeclaresEndpointsAuthenticationAndRequestSchema()
    {
        if (Fixture() is not { } text) return;
        var values = Values(("skolenaervaer.json", text));
        Of(values, SourceConfigurationKeys.EndpointProcessNext).Should().Equal("PUT /buf/skolenaervaer/instances/{instanceOwnerPartyId}/{instanceGuid}/process/next");
        Of(values, SourceConfigurationKeys.EndpointValidate).Should().Equal("GET /buf/skolenaervaer/instances/{instanceOwnerPartyId}/{instanceGuid}/validate");
        Of(values, SourceConfigurationKeys.EndpointApplicationMetadata).Should().Equal("GET /buf/skolenaervaer/api/v1/applicationmetadata");
        Of(values, SourceConfigurationKeys.EndpointInstanceCreate).Should().Equal("POST /buf/skolenaervaer/instances");
        Of(values, SourceConfigurationKeys.EndpointInstanceRead).Should().Equal("GET /buf/skolenaervaer/instances/{instanceOwnerPartyId}/{instanceGuid}");
        Of(values, SourceConfigurationKeys.EndpointOptions).Should().HaveCount(2);
        Of(values, SourceConfigurationKeys.EndpointAuthenticationExchange).Should().Equal("GET /authentication/api/v1/exchange/{tokenProvider}");
        values.Should().ContainSingle(value => value.SemanticKey == SourceConfigurationKeys.EndpointLocalTestToken).Which.Environment.Should().Be(SourceEnvironmentRole.Local);
        Of(values, SourceConfigurationKeys.EndpointDataWrite).Should().Contain("POST /buf/skolenaervaer/instances/{instanceOwnerPartyId}/{instanceGuid}/data/type/bufdir_barnevern_tiltak_skolenaervaer_v2_0_0_altinn");

        var scheme = values.Should().ContainSingle(value => value.SemanticKey == SourceConfigurationKeys.AuthenticationScheme).Subject;
        scheme.Value.Should().Be("Bearer JWT");
        scheme.Attributes.Should().Contain("Name", "AltinnToken").And.Contain("Type", "http");
        Of(values, SourceConfigurationKeys.AuthenticationTokenProvider).Should().Equal("maskinporten", "id-porten");
        Of(values, SourceConfigurationKeys.ContractSchemaReference).Should().Contain("SkolenaervaerType");
        values.Should().NotContain(value => value.SemanticKey.StartsWith("Authentication.Client", StringComparison.Ordinal) || value.SemanticKey.Contains("Scope", StringComparison.Ordinal),
            "client id, redirect URI and scopes are not in the document and are never inferred");
    }

    [Fact]
    [Trait("Category", "LocalFixture")]
    public void TheFixture_ExamplesAreDocumentation_NeverConfiguration()
    {
        if (Fixture() is not { } text) return;
        var values = Values(("skolenaervaer.json", text));
        var all = values.SelectMany(value => new[] { value.Value ?? "" }.Concat(value.Attributes.Values)).ToArray();
        all.Should().NotContain(["1337", "nb", "3", "False", "Bearer <token>"]);
        all.Should().NotContain(item => item.StartsWith("eyJ", StringComparison.Ordinal), "a sample JWT is an example, never a value");
        values.Should().NotContain(value => value.Location.Contains("example", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The OpenAPI configuration reader, collector and guidance parse what they are given: none of them can open a network connection
    /// (no HTTP client, socket or URL fetch), so a declared server such as a TT02 host is never contacted.</summary>
    [Fact]
    public void Configuration_evidence_code_has_no_network_access()
    {
        var api = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "BirkNext.Api");
        if (!Directory.Exists(api)) return;
        foreach (var file in new[] { "Services/SourceAnalysis/Evidence/OpenApiConfigurationReader.cs", "Services/SourceAnalysis/Evidence/SourceConfigurationValueCollector.cs",
            "Services/SourceAnalysis/SourceConfigurationGuidance.cs" })
            System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(Path.Combine(api, file)), @"HttpClient|HttpMessageHandler|WebRequest|Socket|DownloadString|GetAsync\(|SendAsync\(")
                .Should().BeFalse($"{file} only reads analyzed evidence");
    }

    // ── Generic rules (synthetic documents; run everywhere) ─────────────────────────────────────────────────────────────

    private const string Synthetic = """
        {
          "openapi": "3.0.1",
          "info": { "title": "Parcel API", "version": "2.1.0" },
          "servers": [
            { "url": "https://parcels.example.invalid", "description": "Production" },
            { "url": "https://parcels.test.example.invalid", "description": "Acceptance test environment" },
            { "url": "https://unknown.example.invalid" },
            { "url": "https://user:pw@secret.example.invalid", "description": "Test with credentials" }
          ],
          "paths": {
            "/shop/parcels/instances": { "post": { "responses": { "201": { "description": "ok" } } } },
            "/shop/parcels/instances/{a}/{b}/process/next": { "put": { "responses": { "200": { "description": "ok" } } } },
            "/shop/parcels/labels": { "get": { "parameters": [ { "name": "lang", "in": "query", "schema": { "type": "string", "example": "en" } } ], "responses": { "200": { "description": "ok" } } } }
          },
          "components": { "securitySchemes": { "Key": { "type": "apiKey", "in": "header", "name": "X-Api-Key" } } }
        }
        """;

    [Fact]
    public void ServerRoles_ComeFromDescriptions_NeverFromOrder_AndUnsafeUrlsAreSkipped()
    {
        var values = Values(("api/parcels.json", Synthetic));
        Of(values, "Environment.Server.Production").Should().Equal("https://parcels.example.invalid");
        Of(values, "Environment.Server.Test").Should().Equal("https://parcels.test.example.invalid");
        values.Should().ContainSingle(value => value.SemanticKey == SourceConfigurationKeys.EnvironmentServer).Which.Environment.Should().Be(SourceEnvironmentRole.Unclassified,
            "a server without a description is not given a role by its position");
        values.Should().NotContain(value => (value.Value ?? "").Contains("secret.example.invalid") || (value.Value ?? "").Contains("pw"), "a URL with credentials is never a value");
        Of(values, SourceConfigurationKeys.ApplicationRoute).Should().Equal("shop/parcels");
        values.Single(value => value.SemanticKey == SourceConfigurationKeys.ApplicationRoute).Confidence.Should().Be(ArchitectureEvidenceState.Inferred,
            "no declared server host corroborates the route");
        Of(values, SourceConfigurationKeys.AuthenticationScheme).Should().Equal("API key (header X-Api-Key)");
        values.SelectMany(value => new[] { value.Value }).Should().NotContain("en", "a parameter example is not configuration");
    }

    [Fact]
    public void A_document_whose_paths_share_no_application_route_declares_none()
    {
        var values = Values(("openapi.json", """
            { "openapi": "3.0.0", "info": { "title": "x", "version": "1" },
              "paths": { "/api/v1/orders": { "get": { "responses": {} } }, "/api/v1/customers": { "get": { "responses": {} } }, "/health": { "get": { "responses": {} } } } }
            """));
        Of(values, SourceConfigurationKeys.ApplicationRoute).Should().BeEmpty("generic API segments such as api/v1 are not an application route");
    }

    [Fact]
    public void Agreeing_values_aggregate_provenance_and_different_values_are_a_visible_conflict()
    {
        SourceConfigurationValue V(string value, string artifact) => new()
        {
            SemanticKey = "Environment.Server.Test", Value = value, Environment = SourceEnvironmentRole.Test, Artifact = artifact, Location = "servers[0]",
            EvidenceKind = artifact.EndsWith(".json", StringComparison.Ordinal) ? SourceConfigurationEvidenceKind.OpenApi : SourceConfigurationEvidenceKind.ApplicationConfig,
        };
        var agreed = SourceConfigurationResolver.Resolve([V("https://a.invalid", "a.json"), V("https://a.invalid", "appsettings.Test.json")]).Single();
        agreed.State.Should().Be(SourceConfigurationResolutionState.Agreed);
        agreed.Value.Should().Be("https://a.invalid");
        agreed.Candidates.Single().Provenance.Select(item => item.Artifact).Should().Equal("a.json", "appsettings.Test.json");

        var conflict = SourceConfigurationResolver.Resolve([V("https://a.invalid", "a.json"), V("https://b.invalid", "appsettings.Test.json")]).Single();
        conflict.State.Should().Be(SourceConfigurationResolutionState.Conflict);
        conflict.Value.Should().BeNull("a conflict is never silently resolved");
        conflict.Candidates.Select(item => item.Value).Should().Equal("https://a.invalid", "https://b.invalid");

        var perEnvironment = SourceConfigurationResolver.Resolve([V("https://a.invalid", "a.json"), V("https://p.invalid", "b.json") with { SemanticKey = "Environment.Server.Production", Environment = SourceEnvironmentRole.Production }]);
        perEnvironment.Should().HaveCount(2).And.OnlyContain(item => item.State == SourceConfigurationResolutionState.Declared, "different environments are variants, not a conflict");

        var multi = SourceConfigurationResolver.Resolve([V("maskinporten", "a.json") with { SemanticKey = SourceConfigurationKeys.AuthenticationTokenProvider, Environment = SourceEnvironmentRole.Any },
            V("id-porten", "a.json") with { SemanticKey = SourceConfigurationKeys.AuthenticationTokenProvider, Environment = SourceEnvironmentRole.Any }]);
        multi.Should().HaveCount(2).And.OnlyContain(item => item.State == SourceConfigurationResolutionState.Declared, "a declared set is not a conflict");
    }

    [Fact]
    public void Terraform_messaging_names_are_declared_configuration_values_and_computed_names_are_not()
    {
        var domains = SourceEvidenceFixtures.Analyze(("infra/main.tf", """
            resource "azurerm_servicebus_namespace" "bus" {
              name                = "sb-parcels"
              location            = "westeurope"
              resource_group_name = "rg"
              sku                 = "Standard"
            }
            resource "azurerm_servicebus_topic" "closed" {
              name         = "period-closed"
              namespace_id = azurerm_servicebus_namespace.bus.id
            }
            resource "azurerm_servicebus_topic" "computed" {
              name         = "${var.prefix}-events"
              namespace_id = azurerm_servicebus_namespace.bus.id
            }
            """));
        var values = domains.ConfigurationValues!.Values;
        Of(values, SourceConfigurationKeys.MessagingServiceBusNamespace).Should().Equal("sb-parcels");
        Of(values, SourceConfigurationKeys.MessagingServiceBusTopic).Should().Equal("period-closed");
        values.Where(value => value.EvidenceKind == SourceConfigurationEvidenceKind.InfrastructureAsCode).Should().OnlyContain(value => value.Artifact == "infra/main.tf" && value.Group == SourceConfigurationGroup.Messaging);
        domains.ConfigurationValues.Limitations.Should().Contain(item => item.StartsWith("Declared in source: not configured, not reachable, not runtime verified"));
    }
}
