using System.Text.Json;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

public sealed class FrontendQualityExplicitScopeTests
{
    [Theory]
    [InlineData(FrontendReviewAccessScope.PublicOnly, true, "Not included in this review")]
    [InlineData(FrontendReviewAccessScope.AuthenticatedOnly, true, "Included — testing context ready")]
    [InlineData(FrontendReviewAccessScope.PublicAndAuthenticated, true, "Included — testing context ready")]
    [InlineData(FrontendReviewAccessScope.AuthenticatedOnly, false, "Required but unavailable")]
    [InlineData(FrontendReviewAccessScope.PublicAndAuthenticated, false, "Requested but unavailable")]
    public void InclusionDoesNotChangeManualVerification(FrontendReviewAccessScope selected, bool ready, string label)
    {
        var context = new FrontendAnalysisContext { TargetUrl = "https://example.test" }.ForReviewScope(selected);
        var access = new FrontendQualityTargetAccessContext
        {
            ReviewScope = selected, RequiresAuthentication = selected != FrontendReviewAccessScope.PublicOnly,
            AuthenticationType = FrontendAuthenticationType.MicrosoftEntraId,
            Method = AuthenticatedTestingMethod.LocalHttpsProxy, AuthenticatedApiAvailable = ready,
            ManualVerificationStatus = ManualAuthenticationVerificationStatus.Required
        };
        var scope = FrontendQualityReviewScopes.Resolve(context, access,
            [new(FrontendQualityEngineId.StaticSecurity, "Static Security", FrontendQualityEngineRequirement.Required, FrontendQualityCapabilityState.Enabled, null, null)]);
        FrontendQualityReviewScopes.AuthenticatedAccessLabel(scope).Should().Be(label);
        FrontendQualityReviewScopes.ScopeSummary(scope).Should().Be(FrontendQualityReviewScopes.Label(selected));
        FrontendQualityReviewScopes.VerificationLabel(access).Should().Be("Manual verification required");
        if (selected == FrontendReviewAccessScope.PublicOnly) FrontendQualityReviewScopes.LimitationSentence(scope).Should().BeNull();
        else if (ready) FrontendQualityReviewScopes.LimitationSentence(scope).Should().Contain("manual verification");
    }

    [Fact]
    public void ScopeRoundTripsWithoutChangingAuthenticationOrOldHistory()
    {
        var profile = new FrontendAnalysisProfile { FrontendReviewScope = FrontendReviewAccessScope.PublicAndAuthenticated };
        profile.Authentication.AuthenticationType = FrontendAuthenticationType.MicrosoftEntraId;
        var restored = JsonSerializer.Deserialize<FrontendAnalysisProfile>(JsonSerializer.Serialize(profile))!;
        restored.FrontendReviewScope.Should().Be(FrontendReviewAccessScope.PublicAndAuthenticated);
        restored.Authentication.RequiresAuthentication.Should().BeFalse();
        JsonSerializer.Deserialize<FrontendAnalysisProfile>("{}")!.FrontendReviewScope.Should().BeNull();
        var history = new FrontendQualityReviewReport { TargetAccess = new() { ReviewScope = FrontendReviewAccessScope.PublicOnly } };
        var saved = JsonSerializer.Serialize(history);
        profile.FrontendReviewScope = FrontendReviewAccessScope.AuthenticatedOnly;
        FrontendQualityReviewScopes.Executed(JsonSerializer.Deserialize<FrontendQualityReviewReport>(saved)!).Configured.Should().Be(FrontendReviewAccessScope.PublicOnly);
    }

    [Fact]
    public void CoverageSnapshotContainsNoContextCredentials()
    {
        var context = new FrontendAnalysisContext { SessionId = "secret-session" };
        context.ApiAuth.BearerToken = "secret-bearer";
        context.ApiAuth.BasicPassword = "secret-password";
        var access = FrontendQualityTargetAccess.FromContext(context.ForReviewScope(FrontendReviewAccessScope.PublicAndAuthenticated));
        var report = new FrontendQualityReviewReport { TargetAccess = access };
        var snapshot = report with { CoveragePasses = [new(FrontendReviewAccessScope.AuthenticatedOnly, report)] };
        JsonSerializer.Serialize(snapshot).Should().NotContainAny("secret-session", "secret-bearer", "secret-password");
    }

    [Fact]
    public void ExportUsesHistoricalScopeAndModeProvenance()
    {
        var access = new FrontendQualityTargetAccessContext
        {
            ReviewScope = FrontendReviewAccessScope.PublicAndAuthenticated, RequiresAuthentication = true,
            AuthenticatedApiAvailable = true, ManualVerificationStatus = ManualAuthenticationVerificationStatus.Required
        };
        var report = new FrontendQualityReviewReport { TargetAccess = access };
        report = report with { CoveragePasses = [new(FrontendReviewAccessScope.PublicOnly, report), new(FrontendReviewAccessScope.AuthenticatedOnly, report)] };
        var html = new ReportExportService().ExportFrontendQualityReview(report, "Test");
        html.Should().Contain("Public + authenticated").And.Contain("Manual verification required")
            .And.Contain("Testing context at run time:").And.Contain("Authenticated coverage:").And.Contain("Not run");
    }

    [Fact]
    public void GroupedFindingRetainsBothModeInstances()
    {
        var findings = new[] { FrontendReviewAccessScope.PublicOnly, FrontendReviewAccessScope.AuthenticatedOnly }
            .Select(mode => new FrontendQualityFinding { Id = "same", Title = "Missing CSP", SourceRuleId = "csp", EngineId = FrontendQualityEngineId.StaticSecurity, CoverageMode = mode }).ToList();
        FrontendQualityLogicalIssueGrouper.Group(findings).SelectMany(i => i.FindingInstances).Select(i => i.CoverageMode)
            .Should().Contain(FrontendReviewAccessScope.PublicOnly).And.Contain(FrontendReviewAccessScope.AuthenticatedOnly);
    }
}
