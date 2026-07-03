using BirkNext.Api.Data;
using BirkNext.Api.Services.QualityReview;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services;

/// <summary>
/// Builds the structured page model for Quality Review page.
/// Detects workspace artifacts and determines readiness of each review pack.
/// </summary>
public sealed class QualityReviewPageModelBuilder : IQualityReviewPageModelBuilder_QualityReview
{
    private readonly AppDbContext _db;
    private readonly IWorkspaceArtifactStatusService _artifactStatus;
    private readonly ILogger<QualityReviewPageModelBuilder> _logger;

    public QualityReviewPageModelBuilder(
        AppDbContext db,
        IWorkspaceArtifactStatusService artifactStatus,
        ILogger<QualityReviewPageModelBuilder> logger)
    {
        _db = db;
        _artifactStatus = artifactStatus;
        _logger = logger;
    }

    public async Task<QualityReviewPageModel> BuildPageModelAsync()
    {
        var workspaceId = await GetActiveWorkspaceIdAsync();
        if (workspaceId == Guid.Empty)
        {
            return BuildBlockedModel("No active workspace");
        }

        var artifactStatus = await _artifactStatus.GetStatusAsync(workspaceId);

        // Define all quality review packs and their prerequisites
        var packs = new List<QualityReviewPack>
        {
            BuildQAAuditorPack(artifactStatus),
            BuildDataModelQualityPack(artifactStatus),
            BuildConstitutionCompliancePack(artifactStatus),
            BuildWcagPack(artifactStatus),
            BuildOwasPack(artifactStatus),
            BuildGdprPack(artifactStatus),
            BuildIso25010Pack(artifactStatus),
            BuildQAReadinessPack(artifactStatus),
            BuildDeliveryReadinessPack(artifactStatus)
        };

        // Calculate overall readiness
        var availablePacks = packs.Count(p => p.Status == QualityReviewStatus.Available);
        var blockedPacks = packs.Count(p => p.Status == QualityReviewStatus.Blocked);
        var canRun = availablePacks > 0;

        var model = new QualityReviewPageModel
        {
            Title = "Quality Review",
            Description = "Run one or more deterministic quality, compliance, and readiness reviews in a single execution.",
            Target = "Workspace: " + workspaceId.ToString().Substring(0, 8),
            ReadinessStatus = canRun ? QualityReviewStatus.Available : QualityReviewStatus.Blocked,
            ReviewPacks = packs,
            Checks = ExtractAllChecks(packs),
            Sections = new()
            {
                new QualityReviewSection
                {
                    Title = "Loaded Artifacts",
                    Description = $"You have {artifactStatus.LoadedCount} artifacts loaded in this workspace",
                    Checks = ExtractArtifactChecks(artifactStatus)
                },
                new QualityReviewSection
                {
                    Title = "Available Review Packs",
                    Description = $"{availablePacks} pack(s) available to run immediately",
                    Checks = packs.Where(p => p.Status == QualityReviewStatus.Available)
                        .Select(p => new QualityReviewCheck
                        {
                            Name = p.Name,
                            Category = p.Category,
                            Status = QualityReviewStatus.Available,
                            Description = p.Description
                        })
                        .ToList()
                }
            },
            Summary = new QualityReviewSummary
            {
                TotalPacks = packs.Count,
                AvailablePacks = availablePacks,
                BlockedPacks = blockedPacks,
                CanRun = canRun,
                ReadinessMessage = canRun
                    ? $"Ready to run {availablePacks} pack(s)"
                    : "Load more artifacts to enable quality review packs"
            }
        };

        return model;
    }

    private QualityReviewPack BuildQAAuditorPack(WorkspaceArtifactStatus artifacts)
    {
        var hasSpecification = artifacts.HasSpecification;
        var hasPlan = artifacts.HasPlan;
        var hasTasks = artifacts.HasTasks;

        var canRun = hasSpecification || hasPlan || hasTasks;
        var missing = new List<string>();
        if (!hasSpecification) missing.Add("specification.md");
        if (!hasPlan) missing.Add("plan.md");
        if (!hasTasks) missing.Add("tasks");

        return new QualityReviewPack
        {
            Name = "QA Auditor",
            Category = "QA",
            Status = canRun ? QualityReviewStatus.Available : QualityReviewStatus.Blocked,
            Description = "Comprehensive QA audit checking requirement coverage, test adequacy, and product readiness",
            RequiredInputs = ["specification", "plan", "or tasks"],
            MissingInputs = canRun ? [] : missing
        };
    }

    private QualityReviewPack BuildDataModelQualityPack(WorkspaceArtifactStatus artifacts)
    {
        var hasDataModel = artifacts.HasDataModel;
        return new QualityReviewPack
        {
            Name = "Data Model Quality",
            Category = "Data",
            Status = hasDataModel ? QualityReviewStatus.Available : QualityReviewStatus.Blocked,
            Description = "Analyzes data model design, normalization, and consistency",
            RequiredInputs = ["data-model.md"],
            MissingInputs = hasDataModel ? [] : ["data-model.md"]
        };
    }

    private QualityReviewPack BuildConstitutionCompliancePack(WorkspaceArtifactStatus artifacts)
    {
        var hasConstitution = artifacts.HasConstitution;
        return new QualityReviewPack
        {
            Name = "Constitution Compliance",
            Category = "Compliance",
            Status = hasConstitution ? QualityReviewStatus.Available : QualityReviewStatus.Blocked,
            Description = "Verifies compliance with project constitution and architectural rules",
            RequiredInputs = ["constitution.md"],
            MissingInputs = hasConstitution ? [] : ["constitution.md"]
        };
    }

    private QualityReviewPack BuildWcagPack(WorkspaceArtifactStatus artifacts)
    {
        var hasSpecification = artifacts.HasSpecification;
        return new QualityReviewPack
        {
            Name = "WCAG 2.2 Compliance",
            Category = "Accessibility",
            Status = hasSpecification ? QualityReviewStatus.Available : QualityReviewStatus.Blocked,
            Description = "Checks accessibility compliance against WCAG 2.2 AA standards",
            RequiredInputs = ["specification"],
            MissingInputs = hasSpecification ? [] : ["specification.md"]
        };
    }

    private QualityReviewPack BuildOwasPack(WorkspaceArtifactStatus artifacts)
    {
        var hasSpecification = artifacts.HasSpecification;
        return new QualityReviewPack
        {
            Name = "OWASP ASVS / Top 10",
            Category = "Security",
            Status = hasSpecification ? QualityReviewStatus.Available : QualityReviewStatus.Blocked,
            Description = "Security audit against OWASP Application Security Verification Standard",
            RequiredInputs = ["specification"],
            MissingInputs = hasSpecification ? [] : ["specification.md"]
        };
    }

    private QualityReviewPack BuildGdprPack(WorkspaceArtifactStatus artifacts)
    {
        var hasSpecification = artifacts.HasSpecification;
        return new QualityReviewPack
        {
            Name = "GDPR Compliance",
            Category = "Compliance",
            Status = hasSpecification ? QualityReviewStatus.Available : QualityReviewStatus.Blocked,
            Description = "Ensures GDPR compliance for data handling and privacy",
            RequiredInputs = ["specification"],
            MissingInputs = hasSpecification ? [] : ["specification.md"]
        };
    }

    private QualityReviewPack BuildIso25010Pack(WorkspaceArtifactStatus artifacts)
    {
        var hasSpecification = artifacts.HasSpecification;
        return new QualityReviewPack
        {
            Name = "ISO 25010 Quality",
            Category = "Quality",
            Status = hasSpecification ? QualityReviewStatus.Available : QualityReviewStatus.Blocked,
            Description = "Evaluates product quality against ISO/IEC 25010 standard",
            RequiredInputs = ["specification"],
            MissingInputs = hasSpecification ? [] : ["specification.md"]
        };
    }

    private QualityReviewPack BuildQAReadinessPack(WorkspaceArtifactStatus artifacts)
    {
        var hasSpecification = artifacts.HasSpecification;
        var hasTasks = artifacts.HasTasks;
        var canRun = hasSpecification || hasTasks;
        var missing = new List<string>();
        if (!hasSpecification) missing.Add("specification.md");
        if (!hasTasks) missing.Add("tasks");

        return new QualityReviewPack
        {
            Name = "QA Readiness",
            Category = "Readiness",
            Status = canRun ? QualityReviewStatus.Available : QualityReviewStatus.Blocked,
            Description = "Assesses readiness for QA execution and test coverage",
            RequiredInputs = ["specification", "or tasks"],
            MissingInputs = canRun ? [] : missing
        };
    }

    private QualityReviewPack BuildDeliveryReadinessPack(WorkspaceArtifactStatus artifacts)
    {
        var hasPlan = artifacts.HasPlan;
        var hasTasks = artifacts.HasTasks;
        var canRun = hasPlan || hasTasks;
        var missing = new List<string>();
        if (!hasPlan) missing.Add("plan.md");
        if (!hasTasks) missing.Add("tasks");

        return new QualityReviewPack
        {
            Name = "Delivery Readiness",
            Category = "Readiness",
            Status = canRun ? QualityReviewStatus.Available : QualityReviewStatus.Blocked,
            Description = "Evaluates readiness for production deployment",
            RequiredInputs = ["plan", "or tasks"],
            MissingInputs = canRun ? [] : missing
        };
    }

    private List<QualityReviewCheck> ExtractAllChecks(List<QualityReviewPack> packs)
    {
        return packs.Select(p => new QualityReviewCheck
        {
            Name = p.Name,
            Category = p.Category,
            Status = p.Status,
            Description = p.Description
        }).ToList();
    }

    private List<QualityReviewCheck> ExtractArtifactChecks(WorkspaceArtifactStatus artifacts)
    {
        var checks = new List<QualityReviewCheck>();

        if (artifacts.HasSpecification)
            checks.Add(new QualityReviewCheck
            {
                Name = "Specification",
                Category = "Artifacts",
                Status = QualityReviewStatus.Available,
                Description = "Specification document is loaded"
            });

        if (artifacts.HasPlan)
            checks.Add(new QualityReviewCheck
            {
                Name = "Plan",
                Category = "Artifacts",
                Status = QualityReviewStatus.Available,
                Description = "Plan document is loaded"
            });

        if (artifacts.HasConstitution)
            checks.Add(new QualityReviewCheck
            {
                Name = "Constitution",
                Category = "Artifacts",
                Status = QualityReviewStatus.Available,
                Description = "Constitution document is loaded"
            });

        if (artifacts.HasDataModel)
            checks.Add(new QualityReviewCheck
            {
                Name = "Data Model",
                Category = "Artifacts",
                Status = QualityReviewStatus.Available,
                Description = "Data model document is loaded"
            });

        if (artifacts.HasTasks)
            checks.Add(new QualityReviewCheck
            {
                Name = "Tasks",
                Category = "Artifacts",
                Status = QualityReviewStatus.Available,
                Description = "Tasks are configured"
            });

        if (!checks.Any())
            checks.Add(new QualityReviewCheck
            {
                Name = "No artifacts loaded",
                Category = "Artifacts",
                Status = QualityReviewStatus.Blocked,
                Description = "Load artifacts to enable quality review"
            });

        return checks;
    }

    private QualityReviewPageModel BuildBlockedModel(string reason)
    {
        return new QualityReviewPageModel
        {
            Title = "Quality Review",
            Description = reason,
            ReadinessStatus = QualityReviewStatus.Blocked,
            Summary = new QualityReviewSummary { CanRun = false, ReadinessMessage = reason }
        };
    }

    private async Task<Guid> GetActiveWorkspaceIdAsync()
    {
        // Try to get the most recently updated workspace
        var workspace = await _db.SavedWorkspaces
            .OrderByDescending(w => w.UpdatedAt)
            .FirstOrDefaultAsync();

        return workspace?.Id ?? Guid.Empty;
    }
}
