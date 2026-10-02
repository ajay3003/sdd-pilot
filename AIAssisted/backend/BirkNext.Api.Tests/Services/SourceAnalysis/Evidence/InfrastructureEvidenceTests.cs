using System.Text.Json;
using BirkNext.Api.Services.SourceAnalysis.Evidence;
using BirkNext.SourceArchitecture;
using BirkNext.SourceDomains;
using FluentAssertions;
using static BirkNext.Api.Tests.Services.SourceAnalysis.Evidence.SourceEvidenceFixtures;

namespace BirkNext.Api.Tests.Services.SourceAnalysis.Evidence;

/// <summary>Source Analysis → Infrastructure as Code: static Terraform reading, provider-neutral categories, honest Partial/Unsupported states and
/// redaction. Every resource stays "declared in source" — never existence, effective permission, reachability or delivery.</summary>
public sealed class InfrastructureEvidenceTests
{
    private static InfrastructureEvidence Acme() => Analyze(SourceEvidenceFixtures.Acme()).Infrastructure;

    [Fact]
    public void Terraform_resources_are_read_with_provider_neutral_categories_and_resolved_names()
    {
        var infra = Acme();
        infra.Formats.Should().Equal(InfrastructureFormat.Terraform);
        infra.Resources.Should().Contain(r => r.Id == "Infrastructure/azurerm_servicebus_topic.orders_created" && r.Category == InfrastructureCategory.Messaging
            && r.CategoryDetail == "Service Bus topic" && r.DeclaredName == "orders-created");
        // "${var.prefix}" resolves through the variable's literal default only (static resolution, no tfvars applied).
        infra.Resources.Single(r => r.ResourceType == "azurerm_linux_web_app").DeclaredName.Should().Be("app-acme-ordering-api");
        infra.Resources.Single(r => r.ResourceType == "azurerm_postgresql_flexible_server").Category.Should().Be(InfrastructureCategory.Database);
        infra.Resources.Single(r => r.ResourceType == "azurerm_application_insights").Category.Should().Be(InfrastructureCategory.Observability);
        infra.Resources.Single(r => r.ResourceType == "azurerm_user_assigned_identity").Category.Should().Be(InfrastructureCategory.Identity);
        infra.Resources.Single(r => r.ResourceType == "azurerm_private_endpoint").Category.Should().Be(InfrastructureCategory.Networking);
        infra.Resources.Single(r => r.ResourceType == "azurerm_client_config").Kind.Should().Be("data");
        infra.Resources.Where(r => r.Kind == "resource").Should().OnlyContain(r => r.RuntimeState == SourceDomainText.DeclaredNotVerified);
        infra.Resources.Single(r => r.ResourceType == "azurerm_resource_group").TagKeys.Should().BeEquivalentTo(["owner", "cost_center"]);
    }

    [Fact]
    public void References_depends_on_and_module_wiring_are_static_dependencies()
    {
        var infra = Acme();
        infra.Dependencies.Should().Contain(d => d.FromId == "Infrastructure/azurerm_servicebus_subscription.fulfilment" && d.ToId == "Infrastructure/azurerm_servicebus_topic.orders_created" && d.Kind == "depends_on");
        infra.Dependencies.Should().Contain(d => d.FromId == "Infrastructure/azurerm_linux_web_app.api" && d.ToId == "Infrastructure/azurerm_application_insights.main" && d.Kind == "reference");
        infra.Dependencies.Should().Contain(d => d.FromId == "Infrastructure/azurerm_private_endpoint.db" && d.ToId == "Infrastructure/module.network");
        infra.Dependencies.Should().Contain(d => d.Kind == "module-wiring" && d.FromId == "Infrastructure/modules/network/azurerm_virtual_network.vnet" && d.ToId == "Infrastructure/azurerm_resource_group.main"
            && d.State == ArchitectureEvidenceState.StronglySupported);
        infra.Resources.Single(r => r.ResourceType == "azurerm_subnet").ModulePath.Should().Be("Infrastructure/modules/network");
    }

    [Fact]
    public void Local_modules_are_analyzed_and_external_modules_are_never_fetched()
    {
        var infra = Acme();
        infra.Modules.Single(m => m.Name == "network").Should().Match<InfrastructureModule>(m => m.Local && m.Analyzed && m.ResolvedPath == "Infrastructure/modules/network");
        var external = infra.Modules.Single(m => m.Name == "naming");
        external.Local.Should().BeFalse();
        external.Analyzed.Should().BeFalse();
        external.Note.Should().Contain("never fetched");
        infra.Status.Should().Be(SourceDomainStatus.Partial, "an external module could not be analyzed");
    }

    [Fact]
    public void Identity_network_and_security_settings_are_declarations_not_effective_state()
    {
        var infra = Acme();
        var assignment = infra.AccessAssignments.Single();
        assignment.Role.Should().Be("Azure Service Bus Data Sender");
        assignment.ScopeReference.Should().Be("Infrastructure/azurerm_servicebus_namespace.bus");
        assignment.PrincipalReference.Should().Be("Infrastructure/azurerm_user_assigned_identity.api");
        assignment.RuntimeState.Should().Be("Effective permission not verified");
        var app = infra.Resources.Single(r => r.ResourceType == "azurerm_linux_web_app");
        app.Settings.Should().Contain(s => s.Key == "public_network_access_enabled" && s.Value == "false" && s.Area == "Security");
        app.Settings.Should().Contain(s => s.Key == "site_config.minimum_tls_version" && s.Value == "1.2");
        app.Settings.Should().Contain(s => s.Key == "identity.type" && s.Value == "UserAssigned" && s.Area == "Identity");
        infra.Resources.Single(r => r.ResourceType == "azurerm_private_endpoint").Settings
            .Should().Contain(s => s.Key == "private_service_connection.private_connection_resource_id" && s.Value == "→ Infrastructure/azurerm_postgresql_flexible_server.db");
        infra.Resources.Single(r => r.ResourceType == "azurerm_servicebus_namespace").Settings.Should().Contain(s => s.Key == "local_auth_enabled" && s.Value == "false");
    }

    [Fact]
    public void Variables_outputs_providers_and_backend_never_expose_secret_values()
    {
        var infra = Acme();
        infra.Variables.Single(v => v.Name == "client_secret").Should().Match<InfrastructureVariable>(v => v.Sensitive && v.DefaultState == "redacted" && v.SafeDefault == null);
        infra.Variables.Single(v => v.Name == "db_password").DefaultState.Should().Be("redacted", "a password-named variable is sensitive even without sensitive = true");
        infra.Resources.Single(r => r.ResourceType == "azurerm_postgresql_flexible_server").Settings.Should().Contain(s => s.Key == "administrator_password" && s.Value.Contains("sensitive"));
        infra.Outputs.Single(o => o.Name == "db_admin_password").Sensitive.Should().BeTrue();
        infra.Providers.Single(p => p.Name == "azurerm").Should().Match<InfrastructureProvider>(p => p.HasCredentialSettings && p.Source == "hashicorp/azurerm" && p.Version == "~> 3.100");
        infra.Backends.Single().Should().Match<InfrastructureBackend>(b => b.Type == "azurerm" && b.SettingNames.Contains("access_key"));
        JsonSerializer.Serialize(infra).Should().NotContain(Sentinel);
    }

    [Fact]
    public void Tfvars_bind_to_the_root_module_per_environment_and_redact_sensitive_values()
    {
        var infra = Acme();
        var prefix = infra.Variables.Single(v => v.Name == "prefix" && v.File == "Infrastructure/main.tf");
        prefix.Values.Should().Contain(v => v.Environment.Kind == SourceEnvironmentKind.QA && v.SafeValue == "acme-qa");
        prefix.Values.Should().Contain(v => v.Environment.Kind == SourceEnvironmentKind.Production && v.SafeValue == "acme-prod");
        infra.Variables.Single(v => v.Name == "prefix" && v.File.Contains("modules")).Values.Should().BeEmpty("tfvars set the root module beside them, not a child module");
        infra.Variables.Single(v => v.Name == "db_password").Values.Single().Should().Match<InfrastructureVariableValue>(v => v.State == "redacted" && v.SafeValue == null);
        infra.Environments.Select(e => e.Label.Kind).Should().BeEquivalentTo([SourceEnvironmentKind.QA, SourceEnvironmentKind.Production]);
    }

    [Fact]
    public void A_non_Azure_fixture_works_and_an_unknown_provider_is_reported_not_guessed()
    {
        var infra = Analyze(AwsKubernetes()).Infrastructure;
        infra.Formats.Should().BeEquivalentTo([InfrastructureFormat.Terraform, InfrastructureFormat.Kubernetes]);
        infra.Resources.Should().Contain(r => r.ResourceType == "aws_sqs_queue" && r.Category == InfrastructureCategory.Messaging && r.DeclaredName == "orders");
        infra.Resources.Should().Contain(r => r.ResourceType == "aws_s3_bucket" && r.Category == InfrastructureCategory.Storage && r.DeclaredName == "acme-invoices");
        infra.AccessAssignments.Should().Contain(a => a.Role == "arn:aws:iam::aws:policy/AmazonSQSFullAccess" && a.PrincipalReference == "infra/aws_iam_role.worker");
        infra.Resources.Single(r => r.ResourceType == "exoticcloud_widget").Category.Should().Be(InfrastructureCategory.Other);
        infra.Diagnostics.Should().Contain(d => d.Kind == "Unsupported provider" && d.Message.Contains("exoticcloud"));
        infra.Resources.Should().Contain(r => r.Format == InfrastructureFormat.Kubernetes && r.ResourceType == "Deployment" && r.Environment!.Kind == SourceEnvironmentKind.Production);
        infra.Dependencies.Should().Contain(d => d.Kind == "envFrom" && d.ToId.EndsWith("ConfigMap/orders-config"));
        infra.Resources.Single(r => r.ResourceType == "Secret").Settings.Single().Value.Should().Contain("values not read");
        infra.Status.Should().Be(SourceDomainStatus.Partial, "Kubernetes manifests are read heuristically");
        JsonSerializer.Serialize(infra).Should().NotContain(Sentinel).And.NotContain("U0VDUkVU");
    }

    [Fact]
    public void Bicep_is_partial_and_only_unsupported_formats_are_unsupported_not_failed()
    {
        var bicep = Analyze(("main.bicep", """
            @secure()
            param adminPassword string = 'SECRET_SENTINEL_BICEP'
            resource bus 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
              name: 'sb-shop'
              properties: {
                publicNetworkAccess: 'Disabled'
                minimumTlsVersion: '1.2'
              }
            }
            resource topic 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' = {
              parent: bus
              name: 'orders'
            }
            """)).Infrastructure;
        bicep.Status.Should().Be(SourceDomainStatus.Partial);
        bicep.Resources.Should().Contain(r => r.CategoryDetail == "Service Bus topic" && r.DeclaredName == "orders");
        bicep.Resources.Single(r => r.DeclaredName == "sb-shop").Settings.Should().Contain(s => s.Key == "public_network_access" && s.Value == "Disabled");
        bicep.Dependencies.Should().Contain(d => d.Kind == "parent");
        bicep.Variables.Single().Should().Match<InfrastructureVariable>(v => v.Sensitive && v.DefaultState == "redacted");
        JsonSerializer.Serialize(bicep).Should().NotContain(Sentinel);

        var pulumi = Analyze(("Pulumi.yaml", "name: shop\nruntime: dotnet\n")).Infrastructure;
        pulumi.Status.Should().Be(SourceDomainStatus.Unsupported);
        pulumi.Status.Should().NotBe(SourceDomainStatus.FailedAnalysis);
    }

    [Fact]
    public void No_iac_is_not_detected_never_failed()
    {
        var none = Analyze(("src/App/App.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>""")).Infrastructure;
        none.Status.Should().Be(SourceDomainStatus.NotDetected);
        none.StatusReason.Should().Contain("No supported Infrastructure as Code");
        none.Resources.Should().BeEmpty();
    }

    [Fact]
    public void Hcl_reader_handles_heredocs_comments_and_nested_interpolation_without_evaluating()
    {
        var root = HclReader.Parse("""
            # comment
            resource "a_b" "c" {
              /* block comment */
              policy = <<EOF
              { "secret": "SECRET_SENTINEL_HEREDOC" }
            EOF
              name = "x-${lower(var.y)}" // trailing
              list = [
                "one", # inline
                "two",
              ]
            }
            """, out var errors);
        errors.Should().BeEmpty();
        var block = root.Blocks.Single();
        block.Attributes.Select(a => a.Name).Should().Equal("policy", "name", "list");
        HclReader.IsHeredoc(block.Attribute("policy")!.Expression).Should().BeTrue();
        HclReader.Literal(block.Attribute("name")!.Expression).Should().BeNull("interpolation is not evaluated");
        HclReader.References(block.Attribute("name")!.Expression).Should().Contain("var.y");
    }
}
