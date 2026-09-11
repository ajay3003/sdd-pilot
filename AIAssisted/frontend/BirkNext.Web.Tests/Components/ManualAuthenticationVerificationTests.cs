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

    [Fact]
    public void Required_IsNoticeAndInstructionsDoNotPassOrLaunchBrowser()
    {
        var cut = Open();
        Click(cut, "Detect settings");
        cut.WaitForAssertion(() => Assert.Contains("Manual authentication verification required", cut.Markup));
        Assert.DoesNotContain("Detection failed", cut.Markup);
        Assert.DoesNotContain("Continue detection in browser", cut.Markup);
        Assert.True(ActivationDisabled(cut));
        Click(cut, "Open verification instructions");
        Assert.Contains("Manual authentication verification pending", cut.Markup);
        Assert.Contains("MCAS / Defender for Cloud Apps", cut.Markup);
        Assert.Null(_settings.Settings.Profiles.Single(x => x.Id == "dev").ManualVerification);
        Assert.True(ActivationDisabled(cut));
        _api.Verify(x => x.DetectFromUrlAsync(Url, default), Times.Once);
        _api.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExplicitResult_PersistsAndRecomputesWithoutActivation(bool passed)
    {
        var cut = Open();
        Click(cut, "Detect settings");
        Click(cut, "Open verification instructions");
        Click(cut, passed ? "Mark verification passed" : "Mark verification failed");
        var profile = _settings.Settings.Profiles.Single(x => x.Id == "dev");
        cut.WaitForAssertion(() => Assert.NotNull(profile.ManualVerification));
        Assert.Equal(passed ? ManualAuthenticationVerificationStatus.Passed : ManualAuthenticationVerificationStatus.Failed, profile.ManualVerification!.Result);
        Assert.NotNull(profile.ManualVerification.VerifiedAt);
        Assert.Equal(!passed, ActivationDisabled(cut));
        Assert.Equal("local", _settings.Settings.ActiveProfileId);
        Assert.DoesNotContain("BrowserResourceFailure", cut.Markup);
        if (!passed)
        {
            Assert.Contains("Manual authentication verification failed", cut.Markup);
            Click(cut, "Retry manual verification");
            Assert.Contains("Mark verification passed", cut.Markup);
        }
        cut.Dispose();
        var reloaded = Open();
        Assert.Contains("Previous manual authentication verification", reloaded.Markup);
        Assert.DoesNotContain("Manual authentication verification failed", reloaded.Markup);
        Assert.True(ActivationDisabled(reloaded));
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
    public void PassedThenUrlEdit_ShowsStaleAndBlocksActivation()
    {
        var cut = Open();
        Click(cut, "Detect settings");
        Click(cut, "Open verification instructions");
        Click(cut, "Mark verification passed");
        Click(cut, "Edit Environment");
        var input = cut.FindAll("input").Single(x => x.GetAttribute("value") == Url);
        input.Change("https://other.example.com/");
        Assert.Contains("verification stale", cut.Markup);
        Assert.True(ActivationDisabled(cut));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OtherDetectionGates_StillBlockActivationAfterManualPass(bool warning)
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
        Click(cut, "Open verification instructions");
        Click(cut, "Mark verification passed");
        Assert.Contains("Manual authentication verification passed", cut.Markup);
        Assert.True(ActivationDisabled(cut));
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
        Assert.Contains("Manual authentication verification required", cut.Markup);
        Assert.DoesNotContain("Continue detection in browser", cut.Markup);
    }
}
