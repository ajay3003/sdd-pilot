using AngleSharp.Dom;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Component = BirkNext.Web.Components.FrontendAnalysisSettings;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Target Application answers "what is this target, and what did BirkNext detect?"; Authentication answers
/// "how will BirkNext authenticate and test this target?". These tests hold that separation in the DOM: the
/// detected identity lives on Target Application, Authentication leads with a compact status card and keeps
/// the detected-versus-configured identifiers in one collapsible comparison instead of two duplicated tables.
/// </summary>
public sealed class TargetApplicationAndAuthenticationSeparationTests : BunitContext
{
    private const string Url = "https://m2lbdev.example.com/";
    private const string Authority = "https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111";
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string ClientId = "22222222-2222-2222-2222-222222222222";
    private const string Redirect = "https://m2lbdev.example.com/authentication/login-callback";

    private readonly FrontendAnalysisSettingsService _settings = new();
    private readonly Mock<ITargetEnvironmentDetectionApiService> _api = new();

    public TargetApplicationAndAuthenticationSeparationTests()
    {
        Services.AddSingleton<IFrontendAnalysisSettingsService>(_settings);
        Services.AddSingleton(_api.Object);
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
        JSInterop.SetupVoid("birkNextStorage.setItem", _ => true).SetVoidResult();
        JSInterop.SetupVoid("birkNextStorage.setDiscovery", _ => true).SetVoidResult();
        JSInterop.Setup<string?>("birkNextStorage.getDiscovery").SetResult(null);
        _api.Setup(x => x.DetectFromUrlAsync(Url, default)).ReturnsAsync(Detection);
    }

    private static TargetEnvironmentDetectionResult Detection() => new()
    {
        OriginalUrl = Url, Success = true, Reachability = TargetReachability.Reachable,
        Confidence = DetectionConfidence.High,
        DetectedClientFramework = ClientFrameworkType.BlazorWebAssembly,
        SuggestedEnvironmentType = FrontendEnvironmentType.Development,
        SuggestedProfileName = "M2LBDEV",
        State = DetectionState.ManualAuthenticationVerificationRequired,
        ManualAuthenticationVerificationRequired = true,
        ManualAuthenticationVerificationStatus = ManualAuthenticationVerificationStatus.Required,
        AuthenticationRequired = true,
        DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId,
        DetectedAuthority = Authority,
        DetectedTenantId = Tenant,
        DetectedClientId = ClientId,
        DetectedRedirectUrls = [Redirect]
    };

    private const string OtherClientId = "99999999-9999-9999-9999-999999999999";

    /// <summary>A profile whose saved authentication is exactly what detection finds.</summary>
    private const string MatchingAuthentication = $$"""
        { "authenticationType": "MicrosoftEntraId", "expectedAuthority": "{{Authority}}", "expectedTenant": "{{Tenant}}",
          "expectedClientId": "{{ClientId}}", "allowedRedirectUrls": ["{{Redirect}}"], "authenticatedTestingMethod": "LocalHttpsProxy" }
        """;

    /// <summary>A profile whose saved client ID disagrees with the detected one.</summary>
    private const string MismatchedAuthentication = $$"""
        { "authenticationType": "MicrosoftEntraId", "expectedAuthority": "{{Authority}}", "expectedTenant": "{{Tenant}}",
          "expectedClientId": "{{OtherClientId}}", "allowedRedirectUrls": ["{{Redirect}}"], "authenticatedTestingMethod": "LocalHttpsProxy" }
        """;

    private const string NoAuthentication = """{ "authenticationType": "None", "authenticatedTestingMethod": "LocalHttpsProxy" }""";

    private IRenderedComponent<Component> Open(string authenticationJson = NoAuthentication)
    {
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult($$"""
        {"activeProfileId":"local","profiles":[
          {"id":"local","name":"Local","targetUrl":"https://local.example.com"},
          {"id":"dev","name":"M2LB DEV","environmentType":"Development","targetUrl":"{{Url}}","authentication":{{authenticationJson}}}
        ]}
        """);
        var cut = Render<Component>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("M2LB DEV")).Click();
        return cut;
    }

    private IRenderedComponent<Component> Detect(string authenticationJson = NoAuthentication)
    {
        var cut = Open(authenticationJson);
        cut.Find("#target-tab-target").Click(); cut.FindAll("button").Single(b => b.TextContent.Trim() == "Check target application").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Detected from target"));
        return cut;
    }

    private static void OpenTab(IRenderedComponent<Component> cut, string label) =>
        cut.FindAll("[role=tab]").Single(t => t.TextContent.Trim() == label).Click();

    private static IElement Testid(IRenderedComponent<Component> cut, string id) => cut.Find($"[data-testid='{id}']");

    private static string Row(IRenderedComponent<Component> cut, string id) => Testid(cut, id).TextContent.Trim();

    // ── 1–2. Target Application keeps the detected identity ───────────────────

    [Fact]
    public void TargetApplication_ShowsDetectedIdentityProvider()
    {
        var cut = Detect();
        OpenTab(cut, "Target Application");

        var summary = Testid(cut, "target-detected-summary");
        summary.TextContent.Should().Contain("Identity provider");
        // User-facing wording, not the enum spelling.
        Row(cut, "target-identity-provider").Should().Be("Microsoft Entra ID");
        summary.TextContent.Should().Contain("Reachable").And.Contain("Blazor WebAssembly");
    }

    [Fact]
    public void TargetApplication_ShowsDetectedAuthorityTenantAndClientId()
    {
        var cut = Detect();
        OpenTab(cut, "Target Application");

        Row(cut, "target-authority").Should().Be(Authority);
        Row(cut, "target-tenant").Should().Be(Tenant);
        Row(cut, "target-client-id").Should().Be(ClientId);
        Row(cut, "target-detection-confidence").Should().Contain("High");

        // Each detected row is labelled with its provenance rather than presented as configuration.
        var badges = Testid(cut, "target-detected-summary").QuerySelectorAll(".fa-source-badge")
            .Select(b => b.TextContent.Trim()).ToList();
        badges.Should().Contain("Detected").And.Contain("Suggested");
    }

    // ── 14. Target Application never configures authentication ────────────────

    [Fact]
    public void TargetApplication_RemainsReadOnlyForAuthenticationConfiguration()
    {
        var cut = Detect();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Edit Environment").Click();
        OpenTab(cut, "Target Application");

        foreach (var id in new[] { "configured-authentication-type", "configured-authority", "configured-tenant", "configured-client-id", "configured-redirect-urls" })
            cut.FindAll($"#{id}").Should().BeEmpty($"{id} belongs to the Authentication tab");

        cut.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == "Apply authentication");
        cut.FindAll("[data-testid='authentication-comparison']").Should().BeEmpty();
        // The tab points at Authentication instead of configuring it.
        Row(cut, "target-authentication-pointer").Should().Contain("Authentication tab");
    }

    // ── 3–4. Authentication leads with one compact status, not duplicate blocks ─

    [Fact]
    public void Authentication_ShowsCompactMatchStatus_WithoutDuplicatingDetectedAndConfiguredBlocks()
    {
        var cut = Detect(MatchingAuthentication);
        OpenTab(cut, "Authentication");

        Row(cut, "auth-status-auth-settings").Should().Be("Matches saved configuration");
        Row(cut, "authentication-configured-provider").Should().Be("Microsoft Entra ID");
        Row(cut, "authenticated-testing-state").Should().Be("Not connected");
        Row(cut, "authentication-sign-in-required").Should().NotBeNullOrWhiteSpace();

        // A match lists nothing to review: no comparison, no Apply, and no detected identifier echoed anywhere on the tab.
        cut.FindAll("[data-testid='authentication-comparison'], [data-testid='authentication-apply-selected']").Should().BeEmpty();
        cut.Markup.Should().NotContain("Configured identifiers and redirect URLs");
        Testid(cut, "fa-auth-panel").QuerySelector("[data-testid='authentication-differences']").Should().BeNull();
    }

    // ── 5–6. Saved and detected identical: no differences panel ───────────────

    [Fact]
    public void MatchingConfiguration_RendersNoDifferencesPanelAndNoApply()
    {
        var cut = Detect(MatchingAuthentication);
        OpenTab(cut, "Authentication");

        cut.FindAll("[data-testid='authentication-differences']").Should().BeEmpty("the panel exists only when there is something to review");
        cut.FindAll("button").Should().NotContain(b => b.TextContent.Contains("Apply"));
        Row(cut, "auth-summary-settings").Should().Be("Matches saved configuration");
    }

    // ── 7. A mismatched value is a conflict listed in the differences panel ────

    [Fact]
    public void MismatchedValue_IsListedAsAConflict_AndMatchingValuesAreNot()
    {
        var cut = Detect(MismatchedAuthentication);
        OpenTab(cut, "Authentication");

        Row(cut, "auth-status-auth-settings").Should().Be("Review required (1 difference)");
        var table = Testid(cut, "authentication-comparison");
        table.QuerySelectorAll("thead th").Select(h => h.TextContent.Trim()).Should().Equal("Setting", "Saved", "Detected", "State / decision");
        var clientIdRow = table.QuerySelectorAll("tbody tr").Should().ContainSingle().Subject;
        clientIdRow.QuerySelector("th")!.TextContent.Trim().Should().Be("Expected Client ID");
        clientIdRow.TextContent.Should().Contain(ClientId).And.Contain(OtherClientId).And.Contain("Conflict requiring review");

        // Settings that agree are only in the collapsed full list, as No change.
        var all = Testid(cut, "authentication-all-settings");
        all.HasAttribute("open").Should().BeFalse();
        all.QuerySelectorAll("tbody tr").Single(r => r.QuerySelector("th")!.TextContent.Trim() == "Expected Authority")
            .TextContent.Should().Contain("No change");
    }

    // ── 8. Applying is an explicit, per-setting draft edit ─────────────────────

    [Fact]
    public void NothingIsAppliedUntilAValueIsExplicitlySelected()
    {
        var cut = Detect(MismatchedAuthentication);
        OpenTab(cut, "Authentication");

        cut.FindAll("[data-testid='authentication-apply-selected']").Should().BeEmpty();
        cut.FindAll("[data-testid^='auth-decision-'] input:checked").Should().BeEmpty("no decision is pre-selected");
    }

    [Fact]
    public void ApplySelectedChanges_WritesOnlyTheDraft()
    {
        var cut = Detect(MismatchedAuthentication);
        OpenTab(cut, "Authentication");

        cut.Find("[data-testid='auth-decision-use-expected-client-id']").Change(true);
        Row(cut, "authentication-apply-selected").Should().Be("Apply selected changes (1)");
        cut.Find("[data-testid='authentication-apply-selected']").Click();

        // Applying is a draft edit, never a save: the persisted profile still holds the old client ID.
        cut.Find("#configured-client-id").GetAttribute("value").Should().Be(ClientId, "the draft now holds the detected value");
        _settings.Settings.Profiles.Single(p => p.Id == "dev").Authentication.ExpectedClientId.Should().Be(OtherClientId);
        JSInterop.Invocations.Should().NotContain(i => i.Identifier == "birkNextStorage.setItem");
        Row(cut, "authentication-apply-result").Should().Contain("1 change applied to the draft. Save changes to keep it.");
    }

    // ── 9–10. Verification and testing method remain visible on Authentication ─

    [Fact]
    public void ManualVerificationRemainsVisibleOnAuthentication()
    {
        var cut = Detect(MatchingAuthentication);
        OpenTab(cut, "Authentication");

        Row(cut, "authentication-verification-status").Should().Be("Not recorded");
        Row(cut, "authentication-runtime-state").Should().Be("Not verified");
        Testid(cut, "authentication-verification").TextContent.Should().NotContain("Automated detection");
        cut.FindAll("button").Should().ContainSingle(b => b.TextContent.Trim() == "Open manual sign-in check");
    }

    [Fact]
    public void AuthenticatedTestingMethodRemainsVisibleWithItsContextAndSecondaryDevWarning()
    {
        var cut = Detect(MatchingAuthentication);
        OpenTab(cut, "Authentication");

        Row(cut, "authenticated-testing-method-value").Should().Be(AuthenticatedTestingMethodLabels.ProxyOption);
        // Stated once, by the Authenticated testing section, in its own vocabulary.
        Row(cut, "authenticated-testing-state").Should().Be("Not connected");
        cut.FindAll("[data-testid='authenticated-testing-method-context']").Should().BeEmpty();
        Row(cut, "proxy-security-warning").Should().Contain("never displays, logs or saves");

        // The DEV-only caveat is a tagged aside, not a banner competing with the status card.
        var note = Testid(cut, "authenticated-testing-method-help");
        note.ClassList.Should().Contain("fa-auth-note");
        note.QuerySelector(".fa-auth-note-tag")!.TextContent.Trim().Should().Be("DEV only");
    }

    // ── 11–12. The full comparison is collapsed but complete ──────────────────

    [Fact]
    public void AllComparedSettings_AreCollapsedByDefault_AndCarryTheExpectedIdentifiers()
    {
        var cut = Detect(MismatchedAuthentication);
        OpenTab(cut, "Authentication");

        var details = Testid(cut, "authentication-all-settings");
        details.HasAttribute("open").Should().BeFalse();
        var settings = details.QuerySelectorAll("tbody th").Select(th => th.TextContent.Trim()).ToList();
        settings.Should().Equal("Identity provider", "Expected Authority", "Expected Tenant", "Expected Client ID", "Allowed Redirect URLs");
        details.TextContent.Should().Contain("not discoverable from the public sign-in flow");
    }

    // ── 13. No credential surface is introduced ──────────────────────────────

    [Fact]
    public void NoCredentialInputOrValueIsIntroducedOnEitherTab()
    {
        var cut = Detect(MatchingAuthentication);
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Edit Environment").Click();

        foreach (var tab in new[] { "Target Application", "Authentication" })
        {
            OpenTab(cut, tab);
            cut.FindAll("input[type=password]").Should().BeEmpty(tab);
            // No control on either tab asks for, or is named after, a secret.
            var controls = cut.FindAll("input, textarea, select")
                .Select(c => $"{c.Id} {c.GetAttribute("name")} {c.GetAttribute("placeholder")}".ToLowerInvariant());
            foreach (var control in controls)
                control.Should().NotContainAny("secret", "password", "token", "credential");
        }
    }

    // ── 15. Status is never carried by colour alone ──────────────────────────

    [Fact]
    public void AuthenticationStatusIsNotCommunicatedByColourAlone()
    {
        var cut = Detect(MismatchedAuthentication);
        OpenTab(cut, "Authentication");

        // The card states its status in words; the differences panel repeats it next to its heading.
        Row(cut, "auth-status-auth-settings").Should().NotBeEmpty();
        Row(cut, "authentication-differences-state").Should().Be(Row(cut, "auth-status-auth-settings"));

        // Every comparison badge carries a word, and its decorative glyph is hidden from assistive tech.
        foreach (var badge in Testid(cut, "authentication-comparison").QuerySelectorAll(".fa-compare-badge"))
        {
            badge.TextContent.Trim().Should().ContainAny("Safe change available", "Conflict requiring review");
            badge.QuerySelector("[aria-hidden=true]").Should().NotBeNull();
        }
        // Each decision is a labelled radio pair inside a fieldset with a legend.
        var decision = Testid(cut, "auth-decision-expected-client-id");
        decision.QuerySelector("legend")!.TextContent.Should().Contain("Expected Client ID");
        decision.QuerySelectorAll("label").Select(l => l.TextContent.Trim()).Should().Equal("Keep saved value", "Use detected value");
    }
}
