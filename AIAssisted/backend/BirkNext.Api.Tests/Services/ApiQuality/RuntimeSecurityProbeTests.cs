using System.Net;
using System.Text.Json;
using BirkNext.Api.Services.ApiQuality;
using BirkNext.Api.Services.ApiQuality.Security;
using BirkNext.Api.Services.ContractAnalysis;
using BirkNext.ApiReview;
using BirkNext.RuntimeSecurity;
using Microsoft.Extensions.Logging.Abstractions;
using static BirkNext.Api.Tests.Services.ApiQuality.RuntimeSecurityTestSupport;

namespace BirkNext.Api.Tests.Services.ApiQuality;

/// <summary>
/// API documentation exposure, cross-origin CORS and cookie metadata: deterministic rules, engine integration against synthetic
/// responses, expectation-driven assessment, production policy, and the guarantee that cookie values never surface.
/// </summary>
public sealed class RuntimeSecurityProbeTests
{
    private static readonly OpenApiExtractor Extractor = new(NullLogger<OpenApiExtractor>.Instance);
    private const string ValidOpenApi = """{"openapi":"3.0.1","info":{"title":"Children","version":"1"},"paths":{"/api/children":{"get":{"responses":{"200":{"description":"ok"}}}}}}""";

    private static ApiReviewEngine Engine(Handler handler) =>
        new(new HttpClient(handler), new Gateway(false), Extractor, new GraphQlExtractor(NullLogger<GraphQlExtractor>.Instance), NullLogger<ApiReviewEngine>.Instance);

    private static ApiRuntimeSecurityPolicy Policy(ApiDocumentationExposureExpectation exposure = ApiDocumentationExposureExpectation.NotSpecified, CorsExpectations? cors = null, params string[] paths) =>
        new() { ApiDocumentationExposure = exposure, ApiDocumentationPaths = [.. paths], Cors = cors ?? new() };

    // ── Swagger / OpenAPI exposure: rules ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(200, "{\"openapi\":\"3.0.1\"}", ApiDocumentationProbeState.ReachablePublic, "OpenAPI document")]
    [InlineData(200, "<html><div id=\"swagger-ui\"></div></html>", ApiDocumentationProbeState.ReachablePublic, "Swagger UI page")]
    [InlineData(200, "<html><app>Loading…</app></html>", ApiDocumentationProbeState.NotVerified, null)]
    [InlineData(401, "", ApiDocumentationProbeState.ReachableProtected, null)]
    [InlineData(403, "", ApiDocumentationProbeState.ReachableProtected, null)]
    [InlineData(404, "", ApiDocumentationProbeState.NotFound, null)]
    [InlineData(302, "", ApiDocumentationProbeState.Redirected, null)]
    [InlineData(500, "", ApiDocumentationProbeState.NotVerified, null)]
    public void Classify_StatusAndBody(int status, string body, ApiDocumentationProbeState expected, string? kind)
    {
        var (state, documentKind, _) = ApiDocumentationExposureRules.Classify(status, "application/json", body);
        Assert.Equal(expected, state);
        Assert.Equal(kind, documentKind);
    }

    [Theory]
    [InlineData(ApiDocumentationProbeState.ReachablePublic, ApiDocumentationExposureExpectation.Allowed, ApiDocumentationAssessment.AsExpected)]
    [InlineData(ApiDocumentationProbeState.ReachablePublic, ApiDocumentationExposureExpectation.ExpectedProtected, ApiDocumentationAssessment.UnexpectedExposure)]
    [InlineData(ApiDocumentationProbeState.ReachablePublic, ApiDocumentationExposureExpectation.ExpectedUnavailable, ApiDocumentationAssessment.UnexpectedExposure)]
    [InlineData(ApiDocumentationProbeState.ReachableProtected, ApiDocumentationExposureExpectation.ExpectedProtected, ApiDocumentationAssessment.AsExpected)]
    [InlineData(ApiDocumentationProbeState.ReachableProtected, ApiDocumentationExposureExpectation.ExpectedUnavailable, ApiDocumentationAssessment.UnexpectedExposure)]
    [InlineData(ApiDocumentationProbeState.NotFound, ApiDocumentationExposureExpectation.ExpectedUnavailable, ApiDocumentationAssessment.AsExpected)]
    [InlineData(ApiDocumentationProbeState.NotFound, ApiDocumentationExposureExpectation.Allowed, ApiDocumentationAssessment.UnexpectedAbsence)]
    [InlineData(ApiDocumentationProbeState.ReachablePublic, ApiDocumentationExposureExpectation.NotSpecified, ApiDocumentationAssessment.NoExpectation)]
    [InlineData(ApiDocumentationProbeState.NotVerified, ApiDocumentationExposureExpectation.ExpectedProtected, ApiDocumentationAssessment.NotVerified)]
    public void Assess_UsesTheExplicitExpectation_PublicDocsAreNeverADefectByThemselves(ApiDocumentationProbeState observed, ApiDocumentationExposureExpectation expectation, ApiDocumentationAssessment expected) =>
        Assert.Equal(expected, ApiDocumentationExposureRules.Assess(observed, expectation).Assessment);

    [Fact]
    public void Candidates_AreSameOrigin_ConfiguredFirst_SmallAndDeterministic()
    {
        var candidates = ApiDocumentationExposureRules.Candidates(ApiOrigin, ["/internal/docs", "https://other.example.test/swagger", "/../etc", "relative"], ApiOrigin + "/swagger/v1/swagger.json");
        Assert.Equal("/internal/docs", candidates[0].Path);
        Assert.Equal("Configured", candidates[0].Source);
        Assert.Contains(candidates, c => c.Path == "/swagger/v1/swagger.json" && c.Source == "Contract source");
        Assert.DoesNotContain(candidates, c => c.Path.Contains("other") || c.Path.Contains(".."));
        Assert.True(candidates.Count <= ApiDocumentationExposureRules.MaxProbes);
        Assert.Equal(candidates.Count, candidates.Select(c => c.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    // ── Swagger / OpenAPI exposure: probing ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Probe_ValidOpenApiDocument_ReusesTheSharedParser_StopsAtFirstPublicDocument()
    {
        var handler = new Handler { Respond = (r, _) => r.RequestUri!.AbsolutePath == "/swagger/v1/swagger.json" ? Text(HttpStatusCode.OK, ValidOpenApi) : Text(HttpStatusCode.NotFound, "") };
        var result = await ApiRuntimeSecurityProbes.DocumentationExposureAsync(new HttpClient(handler), Extractor, "rest-1", ApiOrigin, null, Policy(ApiDocumentationExposureExpectation.ExpectedProtected), default);
        Assert.Equal(ApiDocumentationProbeState.ReachablePublic, result.Observed);
        Assert.Equal(ApiDocumentationAssessment.UnexpectedExposure, result.Assessment);
        var probe = Assert.Single(result.Probes, p => p.State == ApiDocumentationProbeState.ReachablePublic);
        Assert.Contains("Valid OpenAPI 3.x document (1 operation(s))", probe.DocumentValidation);
        Assert.Equal("/swagger/v1/swagger.json", result.Probes[^1].Path);
        Assert.All(handler.Requests, r => Assert.Null(r.Headers.Authorization));
        Assert.All(handler.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task Probe_MalformedOpenApi_IsReportedAsMalformed_NotAsContractQuality()
    {
        var handler = new Handler { Respond = (r, _) => r.RequestUri!.AbsolutePath == "/openapi.json" ? Text(HttpStatusCode.OK, "{\"openapi\":\"3.0.1\",\"paths\":") : Text(HttpStatusCode.NotFound, "") };
        var result = await ApiRuntimeSecurityProbes.DocumentationExposureAsync(new HttpClient(handler), Extractor, "rest-1", ApiOrigin, null, Policy(ApiDocumentationExposureExpectation.Allowed), default);
        Assert.StartsWith("Malformed or unsupported OpenAPI document", result.Probes.Single(p => p.Path == "/openapi.json").DocumentValidation);
        Assert.Equal(ApiDocumentationAssessment.AsExpected, result.Assessment);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ApiDocumentationExposureExpectation.ExpectedProtected, ApiDocumentationAssessment.AsExpected)]
    [InlineData(HttpStatusCode.Forbidden, ApiDocumentationExposureExpectation.ExpectedProtected, ApiDocumentationAssessment.AsExpected)]
    [InlineData(HttpStatusCode.NotFound, ApiDocumentationExposureExpectation.ExpectedUnavailable, ApiDocumentationAssessment.AsExpected)]
    [InlineData(HttpStatusCode.Redirect, ApiDocumentationExposureExpectation.ExpectedProtected, ApiDocumentationAssessment.AsExpected)]
    public async Task Probe_ProtectedAbsentOrRedirected(HttpStatusCode status, ApiDocumentationExposureExpectation expectation, ApiDocumentationAssessment expected)
    {
        var handler = new Handler { Respond = (_, _) => Text(status, "") };
        var result = await ApiRuntimeSecurityProbes.DocumentationExposureAsync(new HttpClient(handler), Extractor, "rest-1", ApiOrigin, null, Policy(expectation), default);
        Assert.Equal(expected, result.Assessment);
        Assert.Equal(ApiDocumentationExposureRules.DefaultCandidates.Length, handler.Requests.Count);
    }

    [Fact]
    public async Task Engine_NoExposureExpectation_SendsNoDocumentationRequest()
    {
        var handler = new Handler { Respond = (r, _) => Text(HttpStatusCode.OK, "{\"items\":[]}") };
        var report = await Engine(handler).RunAsync(ReviewRequest(runtime: Policy()));
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsolutePath.Contains("swagger") || r.RequestUri.AbsolutePath.Contains("openapi") || r.RequestUri.AbsolutePath.Contains("api-docs"));
        var target = Assert.Single(report.Targets);
        Assert.Equal(ApiDocumentationAssessment.NotVerified, target.DocumentationExposure!.Assessment);
        Assert.Contains(target.Checks, c => c.CheckId == "api-docs-exposure" && c.Result == ApiReviewCheckResult.NotTested);
    }

    [Fact]
    public async Task Engine_PublicDocsAgainstExpectedProtected_IsOneFindingPerOrigin()
    {
        var handler = new Handler { Respond = (r, _) => r.RequestUri!.AbsolutePath == "/swagger" ? Text(HttpStatusCode.OK, "<html><div id='swagger-ui'></div></html>", "text/html") : Text(HttpStatusCode.OK, "{\"items\":[]}") };
        var second = Rest(basePath: "/api/reports") with { TargetId = "rest-2" };
        var report = await Engine(handler).RunAsync(ReviewRequest(runtime: Policy(ApiDocumentationExposureExpectation.ExpectedProtected), targets: [Rest(), second]));
        var finding = Assert.Single(report.Findings, f => f.RuleId == "api-docs-unexpected-exposure");
        Assert.Equal(ApiReviewSeverity.Medium, finding.Severity);
        Assert.All(report.Targets, t => Assert.Equal(ApiDocumentationAssessment.UnexpectedExposure, t.DocumentationExposure!.Assessment));
        Assert.Single(handler.Requests, r => r.RequestUri!.AbsolutePath == "/swagger");
    }

    [Fact]
    public async Task Engine_PublicDocsAllowed_NoFinding()
    {
        var handler = new Handler { Respond = (r, _) => r.RequestUri!.AbsolutePath == "/swagger" ? Text(HttpStatusCode.OK, "<div id='swagger-ui'></div>", "text/html") : Text(HttpStatusCode.OK, "{\"items\":[]}") };
        var report = await Engine(handler).RunAsync(ReviewRequest(runtime: Policy(ApiDocumentationExposureExpectation.Allowed)));
        Assert.DoesNotContain(report.Findings, f => f.RuleId.StartsWith("api-docs-"));
        Assert.Equal(ApiDocumentationAssessment.AsExpected, report.Targets[0].DocumentationExposure!.Assessment);
    }

    [Fact]
    public async Task Engine_Production_NeverProbesDocumentationOrForeignOrigins()
    {
        var handler = new Handler { Respond = (_, _) => Text(HttpStatusCode.OK, "{\"items\":[]}") };
        var report = await Engine(handler).RunAsync(ReviewRequest("Production", Policy(ApiDocumentationExposureExpectation.ExpectedUnavailable)));
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsolutePath is "/swagger" or "/openapi.json");
        Assert.DoesNotContain(handler.Requests, r => r.Headers.TryGetValues("Origin", out var o) && o.Single() == CorsProbeRules.ForeignOrigin);
        Assert.Contains(report.Targets[0].Checks, c => c.CheckId == "api-docs-exposure" && c.Result == ApiReviewCheckResult.NotTested);
        Assert.Contains(report.Targets[0].Checks, c => c.CheckId == "cors-cross-origin" && c.Result == ApiReviewCheckResult.NotTested);
    }

    // ── CORS ───────────────────────────────────────────────────────────────────────────────────────

    private static Dictionary<string, string> Cors(string? origin, bool credentials = false, string? methods = null, string? headers = null, bool vary = false)
    {
        var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (origin is not null) h["access-control-allow-origin"] = origin;
        if (credentials) h["access-control-allow-credentials"] = "true";
        if (methods is not null) h["access-control-allow-methods"] = methods;
        if (headers is not null) h["access-control-allow-headers"] = headers;
        if (vary) h["vary"] = "Origin";
        return h;
    }

    [Fact]
    public void Cors_ConfiguredAllowedOrigin_GrantedIsAsExpected_DeniedIsUnexpectedDeny()
    {
        var expectations = new CorsExpectations { AllowedOrigins = ["https://allowed.example.test"] };
        var granted = CorsProbeRules.Evaluate(CorsProbeKind.ConfiguredAllowedOrigin, "https://allowed.example.test", 204, Cors("https://allowed.example.test", vary: true), expectations, true);
        Assert.Equal(CorsAssessment.AsExpected, granted.Assessment);
        Assert.True(granted.VaryOrigin);
        var denied = CorsProbeRules.Evaluate(CorsProbeKind.ConfiguredAllowedOrigin, "https://allowed.example.test", 204, Cors(null), expectations, true);
        Assert.Equal(CorsAssessment.UnexpectedDeny, denied.Assessment);
        Assert.Equal("cors-allowed-origin-denied", denied.RuleId);
    }

    [Fact]
    public void Cors_ForeignOriginDenied_OrNoHeaders_IsAsExpected_NotANetworkFailure()
    {
        var expectations = new CorsExpectations();
        Assert.Equal(CorsAssessment.AsExpected, CorsProbeRules.Evaluate(CorsProbeKind.ForeignOrigin, CorsProbeRules.ForeignOrigin, 204, Cors("https://app.example.test"), expectations, true).Assessment);
        var none = CorsProbeRules.Evaluate(CorsProbeKind.ForeignOrigin, CorsProbeRules.ForeignOrigin, 204, Cors(null), expectations, true);
        Assert.Equal(CorsObservedBehavior.NoCorsHeaders, none.Observed);
        Assert.Equal(CorsAssessment.AsExpected, none.Assessment);
        Assert.Null(none.RuleId);
        Assert.Equal(CorsAssessment.NotVerified, CorsProbeRules.Evaluate(CorsProbeKind.ForeignOrigin, CorsProbeRules.ForeignOrigin, null, null, expectations, true).Assessment);
    }

    [Theory]
    [InlineData(true, true, "cors-reflected-origin-credentials")]
    [InlineData(false, true, "cors-reflected-origin")]
    [InlineData(false, false, "cors-reflected-origin-public")]
    public void Cors_ReflectedForeignOrigin_SeverityDependsOnCredentialsAndAuthentication(bool credentials, bool authRequired, string rule)
    {
        var o = CorsProbeRules.Evaluate(CorsProbeKind.ForeignOrigin, CorsProbeRules.ForeignOrigin, 204, Cors(CorsProbeRules.ForeignOrigin, credentials), new(), authRequired);
        Assert.Equal(CorsObservedBehavior.ReflectedOrigin, o.Observed);
        Assert.Equal(CorsAssessment.UnexpectedAllow, o.Assessment);
        Assert.Equal(rule, o.RuleId);
    }

    [Fact]
    public void Cors_WildcardWithCredentials_IsMisconfiguration_WildcardAloneOnPublicApiIsObservation()
    {
        Assert.Equal("cors-wildcard-credentials", CorsProbeRules.Evaluate(CorsProbeKind.ForeignOrigin, CorsProbeRules.ForeignOrigin, 204, Cors("*", credentials: true), new(), false).RuleId);
        var publicWildcard = CorsProbeRules.Evaluate(CorsProbeKind.ForeignOrigin, CorsProbeRules.ForeignOrigin, 204, Cors("*"), new(), false);
        Assert.Equal(CorsAssessment.Observation, publicWildcard.Assessment);
        Assert.Null(publicWildcard.RuleId);
    }

    [Fact]
    public void Cors_CredentialsAgainstExpectation_AndMethodHeaderExcess()
    {
        var expectations = new CorsExpectations { AllowedOrigins = ["https://app.example.test"], AllowCredentials = false, AllowedMethods = ["GET"], AllowedHeaders = ["authorization"] };
        Assert.Equal("cors-credentials-unexpected", CorsProbeRules.Evaluate(CorsProbeKind.FrontendOrigin, "https://app.example.test", 204, Cors("https://app.example.test", true), expectations, true).RuleId);
        Assert.Equal(["DELETE"], CorsProbeRules.Excess("GET, DELETE", expectations.AllowedMethods));
        Assert.Equal(["x-custom"], CorsProbeRules.Excess("authorization, x-custom", expectations.AllowedHeaders));
        Assert.Empty(CorsProbeRules.Excess("GET, DELETE", []));
    }

    [Fact]
    public async Task Engine_SendsAllowedAndSyntheticForeignPreflights_AnonymousOnly_FindsReflection()
    {
        var handler = new Handler
        {
            Respond = (r, _) => r.Method == HttpMethod.Options
                ? Text(HttpStatusCode.NoContent, "", headers: resp => { resp.Headers.TryAddWithoutValidation("Access-Control-Allow-Origin", r.Headers.GetValues("Origin").Single()); resp.Headers.TryAddWithoutValidation("Access-Control-Allow-Credentials", "true"); })
                : Text(HttpStatusCode.OK, "{\"items\":[]}"),
        };
        var report = await Engine(handler).RunAsync(ReviewRequest(runtime: Policy(cors: new CorsExpectations { AllowedOrigins = ["https://allowed.example.test"] })));
        var origins = handler.Requests.Where(r => r.Method == HttpMethod.Options).Select(r => r.Headers.GetValues("Origin").Single()).ToList();
        Assert.Equal(["https://app-dev.example.test", "https://allowed.example.test", CorsProbeRules.ForeignOrigin], origins);
        Assert.All(handler.Requests.Where(r => r.Method == HttpMethod.Options), r => Assert.Null(r.Headers.Authorization));
        var finding = Assert.Single(report.Findings, f => f.RuleId == "cors-reflected-origin-credentials");
        Assert.Equal(ApiReviewSeverity.High, finding.Severity);
        Assert.Equal(3, report.Targets[0].CorsProbes.Count);
        Assert.Equal(CorsAssessment.AsExpected, report.Targets[0].CorsProbes.Single(o => o.Kind == CorsProbeKind.ConfiguredAllowedOrigin).Assessment);
    }

    // ── Cookies ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SetCookieParser_KeepsAttributes_NeverTheValue()
    {
        var c = SetCookieMetadataParser.Parse($"Session={CookieSentinel}; Path=/app; Domain=.Example.test; Secure; HttpOnly; SameSite=Lax; Max-Age=3600", "app.example.test", CookieEvidenceSource.FrontendDocumentResponse)!;
        Assert.Equal("Session", c.Name);
        Assert.True(c.Secure); Assert.True(c.HttpOnly); Assert.True(c.Persistent);
        Assert.Equal(CookieSameSite.Lax, c.SameSite);
        Assert.Equal("example.test", c.Domain);
        Assert.Equal("/app", c.Path);
        Assert.DoesNotContain(CookieSentinel, JsonSerializer.Serialize(c));
        Assert.Null(SetCookieMetadataParser.Parse("=novalue", "h", CookieEvidenceSource.FrontendDocumentResponse));
    }

    [Fact]
    public void SetCookieParser_SecretShapedNameIsDigested()
    {
        var c = SetCookieMetadataParser.Parse("eyJhbGciOiJIUzI1NiJ9abcdef=1; Secure", "h", CookieEvidenceSource.FrontendDocumentResponse)!;
        Assert.True(c.NameRedacted);
        Assert.StartsWith("cookie-", c.Name);
    }

    private static CookieObservation Cookie(string header) => SetCookieMetadataParser.Parse(header, "app.example.test", CookieEvidenceSource.FrontendDocumentResponse)!;

    [Fact]
    public void CookieRules_DeclaredAuthCookie_IsEvaluatedStrictly()
    {
        var assessment = CookieSecurityEvaluator.Evaluate([Cookie("Session=x; Domain=example.test; Max-Age=60"), Cookie("Prefs=y; SameSite=Lax")],
            new CookieSecurityExpectations { AuthCookieNames = ["Session"], AllowedSameSite = ["Strict"] });
        var rules = assessment.Findings.Select(f => f.RuleId).ToList();
        Assert.Contains("cookie-auth-missing-secure", rules);
        Assert.Contains("cookie-auth-missing-httponly", rules);
        Assert.Contains("cookie-auth-samesite", rules);
        Assert.Contains("cookie-auth-broad-domain", rules);
        Assert.Contains("cookie-auth-persistent", rules);
        Assert.All(assessment.Findings, f => Assert.Equal("Session", f.Cookie));   // the unclassified Prefs cookie is an observation only
        Assert.Equal(1, assessment.AuthCookies);
    }

    [Fact]
    public void CookieRules_SecureHttpOnlySameSiteSessionCookie_HasNoFinding()
    {
        var assessment = CookieSecurityEvaluator.Evaluate([Cookie("Session=x; Secure; HttpOnly; SameSite=Strict")], new CookieSecurityExpectations { AuthCookieNames = ["Session"], AllowedSameSite = ["Strict"] });
        Assert.Empty(assessment.Findings);
        Assert.False(assessment.Cookies[0].Persistent);
    }

    [Fact]
    public void CookieRules_SameSiteNoneWithoutSecure_AppliesToEveryCookie_UndeclaredIsAnObservationOtherwise()
    {
        var assessment = CookieSecurityEvaluator.Evaluate([Cookie("Tracking=z; SameSite=None"), Cookie("Theme=dark")], null);
        Assert.Equal(["cookie-samesite-none-insecure"], assessment.Findings.Select(f => f.RuleId));
        Assert.Contains(assessment.Notes, n => n.Contains("No auth/session cookie names are declared"));
    }

    [Fact]
    public void CookieRules_DeletionCookiesAreIgnored()
    {
        Assert.Empty(CookieSecurityEvaluator.Evaluate([Cookie("Session=; Max-Age=0")], new CookieSecurityExpectations { AuthCookieNames = ["Session"] }).Cookies);
    }

    [Fact]
    public async Task StaticSecurity_ReadsDocumentSetCookieMetadata_ValueNeverInTheReport()
    {
        var handler = new Handler
        {
            Respond = (r, _) => r.RequestUri!.AbsolutePath == "/"
                ? Text(HttpStatusCode.OK, "<html><body>app</body></html>", "text/html", resp => resp.Headers.TryAddWithoutValidation("Set-Cookie", $"Session={CookieSentinel}; Path=/; SameSite=None"))
                : Text(HttpStatusCode.NotFound, ""),
        };
        var service = new BirkNext.Api.Services.WasmSecurity.BlazorWasmSecurityReviewService(new HttpClient(handler), NullLogger<BirkNext.Api.Services.WasmSecurity.BlazorWasmSecurityReviewService>.Instance);
        var report = await service.ScanAsync(new BirkNext.Api.Services.WasmSecurity.WasmScanRequest
        {
            TargetUrl = "https://app.example.test/", CookieExpectations = new CookieSecurityExpectations { AuthCookieNames = ["Session"] },
        }, default);
        var cookie = Assert.Single(report.CookieSecurity!.Cookies);
        Assert.Equal(CookieClassification.DeclaredAuthOrSession, cookie.Classification);
        Assert.Contains(report.CookieSecurity.Findings, f => f.RuleId == "cookie-samesite-none-insecure");
        Assert.DoesNotContain(CookieSentinel, JsonSerializer.Serialize(report));
    }

    [Fact]
    public void ProxyCookieRegistry_KeepsDistinctAttributeSets_NoValues_Capped()
    {
        var registry = new BirkNext.Api.Services.LocalHttpsProxy.ObservedCookieRegistry(capacity: 2);
        foreach (var header in new[] { $"A={CookieSentinel}; Secure", $"A={CookieSentinel}; Secure; HttpOnly", $"B=1", "C=1", "D=; Max-Age=0" })
            registry.Record(SetCookieMetadataParser.Parse(header, "api.example.test", CookieEvidenceSource.LocalHttpsProxyObserved)!);
        var snapshot = registry.Snapshot();
        Assert.Equal(["A", "B"], snapshot.Select(c => c.Name));
        Assert.True(snapshot[0].HttpOnly);   // the latest attributes of the same cookie identity
        Assert.DoesNotContain(CookieSentinel, JsonSerializer.Serialize(snapshot));
    }
}
