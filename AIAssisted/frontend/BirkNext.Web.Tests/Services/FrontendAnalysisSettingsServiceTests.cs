using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Xunit;

namespace BirkNext.Web.Tests.Services;

public sealed class FrontendAnalysisSettingsServiceTests
{
    // Each test gets a fresh service instance with no profiles (not yet loaded).
    private readonly FrontendAnalysisSettingsService _sut = new();

    // ── Default profile creation ───────────────────────────────────────────────

    [Fact]
    public void CreateProfile_AddsProfileToList()
    {
        _sut.CreateProfile("Test", FrontendEnvironmentType.QA);

        _sut.Settings.Profiles.Should().ContainSingle();
    }

    [Fact]
    public void CreateProfile_SetsNameAndType()
    {
        var p = _sut.CreateProfile("My Profile", FrontendEnvironmentType.Production);

        p.Name.Should().Be("My Profile");
        p.EnvironmentType.Should().Be(FrontendEnvironmentType.Production);
    }

    [Fact]
    public void CreateProfile_AssignsNonEmptyUniqueIds()
    {
        var p1 = _sut.CreateProfile("A", FrontendEnvironmentType.Local);
        var p2 = _sut.CreateProfile("B", FrontendEnvironmentType.QA);

        p1.Id.Should().NotBeNullOrEmpty();
        p2.Id.Should().NotBeNullOrEmpty();
        p1.Id.Should().NotBe(p2.Id);
    }

    [Fact]
    public void CreateProfile_UsesDefaultThresholds()
    {
        var p = _sut.CreateProfile("X", FrontendEnvironmentType.Local);

        p.Performance.Mode.Should().Be(FrontendThresholdMode.Default);
        p.Performance.MaxStartupSizeBytes.Should().Be(8L * 1024 * 1024);
        p.Performance.MaxStartupRequests.Should().Be(30);
        p.Performance.MaxStartupApiCalls.Should().Be(10);
    }

    [Fact]
    public void CreateProfile_UsesDefaultCoreWebVitals()
    {
        var p = _sut.CreateProfile("X", FrontendEnvironmentType.Local);

        p.CoreWebVitals.LcpGoodMs.Should().Be(2500);
        p.CoreWebVitals.LcpPoorMs.Should().Be(4000);
        p.CoreWebVitals.InpGoodMs.Should().Be(200);
        p.CoreWebVitals.ClsGood.Should().Be(0.1);
    }

    [Fact]
    public void CreateProfile_EnablesCoreFeaturesByDefault()
    {
        var p = _sut.CreateProfile("X", FrontendEnvironmentType.Local);

        p.Features.AssetDiscovery.Should().BeTrue();
        p.Features.StartupAnalysis.Should().BeTrue();
        p.Features.RestAnalysis.Should().BeTrue();
        p.Features.SecurityHeaderReview.Should().BeTrue();
        p.Features.PerformanceReadiness.Should().BeTrue();
    }

    [Fact]
    public void CreateProfile_DisablesFutureFeaturesByDefault()
    {
        var p = _sut.CreateProfile("X", FrontendEnvironmentType.Local);

        p.Features.AuthenticatedBrowserReview.Should().BeFalse();
        p.Features.LighthouseIntegration.Should().BeFalse();
        p.Features.PlaywrightRuntimeInspection.Should().BeFalse();
    }

    // ── Duplicate profile detection ───────────────────────────────────────────

    [Fact]
    public void ValidateProfile_RejectsBlankName()
    {
        var p = _sut.CreateProfile("", FrontendEnvironmentType.QA);
        var result = _sut.ValidateProfile(p);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Contains("name is required"));
    }

    [Fact]
    public void ValidateProfile_RejectsDuplicateName()
    {
        _sut.CreateProfile("Existing", FrontendEnvironmentType.Local);
        var p2 = _sut.CreateProfile("Other", FrontendEnvironmentType.QA);
        p2.Name = "Existing";

        var result = _sut.ValidateProfile(p2);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Contains("already exists"));
    }

    [Fact]
    public void ValidateProfile_AllowsSameNameOnSameProfile()
    {
        var p = _sut.CreateProfile("Solo", FrontendEnvironmentType.QA);

        var result = _sut.ValidateProfile(p);

        result.Errors.Should().NotContain(e => e.Contains("already exists"));
    }

    // ── Active profile selection ──────────────────────────────────────────────

    [Fact]
    public void SelectActiveProfile_SetsActiveId()
    {
        var p = _sut.CreateProfile("P", FrontendEnvironmentType.Local);
        _sut.SelectActiveProfile(p.Id);

        _sut.Settings.ActiveProfileId.Should().Be(p.Id);
    }

    [Fact]
    public void SelectActiveProfile_IgnoresUnknownId()
    {
        _sut.SelectActiveProfile("does-not-exist");

        _sut.Settings.ActiveProfileId.Should().BeNull();
    }

    [Fact]
    public void ActiveProfile_ReturnsNullWhenNoActiveSet()
    {
        _sut.CreateProfile("P", FrontendEnvironmentType.Local);

        _sut.ActiveProfile.Should().BeNull();
    }

    [Fact]
    public void ActiveProfile_ReturnsCorrectProfileAfterSelection()
    {
        var p = _sut.CreateProfile("P", FrontendEnvironmentType.QA);
        _sut.SelectActiveProfile(p.Id);

        _sut.ActiveProfile.Should().NotBeNull();
        _sut.ActiveProfile!.Id.Should().Be(p.Id);
    }

    // ── URL validation ────────────────────────────────────────────────────────

    [Fact]
    public void ValidateProfile_AcceptsValidHttpsUrl()
    {
        var p = _sut.CreateProfile("P", FrontendEnvironmentType.QA);
        p.TargetUrl = "https://example-qa.local";

        var result = _sut.ValidateProfile(p);

        result.Errors.Should().NotContain(e => e.Contains("Target URL"));
    }

    [Fact]
    public void ValidateProfile_RejectsInvalidUrl()
    {
        var p = _sut.CreateProfile("P", FrontendEnvironmentType.QA);
        p.TargetUrl = "not-a-url";

        var result = _sut.ValidateProfile(p);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Contains("Target URL"));
    }

    [Fact]
    public void ValidateProfile_RejectsInvalidAuthorityUrl()
    {
        var p = _sut.CreateProfile("P", FrontendEnvironmentType.QA);
        p.Authentication.ExpectedAuthority = "not-a-url";

        var result = _sut.ValidateProfile(p);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Contains("Authority"));
    }

    [Fact]
    public void ValidateProfile_RejectsInvalidRedirectUrl()
    {
        var p = _sut.CreateProfile("P", FrontendEnvironmentType.QA);
        p.Authentication.AllowedRedirectUrls = ["https://ok.example.com", "bad-url"];

        var result = _sut.ValidateProfile(p);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Contains("Redirect URL"));
    }

    // ── Authority validation (delegate to URL check above) ───────────────────

    // ── Production localhost warning ──────────────────────────────────────────

    [Fact]
    public void ValidateProfile_WarnWhenProductionUsesLocalhost()
    {
        var p = _sut.CreateProfile("Prod", FrontendEnvironmentType.Production);
        p.TargetUrl = "https://localhost:5001";

        var result = _sut.ValidateProfile(p);

        result.Warnings.Should().ContainSingle(w => w.Contains("localhost"));
    }

    [Fact]
    public void ValidateProfile_NoLocalhostWarningForLocalEnvironment()
    {
        var p = _sut.CreateProfile("Local", FrontendEnvironmentType.Local);
        p.TargetUrl = "https://localhost:5001";

        var result = _sut.ValidateProfile(p);

        result.Warnings.Should().NotContain(w => w.Contains("localhost"));
    }

    [Fact]
    public void ValidateProfile_LocalLoopback_HasNoTypeMismatchWarning()
    {
        var p = _sut.CreateProfile("Local", FrontendEnvironmentType.Local);
        p.TargetUrl = "https://localhost:5001";

        var result = _sut.ValidateProfile(p);

        result.Warnings.Should().NotContain(w => w.Contains("hostname looks like", StringComparison.OrdinalIgnoreCase));
        result.Warnings.Should().NotContain(w => w.Contains("not a recognized local", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidateProfile_LocalWithDevelopmentHostname_WarnsWithoutMutation()
    {
        var p = _sut.CreateProfile("Local", FrontendEnvironmentType.Local);
        p.TargetUrl = "https://application-dev.example.test";

        var result = _sut.ValidateProfile(p);

        result.Warnings.Should().ContainSingle(w =>
            w.Contains("marked Local") && w.Contains("Development"));
        p.EnvironmentType.Should().Be(FrontendEnvironmentType.Local);
    }

    [Fact]
    public void ValidateProfile_DevelopmentHostnameAndType_HasNoConflictWarning()
    {
        var p = _sut.CreateProfile("Development", FrontendEnvironmentType.Development);
        p.TargetUrl = "https://application-dev.example.test";

        var result = _sut.ValidateProfile(p);

        result.Warnings.Should().NotContain(w => w.Contains("hostname looks like", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidateProfile_CustomProfile_DoesNotImposeInferredEnvironmentType()
    {
        var p = _sut.CreateProfile("Local with auth", FrontendEnvironmentType.Custom);
        p.TargetUrl = "https://application-dev.example.test";

        var result = _sut.ValidateProfile(p);

        result.Warnings.Should().NotContain(w => w.Contains("hostname looks like", StringComparison.OrdinalIgnoreCase));
        _sut.Settings.Profiles.Should().ContainSingle(x => x.Name == "Local with auth");
    }

    // ── HTTPS recommendation ──────────────────────────────────────────────────

    [Fact]
    public void ValidateProfile_WarnHttpForNonLocalEnvironment()
    {
        var p = _sut.CreateProfile("QA", FrontendEnvironmentType.QA);
        p.TargetUrl = "http://example-qa.local";

        var result = _sut.ValidateProfile(p);

        result.Warnings.Should().ContainSingle(w => w.Contains("HTTPS"));
    }

    [Fact]
    public void ValidateProfile_NoHttpsWarningForLocalEnvironment()
    {
        var p = _sut.CreateProfile("Local", FrontendEnvironmentType.Local);
        p.TargetUrl = "http://localhost:5001";

        var result = _sut.ValidateProfile(p);

        result.Warnings.Should().NotContain(w => w.Contains("HTTPS"));
    }

    // ── Positive value validation ─────────────────────────────────────────────

    [Fact]
    public void ValidateProfile_RejectsZeroLatency()
    {
        var p = _sut.CreateProfile("P", FrontendEnvironmentType.QA);
        p.Performance.MaxAverageApiLatencyMs = 0;

        var result = _sut.ValidateProfile(p);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Contains("Average API Latency"));
    }

    [Fact]
    public void ValidateProfile_RejectsZeroStartupSize()
    {
        var p = _sut.CreateProfile("P", FrontendEnvironmentType.QA);
        p.Performance.MaxStartupSizeBytes = 0;

        var result = _sut.ValidateProfile(p);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Contains("Startup Size"));
    }

    // ── Default threshold preset ──────────────────────────────────────────────

    [Fact]
    public void GetDefaultThresholds_Returns8MbStartupSize()
    {
        var t = _sut.GetDefaultThresholds();

        t.MaxStartupSizeBytes.Should().Be(8L * 1024 * 1024);
    }

    [Fact]
    public void GetDefaultThresholds_Returns30MaxRequests()
    {
        var t = _sut.GetDefaultThresholds();

        t.MaxStartupRequests.Should().Be(30);
    }

    [Fact]
    public void GetDefaultThresholds_Returns500KbRestPayload()
    {
        var t = _sut.GetDefaultThresholds();

        t.MaxRestPayloadBytes.Should().Be(500L * 1024);
    }

    [Fact]
    public void GetDefaultThresholds_ModeIsDefault()
    {
        var t = _sut.GetDefaultThresholds();

        t.Mode.Should().Be(FrontendThresholdMode.Default);
    }

    // ── Strict threshold preset ───────────────────────────────────────────────

    [Fact]
    public void GetStrictThresholds_Returns5MbStartupSize()
    {
        var t = _sut.GetStrictThresholds();

        t.MaxStartupSizeBytes.Should().Be(5L * 1024 * 1024);
    }

    [Fact]
    public void GetStrictThresholds_Returns20MaxRequests()
    {
        var t = _sut.GetStrictThresholds();

        t.MaxStartupRequests.Should().Be(20);
    }

    [Fact]
    public void GetStrictThresholds_Returns250KbRestPayload()
    {
        var t = _sut.GetStrictThresholds();

        t.MaxRestPayloadBytes.Should().Be(250L * 1024);
    }

    [Fact]
    public void GetStrictThresholds_ModeIsStrict()
    {
        var t = _sut.GetStrictThresholds();

        t.Mode.Should().Be(FrontendThresholdMode.Strict);
    }

    // ── Restore Default Thresholds ────────────────────────────────────────────

    [Fact]
    public void RestoreDefaultThresholds_ReplacesCustomValues()
    {
        var p = _sut.CreateProfile("P", FrontendEnvironmentType.QA);
        p.Performance.MaxStartupRequests = 999;
        p.Performance.Mode               = FrontendThresholdMode.Custom;

        _sut.RestoreDefaultThresholds(p.Id);

        p.Performance.MaxStartupRequests.Should().Be(30);
        p.Performance.Mode.Should().Be(FrontendThresholdMode.Default);
    }

    // ── Restore Strict Thresholds ─────────────────────────────────────────────

    [Fact]
    public void RestoreStrictThresholds_AppliesStrictValues()
    {
        var p = _sut.CreateProfile("P", FrontendEnvironmentType.QA);

        _sut.RestoreStrictThresholds(p.Id);

        p.Performance.MaxStartupRequests.Should().Be(20);
        p.Performance.MaxAverageApiLatencyMs.Should().Be(300);
        p.Performance.Mode.Should().Be(FrontendThresholdMode.Strict);
    }

    // ── Restore Core Web Vitals ───────────────────────────────────────────────

    [Fact]
    public void RestoreDefaultCoreWebVitals_ReplacesCustomValues()
    {
        var p = _sut.CreateProfile("P", FrontendEnvironmentType.QA);
        p.CoreWebVitals.LcpGoodMs = 9999;
        p.CoreWebVitals.ClsGood   = 9.9;

        _sut.RestoreDefaultCoreWebVitals(p.Id);

        p.CoreWebVitals.LcpGoodMs.Should().Be(2500);
        p.CoreWebVitals.ClsGood.Should().Be(0.1);
    }

    // ── Restore Security Expectations ─────────────────────────────────────────

    [Fact]
    public void RestoreDefaultSecurityExpectations_ClearsCustomHosts()
    {
        var p = _sut.CreateProfile("P", FrontendEnvironmentType.QA);
        p.Security.AllowedBackendDomains = ["custom.example.com"];
        p.Security.ExpectedAuthority     = "https://old-authority.com";

        _sut.RestoreDefaultSecurityExpectations(p.Id);

        p.Security.AllowedBackendDomains.Should().BeEmpty();
        p.Security.ExpectedAuthority.Should().BeNull();
    }

    [Fact]
    public void RestoreDefaultSecurityExpectations_RestoresDefaultHeaders()
    {
        var p = _sut.CreateProfile("P", FrontendEnvironmentType.QA);
        p.Security.ExpectedSecurityHeaders = ["Custom-Header"];

        _sut.RestoreDefaultSecurityExpectations(p.Id);

        p.Security.ExpectedSecurityHeaders.Should().Contain("Content-Security-Policy");
        p.Security.ExpectedSecurityHeaders.Should().Contain("Strict-Transport-Security");
    }

    // ── Restore Feature Toggles ───────────────────────────────────────────────

    [Fact]
    public void RestoreDefaultFeatureToggles_ReenablesCoreFeatures()
    {
        var p = _sut.CreateProfile("P", FrontendEnvironmentType.QA);
        p.Features.AssetDiscovery  = false;
        p.Features.CachingReview   = false;

        _sut.RestoreDefaultFeatureToggles(p.Id);

        p.Features.AssetDiscovery.Should().BeTrue();
        p.Features.CachingReview.Should().BeTrue();
    }

    [Fact]
    public void RestoreDefaultFeatureToggles_LeaveFutureFeaturesDisabled()
    {
        var p = _sut.CreateProfile("P", FrontendEnvironmentType.QA);
        p.Features.LighthouseIntegration      = true;
        p.Features.PlaywrightRuntimeInspection = true;

        _sut.RestoreDefaultFeatureToggles(p.Id);

        p.Features.LighthouseIntegration.Should().BeFalse();
        p.Features.PlaywrightRuntimeInspection.Should().BeFalse();
    }

    // ── Reset Profile ─────────────────────────────────────────────────────────

    [Fact]
    public void ResetProfile_ResetsThresholdsButKeepsName()
    {
        var p = _sut.CreateProfile("Keep Me", FrontendEnvironmentType.Production);
        p.Performance.MaxStartupRequests = 999;

        _sut.ResetProfile(p.Id);

        p.Name.Should().Be("Keep Me");
        p.Performance.MaxStartupRequests.Should().Be(30);
    }

    [Fact]
    public void ResetProfile_ResetsThresholdsButKeepsTargetUrl()
    {
        var p = _sut.CreateProfile("P", FrontendEnvironmentType.Production);
        p.TargetUrl = "https://example.com";
        p.Performance.MaxStartupSizeBytes = 0;

        _sut.ResetProfile(p.Id);

        p.TargetUrl.Should().Be("https://example.com");
        p.Performance.MaxStartupSizeBytes.Should().Be(8L * 1024 * 1024);
    }

    [Fact]
    public void ResetProfile_ResetsFeatureToggles()
    {
        var p = _sut.CreateProfile("P", FrontendEnvironmentType.QA);
        p.Features.AssetDiscovery    = false;
        p.Features.LighthouseIntegration = true;

        _sut.ResetProfile(p.Id);

        p.Features.AssetDiscovery.Should().BeTrue();
        p.Features.LighthouseIntegration.Should().BeFalse();
    }

    [Fact]
    public void ResetProfile_ResetsCoreWebVitals()
    {
        var p = _sut.CreateProfile("P", FrontendEnvironmentType.QA);
        p.CoreWebVitals.LcpGoodMs = 1111;

        _sut.ResetProfile(p.Id);

        p.CoreWebVitals.LcpGoodMs.Should().Be(2500);
    }

    // ── Delete profile ────────────────────────────────────────────────────────

    [Fact]
    public void DeleteProfile_RemovesProfile()
    {
        var p = _sut.CreateProfile("ToDelete", FrontendEnvironmentType.QA);

        _sut.DeleteProfile(p.Id);

        _sut.Settings.Profiles.Should().BeEmpty();
    }

    [Fact]
    public void DeleteProfile_ClearsActiveIdWhenActiveIsDeleted()
    {
        var p = _sut.CreateProfile("P", FrontendEnvironmentType.Local);
        _sut.SelectActiveProfile(p.Id);

        _sut.DeleteProfile(p.Id);

        _sut.Settings.ActiveProfileId.Should().BeNull();
    }

    [Fact]
    public void DeleteProfile_PreservesActiveIdWhenOtherProfileDeleted()
    {
        var p1 = _sut.CreateProfile("P1", FrontendEnvironmentType.Local);
        var p2 = _sut.CreateProfile("P2", FrontendEnvironmentType.QA);
        _sut.SelectActiveProfile(p1.Id);

        _sut.DeleteProfile(p2.Id);

        _sut.Settings.ActiveProfileId.Should().Be(p1.Id);
    }

    // ── Duplicate profile ─────────────────────────────────────────────────────

    [Fact]
    public void DuplicateProfile_CreatesNewEntryInList()
    {
        var p = _sut.CreateProfile("Original", FrontendEnvironmentType.QA);

        _sut.DuplicateProfile(p.Id);

        _sut.Settings.Profiles.Should().HaveCount(2);
    }

    [Fact]
    public void DuplicateProfile_CopiesSettingsAndAppendsLabel()
    {
        var p = _sut.CreateProfile("Original", FrontendEnvironmentType.QA);
        p.TargetUrl = "https://example-qa.local";

        var copy = _sut.DuplicateProfile(p.Id);

        copy.Name.Should().Be("Original (Copy)");
        copy.Id.Should().NotBe(p.Id);
        copy.TargetUrl.Should().Be("https://example-qa.local");
    }

    [Fact]
    public void DuplicateProfile_CopyIsIndependent()
    {
        var p    = _sut.CreateProfile("Original", FrontendEnvironmentType.QA);
        var copy = _sut.DuplicateProfile(p.Id);

        copy.Performance.MaxStartupRequests = 99;

        p.Performance.MaxStartupRequests.Should().Be(30);
    }

    // ── UpdateProfile ─────────────────────────────────────────────────────────

    [Fact]
    public void UpdateProfile_ReplacesEntryInList()
    {
        var p = _sut.CreateProfile("Original", FrontendEnvironmentType.QA);

        var edited = new FrontendAnalysisProfile
        {
            Id              = p.Id,
            Name            = "Renamed",
            EnvironmentType = FrontendEnvironmentType.Production,
            Performance     = new FrontendPerformanceThresholds(),
            CoreWebVitals   = new CoreWebVitalsThresholds(),
            Security        = new FrontendSecuritySettings(),
            Features        = new FrontendAnalysisFeatureToggles()
        };

        _sut.UpdateProfile(edited);

        _sut.Settings.Profiles.Should().ContainSingle(x => x.Name == "Renamed");
    }

    // ── Safe diagnostics ──────────────────────────────────────────────────────

    [Fact]
    public void GetDiagnostics_ReturnsNoneLabelWhenNoActiveProfile()
    {
        var diag = _sut.GetDiagnostics();

        diag.ActiveProfileName.Should().Be("(None)");
    }

    [Fact]
    public void GetDiagnostics_ReturnsActiveProfileInfo()
    {
        var p = _sut.CreateProfile("QA Profile", FrontendEnvironmentType.QA);
        p.TargetUrl = "https://example-qa.local";
        _sut.SelectActiveProfile(p.Id);

        var diag = _sut.GetDiagnostics();

        diag.ActiveProfileName.Should().Be("QA Profile");
        diag.Environment.Should().Be("QA");
        diag.TargetUrl.Should().Be("https://example-qa.local");
    }

    [Fact]
    public void GetDiagnostics_ListsEnabledCoreFeatures()
    {
        var p = _sut.CreateProfile("P", FrontendEnvironmentType.Local);
        _sut.SelectActiveProfile(p.Id);

        var diag = _sut.GetDiagnostics();

        diag.EnabledFeatures.Should().Contain("Asset Discovery");
        diag.EnabledFeatures.Should().Contain("Security Header Review");
    }

    [Fact]
    public void GetDiagnostics_ListsDisabledFutureFeatures()
    {
        var p = _sut.CreateProfile("P", FrontendEnvironmentType.Local);
        _sut.SelectActiveProfile(p.Id);

        var diag = _sut.GetDiagnostics();

        diag.DisabledFeatures.Should().Contain("Lighthouse Integration");
        diag.DisabledFeatures.Should().Contain("Playwright Runtime Inspection");
    }

    [Fact]
    public void GetDiagnostics_DoesNotExposeSecrets()
    {
        var p = _sut.CreateProfile("P", FrontendEnvironmentType.Production);
        p.Authentication.ExpectedClientId = "secret-client-id";
        p.Authentication.ExpectedTenant   = "tenant-id";
        _sut.SelectActiveProfile(p.Id);

        var diag = _sut.GetDiagnostics();

        // Diagnostics should not include raw client ID or tenant ID
        diag.AuthenticationType.Should().NotContain("secret-client-id");
        diag.AuthenticationType.Should().NotContain("tenant-id");

        // TargetUrl is safe to show; client secrets are not
        diag.ActiveProfileName.Should().NotContain("secret-client-id");
    }

    [Fact]
    public void GetDiagnostics_IncludesValidationStatus()
    {
        var p = _sut.CreateProfile("P", FrontendEnvironmentType.QA);
        p.TargetUrl = "not-a-url";
        _sut.SelectActiveProfile(p.Id);

        var diag = _sut.GetDiagnostics();

        diag.ValidationStatus.IsValid.Should().BeFalse();
        diag.ValidationStatus.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void GetDiagnostics_ThresholdModeIsReported()
    {
        var p = _sut.CreateProfile("P", FrontendEnvironmentType.QA);
        p.Performance.Mode = FrontendThresholdMode.Strict;
        _sut.SelectActiveProfile(p.Id);

        var diag = _sut.GetDiagnostics();

        diag.ThresholdMode.Should().Be("Strict");
    }
}
