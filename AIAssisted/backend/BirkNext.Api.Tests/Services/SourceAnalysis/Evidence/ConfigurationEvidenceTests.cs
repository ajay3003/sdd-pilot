using System.Text.Json;
using BirkNext.SourceDomains;
using FluentAssertions;
using static BirkNext.Api.Tests.Services.SourceAnalysis.Evidence.SourceEvidenceFixtures;

namespace BirkNext.Api.Tests.Services.SourceAnalysis.Evidence;

/// <summary>Source Analysis → Configuration: one entry model across formats, categories, environment awareness, value-kind classification
/// with redaction, and conflicts only within one runtime's layered sources for the SAME environment.</summary>
public sealed class ConfigurationEvidenceTests
{
    private static ConfigurationEvidence Acme() => Analyze(SourceEvidenceFixtures.Acme()).Configuration;

    private static ConfigurationEntry Entry(ConfigurationEvidence c, string file, string key) => c.Entries.Single(e => e.File.EndsWith(file, StringComparison.Ordinal) && e.Key == key);

    [Fact]
    public void Entries_are_categorized_with_safe_previews_for_non_sensitive_values_only()
    {
        var c = Acme();
        Entry(c, "Web/appsettings.json", "AzureAd:ClientId").Should().Match<ConfigurationEntry>(e => e.Category == ConfigurationCategory.Authentication && e.ValueKind == ConfigurationValueKind.Identifier
            && e.ValuePreviewSafe == "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee" && e.Component == "Web");
        Entry(c, "Web/appsettings.json", "ServiceBus:Topic").Should().Match<ConfigurationEntry>(e => e.Category == ConfigurationCategory.Messaging && e.ValueKind == ConfigurationValueKind.EntityName && e.ValuePreviewSafe == "orders-created");
        Entry(c, "Web/appsettings.json", "Payments:BaseUrl").Should().Match<ConfigurationEntry>(e => e.ValueKind == ConfigurationValueKind.Url && e.ValuePreviewSafe == "https://payments.example.com/v2");
        Entry(c, "Web/appsettings.json", "FeatureManagement:NewCheckout").Category.Should().Be(ConfigurationCategory.FeatureFlags);
        Entry(c, "Web/appsettings.json", "Logging:LogLevel:Default").Category.Should().Be(ConfigurationCategory.Observability);
    }

    [Fact]
    public void Secrets_connection_strings_and_secret_references_are_classified_never_stored()
    {
        var c = Acme();
        Entry(c, "Web/appsettings.json", "ConnectionStrings:Orders").Should().Match<ConfigurationEntry>(e => e.ValueKind == ConfigurationValueKind.ConnectionString
            && e.Sensitivity == ConfigurationSensitivity.Sensitive && e.ValuePreviewSafe == null);
        // The host of a connection string is an address (kept as a reference for cross-domain linking), not its credentials.
        Entry(c, "Web/appsettings.json", "ConnectionStrings:Orders").References.Should().Equal("host:psql-acme-ordering.postgres.database.azure.com");
        Entry(c, "Web/appsettings.json", "Payments:ApiKey").Should().Match<ConfigurationEntry>(e => e.ValueKind == ConfigurationValueKind.Secret && e.ValuePreviewSafe == null);
        Entry(c, "Web/appsettings.json", "KeyVaultSecret").Should().Match<ConfigurationEntry>(e => e.ValueKind == ConfigurationValueKind.SecretReference && e.ValuePreviewSafe == "Key Vault reference");
        Entry(c, "qa.tfvars", "db_password").Should().Match<ConfigurationEntry>(e => e.Sensitivity == ConfigurationSensitivity.Sensitive && e.ValuePreviewSafe == null);
        var json = JsonSerializer.Serialize(c);
        json.Should().NotContain(Sentinel).And.NotContain("orderadmin;Password");
    }

    [Fact]
    public void Environments_come_from_file_names_and_launch_profiles_and_are_variants_not_conflicts()
    {
        var c = Acme();
        Entry(c, "appsettings.QA.json", "Logging:LogLevel:Default").Environment.Should().Be(new SourceEnvironmentLabel(SourceEnvironmentKind.QA, "QA"));
        Entry(c, "appsettings.Production.json", "Logging:LogLevel:Default").ValuePreviewSafe.Should().Be("Warning");
        Entry(c, "launchSettings.json", "AzureAd:ClientId").Environment.Kind.Should().Be(SourceEnvironmentKind.Development);
        c.Files.Single(f => f.Path.EndsWith("launchSettings.json")).Technology.Should().Be("launchSettings");
        c.Files.Single(f => f.Path.EndsWith("appsettings.QA.json")).EnvironmentBasis.Should().Be("file-name suffix");
        // Information / Debug / Warning for the same key in three environments: three variants, zero conflicts.
        c.Conflicts.Should().BeEmpty();
        c.Environments.Select(e => e.Kind).Should().Contain([SourceEnvironmentKind.Default, SourceEnvironmentKind.Development, SourceEnvironmentKind.QA, SourceEnvironmentKind.Production]);
    }

    [Fact]
    public void Same_environment_same_key_different_values_in_layered_sources_is_a_potential_conflict()
    {
        var c = Analyze(
            ("src/Shop.Api/Shop.Api.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>"""),
            ("src/Shop.Api/appsettings.json", """{ "Orders": { "Topic": "orders-v1" } }"""),
            ("src/Shop.Api/.env", "Orders__Topic=orders-v2\nAPI_TOKEN=SECRET_SENTINEL_ENV_TOKEN\n"),
            ("clients/a/settings.json", """{ "Name": "a" }"""),
            ("clients/b/settings.json", """{ "Name": "b" }""")).Configuration;
        var conflict = c.Conflicts.Single();
        conflict.NormalizedKey.Should().Be("orders:topic");
        conflict.Files.Should().BeEquivalentTo(["src/Shop.Api/.env", "src/Shop.Api/appsettings.json"]);
        conflict.Detail.Should().Contain("values not shown");
        c.Entries.Single(e => e.Key == "API_TOKEN").Sensitivity.Should().Be(ConfigurationSensitivity.Sensitive);
        JsonSerializer.Serialize(c).Should().NotContain(Sentinel).And.NotContain("orders-v2\",\"orders-v1");
    }

    [Fact]
    public void Pipeline_variables_and_ConfigMaps_reach_configuration_without_a_second_parse()
    {
        var acme = Acme();
        Entry(acme, ".azure-pipelines.yml", "sqlHost").Should().Match<ConfigurationEntry>(e => e.Technology == "Azure Pipelines variables" && e.References.Contains("host:psql-acme-ordering.postgres.database.azure.com"));
        var k8s = Analyze(AwsKubernetes()).Configuration;
        k8s.Entries.Single(e => e.Key == "QUEUE_NAME").Should().Match<ConfigurationEntry>(e => e.Technology == "Kubernetes ConfigMap" && e.Environment.Kind == SourceEnvironmentKind.Production);
        k8s.Entries.Single(e => e.Key == "DB_PASSWORD").ValuePreviewSafe.Should().BeNull();
        JsonSerializer.Serialize(k8s).Should().NotContain(Sentinel);
    }

    [Fact]
    public void In_memory_configuration_model_keeps_raw_values_for_ingestion_time_consumers_only()
    {
        var evidence = Analyze(out var model, SourceEvidenceFixtures.Acme());
        model.Values.Should().Contain(v => v.Key == "AzureAd:ClientId" && v.Raw == "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee" && v.ProjectPath == "src/Acme.Ordering.Web/Acme.Ordering.Web.csproj");
        model.Values.Should().Contain(v => v.Raw.Contains(Sentinel), "the raw model is in memory for ingestion only");
        JsonSerializer.Serialize(evidence).Should().NotContain(Sentinel, "the persisted evidence never holds raw sensitive values");
    }

    [Fact]
    public void No_configuration_is_not_detected()
    {
        var c = Analyze(("README.txt", "hello")).Configuration;
        c.Status.Should().Be(SourceDomainStatus.NotDetected);
    }
}
