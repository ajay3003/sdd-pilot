using System.Linq;
using AngleSharp.Dom;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using BirkNext.Web.Tests.Integration;
using Component = BirkNext.Web.Components.FrontendAnalysisSettings;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Authentication follows the Check → review differences → Apply selected changes → draft → Save changes model.
/// Applying selected detected values mutates the draft only, surfaces a bold icon-led "Unsaved changes" indicator,
/// and must never invalidate target discovery or claim the URL changed.
/// </summary>
public sealed partial class AuthenticationApplyDraftTests : BunitContext
{
    private readonly FrontendAnalysisSettingsService _settings = new();
    private readonly Mock<ITargetEnvironmentDetectionApiService> _api = new(MockBehavior.Strict);
    private readonly FakeIntegrationCatalogApi _integrations = new();
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
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
        Services.AddSingleton<IIntegrationCatalogApiService>(_integrations);
        Services.AddSingleton(new IntegrationMappingEvidenceSession());
        JSInterop.SetupVoid("birkNextStorage.setItem", _ => true).SetVoidResult();
        JSInterop.SetupVoid("birkNextStorage.setDiscovery", _ => true).SetVoidResult();
        JSInterop.Setup<string?>("birkNextStorage.getDiscovery").SetResult(null);
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
        DetectedRestBaseUrl = Rest,
        DetectedGraphQlEndpoint = Url + "graphql",
        DetectedHealthEndpoint = Url + "health",
        DetectedSwaggerUrl = Url + "swagger.json"
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

    private static string DetectionLabel(IRenderedComponent<Component> cut)
    {
        OpenTab(cut, "Target Application");
        return cut.Find(".fa-detection-value").TextContent.Trim();
    }

    /// <summary>
    /// Target discovery is current: the detection result section (reachability, framework, environment)
    /// is shown as a live result, never as "Needs re-check", and no stale/URL-changed copy appears.
    /// While editing, the Target Application tab must additionally still offer the endpoint proposals.
    /// </summary>
    private static void AssertDiscoveryCurrent(IRenderedComponent<Component> cut, bool editing = true)
    {
        OpenTab(cut, "Target Application");
        DetectionLabel(cut).Should().BeOneOf(["Target detection complete", "Target detected — unsaved changes", "Target detected — recheck after settings change"],
            "discovery is current; only authentication edits keep the sign-in check from matching the detection");
        cut.Find("[data-testid=target-check]").TextContent.Should().Contain("Target check");
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
            // Target Application keeps only the identity/runtime summary. The endpoint discovery proposals were removed entirely (from every tab).
            cut.Markup.Should().Contain("Detected from target");
            cut.Markup.Should().NotContain("Discovered API Endpoints");
            cut.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == "Apply REST");
            OpenTab(cut, "Endpoint Discovery");
            cut.Markup.Should().NotContain("Discovered API Endpoints");
            cut.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == "Apply REST");
            OpenTab(cut, "Target Application");
        }
    }

    private static IRenderedComponent<Component> DetectAndApply(IRenderedComponent<Component> cut)
    {
        cut.Find("#target-tab-target").Click(); Click(cut, "Check target application");
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Detected from target"));
        OpenTab(cut, "Authentication");
        UseAllDetected(cut);
        return cut;
    }

    /// <summary>Chooses "Use detected value" for every listed difference, then Apply selected changes. Nothing is pre-selected.</summary>
    internal static void UseAllDetected<T>(IRenderedComponent<T> cut) where T : Microsoft.AspNetCore.Components.IComponent
    {
        // Values that pass the trust policy are adopted on detection; the rest wait in "Configuration needs review", decided one by one
        // and saved immediately (the explicit decision is the approval).
        while (cut.FindAll("[data-testid^=awf-use-]").FirstOrDefault() is { } use) use.Click();
    }

    // ── 20. Apply test ──────────────────────────────────────────────────────────

    [Fact]
    public void DetectOnly_RemainsCleanAndReadOnlyAcrossEveryTab()
    {
        var cut = Open("""{ "authenticationType": "MicrosoftEntraId" }""");
        var original = System.Text.Json.JsonSerializer.Serialize(Persisted());
        cut.Find("#target-tab-target").Click(); Click(cut, "Check target application");
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Detected from target"));
        foreach (var tab in new[] { "Target Application", "Authentication", "Endpoint Discovery", "Frontend Review Engines", "Core Web Vitals", "Performance Thresholds", "Integration Catalog" })
        {
            OpenTab(cut, tab);
            // Settings form stays read-only; the managed Edge runtime panel has no editable control either (no runtime trust opt-in).
            // Integrations are a backend catalog saved per record, outside the profile draft: its search/filter controls are not
            // profile fields, and nothing there is written unless the user saves an integration.
            // Authentication differences carry review decisions (radio buttons), which are not profile fields and write nothing.
            if (tab != "Integration Catalog") cut.FindAll("input:not([data-testid^=auth-decision-]):not([data-testid^=awf-]), select, textarea").Should().BeEmpty(tab);
            cut.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == "Save changes" || b.TextContent.Trim() == "Cancel", tab);
        }
        _integrations.Saved.Should().BeEmpty("detecting settings never writes an integration");
        _integrations.Calls.Should().NotContain(c => c == "create" || c == "update" || c.StartsWith("delete") || c.StartsWith("enabled"));
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        ((bool)typeof(Component).GetField("_isEditMode", flags)!.GetValue(cut.Instance)!).Should().BeFalse();
        ((bool)typeof(Component).GetProperty("IsDirty", flags)!.GetValue(cut.Instance)!).Should().BeFalse();
        System.Text.Json.JsonSerializer.Serialize(Persisted()).Should().Be(original);
        SaveCalls().Should().Be(0);
        AssertDiscoveryCurrent(cut);
    }

    [Fact]
    public void ExplicitEditAndCancel_ReturnToReadOnlyWithoutDetection()
    {
        var cut = Open();
        Click(cut, "Edit Environment");
        cut.Find("textarea.form-control").Change("Explicit edit");
        ButtonDisabled(cut, "Save changes").Should().BeFalse();
        Click(cut, "Cancel");
        cut.FindAll("textarea").Should().BeEmpty();
        cut.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == "Cancel");
        SaveCalls().Should().Be(0);
    }


    // ── 21. Unsaved indicator test ─────────────────────────────────────────────


    // ── 22. Save test ──────────────────────────────────────────────────────────


    // ── 23. Cancel test ────────────────────────────────────────────────────────


    // ── 24. Reapply same auth test ─────────────────────────────────────────────

    [Fact]
    public void ApplyAuthentication_WhenDetectedEqualsPersisted_DoesNotCreateFalseDirtyState()
    {
        var cut = Open($$"""
        { "authenticationType": "MicrosoftEntraId", "expectedAuthority": "{{Authority}}", "expectedTenant": "{{Tenant}}",
          "expectedClientId": "{{ClientId}}", "allowedRedirectUrls": ["{{Redirect}}"] }
        """);
        cut.Find("#target-tab-target").Click(); Click(cut, "Check target application");
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Detected from target"));
        OpenTab(cut, "Authentication");

        // Saved and detected match: nothing to review, nothing to apply
        cut.Find("[data-testid=auth-status-auth-settings]").TextContent.Should().Be("Matches saved configuration");
        cut.FindAll("[data-testid=authentication-differences], [data-testid=authentication-apply-selected]").Should().BeEmpty();
        cut.FindAll(".fa-unsaved-notice").Should().BeEmpty();
        cut.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == "Save changes");
        cut.FindAll("#configured-authentication-type").Should().BeEmpty();
        Persisted().Authentication.AllowedRedirectUrls.Should().Equal(Redirect);
        AssertDiscoveryCurrent(cut);
    }

    // ── 25. Partial auth detection test ────────────────────────────────────────


    // ── 26. Manual sign-in check stale independence ─────────────────────────────


    // ── Defect 1: applying the detector's own proposal must not demand re-detection ──



    /// <summary>"Apply detected type" is also the detector's own proposal and must not require re-detection.</summary>
    [Fact]
    public void ApplyDetectedType_DoesNotRequireRedetect()
    {
        _api.Setup(x => x.DetectFromUrlAsync(Url, default)).ReturnsAsync(() =>
        {
            var d = FullDetection();
            d.SuggestedEnvironmentType = FrontendEnvironmentType.Production;
            return d;
        });
        var cut = Open();
        cut.Find("#target-tab-target").Click(); Click(cut, "Check target application");
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Detected from target"));
        // "Apply detected type" is offered both in the detection result actions and inline in the Target tab
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Apply detected type").Click();
        Click(cut, "Save changes");
        cut.WaitForAssertion(() => Persisted().EnvironmentType.Should().Be(FrontendEnvironmentType.Production));

        cut.Markup.Should().NotContain("changed since detection");
        OpenTab(cut, "Authentication");
        Click(cut, "Open manual sign-in check");
        ButtonDisabled(cut, "Record sign-in check completed").Should().BeFalse();
    }

    // ── Defect 2: draft vs persisted authentication must be labelled, never contradictory ──


    // ── 27. Target URL change test ─────────────────────────────────────────────

    [Fact]
    public void FrontendUrlChange_StillStalesDiscovery_WithTargetUrlChangedReason()
    {
        var cut = Open();
        cut.Find("#target-tab-target").Click(); Click(cut, "Check target application");
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Detected from target"));

        Click(cut, "Edit Environment");
        cut.Find("input[type=url]").Change("https://other.example.com/");

        DetectionLabel(cut).Should().Be("Needs re-check");
        cut.Markup.Should().Contain("Discoveries are stale");
        cut.Markup.Should().Contain("The target URL changed since detection.");
        cut.Markup.Should().Contain("Frontend URL changed. Recheck the target application to refresh detection evidence.");
        cut.Markup.Should().NotContain("Apply REST");
        ButtonDisabled(cut, "Set as Active").Should().BeTrue();
    }

    // ── 28. Unrelated field change test ────────────────────────────────────────

    [Fact]
    public void UnrelatedDraftChanges_DoNotStaleDiscovery()
    {
        var cut = Open();
        cut.Find("#target-tab-target").Click(); Click(cut, "Check target application");
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Detected from target"));

        Click(cut, "Edit Environment");
        OpenTab(cut, "General");
        cut.Find("textarea.form-control").Change("Some notes about this environment");

        OpenTab(cut, "Performance Thresholds");
        cut.Find("input.fa-threshold-input").Change("42");

        OpenTab(cut, "Frontend Review Engines");
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
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("Local")).Click();

        cut.Markup.Should().NotContain("Detected from target");
        DetectionLabel(cut).Should().Be("Not checked");
        _api.Verify(x => x.DetectFromUrlAsync(Url, default), Times.Once);
        _api.VerifyNoOtherCalls();
    }

    // ── Guided workflow: detect → validate → populate safe missing settings → review exceptions ──────────────────────────────

    private const string SavedDifferentClient = """{ "authenticationType": "MicrosoftEntraId", "expectedClientId": "99999999-9999-9999-9999-999999999999" }""";

    private void HighConfidenceDetection() =>
        _api.Setup(x => x.DetectFromUrlAsync(Url, default)).ReturnsAsync(() => { var d = FullDetection(); d.Confidence = DetectionConfidence.High; return d; });

    private IRenderedComponent<Component> DetectOnAuthenticationTab(string persisted = PersistedAuthNone)
    {
        HighConfidenceDetection();
        var cut = Open(persisted);
        OpenTab(cut, "Authentication");
        cut.Find("[data-testid=awf-next]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=awf-steps]"));
        return cut;
    }

    [Fact]
    public void Detection_fills_validated_empty_identifiers_with_provenance_and_never_copies_the_provider()
    {
        var cut = DetectOnAuthenticationTab();
        cut.WaitForAssertion(() => Persisted().Authentication.ExpectedClientId.Should().Be(ClientId));
        var saved = Persisted();
        saved.Authentication.ExpectedAuthority.Should().Be(Authority);
        saved.Authentication.ExpectedTenant.Should().Be(Tenant);
        saved.Authentication.AllowedRedirectUrls.Should().Contain(Redirect, "a redirect URL on the target's own origin is safe to add");
        saved.Authentication.AuthenticationType.Should().Be(FrontendAuthenticationType.None, "the observed provider is used directly, never copied");
        saved.AuthenticationAdoption!.Provenance.Should().OnlyContain(p => p.Source == "Detected" && p.Validation == "Passed trust policy");
        cut.Find("[data-testid=awf-adopted]").TextContent.Should().Contain("Expected Client ID").And.Contain("Saved values were not changed");
        cut.FindAll("[data-testid=awf-review]").Should().BeEmpty("routine differences need no decision");
        cut.Find("[data-testid=awf-config-tenant]").TextContent.Should().Be("Configured");
        cut.Find("[data-testid=awf-config-application]").TextContent.Should().Be("Configured");
        cut.Find("[data-testid=awf-status-title]").TextContent.Should().NotContain("Signed in", "a configuration is not a session");
        cut.Find("[data-testid=awf-dim-session]").TextContent.Should().Contain("Not started");
        cut.Markup.Should().NotContain(">MicrosoftEntraId<", "the provider is named as a person reads it");
        _api.Verify(x => x.DetectFromUrlAsync(Url, default), Times.Once);
    }

    [Fact]
    public void A_differing_saved_value_waits_for_a_decision_and_keep_existing_is_remembered()
    {
        var cut = DetectOnAuthenticationTab(SavedDifferentClient);
        var review = cut.WaitForElement("[data-testid=awf-review]");
        review.QuerySelectorAll("[data-testid=awf-review-item]").Select(i => i.GetAttribute("data-setting")).Should().Equal(["Expected Client ID"]);
        cut.Find("[data-testid=awf-status-title]").TextContent.Should().Be("Configuration needs review");
        Persisted().Authentication.ExpectedClientId.Should().Be("99999999-9999-9999-9999-999999999999", "a conflict is never applied automatically");
        Persisted().Authentication.ExpectedTenant.Should().Be(Tenant, "the empty, validated settings were still filled in");

        cut.Find("[data-testid=awf-keep-expected-client-id]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=awf-review]").Should().BeEmpty());
        Persisted().Authentication.ExpectedClientId.Should().Be("99999999-9999-9999-9999-999999999999");
        Persisted().AuthenticationAdoption!.Rejected.Should().ContainSingle().Which.Detected.Should().Be(ClientId);
        cut.Find("[data-testid=awf-adopted]").TextContent.Should().Contain("will not be proposed again");
    }

    [Fact]
    public void Use_detected_value_saves_the_decision_with_its_previous_value_for_restore()
    {
        var cut = DetectOnAuthenticationTab(SavedDifferentClient);
        cut.WaitForElement("[data-testid=awf-use-expected-client-id]").Click();
        cut.WaitForAssertion(() => Persisted().Authentication.ExpectedClientId.Should().Be(ClientId));
        Persisted().AuthenticationAdoption!.Provenance.Last().Should().Match<AuthenticationValueProvenance>(p => p.Source == "Reviewed" && p.Previous == "99999999-9999-9999-9999-999999999999");
    }

    [Fact]
    public void The_guided_workflow_leads_and_the_technical_surface_is_collapsed_under_advanced_diagnostics()
    {
        var cut = Open();
        OpenTab(cut, "Authentication");
        cut.Find("[data-testid=auth-workflow]").Should().NotBeNull();
        cut.Find("[data-testid=awf-next]").GetAttribute("data-action").Should().Be(AuthenticationSettingsChecks.CheckActionId, "nothing was detected yet: detect first");
        cut.Find("[data-testid=auth-advanced-diagnostics]").HasAttribute("open").Should().BeFalse("diagnostics are collapsed by default");
        cut.Find("[data-testid=auth-advanced-toggle]").Click();
        cut.Find("[data-testid=auth-advanced-diagnostics]").HasAttribute("open").Should().BeTrue();
        cut.FindAll("[data-testid=awf-mode] input").Should().HaveCount(2, "attended and unattended stay separate choices");
        cut.Find("[data-testid=awf-mode-unattended]").Change(true);
        cut.FindAll("[data-testid=awf-step]").Select(s => s.GetAttribute("data-step")).Should().Equal(["configuration", "identity", "verify", "discovery"],
            "unattended shows only its own prerequisites — no proxy, certificate or browser");
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
        profile.Features.EnableLighthouseEngine = !profile.Features.EnableLighthouseEngine;
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

/// <summary>Applying the detector's own proposal ≠ detection-invalidating configuration change.</summary>
public sealed class DetectionProposalConsistencyTests
{
    private static FrontendAnalysisProfile Snapshot() => new()
    {
        Id = "dev", TargetUrl = "https://m2lbdev.example.com/", EnvironmentType = FrontendEnvironmentType.Development,
        Authentication = new() { AuthenticationType = FrontendAuthenticationType.None, AllowedRedirectUrls = ["https://existing.example.com/cb"] }
    };

    private static TargetEnvironmentDetectionResult Detection() => new()
    {
        DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId,
        DetectedAuthority = "https://login.microsoftonline.com/t/v2.0", DetectedTenantId = "t", DetectedClientId = "c",
        DetectedRedirectUrls = ["https://m2lbdev.example.com/cb"], SuggestedEnvironmentType = FrontendEnvironmentType.Production
    };

    private static FrontendAnalysisProfile Clone(FrontendAnalysisProfile p) =>
        System.Text.Json.JsonSerializer.Deserialize<FrontendAnalysisProfile>(System.Text.Json.JsonSerializer.Serialize(p))!;

    private static void ApplyProposal(FrontendAnalysisProfile p, TargetEnvironmentDetectionResult d)
    {
        p.Authentication.AuthenticationType = d.DetectedAuthenticationType;
        p.Authentication.ExpectedAuthority = d.DetectedAuthority;
        p.Authentication.ExpectedTenant = d.DetectedTenantId;
        p.Authentication.ExpectedClientId = d.DetectedClientId;
        var merged = new HashSet<string>(p.Authentication.AllowedRedirectUrls, StringComparer.OrdinalIgnoreCase);
        foreach (var u in d.DetectedRedirectUrls) merged.Add(u);
        p.Authentication.AllowedRedirectUrls = merged.ToList();
    }

    [Fact]
    public void AppliedProposal_YieldsDetectionTimeFingerprint()
    {
        var snapshot = Snapshot(); var detection = Detection();
        var current = Clone(snapshot);
        ApplyProposal(current, detection);
        current.EnvironmentType = FrontendEnvironmentType.Production;
        ManualAuthenticationVerificationEvidence.Fingerprint(current).Should().NotBe(ManualAuthenticationVerificationEvidence.Fingerprint(snapshot), "raw fingerprint does change");
        DetectionProposalConsistency.ConsistentFingerprint(current, snapshot, detection)
            .Should().Be(ManualAuthenticationVerificationEvidence.Fingerprint(snapshot), "the applied proposal is evidence-consistent");
    }

    [Fact]
    public void PartialProposal_OnlyAppliedFields_IsConsistent()
    {
        var snapshot = Snapshot(); var detection = Detection();
        detection.DetectedTenantId = null; detection.DetectedClientId = null; detection.DetectedRedirectUrls = [];
        var current = Clone(snapshot);
        current.Authentication.AuthenticationType = detection.DetectedAuthenticationType;
        current.Authentication.ExpectedAuthority = detection.DetectedAuthority;
        DetectionProposalConsistency.ConsistentFingerprint(current, snapshot, detection)
            .Should().Be(ManualAuthenticationVerificationEvidence.Fingerprint(snapshot));
    }

    [Theory]
    [InlineData("authority")]
    [InlineData("tenant")]
    [InlineData("clientId")]
    [InlineData("redirects")]
    [InlineData("url")]
    [InlineData("environment")]
    [InlineData("timeout")]
    public void DivergingEdit_ChangesFingerprint(string edit)
    {
        var snapshot = Snapshot(); var detection = Detection();
        var current = Clone(snapshot);
        ApplyProposal(current, detection);
        switch (edit)
        {
            case "authority": current.Authentication.ExpectedAuthority = "https://login.microsoftonline.com/other/v2.0"; break;
            case "tenant": current.Authentication.ExpectedTenant = "other"; break;
            case "clientId": current.Authentication.ExpectedClientId = "other"; break;
            case "redirects": current.Authentication.AllowedRedirectUrls.Add("https://evil.example.com/cb"); break;
            case "url": current.TargetUrl = "https://other.example.com/"; break;
            case "environment": current.EnvironmentType = FrontendEnvironmentType.QA; break; // neither snapshot nor proposal
            case "timeout": current.RequestTimeoutSeconds++; break;
        }
        DetectionProposalConsistency.ConsistentFingerprint(current, snapshot, detection)
            .Should().NotBe(ManualAuthenticationVerificationEvidence.Fingerprint(snapshot));
    }

    [Fact]
    public void UnchangedProfile_IsConsistent()
    {
        var snapshot = Snapshot();
        DetectionProposalConsistency.ConsistentFingerprint(Clone(snapshot), snapshot, Detection())
            .Should().Be(ManualAuthenticationVerificationEvidence.Fingerprint(snapshot));
    }

    [Fact]
    public void Neutralize_DoesNotMutateInput()
    {
        var snapshot = Snapshot(); var detection = Detection();
        var current = Clone(snapshot);
        ApplyProposal(current, detection);
        var before = System.Text.Json.JsonSerializer.Serialize(current);
        DetectionProposalConsistency.NeutralizeAppliedProposals(current, snapshot, detection);
        System.Text.Json.JsonSerializer.Serialize(current).Should().Be(before);
    }
}
