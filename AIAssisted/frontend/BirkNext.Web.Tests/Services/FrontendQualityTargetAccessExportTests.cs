using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>HTML export mirrors the access-aware engine table: assessment, access, explicit state, precise reason, target access context.</summary>
public sealed class FrontendQualityTargetAccessExportTests
{
    private static FrontendQualityReviewReport Report()
    {
        var policy = new FrontendQualityEngineRequirementSettings().ToPolicy();
        var security = FrontendQualityEngineOutcomeNormalizer.StaticSecurity("https://m2lbdev.example.test/", true, policy,
            new WasmSecurityReviewReport { TargetUrl = "https://m2lbdev.example.test/", ScannedAt = DateTime.UtcNow, Findings = [], Assets = [new WasmDiscoveredAsset { Url = "https://m2lbdev.example.test/", AssetType = "HTML", Status = "200 OK", Analyzed = true }] }, null)
            with { AccessKind = FrontendQualityEngineAccessKind.PublicHttp, AccessLabel = "Public HTTP (frontend shell)" };
        var performance = FrontendQualityEngineOutcomeNormalizer.PassivePerformance("https://m2lbdev.example.test/", true, policy,
            new WasmPerformanceReviewReport { TargetUrl = "https://m2lbdev.example.test/", ReviewedAt = DateTime.UtcNow, Assets = [new DiscoveredAsset { Url = "https://m2lbdev.example.test/", Type = AssetType.Index, StatusCode = 0, Error = "Request timed out" }] }, null)
            with { AccessKind = FrontendQualityEngineAccessKind.PublicHttp, AccessLabel = "Public HTTP (frontend shell)" };
        var runtime = new FrontendQualityEngineOutcome
        {
            EngineId = FrontendQualityEngineId.BrowserRuntime, DisplayName = "Browser Runtime", Enabled = true, Requirement = FrontendQualityEngineRequirement.Optional,
            ExecutionState = FrontendQualityEngineExecutionState.NotApplicable, OutcomeReason = FrontendQualityEngineOutcomeReason.BrowserDomUnavailableForMethod,
            SanitizedFailureReason = FrontendQualityTargetAccess.ProxyDomUnavailable, AccessKind = FrontendQualityEngineAccessKind.AuthenticatedBrowserSession,
            AccessLabel = "Authenticated browser session", RequiredAction = "Switch to Managed Edge (CDP) for DOM checks", ActionHref = FrontendQualityTargetAccess.TargetEnvironmentsHref,
        };
        var outcomes = new List<FrontendQualityEngineOutcome> { security, performance, runtime };
        return new FrontendQualityReviewReport
        {
            TargetUrl = "https://m2lbdev.example.test/", GeneratedAt = DateTime.UtcNow, EngineOutcomes = outcomes,
            Coverage = FrontendQualityCoverage.Evaluate(outcomes), ReleaseDisposition = FrontendQualityReleaseDisposition.Blocked,
            TargetAccess = new FrontendQualityTargetAccessContext
            {
                EnvironmentName = "M2LB DEV", EnvironmentType = "Development", TargetUrl = "https://m2lbdev.example.test/", RequiresAuthentication = true,
                AuthenticationType = FrontendAuthenticationType.MicrosoftEntraId, Method = AuthenticatedTestingMethod.LocalHttpsProxy,
                Mode = FrontendQualityTargetAccessMode.LocalHttpsProxy, ApiContextStatus = AuthenticatedApiContextStatus.Available, AuthenticatedApiAvailable = true,
                ManualVerificationStatus = ManualAuthenticationVerificationStatus.Passed, Reason = "Authenticated via Local HTTPS Proxy (memory only).",
            },
        };
    }

    [Fact]
    public void HtmlExport_ShowsAccessAssessmentStateAndPreciseReasons()
    {
        var html = new ReportExportService().ExportFrontendQualityReview(Report(), "Project");

        html.Should().Contain("<th>Assessment</th>").And.Contain("<th>Access</th>").And.Contain("<th>Reason / required action</th>");
        html.Should().Contain("Completed — no findings").And.Contain("Public HTTP (frontend shell)");
        html.Should().Contain("Timed out").And.Contain("Target did not respond within timeout period.");
        html.Should().Contain("Unsupported").And.Contain("Authenticated DOM unavailable with Local HTTPS Proxy").And.Contain("Required action: Switch to Managed Edge (CDP) for DOM checks.");
        html.Should().Contain("1 of 2 required engines completed.");
        html.Should().Contain("Required engine could not assess the target (1 of 2 required engines completed)");
    }

    [Fact]
    public void HtmlExport_IncludesTargetAccessContextWithoutSecrets()
    {
        var html = new ReportExportService().ExportFrontendQualityReview(Report(), null);

        html.Should().Contain("Target environment access").And.Contain("M2LB DEV").And.Contain("Local HTTPS proxy")
            .And.Contain("Available — memory only").And.Contain("Not available with Local HTTPS Proxy")
            .And.Contain("Manual verification:").And.Contain("Passed").And.Contain("Automated engine access:").And.Contain("no DOM");
        html.Should().NotContainAny("Authorization", "Bearer ", "Cookie", "Set-Cookie", "eyJ");
    }

    [Fact]
    public void HtmlExport_WithoutTargetAccess_StillRenders()
    {
        var report = Report();
        var stripped = new FrontendQualityReviewReport { TargetUrl = report.TargetUrl, GeneratedAt = report.GeneratedAt, EngineOutcomes = report.EngineOutcomes, Coverage = report.Coverage, ReleaseDisposition = report.ReleaseDisposition };

        var html = new ReportExportService().ExportFrontendQualityReview(stripped, null);

        html.Should().NotContain("Target environment access").And.Contain("Automated coverage");
    }
}
