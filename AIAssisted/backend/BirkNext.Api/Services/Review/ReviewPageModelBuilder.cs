namespace BirkNext.Api.Services.Review;

public class DashboardPageModelBuilder(
    IWorkspaceArtifactStatusService artifactStatus) : IDashboardPageModelBuilder
{
    public async Task<ReviewPageModel> BuildAsync()
    {
        var hasConstitution = await artifactStatus.HasArtifactAsync(WorkspaceArtifactKind.Constitution);
        var hasSpecification = await artifactStatus.HasArtifactAsync(WorkspaceArtifactKind.Specification);
        var hasPlan = await artifactStatus.HasArtifactAsync(WorkspaceArtifactKind.Plan);
        var hasTasks = await artifactStatus.HasArtifactAsync(WorkspaceArtifactKind.Tasks);
        var hasDataModel = await artifactStatus.HasArtifactAsync(WorkspaceArtifactKind.DataModel);

        var loadedCount = new[] { hasConstitution, hasSpecification, hasPlan, hasTasks, hasDataModel }.Count(x => x);
        var isReady = loadedCount == 5;
        var isPartiallyLoaded = loadedCount > 0 && loadedCount < 5;
        var isEmpty = loadedCount == 0;

        var status = isEmpty ? ReviewStatus.Empty : isReady ? ReviewStatus.Ready : ReviewStatus.Warning;
        var results = new List<ReviewResult>();

        if (!isEmpty)
        {
            results.Add(new ReviewResult
            {
                Name = "Workspace Completeness",
                Category = "Status",
                Status = isReady ? ReviewStatus.Ready : ReviewStatus.Warning,
                Severity = isReady ? "Info" : "Warning",
                Summary = $"{loadedCount}/5 artifacts loaded",
                Details = GetArtifactsList(hasConstitution, hasSpecification, hasPlan, hasTasks, hasDataModel)
            });

            if (isPartiallyLoaded)
            {
                var missing = GetMissingArtifacts(hasConstitution, hasSpecification, hasPlan, hasTasks, hasDataModel);
                results.Add(new ReviewResult
                {
                    Name = "Missing Artifacts",
                    Category = "Readiness",
                    Status = ReviewStatus.Warning,
                    Severity = "Warning",
                    Summary = $"{missing.Count} required artifact(s) missing",
                    Recommendation = "Load all artifacts to enable full workspace analysis"
                });
            }
        }

        return new ReviewPageModel
        {
            Title = "SDD Governance Dashboard",
            Description = "Executive overview of workspace status, readiness, and analysis progress",
            ReadinessStatus = status,
            Results = results,
            Summary = new ReviewSummary
            {
                StatusMessage = isEmpty ? "No workspace loaded" : isReady ? $"Workspace complete ({loadedCount}/5)" : $"Workspace partial ({loadedCount}/5)",
                TotalResults = results.Count,
                WarningCount = isPartiallyLoaded ? 1 : 0,
                CanRun = !isEmpty,
                HasAvailableActions = true
            }
        };
    }

    private static string GetArtifactsList(bool con, bool spec, bool plan, bool tasks, bool data)
    {
        var items = new List<string>();
        if (con) items.Add("✓ Constitution");
        if (spec) items.Add("✓ Specification");
        if (plan) items.Add("✓ Plan");
        if (tasks) items.Add("✓ Tasks");
        if (data) items.Add("✓ Data Model");
        return string.Join(", ", items);
    }

    private static List<string> GetMissingArtifacts(bool con, bool spec, bool plan, bool tasks, bool data)
    {
        var missing = new List<string>();
        if (!con) missing.Add("Constitution");
        if (!spec) missing.Add("Specification");
        if (!plan) missing.Add("Plan");
        if (!tasks) missing.Add("Tasks");
        if (!data) missing.Add("Data Model");
        return missing;
    }
}

public class ConstitutionExplorerPageModelBuilder(
    IWorkspaceArtifactStatusService artifactStatus) : IConstitutionExplorerPageModelBuilder
{
    public async Task<ReviewPageModel> BuildAsync()
    {
        var artifact = await artifactStatus.GetArtifactAsync(WorkspaceArtifactKind.Constitution);

        if (artifact == null)
        {
            return new ReviewPageModel
            {
                Title = "Constitution Explorer",
                Description = "Review and analyze constitution.md files",
                ReadinessStatus = ReviewStatus.Empty,
                RequiredInputs = new[] { "Constitution" }.ToList(),
                Summary = new ReviewSummary
                {
                    StatusMessage = "No constitution loaded",
                    CanRun = false,
                    HasAvailableActions = true
                }
            };
        }

        var results = new List<ReviewResult>();
        var lines = artifact.Content.Split('\n');
        var sections = CountSections(lines);
        var principles = CountPrinciples(lines);

        results.Add(new ReviewResult
        {
            Name = "Constitution Structure",
            Category = "Analysis",
            Status = ReviewStatus.Ready,
            Severity = "Info",
            Summary = $"Constitution contains {sections} sections and {principles} principles",
            Details = $"Document has {lines.Length} lines with {CountHeadings(lines)} headings"
        });

        if (principles == 0)
        {
            results.Add(new ReviewResult
            {
                Name = "No Principles Detected",
                Category = "Validation",
                Status = ReviewStatus.Warning,
                Severity = "Warning",
                Summary = "Constitution has no documented principles",
                Recommendation = "Add principle statements using standard heading markers"
            });
        }

        return new ReviewPageModel
        {
            Title = "Constitution Explorer",
            Description = "Review and analyze constitution.md files",
            ReadinessStatus = ReviewStatus.Ready,
            ArtifactKind = "Constitution",
            Results = results,
            Summary = new ReviewSummary
            {
                StatusMessage = "Constitution loaded and analyzed",
                TotalResults = results.Count,
                WarningCount = principles == 0 ? 1 : 0,
                CanRun = true,
                HasAvailableActions = true
            }
        };
    }

    private static int CountSections(string[] lines) => lines.Count(l => l.StartsWith("## "));
    private static int CountPrinciples(string[] lines) => lines.Count(l => l.Contains("principle") || l.Contains("Principle"));
    private static int CountHeadings(string[] lines) => lines.Count(l => l.StartsWith("#"));
}

public class DataModelExplorerPageModelBuilder(
    IWorkspaceArtifactStatusService artifactStatus) : IDataModelExplorerPageModelBuilder
{
    public async Task<ReviewPageModel> BuildAsync()
    {
        var artifact = await artifactStatus.GetArtifactAsync(WorkspaceArtifactKind.DataModel);

        if (artifact == null)
        {
            return new ReviewPageModel
            {
                Title = "Data Model Explorer",
                Description = "Review and analyze data-model.md files",
                ReadinessStatus = ReviewStatus.Empty,
                RequiredInputs = new[] { "DataModel" }.ToList(),
                Summary = new ReviewSummary
                {
                    StatusMessage = "No data model loaded",
                    CanRun = false,
                    HasAvailableActions = true
                }
            };
        }

        var results = new List<ReviewResult>();
        var lines = artifact.Content.Split('\n');
        var entities = CountEntities(lines);
        var relationships = CountRelationships(lines);

        results.Add(new ReviewResult
        {
            Name = "Data Model Structure",
            Category = "Analysis",
            Status = ReviewStatus.Ready,
            Severity = "Info",
            Summary = $"Data model defines {entities} entities and {relationships} relationships",
            Details = $"Document contains {lines.Length} lines with {CountTables(lines)} table definitions"
        });

        if (entities == 0)
        {
            results.Add(new ReviewResult
            {
                Name = "No Entities Defined",
                Category = "Validation",
                Status = ReviewStatus.Warning,
                Severity = "Warning",
                Summary = "Data model contains no entity definitions",
                Recommendation = "Define entities using standard table or class notation"
            });
        }

        return new ReviewPageModel
        {
            Title = "Data Model Explorer",
            Description = "Review and analyze data-model.md files",
            ReadinessStatus = ReviewStatus.Ready,
            ArtifactKind = "DataModel",
            Results = results,
            Summary = new ReviewSummary
            {
                StatusMessage = "Data model loaded and analyzed",
                TotalResults = results.Count,
                WarningCount = entities == 0 ? 1 : 0,
                CanRun = true,
                HasAvailableActions = true
            }
        };
    }

    private static int CountEntities(string[] lines) => lines.Count(l => l.Contains("entity") || l.Contains("Entity") || l.Contains("table") || l.Contains("Table"));
    private static int CountRelationships(string[] lines) => lines.Count(l => l.Contains("relationship") || l.Contains("Relationship") || l.Contains("->"));
    private static int CountTables(string[] lines) => lines.Count(l => l.Contains("|") && l.Contains("-"));
}

public class PlanExplorerPageModelBuilder(
    IWorkspaceArtifactStatusService artifactStatus) : IPlanExplorerPageModelBuilder
{
    public async Task<ReviewPageModel> BuildAsync()
    {
        var artifact = await artifactStatus.GetArtifactAsync(WorkspaceArtifactKind.Plan);

        if (artifact == null)
        {
            return new ReviewPageModel
            {
                Title = "Plan Explorer",
                Description = "Review and analyze plan.md files",
                ReadinessStatus = ReviewStatus.Empty,
                RequiredInputs = new[] { "Plan" }.ToList(),
                Summary = new ReviewSummary
                {
                    StatusMessage = "No plan loaded",
                    CanRun = false,
                    HasAvailableActions = true
                }
            };
        }

        var results = new List<ReviewResult>();
        var lines = artifact.Content.Split('\n');
        var phases = CountPhases(lines);
        var risks = CountRisks(lines);
        var decisions = CountDecisions(lines);

        results.Add(new ReviewResult
        {
            Name = "Delivery Plan Overview",
            Category = "Analysis",
            Status = ReviewStatus.Ready,
            Severity = "Info",
            Summary = $"Plan contains {phases} phases, {risks} identified risks, {decisions} key decisions",
            Details = $"Document defines {CountMilestones(lines)} milestones and {CountTasks(lines)} tasks"
        });

        if (risks == 0)
        {
            results.Add(new ReviewResult
            {
                Name = "No Risks Identified",
                Category = "Validation",
                Status = ReviewStatus.Warning,
                Severity = "Warning",
                Summary = "Plan identifies no project risks",
                Recommendation = "Document foreseeable risks and mitigation strategies"
            });
        }

        if (phases == 0)
        {
            results.Add(new ReviewResult
            {
                Name = "No Phases Defined",
                Category = "Validation",
                Status = ReviewStatus.Warning,
                Severity = "Warning",
                Summary = "Plan lacks defined delivery phases",
                Recommendation = "Structure the plan with clear phases and milestones"
            });
        }

        return new ReviewPageModel
        {
            Title = "Plan Explorer",
            Description = "Review and analyze plan.md files",
            ReadinessStatus = ReviewStatus.Ready,
            ArtifactKind = "Plan",
            Results = results,
            Summary = new ReviewSummary
            {
                StatusMessage = $"Plan loaded with {phases} phases and {risks} risks",
                TotalResults = results.Count,
                WarningCount = (risks == 0 ? 1 : 0) + (phases == 0 ? 1 : 0),
                CanRun = true,
                HasAvailableActions = true
            }
        };
    }

    private static int CountPhases(string[] lines) => lines.Count(l => l.Contains("Phase") || l.Contains("phase"));
    private static int CountRisks(string[] lines) => lines.Count(l => l.Contains("Risk") || l.Contains("risk"));
    private static int CountDecisions(string[] lines) => lines.Count(l => l.Contains("Decision") || l.Contains("decision"));
    private static int CountMilestones(string[] lines) => lines.Count(l => l.Contains("Milestone") || l.Contains("milestone"));
    private static int CountTasks(string[] lines) => lines.Count(l => l.StartsWith("- ") || l.StartsWith("* "));
}

public class TaskExplorerPageModelBuilder(
    IWorkspaceArtifactStatusService artifactStatus) : ITaskExplorerPageModelBuilder
{
    public async Task<ReviewPageModel> BuildAsync()
    {
        var artifact = await artifactStatus.GetArtifactAsync(WorkspaceArtifactKind.Tasks);

        if (artifact == null)
        {
            return new ReviewPageModel
            {
                Title = "Task Explorer",
                Description = "Review and analyze tasks.md files",
                ReadinessStatus = ReviewStatus.Empty,
                RequiredInputs = new[] { "Tasks" }.ToList(),
                Summary = new ReviewSummary
                {
                    StatusMessage = "No tasks loaded",
                    CanRun = false,
                    HasAvailableActions = true
                }
            };
        }

        var results = new List<ReviewResult>();
        var lines = artifact.Content.Split('\n');
        var tasks = CountTasks(lines);
        var completedTasks = CountCompletedTasks(lines);
        var unassignedTasks = CountUnassignedTasks(lines);

        results.Add(new ReviewResult
        {
            Name = "Task Summary",
            Category = "Analysis",
            Status = ReviewStatus.Ready,
            Severity = "Info",
            Summary = $"Task list contains {tasks} tasks",
            Details = $"{completedTasks} completed, {tasks - completedTasks} remaining, {unassignedTasks} unassigned"
        });

        if (unassignedTasks > 0)
        {
            results.Add(new ReviewResult
            {
                Name = "Unassigned Tasks",
                Category = "Validation",
                Status = ReviewStatus.Warning,
                Severity = "Warning",
                Summary = $"{unassignedTasks} task(s) without owner assignment",
                Recommendation = "Assign all tasks to team members or components"
            });
        }

        if (completedTasks < tasks * 0.1)
        {
            results.Add(new ReviewResult
            {
                Name = "Low Completion Rate",
                Category = "Status",
                Status = ReviewStatus.Ready,
                Severity = "Info",
                Summary = $"Only {(completedTasks * 100 / (tasks + 1))}% of tasks completed"
            });
        }

        return new ReviewPageModel
        {
            Title = "Task Explorer",
            Description = "Review and analyze tasks.md files",
            ReadinessStatus = ReviewStatus.Ready,
            ArtifactKind = "Tasks",
            Results = results,
            Summary = new ReviewSummary
            {
                StatusMessage = $"Task list loaded: {completedTasks}/{tasks} complete",
                TotalResults = results.Count,
                WarningCount = unassignedTasks > 0 ? 1 : 0,
                CanRun = true,
                HasAvailableActions = true
            }
        };
    }

    private static int CountTasks(string[] lines) => lines.Count(l => l.TrimStart().StartsWith("- ") || l.TrimStart().StartsWith("* "));
    private static int CountCompletedTasks(string[] lines) => lines.Count(l => (l.Contains("[x]") || l.Contains("[X]")));
    private static int CountUnassignedTasks(string[] lines) => lines.Count(l => (l.TrimStart().StartsWith("- ") || l.TrimStart().StartsWith("* ")) && !l.Contains("@"));
}
