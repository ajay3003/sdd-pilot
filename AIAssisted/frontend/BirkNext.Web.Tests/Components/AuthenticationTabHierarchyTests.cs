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

    /// <summary>
    /// Text the reader can actually see. A ReviewDisclosure body is <c>hidden</c> while collapsed and a closed
    /// &lt;details&gt; renders only its summary; neither counts as something the page is saying.
    /// </summary>
    private static string VisibleText(IElement element)
    {
        var clone = (IElement)element.Clone(true);
        foreach (var collapsed in clone.QuerySelectorAll("[hidden], details:not([open])").ToList()) collapsed.Remove();
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
    public void TheTabLeadsWithReadinessThenPrerequisitesThenConfigurationVerificationAndContext()
    {
        var cut = Detect(MatchingAuthentication);

        cut.FindAll("[data-testid='auth-readiness-hero'], [data-testid='auth-prerequisites'], [data-testid='authentication-configuration'], [data-testid='authentication-verification'], [data-testid='authenticated-testing'], [data-testid='auth-discovery-cta']")
            .Select(e => e.GetAttribute("data-testid"))
            .Should().Equal("auth-readiness-hero", "auth-prerequisites", "authentication-configuration",
                            "authentication-verification", "authenticated-testing", "auth-discovery-cta");

        cut.Find("#authentication-configuration-heading").TextContent.Should().Be("Authentication configuration");
        cut.Find("#authentication-verification-heading").TextContent.Should().Be("Authentication verification");
        cut.Find("#authenticated-testing-heading").TextContent.Should().Be("Authenticated testing context");
        // Detection is a block inside the configuration section, not a second full-width configuration card.
        Testid(cut, "authentication-discovery").Closest("[data-testid='authentication-configuration']").Should().NotBeNull();
        cut.Find("#authentication-status-heading").TextContent.Should().Be("Detection");
    }

    /// <summary>
    /// The exact defect this pane was rebuilt for, pinned end to end: detection finds Entra ID, the saved configuration
    /// matches it, and the page must not lead with a verdict saying nothing is configured.
    /// </summary>
    [Fact]
    public void ADetectedProviderMatchingTheSavedConfigurationIsNeverCalledUnconfigured()
    {
        var cut = Detect(MatchingAuthentication);

        Row(cut, "authentication-match-summary").Should().Contain("matches saved configuration");
        Row(cut, "authentication-configuration-state").Should().Be("Configured");
        Row(cut, "auth-summary-configuration").Should().Be("Configured");

        var visible = VisibleText(cut.Find("[data-testid='fa-auth-panel']"));
        visible.Should().NotContain("No authentication is configured");
        visible.Should().NotContain("No authentication provider is configured");
        Row(cut, "auth-readiness-state").Should().NotBe("Not configured");
    }

    /// <summary>
    /// And the reason it used to happen: readiness was handed the target's own sign-in requirement. A target that
    /// forces no sign-in still has a configured provider, and the pane says so.
    /// </summary>
    [Fact]
    public void ASavedProviderIsConfiguredEvenWhenTheTargetForcesNoSignIn()
    {
        var cut = Detect(MatchingAuthentication.Replace("\"requiresAuthentication\": true", "\"requiresAuthentication\": false"));

        Row(cut, "authentication-sign-in-required").Should().Be("No");
        Row(cut, "authentication-configuration-state").Should().Be("Configured");
        Row(cut, "auth-summary-configuration").Should().Be("Configured");
        VisibleText(cut.Find("[data-testid='fa-auth-panel']"))
            .Should().NotContain("provider is configured for this Target Environment");
    }

    // 2, 3, 4. The workflow is a procedure, not a status wall.
    [Fact]
    public void TheSetupWorkflowIsNotExpandedByDefaultButItsActionIsVisible()
    {
        var cut = Detect(MatchingAuthentication);

        var toggle = Testid(cut, "authenticated-testing-setup-toggle");
        toggle.GetAttribute("aria-expanded").Should().Be("false");
        // One constant action. The state is already the summary's Testing context row and the card's own badge;
        // repeating it on the toggle a line below made three copies of one word.
        toggle.TextContent.Should().Contain("View setup details");
        toggle.QuerySelector(".disclosure-hint").Should().BeNull();
        Testid(cut, "authenticated-testing-setup-body").HasAttribute("hidden").Should().BeTrue();

        // The proxy's own diagnostics are inside it, not above it.
        Testid(cut, "authenticated-testing-setup-body").QuerySelector("[data-testid='local-https-proxy-panel']").Should().NotBeNull();
        // The capability inventory is its own collapsed disclosure on the pane, stated once for whichever method is saved.
        Testid(cut, "authenticated-testing-setup-body").QuerySelector("[data-testid='authenticated-capabilities']").Should().BeNull();
        Testid(cut, "auth-capabilities-toggle").GetAttribute("aria-expanded").Should().Be("false");
        Testid(cut, "auth-capabilities-body").QuerySelector("[data-testid='authenticated-capabilities']").Should().NotBeNull();

        toggle.Click();
        Testid(cut, "authenticated-testing-setup-body").HasAttribute("hidden").Should().BeFalse();
    }

    // ── §36. Sign-in required is not authenticated testing ───────────────────────────────────

    // 6, 7, 8, 9.
    [Fact]
    public void ATargetThatNeedsNoSignInCanStillLackAuthenticatedTestingAccess()
    {
        var cut = Open(NoSignInRequired);

        // The target's own requirement, owned by the configuration section…
        Row(cut, "authentication-sign-in-required").Should().Be("No");
        Testid(cut, "authentication-configuration").TextContent.Should().Contain("Sign-in required");

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

        Testid(cut, "authentication-sign-in-required").Closest("[data-testid='authentication-configuration']").Should().NotBeNull();
        Testid(cut, "authenticated-testing-state").Closest("[data-testid='authenticated-testing']").Should().NotBeNull();
        Testid(cut, "authentication-configuration").QuerySelector("[data-testid='authenticated-testing-state']").Should().BeNull();
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

        // The summary row and the card badge, and nothing else.
        Occurrences(visible, "Not connected").Should().Be(2, "the readiness summary and the card's own badge carry it");

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

        // The per-surface statuses survive too, in the one capability inventory.
        var capabilities = Testid(cut, "auth-capabilities-body");
        capabilities.QuerySelector("[data-testid='capability-rest']").Should().NotBeNull();
        capabilities.QuerySelector("[data-testid='capability-graphql']").Should().NotBeNull();
    }

    // ── §39. The four setup steps ────────────────────────────────────────────────────────────

    // 17, 22. The numbered rails are gone: prerequisites are three status cards, and the runtime panel keeps only
    // what those cards do not own.
    [Fact]
    public void ThePrerequisitesAreStatusCardsAndTheSetupIsNoLongerANumberedWizard()
    {
        var cut = Detect(MatchingAuthentication);

        var prerequisites = Testid(cut, "auth-prerequisites");
        prerequisites.QuerySelectorAll("h5").Select(h => h.TextContent.Trim()).Should().Equal(
            "HTTPS inspection certificate", "Local HTTPS Proxy", "Dedicated Edge browser");
        foreach (var step in new[] { "Step 1", "Step 2", "Step 3", "Step 4" })
            cut.Markup.Should().NotContain(step);

        Testid(cut, "authenticated-testing-setup-toggle").Click();
        var body = Testid(cut, "authenticated-testing-setup-body");

        // Nothing has run, so nothing claims success.
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

        // 28. Expanding Testing capabilities reveals the existing distinctions, unflattened.
        Testid(cut, "auth-capabilities-toggle").Click();
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
        // The Method row describes how VERIFICATION is performed. It used to report how authentication was DETECTED,
        // which made the card say "Manual verification required" with the method "Automated detection".
        verification.QuerySelector("[data-testid='authentication-verification-method']")!.TextContent.Trim()
            .Should().Be("Manual verification in a signed-in browser");
        verification.TextContent.Should().NotContain("Automated detection");
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
        Testid(cut, "authentication-configuration").TextContent.Should().Contain("edited here");

        // One configuration section, so the provider is named once as configured and once as detected — never in a
        // second full-width card that repeats both.
        cut.FindAll("[data-testid='authentication-configuration']").Should().ContainSingle();
        Row(cut, "authentication-configured-provider").Should().Be("Microsoft Entra ID");
        Occurrences(VisibleText(cut.Find("[data-testid='fa-auth-panel']")), "Microsoft Entra ID").Should().Be(2);
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

        Testid(cut, "auth-capabilities-toggle").Click();
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
