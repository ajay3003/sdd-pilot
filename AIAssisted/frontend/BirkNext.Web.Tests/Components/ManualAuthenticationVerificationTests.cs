using System.Text.Json;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Component = BirkNext.Web.Components.FrontendAnalysisSettings;

namespace BirkNext.Web.Tests.Components;

public sealed class ManualAuthenticationVerificationTests : BunitContext
{
    private readonly FrontendAnalysisSettingsService _settings = new();
    private readonly Mock<ITargetEnvironmentDetectionApiService> _api = new(MockBehavior.Strict);
    private const string Url = "https://m2lbdev.example.com/";

    public ManualAuthenticationVerificationTests()
    {
        Services.AddSingleton<IFrontendAnalysisSettingsService>(_settings);
        Services.AddSingleton(_api.Object);
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult("""
        {"activeProfileId":"local","profiles":[
          {"id":"local","name":"Local","targetUrl":"https://local.example.com"},
          {"id":"dev","name":"M2LB DEV","environmentType":"Development","targetUrl":"https://m2lbdev.example.com/"}
        ]}
        """);
        JSInterop.SetupVoid("birkNextStorage.setItem", _ => true).SetVoidResult();
        _api.Setup(x => x.DetectFromUrlAsync(Url, default)).ReturnsAsync(() => new TargetEnvironmentDetectionResult
        {
            OriginalUrl = Url, Success = true, Reachability = TargetReachability.Reachable,
            DetectedClientFramework = ClientFrameworkType.BlazorWebAssembly,
            SuggestedEnvironmentType = FrontendEnvironmentType.Development,
            State = DetectionState.ManualAuthenticationVerificationRequired,
            ManualAuthenticationVerificationRequired = true,
            ManualAuthenticationVerificationStatus = ManualAuthenticationVerificationStatus.Required
        });
    }

    private IRenderedComponent<Component> Open()
    {
        var cut = Render<Component>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("M2LB DEV")).Click();
        return cut;
    }

    private static void Click(IRenderedComponent<Component> cut, string label) =>
        cut.FindAll("button").Single(b => b.TextContent.Trim() == label).Click();

    private static bool ActivationDisabled(IRenderedComponent<Component> cut) =>
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Set as Active").HasAttribute("disabled");

    private static void AuthenticationTab(IRenderedComponent<Component> cut) =>
        cut.FindAll("[role=tab]").Single(t => t.TextContent.Trim() == "Authentication").Click();

    private static string CheckStatus(IRenderedComponent<Component> cut) =>
        cut.Find("[data-testid=authentication-verification-status]").TextContent.Trim();

    /// <summary>Detection reaches a target whose sign-in needs a managed browser: detection is complete, authentication is not runtime-verified,
    /// and there is no global verification section or gate — only an optional sign-in check on the Authentication tab.</summary>
    [Fact]
    public void Required_IsAnAuthenticationLimitation_NotAGlobalGate()
    {
        var cut = Open();
        Click(cut, "Detect settings");
        cut.WaitForAssertion(() => Assert.Contains("Target detection complete", cut.Markup));
        Assert.Contains("Authentication runtime verification: Not verified", cut.Find("[data-testid=detection-auth-limitation]").TextContent);
        Assert.Empty(cut.FindAll("section.fa-verification"));
        Assert.DoesNotContain("Mark verification", cut.Markup);
        Assert.DoesNotContain("Activation requires", cut.Markup);
        Assert.DoesNotContain("Detection failed", cut.Markup);
        Assert.DoesNotContain("Continue detection in browser", cut.Markup);
        Assert.Equal("Reachable", cut.Find("[data-testid=te-state-detection]").TextContent);
        Assert.Equal("Not verified", cut.Find("[data-testid=te-state-runtime]").TextContent);
        Assert.Empty(cut.FindAll("[data-testid=te-state-verification]"));
        Assert.False(ActivationDisabled(cut));
        AuthenticationTab(cut);
        Assert.Equal("Not recorded", CheckStatus(cut));
        Click(cut, "Open manual sign-in check");
        Assert.Contains("MCAS / Defender for Cloud Apps", cut.Markup);
        Assert.Null(_settings.Settings.Profiles.Single(x => x.Id == "dev").ManualVerification);
        Assert.False(ActivationDisabled(cut));
        _api.Verify(x => x.DetectFromUrlAsync(Url, default), Times.Once);
        _api.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARecordedCheck_IsPersistedAsAnAuthenticationFact_AndChangesNoReadinessOrTrust(bool passed)
    {
        var cut = Open();
        Click(cut, "Detect settings");
        var readinessBefore = cut.Find("[data-testid=te-state]").TextContent;
        AuthenticationTab(cut);
        Click(cut, "Open manual sign-in check");
        Click(cut, passed ? "Record sign-in check completed" : "Record sign-in check failed");
        var profile = _settings.Settings.Profiles.Single(x => x.Id == "dev");
        cut.WaitForAssertion(() => Assert.NotNull(profile.ManualVerification));
        Assert.Equal(passed ? ManualAuthenticationVerificationStatus.Passed : ManualAuthenticationVerificationStatus.Failed, profile.ManualVerification!.Result);
        Assert.NotNull(profile.ManualVerification.VerifiedAt);
        Assert.Equal(passed ? "Completed (recorded manually)" : "Failed (recorded manually)", CheckStatus(cut));
        Assert.Equal("Not verified", cut.Find("[data-testid=authentication-runtime-state]").TextContent.Trim());
        Assert.Equal(readinessBefore, cut.Find("[data-testid=te-state]").TextContent);
        Assert.False(ActivationDisabled(cut));
        Assert.Equal("local", _settings.Settings.ActiveProfileId);
        Assert.DoesNotContain("BrowserResourceFailure", cut.Markup);
        cut.Dispose();
        var reloaded = Open();
        AuthenticationTab(reloaded);
        Assert.Equal(passed ? "Completed (recorded manually)" : "Failed (recorded manually)", CheckStatus(reloaded));
        Assert.False(ActivationDisabled(reloaded));
    }

    [Theory]
    [InlineData("url")]
    [InlineData("environment")]
    [InlineData("auth")]
    [InlineData("profile")]
    [InlineData("configuration")]
    public void ContextChanges_InvalidatePersistedPass(string change)
    {
        var profile = new FrontendAnalysisProfile { Id = "dev", TargetUrl = Url };
        var evidence = ManualAuthenticationVerificationEvidence.Record(profile, ManualAuthenticationVerificationStatus.Passed);
        var stored = JsonSerializer.Serialize(evidence);
        var restored = JsonSerializer.Deserialize<ManualAuthenticationVerificationEvidence>(stored)!;
        Assert.Equal(ManualAuthenticationVerificationStatus.Passed, restored.StatusFor(profile));
        switch (change)
        {
            case "url": profile.TargetUrl += "other"; break;
            case "environment": profile.EnvironmentType = FrontendEnvironmentType.Production; break;
            case "auth": profile.Authentication.VerificationMode = AuthenticationVerificationMode.ManualManagedEdge; break;
            case "profile": profile.Id = "other"; break;
            case "configuration": profile.RequestTimeoutSeconds++; break;
        }
        Assert.Equal(ManualAuthenticationVerificationStatus.Stale, restored.StatusFor(profile));
        Assert.DoesNotContain("cookies", stored, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", stored, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompletedThenUrlEdit_MarksTheCheckOutOfDate()
    {
        var cut = Open();
        Click(cut, "Detect settings");
        AuthenticationTab(cut);
        Click(cut, "Open manual sign-in check");
        Click(cut, "Record sign-in check completed");
        Click(cut, "Edit Environment");
        cut.FindAll("[role=tab]").Single(t => t.TextContent.Trim() == "Target Application").Click();
        cut.Find("[data-testid=fa-edit-target-url]").Change("https://other.example.com/");
        AuthenticationTab(cut);
        Assert.StartsWith("Out of date", CheckStatus(cut));
        Assert.True(ActivationDisabled(cut), "unsaved edits block activation, as before");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DetectionWarningsOrMissingFramework_AreDetectionFacts_AndTheCheckDoesNotOverrideThem(bool warning)
    {
        _api.Setup(x => x.DetectFromUrlAsync(Url, default)).ReturnsAsync(new TargetEnvironmentDetectionResult
        {
            OriginalUrl = Url, Success = true, Reachability = TargetReachability.Reachable,
            State = DetectionState.ManualAuthenticationVerificationRequired,
            ManualAuthenticationVerificationRequired = true,
            DetectedClientFramework = warning ? ClientFrameworkType.BlazorWebAssembly : null,
            Warnings = warning ? ["Incomplete metadata"] : []
        });
        var cut = Open();
        Click(cut, "Detect settings");
        cut.WaitForAssertion(() => Assert.Contains("Detection needs current settings", cut.Markup));
        AuthenticationTab(cut);
        Click(cut, "Open manual sign-in check");
        Click(cut, "Record sign-in check completed");
        Assert.Equal("Completed (recorded manually)", CheckStatus(cut));
        Assert.Contains("Detection needs current settings", cut.Markup);
        Assert.False(ActivationDisabled(cut));
    }

    [Fact]
    public void SavedExplicitMode_UsesManualApiEvenWithConfiguredAuthenticationNone()
    {
        var cut = Open();
        var profile = _settings.Settings.Profiles.Single(x => x.Id == "dev");
        profile.Authentication.VerificationMode = AuthenticationVerificationMode.ManualManagedEdge;
        _api.Setup(x => x.DetectManualManagedEdgeAsync(Url, default)).ReturnsAsync(new TargetEnvironmentDetectionResult
        {
            OriginalUrl = Url, Success = true, State = DetectionState.ManualAuthenticationVerificationRequired,
            ManualAuthenticationVerificationRequired = true
        });
        Click(cut, "Detect settings");
        _api.Verify(x => x.DetectManualManagedEdgeAsync(Url, default), Times.Once);
        _api.Verify(x => x.DetectFromUrlAsync(It.IsAny<string>(), default), Times.Never);
        Assert.Contains("Authentication runtime verification: Not verified", cut.Find("[data-testid=detection-auth-limitation]").TextContent);
        Assert.DoesNotContain("Continue detection in browser", cut.Markup);
    }
}
