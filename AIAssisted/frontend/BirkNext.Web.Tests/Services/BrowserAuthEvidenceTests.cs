using BirkNext.Integrations;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Browser Discovery evidence → reporter browser-login evidence for one exact application. Login flow, authenticated return and session are
/// separate levels from deterministic rules; generic traffic never counts; another application's login never counts; provider-level evidence
/// is not app-level evidence; nothing reaches the other hops.
/// </summary>
public sealed class BrowserAuthEvidenceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 29, 8, 0, 0, TimeSpan.Zero);
    private const string Altinn = "https://buf.apps.tt02.altinn.no", IdPorten = "https://login.test.idporten.no", M2lb = "https://m2lbdev.bufetat.no";
    private static readonly BrowserAuthAppScope Skolenaervaer = new(Altinn, "/buf/skolenaervaer");

    private static PageAnalysis Page(string origin, string path, int minute, int? lastMinute = null, params ObservedNetworkEndpoint[] endpoints) => new()
    {
        PageOrigin = origin, PagePath = path, FirstObservedAt = T0.AddMinutes(minute), LastObservedAt = T0.AddMinutes(lastMinute ?? minute), Endpoints = [.. endpoints],
    };

    private static ObservedNetworkEndpoint Call(string host, string path, int minute, int status = 200, bool bearer = false, ObservedTrafficCategory category = ObservedTrafficCategory.Rest,
        string? pageOrigin = null, string? pagePath = null) => new()
    {
        Host = host, Path = path, Method = "GET", LastStatus = status, AuthObserved = bearer, Category = category,
        FirstObservedAt = T0.AddMinutes(minute), LastObservedAt = T0.AddMinutes(minute), PageOrigin = pageOrigin, PagePath = pagePath,
    };

    private static BrowserAuthEvidenceResult Classify(params PageAnalysis[] pages) =>
        BrowserAuthEvidence.Classify(new EndpointDiscoverySnapshot { Pages = [.. pages] }, Skolenaervaer, AltinnAccessPresentation.Providers, AltinnAccessPresentation.SessionPaths, T0.AddMinutes(30));

    private static PageAnalysis[] SkolenaervaerLogin() =>
    [
        Page(Altinn, "/buf/skolenaervaer", 0, 3,
            Call("buf.apps.tt02.altinn.no", "/buf/skolenaervaer/api/v1/profile/user", 3, pageOrigin: Altinn, pagePath: "/buf/skolenaervaer")),
        Page(IdPorten, "/authorize/selector", 1),
        Page(IdPorten, "/testid/login", 2),
    ];

    [Fact]
    public void ExactApp_LoginFlow_Return_AndSession_AreObserved_FromBrowserDiscovery()
    {
        var result = Classify(SkolenaervaerLogin());

        result.Level.Should().Be(BrowserAuthEvidenceLevel.AuthenticatedSessionObserved);
        result.Reached(BrowserAuthEvidenceLevel.LoginFlowObserved).Should().BeTrue();
        result.Reached(BrowserAuthEvidenceLevel.AuthenticatedReturnObserved).Should().BeTrue();
        result.Provider.Should().Be("ID-porten TEST");
        result.Usable.Should().BeTrue("no second manual recording is needed");
        result.Steps.Should().HaveCount(3).And.Contain(s => s.Contains("ID-porten TEST"));
        BrowserAuthEvidence.Label(result).Should().Be("Authenticated session observed");
    }

    [Fact]
    public void M2lbLogin_NeverSatisfiesSkolenaervaer_ItIsOnlyProviderLevelEvidence()
    {
        var result = Classify(Page(M2lb, "/", 0, 3, Call("m2lbdev.bufetat.no", "/api/me", 3, bearer: true, pageOrigin: M2lb, pagePath: "/")), Page(IdPorten, "/authorize", 1));

        result.Level.Should().Be(BrowserAuthEvidenceLevel.Unknown);
        result.ProviderObservedElsewhere.Should().BeTrue("ID-porten was seen, but not as a login of this app");
        result.Usable.Should().BeFalse();
    }

    [Fact]
    public void ReturnToAnotherApplication_IsOnlyALoginFlow_NotAnAuthenticatedReturn()
    {
        var result = Classify(Page(Altinn, "/buf/skolenaervaer", 0), Page(IdPorten, "/authorize", 1), Page(M2lb, "/", 2));
        result.Level.Should().Be(BrowserAuthEvidenceLevel.LoginFlowObserved);
        result.Usable.Should().BeFalse();
    }

    [Fact]
    public void AnotherAltinnApp_OnTheSameOrigin_IsNotThisApp()
    {
        var other = new BrowserAuthAppScope(Altinn, "/buf/otherapp");
        BrowserAuthEvidence.Classify(new EndpointDiscoverySnapshot { Pages = [.. SkolenaervaerLogin()] }, other, AltinnAccessPresentation.Providers,
            AltinnAccessPresentation.SessionPaths, T0.AddMinutes(30)).Level.Should().Be(BrowserAuthEvidenceLevel.Unknown);
        Skolenaervaer.Contains(Altinn, "/buf/skolenaervaer-other").Should().BeFalse("a path prefix must end at a segment boundary");
    }

    [Fact]
    public void GenericBrowserTraffic_DomStaticAndApiCalls_AreNotAuthentication()
    {
        var page = Page(Altinn, "/buf/skolenaervaer", 0, 5,
            Call("buf.apps.tt02.altinn.no", "/buf/skolenaervaer/appsettings.json", 1, category: ObservedTrafficCategory.StaticAsset, pageOrigin: Altinn, pagePath: "/buf/skolenaervaer"),
            Call("buf.apps.tt02.altinn.no", "/buf/skolenaervaer/api/v1/applicationmetadata", 2, pageOrigin: Altinn, pagePath: "/buf/skolenaervaer"),
            Call("buf.apps.tt02.altinn.no", "/buf/skolenaervaer/api/v1/profile/user", 3, pageOrigin: Altinn, pagePath: "/buf/skolenaervaer"));
        page.BrowserEvidence = new BirkNext.BrowserCompanion.BrowserPageEvidence { PageOrigin = Altinn, PagePath = "/buf/skolenaervaer" };
        var result = Classify(page, Page(Altinn, "/buf/skolenaervaer/admin", 4));

        result.Level.Should().Be(BrowserAuthEvidenceLevel.Unknown, "no login flow to an identity provider was observed; a session marker alone proves nothing");
        result.ProviderObservedElsewhere.Should().BeFalse();
    }

    [Fact]
    public void AuthenticationLost_AfterTheSession_IsLoggedOut_AndOldEvidenceIsHistorical()
    {
        var lost = Classify([.. SkolenaervaerLogin(), Page(Altinn, "/buf/skolenaervaer/x", 5, 6,
            Call("buf.apps.tt02.altinn.no", "/buf/skolenaervaer/api/v1/data", 6, status: 401, pageOrigin: Altinn, pagePath: "/buf/skolenaervaer/x"))]);
        lost.Level.Should().Be(BrowserAuthEvidenceLevel.LoggedOut);

        var old = BrowserAuthEvidence.Classify(new EndpointDiscoverySnapshot { Pages = [.. SkolenaervaerLogin()] }, Skolenaervaer, AltinnAccessPresentation.Providers,
            AltinnAccessPresentation.SessionPaths, T0.AddDays(3));
        old.Level.Should().Be(BrowserAuthEvidenceLevel.Historical);
        old.ObservedLevel.Should().Be(BrowserAuthEvidenceLevel.AuthenticatedSessionObserved);
        BrowserAuthEvidence.Label(old).Should().Be("Historical · Authenticated session observed");
    }

    private static AltinnEnvironmentView View(string? baseUrl = Altinn) => new()
    {
        EnvironmentId = "dev",
        Fields =
        [
            new AltinnSettingField { Key = "app", Value = "buf/skolenaervaer", State = AltinnSettingState.Configured },
            new AltinnSettingField { Key = "app-base-url", Value = baseUrl, State = AltinnSettingState.Configured },
        ],
        Interactions =
        [
            new() { Key = AltinnHops.ReporterApp, Title = "Reporter access", From = "Reporter", To = "Altinn app", Provider = "ID-porten TEST / TestID", Runtime = "Not observed",
                Requirement = InteractionAuthRules.Resolve([new(InteractionAuthRequirement.BrowserOidc, InteractionAuthSource.TestPackProposal, "pack")]) },
            new() { Key = AltinnHops.AppIntegration, Title = "Integration authentication", From = "Altinn", To = "Meldingsutveksleren (MU)", Runtime = "Not verified", Configuration = "Not configured in Integration Catalog (x)" },
            new() { Key = AltinnHops.Execution, Title = "BirkNext execution credentials", From = "BirkNext", To = "Altinn app", Configuration = "Not configured" },
        ],
        Summary = [new("reporter-access", "Reporter access", AltinnReadinessState.Partial, "Browser/OIDC · ID-porten TEST / TestID · Runtime: Not observed")],
    };

    [Fact]
    public void AltinnScope_IsTheExactApp_AndTheReporterCardBecomesReady_OnlyFromThisAppsEvidence()
    {
        AltinnAccessPresentation.Scope(View())!.Display.Should().Be("https://buf.apps.tt02.altinn.no/buf/skolenaervaer");
        AltinnAccessPresentation.Scope(View(null)).Should().BeNull();

        var view = View();
        var evidence = AltinnAccessPresentation.Evidence(view, new EndpointDiscoverySnapshot { Pages = [.. SkolenaervaerLogin()] }, T0.AddMinutes(30));
        var card = AltinnAccessPresentation.ReporterCard(view.Summary[0], view, evidence);
        card.State.Should().Be(AltinnReadinessState.Ready);
        card.Reason.Should().Contain("Authenticated session observed · Browser Discovery");

        var m2lbOnly = AltinnAccessPresentation.Evidence(view, new EndpointDiscoverySnapshot { Pages = [Page(M2lb, "/", 0, 2), Page(IdPorten, "/authorize", 1)] }, T0.AddMinutes(30));
        AltinnAccessPresentation.ReporterCard(view.Summary[0], view, m2lbOnly).State.Should().Be(AltinnReadinessState.Partial);
        AltinnAccessPresentation.ReporterRuntime(view.Interactions[0], m2lbOnly).Should().Contain("another application; it does not count");
        AltinnAccessPresentation.Line(view, evidence).Should().Contain("Altinn → Meldingsutveksleren (MU): Unknown", "browser evidence never changes the integration hop");
    }
}
