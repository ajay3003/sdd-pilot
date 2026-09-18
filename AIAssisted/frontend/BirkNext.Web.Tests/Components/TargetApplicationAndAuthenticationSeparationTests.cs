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
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Detect settings").Click();
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

        Row(cut, "authentication-match-summary").Should().Contain("matches saved configuration");
        Row(cut, "authentication-provider").Should().Be("Microsoft Entra ID");
        Row(cut, "authenticated-context").Should().Contain("Authenticated context");

        // Exactly one place lists the identifiers, and the old duplicate list is gone.
        cut.FindAll("[data-testid='authentication-comparison']").Should().ContainSingle();
        cut.Markup.Should().NotContain("Configured identifiers and redirect URLs");

        // Authority / Tenant / Client ID appear ONLY inside that one comparison — no second read-only block.
        var comparison = Testid(cut, "authentication-comparison").OuterHtml;
        foreach (var value in new[] { Authority, Tenant, ClientId })
            Occurrences(cut.Markup, value).Should().Be(Occurrences(comparison, value),
                $"{value} must not appear outside the single detected/configured comparison");
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    // ── 5–6. Comparison stays collapsed while everything matches ──────────────

    [Fact]
    public void MatchingConfiguration_KeepsComparisonCollapsed_AndViewDetailsRevealsIt()
    {
        var cut = Detect(MatchingAuthentication);
        OpenTab(cut, "Authentication");

        var details = Testid(cut, "edge-auth-discovery");
        details.HasAttribute("open").Should().BeFalse("everything matches, so the detail is not forced open");
        Testid(cut, "authentication-details-toggle").TextContent.Trim().Should().Be("View details");

        // The comparison itself is present in the DOM behind the disclosure and carries all four columns.
        var table = Testid(cut, "authentication-comparison");
        table.QuerySelectorAll("thead th").Select(h => h.TextContent.Trim())
            .Should().Equal("Setting", "Detected", "Configured", "Status");
        table.TextContent.Should().Contain("Match").And.NotContain("Different");
    }

    // ── 7. A mismatched value renders the Different state and opens the detail ─

    [Fact]
    public void MismatchedValue_RendersDifferentState_AndExpandsAutomatically()
    {
        var cut = Detect(MismatchedAuthentication);
        OpenTab(cut, "Authentication");

        Testid(cut, "edge-auth-discovery").HasAttribute("open").Should().BeTrue("a difference must not stay hidden");
        Row(cut, "authentication-match-summary").Should().Contain("differs from saved configuration");

        var clientIdRow = Testid(cut, "authentication-comparison").QuerySelectorAll("tbody tr")
            .Single(r => r.QuerySelector("th")!.TextContent.Trim() == "Expected Client ID");
        clientIdRow.TextContent.Should().Contain(ClientId).And.Contain(OtherClientId).And.Contain("Different");

        // Settings that do agree are still reported as Match — Different is per value, not per card.
        Testid(cut, "authentication-comparison").QuerySelectorAll("tbody tr")
            .Single(r => r.QuerySelector("th")!.TextContent.Trim() == "Expected Authority")
            .TextContent.Should().Contain("Match");
    }

    // ── 8. Apply authentication only becomes an action when it would change something ─

    [Fact]
    public void ApplyAuthentication_IsInertWhenDetectedAlreadyMatchesSavedConfiguration()
    {
        var cut = Detect(MatchingAuthentication);
        OpenTab(cut, "Authentication");

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Apply authentication")
            .HasAttribute("disabled").Should().BeTrue();
        Row(cut, "apply-authentication-explanation").Should().Be("Detected values already match saved configuration.");
    }

    [Fact]
    public void ApplyAuthentication_IsOfferedAndNamesTheValuesItWouldChange()
    {
        var cut = Detect(MismatchedAuthentication);
        OpenTab(cut, "Authentication");

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Apply authentication")
            .HasAttribute("disabled").Should().BeFalse();
        var explanation = Row(cut, "apply-authentication-explanation");
        explanation.Should().Contain("Expected Client ID", "the user must see which value changes");
        explanation.Should().NotContain("Expected Authority", "settings that already match do not change");
        explanation.Should().Contain("not saved automatically", "detected values are never persisted on their own");

        // Applying is a draft edit, never a save: the persisted profile still holds the old client ID.
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Apply authentication").Click();
        cut.Find("#configured-client-id").GetAttribute("value").Should().Be(ClientId, "the draft now holds the detected value");
        _settings.Settings.Profiles.Single(p => p.Id == "dev").Authentication.ExpectedClientId.Should().Be(OtherClientId);
        JSInterop.Invocations.Should().NotContain(i => i.Identifier == "birkNextStorage.setItem");
    }

    // ── 9–10. Verification and testing method remain visible on Authentication ─

    [Fact]
    public void ManualVerificationRemainsVisibleOnAuthentication()
    {
        var cut = Detect(MatchingAuthentication);
        OpenTab(cut, "Authentication");

        Row(cut, "authentication-verification-status").Should().Contain("Manual authentication verification required");
        Row(cut, "authentication-verification-method").Should().Be("Automated detection");
        cut.FindAll("button").Should().ContainSingle(b => b.TextContent.Trim() == "Review manual verification");
    }

    [Fact]
    public void AuthenticatedTestingMethodRemainsVisibleWithItsContextAndSecondaryDevWarning()
    {
        var cut = Detect(MatchingAuthentication);
        OpenTab(cut, "Authentication");

        Row(cut, "authenticated-testing-method-value").Should().Be(AuthenticatedTestingMethodLabels.ProxyOption);
        Row(cut, "authenticated-testing-method-context").Should().Be("Authenticated context unavailable");
        Row(cut, "proxy-security-warning").Should().Contain("never displays, logs or saves");

        // The DEV-only caveat is a tagged aside, not a banner competing with the status card.
        var note = Testid(cut, "authenticated-testing-method-help");
        note.ClassList.Should().Contain("fa-auth-note");
        note.QuerySelector(".fa-auth-note-tag")!.TextContent.Trim().Should().Be("DEV only");
    }

    // ── 11–12. Advanced details are collapsed but complete ────────────────────

    [Fact]
    public void AdvancedAuthenticationDetails_AreCollapsedByDefault_AndCarryTheExpectedIdentifiers()
    {
        var cut = Detect(MatchingAuthentication);
        OpenTab(cut, "Authentication");

        var details = Testid(cut, "edge-auth-discovery");
        details.HasAttribute("open").Should().BeFalse();
        details.QuerySelector("caption")!.TextContent.Trim().Should().Be("Advanced authentication details");

        var settings = Testid(cut, "authentication-comparison").QuerySelectorAll("tbody th")
            .Select(th => th.TextContent.Trim()).ToList();
        settings.Should().Equal("Identity provider", "Expected Authority", "Expected Tenant", "Expected Client ID", "Allowed Redirect URLs");
        Row(cut, "authentication-detection-source").Should().Be("Detect Settings");
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

        // Card state has a text status badge next to the heading, plus a summary sentence.
        var card = Testid(cut, "authentication-discovery");
        card.QuerySelector(".fa-card-status")!.TextContent.Trim().Should().NotBeEmpty();
        Row(cut, "authentication-match-summary").Should().NotBeEmpty();

        // Every comparison badge carries a word, and its decorative glyph is hidden from assistive tech.
        foreach (var badge in Testid(cut, "authentication-comparison").QuerySelectorAll(".fa-compare-badge"))
        {
            badge.TextContent.Trim().Should().ContainAny("Match", "Different", "Not configured", "Not detected", "Not available");
            badge.QuerySelector("[aria-hidden=true]").Should().NotBeNull();
        }

        // The card itself stays neutral; only the badge changes.
        card.ClassList.Should().Contain("fa-state-detected");
    }
}
