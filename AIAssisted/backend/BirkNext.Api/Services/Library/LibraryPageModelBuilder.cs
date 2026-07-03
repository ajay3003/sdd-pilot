using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BirkNext.Api.Services.Library;

/// <summary>
/// Builds page models for QA Artifact Library.
/// Shows artifacts currently loaded in WorkspaceArtifactRepository.
/// </summary>
public class QAArtifactLibraryPageModelBuilder : IQAArtifactLibraryPageModelBuilder
{
    private readonly AppDbContext _db;
    private readonly IWorkspaceArtifactStatusService _artifactStatus;
    private readonly ILogger<QAArtifactLibraryPageModelBuilder> _logger;

    public QAArtifactLibraryPageModelBuilder(
        AppDbContext db,
        IWorkspaceArtifactStatusService artifactStatus,
        ILogger<QAArtifactLibraryPageModelBuilder> logger)
    {
        _db = db;
        _artifactStatus = artifactStatus;
        _logger = logger;
    }

    public async Task<LibraryPageModel> BuildPageModelAsync()
    {
        try
        {
            var workspaceId = await GetCurrentWorkspaceIdAsync();
            if (workspaceId == Guid.Empty)
            {
                return new LibraryPageModel
                {
                    Title = "QA Artifact Library",
                    Description = "View, replace, or clear artifacts in your workspace.",
                    ReadinessStatus = LibraryStatus.Empty,
                    RequiredInputs = [],
                    MissingInputs = [],
                    Summary = new LibrarySummary
                    {
                        StatusMessage = "No artifacts loaded yet. Import or create artifacts to begin.",
                        TotalItems = 0,
                        EmptyCount = 1,
                        HasAvailableActions = false
                    }
                };
            }

            var artifactStatus = await _artifactStatus.GetStatusAsync(workspaceId);
            var artifacts = await _db.SavedWorkspaceArtifacts
                .Where(a => a.WorkspaceId == workspaceId)
                .ToListAsync();

            var items = new List<LibraryItem>();
            var sections = new List<LibrarySection>();

            // Build section for loaded artifacts
            var loadedSection = new LibrarySection
            {
                Name = "loaded",
                Title = "Loaded Artifacts",
                Description = "Artifacts currently in your workspace"
            };

            foreach (var artifact in artifacts)
            {
                var item = new LibraryItem
                {
                    Name = artifact.ArtifactType.ToString(),
                    Type = "Artifact",
                    Status = LibraryStatus.Ready,
                    Source = "Workspace",
                    ArtifactKind = artifact.ArtifactType.ToString(),
                    LastUpdated = artifact.UpdatedAt.DateTime,
                    Actions = [
                        new LibraryAction
                        {
                            Name = "View",
                            Status = LibraryStatus.Ready,
                            Enabled = true,
                            ExpectedEffect = "Open artifact viewer"
                        },
                        new LibraryAction
                        {
                            Name = "Replace",
                            Status = LibraryStatus.Ready,
                            Enabled = true,
                            ExpectedEffect = "Replace with new artifact version"
                        },
                        new LibraryAction
                        {
                            Name = "Clear",
                            Status = LibraryStatus.Ready,
                            Enabled = true,
                            ExpectedEffect = "Remove artifact from workspace"
                        }
                    ]
                };
                loadedSection.Items.Add(item);
                items.Add(item);
            }

            if (loadedSection.Items.Count == 0)
            {
                loadedSection.Items.Add(new LibraryItem
                {
                    Name = "Empty",
                    Type = "Placeholder",
                    Status = LibraryStatus.Empty,
                    Description = "No artifacts loaded yet"
                });
            }

            sections.Add(loadedSection);

            return new LibraryPageModel
            {
                Title = "QA Artifact Library",
                Description = "View, replace, or clear artifacts in your workspace.",
                ReadinessStatus = artifacts.Count > 0 ? LibraryStatus.Ready : LibraryStatus.Empty,
                Sections = sections,
                Items = items,
                RequiredInputs = [],
                MissingInputs = [],
                Summary = new LibrarySummary
                {
                    StatusMessage = artifacts.Count > 0
                        ? $"You have {artifacts.Count} artifact(s) loaded"
                        : "No artifacts loaded yet. Import or create artifacts to begin.",
                    TotalItems = artifacts.Count,
                    AvailableActionsCount = artifacts.Count > 0 ? artifacts.Count * 3 : 0,
                    HasAvailableActions = artifacts.Count > 0
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building QA Artifact Library page model");
            return ErrorModel("QA Artifact Library", "Failed to load artifacts: " + ex.Message);
        }
    }

    private async Task<Guid> GetCurrentWorkspaceIdAsync()
    {
        var workspace = await _db.SavedWorkspaces
            .Where(w => !w.IsDeleted)
            .OrderByDescending(w => w.UpdatedAt)
            .FirstOrDefaultAsync();
        return workspace?.Id ?? Guid.Empty;
    }

    private static LibraryPageModel ErrorModel(string title, string message)
    {
        return new LibraryPageModel
        {
            Title = title,
            Description = "Library page",
            ReadinessStatus = LibraryStatus.Fail,
            RequiredInputs = [],
            MissingInputs = [],
            Summary = new LibrarySummary
            {
                StatusMessage = message,
                HasAvailableActions = false
            }
        };
    }
}

/// <summary>
/// Builds page models for Create Test Scenario.
/// Requires ReviewContext or relevant artifacts to exist.
/// </summary>
public class CreateTestScenarioPageModelBuilder : ICreateTestScenarioPageModelBuilder
{
    private readonly AppDbContext _db;
    private readonly IWorkspaceArtifactStatusService _artifactStatus;
    private readonly ILogger<CreateTestScenarioPageModelBuilder> _logger;

    public CreateTestScenarioPageModelBuilder(
        AppDbContext db,
        IWorkspaceArtifactStatusService artifactStatus,
        ILogger<CreateTestScenarioPageModelBuilder> logger)
    {
        _db = db;
        _artifactStatus = artifactStatus;
        _logger = logger;
    }

    public async Task<LibraryPageModel> BuildPageModelAsync()
    {
        try
        {
            var workspaceId = await GetCurrentWorkspaceIdAsync();
            if (workspaceId == Guid.Empty)
            {
                return EmptyModel(
                    "No active workspace yet. Create or load a workspace first, then load artifacts.");
            }

            var artifactStatus = await _artifactStatus.GetStatusAsync(workspaceId);

            var missingInputs = new List<string>();
            if (!artifactStatus.HasSpecification && !artifactStatus.HasPlan && !artifactStatus.HasTasks)
            {
                missingInputs.Add("at least one artifact (specification, plan, or tasks)");
            }

            if (missingInputs.Count > 0)
            {
                return BlockedModel(
                    missingInputs,
                    $"Missing: {string.Join(", ", missingInputs)}");
            }

            return new LibraryPageModel
            {
                Title = "Create Test Scenario",
                Description = "Create structured test scenarios linked to your requirements.",
                ReadinessStatus = LibraryStatus.Ready,
                RequiredInputs = ["Specification or Tasks or Plan"],
                MissingInputs = [],
                Actions = [
                    new LibraryAction
                    {
                        Name = "Create New Scenario",
                        Status = LibraryStatus.Ready,
                        Enabled = true,
                        ExpectedEffect = "Open scenario creation form"
                    }
                ],
                Summary = new LibrarySummary
                {
                    StatusMessage = "Ready to create test scenarios",
                    HasAvailableActions = true,
                    AvailableActionsCount = 1
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building Create Test Scenario page model");
            return ErrorModel("Create Test Scenario", "Failed to build page model: " + ex.Message);
        }
    }

    private async Task<Guid> GetCurrentWorkspaceIdAsync()
    {
        var workspace = await _db.SavedWorkspaces
            .Where(w => !w.IsDeleted)
            .OrderByDescending(w => w.UpdatedAt)
            .FirstOrDefaultAsync();
        return workspace?.Id ?? Guid.Empty;
    }

    private static LibraryPageModel EmptyModel(string statusMessage)
    {
        return new LibraryPageModel
        {
            Title = "Create Test Scenario",
            Description = "Create structured test scenarios linked to your requirements.",
            ReadinessStatus = LibraryStatus.Empty,
            RequiredInputs = ["Workspace", "Specification or Tasks or Plan"],
            MissingInputs = ["active workspace"],
            Summary = new LibrarySummary
            {
                StatusMessage = statusMessage,
                HasAvailableActions = false
            }
        };
    }

    private static LibraryPageModel BlockedModel(List<string> missingInputs, string statusMessage)
    {
        return new LibraryPageModel
        {
            Title = "Create Test Scenario",
            Description = "Create structured test scenarios linked to your requirements.",
            ReadinessStatus = LibraryStatus.Blocked,
            RequiredInputs = ["Specification or Tasks or Plan"],
            MissingInputs = missingInputs,
            Summary = new LibrarySummary
            {
                StatusMessage = statusMessage,
                HasAvailableActions = false
            }
        };
    }

    private static LibraryPageModel ErrorModel(string title, string message)
    {
        return new LibraryPageModel
        {
            Title = title,
            Description = "Library page",
            ReadinessStatus = LibraryStatus.Fail,
            RequiredInputs = [],
            MissingInputs = [],
            Summary = new LibrarySummary
            {
                StatusMessage = message,
                HasAvailableActions = false
            }
        };
    }
}

/// <summary>
/// Builds page models for Sample Projects.
/// Shows available sample projects that can be loaded into workspace.
/// </summary>
public class SampleProjectsPageModelBuilder : ISampleProjectsPageModelBuilder
{
    private readonly AppDbContext _db;
    private readonly ILogger<SampleProjectsPageModelBuilder> _logger;

    public SampleProjectsPageModelBuilder(
        AppDbContext db,
        ILogger<SampleProjectsPageModelBuilder> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<LibraryPageModel> BuildPageModelAsync()
    {
        try
        {
            await Task.CompletedTask;

            // For now, provide sample project definitions
            var sampleProjects = new List<LibraryItem>
            {
                new()
                {
                    Name = "E-Commerce Platform",
                    Type = "Sample Project",
                    Status = LibraryStatus.Ready,
                    Source = "Built-in",
                    Description = "Complete e-commerce system with specification, plan, and tasks",
                    Actions = [
                        new LibraryAction
                        {
                            Name = "Load",
                            Status = LibraryStatus.Ready,
                            Enabled = true,
                            ExpectedEffect = "Load all sample project artifacts into workspace"
                        }
                    ]
                },
                new()
                {
                    Name = "Mobile App MVP",
                    Type = "Sample Project",
                    Status = LibraryStatus.Ready,
                    Source = "Built-in",
                    Description = "Minimal viable product mobile application with core requirements and test plan",
                    Actions = [
                        new LibraryAction
                        {
                            Name = "Load",
                            Status = LibraryStatus.Ready,
                            Enabled = true,
                            ExpectedEffect = "Load all sample project artifacts into workspace"
                        }
                    ]
                },
                new()
                {
                    Name = "Data Pipeline ETL",
                    Type = "Sample Project",
                    Status = LibraryStatus.Ready,
                    Source = "Built-in",
                    Description = "Data extraction, transformation, and loading system with data model and compliance requirements",
                    Actions = [
                        new LibraryAction
                        {
                            Name = "Load",
                            Status = LibraryStatus.Ready,
                            Enabled = true,
                            ExpectedEffect = "Load all sample project artifacts into workspace"
                        }
                    ]
                }
            };

            if (sampleProjects.Count == 0)
            {
                return new LibraryPageModel
                {
                    Title = "Sample Projects",
                    Description = "Load pre-built sample projects to explore BirkNext features and learn best practices.",
                    ReadinessStatus = LibraryStatus.Empty,
                    Items = [],
                    RequiredInputs = [],
                    MissingInputs = [],
                    Summary = new LibrarySummary
                    {
                        StatusMessage = "No sample projects available yet",
                        TotalItems = 0,
                        AvailableActionsCount = 0,
                        HasAvailableActions = false
                    }
                };
            }

            return new LibraryPageModel
            {
                Title = "Sample Projects",
                Description = "Load pre-built sample projects to explore BirkNext features and learn best practices.",
                ReadinessStatus = LibraryStatus.Ready,
                Items = sampleProjects,
                RequiredInputs = [],
                MissingInputs = [],
                Summary = new LibrarySummary
                {
                    StatusMessage = $"Choose from {sampleProjects.Count} sample projects to load into your workspace",
                    TotalItems = sampleProjects.Count,
                    AvailableActionsCount = sampleProjects.Count,
                    HasAvailableActions = true
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building Sample Projects page model");
            return ErrorModel("Sample Projects", "Failed to load sample projects: " + ex.Message);
        }
    }

    private static LibraryPageModel ErrorModel(string title, string message)
    {
        return new LibraryPageModel
        {
            Title = title,
            Description = "Library page",
            ReadinessStatus = LibraryStatus.Fail,
            RequiredInputs = [],
            MissingInputs = [],
            Summary = new LibrarySummary
            {
                StatusMessage = message,
                HasAvailableActions = false
            }
        };
    }
}
