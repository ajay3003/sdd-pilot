using System.Text.Json;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

public sealed class FrontendQualityLogicalIssueGroupingTests
{
    [Fact]
    public void Csp_ExactStaticAndZapRules_GroupWithoutChangingSources()
    {
        var findings = new List<FrontendQualityFinding>
        {
            Finding("static-csp", FrontendQualityEngineId.StaticSecurity, "HDR-MISSING-CONTENT-SECURITY-POLICY", "Static wording", FrontendQualitySeverity.Medium),
            Finding("zap-csp", FrontendQualityEngineId.PassiveSecurity, "10038", "ZAP wording", FrontendQualitySeverity.High),
        };
        var before = JsonSerializer.Serialize(findings);

        var issue = FrontendQualityLogicalIssueGrouper.Group(findings).Should().ContainSingle().Subject;

        issue.LogicalId.Should().Be("headers:csp:missing");
        issue.Sources.Should().BeEquivalentTo(new[] { FrontendQualityEngineId.StaticSecurity, FrontendQualityEngineId.PassiveSecurity });
        issue.FindingInstances.Select(instance => instance.SourceFindingId).Should().BeEquivalentTo("static-csp", "zap-csp");
        issue.PrimarySeverity.Should().Be(FrontendQualitySeverity.High);
        issue.Confidence.Should().Be(FrontendQualityEvidenceConfidence.High);
        JsonSerializer.Serialize(findings).Should().Be(before);
    }

    [Fact]
    public void Nosniff_ExactStaticRuleAndZap10021_Group()
    {
        var issues = FrontendQualityLogicalIssueGrouper.Group(
        [
            Finding("static-nosniff", FrontendQualityEngineId.StaticSecurity, "HDR-MISSING-X-CONTENT-TYPE-OPTIONS", "First title"),
            Finding("zap-nosniff", FrontendQualityEngineId.PassiveSecurity, "10021", "Missing X-Content-Type-Options"),
        ]);

        issues.Should().ContainSingle().Which.LogicalId.Should().Be("headers:nosniff:missing");
        issues[0].FindingInstances.Should().HaveCount(2);
    }

    [Fact]
    public void CspAndNosniff_RemainDifferentLogicalIssues()
    {
        var issues = FrontendQualityLogicalIssueGrouper.Group(
        [
            Finding("csp", FrontendQualityEngineId.StaticSecurity, "std-csp-missing", "Same"),
            Finding("nosniff", FrontendQualityEngineId.StaticSecurity, "HDR-MISSING-X-CONTENT-TYPE-OPTIONS", "Same"),
        ]);

        issues.Should().HaveCount(2);
        issues.SelectMany(issue => issue.FindingInstances).Should().HaveCount(2);
    }

    [Fact]
    public void UnknownFinding_RemainsStandaloneWithoutInventedConfidence()
    {
        var issue = FrontendQualityLogicalIssueGrouper.Group(
            [Finding("unknown-1", FrontendQualityEngineId.Accessibility, "axe-unknown", "Unknown")])
            .Should().ContainSingle().Subject;

        issue.LogicalId.Should().StartWith("finding:Accessibility:unknown-1:");
        issue.FindingInstances.Should().ContainSingle().Which.SourceFindingId.Should().Be("unknown-1");
        issue.Confidence.Should().BeNull();
    }

    [Fact]
    public void SameTitleDifferentRules_DoNotGroup()
    {
        var issues = FrontendQualityLogicalIssueGrouper.Group(
        [
            Finding("one", FrontendQualityEngineId.StaticSecurity, "not-csp", "Identical title"),
            Finding("two", FrontendQualityEngineId.PassiveSecurity, "99999", "Identical title"),
        ]);

        issues.Should().HaveCount(2);
    }

    [Fact]
    public void DifferentTitlesSameRegisteredRules_Group()
    {
        var issues = FrontendQualityLogicalIssueGrouper.Group(
        [
            Finding("one", FrontendQualityEngineId.StaticSecurity, "HDR-MISSING-CONTENT-SECURITY-POLICY", "Completely unrelated display A"),
            Finding("two", FrontendQualityEngineId.PassiveSecurity, "10038", "Completely unrelated display B"),
        ]);

        issues.Should().ContainSingle().Which.CanonicalTitle.Should().Be("Content Security Policy header missing");
    }

    [Fact]
    public void SameEngineRepeatedRegisteredRule_RemainsSeparate()
    {
        var issues = FrontendQualityLogicalIssueGrouper.Group(
        [
            Finding("response-a", FrontendQualityEngineId.PassiveSecurity, "10038", "CSP A"),
            Finding("response-b", FrontendQualityEngineId.PassiveSecurity, "10038", "CSP B"),
        ]);

        issues.Should().HaveCount(2);
        issues.Should().OnlyContain(issue => issue.FindingInstances.Count == 1);
    }

    [Fact]
    public void GroupedEvidenceAndOriginalSeverities_ArePreserved()
    {
        var findings = new List<FrontendQualityFinding>
        {
            Finding("one", FrontendQualityEngineId.StaticSecurity, "HDR-MISSING-CONTENT-SECURITY-POLICY", "A", FrontendQualitySeverity.Medium, "static evidence"),
            Finding("two", FrontendQualityEngineId.PassiveSecurity, "10038", "B", FrontendQualitySeverity.High, "tool evidence"),
        };

        var issue = FrontendQualityLogicalIssueGrouper.Group(findings).Single();

        issue.PrimarySeverity.Should().Be(FrontendQualitySeverity.High);
        issue.EvidenceStrength.Should().Be(FrontendQualityEvidenceStrength.ToolDiagnostic);
        issue.FindingInstances.Select(instance => instance.Severity).Should().Equal(FrontendQualitySeverity.Medium, FrontendQualitySeverity.High);
        issue.FindingInstances.SelectMany(instance => instance.SanitizedEvidence).Should().BeEquivalentTo("static evidence", "tool evidence");
        findings.Select(finding => finding.Severity).Should().Equal(FrontendQualitySeverity.Medium, FrontendQualitySeverity.High);
    }

    [Fact]
    public void ManualDisposition_IsPreservedDeterministically()
    {
        var finding = Finding("manual", FrontendQualityEngineId.Accessibility, "color-contrast", "Manual").WithStatus(CheckExecutionStatus.NotAssessed);

        var issue = FrontendQualityLogicalIssueGrouper.Group([finding]).Single();

        issue.ReviewDisposition.Should().Be(FrontendQualityReviewDisposition.ManualVerificationRequired);
        issue.ManualVerificationRequired.Should().BeTrue();
    }

    [Fact]
    public void GroupingTwice_IsStructurallyIdempotent()
    {
        var findings = new List<FrontendQualityFinding>
        {
            Finding("one", FrontendQualityEngineId.StaticSecurity, "HDR-MISSING-CONTENT-SECURITY-POLICY", "A"),
            Finding("two", FrontendQualityEngineId.PassiveSecurity, "10038", "B"),
            Finding("three", FrontendQualityEngineId.Lighthouse, "unused", "C"),
        };

        var first = FrontendQualityLogicalIssueGrouper.Group(findings);
        var second = FrontendQualityLogicalIssueGrouper.Group(findings);

        JsonSerializer.Serialize(second).Should().Be(JsonSerializer.Serialize(first));
        second.SelectMany(issue => issue.FindingInstances).Should().HaveCount(findings.Count);
    }

    [Fact]
    public void SerializationAndSession_PreserveStableGroupedIssue()
    {
        var findings = new List<FrontendQualityFinding>
        {
            Finding("static-csp", FrontendQualityEngineId.StaticSecurity, "HDR-MISSING-CONTENT-SECURITY-POLICY", "A"),
            Finding("zap-csp", FrontendQualityEngineId.PassiveSecurity, "10038", "B", FrontendQualitySeverity.High),
        };
        var report = new FrontendQualityReviewReport
        {
            TargetUrl = "https://example.com",
            GeneratedAt = DateTime.UtcNow,
            Findings = findings,
            LogicalIssues = FrontendQualityLogicalIssueGrouper.Group(findings),
        };
        var roundTrip = JsonSerializer.Deserialize<FrontendQualityReviewReport>(JsonSerializer.Serialize(report))!;
        var session = new RuntimeReviewSessionService();
        session.SaveQualityResult(roundTrip, new FrontendAnalysisContext { TargetUrl = report.TargetUrl });

        var saved = session.QualityReview.Report!.LogicalIssues.Single();
        saved.LogicalId.Should().Be("headers:csp:missing");
        saved.Sources.Should().HaveCount(2);
        saved.FindingInstances.Select(instance => instance.SourceFindingId).Should().Equal("static-csp", "zap-csp");
        saved.PrimarySeverity.Should().Be(FrontendQualitySeverity.High);
        saved.EvidenceStrength.Should().Be(FrontendQualityEvidenceStrength.ToolDiagnostic);
        saved.Confidence.Should().Be(FrontendQualityEvidenceConfidence.High);
    }

    [Fact]
    public void ApprovedSanitization_RemainsEffectiveThroughGroupingAndSerialization()
    {
        const string sentinel = "SECRET-PHASE2E-GROUPING-12345";
        var sanitized = ReportExportService.SanitizePassive(sentinel);
        var findings = new List<FrontendQualityFinding>
        {
            Finding("one", FrontendQualityEngineId.StaticSecurity, "HDR-MISSING-CONTENT-SECURITY-POLICY", "A", evidence: sanitized),
            Finding("two", FrontendQualityEngineId.PassiveSecurity, "10038", "B", evidence: sanitized),
        };
        var report = new FrontendQualityReviewReport { Findings = findings, LogicalIssues = FrontendQualityLogicalIssueGrouper.Group(findings) };

        JsonSerializer.Serialize(report).Should().NotContain(sentinel).And.Contain("REDACTED");
    }

    [Fact]
    public void Grouping_DoesNotChangeCoverageOutcomesDispositionScoresOrSourceCount()
    {
        var outcomes = Enum.GetValues<FrontendQualityEngineId>().Select(id => new FrontendQualityEngineOutcome
        {
            EngineId = id,
            DisplayName = id.ToString(),
            Enabled = true,
            Requirement = id is FrontendQualityEngineId.StaticSecurity or FrontendQualityEngineId.PassivePerformance
                ? FrontendQualityEngineRequirement.Required : FrontendQualityEngineRequirement.Optional,
            ExecutionState = FrontendQualityEngineExecutionState.Assessed,
        }).ToList();
        var findings = Enum.GetValues<FrontendQualityEngineId>()
            .Select((id, index) => Finding($"finding-{index}", id, $"rule-{index}", id.ToString())).ToList();
        var coverage = FrontendQualityCoverage.Evaluate(outcomes);
        var issues = FrontendQualityLogicalIssueGrouper.Group(findings);

        issues.SelectMany(issue => issue.FindingInstances).Should().HaveCount(findings.Count);
        outcomes.Should().HaveCount(6).And.OnlyContain(outcome => outcome.ExecutionState == FrontendQualityEngineExecutionState.Assessed);
        coverage.RequiredCoverageState.Should().Be(FrontendQualityRequiredCoverageState.AllRequiredAssessed);
        typeof(FrontendQualityLogicalIssueGrouper).GetConstructors().Should().BeEmpty("grouping is pure and cannot invoke engine services");
    }

    private static FrontendQualityFinding Finding(
        string id,
        FrontendQualityEngineId engineId,
        string ruleId,
        string title,
        FrontendQualitySeverity severity = FrontendQualitySeverity.Medium,
        string evidence = "evidence") => new()
        {
            Id = id,
            EngineId = engineId,
            SourceRuleId = ruleId,
            SourceSystem = engineId.ToString(),
            Title = title,
            Severity = severity,
            Category = FrontendQualityCategory.Security,
            Description = "description",
            Recommendation = "recommendation",
            Evidence = [evidence],
            Status = CheckExecutionStatus.Failed,
        };
}

file static class FindingTestExtensions
{
    public static FrontendQualityFinding WithStatus(this FrontendQualityFinding finding, CheckExecutionStatus status) => new()
    {
        Id = finding.Id,
        EngineId = finding.EngineId,
        SourceRuleId = finding.SourceRuleId,
        SourceSystem = finding.SourceSystem,
        Title = finding.Title,
        Severity = finding.Severity,
        Category = finding.Category,
        Description = finding.Description,
        Recommendation = finding.Recommendation,
        Evidence = finding.Evidence,
        Status = status,
    };
}
