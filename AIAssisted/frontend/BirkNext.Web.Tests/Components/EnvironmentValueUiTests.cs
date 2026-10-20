using System.Text.RegularExpressions;
using BirkNext.SourceDomains;
using BirkNext.Web.Components.SourceDomains;
using BirkNext.Web.Services;
using BirkNext.Web.Services.ProjectImport;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Source Analysis → Configuration → Environment values shows exactly why a Target Environment of a kind is offered a value: environment,
/// component, value-for, value, artifact and state; a conflict is per environment + component + key, never across components. Import Project
/// lists the environment labels an import's configuration declares. No real environment host is written into shared UI or contracts.
/// </summary>
public sealed class EnvironmentValueUiTests : BunitContext
{
    private static ConfigurationEntry Entry(string key, string value, string env, string component, string file) => new()
    {
        Key = key, NormalizedKey = key.ToLowerInvariant(), ValuePreviewSafe = value, ValueKind = ConfigurationValueKind.Url,
        Environment = SourceEnvironments.Normalize(env), Component = component, File = file, Line = 2, Category = ConfigurationCategory.Api,
    };

    private static SourceEvidenceDomainsSnapshot Evidence(params ConfigurationEntry[] entries) => new()
    {
        SourceSnapshotId = Guid.NewGuid(),
        Configuration = new ConfigurationEvidence
        {
            Domain = SourceEvidenceDomain.Configuration, Status = SourceDomainStatus.Complete, Entries = [.. entries],
            Files = [.. entries.Select(e => e.File).Distinct().Select(f => new ConfigurationFile { Path = f, Environment = entries.First(e => e.File == f).Environment, EnvironmentBasis = "file-name suffix" })],
            Environments = [.. entries.Select(e => e.Environment).Distinct()],
        },
    };

    [Fact]
    public void EnvironmentValues_ListEachEnvironmentComponentAndKey_WithItsArtifact_AndConflictsOnlyWithinOneComponent()
    {
        var evidence = Evidence(
            Entry("Cors:AllowedOrigins:0", "https://app-dev.example.test", "Development", "Gateway.Api", "gateway/appsettings.Development.json"),
            Entry("Cors:AllowedOrigins:0", "https://app-qa.example.test", "Qa", "Gateway.Api", "gateway/appsettings.Qa.json"),
            Entry("Cors:AllowedOrigins:1", "https://app-qa-other.example.test", "Qa", "Gateway.Api", "gateway/appsettings.Qa.json"),
            Entry("ApiBaseUrl", "https://orders-qa.example.test/api", "Qa", "Orders.Web", "orders/appsettings.Qa.json"),
            Entry("ApiBaseUrl", "https://billing-qa.example.test/api", "Qa", "Billing.Web", "billing/appsettings.Qa.json"),
            Entry("ApiBaseUrl", "http://localhost:7112/api", "Development", "Orders.Web", "orders/appsettings.Development.json"));
        var cut = Render<ConfigurationWorkspace>(p => p.Add(x => x.Evidence, evidence));
        cut.FindAll(".sa-subnav-item").Single(b => b.TextContent == "Environment values").Click();

        var rows = cut.FindAll("[data-testid=sd-config-envvalue]");
        rows.Select(r => (r.GetAttribute("data-environment"), r.QuerySelectorAll("td")[0].TextContent, r.QuerySelector("[data-testid=sd-config-envvalue-state]")!.TextContent))
            .Should().BeEquivalentTo([
                ("Development", "Gateway.Api", "Declared"),
                ("QA", "Billing.Web", "Declared"),
                ("QA", "Gateway.Api", "Review required"),
                ("QA", "Orders.Web", "Declared")], "different components are not a conflict; two QA origins of one component are");
        cut.Find("[data-testid=sd-config-envvalues]").TextContent.Should().Contain("appsettings.Qa.json (Cors:AllowedOrigins:0)").And.Contain("runtime not verified");
        cut.Markup.Should().NotContain("localhost", "a developer-machine URL is never offered for a deployed environment");
    }

    [Fact]
    public void ImportHistory_CarriesTheConfigurationEnvironments_OfEachSnapshot()
    {
        var snapshot = new SourceSetupSnapshotFixture().Make();
        var rows = ProjectImportHistory.Previous([], null, new BirkNext.Integrations.SourceSetupView { Snapshots = [snapshot] });
        rows.Single().ConfigurationEnvironments.Should().Equal("Development", "Local", "QA");
    }

    private sealed class SourceSetupSnapshotFixture
    {
        public BirkNext.Integrations.SourceSetupSnapshot Make() =>
            new(Guid.NewGuid(), "app.zip", new string('a', 64), DateTimeOffset.UtcNow, "Ready") { ConfigurationEnvironments = ["Development", "Local", "QA"] };
    }

    [Fact]
    public void NoRealEnvironmentHost_IsHardCodedInSharedConfigurationCode()
    {
        var web = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "BirkNext.Web");
        if (!Directory.Exists(web)) return; // Source-level check runs where the repository sources are present.
        var shared = Path.Combine(web, "..", "..", "shared");
        var files = new[]
        {
            Path.Combine(web, "Components", "FrontendAnalysisSettings.razor"), Path.Combine(web, "Components", "SourceDomains", "EnvironmentValueHint.razor"),
            Path.Combine(web, "Services", "EnvironmentValuePresentation.cs"), Path.Combine(web, "Components", "SourceDomains", "ConfigurationWorkspace.razor"),
            Path.Combine(shared, "SourceEnvironmentValueContracts.cs"),
        };
        foreach (var file in files)
            Regex.IsMatch(File.ReadAllText(file), @"bufetat|m2lbdev|m2lbqa|azurewebsites\.net|norwayeast", RegexOptions.IgnoreCase)
                .Should().BeFalse($"{Path.GetFileName(file)} must take environment values from saved configuration or source evidence, never from constants");
    }
}
