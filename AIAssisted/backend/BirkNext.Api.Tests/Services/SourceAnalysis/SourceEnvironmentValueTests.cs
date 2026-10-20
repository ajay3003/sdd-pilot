
using BirkNext.Integrations;
using BirkNext.SourceDomains;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.SourceAnalysis;

/// <summary>
/// Environment-specific source values for setup forms: a value is offered only for the target environment it is declared for (never another
/// environment, Local or Default), a saved value always wins, several different values need review (never "newest wins"), values of different
/// components are not a conflict for a component-scoped field, and secrets / placeholders / loopback URLs are never candidates. Generic hosts.
/// </summary>
public sealed class SourceEnvironmentValueTests
{
    private static ConfigurationEntry Entry(string key, string value, string env, string? component, ConfigurationValueKind kind = ConfigurationValueKind.Url,
        ConfigurationSensitivity sensitivity = ConfigurationSensitivity.None, string? file = null) => new()
    {
        Key = key, NormalizedKey = key.ToLowerInvariant(), ValuePreviewSafe = value, ValueKind = kind, Sensitivity = sensitivity,
        Environment = SourceEnvironments.Normalize(env), Component = component, File = file ?? $"{component}/appsettings.{env}.json", Line = 3,
    };

    private static IqrSourceSnapshot Snapshot(string archive, params ConfigurationEntry[] entries) => new()
    {
        Id = Guid.NewGuid(), Archive = new(archive, new string('a', 64), 1), AnalyzedAt = DateTimeOffset.UtcNow,
        EvidenceDomains = new SourceEvidenceDomainsSnapshot
        {
            Configuration = new ConfigurationEvidence
            {
                Domain = SourceEvidenceDomain.Configuration, Entries = [.. entries],
                Files = [.. entries.Select(e => e.File).Distinct().Select(f => new ConfigurationFile { Path = f, Environment = entries.First(e => e.File == f).Environment, EnvironmentBasis = "file-name suffix" })],
            },
        },
    };

    private static IReadOnlyList<EnvironmentValueCandidate> Candidates(params ConfigurationEntry[] entries) => SourceEnvironmentValueExtractor.Candidates([Snapshot("app.zip", entries)]);

    private static EnvironmentValueResolution Frontend(IReadOnlyList<EnvironmentValueCandidate> candidates, SourceEnvironmentKind? target, string? configured = null) =>
        EnvironmentValueResolver.Resolve(EnvironmentValueKeys.FrontendOrigin, configured, target, candidates);

    private static readonly ConfigurationEntry DevCors = Entry("Cors:AllowedOrigins:0", "https://app-dev.example.test", "Development", "Gateway.Api");
    private static readonly ConfigurationEntry QaCors = Entry("Cors:AllowedOrigins:0", "https://app-qa.example.test", "Qa", "Gateway.Api");

    [Fact]
    public void A_Development_ResolvesTheDevelopmentFrontendUrl_WithProvenance()
    {
        var resolution = Frontend(Candidates(DevCors), SourceEnvironmentKind.Development);
        resolution.State.Should().Be(EnvironmentValueState.SourceSuggested);
        resolution.Value.Should().Be("https://app-dev.example.test");
        var candidate = resolution.Candidates.Single();
        (candidate.Component, candidate.ConfigurationKey, candidate.File, candidate.ArchiveFileName).Should().Be(("Gateway.Api", "Cors:AllowedOrigins:0", "Gateway.Api/appsettings.Development.json", "app.zip"));
        candidate.Basis.Should().Contain("CORS");
        resolution.RuntimeState.Should().Be("Runtime not verified");
    }

    [Fact]
    public void B_C_EachEnvironmentGetsItsOwnValue()
    {
        var candidates = Candidates(DevCors, QaCors);
        Frontend(candidates, SourceEnvironmentKind.QA).Value.Should().Be("https://app-qa.example.test");
        Frontend(candidates, SourceEnvironmentKind.Development).Value.Should().Be("https://app-dev.example.test");
    }

    [Fact]
    public void D_E_AnEnvironmentWithoutItsOwnValue_StaysEmpty_NoFallback()
    {
        Frontend(Candidates(DevCors), SourceEnvironmentKind.QA).Should().Match<EnvironmentValueResolution>(r => r.State == EnvironmentValueState.NoValue && r.Value == null && r.Candidates.Count == 0);
        Frontend(Candidates(QaCors), SourceEnvironmentKind.Development).State.Should().Be(EnvironmentValueState.NoValue);
    }

    [Fact]
    public void F_G_ASavedValueAlwaysWins_AndADifferenceIsShownNotApplied()
    {
        var candidates = Candidates(DevCors, QaCors);
        var dev = Frontend(candidates, SourceEnvironmentKind.Development, configured: "https://custom-dev.example.test/");
        (dev.State, dev.Value).Should().Be((EnvironmentValueState.ConfiguredDiffersFromSource, "https://custom-dev.example.test/"));
        dev.Candidates.Single().Value.Should().Be("https://app-dev.example.test");
        var qa = Frontend(candidates, SourceEnvironmentKind.QA, configured: "https://app-qa.example.test/");
        (qa.State, qa.Value).Should().Be((EnvironmentValueState.ConfiguredMatchesSource, "https://app-qa.example.test/"), "a trailing slash is the same URL");
    }

    [Fact]
    public void H_TwoDifferentQaValuesForOneField_AreReviewRequired_NeverNewestWins()
    {
        var older = Snapshot("app-v1.zip", Entry("Cors:AllowedOrigins:0", "https://app-qa.example.test", "Qa", "Gateway.Api"));
        var newer = Snapshot("app-v2.zip", Entry("Cors:AllowedOrigins:0", "https://app-qa2.example.test", "Qa", "Gateway.Api")) with { AnalyzedAt = DateTimeOffset.UtcNow.AddDays(1) };
        var resolution = Frontend(SourceEnvironmentValueExtractor.Candidates([older, newer]), SourceEnvironmentKind.QA);
        resolution.State.Should().Be(EnvironmentValueState.ReviewRequired);
        resolution.Value.Should().BeNull();
        resolution.Candidates.Select(c => c.ArchiveFileName).Should().BeEquivalentTo(["app-v1.zip", "app-v2.zip"]);
    }

    [Fact]
    public void I_DifferentComponentsWithDifferentUrls_AreNotAConflict_ForAComponentScopedField()
    {
        var candidates = Candidates(Entry("Orders:ApiBaseUrl", "https://orders-qa.example.test/api", "Qa", "Orders.Web"),
            Entry("Billing:ApiBaseUrl", "https://billing-qa.example.test/api", "Qa", "Billing.Web"));
        EnvironmentValueResolver.Resolve(EnvironmentValueKeys.ApiBaseUrl, null, SourceEnvironmentKind.QA, candidates, component: "Orders.Web").Value.Should().Be("https://orders-qa.example.test/api");
        EnvironmentValueResolver.Resolve(EnvironmentValueKeys.ApiBaseUrl, null, SourceEnvironmentKind.QA, candidates, component: "Billing.Web").Value.Should().Be("https://billing-qa.example.test/api");
        EnvironmentValueResolver.Resolve(EnvironmentValueKeys.ApiBaseUrl, null, SourceEnvironmentKind.QA, candidates).State.Should().Be(EnvironmentValueState.ReviewRequired,
            "one target-wide field cannot take two components' URLs");
    }

    [Fact]
    public void J_K_LocalAndDefaultValues_NeverFillDevelopmentOrQa()
    {
        var candidates = Candidates(Entry("Cors:AllowedOrigins:0", "https://app-local.example.test", "Local", "Gateway.Api"),
            Entry("Cors:AllowedOrigins:0", "https://app.example.test", "", "Gateway.Api", file: "Gateway.Api/appsettings.json"));
        candidates.Select(c => c.Environment.Kind).Should().BeEquivalentTo([SourceEnvironmentKind.Local, SourceEnvironmentKind.Default], "both are listed as evidence");
        foreach (var target in new SourceEnvironmentKind?[] { SourceEnvironmentKind.Development, SourceEnvironmentKind.QA, SourceEnvironmentKind.Default, null })
            Frontend(candidates, target).State.Should().Be(EnvironmentValueState.NoValue);
    }

    [Fact]
    public void L_SecretsPlaceholdersAndConnectionStrings_AreNeverCandidates()
    {
        Candidates(
            Entry("Cors:AllowedOrigins:0", "https://app-dev.example.test", "Development", "Gateway.Api", sensitivity: ConfigurationSensitivity.Sensitive),
            Entry("ServiceBus:FullyQualifiedNamespace", "Endpoint=sb://bus.example.test/;SharedAccessKey=x", "Development", "Worker", ConfigurationValueKind.ConnectionString),
            Entry("AzureAd:Authority", "https://login.example.test/${TENANT}", "Development", "Web", ConfigurationValueKind.Url),
            Entry("Cors:AllowedOrigins:0", "#{origin}#", "Development", "Gateway.Api", ConfigurationValueKind.Placeholder),
            Entry("AzureAd:ClientSecret", "abc", "Development", "Web", ConfigurationValueKind.Secret)).Should().BeEmpty();
    }

    [Fact]
    public void M_SafeUrls_AreCandidates_ButLoopbackUrlsAreNever()
    {
        var candidates = Candidates(Entry("ApiBaseUrl", "http://localhost:7112/api", "Development", "Web"),
            Entry("ApiBaseUrl", "https://app-dev.example.test/api", "Development", "Web", file: "Web/appsettings.Dev.json"),
            Entry("AzureAd:RedirectUri", "http://localhost:7284/authentication/login-callback", "Development", "Web"));
        candidates.Should().ContainSingle().Which.Value.Should().Be("https://app-dev.example.test/api", "a developer-machine URL is not the deployed Development value");
        EnvironmentValueResolver.Resolve(EnvironmentValueKeys.ApiBaseUrl, null, SourceEnvironmentKind.Development, candidates).State.Should().Be(EnvironmentValueState.SourceSuggested);
    }

    [Fact]
    public void AuthoritySemanticKeys_GiveAuthorityAndTenant_AndMessagingKeysAreComponentScoped()
    {
        var candidates = Candidates(Entry("AzureAd:Authority", "https://login.example.test/0b8f3c1e-1111-4a2b-9c3d-123456789abc", "Qa", "Web"),
            Entry("ServiceBus:FQDN", "sb-app-qa.servicebus.example.test", "Qa", "Worker", ConfigurationValueKind.EntityName),
            Entry("EventHub:EventHubNames:0", "cdc-qa.db.orders", "Qa", "Worker", ConfigurationValueKind.EntityName),
            Entry("EventHub:ConsumerGroup", "orders-worker", "Qa", "Worker", ConfigurationValueKind.EntityName));
        candidates.Select(c => (c.SemanticKey, c.Value)).Should().BeEquivalentTo([
            (EnvironmentValueKeys.AuthenticationAuthority, "https://login.example.test/0b8f3c1e-1111-4a2b-9c3d-123456789abc"),
            (EnvironmentValueKeys.AuthenticationTenant, "0b8f3c1e-1111-4a2b-9c3d-123456789abc"),
            (EnvironmentValueKeys.ServiceBusNamespace, "sb-app-qa.servicebus.example.test"),
            (EnvironmentValueKeys.EventHubName, "cdc-qa.db.orders")], "a consumer group is never an Event Hub name, and no $Default is assumed");
    }

    [Theory]
    [InlineData("Development", null, SourceEnvironmentKind.Development)]
    [InlineData("QA", null, SourceEnvironmentKind.QA)]
    [InlineData("Production", null, SourceEnvironmentKind.Production)]
    [InlineData("Custom", "Staging", SourceEnvironmentKind.Staging)]
    [InlineData("RC", "release candidate", null)]
    [InlineData("Custom", "Local docker", null)]
    [InlineData("Custom", "Sandbox", null)]
    public void TargetKind_UsesTheExplicitType_ThenOnlyAnEnvironmentTheNameStates(string type, string? name, SourceEnvironmentKind? expected) =>
        EnvironmentValueResolver.TargetKind(type, name).Should().Be(expected);

    [Fact]
    public void UnknownEnvironment_BorrowsNothing()
    {
        var candidates = Candidates(DevCors, QaCors);
        Frontend(candidates, EnvironmentValueResolver.TargetKind("Custom", "Sandbox")).State.Should().Be(EnvironmentValueState.NoValue);
        Frontend(candidates, SourceEnvironmentKind.Staging).State.Should().Be(EnvironmentValueState.NoValue);
    }

    [Fact]
    public void SnapshotEnvironments_AreListed_WithoutDefault()
    {
        var snapshot = Snapshot("app.zip", DevCors, QaCors, Entry("Cors:AllowedOrigins:0", "https://x.example.test", "Local", "Gateway.Api"),
            Entry("Logging:Level", "Information", "", "Gateway.Api", ConfigurationValueKind.Text, file: "Gateway.Api/appsettings.json"));
        SourceEnvironmentValueExtractor.Environments(snapshot).Should().Equal("Development", "Local", "QA");
    }
}
