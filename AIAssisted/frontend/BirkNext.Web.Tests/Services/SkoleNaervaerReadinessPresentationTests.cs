using BirkNext.Integrations;
using BirkNext.Web.Services;
using FluentAssertions;
using Xunit;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// The run page groups the backend's own Rapportmottak readiness rows: resolved inputs with their source, only the unresolved ones as
/// attention items (each pointing to the owner that resolves it) and the TT02 decision. Nothing is recomputed or upgraded here.
/// </summary>
public sealed class SkoleNaervaerReadinessPresentationTests
{
    /// <summary>The backend's Rapportmottak rows with every run input ready, except the keys given.</summary>
    internal static IReadOnlyList<JourneyPrerequisite> Rows(params string[] unresolved)
    {
        JourneyPrerequisite Row(string key, string label, JourneyPrerequisiteCategory category, string source, bool mandatory = true) =>
            new(key, label, category, unresolved.Contains(key) ? JourneyReadinessState.NotConfigured : JourneyReadinessState.Ready,
                unresolved.Contains(key) ? $"Missing: {label}." : $"Resolved: {label}.", mandatory) { Source = source };
        var authOpen = unresolved.Any(key => key is "unattended-provider" or "idporten-client" or "redirect-uri" or "client-secret" or "synthetic-reporter" or "reporter-profile");
        return
        [
            Row("trusted-environment", "Trusted non-production environment", JourneyPrerequisiteCategory.TrustedEnvironment, "Target Environment"),
            Row("integration:report-intake", "Skolenærvær – Altinn report intake", JourneyPrerequisiteCategory.Integration, "Integration Quality Review + Target Environment"),
            Row("altinn-environment", "Altinn TT02 environment", JourneyPrerequisiteCategory.TrustedEnvironment, "Integration Quality Review"),
            Row("altinn-app", "Approved Altinn app", JourneyPrerequisiteCategory.Integration, "Integration Quality Review"),
            new("reporter-auth", "Reporter authentication", JourneyPrerequisiteCategory.Authentication,
                authOpen ? JourneyReadinessState.NotConfigured : JourneyReadinessState.Ready, "Unattended.") { Source = "Local execution config" },
            Row("unattended-provider", "Unattended ID-porten TEST provider", JourneyPrerequisiteCategory.Authentication, "Local execution config", mandatory: false),
            Row("idporten-client", "ID-porten TEST client", JourneyPrerequisiteCategory.Authentication, "Integration Quality Review", mandatory: false),
            Row("redirect-uri", "Registered redirect URI", JourneyPrerequisiteCategory.Authentication, "Local execution config", mandatory: false),
            Row("client-secret", "Client secret", JourneyPrerequisiteCategory.Authentication, "Local execution config", mandatory: false),
            Row("synthetic-reporter", "Synthetic reporter", JourneyPrerequisiteCategory.SyntheticData, "Test Data", mandatory: false),
            Row("reporter-profile", "Reporter Altinn profile", JourneyPrerequisiteCategory.SyntheticData, "Test Data", mandatory: false),
            Row("app-policy", "App authorization policy", JourneyPrerequisiteCategory.Authorization, "Local execution config"),
            Row("submission-contract", "Altinn app contract", JourneyPrerequisiteCategory.Contract, "Live Altinn metadata"),
            Row("public-metadata", "Public TT02 process metadata", JourneyPrerequisiteCategory.Contract, "Live Altinn metadata", mandatory: false),
            Row("process-contract", "Process contract (current operation)", JourneyPrerequisiteCategory.Contract, "Live Altinn metadata", mandatory: false),
            Row("contract-impact", "Contract change impact", JourneyPrerequisiteCategory.Contract, "Live Altinn metadata"),
            Row("test-reporter", "Approved test reporter", JourneyPrerequisiteCategory.SyntheticData, "Integration Quality Review"),
            Row("synthetic-data", "Known synthetic child (test data)", JourneyPrerequisiteCategory.SyntheticData, "Test Data"),
            new("backend-build", "Backend build", JourneyPrerequisiteCategory.Executor, JourneyReadinessState.Ready, "Running commit.", Mandatory: false) { Source = "BirkNext" },
        ];
    }

    [Fact]
    public void AllResolved_ListsEveryRunInputWithItsSource_AndIsReadyForASupervisedFirstSubmissionOnly()
    {
        var rows = Rows();
        SkoleNaervaerReadinessPresentation.Resolved(rows).Select(row => row.Key).Should().Equal(SkoleNaervaerReadinessPresentation.RunInputs);
        SkoleNaervaerReadinessPresentation.Resolved(rows).Should().OnlyContain(row => !string.IsNullOrEmpty(row.Source));
        SkoleNaervaerReadinessPresentation.NeedsAttention(rows).Should().BeEmpty();
        SkoleNaervaerReadinessPresentation.Decision([]).Should().Be(("A", "Ready for first supervised TT02 submission"));
    }

    [Theory]
    [InlineData("client-secret", "B")]
    [InlineData("idporten-client", "B")]
    [InlineData("redirect-uri", "B")]
    [InlineData("unattended-provider", "B")]
    [InlineData("reporter-profile", "C")]
    [InlineData("synthetic-data", "C")]
    [InlineData("trusted-environment", "D")]
    [InlineData("integration:report-intake", "D")]
    [InlineData("submission-contract", "E")]
    public void OneUnresolvedInput_IsTheOnlyAttentionItem_AndDecidesTheCategory(string key, string code)
    {
        var rows = Rows(key);
        var attention = SkoleNaervaerReadinessPresentation.NeedsAttention(rows);
        attention.Select(row => row.Key).Should().Equal([key], "reporter authentication is replaced by exactly what is missing; resolved inputs are never blockers");
        SkoleNaervaerReadinessPresentation.Resolved(rows).Should().NotContain(row => row.Key == key);
        SkoleNaervaerReadinessPresentation.Decision(attention).Code.Should().Be(code);
    }

    [Theory]
    [InlineData("client-secret")]
    [InlineData("reporter-profile")]
    public void AnUnattendedGap_IsStillAttention_WhenOnlyTheInteractiveFallbackMeetsTheGate(string key)
    {
        var rows = Rows(key).Select(row => row.Key == "reporter-auth" ? row with { State = JourneyReadinessState.Ready, Detail = "Only the interactive fallback is configured." } : row).ToList();
        SkoleNaervaerReadinessPresentation.InteractiveFallbackOnly(rows).Should().BeTrue();
        var attention = SkoleNaervaerReadinessPresentation.NeedsAttention(rows);
        attention.Select(row => row.Key).Should().Equal(key);
        SkoleNaervaerReadinessPresentation.Decision(attention).Code.Should().NotBe("A", "the first TT02 run is unattended; a fallback does not make it ready");
    }

    [Fact]
    public void MissingPublicMetadata_IsShownWithTheIncompleteContract()
    {
        SkoleNaervaerReadinessPresentation.NeedsAttention(Rows("public-metadata", "submission-contract")).Select(row => row.Key).Should().Equal("public-metadata", "submission-contract");
    }

    [Fact]
    public void RootCauseComesFirst_IdPortenBeforeIdentityBeforeTargetBeforeContract()
    {
        var attention = SkoleNaervaerReadinessPresentation.NeedsAttention(Rows("submission-contract", "trusted-environment", "synthetic-data", "client-secret"));
        SkoleNaervaerReadinessPresentation.Decision(attention).Code.Should().Be("B");
        SkoleNaervaerReadinessPresentation.Decision(SkoleNaervaerReadinessPresentation.NeedsAttention(Rows("submission-contract", "trusted-environment", "synthetic-data"))).Code.Should().Be("C");
        SkoleNaervaerReadinessPresentation.Decision(SkoleNaervaerReadinessPresentation.NeedsAttention(Rows("submission-contract", "trusted-environment"))).Code.Should().Be("D");
    }

    [Theory]
    [InlineData("idporten-client", "Open Altinn run configuration", "/integration-quality-review?view=test-verify", null)]
    [InlineData("redirect-uri", "Configure local execution", null, "local")]
    [InlineData("client-secret", "Configure local execution", null, "local")]
    [InlineData("unattended-provider", "Configure local execution", null, "local")]
    [InlineData("reporter-profile", "Open Test Data", "/test-data", null)]
    [InlineData("synthetic-data", "Open Test Data", "/test-data", null)]
    [InlineData("trusted-environment", "Open Target Environment", "/admin/system-settings?section=target-environments", null)]
    [InlineData("public-metadata", "Read public TT02 metadata", null, "contract")]
    public void EachBlocker_PointsToItsOwner_NeverToTheTargetApplicationForAuthentication(string key, string label, string? href, string? group)
    {
        var row = Rows(key).Single(item => item.Key == key);
        var action = SkoleNaervaerReadinessPresentation.ActionFor(row, "dev");
        (action.Label, action.Href, action.Group).Should().Be((label, href, group));
    }

    [Fact]
    public void IntegrationEnrolment_OpensTargetEnvironmentIntegrations_ForThisProfile()
    {
        var action = SkoleNaervaerReadinessPresentation.ActionFor(Rows("integration:report-intake").Single(item => item.Key == "integration:report-intake"), "dev");
        action.Label.Should().Be("Open Target Environment → Integrations");
        action.Href.Should().Contain("tab=integrations").And.Contain("profile=dev");
    }

    [Fact]
    public void AContractChangeTheProducerCannotSatisfy_IsAContractBlocker_WithAReviewAction()
    {
        var attention = SkoleNaervaerReadinessPresentation.NeedsAttention(Rows("contract-impact"));
        attention.Select(row => row.Key).Should().Equal("contract-impact");
        SkoleNaervaerReadinessPresentation.Decision(attention).Should().Be(("E", "Payload / contract blocker"));
        SkoleNaervaerReadinessPresentation.ActionFor(attention.Single(), "dev-env").Label.Should().Be("Review contract changes");
    }
}
