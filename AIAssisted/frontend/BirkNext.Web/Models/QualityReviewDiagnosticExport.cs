using System.Security.Cryptography;
using System.Text;

namespace BirkNext.Web.Models;

public sealed class QualityReviewDiagnosticExport
{
    public int SchemaVersion { get; init; } = 1;
    public string ProjectSlug { get; init; } = string.Empty;
    public string ProjectDisplayName { get; init; } = string.Empty;
    public DateTime RunAtUtc { get; init; }
    public List<string> SelectedPackIds { get; init; } = [];
    public List<ArtifactInputDiagnostic> Artifacts { get; init; } = [];
    public List<PackDiagnostic> Packs { get; init; } = [];
    public double OverallScore { get; init; }
    public int TotalFindings { get; init; }
    public int CriticalCount { get; init; }
    public int HighCount { get; init; }
    public int MediumCount { get; init; }
    public int LowCount { get; init; }
}

public sealed class ArtifactInputDiagnostic
{
    public string ArtifactType { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public bool IsAvailable { get; init; }
    public long ContentLength { get; init; }
    public string Sha256 { get; init; } = string.Empty;

    public static ArtifactInputDiagnostic FromContent(string artifactType, string fileName, string? content)
    {
        if (string.IsNullOrEmpty(content))
            return new()
            {
                ArtifactType = artifactType,
                FileName = fileName,
                IsAvailable = false,
                ContentLength = 0,
                Sha256 = string.Empty,
            };

        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        var sha256Hex = Convert.ToHexString(hash);

        return new()
        {
            ArtifactType = artifactType,
            FileName = fileName,
            IsAvailable = true,
            ContentLength = bytes.Length,
            Sha256 = sha256Hex,
        };
    }
}

public sealed class PackDiagnostic
{
    public string PackId { get; init; } = string.Empty;
    public string PackName { get; init; } = string.Empty;
    public double Score { get; init; }
    public int Critical { get; init; }
    public int High { get; init; }
    public int Medium { get; init; }
    public int Low { get; init; }
    public int Info { get; init; }
    public string? Error { get; init; }
    public List<FindingDiagnostic> Findings { get; init; } = [];

    public static PackDiagnostic FromPackResult(QualityReviewPackResult result)
    {
        var diag = new PackDiagnostic
        {
            PackId = result.PackId,
            PackName = result.PackName,
            Score = result.Score,
            Critical = result.Critical,
            High = result.High,
            Medium = result.Medium,
            Low = result.Low,
            Info = result.Info,
            Error = result.Error,
        };

        if (result.Error is null)
        {
            if (result.QaAudit is { } qa)
                diag.Findings.AddRange(qa.Findings.Select(f => FindingDiagnostic.FromQaFinding(f)));
            else if (result.Compliance is { } cc)
            {
                diag.Findings.AddRange(cc.Violations.Select(v => FindingDiagnostic.FromComplianceViolation(v)));
                diag.Findings.AddRange(cc.Gaps.Select(g => FindingDiagnostic.FromComplianceGap(g)));
            }
            else if (result.Standards is { } st)
                diag.Findings.AddRange(st.Results.Select(r => FindingDiagnostic.FromStandardsResult(r)));
            else if (result.QaReadiness is { } qr)
                diag.Findings.AddRange(qr.Gaps.Select(g => FindingDiagnostic.FromQaReadinessGap(g)));
            else if (result.DeliveryReadiness is { } dr)
                diag.Findings.AddRange(dr.Blockers.Select(b => FindingDiagnostic.FromDeliveryBlocker(b)));
            else if (result.DataModel is { } dm)
                diag.Findings.AddRange(dm.Findings.Select(f => FindingDiagnostic.FromDataModelFinding(f)));
        }

        return diag;
    }
}

public sealed class FindingDiagnostic
{
    public string RuleId { get; init; } = string.Empty;
    public string Severity { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string? Source { get; init; }
    public string? Location { get; init; }
    public string? Evidence { get; init; }
    public string? Recommendation { get; init; }

    public static FindingDiagnostic FromQaFinding(QaFinding f) => new()
    {
        RuleId = f.RuleCode,
        Severity = f.Severity.ToString(),
        Title = f.Title,
        Message = f.Description,
        Source = f.AffectedArtifact,
        Location = null,
        Evidence = null,
        Recommendation = null,
    };

    public static FindingDiagnostic FromComplianceViolation(ComplianceViolation v) => new()
    {
        RuleId = v.RuleId,
        Severity = v.Severity.ToString(),
        Title = v.RuleTitle,
        Message = v.Issue,
        Source = v.Artifact.ToString(),
        Location = null,
        Evidence = null,
        Recommendation = null,
    };

    public static FindingDiagnostic FromComplianceGap(ComplianceGap g) => new()
    {
        RuleId = g.RuleId,
        Severity = g.Severity.ToString(),
        Title = g.RuleTitle,
        Message = g.MissingSummary,
        Source = null,
        Location = null,
        Evidence = null,
        Recommendation = null,
    };

    public static FindingDiagnostic FromStandardsResult(StandardCheckResult r) => new()
    {
        RuleId = r.RuleId,
        Severity = r.Severity.ToString(),
        Title = r.Title,
        Message = r.Description,
        Source = null,
        Location = null,
        Evidence = null,
        Recommendation = r.Recommendation,
    };

    public static FindingDiagnostic FromQaReadinessGap(ReadinessGap g) => new()
    {
        RuleId = g.Category,
        Severity = g.Severity.ToString(),
        Title = string.Empty,
        Message = g.Description,
        Source = null,
        Location = null,
        Evidence = null,
        Recommendation = null,
    };

    public static FindingDiagnostic FromDeliveryBlocker(ReadinessBlocker b) => new()
    {
        RuleId = b.RuleCode ?? string.Empty,
        Severity = b.Severity.ToString(),
        Title = b.Title,
        Message = b.Description ?? string.Empty,
        Source = null,
        Location = null,
        Evidence = null,
        Recommendation = null,
    };

    public static FindingDiagnostic FromDataModelFinding(DataModelFinding f) => new()
    {
        RuleId = f.Category,
        Severity = f.Severity.ToString(),
        Title = f.EntityName ?? string.Empty,
        Message = f.Description,
        Source = "data-model.md",
        Location = null,
        Evidence = null,
        Recommendation = null,
    };
}
