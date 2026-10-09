using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using BirkNext.Api.Services.ApiQuality;
using BirkNext.Api.Services.ContractAnalysis;
using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.Api.Services.WasmSecurity;
using BirkNext.ApiReview;
using BirkNext.LocalHttpsProxy;
using BirkNext.Standards;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BirkNext.Api.Tests.Services.ApiQuality;

/// <summary>
/// Security foundation of API Quality Review: the backend (not a client boolean) decides whether active testing is allowed; expected
/// security headers come from the Target Environment; OWASP references are attached only to rule ids the engines actually emit.
/// </summary>
public sealed class ApiSecurityFoundationTests
{
    private const string Fp = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    private static ApiReviewTarget Target(string host = "api-dev.example.test", string scheme = "https", int port = 443) => new()
    {
        TargetId = "rest-1", ApiType = ApiReviewTargetType.Rest, Scheme = scheme, Host = host, Port = port, BasePath = "/api/children", ServiceName = "Children API", Selected = true,
        Operations = [new ApiReviewOperation { Method = "GET", Path = "/api/children" }],
    };

    private static ApiReviewRunRequest Request(string? type, bool isProduction = false, string targetUrl = "https://app-dev.example.test/", ApiReviewTarget? target = null,
        AuthenticatedTestingMethod method = AuthenticatedTestingMethod.ManagedEdgeCdp) => new()
    {
        Environment = new ApiReviewEnvironmentSnapshot { EnvironmentId = "env-1", Name = "Env", EnvironmentType = type ?? "", IsProduction = isProduction, TargetUrl = targetUrl, AuthenticatedTestingMethod = method },
        Identity = new AuthenticatedReviewIdentity(method, method == AuthenticatedTestingMethod.LocalHttpsProxy ? "env-1" : null, method == AuthenticatedTestingMethod.LocalHttpsProxy ? Fp : null),
        Targets = [target ?? Target()],
    };

    private sealed class ProxyStatus(string? environmentType) : ILocalHttpsProxyStatusQuery
    {
        public LocalHttpsProxyStatus? StatusForProfile(string profileId, string contextFingerprint) => environmentType is null ? null : new LocalHttpsProxyStatus { EnvironmentType = environmentType };
    }

    // ── Environment policy ────────────────────────────────────────────────────

    [Theory]
    [InlineData("Local")]
    [InlineData("Development")]
    [InlineData("QA")]
    [InlineData("Test")]
    public void Allowed_LocalDevelopmentQaTest(string type)
    {
        var decision = ApiEnvironmentSafetyPolicy.Default.Evaluate(Request(type));
        Assert.Equal(ApiEnvironmentSafetyState.Allowed, decision.State);
        Assert.True(decision.ActiveTestingAllowed);
        Assert.False(decision.ProductionLike);
    }

    [Fact]
    public void ProductionType_IsBlocked_AndProductionLike()
    {
        var decision = ApiEnvironmentSafetyPolicy.Default.Evaluate(Request("Production"));
        Assert.Equal(ApiEnvironmentSafetyState.ProductionBlocked, decision.State);
        Assert.True(decision.ProductionLike);
    }

    [Fact]
    public void ClientProductionFlag_WinsOverAnAllowedType()
    {
        // A client that says IsProduction is believed in the safe direction only.
        var decision = ApiEnvironmentSafetyPolicy.Default.Evaluate(Request("Development", isProduction: true));
        Assert.Equal(ApiEnvironmentSafetyState.ProductionBlocked, decision.State);
    }

    [Theory]
    [InlineData("api-prod.example.test")]
    [InlineData("prd-api.example.test")]
    [InlineData("api.production.example.test")]
    public void ProductionMarkerOnTargetHost_BlocksWhateverTheClaim(string host)
    {
        // The client claims Development and IsProduction=false: the backend still refuses a production-looking host.
        var decision = ApiEnvironmentSafetyPolicy.Default.Evaluate(Request("Development", target: Target(host)));
        Assert.Equal(ApiEnvironmentSafetyState.ProductionMarkerBlocked, decision.State);
        Assert.True(decision.ProductionLike);
        Assert.Contains(decision.Evidence, e => e.Contains(host));
    }

    [Fact]
    public void ProductionMarkerOnEnvironmentUrl_Blocks()
    {
        var decision = ApiEnvironmentSafetyPolicy.Default.Evaluate(Request("QA", targetUrl: "https://myapp-prod.example.test/"));
        Assert.Equal(ApiEnvironmentSafetyState.ProductionMarkerBlocked, decision.State);
    }

    [Fact]
    public void LoopbackHosts_AreNeverMarkerBlocked()
    {
        var decision = ApiEnvironmentSafetyPolicy.Default.Evaluate(Request("Local", targetUrl: "http://localhost:5174/", target: Target("localhost", "http", 5199)));
        Assert.Equal(ApiEnvironmentSafetyState.Allowed, decision.State);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Staging-ish")]
    public void UnknownType_FailsClosed(string? type)
    {
        var decision = ApiEnvironmentSafetyPolicy.Default.Evaluate(Request(type));
        Assert.Equal(ApiEnvironmentSafetyState.UnknownBlocked, decision.State);
        Assert.Contains("could not be established", decision.Reason);
    }

    [Theory]
    [InlineData("RC")]
    [InlineData("Custom")]
    public void RecognisedButNotAllowListed_IsNotPermitted(string type)
    {
        var decision = ApiEnvironmentSafetyPolicy.Default.Evaluate(Request(type));
        Assert.Equal(ApiEnvironmentSafetyState.NotPermittedBlocked, decision.State);
        Assert.False(decision.ProductionLike);
    }

    [Fact]
    public void ServerHeldProxyContext_DisagreeingWithTheClaim_IsConflictBlocked()
    {
        var policy = new ApiEnvironmentSafetyPolicy(new ProxyStatus("QA"));
        var decision = policy.Evaluate(Request("Development", method: AuthenticatedTestingMethod.LocalHttpsProxy));
        Assert.Equal(ApiEnvironmentSafetyState.ConflictBlocked, decision.State);
        Assert.Contains(decision.Evidence, e => e.Contains("Server-held proxy context classification: QA"));
    }

    [Fact]
    public void ServerHeldProxyContext_Production_IsProductionBlocked_EvenIfClientSaysDevelopment()
    {
        var decision = new ApiEnvironmentSafetyPolicy(new ProxyStatus("Production")).Evaluate(Request("Development", method: AuthenticatedTestingMethod.LocalHttpsProxy));
        Assert.Equal(ApiEnvironmentSafetyState.ProductionBlocked, decision.State);
    }

    [Fact]
    public void ServerHeldProxyContext_Agreeing_IsAllowed()
    {
        var decision = new ApiEnvironmentSafetyPolicy(new ProxyStatus("Development")).Evaluate(Request("Development", method: AuthenticatedTestingMethod.LocalHttpsProxy));
        Assert.Equal(ApiEnvironmentSafetyState.Allowed, decision.State);
    }

    [Fact]
    public void BlockedHostConfiguration_And_FuzzingSwitch_AreBackendSide()
    {
        var blocked = new ApiEnvironmentSafetyPolicy(null, Options.Create(new ApiActiveTestingOptions { BlockedHosts = ["*.example.test"] }));
        Assert.Equal(ApiEnvironmentSafetyState.NotPermittedBlocked, blocked.Evaluate(Request("Development")).State);

        var off = new ApiEnvironmentSafetyPolicy(null, Options.Create(new ApiActiveTestingOptions { FuzzingEnabled = false }));
        Assert.Equal(ApiEnvironmentSafetyState.Allowed, off.Evaluate(Request("Development")).State);
        Assert.Equal(ApiEnvironmentSafetyState.Disabled, off.EvaluateForFuzzing(Request("Development")).State);
    }

    [Fact]
    public async Task Engine_ErrorProbesFollowTheBackendDecision_NotTheClientFlag()
    {
        // Client says Development and IsProduction=false, policy allows probes — but the host carries a production marker.
        var handler = new RecordingHandler();
        var engine = new ApiReviewEngine(new HttpClient(handler), new NoGateway(), new OpenApiExtractor(NullLogger<OpenApiExtractor>.Instance), new GraphQlExtractor(NullLogger<GraphQlExtractor>.Instance), NullLogger<ApiReviewEngine>.Instance);
        var request = Request("Development", target: Target("children-prod.example.test")) with { Policy = new ApiReviewPolicy { ErrorHandlingProbes = true } };
        var report = await engine.RunAsync(request);

        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsolutePath.Contains("birknext-unknown-route"));
        Assert.Equal(ApiEnvironmentSafetyState.ProductionMarkerBlocked, report.Safety!.State);
        Assert.Contains(report.Limitations, l => l.Contains("Production policy"));
    }

    // ── Expected security headers ─────────────────────────────────────────────

    [Fact]
    public void HeaderEvaluator_ExpectedPresent_Pass_ExpectedMissing_Missing_NotExpected_Observed()
    {
        var observed = new Dictionary<string, string> { ["strict-transport-security"] = "max-age=1", ["x-frame-options"] = "DENY" };
        var rows = SecurityHeaderExpectations.Evaluate(observed, ["Strict-Transport-Security", "X-Content-Type-Options"], apiResponse: false, https: true);
        Assert.Equal(SecurityHeaderOutcome.Pass, rows.Single(r => r.Header == "Strict-Transport-Security").Result);
        Assert.Equal(SecurityHeaderOutcome.Missing, rows.Single(r => r.Header == "X-Content-Type-Options").Result);
        Assert.Equal(SecurityHeaderOutcome.Observed, rows.Single(r => r.Header == "X-Frame-Options").Result);
        Assert.Equal(SecurityHeaderOutcome.NotAssessed, rows.Single(r => r.Header == "Content-Security-Policy").Result);
    }

    [Fact]
    public void HeaderEvaluator_NosniffOnlyCounts_AndHstsIsNotApplicableOnPlainHttp()
    {
        var rows = SecurityHeaderExpectations.Evaluate(new Dictionary<string, string> { ["x-content-type-options"] = "sniff-please" }, ["X-Content-Type-Options", "Strict-Transport-Security"], false, https: false);
        Assert.Equal(SecurityHeaderOutcome.Missing, rows.Single(r => r.Header == "X-Content-Type-Options").Result);
        Assert.Equal(SecurityHeaderOutcome.NotApplicable, rows.Single(r => r.Header == "Strict-Transport-Security").Result);
    }

    [Fact]
    public void HeaderEvaluator_DocumentHeadersOnApiResponses_AreNotApplicable_TransportHeadersApply()
    {
        var rows = SecurityHeaderExpectations.Evaluate(new Dictionary<string, string>(), ["Content-Security-Policy", "X-Frame-Options", "Strict-Transport-Security"], apiResponse: true, https: true);
        Assert.Equal(SecurityHeaderOutcome.NotApplicable, rows.Single(r => r.Header == "Content-Security-Policy").Result);
        Assert.Equal(SecurityHeaderOutcome.NotApplicable, rows.Single(r => r.Header == "X-Frame-Options").Result);
        Assert.Equal(SecurityHeaderOutcome.Missing, rows.Single(r => r.Header == "Strict-Transport-Security").Result);
    }

    [Fact]
    public void StaticSecurity_ExpectedXFrameOptions_IsARealFinding_AndUnexpectedCspIsNot()
    {
        var headers = new Dictionary<string, string> { ["strict-transport-security"] = "max-age=1" };
        var findings = BlazorWasmSecurityReviewService.CheckSecurityHeaders(headers, ["X-Frame-Options", "Strict-Transport-Security"]).ToList();
        var xfo = Assert.Single(findings);
        Assert.Equal("HDR-MISSING-X-FRAME-OPTIONS", xfo.Id);
        Assert.DoesNotContain(findings, f => f.Id.Contains("CONTENT-SECURITY-POLICY"));
    }

    [Fact]
    public void StaticSecurity_WithoutExpectations_KeepsTheHistoricalFiveHeaders()
    {
        var findings = BlazorWasmSecurityReviewService.CheckSecurityHeaders(new Dictionary<string, string>()).Select(f => f.Id).ToList();
        Assert.Equal(5, findings.Count);
        Assert.DoesNotContain("HDR-MISSING-X-FRAME-OPTIONS", findings);
    }

    [Fact]
    public void StaticSecurity_EnvironmentSpecificExpectations_DifferPerEnvironment()
    {
        var observed = new Dictionary<string, string> { ["content-security-policy"] = "default-src 'self'" };
        var dev = BlazorWasmSecurityReviewService.CheckSecurityHeaders(observed, ["Content-Security-Policy"]).ToList();
        var qa = BlazorWasmSecurityReviewService.CheckSecurityHeaders(observed, ["Content-Security-Policy", "Permissions-Policy"]).ToList();
        Assert.Empty(dev);
        Assert.Equal("HDR-MISSING-PERMISSIONS-POLICY", Assert.Single(qa).Id);
    }

    [Fact]
    public async Task Aqr_UsesExpectedHeaders_ConfiguredMissingIsAFinding_NotConfiguredIsNot()
    {
        var handler = new RecordingHandler();   // responses carry no security headers at all
        var engine = new ApiReviewEngine(new HttpClient(handler), new NoGateway(), new OpenApiExtractor(NullLogger<OpenApiExtractor>.Instance), new GraphQlExtractor(NullLogger<GraphQlExtractor>.Instance), NullLogger<ApiReviewEngine>.Instance);

        var onlyXcto = await engine.RunAsync(Request("Development") with { Policy = new ApiReviewPolicy { ExpectedSecurityHeaders = ["X-Content-Type-Options", "Content-Security-Policy"] } });
        Assert.Contains(onlyXcto.Findings, f => f.RuleId == "sec-no-xcto");
        Assert.DoesNotContain(onlyXcto.Findings, f => f.RuleId == "sec-no-hsts");   // HSTS not expected → never a finding
        var headers = onlyXcto.Targets[0].SecurityHeaders;
        Assert.Equal(SecurityHeaderOutcome.NotApplicable, headers.Single(h => h.Header == "Content-Security-Policy").Result);
        Assert.Contains(onlyXcto.Targets[0].Checks, c => c.CheckId == "sec-hsts" && c.Result == ApiReviewCheckResult.NotApplicable);

        var legacy = await engine.RunAsync(Request("Development"));   // older client: no expectation list
        Assert.Contains(legacy.Findings, f => f.RuleId == "sec-no-hsts");
        Assert.Contains(legacy.Findings, f => f.RuleId == "sec-no-xcto");
    }

    // ── OWASP mappings are tied to emitted rule ids ───────────────────────────

    private static string SourceRoot([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", ".."));   // …/AIAssisted

    private static string Read(string relative) => File.ReadAllText(Path.Combine(SourceRoot(), relative));

    [Fact]
    public void EveryApiMappingKey_IsARuleIdTheApiEnginesEmit()
    {
        var sources = string.Join("\n", Directory.GetFiles(Path.Combine(SourceRoot(), "backend", "BirkNext.Api", "Services", "ApiQuality"), "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText));
        var emitted = Regex.Matches(sources, @"Finding\(\s*target(?:\.TargetId)?\s*,\s*""([a-z0-9-]+)""").Select(m => m.Groups[1].Value)
            .Concat(Regex.Matches(sources, @"Add\(""(fuzz-[a-z0-9-]+)""").Select(m => m.Groups[1].Value))
            .Concat(Regex.Matches(sources, @"Finding\(target\.TargetId, ""([a-z0-9-]+)""|Finding\(targetId, ""([a-z0-9-]+)""").SelectMany(m => new[] { m.Groups[1].Value, m.Groups[2].Value }))
            .Concat(Regex.Matches(sources, @"const string RuleId = ""([a-z0-9-]+)""").Select(m => m.Groups[1].Value))
            .Where(v => v.Length > 0).ToHashSet(StringComparer.Ordinal);
        var dead = StandardsReferenceMappings.ApiRuleIds.Where(k => !emitted.Contains(k)).ToList();
        Assert.True(dead.Count == 0, $"Mapping keys no API engine emits: {string.Join(", ", dead)}");
        // The legacy (uncalled) analyzer's ids must not carry the references.
        Assert.Empty(StandardsReferenceMappings.ForApiRule("sec-server-exposed"));
        Assert.Empty(StandardsReferenceMappings.ForApiRule("sec-cors-wildcard"));
    }

    [Fact]
    public void EveryFrontendMappingKey_IsEmitted_ByFqrOrStaticSecurity()
    {
        var fqr = Read(Path.Combine("frontend", "BirkNext.Web", "Services", "FrontendQualityReviewService.cs"));
        var scannerIds = BlazorWasmSecurityReviewService.CheckSecurityHeaders(new Dictionary<string, string>(), SecurityHeaderExpectations.Known.Select(k => k.Header).ToList()).Select(f => f.Id).ToHashSet();
        foreach (var key in StandardsReferenceMappings.FrontendRuleIds)
            Assert.True(fqr.Contains($"\"{key}\"") || scannerIds.Contains(key), $"Frontend mapping key {key} is not emitted");
        Assert.Empty(StandardsReferenceMappings.ForFrontendRule("std-xcontenttypeoptions-missing"));
    }

    [Fact]
    public void EveryIntegrationMappingKey_IsAnEmittedFindingRuleId()
    {
        var iqr = Read(Path.Combine("backend", "BirkNext.Api", "Services", "Integrations", "IntegrationReviewEngine.cs"));
        foreach (var key in StandardsReferenceMappings.IntegrationRuleIds)
            Assert.Matches(new Regex($@"RuleId = ""{Regex.Escape(key)}""|Finding\([^,]+, ""{Regex.Escape(key)}"""), iqr);
        Assert.Empty(StandardsReferenceMappings.ForIntegrationRule("sec-tls"));   // a check id, never a finding
    }

    [Fact]
    public void Mappings_AreReferencesOnly_NeverComplianceClaims()
    {
        foreach (var reference in StandardsReferenceMappings.ApiRuleIds.SelectMany(StandardsReferenceMappings.ForApiRule).Where(r => r.StandardId == "OWASP-TOP10"))
        {
            Assert.Equal(StandardsMappingType.Related, reference.MappingType);
            Assert.Contains("does not establish OWASP compliance", reference.Notes);
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Requests = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json") });
        }
    }

    private sealed class NoGateway : IAuthenticatedReviewGateway
    {
        public AuthenticatedReviewCapabilities Resolve(AuthenticatedReviewIdentity identity) => new() { Method = identity.Method, PublicApi = true, Reason = "public" };
        public Task<AuthenticatedReviewExecutionOutcome> ExecuteRestAsync(AuthenticatedReviewIdentity identity, string httpMethod, string url, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<AuthenticatedReviewExecutionOutcome> ExecuteGraphQlQueryAsync(AuthenticatedReviewIdentity identity, string endpointUrl, string query, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<AuthenticatedGraphQlSchemaOutcome> FetchGraphQlSchemaAsync(AuthenticatedReviewIdentity identity, string endpointUrl, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
}
