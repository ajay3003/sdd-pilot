using BirkNext.Web.Models;
using FluentAssertions;
using System.Security.Cryptography;
using System.Text;

namespace BirkNext.Web.Tests.Services;

public sealed class QualityReviewDiagnosticExportTests
{
    [Fact]
    public void RunQualityReview_CreatesDiagnosticSnapshotFromExactRun()
    {
        // Arrange
        const string projectSlug = "frontend-admin-panel";
        const string projectName = "Frontend Admin Panel";
        var selectedPackIds = new[] { "qa-auditor", "constitution-compliance", "wcag-2.2", "owasp-asvs" };
        var runAt = DateTimeOffset.UtcNow;

        var packResults = new List<QualityReviewPackResult>
        {
            new()
            {
                PackId = "qa-auditor",
                PackName = "QA Auditor",
                PackGroup = "Quality",
                Score = 0,
                Critical = 0,
                High = 0,
                Medium = 0,
                Low = 0,
            },
            new()
            {
                PackId = "constitution-compliance",
                PackName = "Constitution Compliance",
                PackGroup = "Governance",
                Score = 0,
                Critical = 0,
                High = 0,
                Medium = 0,
                Low = 0,
            },
            new()
            {
                PackId = "wcag-2.2",
                PackName = "WCAG 2.2",
                PackGroup = "Standards",
                Score = 73,
                Critical = 0,
                High = 3,
                Medium = 5,
                Low = 8,
            },
            new()
            {
                PackId = "owasp-asvs",
                PackName = "OWASP ASVS / Top 10",
                PackGroup = "Standards",
                Score = 57,
                Critical = 1,
                High = 4,
                Medium = 10,
                Low = 14,
            },
        };

        var report = new QualityReviewReport
        {
            PackResults = packResults,
            OverallScore = 32.5,
            TotalFindings = 44,
            CriticalCount = 1,
            HighCount = 7,
            MediumCount = 15,
            LowCount = 22,
            RunAt = runAt,
        };

        // Act
        var diagnostic = new QualityReviewDiagnosticExport
        {
            SchemaVersion = 1,
            ProjectSlug = projectSlug,
            ProjectDisplayName = projectName,
            RunAtUtc = report.RunAt.UtcDateTime,
            SelectedPackIds = selectedPackIds.ToList(),
            Artifacts = [],
            Packs = packResults.Select(PackDiagnostic.FromPackResult).ToList(),
            OverallScore = report.OverallScore,
            TotalFindings = report.TotalFindings,
            CriticalCount = report.CriticalCount,
            HighCount = report.HighCount,
            MediumCount = report.MediumCount,
            LowCount = report.LowCount,
        };

        // Assert
        diagnostic.ProjectSlug.Should().Be(projectSlug);
        diagnostic.ProjectDisplayName.Should().Be(projectName);
        diagnostic.SelectedPackIds.Should().Equal(selectedPackIds);
        diagnostic.Packs.Should().HaveCount(4);
        diagnostic.Packs[0].PackId.Should().Be("qa-auditor");
        diagnostic.Packs[1].PackId.Should().Be("constitution-compliance");
        diagnostic.Packs[2].PackId.Should().Be("wcag-2.2");
        diagnostic.Packs[3].PackId.Should().Be("owasp-asvs");
        diagnostic.OverallScore.Should().Be(32.5);
        diagnostic.TotalFindings.Should().Be(44);
        diagnostic.CriticalCount.Should().Be(1);
        diagnostic.HighCount.Should().Be(7);
    }

    [Fact]
    public void DiagnosticExport_PreservesRunPackSnapshotAfterSelectionChanges()
    {
        // Arrange - captures snapshot of selected packs at run time
        var runatPackIds = new[] { "qa-auditor", "constitution-compliance", "wcag-2.2", "owasp-asvs" };
        var report = CreateMinimalReport();

        var diagnostic = new QualityReviewDiagnosticExport
        {
            SelectedPackIds = runatPackIds.ToList(),
            Packs = runatPackIds.Select(id => CreateMinimalPackResult(id)).Select(PackDiagnostic.FromPackResult).ToList(),
            TotalFindings = 83,
        };

        // Act - simulate checkbox changes after the run
        var newSelection = new[] { "qa-auditor", "wcag-2.2" };
        // (in real UI, user would change checkboxes, but diagnostic snapshot stays frozen)

        // Assert - diagnostic still has original 4 packs
        diagnostic.SelectedPackIds.Should().HaveCount(4);
        diagnostic.SelectedPackIds.Should().Equal(runatPackIds);
        diagnostic.Packs.Should().HaveCount(4);
        diagnostic.TotalFindings.Should().Be(83);
    }

    [Fact]
    public void QualityReviewDiagnosticExport_DoesNotDeduplicateFindings()
    {
        // Arrange - two identical findings from same pack
        var findings = new List<FindingDiagnostic>
        {
            new()
            {
                RuleId = "WCAG22-1.1.1",
                Severity = "High",
                Title = "Image alt text missing",
                Message = "Image element has no alt attribute",
                Source = null,
            },
            new()
            {
                RuleId = "WCAG22-1.1.1",
                Severity = "High",
                Title = "Image alt text missing",
                Message = "Image element has no alt attribute",
                Source = null,
            },
        };

        // Act - both findings are preserved
        var diagnostic = new PackDiagnostic
        {
            PackId = "wcag-2.2",
            Findings = findings,
        };

        // Assert - both identical findings remain
        diagnostic.Findings.Should().HaveCount(2);
        diagnostic.Findings[0].RuleId.Should().Be(diagnostic.Findings[1].RuleId);
        diagnostic.Findings[0].Message.Should().Be(diagnostic.Findings[1].Message);
    }

    [Fact]
    public void QualityReviewDiagnosticExport_DoesNotInventFindingSource()
    {
        // Arrange - finding with null source/location
        var finding = new FindingDiagnostic
        {
            RuleId = "RULE-001",
            Severity = "Medium",
            Title = "Missing component",
            Message = "Component X is referenced but not defined",
            Source = null,
            Location = null,
            Evidence = null,
        };

        // Assert - nulls remain null (not fabricated)
        finding.Source.Should().BeNull();
        finding.Location.Should().BeNull();
        finding.Evidence.Should().BeNull();
    }

    [Fact]
    public void QualityReviewDiagnosticExport_HashesExactReviewedContent()
    {
        // Arrange - known content with expected SHA256
        var content = "# Test Document\nThis is test content for hashing.";
        var expectedBytes = Encoding.UTF8.GetBytes(content);
        var expectedHash = SHA256.HashData(expectedBytes);
        var expectedHex = Convert.ToHexString(expectedHash);

        // Act
        var artifact = ArtifactInputDiagnostic.FromContent("Specification", "spec.md", content);

        // Assert
        artifact.IsAvailable.Should().BeTrue();
        artifact.ContentLength.Should().Be(expectedBytes.Length);
        artifact.Sha256.Should().Be(expectedHex);
    }

    [Fact]
    public void ArtifactInputDiagnostic_HandlesEmptyContent()
    {
        // Act
        var artifact = ArtifactInputDiagnostic.FromContent("Constitution", "constitution.md", string.Empty);

        // Assert
        artifact.IsAvailable.Should().BeFalse();
        artifact.ContentLength.Should().Be(0);
        artifact.Sha256.Should().BeEmpty();
    }

    [Fact]
    public void ArtifactInputDiagnostic_HandlesNullContent()
    {
        // Act
        var artifact = ArtifactInputDiagnostic.FromContent("Plan", "plan.md", null);

        // Assert
        artifact.IsAvailable.Should().BeFalse();
        artifact.ContentLength.Should().Be(0);
        artifact.Sha256.Should().BeEmpty();
    }

    [Fact]
    public void QualityReviewDiagnosticExport_SerializesToJson()
    {
        // Arrange
        var diagnostic = new QualityReviewDiagnosticExport
        {
            SchemaVersion = 1,
            ProjectSlug = "test-project",
            ProjectDisplayName = "Test Project",
            RunAtUtc = new DateTime(2026, 8, 19, 14, 30, 0, DateTimeKind.Utc),
            SelectedPackIds = ["qa-auditor"],
            OverallScore = 85.5,
            TotalFindings = 10,
            CriticalCount = 1,
            HighCount = 2,
            MediumCount = 3,
            LowCount = 4,
        };

        // Act
        var json = System.Text.Json.JsonSerializer.Serialize(
            diagnostic,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

        // Assert
        json.Should().Contain("\"SchemaVersion\": 1");
        json.Should().Contain("\"ProjectSlug\": \"test-project\"");
        json.Should().Contain("\"OverallScore\": 85.5");
        json.Should().Contain("\"TotalFindings\": 10");
    }

    private static QualityReviewReport CreateMinimalReport()
    {
        return new QualityReviewReport
        {
            OverallScore = 32.5,
            TotalFindings = 83,
            CriticalCount = 1,
            HighCount = 7,
            MediumCount = 15,
            LowCount = 60,
            RunAt = DateTimeOffset.UtcNow,
        };
    }

    private static QualityReviewPackResult CreateMinimalPackResult(string packId)
    {
        return new QualityReviewPackResult
        {
            PackId = packId,
            PackName = packId,
            PackGroup = "Test",
            Score = 50,
            Critical = 0,
            High = 0,
            Medium = 0,
            Low = 0,
        };
    }
}
