using System.Net;
using BirkNext.Integrations;
using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Target Environment → Connected Systems → Altinn and its consumers: provenance states as text, manual vs automated readiness apart, Detect never saves, only
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

        cut.Find("[data-testid=ae]").GetAttribute("aria-label").Should().Be("Altinn · TT02", "the region is labelled; the card shows the visible name");
        cut.FindAll("h1, h2, h3, h4").Should().BeEmpty("the shell never repeats the system name as a heading");
        cut.FindAll("h5").Select(h => h.TextContent.Trim()).Should().Contain(["Things to fix", "Application", "Advanced details"]);
        cut.Find("[data-testid=ae-readiness-manual]").TextContent.Should().Contain("Ready");
        cut.Find("[data-testid=ae-readiness-automated]").TextContent.Should().Contain("Blocked").And.Contain("redirect URI");
        cut.Find("[data-testid=ae-fix-count]").TextContent.Should().Be("3 things need attention");
        foreach (var toggle in new[] { "ae-toggle-application", "ae-toggle-browser", "ae-toggle-automated" }) cut.Find($"[data-testid={toggle}]").Click();
        State(cut, "environment").Should().Be("Suggested", "a test-pack default is a suggestion, never 'configured'");
        State(cut, "form-data-type").Should().Be("Detected from source", "source evidence is not deployed runtime");
        State(cut, "browser-redirect").Should().Be("Observed");
        State(cut, "client-secret").Should().Be("Available for this session");
        cut.Find("[data-testid=ae-field][data-key=client-secret] .pft-value").TextContent.Should().Be("—", "a secret value is never shown");
        cut.Find("[data-testid=ae-browser-note]").TextContent.Should().Contain("never configures BirkNext unattended execution");
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
        cut.FindAll("[data-testid=ae-proposals] [data-testid=ae-proposal]").Select(item => item.GetAttribute("data-key")).Should().Equal("environment", "form-data-type");

        cut.Find("[data-testid=ae-proposals] [data-testid=ae-proposal][data-key=environment] input").Change(false);
        cut.Find("[data-testid=ae-apply]").Click();
        cut.WaitForAssertion(() => _api.Saved.Should().ContainSingle());
        _api.Saved[0].FormDataType.Should().Be("bufdir_barnevern_tiltak_skolenaervaer_v2_0_0_altinn");
        _api.Saved[0].Environment.Should().BeNull("an unselected proposal is never saved");
    }

    [Fact]
    public void Session_secret_is_a_password_field_sent_once_and_never_shown()
    {
        var cut = Render<AltinnEnvironmentPane>(p => p.Add(c => c.EnvironmentId, Env));
        cut.WaitForAssertion(() => cut.Find("[data-testid=ae-secret-reveal]"));
        cut.FindAll("[data-testid=ae-secret]").Should().BeEmpty("the secret input is hidden until requested");
        cut.Find("[data-testid=ae-secret-reveal]").Click();
        var input = cut.Find("[data-testid=ae-secret]");
        input.GetAttribute("type").Should().Be("password");
        input.Change(Secret);
        cut.Find("[data-testid=ae-add-secret]").Click();

        cut.WaitForAssertion(() => _api.Secrets.Should().Equal(Secret));
        cut.FindAll("[data-testid=ae-secret]").Should().BeEmpty("the form closes after sending and keeps nothing");
        cut.Markup.Should().NotContain(Secret);
        JSInterop.Invocations.Should().NotContain(call => call.Arguments.Any(argument => argument != null && argument.ToString()!.Contains(Secret)), "never written to browser storage");
    }

    [Fact]
    public void Skolenaervaer_consumes_the_shared_setup_with_manual_and_automated_readiness()
    {
        var cut = Render<AltinnEnvironmentStatus>(p => p.Add(c => c.EnvironmentId, Env));
        cut.WaitForAssertion(() => cut.Find("[data-testid=aes-manual]"));
        cut.Find("[data-testid=aes-environment]").TextContent.Should().Contain("Ready").And.Contain("Source: Target Environment → Connected Systems → Altinn");
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

    // ── Compact tester view ──

    private const string IntegrationRef = "15550bccc6b34a9e8118a99dc4e1fd82:httpapi:skolen-rv-r-altinn-report-intake";

    /// <summary>The Skolenærvær shape: app ready, unattended client empty, no identities, no trust, contract partial, public metadata read.</summary>
    internal static AltinnEnvironmentView Skolenaervaer() => new()
    {
        EnvironmentId = Env, Detected = true,
        Fields =
        [
            F("environment", "Application", "Altinn environment", "Test (TT02)", AltinnSettingState.Configured, AltinnSettingSource.TargetEnvironment),
            F("app", "Application", "App identifier", "buf/skolenaervaer", AltinnSettingState.Configured, AltinnSettingSource.TargetEnvironment),
            F("form-data-type", "Application", "Form data type", null, AltinnSettingState.Missing, AltinnSettingSource.None),
            F("content-type", "Application", "Content type", null, AltinnSettingState.Missing, AltinnSettingSource.None, AltinnRequirement.Automated),
            F("integration", "Application", "Integration", IntegrationRef, AltinnSettingState.Detected, AltinnSettingSource.IntegrationBinding) with
            {
                DisplayValue = "Skolenærvær – Altinn report intake",
                Proposal = new(IntegrationRef, AltinnSettingState.Detected, AltinnSettingSource.IntegrationBinding, "Integration Quality Review") { DisplayValue = "Skolenærvær – Altinn report intake" },
            },
            F("idporten-token", "Browser authentication", "Token endpoint", "https://test.idporten.no/token", AltinnSettingState.Detected, AltinnSettingSource.PublicMetadata, AltinnRequirement.Optional),
            F("idporten-pkce", "Browser authentication", "Supported PKCE methods", "S256", AltinnSettingState.Detected, AltinnSettingSource.PublicMetadata, AltinnRequirement.Optional),
            F("client-id", "Automated execution", "Client ID", null, AltinnSettingState.Missing, AltinnSettingSource.None, AltinnRequirement.Automated),
            F("redirect-uri", "Automated execution", "Registered redirect URI", null, AltinnSettingState.Missing, AltinnSettingSource.None, AltinnRequirement.Automated),
            F("scopes", "Automated execution", "Scopes", null, AltinnSettingState.Missing, AltinnSettingSource.None, AltinnRequirement.Automated),
            F("enabled", "Automated execution", "Automated execution", "Not enabled", AltinnSettingState.Missing, AltinnSettingSource.BackendConfiguration, AltinnRequirement.Automated),
            F("client-secret", "Automated execution", "Client secret", null, AltinnSettingState.Missing, AltinnSettingSource.None, AltinnRequirement.Automated),
            F("trusted-environment", "Execution safety", "Trusted execution environment", null, AltinnSettingState.Missing, AltinnSettingSource.BackendConfiguration, AltinnRequirement.Automated),
            F("app-approval", "Execution safety", "App approved for testing", "buf/skolenaervaer", AltinnSettingState.Configured, AltinnSettingSource.BackendConfiguration, AltinnRequirement.Automated),
        ],
        Summary =
        [
            new("app", "App", AltinnReadinessState.Ready, "Test (TT02) · buf/skolenaervaer"),
            new("authentication", "Authentication", AltinnReadinessState.Blocked, "Unattended client incomplete."),
            new("identities", "Test identities", AltinnReadinessState.Blocked, "Reporter missing · child missing"),
            new("runtime", "Runtime", AltinnReadinessState.NotAssessed, "Not verified."),
            new("automated", "Automated execution", AltinnReadinessState.Blocked, "Blocked"),
        ],
        ManualReadiness = new("manual", "Manual test readiness", AltinnReadinessState.Partial, "Form data type: missing · Reporter: missing · Synthetic child: missing"),
        ManualMissing = ["Form data type", "Reporter", "Child"],
        AutomatedReadiness = new("automated", "Automated BirkNext execution", AltinnReadinessState.Blocked, "Blocked by 12 requirements"),
        AutomatedBlockers = [.. Enumerable.Range(1, 12).Select(i => $"Blocker {i}")],
        Contract = new("contract", "Report contract", AltinnReadinessState.Partial, "Public TT02 metadata · missing: form schema"),
    };

    private IRenderedComponent<AltinnEnvironmentPane> RenderSkolenaervaer()
    {
        _api.Next = Skolenaervaer();
        var cut = Render<AltinnEnvironmentPane>(p => p.Add(c => c.EnvironmentId, Env));
        cut.WaitForAssertion(() => cut.Find("[data-testid=ae-summary]"));
        return cut;
    }

    [Fact]
    public void Default_view_is_summary_things_to_fix_and_detected_values_with_technical_detail_collapsed()
    {
        var cut = RenderSkolenaervaer();

        cut.FindAll("[data-testid=ae-card]").Select(card => card.GetAttribute("data-key")).Should().Equal("app", "authentication", "identities", "runtime", "automated");
        cut.FindAll("[data-testid=ae-card-reason]").Select(reason => reason.TextContent).Should().Equal("5 automated settings missing", "Reporter and child missing", "12 requirements");
        cut.Find("[data-key=runtime]").TextContent.Should().Contain("Not verified");
        cut.Find("[data-testid=ae-readiness-manual]").TextContent.Should().Contain("Missing: Form data type, Reporter, Child");
        cut.Find("[data-testid=ae-readiness-automated]").TextContent.Should().Contain("12 requirements unresolved").And.NotContain("Blocker 1");
        cut.FindAll("[data-testid=ae-blockers]").Should().BeEmpty();

        cut.Find("[data-testid=ae-fix-count]").TextContent.Should().Be("11 things need attention");
        cut.FindAll("[data-testid=ae-fix-group]").Select(group => group.GetAttribute("data-key")).Should().Equal("application", "automated", "safety", "identities", "contract");
        cut.Find("[data-testid=ae-fix-group][data-key=identities]").TextContent.Should().Contain("Owner: Test Data").And.Contain("Manage test identities");
        cut.Find("[data-testid=ae-fix-group][data-key=identities] [data-testid=ae-fix-action]").GetAttribute("href").Should().Be("/test-data");

        cut.Find("[data-testid=ae-proposals] [data-key=integration] [data-testid=ae-proposal-value]").TextContent.Should().Be("Skolenærvær – Altinn report intake");
        cut.Find("[data-testid=ae-proposals]").TextContent.Should().Contain("Source: Integration Quality Review");
        cut.Markup.Should().NotContain(IntegrationRef, "the internal id is only in advanced details");
        cut.Markup.Should().NotContain("https://test.idporten.no/token", "public endpoints are collapsed");
        cut.Find("[data-testid=ae-section-browser]").TextContent.Should().Contain("Detected from public metadata · PKCE S256");
        cut.FindAll("[data-testid=ae-field-table]").Should().BeEmpty("every technical table is collapsed by default");
        cut.FindAll("[data-testid=ae-secret]").Should().BeEmpty();
        cut.FindAll("[data-testid=ae-observe-form]").Should().BeEmpty();
        cut.FindAll("[data-testid=ae-advanced-body]").Should().BeEmpty();
        foreach (var toggle in cut.FindAll("button[aria-controls]")) toggle.GetAttribute("aria-expanded").Should().Be("false");
    }

    [Fact]
    public void Every_summary_drills_down_to_its_evidence()
    {
        var cut = RenderSkolenaervaer();

        cut.Find("[data-testid=ae-blockers-toggle]").Click();
        cut.FindAll("[data-testid=ae-blockers] li").Should().HaveCount(12);
        cut.Find("[data-testid=ae-toggle-browser]").Click();
        cut.Find("[data-testid=ae-toggle-browser]").GetAttribute("aria-expanded").Should().Be("true");
        cut.Markup.Should().Contain("https://test.idporten.no/token");
        cut.Find("[data-testid=ae-fix-group][data-key=automated] [data-testid=ae-fix-action]").Click();
        cut.Find("[data-testid=ae-client-form]").TextContent.Should().Contain("Never the browser login client");
        cut.Find("[data-testid=ae-toggle-runtime]").Click();
        cut.Find("[data-testid=ae-observe-form]");
        cut.Find("[data-testid=ae-advanced-toggle]").Click();
        cut.Find("[data-testid=ae-evidence-field][data-key=integration] [data-testid=ae-raw-reference]").TextContent.Should().Contain(IntegrationRef);
        cut.Find("[data-testid=ae-section-application] [data-testid=ae-section-line]").TextContent.Should().Be("TT02 · buf/skolenaervaer · 2 settings missing");
    }

    [Fact]
    public void Secret_reveal_can_be_cancelled_without_sending_anything()
    {
        var cut = RenderSkolenaervaer();
        cut.Find("[data-testid=ae-secret-state]").TextContent.Should().Be("Missing");
        cut.Find("[data-testid=ae-secret-reveal]").Click();
        cut.Find("[data-testid=ae-secret]").Change(Secret);
        cut.Find("[data-testid=ae-secret-cancel]").Click();

        cut.FindAll("[data-testid=ae-secret]").Should().BeEmpty();
        _api.Secrets.Should().BeEmpty();
        cut.Find("[data-testid=ae-secret-reveal]").Click();
        cut.Find("[data-testid=ae-secret]").GetAttribute("value").Should().BeNullOrEmpty("a cancelled value is not kept");
    }

    [Fact]
    public void Things_to_fix_separates_manual_from_unattended_only_items()
    {
        var view = Skolenaervaer();
        var groups = AltinnEnvironmentPresentation.ThingsToFix(view);

        groups.Single(group => group.Key == "application").Items.Select(item => item.Label).Should().Equal("Form data type", "Content type");
        groups.Single(group => group.Key == "automated").Items.Select(item => item.Label).Should().Equal("Client ID", "Redirect URI", "Scopes", "Enabled", "Client secret");
        groups.Single(group => group.Key == "application").Kind.Should().Be(AltinnEnvironmentPresentation.FixAction.Application, "Detect already ran");
        AltinnEnvironmentPresentation.ThingsToFix(view with { Detected = false }).First().Action.Should().Be("Detect settings");
        AltinnEnvironmentPresentation.ManualLine(view).Should().NotContain("Client").And.NotContain("secret").And.NotContain("Trusted");
        AltinnEnvironmentPresentation.CardReason(view.Summary.Single(item => item.Key == "runtime"), view).Should().BeEmpty("runtime Not verified is its own state, not a configuration gap");
    }

    [Fact]
    public void Suggested_form_and_content_type_are_not_applied_things_to_fix_and_apply_saves_exactly_them()
    {
        const string Pack = "Test Pack reference · OpenAPI";
        var suggestion = (string value) => new AltinnSettingCandidate(value, AltinnSettingState.Declared, AltinnSettingSource.TestPack, Pack);
        var view = Skolenaervaer() with
        {
            Detected = false,
            Summary = [.. Skolenaervaer().Summary.Select(item => item.Key == "app" ? item with { State = AltinnReadinessState.Partial } : item)],
            Fields = [.. Skolenaervaer().Fields.Select(field => field.Key switch
            {
                "form-data-type" => field with { Value = "bufdir_barnevern_tiltak_skolenaervaer_v2_0_0_altinn", State = AltinnSettingState.Declared, SourceLabel = Pack, Proposal = suggestion("bufdir_barnevern_tiltak_skolenaervaer_v2_0_0_altinn") },
                "content-type" => field with { Value = "application/xml", State = AltinnSettingState.Declared, SourceLabel = Pack, Proposal = suggestion("application/xml") },
                _ => field,
            })],
        };
        _api.Next = view;
        var cut = Render<AltinnEnvironmentPane>(p => p.Add(c => c.EnvironmentId, Env));
        cut.WaitForAssertion(() => cut.Find("[data-testid=ae-fix]"));

        cut.FindAll("[data-testid=ae-fix-group][data-key=application] [data-testid=ae-fix-item]").Select(item => item.TextContent.Trim()).Should()
            .Equal($"Form data type · Not applied · Suggested by {Pack}", $"Content type · Not applied · Suggested by {Pack}");
        cut.Find("[data-testid=ae-card][data-key=app] [data-testid=ae-card-reason]").TextContent.Should().Be("2 not applied");
        _api.Saved.Should().BeEmpty("nothing is applied silently");

        cut.Find("[data-testid=ae-fix-group][data-key=application] [data-testid=ae-fix-action]").Click();

        cut.WaitForAssertion(() => _api.Saved.Should().ContainSingle());
        _api.Saved[0].Should().Match<AltinnEnvironmentSettingsUpdate>(update => update.FormDataType == "bufdir_barnevern_tiltak_skolenaervaer_v2_0_0_altinn"
            && update.ContentType == "application/xml" && update.IntegrationId == null && update.Environment == null);
    }

    // ── Detected settings: preselected ≠ applied, Apply selected (N), conflicts never overwritten ──────────────────────

    private static string ApplyText(IRenderedComponent<AltinnEnvironmentPane> cut) => cut.Find("[data-testid=ae-apply]").TextContent.Trim();
    private static bool ApplyDisabled(IRenderedComponent<AltinnEnvironmentPane> cut) => cut.Find("[data-testid=ae-apply]").HasAttribute("disabled");
    private static AngleSharp.Dom.IElement Box(IRenderedComponent<AltinnEnvironmentPane> cut, string key) => cut.Find($"[data-testid=ae-proposal][data-key={key}] input");

    [Fact]
    public void Detect_preselects_safe_values_and_Apply_selected_counts_them_while_saving_nothing()
    {
        var cut = Render<AltinnEnvironmentPane>(p => p.Add(c => c.EnvironmentId, Env));
        cut.WaitForAssertion(() => cut.Find("[data-testid=ae-detect]"));
        cut.Find("[data-testid=ae-detect]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=ae-apply]"));

        _api.Saved.Should().BeEmpty("Detect calculates candidates and saves nothing");
        Box(cut, "environment").HasAttribute("checked").Should().BeTrue();
        Box(cut, "form-data-type").HasAttribute("checked").Should().BeTrue();
        ApplyText(cut).Should().Be("Apply selected (2)");
        cut.Find("[data-testid=ae-proposal][data-key=environment]").TextContent.Should().Contain("Not applied", "preselected is not applied");

        Box(cut, "environment").Change(false);
        ApplyText(cut).Should().Be("Apply selected (1)");
        Box(cut, "form-data-type").Change(false);
        ApplyText(cut).Should().Be("Apply selected (0)");
        ApplyDisabled(cut).Should().BeTrue("nothing selected, nothing to apply");
        Box(cut, "form-data-type").Change(true);
        ApplyText(cut).Should().Be("Apply selected (1)");
        ApplyDisabled(cut).Should().BeFalse();
        _api.Saved.Should().BeEmpty("selecting is session state only");

        cut.Find("[data-testid=ae-apply]").Click();
        cut.WaitForAssertion(() => _api.Saved.Should().ContainSingle());
        _api.Saved[0].FormDataType.Should().Be("bufdir_barnevern_tiltak_skolenaervaer_v2_0_0_altinn");
        _api.Saved[0].Environment.Should().BeNull("only the selected value is persisted");
        cut.Find("[data-testid=ae-notice]").TextContent.Should().Be("Applied 1 setting to this Target Environment.");
    }

    [Fact]
    public void A_detected_value_that_differs_from_the_configured_one_is_a_conflict_never_preselected_or_applied()
    {
        var view = View(detected: true);
        _api.DetectResult = view with
        {
            Fields = [.. view.Fields.Select(field => field.Key == "app"
                ? field with { State = AltinnSettingState.Conflict, Proposal = new("buf/other-app", AltinnSettingState.Detected, AltinnSettingSource.SourceAnalysis, "Source Analysis") }
                : field)],
        };
        var cut = Render<AltinnEnvironmentPane>(p => p.Add(c => c.EnvironmentId, Env));
        cut.WaitForAssertion(() => cut.Find("[data-testid=ae-detect]"));
        cut.Find("[data-testid=ae-detect]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=ae-conflict][data-key=app]"));

        cut.FindAll("[data-testid=ae-proposal][data-key=app]").Should().BeEmpty("a conflict has no checkbox");
        cut.Find("[data-testid=ae-conflict][data-key=app] [data-testid=ae-conflict-configured]").TextContent.Should().Be("buf/skolenaervaer");
        cut.Find("[data-testid=ae-conflict][data-key=app] [data-testid=ae-conflict-detected]").TextContent.Should().Be("buf/other-app");
        cut.Find("[data-testid=ae-conflict][data-key=app]").TextContent.Should().Contain("Source: Source Analysis");
        ApplyText(cut).Should().Be("Apply selected (2)", "the conflict is not counted");

        cut.Find("[data-testid=ae-apply]").Click();
        cut.WaitForAssertion(() => _api.Saved.Should().ContainSingle());
        _api.Saved[0].AppIdentifier.Should().BeNull("Apply selected never overwrites a configured value");
    }

    private sealed class FakeApi : IAltinnEnvironmentApiService
    {
        public AltinnEnvironmentView? Next { get; set; }
        public List<string> Calls { get; } = [];
        public List<AltinnEnvironmentSettingsUpdate> Saved { get; } = [];
        public List<string> Secrets { get; } = [];
        public List<AltinnSessionIdentityRequest> Identities { get; } = [];

        private static ActiveEventApiResult<AltinnEnvironmentView> Ok(AltinnEnvironmentView view) => new(view, null, HttpStatusCode.OK);
        public Task<ActiveEventApiResult<AltinnEnvironmentView>> GetAsync(string environmentId, CancellationToken ct = default) { Calls.Add("get"); return Task.FromResult(Ok(Next ?? View())); }
        public AltinnEnvironmentView? DetectResult { get; set; }
        public Task<ActiveEventApiResult<AltinnEnvironmentView>> DetectAsync(string environmentId, CancellationToken ct = default) { Calls.Add("detect"); return Task.FromResult(Ok(DetectResult ?? View(detected: true))); }
        public Task<ActiveEventApiResult<AltinnEnvironmentView>> SaveAsync(string environmentId, AltinnEnvironmentSettingsUpdate update, CancellationToken ct = default) { Saved.Add(update); return Task.FromResult(Ok(View())); }
        public Task<ActiveEventApiResult<AltinnEnvironmentView>> RecordObservationAsync(string environmentId, AltinnRuntimeObservationRequest request, CancellationToken ct = default) => Task.FromResult(Ok(View()));
        public Task<ActiveEventApiResult<AltinnEnvironmentView>> RecordBrowserObservationAsync(string environmentId, AltinnBrowserObservationRequest request, CancellationToken ct = default) => Task.FromResult(Ok(View()));
        public Task<ActiveEventApiResult<bool>> AddSessionSecretAsync(string environmentId, string secret, CancellationToken ct = default) { Secrets.Add(secret); return Task.FromResult(new ActiveEventApiResult<bool>(true, null)); }
        public Task<ActiveEventApiResult<bool>> ClearSessionAsync(string environmentId, CancellationToken ct = default) => Task.FromResult(new ActiveEventApiResult<bool>(true, null));
        public Task<ActiveEventApiResult<bool>> AddSessionIdentityAsync(AltinnSessionIdentityRequest request, CancellationToken ct = default) { Identities.Add(request); return Task.FromResult(new ActiveEventApiResult<bool>(true, null)); }
        public Task<ActiveEventApiResult<bool>> ClearSessionIdentitiesAsync(CancellationToken ct = default) => Task.FromResult(new ActiveEventApiResult<bool>(true, null));
    }
}
