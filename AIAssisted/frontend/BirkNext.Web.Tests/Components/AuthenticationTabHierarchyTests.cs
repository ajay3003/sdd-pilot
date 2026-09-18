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
/// The Authentication tab answers three questions, in order: what authentication the target uses, whether
/// that configuration has been verified, and whether BirkNext currently has authenticated TESTING access.
///
/// The three are independent, and these tests hold them apart. A target that needs no sign-in can still lack
/// authenticated testing access. Passing manual verification creates no runtime context. An authenticated API
/// context is not an authenticated browser DOM. Everything else — the proxy workflow, the certificate, the
/// capability inventory — is procedure and detail, one disclosure away.
/// </summary>
public sealed class AuthenticationTabHierarchyTests : BunitContext
{
    private const string Url = "https://m2lbdev.example.com/";
    private const string Authority = "https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111";
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string ClientId = "22222222-2222-2222-2222-222222222222";

    private readonly FrontendAnalysisSettingsService _settings = new();
    private readonly Mock<ITargetEnvironmentDetectionApiService> _api = new();

    public AuthenticationTabHierarchyTests()
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
        State = DetectionState.ManualAuthenticationVerificationRequired,
        ManualAuthenticationVerificationRequired = true,
        ManualAuthenticationVerificationStatus = ManualAuthenticationVerificationStatus.Required,
        AuthenticationRequired = true,
        DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId,
        DetectedAuthority = Authority, DetectedTenantId = Tenant, DetectedClientId = ClientId,
    };

    /// <summary>Saved authentication that matches detection, with the proxy as the testing method.</summary>
    private const string MatchingAuthentication = $$"""
        { "authenticationType": "MicrosoftEntraId", "requiresAuthentication": true,
          "expectedAuthority": "{{Authority}}", "expectedTenant": "{{Tenant}}", "expectedClientId": "{{ClientId}}",
          "authenticatedTestingMethod": "LocalHttpsProxy" }
        """;

    /// <summary>A target that needs no sign-in of its own — but still has no authenticated testing access.</summary>
    private const string NoSignInRequired = """
        { "authenticationType": "None", "requiresAuthentication": false, "authenticatedTestingMethod": "LocalHttpsProxy" }
        """;

    private IRenderedComponent<Component> Open(string authenticationJson)
    {
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult($$"""
        {"activeProfileId":"local","profiles":[
          {"id":"local","name":"Local","targetUrl":"https://local.example.com"},
          {"id":"dev","name":"M2LB DEV","environmentType":"Development","targetUrl":"{{Url}}","authentication":{{authenticationJson}}}
        ]}
        """);
        var cut = Render<Component>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("M2LB DEV")).Click();
        cut.FindAll("[role=tab]").Single(t => t.TextContent.Trim() == "Authentication").Click();
        return cut;
    }

    private IRenderedComponent<Component> Detect(string authenticationJson)
    {
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult($$"""
        {"activeProfileId":"local","profiles":[
          {"id":"local","name":"Local","targetUrl":"https://local.example.com"},
          {"id":"dev","name":"M2LB DEV","environmentType":"Development","targetUrl":"{{Url}}","authentication":{{authenticationJson}}}
        ]}
        """);
        var cut = Render<Component>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("M2LB DEV")).Click();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Detect settings").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Detected from target"));
        cut.FindAll("[role=tab]").Single(t => t.TextContent.Trim() == "Authentication").Click();
        return cut;
    }

    private static IElement Testid(IRenderedComponent<Component> cut, string id) => cut.Find($"[data-testid='{id}']");
    private static string Row(IRenderedComponent<Component> cut, string id) => Testid(cut, id).TextContent.Trim();

    /// <summary>Text the reader can actually see: a collapsed disclosure body is hidden and does not count.</summary>
    private static string VisibleText(IElement element)
    {
        var clone = (IElement)element.Clone(true);
        foreach (var hidden in clone.QuerySelectorAll("[hidden]").ToList()) hidden.Remove();
        return clone.TextContent;
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.OrdinalIgnoreCase)) count++;
        return count;
    }

    // ── §35. The three primary concerns ──────────────────────────────────────────────────────

    // 1, 5.
    [Fact]
    public void TheTabLeadsWithDetectedConfiguredThenVerificationThenAuthenticatedTesting()
    {
        var cut = Detect(MatchingAuthentication);

        cut.FindAll("[data-testid='authentication-discovery'], [data-testid='authentication-verification'], [data-testid='authenticated-testing'], [data-testid='authentication-configuration']")
            .Select(e => e.GetAttribute("data-testid"))
            .Should().Equal("authentication-discovery", "authentication-verification", "authenticated-testing",
                            "authentication-configuration");

        cut.Find("#authentication-status-heading").TextContent.Should().Be("Detected & configured authentication");
        cut.Find("#authentication-verification-heading").TextContent.Should().Be("Verification");
        cut.Find("#authenticated-testing-heading").TextContent.Should().Be("Authenticated testing");
    }

    // 2, 3, 4. The workflow is a procedure, not a status wall.
    [Fact]
    public void TheSetupWorkflowIsNotExpandedByDefaultButItsActionIsVisible()
    {
        var cut = Detect(MatchingAuthentication);

        var toggle = Testid(cut, "authenticated-testing-setup-toggle");
        toggle.GetAttribute("aria-expanded").Should().Be("false");
        // One constant action; the state it opens onto rides along as the hint.
        toggle.TextContent.Should().Contain("View setup details");
        toggle.QuerySelector(".disclosure-hint")!.TextContent.Trim()
            .Should().Be(AuthenticatedTestingStates.Label(AuthenticatedTestingState.NotConnected));
        Testid(cut, "authenticated-testing-setup-body").HasAttribute("hidden").Should().BeTrue();

        // The proxy's own diagnostics and the capability inventory are inside it, not above it.
        Testid(cut, "authenticated-testing-setup-body").QuerySelector("[data-testid='local-https-proxy-panel']").Should().NotBeNull();
        Testid(cut, "authenticated-testing-setup-body").QuerySelector("[data-testid='authenticated-capabilities']").Should().NotBeNull();

        toggle.Click();
        Testid(cut, "authenticated-testing-setup-body").HasAttribute("hidden").Should().BeFalse();
    }

    // ── §36. Sign-in required is not authenticated testing ───────────────────────────────────

    // 6, 7, 8, 9.
    [Fact]
    public void ATargetThatNeedsNoSignInCanStillLackAuthenticatedTestingAccess()
    {
        var cut = Open(NoSignInRequired);

        // The target's own requirement…
        Row(cut, "authentication-sign-in-required").Should().Be("No");
        Testid(cut, "authentication-discovery").TextContent.Should().Contain("Sign-in required");

        // …and BirkNext's testing access, which is a different question with a different answer.
        Row(cut, "authenticated-testing-state").Should().Be("Not connected");
        Row(cut, "authenticated-testing-purpose").Should().Contain("separate from whether the target itself requires sign-in");

        // Nothing presents the combination as a contradiction.
        cut.Markup.Should().NotContainAny("inconsistent", "conflict", "contradiction");
    }

    // 10. The two live in different sections, so neither relabels the other.
    [Fact]
    public void SignInRequirementAndTestingStateAreOwnedBySeparateSections()
    {
        var cut = Detect(MatchingAuthentication);

        Testid(cut, "authentication-sign-in-required").Closest("[data-testid='authentication-discovery']").Should().NotBeNull();
        Testid(cut, "authenticated-testing-state").Closest("[data-testid='authenticated-testing']").Should().NotBeNull();
        Testid(cut, "authentication-discovery").QuerySelector("[data-testid='authenticated-testing-state']").Should().BeNull();
        Testid(cut, "authenticated-testing").QuerySelector("[data-testid='authentication-sign-in-required']").Should().BeNull();
    }

    // ── §37. Provider label ──────────────────────────────────────────────────────────────────

    // 11, 12.
    [Fact]
    public void TheProviderIsNamedAsAPersonReadsItAndTheEnumSpellingIsAbsent()
    {
        var cut = Detect(MatchingAuthentication);

        Row(cut, "authentication-provider").Should().Be("Microsoft Entra ID");
        cut.Markup.Should().NotContain("MicrosoftEntraId", "the enum spelling is an implementation detail");

        // Including inside the editable configuration, where the select used to render enum names.
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Edit Environment").Click();
        cut.FindAll("[role=tab]").Single(t => t.TextContent.Trim() == "Authentication").Click();
        cut.Find("#configured-authentication-type").QuerySelectorAll("option").Select(o => o.TextContent)
            .Should().Contain("Microsoft Entra ID").And.NotContain("MicrosoftEntraId");
    }

    // ── §38. Copy deduplication ──────────────────────────────────────────────────────────────

    // 13, 14, 16.
    [Fact]
    public void TheMissingAuthenticatedContextIsStatedOnceNotInFourNearSynonyms()
    {
        var cut = Detect(MatchingAuthentication);
        var visible = VisibleText(cut.Find("[data-testid='fa-auth-panel']"));

        // The one primary statement.
        Occurrences(visible, "Not connected").Should().BeLessThanOrEqualTo(2, "the state and the setup hint carry it");

        // Its near-synonyms belong to the setup workflow and the capability list, which are collapsed.
        foreach (var synonym in new[]
                 {
                     "Authenticated context unavailable", "Authenticated API context unavailable",
                     "Authenticated traffic not detected", "Waiting for authenticated traffic",
                     "not observed yet", "Authenticated DOM",
                 })
            visible.Should().NotContain(synonym);
    }

    // 15. The detailed statuses survive — one expand away.
    [Fact]
    public void TheDetailedNotObservedStatusesRemainAvailableInsideTheSetup()
    {
        var cut = Detect(MatchingAuthentication);

        Testid(cut, "authenticated-testing-setup-toggle").Click();

        var body = Testid(cut, "authenticated-testing-setup-body");
        body.QuerySelector("[data-testid='proxy-credential']").Should().NotBeNull();
        body.QuerySelector("[data-testid='proxy-authenticated-traffic']").Should().NotBeNull();
        body.QuerySelector("[data-testid='capability-rest']").Should().NotBeNull();
        body.QuerySelector("[data-testid='capability-graphql']").Should().NotBeNull();
    }

    // ── §39. The four setup steps ────────────────────────────────────────────────────────────

    // 17, 22.
    [Fact]
    public void TheSetupPresentsFourStepsAndMarksNoneComplete()
    {
        var cut = Detect(MatchingAuthentication);
        Testid(cut, "authenticated-testing-setup-toggle").Click();

        var body = Testid(cut, "authenticated-testing-setup-body");
        body.QuerySelectorAll("h4").Select(h => h.TextContent.Trim()).Should().Equal(
            "Step 1 — Proxy", "Step 2 — Certificate", "Step 3 — Browser", "Step 4 — Authenticated API context");

        // Nothing has run, so no step claims success.
        body.QuerySelector("[data-testid='proxy-state']")!.TextContent.Should().NotContain("Listening");
        body.QuerySelector("[data-testid='proxy-credential']")!.TextContent.Should().Contain("Waiting for authenticated traffic");
    }

    // ── §40. Testing access summary ──────────────────────────────────────────────────────────

    // 23, 24, 25, 26, 27, 28.
    [Fact]
    public void TestingAccessIsSummarisedCompactlyWithTheInventoryOneExpandAway()
    {
        var cut = Detect(MatchingAuthentication);

        Row(cut, "testing-access-public").Should().Be("Available");
        Row(cut, "testing-access-api").Should().Be("Not available");
        Row(cut, "testing-access-dom").Should().Be("Not available");

        // 27. The per-surface rows are not in the primary surface.
        VisibleText(cut.Find("[data-testid='authenticated-testing']")).Should().NotContain("REST · Authenticated endpoint");

        // 28. Expanding the setup reveals the existing distinctions, unflattened.
        Testid(cut, "authenticated-testing-setup-toggle").Click();
        var capabilities = Testid(cut, "authenticated-capabilities").TextContent;
        capabilities.Should().Contain("REST · Public discovery").And.Contain("REST · Authenticated endpoint");
        capabilities.Should().Contain("GraphQL · Schema discovery").And.Contain("GraphQL · Authenticated query endpoint");
        capabilities.Should().Contain("Security · Authenticated API context").And.Contain("Security · Authenticated DOM");
    }

    // ── §41. Verification stays its own concern ──────────────────────────────────────────────

    // 29, 30, 32.
    [Fact]
    public void VerificationIsSeparateFromAuthenticatedTestingAndSaysSo()
    {
        var cut = Detect(MatchingAuthentication);

        var verification = Testid(cut, "authentication-verification");
        verification.QuerySelector("[data-testid='authentication-verification-status']")!.TextContent
            .Should().Contain("Manual authentication verification required");
        verification.QuerySelector("[data-testid='authentication-verification-open']")!.TextContent.Trim()
            .Should().Be("Open verification instructions");
        Row(cut, "authentication-verification-scope").Should().Contain("does not create an authenticated testing context");

        // 32. The two states are independent: verification is still required while testing is not connected.
        Row(cut, "authenticated-testing-state").Should().Be("Not connected");
        verification.QuerySelector("[data-testid='authenticated-testing-state']").Should().BeNull();
    }

    // ── §42. Configuration match and difference ──────────────────────────────────────────────

    // 33.
    [Fact]
    public void MatchingConfigurationIsStatedOnce()
    {
        var cut = Detect(MatchingAuthentication);

        Row(cut, "authentication-match-summary").Should().Contain("matches saved configuration");
        // The read-only section no longer echoes the same provider and sign-in fields.
        Testid(cut, "authentication-configuration").TextContent.Should().Contain("edited here");
        VisibleText(cut.Find("[data-testid='authentication-configuration']")).Should().NotContain("Microsoft Entra ID");
    }

    // 34, 35, 36.
    [Fact]
    public void DifferingConfigurationKeepsItsComparisonAndItsApplyAction()
    {
        var mismatched = MatchingAuthentication.Replace(ClientId, "99999999-9999-9999-9999-999999999999");
        var cut = Detect(mismatched);

        Row(cut, "authentication-match-summary").Should().NotContain("matches saved configuration");
        cut.Find("[data-testid='authentication-comparison']").QuerySelectorAll("tr")
            .Should().Contain(r => r.TextContent.Contains(ClientId), "the comparison still lists both values");
        cut.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "Apply authentication");
    }

    // ── §43. Semantic separations ────────────────────────────────────────────────────────────

    // 37, 38, 39.
    [Fact]
    public void TheThreeConceptsAreDerivedIndependently()
    {
        // 37. Sign-in required is configuration; testing access is runtime. Neither reads the other.
        var noSignIn = Open(NoSignInRequired);
        Row(noSignIn, "authentication-sign-in-required").Should().Be("No");
        Row(noSignIn, "authenticated-testing-state").Should().Be("Not connected");

        // 38. Verification and testing access disagree, correctly: one is a human confirmation, the
        // other a runtime context.
        var detected = Detect(MatchingAuthentication);
        Row(detected, "authentication-verification-status").Should().Contain("required");
        Row(detected, "authenticated-testing-state").Should().Be("Not connected");

        // 39. API context and browser DOM are separate rows with separate answers.
        Testid(detected, "testing-access-api").Should().NotBeSameAs(Testid(detected, "testing-access-dom"));
    }

    // 40, 41. Public access is not authenticated access, and schema discovery is not an observed query.
    [Fact]
    public void PublicAccessIsNeverPresentedAsAuthenticatedAccess()
    {
        var cut = Detect(MatchingAuthentication);

        Row(cut, "testing-access-public").Should().Be("Available");
        Row(cut, "testing-access-api").Should().Be("Not available");

        Testid(cut, "authenticated-testing-setup-toggle").Click();
        var capabilities = Testid(cut, "authenticated-capabilities");
        capabilities.QuerySelector("[data-testid='capability-rest']")!.TextContent.Should().Be("Not observed");
        capabilities.QuerySelector("[data-testid='capability-graphql']")!.TextContent.Should().Be("Not observed");
        // Public discovery rows stay available, and are not the same claim.
        capabilities.TextContent.Should().Contain("REST · Public discovery");
    }

    // ── §28, §33. The security note and the page's semantics ─────────────────────────────────

    [Fact]
    public void TheDevOnlySecurityNoteSurvivesAsASecondaryAside()
    {
        var cut = Detect(MatchingAuthentication);

        Row(cut, "proxy-security-warning").Should().Contain("never displays, logs or saves");
        // Secondary: it is a tagged note inside the method card, not a banner above the three answers.
        Testid(cut, "proxy-security-warning").Closest("[data-testid='authenticated-testing-setup-body']").Should().NotBeNull();
    }

    [Fact]
    public void HeadingsAreHierarchicalAndDisclosuresExposeTheirState()
    {
        var cut = Detect(MatchingAuthentication);

        var panel = cut.Find("[data-testid='fa-auth-panel']");
        panel.QuerySelectorAll("h3").Select(h => h.TextContent.Trim()).Should().OnlyHaveUniqueItems();

        foreach (var toggle in panel.QuerySelectorAll(".disclosure-toggle"))
        {
            toggle.TagName.Should().Be("BUTTON");
            toggle.GetAttribute("aria-expanded").Should().BeOneOf("true", "false");
            cut.Find($"#{toggle.GetAttribute("aria-controls")}").Should().NotBeNull();
            toggle.TextContent.Trim().Should().NotBeEmpty();
        }
        // Every state is a text label; none of them relies on colour alone.
        Row(cut, "authenticated-testing-state").Should().NotBeNullOrWhiteSpace();
    }
}
