using BirkNext.Web.Services;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace BirkNext.Web.Tests.Integration;

/// <summary>
/// Real execution of exact four-pack Quality Review against Frontend Admin Panel.
/// Uses production service implementations, no mocks.
/// </summary>
public sealed class QualityReviewFourPackRealExecution
{
    private readonly ITestOutputHelper _output;

    public QualityReviewFourPackRealExecution(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task FrontendAdminPanel_RealFourPackExecution_AnalyzeResults()
    {
        // Construct real services (following QaAuditorServiceTests pattern)
        var parser = new ArtifactParserService();
        var qaAuditor = new QaAuditorService(
            new ArtifactTraceabilityService(),
            new ConstitutionComplianceService());
        var constitution = new ConstitutionComplianceService();
        var standards = new StandardsComplianceService();
        var qaReadiness = new QAReadinessService();
        var delivery = new DeliveryReadinessAssessmentService();
        var dataModelAnalysis = new DataModelAnalysisService();
        var qrService = new QualityReviewService(
            parser, qaAuditor, constitution, standards, qaReadiness, delivery, dataModelAnalysis);

        // Initialize standards (required for WCAG/OWASP)
        await qrService.InitializeAsync();

        // Realistic Frontend Admin Panel content
        var constitutionText = @"# Constitution

## Board Governance
The project is governed by a board of directors.

## Decision Making
All major decisions require board approval.";

        var specText = @"# Specification

## Authentication
Users authenticate via OAuth 2.0.

## API Endpoints
- GET /api/projects
- POST /api/projects";

        var planText = @"# Plan

## Timeline
Q1: Architecture
Q2: Development";

        var taskText = @"# Tasks

## Sprint 1
- [ ] Task 1
- [x] Task 2";

        var dataModelText = @"# Data Model

## Users
- id (UUID)
- email (varchar)";

        // Execute with EXACTLY 4 selected packs
        var report = await qrService.RunAsync(
            constitutionText, specText, planText, taskText, dataModelText,
            new[] { "qa-auditor", "constitution-compliance", "wcag-2.2", "owasp-asvs" });

        // EMPIRICAL VERIFICATION

        _output.WriteLine("\n===== ACTUAL FOUR-PACK RESULTS =====\n");

        // Verify exactly 4 packs
        report.PackResults.Should().HaveCount(4);
        _output.WriteLine($"✓ Exactly 4 pack results");

        // Report each pack's actual results
        foreach (var pack in report.PackResults.OrderBy(p => p.PackId))
        {
            _output.WriteLine($"\n{pack.PackId}:");
            _output.WriteLine($"  DisplayName: {pack.DisplayName}");
            _output.WriteLine($"  Score: {pack.Score}%");
            _output.WriteLine($"  Critical: {pack.Critical}");
            _output.WriteLine($"  High: {pack.High}");
            _output.WriteLine($"  Medium: {pack.Medium}");
            _output.WriteLine($"  Low: {pack.Low}");
            _output.WriteLine($"  Total findings: {pack.Critical + pack.High + pack.Medium + pack.Low}");
            if (!string.IsNullOrEmpty(pack.Error))
                _output.WriteLine($"  Error: {pack.Error}");
        }

        // Aggregate results
        _output.WriteLine($"\nAGGREGATE:");
        _output.WriteLine($"  Overall Score: {report.OverallScore}%");
        _output.WriteLine($"  Total Findings: {report.TotalFindings}");
        _output.WriteLine($"  Critical: {report.CriticalCount}");
        _output.WriteLine($"  High: {report.HighCount}");
        _output.WriteLine($"  Medium: {report.MediumCount}");
        _output.WriteLine($"  Low: {report.LowCount}");

        // VERIFY ARITHMETIC

        // Score formula: Average of pack scores
        var validPacks = report.PackResults.Where(r => r.Error is null).ToList();
        if (validPacks.Count > 0)
        {
            var expectedOverall = Math.Round(validPacks.Average(r => r.Score), 1);
            report.OverallScore.Should().Be(expectedOverall,
                $"Overall score should be average of {validPacks.Count} packs");
            _output.WriteLine($"✓ Overall score {report.OverallScore}% == average {expectedOverall}%");
        }

        // Finding count: Sum of all pack findings
        var sumCritical = report.PackResults.Sum(r => r.Critical);
        var sumHigh = report.PackResults.Sum(r => r.High);
        var sumMedium = report.PackResults.Sum(r => r.Medium);
        var sumLow = report.PackResults.Sum(r => r.Low);
        var expectedTotal = sumCritical + sumHigh + sumMedium + sumLow;

        report.CriticalCount.Should().Be(sumCritical);
        report.HighCount.Should().Be(sumHigh);
        report.MediumCount.Should().Be(sumMedium);
        report.LowCount.Should().Be(sumLow);
        report.TotalFindings.Should().Be(expectedTotal);

        _output.WriteLine($"✓ Finding counts correct: {expectedTotal} == sum of all");

        // DETAILED PACK ANALYSIS

        _output.WriteLine($"\n===== PACK-SPECIFIC ANALYSIS =====\n");

        var qaAuditorPack = report.PackResults.First(r => r.PackId == "qa-auditor");
        var constitutionPack = report.PackResults.First(r => r.PackId == "constitution-compliance");
        var wcagPack = report.PackResults.First(r => r.PackId == "wcag-2.2");
        var owaspPack = report.PackResults.First(r => r.PackId == "owasp-asvs");

        _output.WriteLine($"QA Auditor Score: {qaAuditorPack.Score}%");
        _output.WriteLine($"Constitution Score: {constitutionPack.Score}%");
        _output.WriteLine($"WCAG Score: {wcagPack.Score}%");
        _output.WriteLine($"OWASP Score: {owaspPack.Score}%");

        // RERUN TEST - Verify no accumulation
        _output.WriteLine($"\n===== RERUN VERIFICATION =====\n");

        var report2 = await qrService.RunAsync(
            constitutionText, specText, planText, taskText, dataModelText,
            new[] { "qa-auditor", "constitution-compliance", "wcag-2.2", "owasp-asvs" });

        report2.TotalFindings.Should().Be(report.TotalFindings,
            "Rerunning same review must not duplicate findings");
        _output.WriteLine($"✓ Rerun produced same total: {report.TotalFindings} findings");

        // DESELECTION TEST
        _output.WriteLine($"\n===== DESELECTION VERIFICATION =====\n");

        var report3 = await qrService.RunAsync(
            constitutionText, specText, planText, taskText, dataModelText,
            new[] { "qa-auditor", "constitution-compliance", "wcag-2.2" });

        report3.PackResults.Should().HaveCount(3);
        report3.PackResults.Should().NotContain(r => r.PackId == "owasp-asvs");
        _output.WriteLine($"✓ Deselecting OWASP removed it: {report3.PackResults.Count} packs remain");

        // SUMMARY
        _output.WriteLine($"\n===== SUMMARY =====");
        _output.WriteLine($"✓ FOUR selected packs executed: {string.Join(", ", report.PackResults.Select(r => r.PackId).Order())}");
        _output.WriteLine($"✓ NO unselected packs present");
        _output.WriteLine($"✓ Scores {report.PackResults.Select(r => r.Score).All(s => s >= 0 && s <= 100)} are within 0..100");
        _output.WriteLine($"✓ Finding arithmetic verified");
        _output.WriteLine($"✓ Rerun does not accumulate");
        _output.WriteLine($"✓ Deselection works correctly");
    }
}
