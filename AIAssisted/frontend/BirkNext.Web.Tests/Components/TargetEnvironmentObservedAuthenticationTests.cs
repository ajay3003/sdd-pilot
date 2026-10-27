using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;
using Component = BirkNext.Web.Components.FrontendAnalysisSettings;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Authentication is observed first: a confidently observed provider and sign-in requirement are effective without Edit → Save,
/// an explicit override wins but never erases the observation, a disagreement is "Review required" (not a defect), and nothing
/// observed is copied into configuration. Observation grants no execution trust and never marks a manual sign-in check passed.
/// "Set as Active" is a real primary action while it can run.
/// </summary>
public sealed class TargetEnvironmentObservedAuthenticationTests : BunitContext
{
    private const string Url = "https://app.example.com/";
    private const string Authority = "https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111";
    private const string ClientId = "22222222-2222-2222-2222-222222222222";

    private readonly FrontendAnalysisSettingsService _settings = new();
    private readonly Mock<ITargetEnvironmentDetectionApiService> _api = new();

    public TargetEnvironmentObservedAuthenticationTests()
    {
        Services.AddSingleton<IFrontendAnalysisSettingsService>(_settings);
        Services.AddSingleton(_api.Object);
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
        JSInterop.SetupVoid("birkNextStorage.setItem", _ => true).SetVoidResult();
        JSInterop.SetupVoid("birkNextStorage.setDiscovery", _ => true).SetVoidResult();
        JSInterop.SetupVoid("birkNextStorage.setSnapshots", _ => true).SetVoidResult();
        JSInterop.Setup<string?>("birkNextStorage.getDiscovery").SetResult(null);
        _api.Setup(x => x.DetectFromUrlAsync(Url, default)).ReturnsAsync(() => Observation());
    }

    private static TargetEnvironmentDetectionResult Observation(bool signIn = false, DetectionConfidence confidence = DetectionConfidence.High) => new()
    {
        OriginalUrl = Url, Success = true, Reachability = TargetReachability.Reachable, Confidence = confidence,
        DetectedClientFramework = ClientFrameworkType.BlazorWebAssembly, State = DetectionState.Complete,
        AuthenticationRequired = signIn, DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId,
        DetectedAuthority = Authority, DetectedClientId = ClientId,
    };

    private static FrontendAnalysisProfile Profile(FrontendAuthenticationSettings? auth = null, string url = Url) =>
        new() { Id = "dev", Name = "Dev", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = url, Authentication = auth ?? new() };

    private static DetectionSnapshot Snapshot(TargetEnvironmentDetectionResult? result = null, string profileId = "dev", string url = Url) =>
        new() { Result = result ?? Observation(), DetectedProfileId = profileId, DetectedUrl = url, DetectedAt = DateTimeOffset.UnixEpoch };

    // ── Effective-value precedence ──────────────────────────────────────────────────────────

    [Fact]
    public void Confident_observation_is_effective_without_any_saved_configuration()
    {
        var effective = EffectiveAuthentication.Resolve(Profile(), Snapshot());
        effective.Provider.Should().Be(FrontendAuthenticationType.MicrosoftEntraId);
        effective.ProviderValue.Origin.Should().Be(AuthenticationValueOrigin.Observed);
        effective.SignInRequired.Should().BeFalse();
        effective.SignInValue.Origin.Should().Be(AuthenticationValueOrigin.Observed);
        effective.SignInValue.Value.Should().Be("No");
        effective.Settings.ExpectedAuthority.Should().Be(Authority, "empty identifiers are completed from observation");
        effective.ReviewRequired.Should().BeFalse();
        EffectiveAuthentication.SourceLabel(effective).Should().Be("Observed configuration");
    }

    [Fact]
    public void Precedence_is_override_then_observed_then_source_config_then_unknown()
    {
        var overridden = new FrontendAuthenticationSettings { AuthenticationType = FrontendAuthenticationType.OpenIdConnect, ConfiguredOverrides = [EffectiveAuthentication.ProviderKey] };
        EffectiveAuthentication.Resolve(Profile(overridden), Snapshot()).ProviderValue.Origin.Should().Be(AuthenticationValueOrigin.ConfiguredOverride);
        EffectiveAuthentication.Resolve(Profile(), Snapshot(), Authority).ProviderValue.Origin.Should().Be(AuthenticationValueOrigin.Observed, "observation beats source/config");
        var fromSource = EffectiveAuthentication.Resolve(Profile(), null, Authority);
        fromSource.ProviderValue.Origin.Should().Be(AuthenticationValueOrigin.DetectedFromConfiguration);
        fromSource.Provider.Should().Be(FrontendAuthenticationType.MicrosoftEntraId);
        fromSource.SignInValue.Origin.Should().Be(AuthenticationValueOrigin.Unknown, "configuration does not establish the target's sign-in rule");
        var unknown = EffectiveAuthentication.Resolve(Profile(), null);
        unknown.ProviderValue.Should().Be(new EffectiveAuthenticationValue("Provider", "Not determined", AuthenticationValueOrigin.Unknown));
        EffectiveAuthentication.SourceLabel(unknown).Should().Be("Not configured");
    }

    [Fact]
    public void Stale_foreign_failed_or_low_confidence_observation_is_never_applied()
    {
        EffectiveAuthentication.Resolve(Profile(), Snapshot(profileId: "qa")).HasObserved.Should().BeFalse("another environment's evidence");
        EffectiveAuthentication.Resolve(Profile(), Snapshot(url: "https://other.example.com")).HasObserved.Should().BeFalse("an old target URL");
        EffectiveAuthentication.Resolve(Profile(), Snapshot(Observation(confidence: DetectionConfidence.Low))).HasObserved.Should().BeFalse("weak inference is not observation");
        var failed = Observation(); failed.Success = false;
        EffectiveAuthentication.Resolve(Profile(), Snapshot(failed)).HasObserved.Should().BeFalse();
    }

    [Fact]
    public void Override_is_effective_keeps_the_observed_value_and_disagreement_is_review_required()
    {
        var auth = new FrontendAuthenticationSettings { AuthenticationType = FrontendAuthenticationType.OpenIdConnect, ConfiguredOverrides = [EffectiveAuthentication.ProviderKey] };
        var effective = EffectiveAuthentication.Resolve(Profile(auth), Snapshot());
        effective.Provider.Should().Be(FrontendAuthenticationType.OpenIdConnect);
        effective.ProviderValue.ObservedValue.Should().Be("Microsoft Entra ID");
        effective.ProviderValue.ReviewRequired.Should().BeTrue();
        effective.ReviewRequired.Should().BeTrue();

        var agreeing = EffectiveAuthentication.Resolve(Profile(new() { AuthenticationType = FrontendAuthenticationType.MicrosoftEntraId, ConfiguredOverrides = [EffectiveAuthentication.ProviderKey] }), Snapshot());
        agreeing.ProviderValue.ObservedMatches.Should().BeTrue();
        agreeing.ReviewRequired.Should().BeFalse();
    }

    [Fact]
    public void Legacy_saved_values_count_as_configured_and_a_no_op_save_keeps_the_verification_fingerprint()
    {
        var legacy = new FrontendAuthenticationSettings { AuthenticationType = FrontendAuthenticationType.MicrosoftEntraId, RequiresAuthentication = true };
        EffectiveAuthentication.IsOverridden(legacy, EffectiveAuthentication.ProviderKey).Should().BeTrue();
        var profile = Profile(legacy);
        var before = ManualAuthenticationVerificationEvidence.Fingerprint(profile);

        var draft = Profile(new() { AuthenticationType = FrontendAuthenticationType.MicrosoftEntraId, RequiresAuthentication = true });
        EffectiveAuthentication.ApplyEdit(legacy, draft.Authentication);
        draft.Authentication.ConfiguredOverrides.Should().BeNull("nothing changed, so the persisted shape is unchanged");
        ManualAuthenticationVerificationEvidence.Fingerprint(draft).Should().Be(before);
    }

    [Fact]
    public void Only_values_a_person_changes_become_overrides_and_observed_values_are_never_copied()
    {
        var saved = new FrontendAuthenticationSettings();
        var draft = new FrontendAuthenticationSettings { AuthenticationType = FrontendAuthenticationType.OpenIdConnect };
        EffectiveAuthentication.ApplyEdit(saved, draft);
        EffectiveAuthentication.IsOverridden(draft, EffectiveAuthentication.ProviderKey).Should().BeTrue();
        EffectiveAuthentication.IsOverridden(draft, EffectiveAuthentication.SignInRequiredKey).Should().BeFalse();
        var explicitNo = new FrontendAuthenticationSettings { RequiresAuthentication = false };
        EffectiveAuthentication.ApplyEdit(new FrontendAuthenticationSettings { RequiresAuthentication = true }, explicitNo);
        explicitNo.ConfiguredOverrides.Should().Equal([EffectiveAuthentication.SignInRequiredKey], "an explicit change to No is an override legacy inference cannot express");

        var untouched = new FrontendAuthenticationSettings();
        EffectiveAuthentication.ApplyEdit(saved, untouched);
        untouched.AuthenticationType.Should().Be(FrontendAuthenticationType.None, "an observed provider is effective without being written to configuration");

        var cleared = new FrontendAuthenticationSettings { AuthenticationType = FrontendAuthenticationType.OpenIdConnect, ConfiguredOverrides = [EffectiveAuthentication.ProviderKey] };
        EffectiveAuthentication.ClearOverride(cleared, EffectiveAuthentication.ProviderKey);
        EffectiveAuthentication.Resolve(Profile(cleared), Snapshot()).ProviderValue.Origin.Should().Be(AuthenticationValueOrigin.Observed);
    }

    [Fact]
    public void Effective_settings_carry_no_secret_or_trust_fields()
    {
        typeof(FrontendAuthenticationSettings).GetProperties().Select(p => p.Name)
            .Should().NotContain(n => n.Contains("Password") || n.Contains("Token") || n.Contains("Secret") || n.Contains("Cookie") || n.Contains("Certificate") || n.Contains("Approved"));
        typeof(EffectiveAuthenticationSettings).GetProperties().Select(p => p.Name)
            .Should().NotContain(n => n.Contains("Authoriz") || n.Contains("Verified") || n.Contains("Trust"));
    }

    // ── Consumers ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Review_context_uses_effective_authentication_without_granting_a_session_or_staling_verification()
    {
        var profile = Profile();
        var settings = new Mock<IFrontendAnalysisSettingsService>();
        settings.Setup(s => s.ActiveProfile).Returns(profile);
        settings.Setup(s => s.Settings).Returns(new FrontendAnalysisSettings { Profiles = [profile], ActiveProfileId = profile.Id });
        settings.Setup(s => s.ValidateProfile(It.IsAny<FrontendAnalysisProfile>())).Returns(new ProfileValidationResult());
        settings.Setup(s => s.LoadAsync(It.IsAny<IJSRuntime>())).Returns(Task.CompletedTask);
        settings.Setup(s => s.GetDetectionSnapshot("dev")).Returns(Snapshot(Observation(signIn: true)));
        var factory = new FrontendAnalysisContextFactory(settings.Object, new PlaceholderAuthenticatedBrowserSessionService(settings.Object), new Mock<IJSRuntime>().Object);

        var context = await factory.GetActiveContextAsync();

        context.AuthenticationType.Should().Be(FrontendAuthenticationType.MicrosoftEntraId, "no Edit → Save is needed to use observed values");
        context.RequiresAuthentication.Should().BeTrue();
        context.IsAuthenticatedSessionAvailable.Should().BeFalse("a known provider is not an authenticated context");
        context.ManualVerificationFingerprint.Should().Be(ManualAuthenticationVerificationEvidence.Fingerprint(Profile()), "digests stay over the saved profile");
        profile.Authentication.AuthenticationType.Should().Be(FrontendAuthenticationType.None, "the saved profile is not modified");
    }

    // ── UI ──────────────────────────────────────────────────────────────────────────────────

    private IRenderedComponent<Component> Open(string authenticationJson, string activeId = "local", string devUrl = Url)
    {
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult($$"""
        {"activeProfileId":"{{activeId}}","profiles":[
          {"id":"local","name":"Local","targetUrl":"https://local.example.com"},
          {"id":"dev","name":"Dev","environmentType":"Development","targetUrl":"{{devUrl}}","authentication":{{authenticationJson}}}
        ]}
        """);
        var cut = Render<Component>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("Dev")).Click();
        return cut;
    }

    /// <summary>Runs the read-only target check (its button lives on the Target Application tab, or in the header in older layouts)
    /// and waits until its evidence is stored for this environment.</summary>
    private void CheckTarget(IRenderedComponent<Component> cut)
    {
        cut.Find("#target-tab-target").Click();
        cut.FindAll("button").First(b => b.TextContent.Trim() is "Check target application" or "Detect settings").Click();
        cut.WaitForAssertion(() => _settings.GetDetectionSnapshot("dev").Should().NotBeNull());
    }

    private static void AuthTab(IRenderedComponent<Component> cut) => cut.FindAll("[role=tab]").Single(t => t.TextContent.Trim() == "Authentication").Click();

    private static string Text(IRenderedComponent<Component> cut, string id) => cut.Find($"[data-testid='{id}']").TextContent.Trim();

    [Fact]
    public void Observed_values_show_their_origin_and_need_no_save()
    {
        var cut = Open("""{ "authenticationType": "None", "requiresAuthentication": false }""");
        CheckTarget(cut);
        AuthTab(cut);

        Text(cut, "authentication-configured-provider").Should().Be("Microsoft Entra ID");
        Text(cut, "authentication-configured-provider-origin").Should().Be("Observed");
        Text(cut, "authentication-sign-in-required").Should().Be("No");
        Text(cut, "authentication-sign-in-required-origin").Should().Be("Observed");
        Text(cut, "authentication-configuration-source").Should().Be("Observed configuration");
        Text(cut, "authentication-configuration-state").Should().NotBe("Configured", "observed is not configured");
        cut.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == "Save changes", "nothing needs saving");
        cut.FindAll("[data-testid='authentication-override-review']").Should().BeEmpty();
        _settings.Settings.Profiles.Single(p => p.Id == "dev").Authentication.AuthenticationType.Should().Be(FrontendAuthenticationType.None, "observation is not copied into configuration");
        cut.Find("[data-testid='authentication-verification']").TextContent.Should().NotContain("Passed", "observation never marks the manual sign-in check");
    }

    [Fact]
    public void Override_differing_from_observation_is_review_required_and_keeps_both_facts()
    {
        var cut = Open("""{ "authenticationType": "OpenIdConnect", "requiresAuthentication": false, "configuredOverrides": ["provider"] }""");
        CheckTarget(cut);
        AuthTab(cut);

        Text(cut, "authentication-configured-provider").Should().Be("OpenID Connect");
        Text(cut, "authentication-configured-provider-origin").Should().Be("Configured override");
        Text(cut, "authentication-configured-provider-observed").Should().Be("Observed value: Microsoft Entra ID");
        Text(cut, "authentication-override-review").Should().Contain(EffectiveAuthentication.ReviewRequiredText).And.Contain("not a defect");
        Text(cut, "authentication-configuration-source").Should().Be("Configured override");

        cut.Find("[data-testid='authentication-use-observed']").Click();
        cut.WaitForAssertion(() => Text(cut, "authentication-configured-provider-origin").Should().Be("Observed"));
        cut.FindAll("[data-testid='authentication-override-review']").Should().BeEmpty();
    }

    [Fact]
    public void Set_as_active_is_a_primary_action_until_active()
    {
        var cut = Open("""{ "authenticationType": "None" }""");
        var button = cut.Find("[data-testid='set-active-target']");
        button.ClassList.Should().Contain("btn-primary").And.NotContain("btn-secondary");
        button.HasAttribute("disabled").Should().BeFalse();
        button.TagName.Should().Be("BUTTON", "a native button keeps keyboard focus and activation");

        button.Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid='set-active-target']").Should().BeEmpty("an active environment shows no activation action"));
        Text(cut, "active-target-badge").Should().Be("Active environment");
    }

    [Fact]
    public void Set_as_active_is_disabled_only_when_it_cannot_run()
    {
        var cut = Open("""{ "authenticationType": "None" }""", devUrl: "");
        var button = cut.Find("[data-testid='set-active-target']");
        button.HasAttribute("disabled").Should().BeTrue();
        button.GetAttribute("aria-describedby").Should().Be("activation-gate-reason");
        cut.Find("#activation-gate-reason").TextContent.Should().Contain("Frontend URL");
    }
}
