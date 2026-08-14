using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BirkNext.Api.Services.Analysis;

/// <summary>
/// Builds structured page models for Spec Drift analysis page.
/// Requires: specification and at least one comparison source/change input.
/// </summary>
public class SpecDriftPageModelBuilder : ISpecDriftPageModelBuilder
{
    private readonly AppDbContext _db;
    private readonly IWorkspaceArtifactStatusService _artifactStatus;
    private readonly ILogger<SpecDriftPageModelBuilder> _logger;

    public SpecDriftPageModelBuilder(
        AppDbContext db,
        IWorkspaceArtifactStatusService artifactStatus,
        ILogger<SpecDriftPageModelBuilder> logger)
    {
        _db = db;
        _artifactStatus = artifactStatus;
        _logger = logger;
    }

    public async Task<AnalysisPageModel> BuildPageModelAsync()
    {
        try
        {
            var workspaceId = await GetCurrentWorkspaceIdAsync();
            if (workspaceId == Guid.Empty)
            {
                return new AnalysisPageModel
                {
                    Title = "Spec Drift",
                    Description = "Track specification changes and drift in coverage over time.",
                    ReadinessStatus = AnalysisStatus.Blocked,
                    RequiredInputs = ["Specification", "Change Input"],
                    MissingInputs = ["Workspace not found"],
                    Summary = new AnalysisSummary
                    {
                        CanRun = false,
                        ReadinessMessage = "No active workspace"
                    }
                };
            }

            var artifactStatus = await _artifactStatus.GetStatusAsync(workspaceId);

            // Spec Drift requires specification
            var missingInputs = new List<string>();
            if (!artifactStatus.HasSpecification)
                missingInputs.Add("specification");

            // Spec Drift requires some comparison source (for now, just requires plan or tasks)
            if (!artifactStatus.HasPlan && !artifactStatus.HasTasks && !artifactStatus.HasConstitution)
                missingInputs.Add("change source (plan, tasks, or constitution)");

            if (missingInputs.Count > 0)
            {
                return new AnalysisPageModel
                {
                    Title = "Spec Drift",
                    Description = "Track specification changes and drift in coverage over time.",
                    ReadinessStatus = AnalysisStatus.Blocked,
                    RequiredInputs = ["Specification", "Change Input (Plan/Tasks/Constitution)"],
                    MissingInputs = missingInputs,
                    Summary = new AnalysisSummary
                    {
                        CanRun = false,
                        ReadinessMessage = $"Missing: {string.Join(", ", missingInputs)}"
                    }
                };
            }

            return new AnalysisPageModel
            {
                Title = "Spec Drift",
                Description = "Track specification changes and drift in coverage over time.",
                ReadinessStatus = AnalysisStatus.Ready,
                RequiredInputs = ["Specification", "Change Input (Plan/Tasks/Constitution)"],
                MissingInputs = [],
                Summary = new AnalysisSummary
                {
                    CanRun = true,
                    ReadinessMessage = "Ready to analyze spec drift"
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building Spec Drift page model");
            return ErrorModel("Spec Drift", "Failed to build analysis page: " + ex.Message);
        }
    }

    private async Task<Guid> GetCurrentWorkspaceIdAsync()
    {
        var workspace = await _db.SavedWorkspaces.FirstOrDefaultAsync(w => w.IsCurrent && !w.IsDeleted);
        return workspace?.Id ?? Guid.Empty;
    }

    private AnalysisPageModel ErrorModel(string title, string message)
    {
        return new AnalysisPageModel
        {
            Title = title,
            Description = "Analysis page",
            ReadinessStatus = AnalysisStatus.Fail,
            RequiredInputs = [],
            MissingInputs = [],
            Summary = new AnalysisSummary
            {
                CanRun = false,
                ReadinessMessage = message
            }
        };
    }
}

/// <summary>
/// Builds page models for Impact Analysis page.
/// Requires: specification and change input.
/// </summary>
public class ImpactAnalysisPageModelBuilder : IImpactAnalysisPageModelBuilder
{
    private readonly AppDbContext _db;
    private readonly IWorkspaceArtifactStatusService _artifactStatus;
    private readonly ILogger<ImpactAnalysisPageModelBuilder> _logger;

    public ImpactAnalysisPageModelBuilder(
        AppDbContext db,
        IWorkspaceArtifactStatusService artifactStatus,
        ILogger<ImpactAnalysisPageModelBuilder> logger)
    {
        _db = db;
        _artifactStatus = artifactStatus;
        _logger = logger;
    }

    public async Task<AnalysisPageModel> BuildPageModelAsync()
    {
        try
        {
            var workspaceId = await GetCurrentWorkspaceIdAsync();
            if (workspaceId == Guid.Empty)
            {
                return AnalysisPageModelSemantics.BlockedNoWorkspaceModel(
                    "Impact Analysis",
                    "Analyze the impact of changes on requirements, tasks, and tests.",
                    ["Workspace", "Specification", "Change Input (Plan/Constitution)"]);
            }

            var artifactStatus = await _artifactStatus.GetStatusAsync(workspaceId);

            var missingInputs = new List<string>();
            if (!artifactStatus.HasSpecification)
                missingInputs.Add("specification");

            // Impact analysis needs change input (plan or constitution typically)
            if (!artifactStatus.HasPlan && !artifactStatus.HasConstitution)
                missingInputs.Add("change input (plan or constitution)");

            if (missingInputs.Count > 0)
            {
                return new AnalysisPageModel
                {
                    Title = "Impact Analysis",
                    Description = "Analyze the impact of changes on requirements, tasks, and tests.",
                    ReadinessStatus = AnalysisStatus.Blocked,
                    RequiredInputs = ["Specification", "Change Input (Plan/Constitution)"],
                    MissingInputs = missingInputs,
                    Summary = new AnalysisSummary
                    {
                        CanRun = false,
                        ReadinessMessage = $"Missing: {string.Join(", ", missingInputs)}"
                    }
                };
            }

            // Optional data missing = Warning, not Blocked
            var warnings = new List<string>();
            if (!artifactStatus.HasTasks)
                warnings.Add("Tasks not loaded - some impact analysis may be limited");
            if (!artifactStatus.HasDataModel)
                warnings.Add("Data model not loaded - entity impact analysis unavailable");

            var status = warnings.Count > 0 ? AnalysisStatus.Warning : AnalysisStatus.Ready;

            return new AnalysisPageModel
            {
                Title = "Impact Analysis",
                Description = "Analyze the impact of changes on requirements, tasks, and tests.",
                ReadinessStatus = status,
                RequiredInputs = ["Specification", "Change Input (Plan/Constitution)"],
                MissingInputs = [],
                Summary = new AnalysisSummary
                {
                    CanRun = true,
                    ReadinessMessage = warnings.Count > 0
                        ? "Ready to analyze with limited scope: " + string.Join(", ", warnings)
                        : "Ready to analyze impact"
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building Impact Analysis page model");
            return ErrorModel("Impact Analysis", "Failed to build analysis page: " + ex.Message);
        }
    }

    private async Task<Guid> GetCurrentWorkspaceIdAsync()
    {
        var workspace = await _db.SavedWorkspaces.FirstOrDefaultAsync(w => w.IsCurrent && !w.IsDeleted);
        return workspace?.Id ?? Guid.Empty;
    }

    private AnalysisPageModel ErrorModel(string title, string message)
    {
        return new AnalysisPageModel
        {
            Title = title,
            Description = "Analysis page",
            ReadinessStatus = AnalysisStatus.Fail,
            RequiredInputs = [],
            MissingInputs = [],
            Summary = new AnalysisSummary
            {
                CanRun = false,
                ReadinessMessage = message
            }
        };
    }
}

/// <summary>
/// Builds page models for Requirements Traceability page.
/// Requires: specification and tasks or plan.
/// </summary>
public class RequirementsTraceabilityPageModelBuilder : IRequirementsTraceabilityPageModelBuilder
{
    private readonly AppDbContext _db;
    private readonly IWorkspaceArtifactStatusService _artifactStatus;
    private readonly ILogger<RequirementsTraceabilityPageModelBuilder> _logger;

    public RequirementsTraceabilityPageModelBuilder(
        AppDbContext db,
        IWorkspaceArtifactStatusService artifactStatus,
        ILogger<RequirementsTraceabilityPageModelBuilder> logger)
    {
        _db = db;
        _artifactStatus = artifactStatus;
        _logger = logger;
    }

    public async Task<AnalysisPageModel> BuildPageModelAsync()
    {
        try
        {
            var workspaceId = await GetCurrentWorkspaceIdAsync();
            if (workspaceId == Guid.Empty)
            {
                return AnalysisPageModelSemantics.BlockedNoWorkspaceModel(
                    "Requirements Traceability",
                    "Track which requirements are covered by tests, tasks, and plan items.",
                    ["Workspace", "Specification", "Tests/Tasks or Plan"]);
            }

            var artifactStatus = await _artifactStatus.GetStatusAsync(workspaceId);

            var missingInputs = new List<string>();
            if (!artifactStatus.HasSpecification)
                missingInputs.Add("specification");

            if (!artifactStatus.HasTasks && !artifactStatus.HasPlan)
                missingInputs.Add("tasks or plan");

            if (missingInputs.Count > 0)
            {
                return new AnalysisPageModel
                {
                    Title = "Requirements Traceability",
                    Description = "Track which requirements are covered by tests, tasks, and plan items.",
                    ReadinessStatus = AnalysisStatus.Blocked,
                    RequiredInputs = ["Specification", "Tests/Tasks or Plan"],
                    MissingInputs = missingInputs,
                    Summary = new AnalysisSummary
                    {
                        CanRun = false,
                        ReadinessMessage = $"Missing: {string.Join(", ", missingInputs)}"
                    }
                };
            }

            return new AnalysisPageModel
            {
                Title = "Requirements Traceability",
                Description = "Track which requirements are covered by tests, tasks, and plan items.",
                ReadinessStatus = AnalysisStatus.Ready,
                RequiredInputs = ["Specification", "Tests/Tasks or Plan"],
                MissingInputs = [],
                Summary = new AnalysisSummary
                {
                    CanRun = true,
                    ReadinessMessage = "Ready to analyze traceability"
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building Requirements Traceability page model");
            return ErrorModel("Requirements Traceability", "Failed to build analysis page: " + ex.Message);
        }
    }

    private async Task<Guid> GetCurrentWorkspaceIdAsync()
    {
        var workspace = await _db.SavedWorkspaces.FirstOrDefaultAsync(w => w.IsCurrent && !w.IsDeleted);
        return workspace?.Id ?? Guid.Empty;
    }

    private AnalysisPageModel ErrorModel(string title, string message)
    {
        return new AnalysisPageModel
        {
            Title = title,
            Description = "Analysis page",
            ReadinessStatus = AnalysisStatus.Fail,
            RequiredInputs = [],
            MissingInputs = [],
            Summary = new AnalysisSummary
            {
                CanRun = false,
                ReadinessMessage = message
            }
        };
    }
}

/// <summary>
/// Builds page models for Implementation Review page.
/// Requires: implementation/code input and workspace context.
/// </summary>
public class ImplementationReviewPageModelBuilder : IImplementationReviewPageModelBuilder
{
    private readonly AppDbContext _db;
    private readonly IWorkspaceArtifactStatusService _artifactStatus;
    private readonly ILogger<ImplementationReviewPageModelBuilder> _logger;

    public ImplementationReviewPageModelBuilder(
        AppDbContext db,
        IWorkspaceArtifactStatusService artifactStatus,
        ILogger<ImplementationReviewPageModelBuilder> logger)
    {
        _db = db;
        _artifactStatus = artifactStatus;
        _logger = logger;
    }

    public async Task<AnalysisPageModel> BuildPageModelAsync()
    {
        try
        {
            var workspaceId = await GetCurrentWorkspaceIdAsync();
            if (workspaceId == Guid.Empty)
            {
                return AnalysisPageModelSemantics.BlockedNoWorkspaceModel(
                    "Implementation Review",
                    "Review implementation code against requirements and design.",
                    ["Workspace", "Implementation/Code Input", "Specification (context)"]);
            }

            var artifactStatus = await _artifactStatus.GetStatusAsync(workspaceId);

            var missingInputs = new List<string>();

            // Implementation review requires code/implementation input
            // For now, we check if specification exists as context
            if (!artifactStatus.HasSpecification)
                missingInputs.Add("specification for context");

            if (missingInputs.Count > 0)
            {
                return new AnalysisPageModel
                {
                    Title = "Implementation Review",
                    Description = "Review implementation code against requirements and design.",
                    ReadinessStatus = AnalysisStatus.Blocked,
                    RequiredInputs = ["Implementation/Code Input", "Specification (context)"],
                    MissingInputs = missingInputs,
                    Summary = new AnalysisSummary
                    {
                        CanRun = false,
                        ReadinessMessage = $"Missing: {string.Join(", ", missingInputs)}"
                    }
                };
            }

            return new AnalysisPageModel
            {
                Title = "Implementation Review",
                Description = "Review implementation code against requirements and design.",
                ReadinessStatus = AnalysisStatus.Ready,
                RequiredInputs = ["Implementation/Code Input", "Specification (context)"],
                MissingInputs = [],
                Summary = new AnalysisSummary
                {
                    CanRun = true,
                    ReadinessMessage = "Ready to review implementation"
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building Implementation Review page model");
            return ErrorModel("Implementation Review", "Failed to build analysis page: " + ex.Message);
        }
    }

    private async Task<Guid> GetCurrentWorkspaceIdAsync()
    {
        var workspace = await _db.SavedWorkspaces.FirstOrDefaultAsync(w => w.IsCurrent && !w.IsDeleted);
        return workspace?.Id ?? Guid.Empty;
    }

    private AnalysisPageModel ErrorModel(string title, string message)
    {
        return new AnalysisPageModel
        {
            Title = title,
            Description = "Analysis page",
            ReadinessStatus = AnalysisStatus.Fail,
            RequiredInputs = [],
            MissingInputs = [],
            Summary = new AnalysisSummary
            {
                CanRun = false,
                ReadinessMessage = message
            }
        };
    }
}

/// <summary>
/// Builds page models for Implementation Traceability page.
/// Requires: implementation/code input plus specification/tasks/plan context.
/// </summary>
public class ImplementationTraceabilityPageModelBuilder : IImplementationTraceabilityPageModelBuilder
{
    private readonly AppDbContext _db;
    private readonly IWorkspaceArtifactStatusService _artifactStatus;
    private readonly ILogger<ImplementationTraceabilityPageModelBuilder> _logger;

    public ImplementationTraceabilityPageModelBuilder(
        AppDbContext db,
        IWorkspaceArtifactStatusService artifactStatus,
        ILogger<ImplementationTraceabilityPageModelBuilder> logger)
    {
        _db = db;
        _artifactStatus = artifactStatus;
        _logger = logger;
    }

    public async Task<AnalysisPageModel> BuildPageModelAsync()
    {
        try
        {
            var workspaceId = await GetCurrentWorkspaceIdAsync();
            if (workspaceId == Guid.Empty)
            {
                return AnalysisPageModelSemantics.BlockedNoWorkspaceModel(
                    "Implementation Traceability",
                    "Trace code changes to requirements, tasks, and plan items.",
                    ["Workspace", "Implementation/Code Input", "Specification", "Tasks or Plan"]);
            }

            var artifactStatus = await _artifactStatus.GetStatusAsync(workspaceId);

            var missingInputs = new List<string>();
            if (!artifactStatus.HasSpecification)
                missingInputs.Add("specification");

            if (!artifactStatus.HasTasks && !artifactStatus.HasPlan)
                missingInputs.Add("tasks or plan");

            if (missingInputs.Count > 0)
            {
                return new AnalysisPageModel
                {
                    Title = "Implementation Traceability",
                    Description = "Trace code changes to requirements, tasks, and plan items.",
                    ReadinessStatus = AnalysisStatus.Blocked,
                    RequiredInputs = ["Implementation/Code Input", "Specification", "Tasks or Plan"],
                    MissingInputs = missingInputs,
                    Summary = new AnalysisSummary
                    {
                        CanRun = false,
                        ReadinessMessage = $"Missing: {string.Join(", ", missingInputs)}"
                    }
                };
            }

            return new AnalysisPageModel
            {
                Title = "Implementation Traceability",
                Description = "Trace code changes to requirements, tasks, and plan items.",
                ReadinessStatus = AnalysisStatus.Ready,
                RequiredInputs = ["Implementation/Code Input", "Specification", "Tasks or Plan"],
                MissingInputs = [],
                Summary = new AnalysisSummary
                {
                    CanRun = true,
                    ReadinessMessage = "Ready to trace implementation"
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building Implementation Traceability page model");
            return ErrorModel("Implementation Traceability", "Failed to build analysis page: " + ex.Message);
        }
    }

    private async Task<Guid> GetCurrentWorkspaceIdAsync()
    {
        var workspace = await _db.SavedWorkspaces.FirstOrDefaultAsync(w => w.IsCurrent && !w.IsDeleted);
        return workspace?.Id ?? Guid.Empty;
    }

    private AnalysisPageModel ErrorModel(string title, string message)
    {
        return new AnalysisPageModel
        {
            Title = title,
            Description = "Analysis page",
            ReadinessStatus = AnalysisStatus.Fail,
            RequiredInputs = [],
            MissingInputs = [],
            Summary = new AnalysisSummary
            {
                CanRun = false,
                ReadinessMessage = message
            }
        };
    }
}

file static class AnalysisPageModelSemantics
{
    public static AnalysisPageModel BlockedNoWorkspaceModel(
        string title,
        string description,
        List<string> requiredInputs) =>
        new()
        {
            Title = title,
            Description = description,
            ReadinessStatus = AnalysisStatus.Blocked,
            RequiredInputs = requiredInputs,
            MissingInputs = ["active workspace"],
            Summary = new AnalysisSummary
            {
                CanRun = false,
                ReadinessMessage = "No active workspace. Create or load a workspace first."
            }
        };
}
