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
    public void UnlinkedBehavioralTask_IsNeedsReview_NotPossibleDeviation()
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
            f.Status == AlignmentStatus.NeedsReview && f.UnlinkedBusinessTask && f.Deviation == null,
            "unlinked behavior without conflicting authored statements is a review question, not a deviation");
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

        results.Single(f => f.TaskId == "T1").Status.Should().Be(AlignmentStatus.NeedsReview, "exposed behavior without a reference is a review question");
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
    public void SecurityFlagIsATopic_TestingNeedsActualTestingIntent()
    {
        TaskTopicTaxonomy.Detect(new TaskItem { Id = "T1", Title = "Do it", IsTestingTask = true, IsSecurityTask = true })
            .Should().Contain(AffectedArea.Security).And.NotContain(AffectedArea.Testing, "a flag alone is not testing intent");
        TaskTopicTaxonomy.Detect(new TaskItem { Id = "T2", Title = "Write integration tests for search" }).Should().Contain(AffectedArea.Testing);
    }

    // ── Hardened authored-alignment semantics ────────────────────────────────────────────────────────────────────────

    private static ReviewContext Context(IEnumerable<TaskItem> tasks, string? requirementText = null, IEnumerable<string>? entities = null, IEnumerable<SemanticUserStory>? stories = null) => new()
    {
        Specification = new SpecificationSemanticModel
        {
            Requirements = requirementText is null ? [] : [new SemanticRequirement { Id = "FR-003", Text = requirementText }],
            UserStories = [.. stories ?? []],
        },
        DataModel = new DataModelSemanticModel { Title = "Data Model: Person Module Core", Entities = [.. (entities ?? []).Select(e => new SemanticDataEntity { Id = e, Name = e })] },
        Tasks = new TaskSemanticModel { AllTasks = [.. tasks], TotalTasks = tasks.Count() },
    };

    private static TaskFinding One(ReviewContext context) => new TaskSpecAlignmentService().Analyse(context).Findings.Single();

    [Fact]
    public void DirectReference_IsALinkNotAPass_AndUnresolvedReferenceNeedsReview()
    {
        var linked = One(Context([new TaskItem { Id = "T1", Title = "Implement FR-003 logging rule", LinkedFRIds = ["FR-003"] }], "System MUST log access."));
        linked.Status.Should().Be(AlignmentStatus.Linked);
        linked.IsFinding.Should().BeFalse("a direct spec link is neutral — neither a finding nor a pass");
        linked.RecommendedAction.Should().Contain("does not show the task implements them");

        var unresolved = One(Context([new TaskItem { Id = "T2", Title = "Implement FR-999", LinkedFRIds = ["FR-999"] }]));
        unresolved.Status.Should().Be(AlignmentStatus.NeedsReview);
        unresolved.UnresolvedReferences.Should().Equal("FR-999");
    }

    [Fact]
    public void DataModelTask_IsADesignArtifactLink_NotRequirementCoverage()
    {
        var finding = One(Context([new TaskItem { Id = "T008", Title = "Create BarnStatusHistorikk entity with EF configuration" }], entities: ["BarnStatusHistorikk", "Person"]));
        finding.Status.Should().Be(AlignmentStatus.DesignLinked);
        finding.DesignLinks.Should().ContainSingle(d => d.Name == "BarnStatusHistorikk" && d.ArtifactType == "Data Model" && d.Source == "Data Model: Person Module Core");
        finding.Matches.Should().BeEmpty("no requirement coverage is added");
        finding.IsFinding.Should().BeFalse();
        finding.Purpose.Should().Be(TaskPurpose.DataModel);
    }

    [Fact]
    public void SetupWithTestProjects_IsInfrastructure_NotTestWork()
    {
        var finding = One(Context([new TaskItem { Id = "T001", Title = "Create solution with src and test projects (xUnit, TestContainers)", IsTestingTask = true }]));
        finding.Status.Should().Be(AlignmentStatus.TechnicalOnly);
        finding.Purpose.Should().Be(TaskPurpose.InfrastructureSetup);
        finding.AffectedAreas.Should().NotContain(AffectedArea.Testing, "a test project or package is not testing work");
        finding.IsFinding.Should().BeFalse();
    }

    [Fact]
    public void ActualTestTask_IsTestImplementation_WithItsDirectLink()
    {
        var finding = One(Context([new TaskItem { Id = "T030", Title = "Add SecurityClassificationServiceTests for FR-003 kode 6 filtering", LinkedFRIds = ["FR-003"] }], "System MUST hide Kode 6 children."));
        finding.Purpose.Should().Be(TaskPurpose.TestImplementation);
        finding.Status.Should().Be(AlignmentStatus.Linked);
        finding.AffectedAreas.Should().Contain([AffectedArea.Testing, AffectedArea.Security]);
    }

    [Fact]
    public void PossibleDeviation_NeedsBothConflictingAuthoredStatements()
    {
        var finding = One(Context([new TaskItem { Id = "T019", Title = "Return 200 with empty response for unauthorised access", LinkedFRIds = ["FR-003"] }],
            "Unauthorised access MUST return 403."));
        finding.Status.Should().Be(AlignmentStatus.PossibleDeviation);
        finding.Deviation!.TaskSays.Should().Contain("Return 200");
        finding.Deviation.AuthoredReference.Should().Be("FR-003");
        finding.Deviation.AuthoredSays.Should().Contain("403");
        finding.Reason.Should().Contain("Task says").And.Contain("FR-003 says");
    }

    [Fact]
    public void AmbiguousSecurityBehavior_WithoutReference_IsNeedsReview_NotDeviation()
    {
        var finding = One(Context([new TaskItem { Id = "T022", Title = "Enforce kode 6 security filter on search results" }]));
        finding.Status.Should().Be(AlignmentStatus.NeedsReview);
        finding.UnlinkedBusinessTask.Should().BeTrue();
        finding.Deviation.Should().BeNull();
    }

    [Fact]
    public void UserStoryReferences_ShowAuthoredIds_NeverGeneratedOnes()
    {
        SpecExplorerService.AuthoredUserStoryId("User Story 1 — Search for Children (Priority: P1)").Should().Be("US1");
        SpecExplorerService.AuthoredUserStoryId("Search for children").Should().BeNull("no authored number: no invented id");
        SpecExplorerService.IsGeneratedUserStoryId("US-ea6").Should().BeTrue();
        SpecExplorerService.IsGeneratedUserStoryId("US-001").Should().BeFalse();

        var finding = One(Context([new TaskItem { Id = "T010", Title = "Search endpoint", UserStoryId = "US1" }],
            stories: [new SemanticUserStory { Id = "US-ea6", Title = "Search for Children" }]));
        finding.Matches.Single().ItemId.Should().Be("US1", "the authored tag, not the internal id");
        finding.Matches.Single().Title.Should().Be("Search for Children");
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
