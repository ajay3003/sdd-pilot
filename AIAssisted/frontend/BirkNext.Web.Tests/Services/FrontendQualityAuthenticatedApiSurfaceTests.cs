using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// The Frontend Quality Review consumes the Local HTTPS proxy's authenticated API context through the gateway-backed API-surface
/// probes: one shared probe set per review, only for a proxy environment with active HTTP engines, findings owned by Static
/// Security (API security headers / CORS / rejected credential) and Passive Performance (authenticated latency), honest
/// "not checked" reporting when the context is missing, and never a credential in the report.
/// </summary>
public sealed class FrontendQualityAuthenticatedApiSurfaceTests : BunitContext
{
    private const string Target = "https://m2lbdev.example.test/";

    private static FrontendAuthenticatedApiCheck Check(string label, int status, double ms, Dictionary<string, string>? headers = null, string contentType = "application/json") => new()
    {
        Label = label, Url = $"https://api-dev.example.test/{label.ToLowerInvariant()}", Mode = ReviewExecutionMode.AuthenticatedViaLocalHttpsProxy,
        Status = AuthenticatedExecutionStatus.Executed, StatusCode = status, ElapsedMs = ms, ContentType = contentType, Outcome = $"HTTP {status}",
        SecurityHeaders = headers ?? new Dictionary<string, string>(),
    };

    private static FrontendAuthenticatedApiSurfaceResult Surface(params FrontendAuthenticatedApiCheck[] checks) => new()
    {
        ContextAvailable = true, Checks = checks,
        Capabilities = new AuthenticatedReviewCapabilities { Method = AuthenticatedTestingMethod.LocalHttpsProxy, ContextStatus = AuthenticatedApiContextStatus.Available, AuthenticatedApi = true },
    };

    // ── Findings mapper ─────────────────────────────────────────────────────────

    [Fact]
    public void Findings_RejectedCredential_IsHighSecurityFindingOwnedByStaticSecurity()
    {
        var findings = FrontendQualityAuthenticatedApiSurfaceFindings.Build(Surface(Check("Health", 401, 50)), new(), securityActive: true, performanceActive: true);

        var finding = findings.Should().ContainSingle().Which;
        finding.Severity.Should().Be(FrontendQualitySeverity.High);
        finding.Category.Should().Be(FrontendQualityCategory.Security);
        finding.EngineId.Should().Be(FrontendQualityEngineId.StaticSecurity);
        finding.SourceRuleId.Should().Be("auth-api-rejected");
        finding.Evidence.Should().Contain("HTTP 401");
    }

    [Fact]
    public void Findings_MissingSecurityHeadersAndWildcardCorsWithCredentials()
    {
        var check = Check("REST API", 200, 90, new Dictionary<string, string> { ["access-control-allow-origin"] = "*", ["access-control-allow-credentials"] = "true", ["cache-control"] = "public, max-age=60" });

        var findings = FrontendQualityAuthenticatedApiSurfaceFindings.Build(Surface(check), new(), true, true);

        findings.Select(f => f.SourceRuleId).Should().BeEquivalentTo(["auth-api-hsts", "auth-api-xcto", "auth-api-cors", "auth-api-cache"]);
        findings.Single(f => f.SourceRuleId == "auth-api-cors").Severity.Should().Be(FrontendQualitySeverity.High);
        findings.Should().OnlyContain(f => f.EngineId == FrontendQualityEngineId.StaticSecurity && f.SourceSystem == FrontendQualityAuthenticatedApiSurfaceFindings.SourceSystem);
    }

    [Fact]
    public void Findings_HardenedApi_ProducesNoSecurityFindings()
    {
        var check = Check("REST API", 200, 90, new Dictionary<string, string> { ["strict-transport-security"] = "max-age=31536000", ["x-content-type-options"] = "nosniff", ["cache-control"] = "no-store", ["access-control-allow-origin"] = "https://m2lbdev.example.test" });

        FrontendQualityAuthenticatedApiSurfaceFindings.Build(Surface(check), new(), true, true).Should().BeEmpty();
    }

    [Fact]
    public void Findings_SlowAuthenticatedResponse_IsPerformanceFindingOwnedByPassivePerformance()
    {
        var thresholds = new FrontendPerformanceThresholds { MaxSingleRequestLatencyMs = 500 };
        var hardened = new Dictionary<string, string> { ["strict-transport-security"] = "x", ["x-content-type-options"] = "nosniff", ["cache-control"] = "no-store" };

        var findings = FrontendQualityAuthenticatedApiSurfaceFindings.Build(Surface(Check("GraphQL", 200, 1800, hardened)), thresholds, true, true);

        var finding = findings.Should().ContainSingle().Which;
        finding.EngineId.Should().Be(FrontendQualityEngineId.PassivePerformance);
        finding.Category.Should().Be(FrontendQualityCategory.Performance);
        finding.SourceRuleId.Should().Be("auth-api-latency");
        finding.Evidence.Should().Contain("Threshold: 500 ms");
    }

    [Fact]
    public void Findings_InactiveEngine_DoesNotReceiveFindings()
    {
        var slowAndRejected = Check("Health", 401, 3000);

        FrontendQualityAuthenticatedApiSurfaceFindings.Build(Surface(slowAndRejected), new(), securityActive: false, performanceActive: true).Should().BeEmpty("rejected credentials belong to Static Security, which is inactive; a rejected call has no latency finding");
        FrontendQualityAuthenticatedApiSurfaceFindings.Build(Surface(slowAndRejected), new(), securityActive: true, performanceActive: false).Should().ContainSingle();
    }

    [Fact]
    public void Limitations_DescribeExecutedProbesOrTheReasonNothingRan()
    {
        FrontendQualityAuthenticatedApiSurfaceFindings.Limitations(Surface(Check("REST API", 200, 10), Check("GraphQL", 200, 20)))
            .Should().ContainSingle().Which.Should().Contain("2 approved read-only request(s)").And.Contain("REST API HTTP 200").And.Contain("never captured");
        FrontendQualityAuthenticatedApiSurfaceFindings.Limitations(new FrontendAuthenticatedApiSurfaceResult { ContextAvailable = false, NotExecutedReason = "Start the local proxy." })
            .Should().ContainSingle().Which.Should().Be("Authenticated API surface not checked: Start the local proxy.");
    }

    // ── Orchestration ───────────────────────────────────────────────────────────

    private sealed class SurfaceSpy(FrontendAuthenticatedApiSurfaceResult result) : IFrontendAuthenticatedApiSurfaceService
    {
        public List<FrontendAuthenticatedApiSurfaceRequest> Requests { get; } = [];
        public Task<FrontendAuthenticatedApiSurfaceResult> ProbeAsync(FrontendAuthenticatedApiSurfaceRequest request, CancellationToken cancellationToken = default)
        { Requests.Add(request); return Task.FromResult(result); }
    }

    private sealed class Fixture
    {
        public FrontendAnalysisContext Context { get; }
        public FrontendQualityTargetAccessContext Access { get; }
        public Fixture(bool requiresAuth, AuthenticatedTestingMethod method, AuthenticatedApiContextStatus contextStatus)
        {
            var profile = new FrontendAnalysisProfile
            {
                Id = "dev", Name = "M2LB DEV", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = Target, Performance = new(),
                RestBaseUrl = "https://api-dev.example.test/", HealthEndpoint = "https://api-dev.example.test/health", GraphQlEndpoint = "https://api-dev.example.test/graphql",
            };
            profile.Authentication.RequiresAuthentication = requiresAuth;
            profile.Authentication.AuthenticatedTestingMethod = method;
            profile.Features.EnableBrowserRuntimeEngine = false; profile.Features.EnableAccessibilityEngine = false;
            profile.Features.EnableLighthouseEngine = false; profile.Features.EnablePassiveSecurityEngine = false;
            Context = new FrontendAnalysisContext
            {
                ActiveProfile = profile, TargetUrl = Target, RequiresAuthentication = requiresAuth, RestBaseUrl = profile.RestBaseUrl, HealthEndpoint = profile.HealthEndpoint, GraphQlEndpoint = profile.GraphQlEndpoint,
                FeatureToggles = profile.Features, EngineRequirements = profile.EngineRequirements, ReviewEngineSelection = profile.ReviewEngineSelection,
                ReviewIdentity = ReviewAuthenticationIdentity.For(profile),
                AllowedBackendDomains = [], AllowedRestHosts = [], AllowedGraphQlEndpoints = [], AllowedCdnHosts = [], SecuritySettings = new(),
            };
            Access = FrontendQualityTargetAccess.Build(Context, requiresAuth && method == AuthenticatedTestingMethod.LocalHttpsProxy
                ? new AuthenticatedReviewCapabilities { Method = method, ContextStatus = contextStatus, AuthenticatedApi = contextStatus == AuthenticatedApiContextStatus.Available, Reason = "Start the local proxy." }
                : null, false, null, null);
        }

        public FrontendQualityReviewOrchestrator Orchestrator(IFrontendAuthenticatedApiSurfaceService surface) => new(
            new OkSecurity(), new OkPerformance(), new OkPreflight(), new MockQuality(), null, null, null, null, null,
            OrchestrationTestHelpers.CreateAlwaysReadyMockService(), new FixedResolver(Access), surface);
    }

    [Fact]
    public async Task ProxyEnvironmentWithContext_ProbesOnce_FindingsAndProvenanceOnReport()
    {
        var fixture = new Fixture(true, AuthenticatedTestingMethod.LocalHttpsProxy, AuthenticatedApiContextStatus.Available);
        var spy = new SurfaceSpy(Surface(Check("REST API", 200, 90), Check("Health", 401, 40), Check("GraphQL", 200, 2500, new Dictionary<string, string> { ["strict-transport-security"] = "x", ["x-content-type-options"] = "nosniff", ["cache-control"] = "no-store" })));

        var result = await fixture.Orchestrator(spy).RunAsync(Target, fixture.Context);

        spy.Requests.Should().ContainSingle("one shared probe set per review");
        spy.Requests[0].Identity.Method.Should().Be(AuthenticatedTestingMethod.LocalHttpsProxy);
        spy.Requests[0].Identity.ContextFingerprint.Should().Be(fixture.Context.ReviewIdentity!.ContextFingerprint);
        spy.Requests[0].RestBaseUrl.Should().Be("https://api-dev.example.test/");
        var report = result.QualityReport!;
        report.AuthenticatedApiSurface.Should().NotBeNull();
        report.AuthenticatedApiSurface!.ExecutedCount.Should().Be(3);
        report.Findings.Should().Contain(f => f.SourceRuleId == "auth-api-rejected" && f.EngineId == FrontendQualityEngineId.StaticSecurity);
        report.Findings.Should().Contain(f => f.SourceRuleId == "auth-api-latency" && f.EngineId == FrontendQualityEngineId.PassivePerformance);
        report.Limitations.Should().Contain(l => l.Contains("Authenticated API surface checked through the Local HTTPS proxy gateway: 3 approved read-only request(s)"));
        report.EngineOutcomes.Single(o => o.EngineId == FrontendQualityEngineId.StaticSecurity).AccessLabel.Should().Be("Public HTTP (frontend shell) + Authenticated HTTP (Local HTTPS Proxy)");
        report.EngineOutcomes.Single(o => o.EngineId == FrontendQualityEngineId.StaticSecurity).ExecutionState.Should().Be(FrontendQualityEngineExecutionState.Assessed);
        System.Text.Json.JsonSerializer.Serialize(report).Should().NotContainAny("Authorization", "Bearer ", "Cookie", "eyJ");
    }

    [Fact]
    public async Task ProxyEnvironmentWithoutContext_NoProbeExecuted_ReportedHonestly()
    {
        var fixture = new Fixture(true, AuthenticatedTestingMethod.LocalHttpsProxy, AuthenticatedApiContextStatus.WaitingForAuthenticatedTraffic);
        var spy = new SurfaceSpy(new FrontendAuthenticatedApiSurfaceResult { ContextAvailable = false, NotExecutedReason = "Start the local proxy." });

        var result = await fixture.Orchestrator(spy).RunAsync(Target, fixture.Context);

        spy.Requests.Should().ContainSingle("the gateway decides; the review asks once and records the typed answer");
        var report = result.QualityReport!;
        report.AuthenticatedApiSurface!.ContextAvailable.Should().BeFalse();
        report.Findings.Should().NotContain(f => f.SourceSystem == FrontendQualityAuthenticatedApiSurfaceFindings.SourceSystem);
        report.Limitations.Should().Contain("Authenticated API surface not checked: Start the local proxy.");
        report.EngineOutcomes.Single(o => o.EngineId == FrontendQualityEngineId.StaticSecurity).AccessLabel.Should().Be("Public HTTP (frontend shell); authenticated API surface not available");
        report.EngineOutcomes.Single(o => o.EngineId == FrontendQualityEngineId.StaticSecurity).ExecutionState.Should().Be(FrontendQualityEngineExecutionState.Assessed, "the public shell assessment still counts");
    }

    [Theory]
    [InlineData(false, AuthenticatedTestingMethod.LocalHttpsProxy)]
    [InlineData(true, AuthenticatedTestingMethod.ManagedEdgeCdp)]
    [InlineData(true, AuthenticatedTestingMethod.ManualOnly)]
    public async Task PublicTargetOrNonProxyMethod_NeverCallsTheProbe(bool requiresAuth, AuthenticatedTestingMethod method)
    {
        var fixture = new Fixture(requiresAuth, method, AuthenticatedApiContextStatus.NotApplicable);
        var spy = new SurfaceSpy(Surface());

        var result = await fixture.Orchestrator(spy).RunAsync(Target, fixture.Context);

        spy.Requests.Should().BeEmpty();
        result.QualityReport!.AuthenticatedApiSurface.Should().BeNull();
        result.QualityReport.Limitations.Should().NotContain(l => l.Contains("Authenticated API surface"));
    }

    [Fact]
    public async Task HttpEnginesDisabled_ProbeNotRequested()
    {
        var fixture = new Fixture(true, AuthenticatedTestingMethod.LocalHttpsProxy, AuthenticatedApiContextStatus.Available);
        fixture.Context.FeatureToggles.EnableSecurityEngine = false;
        fixture.Context.FeatureToggles.EnablePerformanceEngine = false;
        var spy = new SurfaceSpy(Surface(Check("REST API", 200, 10)));

        var result = await fixture.Orchestrator(spy).RunAsync(Target, fixture.Context);

        spy.Requests.Should().BeEmpty("no active engine consumes the proxy context");
        result.PreflightBlocked.Should().BeTrue();
    }

    // ── UI / export ─────────────────────────────────────────────────────────────

    private static FrontendQualityReviewReport ReportWith(FrontendAuthenticatedApiSurfaceResult surface) => new()
    {
        TargetUrl = Target, GeneratedAt = DateTime.UtcNow, ReleaseDisposition = FrontendQualityReleaseDisposition.NoAutomatedBlockDetected,
        EngineOutcomes = [new FrontendQualityEngineOutcome { EngineId = FrontendQualityEngineId.StaticSecurity, DisplayName = "Static Security", Enabled = true, Requirement = FrontendQualityEngineRequirement.Required, ExecutionState = FrontendQualityEngineExecutionState.Assessed, FindingCount = 0 }],
        AuthenticatedApiSurface = surface,
    };

    [Fact]
    public void DecisionSupport_RendersProbeTable_AndNotCheckedState()
    {
        var executed = Render<FrontendQualityDecisionSupport>(p => p.Add(x => x.Report, ReportWith(Surface(Check("REST API", 200, 90, new Dictionary<string, string> { ["strict-transport-security"] = "max-age=31536000" }), Check("Health", 401, 40)))));
        executed.Find("[data-testid=fqr-auth-api-surface]").TextContent.Should().Contain("Authenticated API surface (Local HTTPS Proxy)");
        executed.FindAll("[data-testid=fqr-auth-api-check]").Should().HaveCount(2);
        executed.Find("[data-testid=fqr-auth-api-check][data-check='Health']").TextContent.Should().Contain("HTTP 401").And.Contain("Authenticated via Local HTTPS Proxy");
        executed.Find("[data-testid=fqr-auth-api-check][data-check='REST API']").TextContent.Should().Contain("strict-transport-security: max-age=31536000").And.Contain("90 ms");

        var notChecked = Render<FrontendQualityDecisionSupport>(p => p.Add(x => x.Report, ReportWith(new FrontendAuthenticatedApiSurfaceResult { ContextAvailable = false, NotExecutedReason = "Start the local proxy." })));
        notChecked.Find("[data-testid=fqr-auth-api-not-executed]").TextContent.Should().Contain("Not checked.").And.Contain("Start the local proxy.");
    }

    [Fact]
    public void Export_IncludesProbeSectionWithoutSecrets()
    {
        var html = new ReportExportService().ExportFrontendQualityReview(ReportWith(Surface(Check("GraphQL", 200, 210, new Dictionary<string, string> { ["x-content-type-options"] = "nosniff" }))), "Project");

        html.Should().Contain("Authenticated API surface (Local HTTPS Proxy)").And.Contain("<td>GraphQL</td>").And.Contain("HTTP 200").And.Contain("210 ms").And.Contain("x-content-type-options: nosniff");
        html.Should().NotContainAny("Authorization", "Bearer ", "Cookie", "eyJ");
        new ReportExportService().ExportFrontendQualityReview(ReportWith(new FrontendAuthenticatedApiSurfaceResult { ContextAvailable = false, NotExecutedReason = "Expired." }), null)
            .Should().Contain("<strong>Not checked.</strong> Expired.");
    }

    // ── Minimal fakes ───────────────────────────────────────────────────────────

    private sealed class OkSecurity : ISecurityScanner
    {
        public Task<(WasmSecurityReviewReport?, string?)> ScanAsync(WasmScanRequest request) => Task.FromResult<(WasmSecurityReviewReport?, string?)>((new WasmSecurityReviewReport { TargetUrl = Target, ScannedAt = DateTime.UtcNow, Assets = [new WasmDiscoveredAsset { Url = Target, AssetType = "HTML", Status = "200 OK", Analyzed = true }] }, null));
    }

    private sealed class OkPerformance : IBlazorWasmPerformanceReviewService
    {
        public Task<WasmPerformanceReviewReport> RunReviewAsync(string targetUrl, FrontendPerformanceThresholds? thresholds = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WasmPerformanceReviewReport { TargetUrl = Target, ReviewedAt = DateTime.UtcNow, Assets = [new DiscoveredAsset { Url = Target, Type = AssetType.Index, StatusCode = 200 }] });
        public Task<WasmAssetDiscoveryResult> DiscoverAssetsAsync(string targetUrl, FrontendPerformanceThresholds? thresholds = null, CancellationToken cancellationToken = default) => Task.FromResult(new WasmAssetDiscoveryResult());
        public WasmPerformanceReviewReport? GetCached() => null;
        public void ClearCache() { }
    }

    private sealed class OkPreflight : ITargetPreflightService
    {
        public Task<TargetPreflightResult> CheckTargetAsync(string targetUrl) => Task.FromResult(new TargetPreflightResult { Status = PreflightStatus.Ready, Message = "Target is reachable (HTTP 200).", ResponseStatusCode = 200 });
    }

    private sealed class MockQuality : IFrontendQualityReviewService
    {
        public FrontendQualityReviewReport BuildReport(string targetUrl, WasmSecurityReviewReport? security, WasmPerformanceReviewReport? performance) => new() { TargetUrl = targetUrl };
    }

    private sealed class FixedResolver(FrontendQualityTargetAccessContext access) : IFrontendQualityTargetAccessResolver
    {
        public Task<FrontendQualityTargetAccessContext> ResolveAsync(FrontendAnalysisContext context, CancellationToken cancellationToken = default) => Task.FromResult(access);
    }
}
