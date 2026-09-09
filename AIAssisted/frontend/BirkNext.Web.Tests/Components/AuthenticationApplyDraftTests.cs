using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Component = BirkNext.Web.Components.FrontendAnalysisSettings;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Authentication follows the same Detected → Apply → draft → Save changes model as the endpoint
/// proposals. Applying detected authentication mutates the draft only, surfaces a bold icon-led
/// "Unsaved changes" indicator, and must never invalidate target discovery or claim the URL changed.
/// </summary>
public sealed class AuthenticationApplyDraftTests : BunitContext
{
    private readonly FrontendAnalysisSettingsService _settings = new();
    private readonly Mock<ITargetEnvironmentDetectionApiService> _api = new(MockBehavior.Strict);
    private const string Url = "https://m2lbdev.example.com/";
    private const string Authority = "https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111";
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string ClientId = "22222222-2222-2222-2222-222222222222";
    private const string Redirect = "https://m2lbdev.example.com/authentication/login-callback";
    private const string Rest = "https://m2lbdev-api.example.com/api";

    public AuthenticationApplyDraftTests()
    {
        Services.AddSingleton<IFrontendAnalysisSettingsService>(_settings);
        Services.AddSingleton(_api.Object);
        JSInterop.SetupVoid("birkNextStorage.setItem", _ => true).SetVoidResult();
        _api.Setup(x => x.DetectFromUrlAsync(Url, default)).ReturnsAsync(() => FullDetection());
    }

    private const string PersistedAuthNone = """{ "authenticationType": "None" }""";

    private void UseStoredAuthentication(string authenticationJson) =>
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult($$"""
        {"activeProfileId":"local","profiles":[
          {"id":"local","name":"Local","targetUrl":"https://local.example.com"},
          {"id":"dev","name":"M2LB DEV","environmentType":"Development","targetUrl":"{{Url}}","authentication":{{authenticationJson}}}
        ]}
        """);

    private static TargetEnvironmentDetectionResult FullDetection() => new()
    {
        OriginalUrl = Url, Success = true, Reachability = TargetReachability.Reachable,
        DetectedClientFramework = ClientFrameworkType.BlazorWebAssembly,
        SuggestedEnvironmentType = FrontendEnvironmentType.Development,
        State = DetectionState.ManualAuthenticationVerificationRequired,
        ManualAuthenticationVerificationRequired = true,
        ManualAuthenticationVerificationStatus = ManualAuthenticationVerificationStatus.Required,
        AuthenticationRequired = true,
        DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId,
        DetectedAuthority = Authority,
        DetectedTenantId = Tenant,
        DetectedClientId = ClientId,
        DetectedRedirectUrls = [Redirect],
        DetectedRestBaseUrl = Rest
    };

    private IRenderedComponent<Component> Open(string persistedAuthenticationJson = PersistedAuthNone)
    {
        UseStoredAuthentication(persistedAuthenticationJson);
        var cut = Render<Component>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("M2LB DEV")).Click();
        return cut;
    }

    private static void Click(IRenderedComponent<Component> cut, string label) =>
        cut.FindAll("button").Single(b => b.TextContent.Trim() == label).Click();

    private static void OpenTab(IRenderedComponent<Component> cut, string label) =>
        cut.FindAll("[role=tab]").Single(t => t.TextContent.Trim() == label).Click();

    private static bool ButtonDisabled(IRenderedComponent<Component> cut, string label) =>
        cut.FindAll("button").Single(b => b.TextContent.Trim() == label).HasAttribute("disabled");

    private int SaveCalls() => JSInterop.Invocations.Count(i => i.Identifier == "birkNextStorage.setItem");

    private FrontendAnalysisProfile Persisted() => _settings.Settings.Profiles.Single(x => x.Id == "dev");

    private static string DetectionLabel(IRenderedComponent<Component> cut) => cut.Find(".fa-detection-value").TextContent.Trim();

    /// <summary>
    /// Target discovery is current: the detection result section (reachability, framework, environment)
    /// is shown as a live result, never as "Needs re-check", and no stale/URL-changed copy appears.
    /// While editing, the Target Application tab must additionally still offer the endpoint proposals.
    /// </summary>
    private static void AssertDiscoveryCurrent(IRenderedComponent<Component> cut, bool editing = true)
    {
        DetectionLabel(cut).Should().Be("Manual authentication verification required");
        cut.Markup.Should().Contain("Detection result");
        cut.Find(".fa-result-message").TextContent.Should().NotContain("changed");
        cut.Markup.Should().Contain("Reachable");
        cut.Markup.Should().Contain("Blazor WebAssembly");
        cut.Markup.Should().NotContain("Discoveries are stale");
        cut.Markup.Should().NotContain("target URL changed");
        cut.Markup.Should().NotContain("Frontend URL changed");
        OpenTab(cut, "Target Application");
        cut.Markup.Should().NotContain("Discoveries are stale");
        if (editing)
        {
            cut.Markup.Should().Contain("Detected from target");
            cut.Markup.Should().Contain("Discovered API Endpoints");
            cut.Markup.Should().Contain("Apply REST");
        }
    }

    private static IRenderedComponent<Component> DetectAndApply(IRenderedComponent<Component> cut)
    {
        Click(cut, "Detect settings");
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Detected from target"));
        OpenTab(cut, "Authentication");
        Click(cut, "Apply authentication");
        return cut;
    }

    // ── 20. Apply test ──────────────────────────────────────────────────────────

    [Fact]
    public void ApplyAuthentication_PopulatesDraft_MarksDirty_WithoutSavingActivatingOrStalingDiscovery()
    {
        var cut = Open();
        Click(cut, "Detect settings");
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Detected from target"));
        var savesBeforeApply = SaveCalls();

        OpenTab(cut, "Authentication");
        cut.Markup.Should().NotContain("Apply detected authentication settings");
        cut.Find("#configured-authentication-type").GetAttribute("value").Should().Be("None");
        cut.FindAll(".fa-unsaved-notice").Should().BeEmpty("nothing has been applied yet");
        ButtonDisabled(cut, "Save changes").Should().BeTrue();
        cut.Markup.Should().Contain("Detected Authentication");

        Click(cut, "Apply authentication");

        // Configured draft populated from the detected proposal
        cut.Find("#configured-authentication-type").GetAttribute("value").Should().Be("MicrosoftEntraId");
        cut.Find("#configured-authority").GetAttribute("value").Should().Be(Authority);
        cut.Find("#configured-tenant").GetAttribute("value").Should().Be(Tenant);
        cut.Find("#configured-client-id").GetAttribute("value").Should().Be(ClientId);
        cut.Find("#configured-redirect-urls").GetAttribute("value").Should().Contain(Redirect);

        // Detected evidence remains visible as provenance
        cut.Markup.Should().Contain("Detected Authentication");
        cut.Markup.Should().Contain("Apply authentication");

        // Dirty → top-level Save enabled, unsaved indicator visible, no second Save inside Authentication
        ButtonDisabled(cut, "Save changes").Should().BeFalse();
        cut.Find(".fa-unsaved-notice").TextContent.Should().Contain("Unsaved changes");
        cut.FindAll("button").Where(b => b.TextContent.Contains("Save", StringComparison.OrdinalIgnoreCase))
            .Select(b => b.TextContent.Trim()).Should().Equal("Save changes");

        // Draft only: nothing persisted, no save, no activation, no manual verification result
        Persisted().Authentication.AuthenticationType.Should().Be(FrontendAuthenticationType.None);
        Persisted().Authentication.ExpectedAuthority.Should().BeNull();
        Persisted().ManualVerification.Should().BeNull();
        SaveCalls().Should().Be(savesBeforeApply);
        _settings.Settings.ActiveProfileId.Should().Be("local");
        cut.Markup.Should().Contain("Manual authentication verification required");
        cut.Markup.Should().NotContain("Manual authentication verification passed");
        ButtonDisabled(cut, "Set as Active").Should().BeTrue();
        cut.Find("#activation-gate-reason").TextContent.Should().Contain("Unsaved changes");

        // Target discovery remains current — the previous bug produced "Discoveries are stale" here
        AssertDiscoveryCurrent(cut);
        _api.Verify(x => x.DetectFromUrlAsync(Url, default), Times.Once);
        _api.VerifyNoOtherCalls();
    }

    // ── 21. Unsaved indicator test ─────────────────────────────────────────────

    [Fact]
    public void UnsavedIndicator_IsBoldIconLed_AndAccessible()
    {
        var cut = DetectAndApply(Open());

        var notice = cut.Find(".fa-unsaved-notice");
        notice.GetAttribute("role").Should().Be("status");
        notice.QuerySelector("strong")!.TextContent.Trim().Should().Be("Unsaved changes");
        notice.QuerySelector("svg").Should().NotBeNull("the indicator is icon-led");
        notice.QuerySelector("svg")!.GetAttribute("aria-hidden").Should().Be("true", "icon must not be the only carrier of meaning");
        notice.TextContent.Should().Contain("Authentication settings have been applied to the draft. Use Save changes to persist them.");

        // Placed next to Configured Authentication, not at the top of the page
        var configured = cut.Find("#configured-authentication-heading");
        configured.TextContent.Trim().Should().Be("Configured Authentication");
        configured.NextElementSibling!.ClassList.Should().Contain("fa-unsaved-notice");

        // Apply and Save are real buttons with clear accessible names (keyboard reachable by default)
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Apply authentication").GetAttribute("type").Should().Be("button");
        cut.FindAll("button").Should().ContainSingle(b => b.TextContent.Trim() == "Save changes");
    }

    // ── 22. Save test ──────────────────────────────────────────────────────────

    [Fact]
    public void SaveChanges_PersistsAppliedAuthentication_ClearsIndicator_KeepsDiscoveryCurrent()
    {
        var cut = DetectAndApply(Open());
        var savesBefore = SaveCalls();

        Click(cut, "Save changes");

        cut.WaitForAssertion(() => Persisted().Authentication.AuthenticationType.Should().Be(FrontendAuthenticationType.MicrosoftEntraId));
        Persisted().Authentication.ExpectedAuthority.Should().Be(Authority);
        Persisted().Authentication.ExpectedTenant.Should().Be(Tenant);
        Persisted().Authentication.ExpectedClientId.Should().Be(ClientId);
        Persisted().Authentication.AllowedRedirectUrls.Should().Equal(Redirect);
        SaveCalls().Should().Be(savesBefore + 1, "one environment persistence path");

        // Dirty cleared, indicator gone, edit mode exited
        cut.FindAll(".fa-unsaved-notice").Should().BeEmpty();
        cut.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == "Save changes");

        // Manual verification and activation remain independent of Save
        Persisted().ManualVerification.Should().BeNull();
        cut.Markup.Should().Contain("Manual authentication verification required");
        _settings.Settings.ActiveProfileId.Should().Be("local");
        ButtonDisabled(cut, "Set as Active").Should().BeTrue();
        cut.Find("#activation-gate-reason").TextContent.Should().Contain("manual authentication verification");

        AssertDiscoveryCurrent(cut, editing: false);
    }

    // ── 23. Cancel test ────────────────────────────────────────────────────────

    [Fact]
    public void Cancel_RevertsDraft_ClearsIndicator_KeepsDiscoveryCurrent()
    {
        var cut = DetectAndApply(Open());
        var savesBefore = SaveCalls();

        Click(cut, "Cancel");

        Persisted().Authentication.AuthenticationType.Should().Be(FrontendAuthenticationType.None);
        Persisted().Authentication.ExpectedAuthority.Should().BeNull();
        SaveCalls().Should().Be(savesBefore);
        cut.FindAll(".fa-unsaved-notice").Should().BeEmpty();
        cut.FindAll(".fa-dl-row").Single(r => r.QuerySelector("dt")!.TextContent.Trim() == "Authentication Type")
            .QuerySelector("dd")!.TextContent.Trim().Should().Be("None", "view mode shows persisted authentication");

        AssertDiscoveryCurrent(cut, editing: false);
    }

    // ── 24. Reapply same auth test ─────────────────────────────────────────────

    [Fact]
    public void ApplyAuthentication_WhenDetectedEqualsPersisted_DoesNotCreateFalseDirtyState()
    {
        var cut = DetectAndApply(Open($$"""
        { "authenticationType": "MicrosoftEntraId", "expectedAuthority": "{{Authority}}", "expectedTenant": "{{Tenant}}",
          "expectedClientId": "{{ClientId}}", "allowedRedirectUrls": ["{{Redirect}}"] }
        """));

        cut.FindAll(".fa-unsaved-notice").Should().BeEmpty();
        ButtonDisabled(cut, "Save changes").Should().BeTrue();
        cut.Find("#configured-authentication-type").GetAttribute("value").Should().Be("MicrosoftEntraId");
        Persisted().Authentication.AllowedRedirectUrls.Should().Equal(Redirect);
        AssertDiscoveryCurrent(cut);
    }

    // ── 25. Partial auth detection test ────────────────────────────────────────

    [Fact]
    public void ApplyAuthentication_PartialDetection_CopiesOnlyDetectedValues()
    {
        _api.Setup(x => x.DetectFromUrlAsync(Url, default)).ReturnsAsync(() =>
        {
            var partial = FullDetection();
            partial.DetectedTenantId = null;
            partial.DetectedClientId = null;
            partial.DetectedRedirectUrls = [];
            return partial;
        });
        var cut = DetectAndApply(Open());

        cut.Find("#configured-authentication-type").GetAttribute("value").Should().Be("MicrosoftEntraId");
        cut.Find("#configured-authority").GetAttribute("value").Should().Be(Authority);
        (cut.Find("#configured-tenant").GetAttribute("value") ?? "").Should().BeEmpty("not fabricated");
        (cut.Find("#configured-client-id").GetAttribute("value") ?? "").Should().BeEmpty("not fabricated");
        (cut.Find("#configured-redirect-urls").GetAttribute("value") ?? "").Should().BeEmpty("not fabricated");
        cut.Find(".fa-unsaved-notice").TextContent.Should().Contain("Unsaved changes");
        ButtonDisabled(cut, "Save changes").Should().BeFalse();
        AssertDiscoveryCurrent(cut);
    }

    // ── 26. Manual verification stale independence ─────────────────────────────

    [Fact]
    public void AuthChange_StalesPassedManualVerification_ButTargetDiscoveryRemainsCurrent()
    {
        var cut = Open();
        Click(cut, "Detect settings");
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Detected from target"));
        Click(cut, "Open verification instructions");
        Click(cut, "Mark verification passed");
        cut.WaitForAssertion(() => Persisted().ManualVerification!.Result.Should().Be(ManualAuthenticationVerificationStatus.Passed));
        cut.Markup.Should().Contain("Manual authentication verification passed");
        ButtonDisabled(cut, "Set as Active").Should().BeFalse();

        OpenTab(cut, "Authentication");
        Click(cut, "Apply authentication");

        // Verification context changed → manual verification stale, activation blocked
        cut.Markup.Should().Contain("Manual authentication verification stale");
        ButtonDisabled(cut, "Set as Active").Should().BeTrue();
        Persisted().ManualVerification!.Result.Should().Be(ManualAuthenticationVerificationStatus.Passed, "persisted evidence is untouched; staleness is computed");

        // …but endpoint/framework/environment discovery is NOT stale
        cut.Markup.Should().Contain("Manual authentication verification stale");
        AssertDiscoveryCurrent(cut);
    }

    [Fact]
    public void AfterSave_AuthChange_RequiresRepeatDetectionForManualVerification_WithAccurateReason()
    {
        var cut = DetectAndApply(Open());
        Click(cut, "Save changes");
        cut.WaitForAssertion(() => Persisted().Authentication.AuthenticationType.Should().Be(FrontendAuthenticationType.MicrosoftEntraId));

        // Verification context changed since detection → recording is gated with an accurate reason
        cut.Markup.Should().Contain("Authentication or environment settings changed since detection.");
        cut.Markup.Should().Contain("Run Detect settings again before recording manual verification.");
        cut.Markup.Should().NotContain("target URL changed");
        Click(cut, "Open verification instructions");
        ButtonDisabled(cut, "Mark verification passed").Should().BeTrue();
        AssertDiscoveryCurrent(cut, editing: false);
    }

    // ── 27. Target URL change test ─────────────────────────────────────────────

    [Fact]
    public void FrontendUrlChange_StillStalesDiscovery_WithTargetUrlChangedReason()
    {
        var cut = Open();
        Click(cut, "Detect settings");
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Detected from target"));

        cut.Find("input[type=url]").Change("https://other.example.com/");

        DetectionLabel(cut).Should().Be("Needs re-check");
        cut.Markup.Should().Contain("Discoveries are stale");
        cut.Markup.Should().Contain("The target URL changed since detection.");
        cut.Markup.Should().Contain("Frontend URL changed. Run Detect settings again before activating.");
        cut.Markup.Should().NotContain("Apply REST");
        ButtonDisabled(cut, "Set as Active").Should().BeTrue();
    }

    // ── 28. Unrelated field change test ────────────────────────────────────────

    [Fact]
    public void UnrelatedDraftChanges_DoNotStaleDiscovery()
    {
        var cut = Open();
        Click(cut, "Detect settings");
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Detected from target"));

        OpenTab(cut, "General");
        cut.Find("textarea.form-control").Change("Some notes about this environment");

        OpenTab(cut, "Performance Thresholds");
        cut.Find("input.fa-threshold-input").Change("42");

        OpenTab(cut, "Feature Toggles");
        var toggle = cut.FindAll("input[type=checkbox]").First();
        toggle.Change(!toggle.HasAttribute("checked"));

        ButtonDisabled(cut, "Save changes").Should().BeFalse("draft is dirty");
        cut.FindAll(".fa-unsaved-notice").Should().BeEmpty("authentication draft is unchanged");
        AssertDiscoveryCurrent(cut);
    }

    // ── 19. Profile switch regression ──────────────────────────────────────────

    [Fact]
    public void ProfileSwitch_DoesNotLeakDetectionEvidence()
    {
        var cut = DetectAndApply(Open());
        Click(cut, "Cancel");
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("Local")).Click();

        cut.Markup.Should().NotContain("Detected from target");
        DetectionLabel(cut).Should().Be("Not checked");
        _api.Verify(x => x.DetectFromUrlAsync(Url, default), Times.Once);
        _api.VerifyNoOtherCalls();
    }
}

/// <summary>Typed target discovery fingerprint: identity only, never authentication or other settings.</summary>
public sealed class TargetDiscoveryFingerprintTests
{
    private static FrontendAnalysisProfile Profile() => new()
    {
        Id = "dev", TargetUrl = "https://m2lbdev.example.com/",
        Authentication = new() { AuthenticationType = FrontendAuthenticationType.None }
    };

    [Fact]
    public void AuthenticationChanges_DoNotStaleDiscovery()
    {
        var profile = Profile();
        var fingerprint = TargetDiscoveryFingerprint.For(profile);
        profile.Authentication.AuthenticationType = FrontendAuthenticationType.MicrosoftEntraId;
        profile.Authentication.ExpectedAuthority = "https://login.microsoftonline.com/x";
        profile.Authentication.ExpectedTenant = "t";
        profile.Authentication.ExpectedClientId = "c";
        profile.Authentication.AllowedRedirectUrls = ["https://m2lbdev.example.com/cb"];
        profile.Authentication.VerificationMode = AuthenticationVerificationMode.ManualManagedEdge;
        fingerprint.StaleReasonFor(profile).Should().Be(TargetDiscoveryStaleReason.None);
    }

    [Fact]
    public void UnrelatedSettingsChanges_DoNotStaleDiscovery()
    {
        var profile = Profile();
        var fingerprint = TargetDiscoveryFingerprint.For(profile);
        profile.Notes = "n";
        profile.Performance.MaxRestPayloadBytes += 1;
        profile.Features.RestAnalysis = !profile.Features.RestAnalysis;
        profile.RequestTimeoutSeconds++;
        profile.Security.AllowedRestHosts = ["api.example.com"];
        fingerprint.StaleReasonFor(profile).Should().Be(TargetDiscoveryStaleReason.None);
    }

    [Fact]
    public void ButAuthenticationChanges_DoStaleManualVerificationContext()
    {
        var profile = Profile();
        var before = ManualAuthenticationVerificationEvidence.Fingerprint(profile);
        profile.Authentication.AuthenticationType = FrontendAuthenticationType.MicrosoftEntraId;
        ManualAuthenticationVerificationEvidence.Fingerprint(profile).Should().NotBe(before);
    }

    [Fact]
    public void TargetUrlChange_IsTypedAsTargetUrlChanged()
    {
        var profile = Profile();
        var fingerprint = TargetDiscoveryFingerprint.For(profile);
        profile.TargetUrl = "https://other.example.com/";
        fingerprint.StaleReasonFor(profile).Should().Be(TargetDiscoveryStaleReason.TargetUrlChanged);
    }

    [Fact]
    public void ProfileIdentityChange_IsTypedAsProfileChanged()
    {
        var profile = Profile();
        var fingerprint = TargetDiscoveryFingerprint.For(profile);
        profile.Id = "qa";
        fingerprint.StaleReasonFor(profile).Should().Be(TargetDiscoveryStaleReason.ProfileChanged);
    }

    [Theory]
    [InlineData("https://m2lbdev.example.com")]
    [InlineData("HTTPS://M2LBDEV.EXAMPLE.COM/")]
    [InlineData("https://m2lbdev.example.com:443/")]
    [InlineData("https://m2lbdev.example.com/#fragment")]
    public void EquivalentUrls_AreNotStale(string equivalent)
    {
        var profile = Profile();
        var fingerprint = TargetDiscoveryFingerprint.For(profile);
        profile.TargetUrl = equivalent;
        fingerprint.StaleReasonFor(profile).Should().Be(TargetDiscoveryStaleReason.None);
    }
}
