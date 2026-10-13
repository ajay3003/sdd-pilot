using System.Text.Json;
using BirkNext.Api.Services.Integrations;
using BirkNext.Integrations;
using BirkNext.SourceDomains;
using FluentAssertions;
using static BirkNext.Api.Tests.Services.SourceAnalysis.Evidence.SourceEvidenceFixtures;

namespace BirkNext.Api.Tests.Services.Integrations;

/// <summary>
/// Target Environment → Integrations compares environment-specific configured values (seeded or typed) with Terraform in Source Analysis:
/// agreement is supporting evidence, disagreement needs review, absence leaves seeded values as assumptions. Read-only — nothing is written.
/// Generic "Acme" Terraform; no project-specific names.
/// </summary>
public sealed class IntegrationTerraformComparisonTests
{
    private const string Main = """
        variable "env" { default = "dev" }
        resource "azurerm_eventhub_namespace" "orders" { name = "evhns-orders-${var.env}" }
        resource "azurerm_eventhub" "placed" {
          name         = "orders-placed"
          namespace_id = azurerm_eventhub_namespace.orders.id
        }
        """;

    private static IqrSourceSnapshot Terraform()
    {
        var evidence = Analyze(("Acme.Orders.sln", ""), ("infra/main.tf", Main), ("infra/dev.tfvars", "env = \"dev\"\n"));
        return new IqrSourceSnapshot { Id = evidence.SourceSnapshotId, Archive = new("orders-infra.zip", evidence.SourceFingerprint, 3), EvidenceDomains = evidence };
    }

    private static IntegrationCatalog Catalog(string configuredNamespace) => new()
    {
        EnvironmentId = "orders-dev",
        Platforms = [new IntegrationPlatform { Id = "eh", EnvironmentId = "orders-dev", Name = "Orders Event Hubs", Kind = IntegrationKind.EventHub, Namespace = configuredNamespace, Origin = IntegrationRecordOrigin.Seed }],
        Integrations = [new IntegrationDefinition { Id = "placed", PlatformId = "eh", Kind = IntegrationKind.EventHub, EndpointOrTopic = "orders-placed" }],
    };

    [Fact]
    public void Agreement_IsSupportingEvidence_FromTheTerraformSnapshot()
    {
        var snapshot = Terraform();
        var result = IntegrationTerraformComparisons.Compare(Catalog("evhns-orders-dev"), [snapshot], "Orders DEV", sourceAnalysisEnabled: true);
        result.TerraformAvailable.Should().BeTrue();
        result.ArchiveName.Should().Be("orders-infra.zip");
        result.SnapshotId.Should().Be(snapshot.Id);
        result.Values.Single(v => v.Comparison.Field == "Event Hubs namespace").Comparison.State.Should().Be(SourceComparisonState.Matches);
        result.Values.Single(v => v.Comparison.Field == "Event Hub").Comparison.State.Should().Be(SourceComparisonState.Matches);
    }

    [Fact]
    public void ADifference_NeedsReview_AndTheConfiguredValueIsNeverOverwritten()
    {
        var catalog = Catalog("evhns-orders-legacy");
        var before = JsonSerializer.Serialize(catalog);
        var result = IntegrationTerraformComparisons.Compare(catalog, [Terraform()], "Orders DEV", sourceAnalysisEnabled: true);
        var ns = result.Values.Single(v => v.Comparison.Field == "Event Hubs namespace");
        ns.Comparison.State.Should().Be(SourceComparisonState.Differs);
        ns.Comparison.ConfiguredValue.Should().Be("evhns-orders-legacy");
        ns.Comparison.Candidates.Select(c => c.Name).Should().Contain("evhns-orders-dev");
        JsonSerializer.Serialize(catalog).Should().Be(before, "the seed is not silently preferred and source is not silently applied");
    }

    [Fact]
    public void WithoutTerraform_NothingIsCompared_OrClaimed()
    {
        var applicationOnly = new IqrSourceSnapshot { Archive = new("app.zip", new string('a', 64), 10) };
        var result = IntegrationTerraformComparisons.Compare(Catalog("evhns-orders-dev"), [applicationOnly], "Orders DEV", sourceAnalysisEnabled: true);
        result.TerraformAvailable.Should().BeFalse();
        result.Values.Should().BeEmpty();
        IntegrationTerraformComparisons.Compare(Catalog("x"), [], null, sourceAnalysisEnabled: true).TerraformAvailable.Should().BeFalse();
    }

    [Theory]
    [InlineData("azurerm_eventhub_namespace_authorization_rule")]
    [InlineData("azurerm_eventhub_authorization_rule")]
    [InlineData("azurerm_servicebus_namespace_authorization_rule")]
    [InlineData("azurerm_servicebus_topic_authorization_rule")]
    [InlineData("azurerm_servicebus_namespace_network_rule_set")]
    [InlineData("azurerm_eventhub_namespace_disaster_recovery_config")]
    [InlineData("Microsoft.EventHub/namespaces/authorizationRules")]
    [InlineData("azurerm_application_insights_workbook")]
    [InlineData("azurerm_application_insights_standard_web_test")]
    public void AChildSetting_IsNotMistakenForItsParentResource(string type) =>
        InfrastructureIdentity.KindOfType(type).Should().Be(InfrastructureResourceKind.Unknown, "an access rule, workbook or web test is not the resource it configures");

    [Fact]
    public void AnAuthorizationRule_IsNotANamespaceCandidate_SoItCannotCreateAFalseConflict()
    {
        const string withRule = Main + """

            resource "azurerm_eventhub_namespace_authorization_rule" "send" {
              name         = "orders-send"
              namespace_id = azurerm_eventhub_namespace.orders.id
            }
            """;
        var evidence = Analyze(("Acme.Orders.sln", ""), ("infra/main.tf", withRule), ("infra/dev.tfvars", "env = \"dev\"\n"));
        var snapshot = new IqrSourceSnapshot { Id = evidence.SourceSnapshotId, Archive = new("orders-infra.zip", evidence.SourceFingerprint, 3), EvidenceDomains = evidence };
        var ns = IntegrationTerraformComparisons.Compare(Catalog("evhns-orders-dev"), [snapshot], "Orders DEV", sourceAnalysisEnabled: true).Values.Single(v => v.Comparison.Field == "Event Hubs namespace").Comparison;
        ns.State.Should().Be(SourceComparisonState.Matches);
        ns.Candidates.Select(c => c.Name).Should().NotContain("orders-send");
        InfrastructureIdentity.KindOfType("azurerm_eventhub_namespace").Should().Be(InfrastructureResourceKind.EventHubNamespace);
        InfrastructureIdentity.KindOfType("azurerm_servicebus_topic").Should().Be(InfrastructureResourceKind.ServiceBusTopic);
        InfrastructureIdentity.KindOfType("azurerm_application_insights").Should().Be(InfrastructureResourceKind.TelemetryComponent);
    }

    [Fact]
    public void WhenSourceAnalysisIsDisabled_TheComparisonSaysSo()
    {
        var result = IntegrationTerraformComparisons.Compare(Catalog("evhns-orders-dev"), [Terraform()], "Orders DEV", sourceAnalysisEnabled: false);
        result.SourceAnalysisEnabled.Should().BeFalse();
        result.TerraformAvailable.Should().BeFalse();
        result.Values.Should().BeEmpty();
    }
}
