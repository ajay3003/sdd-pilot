using BirkNext.LocalHttpsProxy;
using BirkNext.ManagedEdge;
using BirkNext.SourceDomains;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Component = BirkNext.Web.Components.FrontendAnalysisSettings;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Target Environment forms show the actual value source declares for the environment's OWN kind instead of an example: Development values for a
/// Development target, QA values for QA, nothing borrowed across environments or from Local/Default, nothing for an unknown environment. A saved
/// value always wins; a prefilled value is only the edit draft until Save; provenance and "Runtime not verified" are always visible. Generic hosts.
/// </summary>
public sealed class TargetEnvironmentSourceValueTests : BunitContext
{
    private const string DevOrigin = "https://app-dev.example.test";
    private const string QaOrigin = "https://app-qa.example.test";
    private const string DevAuthority = "https://login.example.test/0b8f3c1e-1111-4a2b-9c3d-123456789abc";
    private readonly FrontendAnalysisSettingsService _settings = new();
    private readonly Mock<IIntegrationCatalogApiService> _catalog = new();
    private List<EnvironmentValueCandidate> _candidates;

    private static EnvironmentValueCandidate Candidate(string key, string value, string env, string component = "Gateway.Api", string file = "appsettings.{0}.json") => new()
    {
        SemanticKey = key, Value = value, Environment = SourceEnvironments.Normalize(env), Component = component, SnapshotId = Guid.NewGuid(), ArchiveFileName = "app.zip",
        File = $"{component}/{string.Format(file, env)}", ConfigurationKey = key == EnvironmentValueKeys.FrontendOrigin ? "Cors:AllowedOrigins:0" : "AzureAd:Authority",
        Basis = "CORS allowed origin", EnvironmentBasis = "file-name suffix", Line = 4,
    };

    public TargetEnvironmentSourceValueTests()
    {
        _candidates =
        [
            Candidate(EnvironmentValueKeys.FrontendOrigin, DevOrigin, "Development"),
            Candidate(EnvironmentValueKeys.FrontendOrigin, QaOrigin, "Qa"),
            Candidate(EnvironmentValueKeys.FrontendOrigin, "https://app-local.example.test", "Local"),
            Candidate(EnvironmentValueKeys.FrontendOrigin, "https://app-default.example.test", "", file: "appsettings.json"),
            Candidate(EnvironmentValueKeys.AuthenticationAuthority, DevAuthority, "Development", "Web"),
        ];
        _catalog.Setup(c => c.EnvironmentValuesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => new SourceEnvironmentValuesView { Candidates = _candidates });
        Services.AddSingleton<IFrontendAnalysisSettingsService>(_settings);
        Services.AddSingleton(_catalog.Object);
        Services.AddSingleton(new Mock<ITargetEnvironmentDetectionApiService>().Object);
        Services.AddSingleton(new ManagedEdgeRuntime(new Mock<IManagedEdgeCdpApiService>().Object));
        Services.AddSingleton(new LocalHttpsProxyRuntime(new Mock<ILocalHttpsProxyApiService>().Object));
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
        JSInterop.SetupVoid("birkNextStorage.setItem", _ => true).SetVoidResult();
        JSInterop.SetupVoid("birkNextStorage.setDiscovery", _ => true).SetVoidResult();
        JSInterop.Setup<string?>("birkNextStorage.getDiscovery").SetResult(null);
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult("""
            {"activeProfileId":"dev","profiles":[
              {"id":"dev","name":"App DEV","environmentType":"Development"},
              {"id":"qa","name":"App QA","environmentType":"QA"},
              {"id":"saved","name":"App DEV saved","environmentType":"Development","targetUrl":"https://custom-dev.example.test/"},
              {"id":"sandbox","name":"Sandbox","environmentType":"Custom"},
              {"id":"test","name":"App TEST","environmentType":"Test"}
            ]}
            """);
    }

    private IRenderedComponent<Component> Open(string profileName)
    {
        var cut = Render<Component>();
        cut.WaitForAssertion(() => cut.FindAll(".fa-profile-chip").Should().NotBeEmpty());
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains(profileName) && (profileName != "App DEV" || !b.TextContent.Contains("saved"))).Click();
        TargetTab(cut);
        return cut;
    }

    private static void TargetTab(IRenderedComponent<Component> cut) => cut.FindAll("[role=tab]").Single(t => t.TextContent.Trim() == "Target Application").Click();

    private static void Edit(IRenderedComponent<Component> cut) => cut.FindAll("button").First(b => b.TextContent.Trim() == "Edit Environment").Click();

    private static string Value(IRenderedComponent<Component> cut, string testId) => cut.Find($"[data-testid={testId}]").GetAttribute("value") ?? "";

    private FrontendAnalysisProfile Saved(string id) => _settings.Settings.Profiles.Single(p => p.Id == id);

    [Fact]
    public void Development_ShowsTheDevelopmentValue_AndEditPrefillsTheDraftOnly()
    {
        var cut = Open("App DEV");
        cut.Find("[data-testid=fa-view-target-url-source-line]").TextContent.Should().Be($"Source value for Development: {DevOrigin} · Runtime not verified");
        Edit(cut);
        Value(cut, "fa-edit-target-url").Should().Be(DevOrigin);
        cut.Find("[data-testid=fa-edit-target-url]").HasAttribute("placeholder").Should().BeFalse("an example is shown only when no actual value exists");
        cut.Find("[data-testid=fa-target-url-source]").GetAttribute("data-prefilled").Should().Be("true");
        cut.Find("[data-testid=fa-target-url-source-line]").TextContent.Should().Be("Source-derived · Development · not saved until you save · Runtime not verified");
        cut.Find("[data-testid=fa-target-url-source-source]").TextContent.Should().Contain("Gateway.Api · appsettings.Development.json (Cors:AllowedOrigins:0)").And.Contain("app.zip");
        Saved("dev").TargetUrl.Should().BeNull("opening the form never persists a source value");
    }

    [Fact]
    public void Qa_GetsItsOwnValue_AndSwitchingEnvironmentsNeverBleeds()
    {
        var cut = Open("App QA");
        Edit(cut);
        Value(cut, "fa-edit-target-url").Should().Be(QaOrigin);
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Cancel").Click();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("App DEV") && !b.TextContent.Contains("saved")).Click();
        TargetTab(cut);
        Edit(cut);
        Value(cut, "fa-edit-target-url").Should().Be(DevOrigin);
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Cancel").Click();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("App QA")).Click();
        TargetTab(cut);
        Edit(cut);
        Value(cut, "fa-edit-target-url").Should().Be(QaOrigin);
    }

    [Fact]
    public void QaWithoutAQaValue_StaysEmpty_WithGuidance_NoDevelopmentFallback()
    {
        _candidates.RemoveAll(c => c.Environment.Kind == SourceEnvironmentKind.QA);
        var cut = Open("App QA");
        Edit(cut);
        Value(cut, "fa-edit-target-url").Should().BeEmpty();
        cut.Find("[data-testid=fa-edit-target-url]").GetAttribute("placeholder").Should().Be("https://myapp.example.com");
        cut.Find("[data-testid=fa-target-url-source-line]").TextContent.Should().Be("No QA value found in the imported source configuration.");
        cut.Markup.Should().NotContain(DevOrigin);
    }

    [Theory]
    [InlineData("Sandbox")]
    [InlineData("App TEST")]
    public void AnUnknownOrUnmatchedEnvironment_BorrowsNothing_NotLocalNotDefault(string name)
    {
        var cut = Open(name);
        Edit(cut);
        Value(cut, "fa-edit-target-url").Should().BeEmpty();
        cut.Markup.Should().NotContain("app-local.example.test").And.NotContain("app-default.example.test").And.NotContain(DevOrigin);
    }

    [Fact]
    public void ASavedValue_IsKept_AndTheDifferenceIsShown()
    {
        var cut = Open("App DEV saved");
        cut.Find("[data-testid=fa-view-target-url-source-line]").TextContent.Should().Be(
            $"Configured · differs from source: Development declares {DevOrigin}. The configured value is kept.");
        Edit(cut);
        Value(cut, "fa-edit-target-url").Should().Be("https://custom-dev.example.test/");
        cut.Find("[data-testid=fa-target-url-source-use]").Click();
        Value(cut, "fa-edit-target-url").Should().Be(DevOrigin, "Use fills the draft on request");
        Saved("saved").TargetUrl.Should().Be("https://custom-dev.example.test/", "nothing is saved until Save");
    }

    [Fact]
    public void ConflictingValues_FillNothing_AndListTheCandidates()
    {
        _candidates.Add(Candidate(EnvironmentValueKeys.FrontendOrigin, "https://app-qa2.example.test", "Qa", "Admin.Api"));
        var cut = Open("App QA");
        Edit(cut);
        Value(cut, "fa-edit-target-url").Should().BeEmpty();
        cut.Find("[data-testid=fa-target-url-source]").GetAttribute("data-state").Should().Be("ReviewRequired");
        cut.Find("[data-testid=fa-target-url-source-line]").TextContent.Should().StartWith("Review required: source declares 2 different values for QA. None is filled in.");
        cut.FindAll("[data-testid=fa-target-url-source-candidate]").Should().HaveCount(2);
    }

    [Fact]
    public void SavingThePrefilledDraft_PersistsIt()
    {
        var cut = Open("App DEV");
        Edit(cut);
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Save changes").Click();
        cut.WaitForAssertion(() => Saved("dev").TargetUrl.Should().Be(DevOrigin));
    }

    [Fact]
    public void AddEnvironment_FollowsTheChosenType_ButNeverOverwritesATypedUrl()
    {
        var cut = Render<Component>();
        cut.WaitForAssertion(() => cut.FindAll(".fa-profile-chip").Should().NotBeEmpty());
        cut.FindAll("button").First(b => b.TextContent.Contains("Add Environment")).Click();
        cut.Find("[data-testid=fa-create-type]").Change(FrontendEnvironmentType.QA.ToString());
        Value(cut, "fa-create-url").Should().Be(QaOrigin);
        cut.Find("[data-testid=fa-create-url-source-line]").TextContent.Should().StartWith("Source-derived · QA");
        cut.Find("[data-testid=fa-create-type]").Change(FrontendEnvironmentType.Development.ToString());
        Value(cut, "fa-create-url").Should().Be(DevOrigin);
        cut.Find("[data-testid=fa-create-type]").Change(FrontendEnvironmentType.Custom.ToString());
        Value(cut, "fa-create-url").Should().BeEmpty("an unknown environment gets nothing");
        cut.Find("[data-testid=fa-create-url]").Input("https://typed.example.test/");
        cut.Find("[data-testid=fa-create-type]").Change(FrontendEnvironmentType.QA.ToString());
        Value(cut, "fa-create-url").Should().Be("https://typed.example.test/");
    }

    [Fact]
    public void Authentication_ExpectedAuthorityIsPrefilledForItsEnvironment_TenantStaysEmptyWithoutSource()
    {
        var cut = Open("App DEV");
        Edit(cut);
        cut.FindAll("[role=tab]").Single(t => t.TextContent.Trim() == "Authentication").Click();
        cut.Find("#configured-authority").GetAttribute("value").Should().Be(DevAuthority);
        cut.Find("[data-testid=fa-authority-source-line]").TextContent.Should().StartWith("Source-derived · Development");
        cut.Find("#configured-tenant").GetAttribute("value").Should().BeNullOrEmpty();
    }
}
