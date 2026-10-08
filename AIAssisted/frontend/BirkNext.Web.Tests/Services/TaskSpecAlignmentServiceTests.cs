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

        report.Findings.Should().HaveCount(report.TotalTasks);
        report.Findings.Should().OnlyContain(f => !string.IsNullOrWhiteSpace(f.ClassificationBasis));
    }

    [Fact]
    public void PersonAdapterSample_ClassifiesInfrastructureTasksAsTechnicalOnly()
    {
        var report = AnalysePersonAdapterSample();

        report.Findings.Should().Contain(f => f.Status == AlignmentStatus.TechnicalOnly);
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

    [Fact]
    public void PersonAdapterSample_FindingsAreOnlyNeedsReviewAndPossibleDeviations()
    {
        var report = AnalysePersonAdapterSample();

        report.Findings.Should().HaveCount(report.TotalTasks, "every task gets exactly one analysis result");
        report.FindingCount.Should().Be(report.Findings.Count(f => f.IsFinding));
        report.FindingCount.Should().Be(report.NeedsReviewTasks + report.PossibleDeviations);
        report.Findings.Where(f => f.IsFinding).Should().OnlyContain(f =>
            f.Status == AlignmentStatus.NeedsReview || f.Status == AlignmentStatus.PossibleDeviation);
        (report.LinkedTasks + report.TechnicalOnlyTasks + report.NeedsReviewTasks + report.PossibleDeviations).Should().Be(report.TotalTasks);
        (report.HighImpactTasks + report.MediumImpactTasks + report.LowImpactTasks + report.UnknownImpactTasks).Should().Be(report.TotalTasks);
        report.Findings.Should().OnlyContain(f => !string.IsNullOrWhiteSpace(f.ClassificationBasis));
    }

    [Fact]
    public void PersonAdapterSample_EndpointTaskWithoutSpecReference_IsPossibleDeviationNotTechnicalOnly()
    {
        var report = AnalysePersonAdapterSample();

        report.Findings.Should().OnlyContain(f => report.Findings.Any(task => task.TaskId == f.TaskId));
        report.Findings.Should().Contain(f => f.Status == AlignmentStatus.PossibleDeviation || f.Status == AlignmentStatus.NeedsReview);
    }

    [Fact]
    public void LinkedResult_IsALinkNotCoverage()
    {
        var report = AnalysePersonAdapterSample();

        var linked = report.Findings.Where(f => f.Status == AlignmentStatus.Linked).ToList();
        linked.Should().NotBeEmpty();
        linked.Should().OnlyContain(f => f.Matches.Count > 0 && !f.IsFinding);
        linked.Should().OnlyContain(f => !f.RecommendedAction.Contains("covered", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReferenceThatDoesNotResolveInSpec_NeedsReviewWithUnresolvedReference()
    {
        var context = new ReviewContext
        {
            Specification = new SpecificationSemanticModel { Requirements = [new SemanticRequirement { Id = "FR-001", Text = "Sign in" }] },
            Tasks = new TaskSemanticModel
            {
                AllTasks = [new TaskItem { Id = "T1", Title = "Implement sign-in", LinkedFRIds = ["FR-099"] }],
                TotalTasks = 1,
            },
        };

        var result = new TaskSpecAlignmentService().Analyse(context).Findings.Single();

        result.Status.Should().Be(AlignmentStatus.NeedsReview);
        result.UnresolvedReferences.Should().Equal("FR-099");
        result.Matches.Should().BeEmpty();
        result.IsFinding.Should().BeTrue();
    }

    [Fact]
    public void ResolvedAndUnresolvedReferences_LinkedButNamesTheUnresolvedOne()
    {
        var context = new ReviewContext
        {
            Specification = new SpecificationSemanticModel { Requirements = [new SemanticRequirement { Id = "FR-001", Text = "Sign in" }] },
            Tasks = new TaskSemanticModel
            {
                AllTasks = [new TaskItem { Id = "T1", Title = "Implement sign-in", LinkedFRIds = ["FR-001", "FR-099"] }],
                TotalTasks = 1,
            },
        };

        var result = new TaskSpecAlignmentService().Analyse(context).Findings.Single();

        result.Status.Should().Be(AlignmentStatus.Linked);
        result.Matches.Select(m => m.ItemId).Should().Equal("FR-001");
        result.UnresolvedReferences.Should().Equal("FR-099");
        result.RecommendedAction.Should().Contain("FR-099");
    }

    [Fact]
    public void ExposedEndpoint_OverridesSetupKeyword_ButNotInTestTasks()
    {
        var context = new ReviewContext
        {
            Tasks = new TaskSemanticModel
            {
                AllTasks =
                [
                    new TaskItem { Id = "T1", Title = "Add options class and controller with POST /orders" },
                    new TaskItem { Id = "T2", Title = "Integration test for POST /orders endpoint", IsTestingTask = true },
                ],
                TotalTasks = 2,
            },
        };

        var results = new TaskSpecAlignmentService().Analyse(context).Findings;

        results.Single(f => f.TaskId == "T1").Status.Should().Be(AlignmentStatus.PossibleDeviation);
        results.Single(f => f.TaskId == "T2").Status.Should().Be(AlignmentStatus.TechnicalOnly);
    }

    [Theory]
    [InlineData("Handle CdcEvent payloads", "event", true)]
    [InlineData("Prevent duplicate rows", "event", false)]
    [InlineData("Create AdapterDbContext", "dbcontext", true)]
    [InlineData("Record conflict resolution", "solution", false)]
    [InlineData("Router discards deletes", "di", false)]
    [InlineData("Audit DI registrations", "di", true)]
    [InlineData("Call the public API", "api", true)]
    public void KeywordMatching_RequiresWordOrCamelCaseStart(string text, string term, bool expected)
    {
        (TaskTopicTaxonomy.MatchedTerm(text, [term]) is not null).Should().Be(expected);
    }

    [Fact]
    public void InfrastructureTopic_ComesFromSetupFilesNotFromAnySourceFile()
    {
        TaskTopicTaxonomy.Detect(new TaskItem { Id = "T1", Title = "Implement mapper", RelatedFileIds = ["src/Mapper.cs"] })
            .Should().NotContain(AffectedArea.Infrastructure);
        TaskTopicTaxonomy.Detect(new TaskItem { Id = "T2", Title = "Add package", RelatedFileIds = ["src/App.csproj"] })
            .Should().Contain(AffectedArea.Infrastructure);
    }

    [Fact]
    public void TaskArtifactTestingAndSecurityFlags_AreTopics()
    {
        TaskTopicTaxonomy.Detect(new TaskItem { Id = "T1", Title = "Do it", IsTestingTask = true, IsSecurityTask = true })
            .Should().Contain([AffectedArea.Testing, AffectedArea.Security]);
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
        return TestDataHelper.ResolveFixturePath("person-adapter", fileName);
    }
}
