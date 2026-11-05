using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Detect → validate → populate safe missing settings → review exceptions: empty + trusted values are adopted (with provenance, idempotent);
/// equal values are not shown; differing saved values are conflicts and never overwritten; untrusted or low-confidence values stay
/// suggestions; a rejected value is not proposed again until detection finds a different one; the provider is never copied.
/// </summary>
public sealed class AuthenticationAdoptionTests
{
    private const string Target = "https://app.example.no/";
    private const string Tenant = "11111111-2222-3333-4444-555555555555";
    private const string Client = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);

    private static TargetEnvironmentDetectionResult Detection(string? authority = $"https://login.microsoftonline.com/{Tenant}/v2.0", string? tenant = Tenant, string? client = Client,
        DetectionConfidence confidence = DetectionConfidence.High, string url = Target, params string[] redirects) => new()
    {
        Success = true, OriginalUrl = url, NormalizedTargetUrl = url, Confidence = confidence, DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId,
        DetectedAuthority = authority, DetectedTenantId = tenant, DetectedClientId = client, DetectedRedirectUrls = [.. redirects],
    };

    private static AuthenticationAdoptionOutcome Outcome(IReadOnlyList<AuthenticationAdoptionItem> items, string setting) => items.First(i => i.Setting == setting).Outcome;

    [Fact]
    public void Empty_saved_values_that_pass_the_trust_policy_are_adopted_with_provenance_and_adoption_is_idempotent()
    {
        var saved = new FrontendAuthenticationSettings();
        var history = new AuthenticationAdoptionHistory();
        var detection = Detection(redirects: [Target + "signin-oidc"]);
        var items = AuthenticationAdoption.Evaluate(detection, saved, Target, history);
        Outcome(items, AuthenticationAdoption.Authority).Should().Be(AuthenticationAdoptionOutcome.AutoAdopt);
        Outcome(items, AuthenticationAdoption.Tenant).Should().Be(AuthenticationAdoptionOutcome.AutoAdopt);
        Outcome(items, AuthenticationAdoption.ClientId).Should().Be(AuthenticationAdoptionOutcome.AutoAdopt);
        Outcome(items, AuthenticationAdoption.RedirectUrls).Should().Be(AuthenticationAdoptionOutcome.AutoAdopt);

        var adopted = AuthenticationAdoption.Adopt(items, detection, saved, history, Now, Now);
        adopted.Should().BeEquivalentTo([AuthenticationAdoption.Authority, AuthenticationAdoption.Tenant, AuthenticationAdoption.ClientId, AuthenticationAdoption.RedirectUrls]);
        saved.ExpectedClientId.Should().Be(Client);
        saved.AuthenticationType.Should().Be(FrontendAuthenticationType.None, "the provider is observed and used directly — never copied into configuration");
        history.Provenance.Should().OnlyContain(p => p.Source == "Detected" && p.Validation == "Passed trust policy" && p.DetectedAt == Now);
        AuthenticationAdoption.OverrideState(AuthenticationAdoption.ClientId, saved, history).Should().Be("Detected and validated");

        var again = AuthenticationAdoption.Evaluate(detection, saved, Target, history);
        again.Should().NotContain(i => i.Outcome == AuthenticationAdoptionOutcome.AutoAdopt, "a second run finds the values equal");
        AuthenticationAdoption.Adopt(again, detection, saved, history, Now, Now).Should().BeEmpty();
        history.Provenance.Should().HaveCount(4, "no duplicate provenance records");
    }

    [Fact]
    public void A_saved_value_that_differs_is_a_conflict_and_is_never_overwritten()
    {
        var saved = new FrontendAuthenticationSettings { ExpectedClientId = "99999999-9999-9999-9999-999999999999", ExpectedTenant = Tenant };
        var items = AuthenticationAdoption.Evaluate(Detection(), saved, Target);
        Outcome(items, AuthenticationAdoption.ClientId).Should().Be(AuthenticationAdoptionOutcome.Conflict);
        Outcome(items, AuthenticationAdoption.Tenant).Should().Be(AuthenticationAdoptionOutcome.Equal, "equal values are not shown as differences");
        AuthenticationAdoption.Adopt(items, Detection(), saved, new(), Now, Now).Should().NotContain(AuthenticationAdoption.ClientId);
        saved.ExpectedClientId.Should().Be("99999999-9999-9999-9999-999999999999");
        AuthenticationAdoption.OverrideState(AuthenticationAdoption.ClientId, saved, null).Should().Be("Manual override");
    }

    [Theory]
    [InlineData("https://evil.example.com/tenant/v2.0", "not a Microsoft identity platform authority")]
    [InlineData("http://login.microsoftonline.com/x", "not an https URL")]
    [InlineData("https://login.microsoftonline.com/99999999-0000-0000-0000-000000000000/v2.0", "differs from the detected tenant")]
    public void An_untrusted_authority_is_a_suggestion_with_its_reason(string authority, string reason)
    {
        var item = AuthenticationAdoption.Evaluate(Detection(authority), new(), Target).First(i => i.Setting == AuthenticationAdoption.Authority);
        item.Outcome.Should().Be(AuthenticationAdoptionOutcome.Suggestion);
        item.Reason.Should().Contain(reason);
    }

    [Fact]
    public void Multi_tenant_values_low_confidence_and_another_origin_are_never_adopted()
    {
        var common = AuthenticationAdoption.Evaluate(Detection(tenant: "common"), new(), Target);
        Outcome(common, AuthenticationAdoption.Tenant).Should().Be(AuthenticationAdoptionOutcome.Suggestion);

        var low = AuthenticationAdoption.Evaluate(Detection(confidence: DetectionConfidence.Medium), new(), Target);
        low.Where(i => i.Detected is not null && i.Setting != AuthenticationAdoption.Provider).Should().OnlyContain(i => i.Outcome == AuthenticationAdoptionOutcome.Suggestion);

        var elsewhere = AuthenticationAdoption.Evaluate(Detection(url: "https://other.example.no/"), new(), Target);
        elsewhere.First(i => i.Setting == AuthenticationAdoption.ClientId).Reason.Should().Contain("not made for this environment's Frontend URL");

        var badClient = AuthenticationAdoption.Evaluate(Detection(client: "client-id"), new(), Target);
        Outcome(badClient, AuthenticationAdoption.ClientId).Should().Be(AuthenticationAdoptionOutcome.Suggestion);

        var redirects = AuthenticationAdoption.Evaluate(Detection(redirects: ["https://attacker.example.com/cb"]), new(), Target);
        redirects.Single(i => i.Setting == AuthenticationAdoption.RedirectUrls).Outcome.Should().Be(AuthenticationAdoptionOutcome.Suggestion);
    }

    [Fact]
    public void A_rejected_value_is_not_proposed_again_until_detection_finds_a_different_one()
    {
        var saved = new FrontendAuthenticationSettings { ExpectedClientId = "99999999-9999-9999-9999-999999999999" };
        var history = new AuthenticationAdoptionHistory();
        var detection = Detection();
        var conflict = AuthenticationAdoption.Evaluate(detection, saved, Target, history).First(i => i.Setting == AuthenticationAdoption.ClientId);
        AuthenticationAdoption.Decide(conflict.Setting, conflict, "keep", null, detection, saved, history, Now);
        Outcome(AuthenticationAdoption.Evaluate(detection, saved, Target, history), AuthenticationAdoption.ClientId).Should().Be(AuthenticationAdoptionOutcome.Rejected);
        var changed = Detection(client: "cccccccc-cccc-cccc-cccc-cccccccccccc");
        Outcome(AuthenticationAdoption.Evaluate(changed, saved, Target, history), AuthenticationAdoption.ClientId).Should().Be(AuthenticationAdoptionOutcome.Conflict, "new evidence reopens the question");
    }

    [Fact]
    public void A_rejected_redirect_url_is_not_proposed_again()
    {
        var history = new AuthenticationAdoptionHistory();
        var detection = Detection(redirects: ["https://elsewhere.example.no/cb"]);
        var item = AuthenticationAdoption.Evaluate(detection, new(), Target, history).Single(i => i.Setting == AuthenticationAdoption.RedirectUrls);
        item.Outcome.Should().Be(AuthenticationAdoptionOutcome.Suggestion);
        AuthenticationAdoption.Decide(item.Setting, item, "keep", null, detection, new(), history, Now);
        AuthenticationAdoption.Evaluate(detection, new(), Target, history).Should().NotContain(i => i.Setting == AuthenticationAdoption.RedirectUrls);
    }

    [Fact]
    public void Use_detected_custom_and_restore_previous_record_their_provenance()
    {
        var saved = new FrontendAuthenticationSettings { ExpectedClientId = "99999999-9999-9999-9999-999999999999" };
        var history = new AuthenticationAdoptionHistory();
        var detection = Detection();
        var conflict = AuthenticationAdoption.Evaluate(detection, saved, Target, history).First(i => i.Setting == AuthenticationAdoption.ClientId);
        AuthenticationAdoption.Decide(conflict.Setting, conflict, "use", null, detection, saved, history, Now);
        saved.ExpectedClientId.Should().Be(Client);
        AuthenticationAdoption.OverrideState(AuthenticationAdoption.ClientId, saved, history).Should().Be("Detected, accepted by a person");
        AuthenticationAdoption.Decide(conflict.Setting, conflict, "restore", null, detection, saved, history, Now);
        saved.ExpectedClientId.Should().Be("99999999-9999-9999-9999-999999999999", "Restore previous puts back the value the decision replaced");
        AuthenticationAdoption.Decide(conflict.Setting, conflict, "custom", "  dddddddd-dddd-dddd-dddd-dddddddddddd ", detection, saved, history, Now);
        saved.ExpectedClientId.Should().Be("dddddddd-dddd-dddd-dddd-dddddddddddd");
        AuthenticationAdoption.OverrideState(AuthenticationAdoption.ClientId, saved, history).Should().Be("Manual override");
    }

    [Fact]
    public void No_detection_or_a_failed_detection_proposes_nothing_and_missing_values_keep_the_saved_ones()
    {
        AuthenticationAdoption.Evaluate(null, new(), Target).Should().BeEmpty();
        AuthenticationAdoption.Evaluate(new() { Success = false }, new(), Target).Should().BeEmpty();
        var saved = new FrontendAuthenticationSettings { ExpectedAuthority = "https://login.microsoftonline.com/x" };
        Outcome(AuthenticationAdoption.Evaluate(Detection(authority: null), saved, Target), AuthenticationAdoption.Authority).Should().Be(AuthenticationAdoptionOutcome.NotDetected);
    }

    [Fact]
    public void Adoption_makes_legacy_override_tracking_explicit_so_adopted_values_are_not_overrides()
    {
        var saved = new FrontendAuthenticationSettings { RequiresAuthentication = true };
        saved.ConfiguredOverrides.Should().BeNull();
        AuthenticationAdoption.Adopt(AuthenticationAdoption.Evaluate(Detection(), saved, Target), Detection(), saved, new(), Now, Now);
        saved.ConfiguredOverrides.Should().Equal([EffectiveAuthentication.SignInRequiredKey], "what a person saved stays an override; adopted identifiers are not");
    }
}
