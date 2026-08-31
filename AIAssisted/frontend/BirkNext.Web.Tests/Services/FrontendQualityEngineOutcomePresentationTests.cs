using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

public sealed class FrontendQualityEngineOutcomePresentationTests
{
    [Theory]
    [InlineData(FrontendQualityEngineOutcomeReason.None, "Assessed", FrontendQualityEngineOutcomePresentation.OutcomePresentationCategory.Success)]
    [InlineData(FrontendQualityEngineOutcomeReason.NotSelected, "Not selected", FrontendQualityEngineOutcomePresentation.OutcomePresentationCategory.NotApplicable)]
    [InlineData(FrontendQualityEngineOutcomeReason.BlockedByDeploymentPolicy, "Unavailable on this deployment", FrontendQualityEngineOutcomePresentation.OutcomePresentationCategory.PolicyBlocked)]
    [InlineData(FrontendQualityEngineOutcomeReason.DisabledInSystemSettings, "Disabled in System Settings", FrontendQualityEngineOutcomePresentation.OutcomePresentationCategory.SettingsDisabled)]
    [InlineData(FrontendQualityEngineOutcomeReason.ReadinessUnavailable, "Not ready", FrontendQualityEngineOutcomePresentation.OutcomePresentationCategory.NotReady)]
    [InlineData(FrontendQualityEngineOutcomeReason.AuthenticationModeUnsupported, "Not supported for authenticated review", FrontendQualityEngineOutcomePresentation.OutcomePresentationCategory.AuthUnsupported)]
    [InlineData(FrontendQualityEngineOutcomeReason.AuthenticationRequired, "Authentication required", FrontendQualityEngineOutcomePresentation.OutcomePresentationCategory.AuthRequired)]
    [InlineData(FrontendQualityEngineOutcomeReason.AuthenticationExpired, "Authentication expired", FrontendQualityEngineOutcomePresentation.OutcomePresentationCategory.AuthExpired)]
    [InlineData(FrontendQualityEngineOutcomeReason.AuthenticationCancelled, "Authentication cancelled", FrontendQualityEngineOutcomePresentation.OutcomePresentationCategory.AuthCancelled)]
    [InlineData(FrontendQualityEngineOutcomeReason.UnexpectedOrigin, "Page navigated away", FrontendQualityEngineOutcomePresentation.OutcomePresentationCategory.OriginShift)]
    [InlineData(FrontendQualityEngineOutcomeReason.SessionUnavailable, "Session unavailable", FrontendQualityEngineOutcomePresentation.OutcomePresentationCategory.SessionUnavailable)]
    [InlineData(FrontendQualityEngineOutcomeReason.ResourceUnavailable, "Resource unavailable", FrontendQualityEngineOutcomePresentation.OutcomePresentationCategory.ResourceUnavailable)]
    [InlineData(FrontendQualityEngineOutcomeReason.TargetPolicyRejected, "Target rejected", FrontendQualityEngineOutcomePresentation.OutcomePresentationCategory.TargetRejected)]
    [InlineData(FrontendQualityEngineOutcomeReason.EngineUnavailable, "Engine unavailable", FrontendQualityEngineOutcomePresentation.OutcomePresentationCategory.EngineMissing)]
    [InlineData(FrontendQualityEngineOutcomeReason.EngineError, "Engine error", FrontendQualityEngineOutcomePresentation.OutcomePresentationCategory.EngineFailed)]
    [InlineData(FrontendQualityEngineOutcomeReason.Cancelled, "Cancelled", FrontendQualityEngineOutcomePresentation.OutcomePresentationCategory.Cancelled)]
    public void EveryCanonicalReason_HasDistinctPresentation(
        FrontendQualityEngineOutcomeReason reason,
        string expectedLabel,
        FrontendQualityEngineOutcomePresentation.OutcomePresentationCategory expectedCategory)
    {
        var presentation = FrontendQualityEngineOutcomePresentation.GetPresentation(reason);

        presentation.Label.Should().Be(expectedLabel);
        presentation.Category.Should().Be(expectedCategory);
        presentation.Description.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(FrontendQualityEngineOutcomeReason.None, true)]
    [InlineData(FrontendQualityEngineOutcomeReason.NotSelected, false)]
    [InlineData(FrontendQualityEngineOutcomeReason.BlockedByDeploymentPolicy, false)]
    [InlineData(FrontendQualityEngineOutcomeReason.DisabledInSystemSettings, false)]
    [InlineData(FrontendQualityEngineOutcomeReason.ReadinessUnavailable, false)]
    [InlineData(FrontendQualityEngineOutcomeReason.AuthenticationModeUnsupported, false)]
    [InlineData(FrontendQualityEngineOutcomeReason.AuthenticationRequired, false)]
    [InlineData(FrontendQualityEngineOutcomeReason.EngineError, false)]
    public void AssessmentStatus_CorrectlyIdentifiesMeaningfulAssessment(
        FrontendQualityEngineOutcomeReason reason,
        bool expectedAssessed)
    {
        FrontendQualityEngineOutcomePresentation.IsAssessed(reason).Should().Be(expectedAssessed);
    }

    [Fact]
    public void NotSelected_RemainsNeutral()
    {
        var presentation = FrontendQualityEngineOutcomePresentation.GetPresentation(FrontendQualityEngineOutcomeReason.NotSelected);

        presentation.Category.Should().Be(FrontendQualityEngineOutcomePresentation.OutcomePresentationCategory.NotApplicable);
        presentation.CountsAsAssessed.Should().BeFalse();
        presentation.Label.Should().Be("Not selected");
    }

    [Fact]
    public void PolicySettingsReadiness_AllDistinct()
    {
        var policy = FrontendQualityEngineOutcomePresentation.GetPresentation(FrontendQualityEngineOutcomeReason.BlockedByDeploymentPolicy);
        var settings = FrontendQualityEngineOutcomePresentation.GetPresentation(FrontendQualityEngineOutcomeReason.DisabledInSystemSettings);
        var readiness = FrontendQualityEngineOutcomePresentation.GetPresentation(FrontendQualityEngineOutcomeReason.ReadinessUnavailable);

        policy.Category.Should().NotBe(settings.Category);
        policy.Category.Should().NotBe(readiness.Category);
        settings.Category.Should().NotBe(readiness.Category);

        policy.Label.Should().NotBe(settings.Label);
        policy.Label.Should().NotBe(readiness.Label);
        settings.Label.Should().NotBe(readiness.Label);
    }

    [Fact]
    public void AuthStates_AllDistinct()
    {
        var required = FrontendQualityEngineOutcomePresentation.GetPresentation(FrontendQualityEngineOutcomeReason.AuthenticationRequired);
        var expired = FrontendQualityEngineOutcomePresentation.GetPresentation(FrontendQualityEngineOutcomeReason.AuthenticationExpired);
        var cancelled = FrontendQualityEngineOutcomePresentation.GetPresentation(FrontendQualityEngineOutcomeReason.AuthenticationCancelled);
        var originShift = FrontendQualityEngineOutcomePresentation.GetPresentation(FrontendQualityEngineOutcomeReason.UnexpectedOrigin);

        required.Category.Should().Be(FrontendQualityEngineOutcomePresentation.OutcomePresentationCategory.AuthRequired);
        expired.Category.Should().Be(FrontendQualityEngineOutcomePresentation.OutcomePresentationCategory.AuthExpired);
        cancelled.Category.Should().Be(FrontendQualityEngineOutcomePresentation.OutcomePresentationCategory.AuthCancelled);
        originShift.Category.Should().Be(FrontendQualityEngineOutcomePresentation.OutcomePresentationCategory.OriginShift);

        required.Label.Should().NotBe(expired.Label);
        required.Label.Should().NotBe(cancelled.Label);
        expired.Label.Should().NotBe(cancelled.Label);
    }

    [Fact]
    public void AuthUnsupported_Explicit()
    {
        var unsupported = FrontendQualityEngineOutcomePresentation.GetPresentation(FrontendQualityEngineOutcomeReason.AuthenticationModeUnsupported);

        unsupported.Label.Should().Contain("supported");
        unsupported.Label.Should().Contain("authenticated");
        unsupported.Category.Should().Be(FrontendQualityEngineOutcomePresentation.OutcomePresentationCategory.AuthUnsupported);
    }

    [Fact]
    public void ResourceVsEngineFailures_AllDistinct()
    {
        var resource = FrontendQualityEngineOutcomePresentation.GetPresentation(FrontendQualityEngineOutcomeReason.ResourceUnavailable);
        var engineMissing = FrontendQualityEngineOutcomePresentation.GetPresentation(FrontendQualityEngineOutcomeReason.EngineUnavailable);
        var engineError = FrontendQualityEngineOutcomePresentation.GetPresentation(FrontendQualityEngineOutcomeReason.EngineError);

        resource.Category.Should().Be(FrontendQualityEngineOutcomePresentation.OutcomePresentationCategory.ResourceUnavailable);
        engineMissing.Category.Should().Be(FrontendQualityEngineOutcomePresentation.OutcomePresentationCategory.EngineMissing);
        engineError.Category.Should().Be(FrontendQualityEngineOutcomePresentation.OutcomePresentationCategory.EngineFailed);

        resource.Label.Should().NotBe(engineMissing.Label);
        resource.Label.Should().NotBe(engineError.Label);
        engineMissing.Label.Should().NotBe(engineError.Label);
    }

    [Fact]
    public void UnknownEnumValue_SafeFallback()
    {
        // Test defensive behavior for unexpected enum value
        var unknownValue = (FrontendQualityEngineOutcomeReason)9999;
        var presentation = FrontendQualityEngineOutcomePresentation.GetPresentation(unknownValue);

        presentation.Label.Should().Be("Unknown");
        presentation.Label.Should().NotContain(unknownValue.ToString());
        presentation.CountsAsAssessed.Should().BeFalse();
    }
}
