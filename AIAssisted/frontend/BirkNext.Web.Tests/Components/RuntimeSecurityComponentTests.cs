using System.Net;
using BirkNext.ApiReview;
using BirkNext.LocalHttpsProxy;
using BirkNext.RuntimeSecurity;
using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Runtime security UI pieces: the Security Expectations editor (every field states its consumer), cookie metadata (never values,
/// proxy observation), the exports (sections present, nothing secret) and the sign-in hooks (no secret, token only on protected calls).
/// </summary>
public sealed class RuntimeSecurityComponentTests : BunitContext
{
    private const string CookieSentinel = "SENTINEL-COOKIE-VALUE-91c2";

    // ── Security Expectations editor ────────────────────────────────────────────────────────────────

    [Fact]
    public void Editor_EveryGroupStatesItsConsumer_EveryInputIsLabelled()
    {
        var settings = new RuntimeSecurityExpectations();
        var cut = Render<RuntimeSecurityExpectationsEditor>(p => p.Add(x => x.Settings, settings).Add(x => x.Editable, true));
        foreach (var usage in new[] { RuntimeSecuritySettingsUsage.ApiDocumentationExposure, RuntimeSecuritySettingsUsage.Cors, RuntimeSecuritySettingsUsage.Cookies, RuntimeSecuritySettingsUsage.AuthorizationScenarios, RuntimeSecuritySettingsUsage.BodyFuzzOperations })
            cut.Markup.Should().Contain("Used by: " + usage);
        cut.Find("[data-testid=rse-authz-add]").Click();
        cut.Find("[data-testid=rse-bodyfuzz-add]").Click();
        foreach (var input in cut.FindAll("input,textarea")) cut.Find($"label[for='{input.Id}']").TextContent.Should().NotBeEmpty();
        cut.Markup.Should().Contain("Never enter passwords, tokens or cookie values");
    }

    [Fact]
    public void Editor_EditsTheSettings_AddScenarioHasExplicitExpectations()
    {
        var settings = new RuntimeSecurityExpectations();
        var cut = Render<RuntimeSecurityExpectationsEditor>(p => p.Add(x => x.Settings, settings).Add(x => x.Editable, true));
        cut.Find("[data-testid=rse-docs-expectation]").Change("ExpectedProtected");
        cut.Find("[data-testid=rse-cors-origins]").Change("https://a.example.test\nhttps://b.example.test\n");
        cut.Find("[data-testid=rse-cors-credentials]").Change("false");
        cut.Find("[data-testid=rse-cookie-names]").Change("Session");
        cut.Find("[data-testid=rse-authz-add]").Click();
        settings.ApiDocumentationExposure.Should().Be(ApiDocumentationExposureExpectation.ExpectedProtected);
        settings.Cors.AllowedOrigins.Should().Equal("https://a.example.test", "https://b.example.test");
        settings.Cors.AllowCredentials.Should().BeFalse();
        settings.Cookies.AuthCookieNames.Should().Equal("Session");
        var scenario = settings.AuthorizationScenarios.Should().ContainSingle().Subject;
        scenario.Identities.Select(i => (i.Alias, i.Expected)).Should().Equal((AuthorizationIdentityAliases.ProxySession, AuthorizationExpectation.Allow), (AuthorizationIdentityAliases.Anonymous, AuthorizationExpectation.Deny));
        cut.Find("[data-testid=rse-authz-invalid]").TextContent.Should().Contain("absolute http(s) URL");
    }

    [Fact]
    public void Editor_ViewMode_IsASummary()
    {
        var settings = new RuntimeSecurityExpectations { BodyFuzzOperations = [new() { Path = "/a", Policy = BodyFuzzingPolicy.ReadOnlyBodySafe }, new() { Path = "/b", Policy = BodyFuzzingPolicy.StateChangingWithCleanup }] };
        var cut = Render<RuntimeSecurityExpectationsEditor>(p => p.Add(x => x.Settings, settings));
        cut.FindAll("input,textarea,select").Should().BeEmpty();
        cut.Find("[data-testid=rse-summary-docs]").TextContent.Should().Contain("Not specified (not probed)");
        cut.Find("[data-testid=rse-summary-bodyfuzz]").TextContent.Should().Contain("1 read-only").And.Contain("1 state-changing (blocked without cleanup)");
    }

    [Fact]
    public void ApprovedExpectationsCopy_KeepsRuntimeSecurity_AsAnIndependentCopy()
    {
        var approved = new BirkNext.SecurityExpectations.ApprovedSecurityExpectations();
        approved.RuntimeSecurity.Cors.AllowedOrigins.Add("https://a.example.test");
        var copy = BirkNext.SecurityExpectations.SecurityExpectationValues.Copy(approved);
        copy.RuntimeSecurity.Cors.AllowedOrigins.Should().Equal("https://a.example.test");
        copy.RuntimeSecurity.Cors.AllowedOrigins.Add("https://b.example.test");
        approved.RuntimeSecurity.Cors.AllowedOrigins.Should().HaveCount(1);
    }

    // ── Cookie section ─────────────────────────────────────────────────────────────────────────────

    private static CookieSecurityAssessment Assessment() => CookieSecurityEvaluator.Evaluate(
        [SetCookieMetadataParser.Parse($"Session={CookieSentinel}; Path=/; SameSite=None", "app.example.test", CookieEvidenceSource.FrontendDocumentResponse)!],
        new CookieSecurityExpectations { AuthCookieNames = ["Session"] });

    [Fact]
    public void CookieSection_ShowsAttributesAndFindings_NeverAValue()
    {
        var cut = Render<CookieSecuritySection>(p => p.Add(x => x.Assessment, Assessment()).Add(x => x.ProfileId, "dev"));
        var row = cut.Find("[data-testid=fqr-cookie-row]");
        row.GetAttribute("data-cookie").Should().Be("Session");
        row.TextContent.Should().Contain("Auth/session (declared)").And.Contain("None");
        cut.FindAll("[data-testid=fqr-cookie-finding]").Select(f => f.GetAttribute("data-rule")).Should().Contain(["cookie-samesite-none-insecure", "cookie-auth-missing-secure", "cookie-auth-missing-httponly"]);
        cut.Markup.Should().NotContain(CookieSentinel);
    }

    [Fact]
    public void CookieSection_NotRecorded_IsStatedNotPassed()
    {
        var cut = Render<CookieSecuritySection>(p => p.Add(x => x.Assessment, null));
        cut.Find("[data-testid=fqr-cookies-not-recorded]").TextContent.Should().Contain("not recorded");
        cut.FindAll("[data-testid=fqr-cookie-no-findings]").Should().BeEmpty();
    }

    [Fact]
    public void CookieSection_LoadsProxyObservedAttributes_ForTheActiveEnvironmentOnly()
    {
        var proxy = new Mock<ILocalHttpsProxyApiService>();
        proxy.Setup(p => p.GetRuntimeAsync()).ReturnsAsync(new LocalHttpsProxyStatus { SessionId = "s1", ProfileId = "dev", ContextFingerprint = "FP" });
        proxy.Setup(p => p.ObservedCookieAttributesAsync(It.Is<LocalHttpsProxySessionRequest>(r => r.SessionId == "s1" && r.ProfileId == "dev")))
            .ReturnsAsync([SetCookieMetadataParser.Parse("AppSession=v; Secure; HttpOnly; SameSite=Lax", "api.example.test", CookieEvidenceSource.LocalHttpsProxyObserved)!]);
        Services.AddSingleton(proxy.Object);
        var cut = Render<CookieSecuritySection>(p => p.Add(x => x.Assessment, Assessment()).Add(x => x.ProfileId, "dev").Add(x => x.Expectations, new CookieSecurityExpectations { AuthCookieNames = ["AppSession"] }));
        cut.Find("[data-testid=fqr-cookies-proxy-load]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=fqr-cookies-proxy-result] [data-testid=fqr-cookie-row]").GetAttribute("data-cookie").Should().Be("AppSession"));
        cut.Find("[data-testid=fqr-cookies-proxy-result]").TextContent.Should().Contain("Auth/session (declared)");

        proxy.Setup(p => p.GetRuntimeAsync()).ReturnsAsync(new LocalHttpsProxyStatus { SessionId = "s2", ProfileId = "other", ContextFingerprint = "FP2" });
        cut.Find("[data-testid=fqr-cookies-proxy-load]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=fqr-cookies-proxy-status]").TextContent.Should().Contain("No Local HTTPS proxy session is running for this Target Environment"));
    }

    // ── Exports ────────────────────────────────────────────────────────────────────────────────────

    private static string Esc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
    private static string Table(string[] head, IEnumerable<string[]> rows) => "<table>" + string.Join("", head) + string.Join("", rows.Select(r => string.Join("|", r))) + "</table>";
    private static string Badge(string s) => $"[{s}]";

    [Fact]
    public void Export_AuthorizationScenarios_StatesWarningOutcomesAndNoCredential()
    {
        var html = RuntimeSecurityExport.AuthorizationSection(new AuthorizationRunReport
        {
            RunId = "auth-1", EnvironmentName = "DEV", ExpectationFingerprint = "abc", Trust = new() { State = TrustedTargetState.Trusted, Reason = "Registered." },
            Observations = [new() { Scenario = "Roles", IdentityAlias = "anonymous", Expected = AuthorizationExpectation.Deny, StatusCode = 200, Outcome = AuthorizationOutcome.UnexpectedAllow, Reason = "Authorization: Bearer eyJabcdefghijk.x.y leaked" }],
        }, Table, Badge, Esc);
        html.Should().Contain(RuntimeSecurityWording.NotAPenetrationTest).And.Contain("UnexpectedAllow").And.Contain("abc");
        html.Should().NotContain("eyJabcdefghijk").And.NotContain("Bearer eyJ");
    }

    [Fact]
    public void Export_CookieSection_HasAttributesNoValues()
    {
        var html = RuntimeSecurityExport.CookieSection(Assessment(), Table, Badge, Esc);
        html.Should().Contain("Session").And.Contain("cookie-auth-missing-secure").And.NotContain(CookieSentinel);
        RuntimeSecurityExport.CookieSection(null, Table, Badge, Esc).Should().Contain("Not recorded");
    }

    [Fact]
    public void Export_DocumentationAndCors_AndBodySummary()
    {
        var target = new ApiReviewTargetResult
        {
            Target = new ApiReviewTarget { TargetId = "t" },
            DocumentationExposure = new ApiDocumentationExposureResult { Origin = "https://api.example.test", Observed = ApiDocumentationProbeState.ReachableProtected, Expectation = ApiDocumentationExposureExpectation.ExpectedProtected, Assessment = ApiDocumentationAssessment.AsExpected, Reason = "ok", Probes = [new() { Path = "/swagger", Source = "Default candidate", StatusCode = 401, State = ApiDocumentationProbeState.ReachableProtected }] },
            CorsProbes = [new CorsProbeObservation { Kind = CorsProbeKind.ForeignOrigin, Origin = CorsProbeRules.ForeignOrigin, StatusCode = 204, Observed = CorsObservedBehavior.NoCorsHeaders, Assessment = CorsAssessment.AsExpected }],
        };
        var html = RuntimeSecurityExport.DocumentationAndCors(target, Table, Badge, Esc);
        html.Should().Contain("API documentation exposure").And.Contain("/swagger").And.Contain("CORS probes").And.Contain(CorsProbeRules.ForeignOrigin);
        var run = new ApiFuzzingReport { Settings = new ApiFuzzingSettings { BodyFuzzing = true }, BodyTrust = [new() { State = TrustedTargetState.NotRegistered, Reason = "not registered" }], BodyOperations = [new() { Method = "POST", Path = "/s", Policy = BodyFuzzingPolicy.ReadOnlyBodySafe }] };
        RuntimeSecurityExport.BodyFuzzingSummary(run, Esc).Should().Contain("NotRegistered").And.Contain("POST /s");
        RuntimeSecurityExport.BodyFuzzingSummary(new ApiFuzzingReport(), Esc).Should().Contain("off for this run");
    }

    // ── Sign-in hooks ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AuthOptions_HaveNoSecret_AndAreConfiguredOnlyWithTenantClientAndScope()
    {
        typeof(SecurityExecutionAuthOptions).GetProperties().Select(p => p.Name).Should().NotContain(n => n.Contains("Secret", StringComparison.OrdinalIgnoreCase));
        new SecurityExecutionAuthOptions().IsConfigured.Should().BeFalse();
        new SecurityExecutionAuthOptions { TenantId = "t", SpaClientId = "c" }.IsConfigured.Should().BeFalse();
        var configured = new SecurityExecutionAuthOptions { TenantId = "tenant-guid", SpaClientId = "c", ApiScope = "api://x/.default" };
        configured.IsConfigured.Should().BeTrue();
        configured.EffectiveAuthority.Should().Be("https://login.microsoftonline.com/tenant-guid");
        new UnavailableSecurityExecutionTokenProvider(configured).GetAccessTokenAsync().Result.Should().BeNull("no sign-in library is bundled in this build");
    }

    private sealed class StaticToken(string? token) : ISecurityExecutionTokenProvider
    {
        public bool IsConfigured => true;
        public Task<string?> GetAccessTokenAsync(CancellationToken ct = default) => Task.FromResult(token);
    }

    private sealed class Capture : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Requests = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Requests.Add(request); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }
    }

    [Fact]
    public async Task BearerHandler_AttachesTheUserTokenOnlyToProtectedSecurityExecution()
    {
        var capture = new Capture();
        using var client = new HttpClient(new SecurityExecutionBearerHandler(new StaticToken("user-token")) { InnerHandler = capture }) { BaseAddress = new Uri("http://localhost:5000/") };
        await client.PostAsync("api/api-quality/authorization/runs", null);
        await client.PostAsync("api/api-quality/fuzzing/runs", null);
        await client.PostAsync("api/api-quality/review", null);
        await client.GetAsync("api/source-analysis");
        capture.Requests.Select(r => r.Headers.Authorization?.Parameter).Should().Equal("user-token", "user-token", null, null);
    }
}
