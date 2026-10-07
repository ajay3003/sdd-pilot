using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.CriticalE2E;
using BirkNext.Integrations;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// The false "backend not reachable" regression: the real API services run over a scripted HTTP handler, so a feature
/// endpoint can fail (400/403/404/500) while the backend — and every other endpoint — answers. Only a failure with no HTTP
/// response may say "unreachable"; a missing Target Environment is a configuration state that requests nothing.
/// </summary>
public sealed class BackendConnectivityPageTests : BunitContext
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>Answers by path ("POST api/critical-e2e/overview"); records every request so tests can see what was (not) asked.</summary>
    private sealed class ScriptedBackend : HttpMessageHandler
    {
        public Dictionary<string, Func<HttpResponseMessage>> Routes { get; } = new(StringComparer.Ordinal);
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = $"{request.Method} {request.RequestUri!.AbsolutePath.TrimStart('/')}";
            Requests.Add(key + request.RequestUri.Query);
            if (Routes.TryGetValue(key, out var respond)) return Task.FromResult(respond());
            throw new HttpRequestException("TypeError: Failed to fetch"); // no route: what a browser reports for a refused connection
        }
    }

    private static Func<HttpResponseMessage> Json(object body) => () => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body, options: Web) };
    private static Func<HttpResponseMessage> Status(HttpStatusCode status) => () => new HttpResponseMessage(status) { Content = new StringContent("") };

    private readonly ScriptedBackend _backend = new();
    private HttpClient Http => new(_backend) { BaseAddress = new Uri("http://localhost:5000/") };

    private void Context(string profileId)
    {
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(() => new FrontendAnalysisContext
        {
            ActiveProfile = new FrontendAnalysisProfile { Id = profileId, Name = profileId.Length == 0 ? "" : "M2LB QA", EnvironmentType = FrontendEnvironmentType.QA },
        });
        Services.AddSingleton(context.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    // ── Critical E2E ─────────────────────────────────────────────────────────────────────────────────────────────

    private static CriticalE2EOverview Overview() => new()
    {
        EnvironmentId = "qa", EnvironmentName = "M2LB QA",
        Release = new CriticalE2EReleaseStatus { Disposition = CriticalE2EReleaseDisposition.NotEvaluated, Summary = "No build is selected." },
        Attended = new CriticalE2EAttendedReadiness { Status = new CriticalE2EEngineStatus { State = CriticalE2EEngineState.RequiresBrowserSession, Message = "Browser Companion not connected." } },
        BrowserEngine = new CriticalE2EEngineStatus { State = CriticalE2EEngineState.RequiresBrowserSession },
        IntegrationEngine = new CriticalE2EEngineStatus { State = CriticalE2EEngineState.RequiresBrowserSession },
        ElementPick = new CriticalE2EEngineStatus { State = CriticalE2EEngineState.RequiresBrowserSession },
    };

    private IRenderedComponent<CriticalE2ERegression> CriticalE2E(string profileId = "qa")
    {
        Services.AddSingleton<ICriticalE2EApiService>(new CriticalE2EApiService(Http));
        Context(profileId);
        return Render<CriticalE2ERegression>();
    }

    [Fact]
    public void CriticalE2E_GeneralApiWorksWhileTheFlowsEndpointFails_TheBackendStaysReachableAndThePageUsable()
    {
        _backend.Routes["POST api/critical-e2e/overview"] = Json(Overview());
        _backend.Routes["GET api/critical-e2e/flows"] = Status(HttpStatusCode.InternalServerError);

        var cut = CriticalE2E();

        cut.WaitForAssertion(() => cut.Find("[data-testid=e2e-flows-error]"));
        // The overview (target, build, coverage, browser readiness) is still on screen: degraded, not unavailable.
        cut.Find("[data-testid=e2e-flows]").Should().NotBeNull();
        cut.FindAll("[data-testid=e2e-overview-error]").Should().BeEmpty();
        cut.Find("[data-testid=e2e-flows-error-connectivity]").TextContent.Should().Be("Backend reachable");
        cut.Find("[data-testid=e2e-flows-error-message]").TextContent.Should().Contain("Critical flow definitions").And.Contain("HTTP 500");
        cut.Markup.Should().NotContainEquivalentOf("not reachable").And.NotContainEquivalentOf("unreachable").And.NotContain("local BirkNext backend");
    }

    [Fact]
    public void CriticalE2E_CheckAgainRerunsOnlyTheFailedRequestAndRecovers()
    {
        _backend.Routes["POST api/critical-e2e/overview"] = Json(Overview());
        _backend.Routes["GET api/critical-e2e/flows"] = Status(HttpStatusCode.InternalServerError);
        var cut = CriticalE2E();
        cut.WaitForAssertion(() => cut.Find("[data-testid=e2e-flows-error-retry]"));
        var overviews = _backend.Requests.Count(r => r.StartsWith("POST api/critical-e2e/overview"));

        _backend.Routes["GET api/critical-e2e/flows"] = Json(new List<CriticalE2EFlowDefinition>());
        cut.Find("[data-testid=e2e-flows-error-retry]").Click();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=e2e-flows-error]").Should().BeEmpty());
        _backend.Requests.Count(r => r.StartsWith("POST api/critical-e2e/overview")).Should().Be(overviews, "only the failed request is retried");
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "endpoint was not found", "Endpoint not found")]
    [InlineData(HttpStatusCode.Forbidden, "access denied", "Access denied")]
    [InlineData(HttpStatusCode.Unauthorized, "authentication required", "Authentication required")]
    [InlineData(HttpStatusCode.InternalServerError, "HTTP 500", "Server error")]
    public void CriticalE2E_AnOverviewHttpErrorIsAnEndpointFailureNotAnUnreachableBackend(HttpStatusCode status, string phrase, string category)
    {
        _backend.Routes["POST api/critical-e2e/overview"] = Status(status);
        _backend.Routes["GET api/critical-e2e/flows"] = Json(new List<CriticalE2EFlowDefinition>());

        var cut = CriticalE2E();

        cut.WaitForAssertion(() => cut.Find("[data-testid=e2e-overview-error]"));
        cut.Find("[data-testid=e2e-overview-error]").GetAttribute("data-connectivity").Should().Be("Reachable");
        cut.Find("[data-testid=e2e-overview-error-connectivity]").TextContent.Should().Be("Backend reachable");
        cut.Find("[data-testid=e2e-overview-error-message]").TextContent.Should().Contain(phrase).And.Contain($"HTTP {(int)status}");
        cut.Find("[data-testid=e2e-overview-error]").TextContent.Should().Contain(category);
        cut.Markup.Should().NotContainEquivalentOf("unreachable").And.NotContainEquivalentOf("not reachable");
    }

    [Fact]
    public void CriticalE2E_AStoppedBackendIsTheOneCaseThatSaysUnreachable()
    {
        // No routes: every request fails before an HTTP response, as when the API is stopped.
        var cut = CriticalE2E();

        cut.WaitForAssertion(() => cut.Find("[data-testid=e2e-overview-error]"));
        cut.Find("[data-testid=e2e-overview-error-connectivity]").TextContent.Should().Be("Backend unreachable");
        cut.Find("[data-testid=e2e-overview-error]").ClassList.Should().Contain("brn-danger");
        cut.Find("[data-testid=e2e-overview-error-message]").TextContent.Should().NotContain("local");
    }

    [Fact]
    public void CriticalE2E_NoTargetEnvironmentIsAConfigurationStateAndNeverSendsAnEmptyEnvironmentId()
    {
        _backend.Routes["POST api/critical-e2e/overview"] = Json(Overview() with { EnvironmentId = "", EnvironmentName = "" });

        var cut = CriticalE2E(profileId: "");

        cut.WaitForAssertion(() => cut.Find("[data-testid=e2e-flows]"));
        _backend.Requests.Should().NotContain(r => r.StartsWith("GET api/critical-e2e/flows"), "an empty environment id is a 400, which used to read as 'backend not reachable'");
        cut.FindAll("[data-testid=e2e-overview-error], [data-testid=e2e-flows-error]").Should().BeEmpty();
    }

    // ── Security Classification ──────────────────────────────────────────────────────────────────────────────────

    private IRenderedComponent<SecurityClassificationReview> SecurityClassification(string profileId = "qa")
    {
        Services.AddSingleton<IClassificationReviewApiService>(new ClassificationReviewApiService(Http));
        Services.AddSingleton(new FeatureVisibilityService());
        Services.AddSingleton<IReportExportService, ReportExportService>();
        Context(profileId);
        return Render<SecurityClassificationReview>();
    }

    private void ScopeAnswers() =>
        _backend.Routes["GET api/security-classification/source-scope"] = Json(new ClassificationScopeOptions { Coverage = ClassificationSourceCoverage.Rows(null) });

    [Fact]
    public void SecurityClassification_GeneralApiWorksWhileTheReviewEndpointIs404_SourceEvidenceStillLoads()
    {
        _backend.Routes["GET api/security-classification"] = Status(HttpStatusCode.NotFound);
        ScopeAnswers();

        var cut = SecurityClassification();

        cut.WaitForAssertion(() => cut.Find("[data-testid=sc-overview-error]"));
        cut.Find("[data-testid=sc-overview-error-connectivity]").TextContent.Should().Be("Backend reachable");
        cut.Find("[data-testid=sc-overview-error-message]").TextContent.Should().Contain("Security Classification reviews").And.Contain("not found").And.Contain("HTTP 404");
        // The source scope is a separate request and is still read: the page is degraded, not unavailable.
        _backend.Requests.Should().Contain(r => r.StartsWith("GET api/security-classification/source-scope"));
        cut.Find("[data-testid=sc-source-evidence]").Should().NotBeNull();
        cut.Markup.Should().NotContainEquivalentOf("backend not reachable").And.NotContainEquivalentOf("unreachable");
    }

    [Fact]
    public void SecurityClassification_AServerErrorCanBeRetriedAndRecovers()
    {
        _backend.Routes["GET api/security-classification"] = Status(HttpStatusCode.InternalServerError);
        ScopeAnswers();
        var cut = SecurityClassification();
        cut.WaitForAssertion(() => cut.Find("[data-testid=sc-overview-error-retry]"));

        _backend.Routes["GET api/security-classification"] = Json(new ClassificationOverview());
        cut.Find("[data-testid=sc-overview-error-retry]").Click();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=sc-overview-error]").Should().BeEmpty());
    }

    [Fact]
    public void SecurityClassification_NoTargetEnvironmentRequestsNothingAndSaysSo()
    {
        var cut = SecurityClassification(profileId: "");

        cut.WaitForAssertion(() => cut.Find("[data-testid=sc-no-target]"));
        cut.Find("[data-testid=sc-no-target]").TextContent.Should().Contain("No Target Environment is selected");
        _backend.Requests.Should().BeEmpty("an empty environment id is a 400, which used to read as 'backend not reachable'");
        cut.Markup.Should().NotContainEquivalentOf("not reachable");
    }

    [Fact]
    public void SecurityClassification_NoSourceSnapshotIsANormalStateNotABackendError()
    {
        _backend.Routes["GET api/security-classification"] = Json(new ClassificationOverview());
        ScopeAnswers(); // answers with no snapshots

        var cut = SecurityClassification();

        cut.WaitForAssertion(() => cut.Find("[data-testid=sc-source-evidence]"));
        cut.FindAll("[data-testid=sc-overview-error], [data-testid=sc-context-error]").Should().BeEmpty();
        cut.Markup.Should().NotContainEquivalentOf("could not be loaded").And.NotContainEquivalentOf("unreachable");
    }
}
