using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

public sealed class TaskSpecAlignmentServiceTests
{
    [Fact]
    public void PersonAdapterSample_ClassifiesSpecLinkedTasksFromReviewContextRelationships()
    {
        var report = AnalysePersonAdapterSample();

        report.Findings.Should().Contain(f =>
            f.TaskId == "T015" &&
            f.Status == AlignmentStatus.Linked &&
            f.Matches.Any(m => m.MatchType == SpecMatchType.UserStory));
    }

    [Fact]
    public void PersonAdapterSample_ClassifiesInfrastructureTasksAsTechnicalOnly()
    {
        var report = AnalysePersonAdapterSample();

        report.Findings.Should().Contain(f =>
            f.TaskId == "T001" &&
            f.Status == AlignmentStatus.TechnicalOnly);
    }

    [Fact]
    public void PersonAdapterSample_KeepsAmbiguousUnlinkedTasksAsNeedsReview()
    {
        var context = new ReviewContext
        {
            Tasks = new TaskSemanticModel
            {
                AllTasks =
                [
                    new TaskItem
                    {
                        Id = "T900",
                        Title = "Review pending implementation notes",
                        Description = "Confirm remaining open items with product owner."
                    }
                ],
                TotalTasks = 1
            }
        };

        var report = new TaskSpecAlignmentService().Analyse(context);

        report.Findings.Should().ContainSingle(f =>
            f.TaskId == "T900" &&
            f.Status == AlignmentStatus.NeedsReview);
    }

    [Fact]
    public void UnlinkedBehavioralTask_IsPossibleDeviation()
    {
        var context = new ReviewContext
        {
            Tasks = new TaskSemanticModel
            {
                AllTasks =
                [
                    new TaskItem
                    {
                        Id = "T901",
                        Title = "Add public endpoint for manual CDC replay",
                        Description = "Create API route that triggers replay without a linked requirement."
                    }
                ],
                TotalTasks = 1
            }
        };

        var report = new TaskSpecAlignmentService().Analyse(context);

        report.Findings.Should().ContainSingle(f =>
            f.TaskId == "T901" &&
            f.Status == AlignmentStatus.PossibleDeviation);
    }

    private static AlignmentReport AnalysePersonAdapterSample()
    {
        var specText = File.ReadAllText(FindSamplePath("spec.md"));
        var tasksText = File.ReadAllText(FindSamplePath("tasks.md"));

        var specTree = SpecExplorerService.Parse(specText);
        var taskTree = TaskExplorerService.Parse(tasksText);

        var context = ReviewContextFactory.Create(
            new ConstitutionSemanticModel(),
            SpecExplorerService.BuildSemanticModel(specTree, specText),
            new PlanSemanticModel(),
            TaskExplorerService.BuildSemanticModel(taskTree),
            new DataModelSemanticModel());

        return new TaskSpecAlignmentService().Analyse(context);
    }

    private static string FindSamplePath(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "SampleData", "person-adapter", fileName);
            if (File.Exists(path))
                return path;

            path = Path.Combine(directory.FullName, "BirkNext", "SampleData", "person-adapter", fileName);
            if (File.Exists(path))
                return path;

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate SampleData/person-adapter/{fileName}.");
    }
}
