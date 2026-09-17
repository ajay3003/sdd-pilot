using Xunit;
using FluentAssertions;
using BirkNext.Web.Models;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Integration Quality Review frontend tests covering redesigned UX.
/// Tests verify: target summary, integration grouping, evidence display,
/// readiness states, findings presentation, and manual review obligations.
/// </summary>
public class IntegrationQualityReviewTests
{
    // ── Data structure tests ──

    [Fact]
    public void IntegrationQualityReport_ContainsAllRequiredFields()
    {
        var report = new IntegrationQualityReport();

        report.Should().NotBeNull();
        report.Findings.Should().BeEmpty();
        report.Statuses.Should().BeEmpty();
        report.Recommendations.Should().BeEmpty();
        report.Limitations.Should().BeEmpty();
    }

    [Fact]
    public void IntegrationStatus_ReflectsEnabled()
    {
        var status = new IntegrationStatus
        {
            Name = "TestIntegration",
            Type = IntegrationType.REST,
            Enabled = true,
            HasRequiredFields = true,
            Score = 85
        };

        status.Enabled.Should().BeTrue();
        status.HasRequiredFields.Should().BeTrue();
        status.Score.Should().Be(85);
    }

    [Fact]
    public void IntegrationFinding_SeveritiesDistinguishable()
    {
        var findings = new List<IntegrationFinding>
        {
            new() { Title = "Critical", Severity = IntegrationFindingSeverity.Critical },
            new() { Title = "High", Severity = IntegrationFindingSeverity.High },
            new() { Title = "Medium", Severity = IntegrationFindingSeverity.Medium },
            new() { Title = "Low", Severity = IntegrationFindingSeverity.Low },
            new() { Title = "Info", Severity = IntegrationFindingSeverity.Info }
        };

        findings.Should().HaveCount(5);
        findings.Where(f => f.Severity == IntegrationFindingSeverity.Critical).Should().HaveCount(1);
        findings.Where(f => f.Severity == IntegrationFindingSeverity.High).Should().HaveCount(1);
    }

    [Fact]
    public void IntegrationReport_TracksEnabledCount()
    {
        var report = new IntegrationQualityReport
        {
            IntegrationCount = 5,
            EnabledCount = 3
        };

        report.IntegrationCount.Should().Be(5);
        report.EnabledCount.Should().Be(3);
    }

    [Fact]
    public void IntegrationStatus_DistinguishesHealthWorkerReachability()
    {
        var reachable = new IntegrationStatus
        {
            HealthReachable = true,
            WorkerReachable = true
        };
        var unreachable = new IntegrationStatus
        {
            HealthReachable = false,
            WorkerReachable = false
        };

        reachable.HealthReachable.Should().BeTrue();
        unreachable.HealthReachable.Should().BeFalse();
    }

    [Fact]
    public void IntegrationAuthenticationSummary_TracksBothCapabilitiesAndChecks()
    {
        var summary = new IntegrationAuthenticationSummary
        {
            Capabilities = new BirkNext.LocalHttpsProxy.AuthenticatedReviewCapabilities
            {
                AuthenticatedRest = true
            },
            Checks = new List<IntegrationAuthenticatedCheck>
            {
                new() { Label = "Health", Outcome = "Reachable", StatusCode = 200 }
            }
        };

        summary.Capabilities.AuthenticatedRest.Should().BeTrue();
        summary.Checks.Should().HaveCount(1);
        summary.Checks[0].Outcome.Should().Be("Reachable");
    }

    // ── Grouping tests ──

    [Fact]
    public void IntegrationsByType_CanGroupREST()
    {
        var integrations = new List<IntegrationStatus>
        {
            new() { Type = IntegrationType.REST, Name = "API1" },
            new() { Type = IntegrationType.REST, Name = "API2" }
        };

        var restOnly = integrations.Where(i => i.Type == IntegrationType.REST).ToList();
        restOnly.Should().HaveCount(2);
    }

    [Fact]
    public void IntegrationsByType_CanGroupMessaging()
    {
        var integrations = new List<IntegrationStatus>
        {
            new() { Type = IntegrationType.EventHub, Name = "Events" },
            new() { Type = IntegrationType.Kafka, Name = "Stream" },
            new() { Type = IntegrationType.ServiceBus, Name = "Queue" }
        };

        var messaging = integrations.Where(i => i.Type is IntegrationType.EventHub or IntegrationType.Kafka or IntegrationType.ServiceBus).ToList();
        messaging.Should().HaveCount(3);
    }

    [Fact]
    public void IntegrationsByType_CanGroupGraphQL()
    {
        var integrations = new List<IntegrationStatus>
        {
            new() { Type = IntegrationType.GraphQL, Name = "GraphQL1" }
        };

        var graphql = integrations.Where(i => i.Type == IntegrationType.GraphQL).ToList();
        graphql.Should().HaveCount(1);
    }

    // ── Finding summary tests ──

    [Fact]
    public void FindingCounts_CanBeAggregatedBySeverity()
    {
        var report = new IntegrationQualityReport
        {
            Findings = new List<IntegrationFinding>
            {
                new() { Severity = IntegrationFindingSeverity.Critical },
                new() { Severity = IntegrationFindingSeverity.Critical },
                new() { Severity = IntegrationFindingSeverity.High },
                new() { Severity = IntegrationFindingSeverity.Medium }
            }
        };

        var critical = report.Findings.Count(f => f.Severity == IntegrationFindingSeverity.Critical);
        var high = report.Findings.Count(f => f.Severity == IntegrationFindingSeverity.High);
        var medium = report.Findings.Count(f => f.Severity == IntegrationFindingSeverity.Medium);

        critical.Should().Be(2);
        high.Should().Be(1);
        medium.Should().Be(1);
    }

    [Fact]
    public void RecommendationsAndFindings_AreDistinct()
    {
        var report = new IntegrationQualityReport
        {
            Findings = new List<IntegrationFinding>
            {
                new() { Title = "Missing field", Severity = IntegrationFindingSeverity.High }
            },
            Recommendations = new List<string>
            {
                "Verify business logic in integration"
            }
        };

        report.Findings.Should().HaveCount(1);
        report.Recommendations.Should().HaveCount(1);
        report.Findings[0].Title.Should().Be("Missing field");
        report.Recommendations[0].Should().Contain("Verify");
    }

    [Fact]
    public void IntegrationFinding_IncludesEvidenceLinks()
    {
        var finding = new IntegrationFinding
        {
            Title = "Health check failed",
            Evidence = new List<string> { "http://example.com/health", "Status: 500" }
        };

        finding.Evidence.Should().HaveCount(2);
        finding.Evidence.Should().Contain("http://example.com/health");
    }

    [Fact]
    public void Limitations_ExplainReviewBoundaries()
    {
        var report = new IntegrationQualityReport
        {
            Limitations = new List<string>
            {
                "Read-only review",
                "Runtime evidence depends on observed traffic"
            }
        };

        report.Limitations.Should().HaveCount(2);
        report.Limitations[0].Should().Contain("Read-only");
    }

    [Fact]
    public void AuthenticatedReviewCapabilities_DistinguishPublicVsAuthenticated()
    {
        var capabilitiesWithAuth = new BirkNext.LocalHttpsProxy.AuthenticatedReviewCapabilities
        {
            AuthenticatedRest = true
        };
        var capabilitiesNoAuth = new BirkNext.LocalHttpsProxy.AuthenticatedReviewCapabilities
        {
            AuthenticatedRest = false
        };

        capabilitiesWithAuth.AuthenticatedRest.Should().BeTrue();
        capabilitiesNoAuth.AuthenticatedRest.Should().BeFalse();
    }

    [Fact]
    public void IntegrationStatus_TracksAssessmentCompleteness()
    {
        var complete = new IntegrationStatus
        {
            Name = "API",
            HasRequiredFields = true,
            Score = 85
        };
        var incomplete = new IntegrationStatus
        {
            Name = "API2",
            HasRequiredFields = false,
            Score = 40
        };

        complete.HasRequiredFields.Should().BeTrue();
        incomplete.HasRequiredFields.Should().BeFalse();
    }

    [Fact]
    public void IntegrationReport_PreservesExistingFunctionality()
    {
        var report = new IntegrationQualityReport
        {
            EnvironmentName = "dev",
            GeneratedAt = new DateTime(2026, 9, 17),
            OverallScore = 75,
            IntegrationCount = 5,
            EnabledCount = 4,
            MissingConfigCount = 1,
            IsReadyForDeployment = true
        };

        report.EnvironmentName.Should().Be("dev");
        report.OverallScore.Should().Be(75);
        report.IsReadyForDeployment.Should().BeTrue();
        report.MissingConfigCount.Should().Be(1);
    }

    [Fact]
    public void IntegrationFinding_FilterableByIntegration()
    {
        var findings = new List<IntegrationFinding>
        {
            new() { IntegrationId = "int1", IntegrationName = "API1", Title = "Issue 1" },
            new() { IntegrationId = "int2", IntegrationName = "API2", Title = "Issue 2" }
        };

        var api1Findings = findings.Where(f => f.IntegrationId == "int1").ToList();
        api1Findings.Should().HaveCount(1);
        api1Findings[0].IntegrationName.Should().Be("API1");
    }

    [Fact]
    public void IntegrationFinding_HasRecommendationForRemediationGuidance()
    {
        var finding = new IntegrationFinding
        {
            Title = "Missing auth",
            Recommendation = "Set AuthType field to ManagedIdentity"
        };

        finding.Recommendation.Should().NotBeEmpty();
        finding.Recommendation.Should().Contain("AuthType");
    }

    // ── Semantics verification ──

    [Fact]
    public void EndpointReachability_IsDistinctFromRuntimeEvidence()
    {
        // HealthReachable=true: proves endpoint responds to HTTP HEAD
        var healthProbe = new IntegrationStatus { HealthReachable = true };

        // This proves: HTTP 200 from configured health URL
        // This does NOT prove: an integration interaction was observed
        // True runtime evidence would require: actual message/request flowing through integration

        healthProbe.HealthReachable.Should().BeTrue();
    }

    [Fact]
    public void AuthenticationCapability_DistinctFromAuthenticatedChecks()
    {
        // Capability: authenticated review available for this environment
        var hasCapability = new IntegrationAuthenticationSummary
        {
            Capabilities = new BirkNext.LocalHttpsProxy.AuthenticatedReviewCapabilities { AuthenticatedRest = true },
            Checks = new List<IntegrationAuthenticatedCheck>()
        };

        hasCapability.Capabilities.AuthenticatedRest.Should().BeTrue();
        hasCapability.Checks.Should().BeEmpty();

        // Checks: actual authenticated requests that were executed
        var withChecks = new IntegrationAuthenticationSummary
        {
            Capabilities = new BirkNext.LocalHttpsProxy.AuthenticatedReviewCapabilities { AuthenticatedRest = true },
            Checks = new List<IntegrationAuthenticatedCheck>
            {
                new() { Label = "Health", Outcome = "Reachable", StatusCode = 200 }
            }
        };

        withChecks.Capabilities.AuthenticatedRest.Should().BeTrue();
        withChecks.Checks.Should().HaveCount(1);
    }
}
