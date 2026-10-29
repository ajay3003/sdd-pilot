using BirkNext.Api.Services.AltinnTesting;
using BirkNext.Integrations;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.AltinnTesting;

/// <summary>
/// Authentication per interaction: reporter → Altinn app (browser/OIDC), Altinn → integration consumer (the integration's own mechanism) and
/// BirkNext execution credentials are separate hops. A browser login never fills another hop; precedence and conflicts are explicit.
/// </summary>
public sealed class InteractionAuthenticationTests
{
    private static readonly AltinnBrowserAuthenticationObservation BrowserLogin = new()
    {
        Issuer = "https://test.idporten.no", ClientId = "altinn-portal", EvidenceSource = "Manual observation", ObservedAt = new DateTimeOffset(2026, 10, 29, 8, 0, 0, TimeSpan.Zero),
    };

    private static IntegrationDefinition Intake(IntegrationAuthMechanism producer = IntegrationAuthMechanism.NotConfigured, IntegrationAuthMechanism consumer = IntegrationAuthMechanism.NotConfigured) => new()
    {
        Id = "dev:http:report-intake", DisplayName = "Report intake", Kind = IntegrationKind.HttpApi, SourceSystem = "Altinn", DestinationSystem = "Meldingsutveksleren (MU)",
        ProducerAuthentication = producer, ConsumerAuthentication = consumer,
    };

    private static InteractionAuthentication Hop(IReadOnlyList<InteractionAuthentication> hops, string key) => hops.Single(h => h.Key == key);

    [Fact]
    public void ReporterAccess_IsBrowserOidc_WithIdPortenTest_AndOnlyAProposalUntilObserved()
    {
        var hops = AltinnInteractionAuthentication.Build(false, null, Intake(), null, AltinnReadinessState.Blocked);
        var reporter = Hop(hops, AltinnInteractionAuthentication.ReporterApp);

        reporter.Requirement.Requirement.Should().Be(InteractionAuthRequirement.BrowserOidc);
        reporter.Requirement.IsSuggestion.Should().BeTrue("a Test Pack proposal states the requirement, it is not configuration");
        reporter.Provider.Should().Be("ID-porten TEST / TestID");
        reporter.Runtime.Should().Be("Not observed");
        hops.Select(h => h.Title).Should().Equal("Reporter access", "Integration authentication", "BirkNext execution credentials");
    }

    [Fact]
    public void BrowserLogin_NeverPopulatesIntegrationAuthentication_OrExecutionCredentials()
    {
        var hops = AltinnInteractionAuthentication.Build(false, BrowserLogin, Intake(), null, AltinnReadinessState.Blocked);

        Hop(hops, AltinnInteractionAuthentication.ReporterApp).Runtime.Should().StartWith("Recorded manually");
        var integration = Hop(hops, AltinnInteractionAuthentication.AppIntegration);
        integration.Requirement.Requirement.Should().Be(InteractionAuthRequirement.Unknown, "ID-porten browser evidence never proves Altinn → MU authentication");
        integration.Requirement.Candidates.Should().BeEmpty();
        integration.Configuration.Should().StartWith("Not configured in Integration Catalog");
        integration.Provider.Should().BeNull();
        var execution = Hop(hops, AltinnInteractionAuthentication.Execution);
        execution.Configuration.Should().Be("Not configured", "browser auth evidence does not fill client id, secret or scopes");
        execution.Runtime.Should().Be("Not applicable until automated execution");
    }

    [Fact]
    public void IntegrationEvidence_NotRequired_And_Bearer_AreRepresented_IndependentlyOfBrowserAuth()
    {
        Hop(AltinnInteractionAuthentication.Build(false, BrowserLogin, Intake(IntegrationAuthMechanism.None), null, AltinnReadinessState.Blocked), AltinnInteractionAuthentication.AppIntegration)
            .Requirement.Requirement.Should().Be(InteractionAuthRequirement.NotRequired);
        var bearer = AltinnInteractionAuthentication.Build(false, BrowserLogin, Intake(IntegrationAuthMechanism.EntraIdClientCredentials), null, AltinnReadinessState.Blocked);
        Hop(bearer, AltinnInteractionAuthentication.AppIntegration).Requirement.Requirement.Should().Be(InteractionAuthRequirement.BearerOAuthApi);
        Hop(bearer, AltinnInteractionAuthentication.ReporterApp).Requirement.Requirement.Should().Be(InteractionAuthRequirement.BrowserOidc, "browser auth stays separate");
    }

    [Fact]
    public void ConfiguredNotRequired_AgainstConfiguredBearer_IsAConflict_NeverSilentlyChosen()
    {
        var hops = AltinnInteractionAuthentication.Build(false, null, Intake(IntegrationAuthMechanism.None, IntegrationAuthMechanism.EntraIdClientCredentials), null, AltinnReadinessState.Blocked);
        Hop(hops, AltinnInteractionAuthentication.AppIntegration).Requirement.Conflict.Should().BeTrue();
        var summary = AltinnInteractionAuthentication.Summary(hops, AltinnReadinessState.Blocked);
        summary.Single(s => s.Key == "integration-auth").Reason.Should().StartWith("Conflict:").And.Contain("review authentication");
        summary.Single(s => s.Key == "integration-auth").Label.Should().Be("Altinn → Meldingsutveksleren (MU) authentication");
    }

    [Fact]
    public void Precedence_ExplicitWins_RuntimeOnlyProposes_TestPackLast()
    {
        var resolution = InteractionAuthRules.Resolve(
        [
            new(InteractionAuthRequirement.BrowserOidc, InteractionAuthSource.TestPackProposal, "pack"),
            new(InteractionAuthRequirement.BearerOAuthApi, InteractionAuthSource.RuntimeSuggestion, "runtime"),
            new(InteractionAuthRequirement.NotRequired, InteractionAuthSource.ExplicitConfiguration, "configured"),
        ]);
        resolution.Requirement.Should().Be(InteractionAuthRequirement.NotRequired);
        resolution.IsSuggestion.Should().BeFalse();
        resolution.Conflict.Should().BeFalse("runtime and Test Pack candidates propose; they never conflict with configuration");

        var suggested = InteractionAuthRules.Resolve([new(InteractionAuthRequirement.BearerOAuthApi, InteractionAuthSource.RuntimeSuggestion, "runtime"),
            new(InteractionAuthRequirement.BrowserOidc, InteractionAuthSource.TestPackProposal, "pack")]);
        suggested.Requirement.Should().Be(InteractionAuthRequirement.BearerOAuthApi);
        suggested.IsSuggestion.Should().BeTrue();
        InteractionAuthRules.Resolve([new(InteractionAuthRequirement.Unknown, InteractionAuthSource.ExplicitConfiguration, "x")]).Should().Be(InteractionAuthResolution.Unknown);
        InteractionAuthRules.Resolve([new(InteractionAuthRequirement.NotRequired, InteractionAuthSource.ExplicitConfiguration, "a"),
            new(InteractionAuthRequirement.BearerOAuthApi, InteractionAuthSource.SourceDeclaration, "contract")]).Conflict.Should().BeTrue();
    }
}
