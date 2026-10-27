using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Moq;
using Component = BirkNext.Web.Components.FrontendAnalysisSettings;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// The fourth authentication prerequisite as a flow: Check authentication settings (read-only) → differences reviewed one by one →
/// Apply selected changes (draft only) → Save changes. Detection never writes configuration, nothing is pre-selected, kept saved values
/// stay differences, and a target or configuration change asks for a recheck instead of reusing an old comparison.
/// </summary>
public sealed partial class AuthenticationApplyDraftTests
{
    private const string MatchingEntra = $$"""
        { "authenticationType": "MicrosoftEntraId", "requiresAuthentication": true, "expectedAuthority": "{{Authority}}", "expectedTenant": "{{Tenant}}",
          "expectedClientId": "{{ClientId}}", "allowedRedirectUrls": ["{{Redirect}}"] }
        """;

    private static string Status(IRenderedComponent<Component> cut) => cut.Find("[data-testid=auth-status-auth-settings]").TextContent.Trim();

    private static void CheckSettings(IRenderedComponent<Component> cut) =>
        cut.Find("[data-testid=auth-action-auth-settings]").Click();

    [Fact]
    public void TheCheckRunsFromTheFourthCard_StaysOnTheTab_AndTheHeaderDetectButtonIsNotOffered()
    {
        var cut = Open(MatchingEntra);
        OpenTab(cut, "Authentication");

        cut.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == "Detect settings", "the Authentication tab owns its check");
        cut.Markup.Should().NotContain("Detect settings checks this Frontend URL");
        Status(cut).Should().Be("Not checked");
        cut.Find("[data-testid=auth-action-auth-settings]").TextContent.Trim().Should().Be("Check authentication settings");

        CheckSettings(cut);

        cut.Find("[role=tab][aria-selected=true]").TextContent.Trim().Should().Be("Authentication", "the check does not switch tabs");
        Status(cut).Should().Be("Matches saved configuration");
        cut.Find("[data-testid=auth-summary-settings]").TextContent.Trim().Should().Be("Matches saved configuration");
        cut.Find("[data-testid=auth-card-auth-settings]").TextContent.Should().Contain("Saved configuration").And.Contain(Url);
        _api.Verify(x => x.DetectFromUrlAsync(Url, default), Times.Once);

        // There is no environment-wide detect: the Target Application tab owns its own target check.
        OpenTab(cut, "Target Application");
        cut.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == "Detect settings");
        cut.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "Check target application");
    }

    /// <summary>There is no environment-level detect action or detection status on any tab: each tab owns its own check, and the
    /// target check lives only on Target Application.</summary>
    [Fact]
    public void NoTabOffersAnEnvironmentWideDetectAction_OnlyTargetApplicationOwnsTheTargetCheck()
    {
        var cut = Open(MatchingEntra);
        var tabs = cut.FindAll("[role=tab]").Select(t => t.TextContent.Trim()).ToList();
        tabs.Should().Contain("Target Application");

        foreach (var tab in tabs)
        {
            OpenTab(cut, tab);
            cut.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == "Detect settings", $"{tab} must not offer a generic detect");
            cut.Markup.Should().NotContain("Detect settings checks this Frontend URL", tab);
            cut.FindAll(".fa-selected-summary .fa-detection-value").Should().BeEmpty($"{tab} must not show an environment-level detection status");
            cut.FindAll("[data-testid=target-check]").Should().HaveCount(tab == "Target Application" ? 1 : 0, tab);
        }
    }

    /// <summary>The acceptance fixture: Entra ID saved, the same Entra ID detected — no differences panel and no Apply.</summary>
    [Fact]
    public void EntraSavedAndDetectedIdentical_HasNoDifferencesPanelAndNoApplyButton()
    {
        var cut = Open(MatchingEntra);
        OpenTab(cut, "Authentication");
        CheckSettings(cut);

        cut.FindAll("[data-testid=authentication-differences]").Should().BeEmpty();
        cut.FindAll("button").Should().NotContain(b => b.TextContent.Contains("Apply", StringComparison.Ordinal));
        SaveCalls().Should().Be(0);
        System.Text.Json.JsonSerializer.Serialize(Persisted().Authentication).Should().Contain(ClientId);
    }

    [Fact]
    public void PartialApply_LeavesTheRemainingDifferences_AndKeepSavedIsReviewedNotMatched()
    {
        var cut = Open($$"""{ "authenticationType": "MicrosoftEntraId", "expectedAuthority": "https://login.example.com/other", "expectedClientId": "33333333-3333-3333-3333-333333333333" }""");
        OpenTab(cut, "Authentication");
        CheckSettings(cut);

        // Authority and Client ID conflict; Tenant and Redirect URLs are safe additions.
        cut.FindAll("[data-testid=authentication-difference-row]").Select(r => (r.GetAttribute("data-setting"), r.GetAttribute("data-kind")))
            .Should().Equal(("Expected Authority", "Conflict"), ("Expected Tenant", "SafeChange"), ("Expected Client ID", "Conflict"), ("Allowed Redirect URLs", "SafeChange"));
        Status(cut).Should().Be("Review required (4 differences)");

        cut.Find("[data-testid=auth-decision-use-expected-tenant]").Change(true);
        cut.Find("[data-testid=auth-decision-keep-expected-client-id]").Change(true);
        cut.Find("[data-testid=authentication-apply-selected]").TextContent.Trim().Should().Be("Apply selected changes (1)");
        cut.Find("[data-testid=authentication-apply-selected]").Click();

        // Only the tenant moved into the draft; the conflicts kept their saved values.
        cut.Find("#configured-tenant").GetAttribute("value").Should().Be(Tenant);
        cut.Find("#configured-authority").GetAttribute("value").Should().Be("https://login.example.com/other");
        cut.Find("#configured-client-id").GetAttribute("value").Should().Be("33333333-3333-3333-3333-333333333333");
        cut.FindAll("[data-testid=authentication-difference-row]").Select(r => r.GetAttribute("data-setting"))
            .Should().Equal("Expected Authority", "Expected Client ID", "Allowed Redirect URLs");
        Status(cut).Should().Be("Review required (3 differences)");
        cut.Find("[data-testid=authentication-review-progress]").TextContent.Should().Be("2 of 3 still need a decision.");
        Persisted().Authentication.ExpectedTenant.Should().BeNull("Apply writes the draft; only Save changes persists");

        cut.Find("[data-testid=auth-decision-keep-expected-authority]").Change(true);
        cut.Find("[data-testid=auth-decision-keep-allowed-redirect-urls]").Change(true);
        Status(cut).Should().Be("Reviewed — 3 kept saved");
        cut.Find("[data-testid=auth-card-auth-settings]").TextContent.Should().Contain("kept values remain differences, not matches");
        cut.FindAll("[data-testid=authentication-differences]").Should().ContainSingle("kept differences stay visible");
    }

    [Fact]
    public void ATargetUrlChangeMakesTheComparisonRecheckRequired()
    {
        var cut = Open(MatchingEntra);
        OpenTab(cut, "Authentication");
        CheckSettings(cut);
        Status(cut).Should().Be("Matches saved configuration");

        Click(cut, "Edit Environment");
        OpenTab(cut, "Target Application");
        cut.Find("input[type=url]").Change("https://other.example.com/");
        OpenTab(cut, "Authentication");

        Status(cut).Should().Be("Recheck required");
        cut.Find("[data-testid=auth-action-auth-settings]").TextContent.Trim().Should().Be("Recheck authentication settings");
        cut.FindAll("[data-testid=authentication-differences]").Should().BeEmpty();
    }

    [Fact]
    public void AFailedCheckSaysSoAndOffersToCheckAgain_WithoutTouchingConfiguration()
    {
        _api.Setup(x => x.DetectFromUrlAsync(Url, default)).ReturnsAsync(new TargetEnvironmentDetectionResult
        {
            OriginalUrl = Url, Success = false, Reachability = TargetReachability.Unreachable, Message = "The target did not respond.",
        });
        var cut = Open(MatchingEntra);
        OpenTab(cut, "Authentication");
        CheckSettings(cut);

        Status(cut).Should().Be("Check failed");
        cut.Find("[data-testid=auth-card-auth-settings]").TextContent.Should().Contain("The target did not respond.");
        cut.Find("[data-testid=auth-action-auth-settings]").TextContent.Trim().Should().Be("Check again");
        SaveCalls().Should().Be(0);
    }

    [Fact]
    public void ADifferenceNeverBlocksTheManualSignInCheck()
    {
        var cut = Open($$"""{ "authenticationType": "MicrosoftEntraId", "expectedClientId": "33333333-3333-3333-3333-333333333333" }""");
        OpenTab(cut, "Authentication");
        var before = cut.Find("[data-testid=auth-readiness-hero] .ar-hero-actions").TextContent;
        CheckSettings(cut);

        Status(cut).Should().StartWith("Review required");
        cut.Find("[data-testid=auth-readiness-hero] .ar-hero-actions").TextContent.Should().Be(before, "manual sign-in readiness is the proxy prerequisites' answer, not the settings check's");
    }
}

/// <summary>The settings check as rules.</summary>
public sealed class AuthenticationSettingsCheckRuleTests
{
    private static TargetEnvironmentDetectionResult Detected() => new()
    {
        Success = true, DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId,
        DetectedAuthority = "https://login.microsoftonline.com/t", DetectedTenantId = "t", DetectedClientId = "c", DetectedRedirectUrls = ["https://app/cb"],
    };

    private static AuthenticationSettingsCheckResult Evaluate(FrontendAuthenticationSettings saved, TargetEnvironmentDetectionResult? detection = null,
        string? recheck = null, Dictionary<string, AuthenticationDifferenceDecision>? decisions = null, bool target = true, bool checking = false, string? failure = null) =>
        AuthenticationSettingsChecks.Evaluate(target, checking, detection, failure, recheck, AuthenticationPresentation.Compare(detection, saved), decisions ?? []);

    [Fact]
    public void ComparisonStatusesMapToTheSixReviewClasses()
    {
        AuthenticationComparisonRow Row(AuthenticationComparisonStatus s, string setting = "Expected Client ID") => new(setting, "d", "c", s);
        AuthenticationSettingsChecks.Classify(Row(AuthenticationComparisonStatus.Match), true).Should().Be(AuthenticationDifferenceKind.NoChange);
        AuthenticationSettingsChecks.Classify(Row(AuthenticationComparisonStatus.NotConfigured), true).Should().Be(AuthenticationDifferenceKind.SafeChange);
        AuthenticationSettingsChecks.Classify(Row(AuthenticationComparisonStatus.Different), true).Should().Be(AuthenticationDifferenceKind.Conflict);
        AuthenticationSettingsChecks.Classify(Row(AuthenticationComparisonStatus.Different, "Allowed Redirect URLs"), true)
            .Should().Be(AuthenticationDifferenceKind.SafeChange, "detected redirect URLs are merged, never replacing a saved one");
        AuthenticationSettingsChecks.Classify(Row(AuthenticationComparisonStatus.NotDetected), true).Should().Be(AuthenticationDifferenceKind.NotDetected);
        AuthenticationSettingsChecks.Classify(Row(AuthenticationComparisonStatus.NotAvailable), true).Should().Be(AuthenticationDifferenceKind.NotDiscoverable);
        AuthenticationSettingsChecks.Classify(Row(AuthenticationComparisonStatus.NotAvailable), false).Should().Be(AuthenticationDifferenceKind.NotApplicable);
    }

    [Fact]
    public void EveryStateHasItsOwnWords()
    {
        var saved = new FrontendAuthenticationSettings { AuthenticationType = FrontendAuthenticationType.MicrosoftEntraId, ExpectedClientId = "other" };
        AuthenticationSettingsChecks.StatusLabel(Evaluate(saved, target: false)).Should().Be("Unavailable");
        AuthenticationSettingsChecks.StatusLabel(Evaluate(saved)).Should().Be("Not checked");
        AuthenticationSettingsChecks.StatusLabel(Evaluate(saved, checking: true)).Should().Be("Checking…");
        AuthenticationSettingsChecks.StatusLabel(Evaluate(saved, failure: "Detection failed.")).Should().Be("Check failed");
        AuthenticationSettingsChecks.StatusLabel(Evaluate(saved, Detected(), recheck: "changed")).Should().Be("Recheck required");
        AuthenticationSettingsChecks.StatusLabel(Evaluate(saved, Detected())).Should().Be("Review required (4 differences)");
        AuthenticationSettingsChecks.StatusLabel(Evaluate(saved, new TargetEnvironmentDetectionResult { Success = true })).Should().Be("Nothing detected", "silence is never a match");
        var all = new FrontendAuthenticationSettings
        {
            AuthenticationType = FrontendAuthenticationType.MicrosoftEntraId, ExpectedAuthority = "https://login.microsoftonline.com/t",
            ExpectedTenant = "t", ExpectedClientId = "c", AllowedRedirectUrls = ["https://app/cb"],
        };
        AuthenticationSettingsChecks.StatusLabel(Evaluate(all, Detected())).Should().Be("Matches saved configuration");
    }

    [Fact]
    public void TheCardIsNeverAnInputToProxyReadiness()
    {
        var card = AuthenticationSettingsChecks.Card(Evaluate(new(), Detected()), "https://app/");
        card.Id.Should().Be("auth-settings");
        card.State.Should().Be(AuthPrerequisiteState.NeedsAttention);
        card.ActionId.Should().BeNull("review happens in the differences panel, not through a card action");
        AuthenticationReadinessPresentation.Summarize(AuthConfigurationState.Configured, AuthenticatedTestingMethod.LocalHttpsProxy, null, null, loaded: true)
            .Prerequisites.Select(p => p.Id).Should().NotContain("auth-settings");
    }

    [Fact]
    public void ApplySelectedCopiesOnlyTheNamedSettings_AndMergesRedirects()
    {
        var target = new FrontendAuthenticationSettings { ExpectedClientId = "kept", AllowedRedirectUrls = ["https://saved/cb"] };
        var applied = AuthenticationSettingsChecks.ApplySelected(target, Detected(), ["Expected Tenant", "Allowed Redirect URLs"]);

        applied.Should().Equal("Expected Tenant", "Allowed Redirect URLs");
        target.ExpectedTenant.Should().Be("t");
        target.ExpectedClientId.Should().Be("kept");
        target.ExpectedAuthority.Should().BeNull();
        target.AuthenticationType.Should().Be(FrontendAuthenticationType.None);
        target.AllowedRedirectUrls.Should().Equal("https://saved/cb", "https://app/cb");
    }

    [Fact]
    public void ADecisionForUseDetectedKeepsTheReviewOpenUntilApplied()
    {
        var saved = new FrontendAuthenticationSettings { AuthenticationType = FrontendAuthenticationType.MicrosoftEntraId, ExpectedAuthority = "https://login.microsoftonline.com/t",
            ExpectedTenant = "t", AllowedRedirectUrls = ["https://app/cb"], ExpectedClientId = "other" };
        var use = Evaluate(saved, Detected(), decisions: new() { ["Expected Client ID"] = AuthenticationDifferenceDecision.UseDetected });
        use.State.Should().Be(AuthenticationSettingsCheckState.ReviewRequired);
        use.SelectedChanges.Should().Be(1);
        use.OpenDifferences.Should().Be(0);

        var keep = Evaluate(saved, Detected(), decisions: new() { ["Expected Client ID"] = AuthenticationDifferenceDecision.KeepSaved });
        keep.State.Should().Be(AuthenticationSettingsCheckState.Reviewed);
        keep.KeptSaved.Should().Be(1);
        keep.ShowDifferences.Should().BeTrue();
    }
}
