using BirkNext.Integrations;
using BirkNext.SecurityExpectations;
using BirkNext.SourceArchitecture;
using BirkNext.SourceEvidence;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Security Configuration Review (Target Environments → Security Expectations): assessment first, logical values second, evidence last; every
/// approval is an explicit action. The assessment itself is covered by the backend SecurityConfigurationReviewTests; these tests cover how
/// the panel presents and acts on it.
/// </summary>
public sealed class SecurityExpectationsPanelTests : BunitContext
{
    private readonly FakeApi _api = new();
    private readonly FrontendSecuritySettings _settings = new() { AllowedRestHosts = ["manual.example.test"] };
    private readonly FrontendAnalysisSettingsService _profiles = new();
    private ApprovedSecurityExpectations? _applied;

    public SecurityExpectationsPanelTests()
    {
        Services.AddSingleton<ISecurityExpectationApi>(_api);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    /// <summary>The panel reads the Target Environment (name, type, URL) from the settings service.</summary>
    private void Environment(FrontendEnvironmentType type, string id = "qa")
    {
        _profiles.Settings.Profiles.Add(new FrontendAnalysisProfile { Id = id, Name = type == FrontendEnvironmentType.QA ? "Shop QA" : $"Shop {type}", EnvironmentType = type, TargetUrl = "https://shop.example.test" });
        Services.AddSingleton<IFrontendAnalysisSettingsService>(_profiles);
    }

    private IRenderedComponent<SecurityExpectationsPanel> Panel(bool editable = false) => Render<SecurityExpectationsPanel>(p => p
        .Add(c => c.EnvironmentId, "qa").Add(c => c.Settings, _settings).Add(c => c.Editable, editable)
        .Add(c => c.ApprovedChanged, a =>
        {
            _applied = a; _settings.ExpectedAuthority = a.ExpectedAuthority; _settings.ExpectedTenant = a.ExpectedTenant; _settings.ExpectedClientId = a.ExpectedClientId;
            _settings.AllowedRestHosts = a.AllowedRestHosts; _settings.AllowedGraphQlHosts = a.AllowedGraphQlHosts; _settings.AllowedCdnHosts = a.AllowedCdnHosts;
            _settings.AllowedRedirectUrls = a.AllowedRedirectUrls; _settings.ScopedClientIds = a.ScopedClientIds; _settings.Origins = a.Origins;
        }));

    private static void Discover(IRenderedComponent<SecurityExpectationsPanel> cut)
    {
        cut.Find("[data-testid=sec-discover]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=sec-status]").TextContent.Should().Contain("0 approved automatically"));
    }

    private static AngleSharp.Dom.IElement Card(IRenderedComponent<SecurityExpectationsPanel> cut, SecurityExpectationField field) => cut.Find($"[data-testid=scr-check-{field}]");

    private static void ShowAll(IRenderedComponent<SecurityExpectationsPanel> cut) => cut.Find("[data-testid=scr-filter-all]").Click();

    [Fact]
    public void WithoutSource_ShowsAUsefulEmptyState_NotAnEmptyTable()
    {
        _api.Sources = [];
        var cut = Panel();
        cut.Find("h3").TextContent.Should().Be("Security Configuration Review");
        cut.Markup.Should().Contain("Compare source-declared security configuration with approved expectations for the selected environment.")
            .And.Contain("Nothing is approved or validated automatically").And.Contain("Never enter secrets");
        cut.Find("[data-testid=scr-empty]").TextContent.Should().Contain("No source security configuration evidence is available").And.Contain("Open Source Analysis").And.Contain("Choose source snapshot");
        cut.FindAll("table, [data-testid^=scr-check-]").Should().BeEmpty();
        cut.Find("[data-testid=sec-approved-count]").TextContent.Should().Be("6");
    }

    [Fact]
    public void Review_LeadsWithAssessment_ThenValues_EvidenceCollapsed()
    {
        Environment(FrontendEnvironmentType.QA);
        var cut = Panel();
        Discover(cut);

        cut.Find("[data-testid=scr-environment]").TextContent.Should().Be("Shop QA (QA)");
        cut.Find("[data-testid=scr-summary]").TextContent.Should().Contain("Needs review").And.Contain("Placeholders / defaults").And.Contain("Environment mismatches");
        cut.Markup.Should().NotContain("evidence items", "raw evidence counts are not a headline KPI");
        cut.Find("[data-testid=scr-attention]").TextContent.Should().Contain("2 items need attention").And.Contain("GraphQL hosts")
            .And.Contain("Approved manual.example.test is not declared for QA", "a manual approval without a source declaration is surfaced as stale, not removed");
        cut.Find("[data-testid=scr-filter-attention]").GetAttribute("aria-pressed").Should().Be("true", "the default filter is Needs attention");
        cut.FindAll("[data-testid^=scr-check-]").Should().HaveCount(2);

        var toggle = Card(cut, SecurityExpectationField.GraphQlHost).QuerySelector("[data-testid=scr-evidence-toggle]")!;
        toggle.TextContent.Should().Be("Evidence (1)");
        toggle.GetAttribute("aria-expanded").Should().Be("false");
        cut.Find($"#{toggle.GetAttribute("aria-controls")}").HasAttribute("hidden").Should().BeTrue();
        cut.Markup.Should().NotContain("GraphQl:Endpoint", "evidence is not rendered until expanded");

        toggle.Click();
        cut.Find("[data-testid=scr-evidence-toggle]").GetAttribute("aria-expanded").Should().Be("true");
        cut.Find("[data-testid=scr-evidence]").TextContent.Should().Contain("Shop.Ui").And.Contain("Shop.Ui/appsettings.json").And.Contain("GraphQl:Endpoint").And.Contain("applies (shared)");
    }

    [Fact]
    public void ConsistentValues_AreSummarized_NotShownAsProblems()
    {
        Environment(FrontendEnvironmentType.QA);
        _settings.AllowedGraphQlHosts = ["graphql.example.test"];
        _settings.AllowedRestHosts = [];
        var cut = Panel();
        Discover(cut);
        cut.Find("[data-testid=scr-attention]").TextContent.Should().Contain("Nothing needs attention");
        cut.Find("[data-testid=scr-filter-all]").GetAttribute("aria-pressed").Should().Be("true");
        var card = Card(cut, SecurityExpectationField.GraphQlHost);
        card.QuerySelector("[data-testid=scr-badge]")!.TextContent.Should().Contain("Consistent");
        card.TextContent.Should().Contain("GraphQL hosts assessment: Consistent.");
        card.QuerySelectorAll("[data-testid=sec-accept]").Should().BeEmpty();
        cut.Find("[data-testid=scr-count-consistent]").TextContent.Should().Be("1");
    }

    [Fact]
    public void MultiValue_AddToApprovedValues_PreservesManualValuesAndRecordsProvenance()
    {
        Environment(FrontendEnvironmentType.QA);
        var cut = Panel();
        Discover(cut);
        var add = Card(cut, SecurityExpectationField.GraphQlHost).QuerySelector("[data-testid=sec-accept]")!;
        add.TextContent.Should().Be("Add to approved values");
        add.Click();
        cut.WaitForAssertion(() => _applied.Should().NotBeNull());
        _settings.AllowedGraphQlHosts.Should().Equal("graphql.example.test");
        _settings.AllowedRestHosts.Should().Equal("manual.example.test");
        _settings.Origins.Single().Origin.Should().Be(SecurityExpectationOrigin.AcceptedFromSource);
        cut.Find("[data-testid=sec-status]").TextContent.Should().Contain("saved to approved expectations");
        JSInterop.Invocations.Should().Contain(i => i.Identifier == "Blazor._internal.domWrapper.focus", "focus returns to the card heading (without scrolling)");
        ShowAll(cut);
        Card(cut, SecurityExpectationField.GraphQlHost).QuerySelector("[data-testid=scr-badge]")!.TextContent.Should().Contain("Consistent");
    }

    [Fact]
    public void SingleValue_ApproveThisValue_ThenConflictNeedsExplicitReplace_AndIgnoreKeepsApproval()
    {
        Environment(FrontendEnvironmentType.QA);
        _api.Candidates = [_api.Make("auth", SecurityExpectationField.Authority, "https://identity.example.test/a", key: "Auth:Authority")];
        var cut = Panel();
        Discover(cut);
        Card(cut, SecurityExpectationField.Authority).QuerySelector("[data-testid=sec-accept]")!.TextContent.Should().Be("Approve this value");

        _settings.ExpectedAuthority = "https://identity.example.test/existing";
        Discover(cut);
        var card = Card(cut, SecurityExpectationField.Authority);
        card.QuerySelector("[data-testid=scr-badge]")!.TextContent.Should().Contain("Conflict");
        card.TextContent.Should().Contain("the approved value is https://identity.example.test/existing");
        card.QuerySelector("[data-testid=sec-reject]")!.Click();
        cut.WaitForAssertion(() => _api.Reviews.Should().ContainSingle());
        _api.Reviews.Single().Accept.Should().BeFalse();
        _settings.ExpectedAuthority.Should().Be("https://identity.example.test/existing");
        _applied.Should().BeNull();

        Discover(cut);
        ShowAll(cut);
        var replace = Card(cut, SecurityExpectationField.Authority).QuerySelector("[data-testid=sec-accept]")!;
        replace.TextContent.Should().Be("Replace approved value");
        replace.Click();
        cut.WaitForAssertion(() => _settings.ExpectedAuthority.Should().Be("https://identity.example.test/a"));
        _api.Reviews.Last().Request.Replace.Should().BeTrue();
    }

    [Fact]
    public void ClientIds_AreGroupedByComponent_AndApprovedPerComponent()
    {
        Environment(FrontendEnvironmentType.QA);
        _api.Candidates =
        [
            _api.Make("web", SecurityExpectationField.ClientId, "22222222-2222-2222-2222-222222222222", component: "Shop.Ui", key: "AzureAd:ClientId"),
            _api.Make("api", SecurityExpectationField.ClientId, "33333333-3333-3333-3333-333333333333", component: "Shop.Api", file: "Shop.Api/appsettings.json", key: "AzureAd:ClientId"),
        ];
        var cut = Panel();
        Discover(cut);
        var card = Card(cut, SecurityExpectationField.ClientId);
        card.QuerySelectorAll("h6").Select(h => h.TextContent).Should().Equal("Shop.Api · AzureAd", "Shop.Ui · AzureAd");
        card.TextContent.Should().NotContain("Conflict", "different components may use different client registrations");
        cut.Find("[data-testid=scr-attention-ClientId]").TextContent.Should().Contain("2 component scopes need review");
        var buttons = card.QuerySelectorAll("[data-testid=sec-accept]");
        buttons.Select(b => b.TextContent).Should().AllBe("Approve for this component");

        buttons[0].Click();
        cut.WaitForAssertion(() => _api.Reviews.Should().ContainSingle());
        _api.Reviews.Single().Request.Scope.Should().Be("Shop.Api · AzureAd");
        _settings.ScopedClientIds.Should().ContainSingle(s => s.Scope == "Shop.Api · AzureAd" && s.Value == "33333333-3333-3333-3333-333333333333");
        _settings.ExpectedClientId.Should().BeNull("a component approval never writes the project-wide value");
    }

    [Fact]
    public void PlaceholderValue_IsFlagged_AndCannotBeApproved()
    {
        Environment(FrontendEnvironmentType.Local);
        _api.Candidates = [_api.Make("zero", SecurityExpectationField.TenantId, "00000000-0000-0000-0000-000000000000", file: "Shop.Ui/appsettings.Local.json", key: "AzureAd:TenantId")];
        var cut = Panel();
        Discover(cut);
        var card = Card(cut, SecurityExpectationField.TenantId);
        card.QuerySelector("[data-testid=scr-finding]")!.GetAttribute("data-type").Should().Be(nameof(SecurityConfigurationFindingType.PlaceholderValue));
        card.QuerySelector("[data-testid=scr-group]")!.GetAttribute("data-status").Should().Be("Placeholder");
        card.QuerySelectorAll("[data-testid=sec-accept]").Should().BeEmpty();
        cut.Find("[data-testid=scr-count-placeholder]").TextContent.Should().Be("1");
    }

    [Fact]
    public void LoopbackRedirect_ForANonLocalEnvironment_IsAnEnvironmentMismatch()
    {
        Environment(FrontendEnvironmentType.Development);
        _api.Candidates = [_api.Make("cb", SecurityExpectationField.RedirectUrl, "http://localhost:7284/authentication/login-callback", file: "Shop.Ui/appsettings.Development.json", key: "AzureAd:RedirectUri")];
        var cut = Panel();
        Discover(cut);
        Card(cut, SecurityExpectationField.RedirectUrl).QuerySelector("[data-testid=scr-finding]")!.GetAttribute("data-type").Should().Be(nameof(SecurityConfigurationFindingType.EnvironmentMismatch));
        cut.Find("[data-testid=scr-count-mismatch]").TextContent.Should().Be("1");
    }

    [Fact]
    public void StaleApproval_IsVisible_AndKept()
    {
        Environment(FrontendEnvironmentType.QA);
        _settings.ExpectedTenant = "11111111-1111-1111-1111-111111111111";
        var cut = Panel();
        Discover(cut);
        var card = Card(cut, SecurityExpectationField.TenantId);
        card.QuerySelector("[data-testid=scr-finding]")!.GetAttribute("data-type").Should().Be(nameof(SecurityConfigurationFindingType.StaleApprovedValue));
        card.TextContent.Should().Contain("The approval is kept").And.Contain("11111111-1111-1111-1111-111111111111");
        _settings.ExpectedTenant.Should().Be("11111111-1111-1111-1111-111111111111");
    }

    [Fact]
    public void StaleDiscovery_IsNotPresentedAsCurrent_AndCannotBeApproved()
    {
        Environment(FrontendEnvironmentType.QA);
        _api.ResultCurrent = false;
        _settings.AllowedGraphQlHosts = ["legacy.example.test"];
        var cut = Panel();
        Discover(cut);
        cut.FindAll("[data-testid^=scr-check-], [data-testid=sec-accept]").Should().BeEmpty();
        cut.Find("[data-testid=scr-empty]").TextContent.Should().Contain("not current");
        _settings.AllowedGraphQlHosts.Should().Equal("legacy.example.test");
    }

    [Fact]
    public void ServerRefusal_PreservesApprovedConfiguration()
    {
        Environment(FrontendEnvironmentType.QA);
        var cut = Panel();
        Discover(cut);
        _api.ReviewError = "Source evidence changed; refresh discovery.";
        cut.Find("[data-testid=sec-accept]").Click();
        cut.WaitForAssertion(() => cut.Find("[role=alert]").TextContent.Should().Be(_api.ReviewError));
        _applied.Should().BeNull();
        _settings.AllowedGraphQlHosts.Should().BeEmpty();
    }

    [Fact]
    public void UnsafeValues_AreRedacted_AndCannotBeApproved()
    {
        Environment(FrontendEnvironmentType.QA);
        _api.Candidates = [_api.Make("unsafe", SecurityExpectationField.RedirectUrl, "https://example.test/?access_token=DO-NOT-RENDER") with { NormalizedValue = "unsafe", Explanation = "password=DO-NOT-RENDER" }];
        var cut = Panel();
        Discover(cut);
        ShowAll(cut);
        cut.Markup.Should().NotContain("DO-NOT-RENDER");
        cut.FindAll("[data-testid=sec-accept]").Should().BeEmpty();
    }

    [Fact]
    public void EditMode_KeepsLabelledManualFields_AndOffersNoApproval()
    {
        var cut = Panel(true);
        foreach (var input in cut.FindAll("input,textarea")) cut.Find($"label[for='{input.Id}']").TextContent.Should().NotBeEmpty();
        cut.FindAll("[data-testid=sec-accept], [data-testid^=scr-check-]").Should().BeEmpty();
        cut.Find("#sec-field-ClientId-input").Change("manual-new");
        _settings.ExpectedClientId.Should().Be("manual-new");
        _api.Reviews.Should().BeEmpty();
    }

    [Fact]
    public async Task ManualChangesDuringPendingApproval_AreNeverOverwritten()
    {
        Environment(FrontendEnvironmentType.QA);
        _api.ReviewGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var cut = Panel();
        Discover(cut);
        var pending = cut.Find("[data-testid=sec-accept]").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        cut.WaitForAssertion(() => _api.Reviews.Should().ContainSingle());
        _settings.AllowedRestHosts.Add("new-manual.example.test");
        cut.Render();
        _api.ReviewGate.SetResult();
        await pending;
        _applied.Should().BeNull();
        _settings.AllowedRestHosts.Should().Contain("new-manual.example.test");
        _settings.AllowedGraphQlHosts.Should().BeEmpty();
        cut.Find("[role=alert]").TextContent.Should().Contain("current expectations were preserved");
    }

    [Fact]
    public void Filters_ShowEveryCheck_AndAttentionItemsJumpToTheirCard()
    {
        Environment(FrontendEnvironmentType.QA);
        var cut = Panel();
        Discover(cut);
        cut.Find("[data-testid=scr-filter-notassessed]").Click();
        cut.Find("[data-testid=scr-filter-notassessed]").GetAttribute("aria-pressed").Should().Be("true");
        cut.FindAll("[data-testid^=scr-check-]").Should().HaveCount(7, "checks without declarations to compare, and headers, are Not assessed");
        cut.Find("[data-testid=scr-filter-all]").Click();
        cut.FindAll("[data-testid^=scr-check-]").Should().HaveCount(SecurityConfigurationReviewEngine.ReviewedFields.Length);

        cut.Find("[data-testid=scr-filter-attention]").Click();
        cut.Find("[data-testid=scr-attention-GraphQlHost]").Click();
        cut.WaitForAssertion(() => JSInterop.Invocations.Should().Contain(i => i.Identifier == "Blazor._internal.domWrapper.focus"));
        cut.Find("[data-testid=scr-filter-all]").GetAttribute("aria-pressed").Should().Be("true", "the target card is always visible after the jump");
    }

    [Fact]
    public void LargeProject_RendersLogicalValuesCompactly_WithoutEvidence()
    {
        Environment(FrontendEnvironmentType.QA);
        _api.Candidates = Enumerable.Range(0, 60).Select(i => _api.Make($"h{i}", SecurityExpectationField.RestHost, $"https://h{i:00}.example.test", component: $"Component{i % 50}", key: "ApiBaseUrl")).ToList();
        var cut = Panel();
        Discover(cut);
        var card = Card(cut, SecurityExpectationField.RestHost);
        card.QuerySelectorAll("[data-testid=scr-group]").Should().HaveCount(8);
        card.TextContent.Should().Contain("Show 52 more values");
        card.QuerySelectorAll("[data-testid=scr-finding][data-type=UnapprovedValue]").Should().ContainSingle("60 unapproved values are one finding line, not 60")
            .Which.GetAttribute("data-count").Should().Be("60");
        cut.FindAll("[data-testid=scr-evidence] li").Should().BeEmpty();
        cut.Find("[data-testid=scr-attention-RestHost]").TextContent.Should().Contain("60 values not yet approved");
    }

    [Fact]
    public void Accessibility_DisclosuresReferenceTheirRegions_AndStatusIsText()
    {
        Environment(FrontendEnvironmentType.QA);
        var cut = Panel();
        Discover(cut);
        ShowAll(cut);
        foreach (var toggle in cut.FindAll("[aria-controls]"))
            cut.FindAll($"#{toggle.GetAttribute("aria-controls")}").Should().ContainSingle("aria-controls points to an existing region");
        foreach (var badge in cut.FindAll("[data-testid=scr-badge]"))
            badge.TextContent.Trim().Length.Should().BeGreaterThan(2, "state is conveyed by text, not color alone");
        cut.FindAll("[data-testid^=scr-check-] h5").Should().HaveCount(SecurityConfigurationReviewEngine.ReviewedFields.Length);
        cut.Markup.Should().Contain("Tenant assessment: Not assessed.");
    }

    [Fact]
    public async Task NewerSnapshot_IsOffered_NeverSwitchedTo()
    {
        await _api.DiscoverAsync("qa", new(_api.Snapshot.Id, new()));
        _api.AdditionalSources = [_api.Snapshot with { Id = Guid.NewGuid(), Archive = new("newer.zip", new string('b', 64), 2), AnalyzedAt = _api.Snapshot.AnalyzedAt.AddMinutes(1) }];
        var cut = Panel();
        cut.Markup.Should().Contain("Newer source snapshot available");
        cut.Find("[data-testid=sec-snapshot]").TextContent.Should().Contain("generic.zip").And.NotContain("newer.zip");
        cut.Find("[data-testid=sec-primary]").GetAttribute("value").Should().Be(_api.Snapshot.Id.ToString(), "the newer snapshot is offered, never switched to");
        cut.Find("[data-testid=sec-newer-snapshot]").TextContent.Should().Contain("not switched automatically");
        _api.DiscoverCalls.Should().Be(1);
    }

    [Fact]
    public void TargetEnvironment_PassesTheActualProfileId_AndPersistsOnlyExplicitApproval()
    {
        Services.AddSingleton<IFrontendAnalysisSettingsService>(new FrontendAnalysisSettingsService());
        Services.AddSingleton(Moq.Mock.Of<ITargetEnvironmentDetectionApiService>());
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult("""{"activeProfileId":"actual-qa-id","profiles":[{"id":"actual-qa-id","name":"QA","environmentType":2}]}""");
        JSInterop.SetupVoid("birkNextStorage.setItem", _ => true).SetVoidResult();
        var cut = Render<BirkNext.Web.Components.FrontendAnalysisSettings>(p => p.Add(c => c.InitialTab, "security"));
        var panel = cut.FindComponent<SecurityExpectationsPanel>();
        panel.Instance.EnvironmentId.Should().Be("actual-qa-id");
        var settings = Services.GetRequiredService<IFrontendAnalysisSettingsService>();
        cut.Find("[data-testid=sec-discover]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=sec-status]").TextContent.Should().Contain("0 approved automatically"));
        cut.Find("[data-testid=scr-environment]").TextContent.Should().Be("QA (QA)");
        settings.Settings.Profiles.Single().Security.AllowedGraphQlHosts.Should().BeEmpty();
        cut.Find("[data-testid=sec-accept]").Click();
        cut.WaitForAssertion(() => settings.Settings.Profiles.Single().Security.AllowedGraphQlHosts.Should().Equal("graphql.example.test"));
        settings.Settings.Profiles.Single().Security.Origins.Should().Contain(p => p.Origin == SecurityExpectationOrigin.AcceptedFromSource);
        JSInterop.Invocations.Should().Contain(i => i.Identifier == "birkNextStorage.setItem");
    }

    [Fact]
    public async Task SwitchingTargetDuringPendingApproval_CannotApplyToAnotherProfile()
    {
        Services.AddSingleton<IFrontendAnalysisSettingsService>(new FrontendAnalysisSettingsService());
        Services.AddSingleton(Moq.Mock.Of<ITargetEnvironmentDetectionApiService>());
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult("""{"activeProfileId":"actual-qa-id","profiles":[{"id":"actual-qa-id","name":"QA","environmentType":2},{"id":"other-id","name":"Other","environmentType":2}]}""");
        JSInterop.SetupVoid("birkNextStorage.setItem", _ => true).SetVoidResult();
        _api.ReviewGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var cut = Render<BirkNext.Web.Components.FrontendAnalysisSettings>(p => p.Add(c => c.InitialTab, "security"));
        cut.Find("[data-testid=sec-discover]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=sec-status]").TextContent.Should().Contain("0 approved automatically"));
        var pending = cut.Find("[data-testid=sec-accept]").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        cut.WaitForAssertion(() => _api.Reviews.Should().ContainSingle());
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("Other")).Click();
        _api.ReviewGate.SetResult();
        await pending;
        Services.GetRequiredService<IFrontendAnalysisSettingsService>().Settings.Profiles.Should().OnlyContain(p => p.Security.AllowedGraphQlHosts.Count == 0);
    }

    [Fact]
    public void SourceApprovalMetadata_DoesNotChangeTheAuthenticationVerificationContext()
    {
        var profile = new FrontendAnalysisProfile { Id = "qa", TargetUrl = "https://shop.example.test", Security = _settings };
        var legacySecurity = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(profile.Security))!;
        legacySecurity.AsObject().Remove("origins");
        legacySecurity.AsObject().Remove("scopedClientIds");
        // Runtime security expectations (CORS, cookies, authorization scenarios) are not authentication configuration either.
        legacySecurity.AsObject().Remove("runtimeSecurity");
        var legacyContext = System.Text.Json.JsonSerializer.Serialize(new { profile.Id, profile.TargetUrl, profile.EnvironmentType, profile.Authentication,
            profile.RequestTimeoutSeconds, profile.RetryCount, Security = legacySecurity, profile.ExpectedApiGateway, profile.AllowedRestHosts, profile.AllowedGraphQlEndpoints });
        ManualAuthenticationVerificationEvidence.Fingerprint(profile).Should().Be(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(legacyContext))));
        var evidence = ManualAuthenticationVerificationEvidence.Record(profile, ManualAuthenticationVerificationStatus.Passed);
        _settings.Origins.Add(new(SecurityExpectationField.RestHost, "manual.example.test", SecurityExpectationOrigin.AcceptedFromSource, _api.Snapshot.Id));
        _settings.ScopedClientIds.Add(new("Shop.Ui · AzureAd", "22222222-2222-2222-2222-222222222222"));
        evidence.StatusFor(profile).Should().Be(ManualAuthenticationVerificationStatus.Passed);
        _settings.AllowedRestHosts.Add("different.example.test");
        evidence.StatusFor(profile).Should().Be(ManualAuthenticationVerificationStatus.Stale);
    }

    private sealed class FakeApi : ISecurityExpectationApi
    {
        public IqrSourceSnapshot Snapshot { get; } = new() { Id = Guid.NewGuid(), IntegrationId = "source-analysis", Archive = new("generic.zip", new string('a', 64), 2), AnalyzedAt = DateTimeOffset.UtcNow };
        public List<IqrSourceSnapshot> AdditionalSources { get; set; } = [];
        public List<IqrSourceSnapshot>? Sources { get; set; }
        public List<SecurityExpectationCandidate> Candidates { get; set; }
        public bool ResultCurrent { get; set; } = true;
        public int DiscoverCalls;
        public string? ReviewError;
        public TaskCompletionSource? ReviewGate;
        public List<(SecurityCandidateReviewRequest Request, bool Accept)> Reviews { get; } = [];
        private SecurityExpectationDiscoveryResult? _result;
        public FakeApi() => Candidates = [Make("gql", SecurityExpectationField.GraphQlHost, "graphql.example.test")];
        public SecurityExpectationCandidate Make(string id, SecurityExpectationField field, string value, string component = "Shop.Ui", string file = "Shop.Ui/appsettings.json", string key = "GraphQl:Endpoint") => new()
        {
            Id = id, FieldType = field, Value = value, NormalizedValue = SecurityExpectationValues.Normalize(field, value) ?? "", SourceSnapshotId = Snapshot.Id, SourceComponent = component,
            SourceFile = file, SourceSymbol = key, Explanation = "Explicit source configuration, no runtime observation.", EvidenceState = ArchitectureEvidenceState.Confirmed,
            SupportingEvidence = [new(Snapshot.Id, "generic.zip / " + component, file, 1, key, "Configuration", value, "Explicit source configuration", ArchitectureEvidenceState.Confirmed)],
        };
        private List<IqrSourceSnapshot> All => Sources ?? AdditionalSources.Concat([Snapshot]).ToList();
        public Task<IReadOnlyList<IqrSourceSnapshot>> SourcesAsync(string env) => Task.FromResult<IReadOnlyList<IqrSourceSnapshot>>(All);
        public Task<ReviewSourceOptions> SourceScopeAsync(string env, ReviewSourceScopeRequest? scope)
        {
            var options = new ReviewSourceOptions { Snapshots = All.Select(s => new ReviewSourceSnapshot { SnapshotId = s.Id, RepositoryKey = "generic", Repository = "Generic",
                IdentityBasis = "Archive file name", ArchiveName = s.Archive.FileName, Fingerprint = s.Archive.Sha256, AnalyzedAt = s.AnalyzedAt, SourceStatus = s.Status.ToString(), Latest = true }).ToList() };
            return Task.FromResult(BirkNext.Web.Tests.Pages.SourceScopeFixture.Resolve(options, scope, []));
        }
        public Task<IReadOnlyList<SecurityExpectationDiscoveryResult>> HistoryAsync(string env) => Task.FromResult<IReadOnlyList<SecurityExpectationDiscoveryResult>>(_result is null ? [] : [_result]);
        public Task<SecurityExpectationDiscoveryResult> DiscoverAsync(string env, SecurityDiscoveryRequest request)
        {
            DiscoverCalls++;
            _result = new() { Id = Guid.NewGuid(), TargetEnvironmentId = env, SourceSnapshotId = Snapshot.Id, SourceFingerprint = Snapshot.Archive.Sha256, IsCurrent = ResultCurrent, Status = ArchitectureStatus.Complete,
                Candidates = SecurityExpectationValues.Group(Candidates).Select(c => c with { IsCurrent = ResultCurrent }).ToList() };
            return Task.FromResult(_result);
        }
        public async Task<SecurityCandidateReviewResponse> ReviewAsync(string env, SecurityCandidateReviewRequest request, bool accept)
        {
            if (ReviewError is not null) throw new InvalidOperationException(ReviewError);
            Reviews.Add((request, accept));
            var candidate = _result!.Candidates.Single(c => c.Id == request.CandidateId);
            if (ReviewGate is not null) await ReviewGate.Task;
            var approved = !accept ? SecurityExpectationValues.Copy(request.Approved)
                : request.Scope is { } scope ? SecurityExpectationValues.AcceptScoped(request.Approved, candidate, scope, Snapshot.Archive.Sha256, DateTimeOffset.UtcNow)
                : SecurityExpectationValues.Accept(request.Approved, candidate, Snapshot.Archive.Sha256, request.Replace, DateTimeOffset.UtcNow);
            _result = _result with { Revision = _result.Revision + 1, Candidates = _result.Candidates.Select(c => c.Id == candidate.Id ? c with { CandidateState = accept ? SecurityCandidateState.Accepted : SecurityCandidateState.Rejected } : c).ToList() };
            return new SecurityCandidateReviewResponse(_result, approved);
        }
    }
}
