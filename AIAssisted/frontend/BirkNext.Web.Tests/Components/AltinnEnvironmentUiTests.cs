using System.Net;
using BirkNext.Integrations;
using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Target Environment → External Integrations → Altinn and its consumers: provenance states as text, manual vs automated readiness apart, Detect never saves, only
/// selected proposals are applied, the secret is a password field sent once and never shown, browser evidence is labelled apart from the
/// unattended client, and Skolenærvær/Test Data consume the setup without a second editor or any terminal instruction.
/// </summary>
public sealed class AltinnEnvironmentUiTests : BunitContext
{
    private const string Env = "dev";
    private const string Secret = "session-secret-NEVER-SHOWN";
    private readonly FakeApi _api = new();

    public AltinnEnvironmentUiTests()
    {
        Services.AddSingleton<IAltinnEnvironmentApiService>(_api);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static AltinnSettingField F(string key, string group, string label, string? value, AltinnSettingState state, AltinnSettingSource source,
        AltinnRequirement requirement = AltinnRequirement.Manual, AltinnSettingCandidate? proposal = null) => new()
    {
        Key = key, Group = group, Label = label, Value = value, State = state, Source = source, SourceLabel = source.ToString(), Requirement = requirement, Proposal = proposal,
    };

    internal static AltinnEnvironmentView View(bool detected = false) => new()
    {
        EnvironmentId = Env, Detected = detected,
        Fields =
        [
            F("environment", "Application", "Altinn environment", "Test (TT02)", AltinnSettingState.Declared, AltinnSettingSource.TestPack,
                proposal: new("Test (TT02)", AltinnSettingState.Declared, AltinnSettingSource.TestPack, "Skolenærvær test pack")),
            F("app", "Application", "App identifier", "buf/skolenaervaer", AltinnSettingState.Configured, AltinnSettingSource.TargetEnvironment),
            F("form-data-type", "Application", "Form data type", "bufdir_barnevern_tiltak_skolenaervaer_v2_0_0_altinn", AltinnSettingState.Detected, AltinnSettingSource.SourceAnalysis,
                proposal: new("bufdir_barnevern_tiltak_skolenaervaer_v2_0_0_altinn", AltinnSettingState.Detected, AltinnSettingSource.SourceAnalysis, "Source Analysis")),
            F("browser-redirect", "Browser authentication", "Observed browser redirect URI", "https://af.tt02.altinn.no/api/cb", AltinnSettingState.Observed, AltinnSettingSource.RuntimeObservation, AltinnRequirement.Optional),
            F("redirect-uri", "Automated execution", "Registered redirect URI", null, AltinnSettingState.Missing, AltinnSettingSource.None, AltinnRequirement.Automated),
            F("client-secret", "Automated execution", "Client secret", null, AltinnSettingState.AvailableForSession, AltinnSettingSource.Session, AltinnRequirement.Automated),
        ],
        Summary =
        [
            new("app", "App", AltinnReadinessState.Ready, "Test (TT02) · buf/skolenaervaer"),
            new("authentication", "Authentication", AltinnReadinessState.Partial, "Unattended client incomplete."),
            new("runtime", "Runtime", AltinnReadinessState.Partial, "Observed (1 observation); not verified by BirkNext."),
            new("automated", "Automated execution", AltinnReadinessState.Blocked, "Blocked by 1 requirement: redirect URI"),
        ],
        ManualReadiness = new("manual", "Manual test readiness", AltinnReadinessState.Ready, "TT02 app, reporter, child, integration and contract are known."),
        AutomatedReadiness = new("automated", "Automated BirkNext execution", AltinnReadinessState.Blocked, "Blocked by 1 requirement: No registered redirect URI"),
        Identities = [new("reporter", "reporter-a", "Skolenærvær reporter A", AltinnSettingState.Confirmed, "Test Data · this backend session", "")],
        Detection = new(1, 2, 1, 1, 0, ["Missing: Registered redirect URI (automated execution)"]),
    };

    [Fact]
    public void Pane_shows_states_as_text_and_keeps_manual_and_automated_readiness_apart()
    {
        var cut = Render<AltinnEnvironmentPane>(p => p.Add(c => c.EnvironmentId, Env));
        cut.WaitForAssertion(() => cut.Find("[data-testid=ae-summary]"));

        cut.Find("[data-testid=ae-title]").TextContent.Should().Be("Altinn · TT02");
        cut.Find("[data-testid=ae-readiness-manual]").TextContent.Should().Contain("Ready");
        cut.Find("[data-testid=ae-readiness-automated]").TextContent.Should().Contain("Blocked").And.Contain("redirect URI");
        cut.Find("[data-testid=ae-missing-line]").TextContent.Should().Be("1 required setting missing or in conflict");
        State(cut, "environment").Should().Be("Suggested", "a test-pack default is a suggestion, never 'configured'");
        State(cut, "form-data-type").Should().Be("Detected from source", "source evidence is not deployed runtime");
        State(cut, "browser-redirect").Should().Be("Observed");
        State(cut, "client-secret").Should().Be("Available for this session");
        cut.Find("[data-testid=ae-field][data-key=client-secret] .ae-value").TextContent.Should().Be("—", "a secret value is never shown");
        cut.Find("[data-testid=ae-browser-note]").TextContent.Should().Contain("never configures BirkNext's unattended client");
        cut.Find("[data-testid=ae-manage-identities]").GetAttribute("href").Should().Be("/test-data");
        cut.Markup.Should().NotContain("PowerShell").And.NotContain(".ps1");
        _api.Calls.Should().Equal("get");
    }

    private static string State(IRenderedComponent<AltinnEnvironmentPane> cut, string key) =>
        cut.Find($"[data-testid=ae-field][data-key={key}] [data-testid=ae-field-state]").TextContent;

    [Fact]
    public void Detect_never_saves_and_only_selected_proposals_are_applied()
    {
        var cut = Render<AltinnEnvironmentPane>(p => p.Add(c => c.EnvironmentId, Env));
        cut.WaitForAssertion(() => cut.Find("[data-testid=ae-detect]"));
        cut.Find("[data-testid=ae-detect]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=ae-detection-line]").TextContent.Should().Be("1 configured · 2 detected · 1 runtime observation · 1 value missing · 0 conflicts"));
        _api.Calls.Should().Equal("get", "detect");
        _api.Saved.Should().BeEmpty("detection is not a save");
        cut.FindAll("[data-testid=ae-proposals] .ae-proposal").Select(item => item.GetAttribute("data-key")).Should().Equal("environment", "form-data-type");

        cut.Find("[data-testid=ae-proposals] .ae-proposal[data-key=environment] input").Change(false);
        cut.Find("[data-testid=ae-apply]").Click();
        cut.WaitForAssertion(() => _api.Saved.Should().ContainSingle());
        _api.Saved[0].FormDataType.Should().Be("bufdir_barnevern_tiltak_skolenaervaer_v2_0_0_altinn");
        _api.Saved[0].Environment.Should().BeNull("an unselected proposal is never saved");
    }

    [Fact]
    public void Session_secret_is_a_password_field_sent_once_and_never_shown()
    {
        var cut = Render<AltinnEnvironmentPane>(p => p.Add(c => c.EnvironmentId, Env));
        cut.WaitForAssertion(() => cut.Find("[data-testid=ae-secret]"));
        var input = cut.Find("[data-testid=ae-secret]");
        input.GetAttribute("type").Should().Be("password");
        input.Change(Secret);
        cut.Find("[data-testid=ae-add-secret]").Click();

        cut.WaitForAssertion(() => _api.Secrets.Should().Equal(Secret));
        cut.Find("[data-testid=ae-secret]").GetAttribute("value").Should().BeNullOrEmpty("the field is cleared after sending");
        cut.Markup.Should().NotContain(Secret);
        JSInterop.Invocations.Should().NotContain(call => call.Arguments.Any(argument => argument != null && argument.ToString()!.Contains(Secret)), "never written to browser storage");
    }

    [Fact]
    public void Skolenaervaer_consumes_the_shared_setup_with_manual_and_automated_readiness()
    {
        var cut = Render<AltinnEnvironmentStatus>(p => p.Add(c => c.EnvironmentId, Env));
        cut.WaitForAssertion(() => cut.Find("[data-testid=aes-manual]"));
        cut.Find("[data-testid=aes-environment]").TextContent.Should().Contain("Ready").And.Contain("Source: Target Environment → External Integrations → Altinn");
        cut.Find("[data-testid=aes-manual]").TextContent.Should().Contain("Ready");
        cut.Find("[data-testid=aes-automated]").TextContent.Should().Contain("Blocked");
        cut.Find("[data-testid=aes-open]").GetAttribute("href").Should().Be(AltinnEnvironmentPresentation.AltinnHref(Env));
        cut.FindAll("input").Should().BeEmpty("the consumer never edits the Altinn setup");
    }

    [Fact]
    public void Session_identity_is_a_password_field_and_the_identifier_never_comes_back()
    {
        var cut = Render<SessionTestIdentitiesPanel>();
        var identifier = cut.Find("[data-testid=sti-identifier]");
        identifier.GetAttribute("type").Should().Be("password");
        cut.Find("[data-testid=sti-alias]").Change("reporter-a");
        identifier.Change("05848512345");
        cut.Find("[data-testid=sti-confirm]").Change("reporter-a");
        cut.Find("[data-testid=sti-add]").Click();

        cut.WaitForAssertion(() => _api.Identities.Should().ContainSingle());
        _api.Identities[0].Should().Match<AltinnSessionIdentityRequest>(r => r.Role == "reporter" && r.Alias == "reporter-a" && r.ConfirmProfileAlias == "reporter-a");
        cut.Find("[data-testid=sti-message]").TextContent.Should().Contain("reporter-a").And.NotContain("05848512345");
        cut.Find("[data-testid=sti-identifier]").GetAttribute("value").Should().BeNullOrEmpty();
    }

    [Fact]
    public void Presentation_never_collapses_states_and_maps_only_non_secret_proposals()
    {
        AltinnEnvironmentPresentation.StateLabel(AltinnSettingState.Declared).Should().Be("Suggested");
        AltinnEnvironmentPresentation.StateLabel(AltinnSettingState.Verified).Should().Be("Runtime verified");
        AltinnEnvironmentPresentation.StateLabel(AltinnSettingState.AvailableToBackend).Should().Be("Available to backend");
        var update = AltinnEnvironmentPresentation.Apply([("environment", "Test (TT02)"), ("scopes", "openid altinn:instances.write"), ("client-secret", "x")]);
        update.Environment.Should().Be("Test");
        update.Scopes.Should().Equal("openid", "altinn:instances.write");
        AltinnEnvironmentPresentation.Proposals(View() with
        {
            Fields = [F("client-secret", "Automated execution", "Client secret", null, AltinnSettingState.Missing, AltinnSettingSource.None,
                proposal: new("x", AltinnSettingState.Detected, AltinnSettingSource.Session, "Session"))],
        }).Should().BeEmpty("a secret is never an applicable proposal");
    }

    private sealed class FakeApi : IAltinnEnvironmentApiService
    {
        public List<string> Calls { get; } = [];
        public List<AltinnEnvironmentSettingsUpdate> Saved { get; } = [];
        public List<string> Secrets { get; } = [];
        public List<AltinnSessionIdentityRequest> Identities { get; } = [];

        private static ActiveEventApiResult<AltinnEnvironmentView> Ok(AltinnEnvironmentView view) => new(view, null, HttpStatusCode.OK);
        public Task<ActiveEventApiResult<AltinnEnvironmentView>> GetAsync(string environmentId, CancellationToken ct = default) { Calls.Add("get"); return Task.FromResult(Ok(View())); }
        public Task<ActiveEventApiResult<AltinnEnvironmentView>> DetectAsync(string environmentId, CancellationToken ct = default) { Calls.Add("detect"); return Task.FromResult(Ok(View(detected: true))); }
        public Task<ActiveEventApiResult<AltinnEnvironmentView>> SaveAsync(string environmentId, AltinnEnvironmentSettingsUpdate update, CancellationToken ct = default) { Saved.Add(update); return Task.FromResult(Ok(View())); }
        public Task<ActiveEventApiResult<AltinnEnvironmentView>> RecordObservationAsync(string environmentId, AltinnRuntimeObservationRequest request, CancellationToken ct = default) => Task.FromResult(Ok(View()));
        public Task<ActiveEventApiResult<AltinnEnvironmentView>> RecordBrowserObservationAsync(string environmentId, AltinnBrowserObservationRequest request, CancellationToken ct = default) => Task.FromResult(Ok(View()));
        public Task<ActiveEventApiResult<bool>> AddSessionSecretAsync(string environmentId, string secret, CancellationToken ct = default) { Secrets.Add(secret); return Task.FromResult(new ActiveEventApiResult<bool>(true, null)); }
        public Task<ActiveEventApiResult<bool>> ClearSessionAsync(string environmentId, CancellationToken ct = default) => Task.FromResult(new ActiveEventApiResult<bool>(true, null));
        public Task<ActiveEventApiResult<bool>> AddSessionIdentityAsync(AltinnSessionIdentityRequest request, CancellationToken ct = default) { Identities.Add(request); return Task.FromResult(new ActiveEventApiResult<bool>(true, null)); }
        public Task<ActiveEventApiResult<bool>> ClearSessionIdentitiesAsync(CancellationToken ct = default) => Task.FromResult(new ActiveEventApiResult<bool>(true, null));
    }
}
