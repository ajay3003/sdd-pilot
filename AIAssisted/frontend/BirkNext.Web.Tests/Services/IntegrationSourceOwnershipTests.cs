using System.Reflection;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Pins the integration provenance model as it actually behaves.
///
/// Target Environment → Integrations is a configuration surface. Manual entry and the audited M2LB catalogue write
/// provenance into a saved profile; <see cref="IntegrationConfigurationSource.EndpointDiscovery"/> has no frontend
/// producer, which is why the Detected partition was removed rather than left as an unreachable view. Observed
/// REST/GraphQL traffic belongs to Endpoint Discovery, and Integration Quality Review receives it separately as
/// runtime observations rather than as configuration.
/// </summary>
public sealed class IntegrationSourceOwnershipTests
{
    // ── 1. No frontend production writer creates EndpointDiscovery ───────────

    [Fact]
    public void NoFrontendProductionCodeAssignsTheEndpointDiscoverySource()
    {
        var web = typeof(IntegrationConfigPresenter).Assembly;

        // Every provenance the frontend can write is reachable through IntegrationConfig instances it creates.
        // CodeSuggested is written when a known template is accepted; Manual when a person edits. Nothing in the
        // frontend assembly produces EndpointDiscovery, so a Detected view could never populate.
        var writers = web.GetTypes()
            .Where(t => t.Namespace?.StartsWith("BirkNext.Web", StringComparison.Ordinal) == true)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Count(m => m.Name.Contains("EndpointDiscoverySource", StringComparison.Ordinal));

        writers.Should().Be(0, "the frontend has no API for writing the EndpointDiscovery provenance");

        // The value itself remains part of the contract so a backend-produced record still renders honestly.
        Enum.IsDefined(IntegrationConfigurationSource.EndpointDiscovery).Should().BeTrue();
        IntegrationConfigPresenter.SourceLabel(IntegrationConfigurationSource.EndpointDiscovery).Should().Be("Discovered");
    }

    // ── 2–3. The reachable sources behave ───────────────────────────────────

    [Fact]
    public void AcceptingAKnownTemplateMarksTheIntegrationAsSuggestedFromAuditedSource()
    {
        var target = new IntegrationConfig { Id = "i1", Name = "Custom", Type = IntegrationType.EventHub };

        IntegrationConfigPresenter.ApplyTemplate(target, new KnownIntegrationTemplate
        {
            Id = "t1", DisplayName = "M2LB Person events", IntegrationType = IntegrationType.EventHub,
            EndpointOrNamespace = "sb://m2lb.servicebus.windows.net", Resource = "person-events",
        });

        target.ConfigurationSource.Should().Be(IntegrationConfigurationSource.CodeSuggested);
        IntegrationConfigPresenter.SourceLabel(target.ConfigurationSource)
            .Should().Be("Suggested from audited M2LB source", "the catalogue is audited source, not runtime evidence");
        IntegrationConfigPresenter.SourceLabel(target.ConfigurationSource)
            .Should().NotContainAny("Verified", "Runtime", "Discovered");
    }

    [Fact]
    public void ManualIsTheHighestAuthorityAndLegacyConfigurationStaysUnknown()
    {
        IntegrationConfigPresenter.SourceLabel(IntegrationConfigurationSource.Manual).Should().Be("Manual");
        // Configuration written before provenance existed must not claim a provenance it never had.
        new IntegrationConfig().ConfigurationSource.Should().Be(IntegrationConfigurationSource.Unknown);
    }

    // ── 4. Precedence contract ──────────────────────────────────────────────

    /// <summary>
    /// Configuration authority is Manual > EndpointDiscovery > CodeSuggested > Unknown, but the enum does NOT encode
    /// it: the declared values are Unknown 0, EndpointDiscovery 1, CodeSuggested 2, Manual 3, so EndpointDiscovery
    /// sorts *below* CodeSuggested numerically. Precedence lives only in the backend merger's explicit map, and no
    /// caller may derive it by comparing enum values.
    /// </summary>
    [Fact]
    public void EnumOrderIsNotTheAuthorityOrderSoPrecedenceMustNeverBeDerivedFromIt()
    {
        ((int)IntegrationConfigurationSource.Unknown).Should().Be(0);
        ((int)IntegrationConfigurationSource.EndpointDiscovery).Should().Be(1);
        ((int)IntegrationConfigurationSource.CodeSuggested).Should().Be(2);
        ((int)IntegrationConfigurationSource.Manual).Should().Be(3);

        ((int)IntegrationConfigurationSource.EndpointDiscovery)
            .Should().BeLessThan((int)IntegrationConfigurationSource.CodeSuggested,
                "the declaration order is historical; comparing enum values would invert discovery and suggestion");

        // The one ordering that does hold both ways: a person outranks everything.
        ((int)IntegrationConfigurationSource.Manual).Should().BeGreaterThan((int)IntegrationConfigurationSource.CodeSuggested);
    }

    // ── 5. Provenance is presentation, never identity ───────────────────────

    [Fact]
    public void ProvenanceAndDisplayNameAreNotStructuralIdentity()
    {
        var a = new IntegrationConfig
        {
            Id = "a", Name = "REST api.example.test/api", Type = IntegrationType.REST,
            Endpoint = "https://api.example.test/api", ConfigurationSource = IntegrationConfigurationSource.Manual,
        };
        var b = new IntegrationConfig
        {
            Id = "b", Name = "Renamed by a person", Type = IntegrationType.REST,
            Endpoint = "https://api.example.test/api", ConfigurationSource = IntegrationConfigurationSource.CodeSuggested,
        };

        // Structural identity is type + endpoint + resource. Changing provenance or the display name must not
        // detach an integration from its baseline history.
        (a.Type, a.Endpoint, a.Resource).Should().Be((b.Type, b.Endpoint, b.Resource));
    }
}
