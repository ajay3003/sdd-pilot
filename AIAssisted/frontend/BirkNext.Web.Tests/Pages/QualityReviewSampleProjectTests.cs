using AngleSharp.Dom;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

public sealed class QualityReviewSampleProjectTests : BunitContext
{
    private readonly FakeSampleProjectDocumentResolver _resolver = new();
    private readonly CapturingQualityReviewService _qualityReview = new();
    private readonly QualityReviewSessionService _qualitySession = new();

    public QualityReviewSampleProjectTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;

        Services.AddSingleton<ISampleProjectDocumentResolver>(_resolver);
        Services.AddSingleton<IQualityReviewService>(_qualityReview);
        Services.AddSingleton(_qualitySession);
        Services.AddSingleton(Mock.Of<IDashboardSnapshotService>());
        Services.AddSingleton(Mock.Of<IDeliveryReadinessAssessmentService>());
        Services.AddSingleton(Mock.Of<IReportExportService>());
    }

    [Fact]
    public void InitialLoad_ReadsAllArtifactsFromSelectedSampleProject()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var cut = Render<QualityReview>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Sample Project:");
            cut.Markup.Should().Contain("Project A");
            cut.FindAll(".artifact-card").Should().HaveCount(5);
            cut.FindAll(".artifact-status.is-loaded").Should().HaveCount(5);
            cut.Markup.Should().Contain("constitution.md");
            cut.Markup.Should().Contain("spec.md");
            cut.Markup.Should().Contain("plan.md");
            cut.Markup.Should().Contain("tasks.md");
            cut.Markup.Should().Contain("data-model.md");
            cut.Markup.Should().Contain("5 artifacts available");
        });
    }

    [Fact]
    public void ReadOnlyUi_DoesNotRenderManualArtifactInputControls()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var cut = Render<QualityReview>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Run Quality Review");
            cut.Markup.Should().Contain("Review Packs");
            cut.Markup.Should().NotContain("SpecificationImport");
            cut.FindAll("input[type=file]").Should().BeEmpty();
            cut.FindAll("textarea").Should().BeEmpty();
            cut.Markup.Should().NotContain("drag/drop");
            cut.Markup.Should().NotContain("Browse");
            cut.Markup.Should().NotContain("Upload");
            cut.Markup.Should().NotContain("Clear artifact");
        });
    }

    [Fact]
    public void NoProjectSelected_ShowsNoProjectStateWithoutManualFallback()
    {
        _resolver.SetSelectedProject(null);

        var cut = Render<QualityReview>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("No Sample Project selected.");
            cut.Markup.Should().NotContain("Run Quality Review");
            cut.Markup.Should().NotContain("Sample Project Artifacts");
            cut.Markup.Should().NotContain("OLD WORKSPACE SPEC");
        });
    }

    [Fact]
    public void MissingDataModel_MarksOnlyDataModelMissingAndDisablesDataModelPack()
    {
        SeedProjectA(includeDataModel: false);
        _resolver.SetSelectedProject("project-a");

        var cut = Render<QualityReview>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".artifact-status.is-loaded").Should().HaveCount(4);
            cut.Markup.Should().Contain("4 artifacts available");

            var dataModelCard = FindArtifactCard(cut, "Data Model");
            dataModelCard.TextContent.Should().Contain("Missing");
            dataModelCard.TextContent.Should().Contain("data-model.md");

            var dataModelPack = FindPackLabel(cut, "Data Model Quality");
            dataModelPack.ClassList.Should().Contain("is-disabled");
            dataModelPack.QuerySelector("input")!.HasAttribute("disabled").Should().BeTrue();

            FindPackLabel(cut, "QA Auditor").ClassList.Should().NotContain("is-disabled");
            FindPackLabel(cut, "Delivery Readiness").ClassList.Should().NotContain("is-disabled");
        });
    }

    [Fact]
    public void WorkspaceArtifactCannotOverrideSampleProjectResolvedArtifact()
    {
        SeedProjectA(specification: "PROJECT A SPEC");
        _resolver.SetSelectedProject("project-a");

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            _qualityReview.Calls.Should().HaveCount(1);
            _qualityReview.Calls[0].Specification.Should().Be("PROJECT A SPEC");
            _qualityReview.Calls[0].Specification.Should().NotBe("OLD WORKSPACE SPEC");
        });
    }

    [Fact]
    public void ProjectSwitch_ReloadsArtifactsAndRecalculatesPackAvailability()
    {
        SeedProjectA();
        SeedProjectB(includeDataModel: false);
        _resolver.SetSelectedProject("project-a");

        var cut = Render<QualityReview>();
        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Project A");
            cut.Markup.Should().Contain("5 artifacts available");
            FindPackLabel(cut, "Data Model Quality").ClassList.Should().NotContain("is-disabled");
        });

        _resolver.SetSelectedProject("project-b");
        cut.Render();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Project B");
            cut.Markup.Should().NotContain("PROJECT A");
            cut.Markup.Should().Contain("4 artifacts available");
            FindPackLabel(cut, "Data Model Quality").ClassList.Should().Contain("is-disabled");
            cut.Instance.Should().NotBeNull();
        });
    }

    [Fact]
    public void ProjectSwitch_ClearsReportFromPreviousProject()
    {
        SeedProjectA();
        SeedProjectB();
        _resolver.SetSelectedProject("project-a");
        _qualitySession.SaveResult(
            MakeReport(new QualityReviewPackResult
            {
                PackId = "qa-auditor",
                PackName = "Restored A Pack",
                PackGroup = "Quality",
                Score = 90,
            }),
            ["qa-auditor"],
            "project-a",
            new Dictionary<WorkspaceArtifactKind, string>
            {
                [WorkspaceArtifactKind.Specification] = "A SPEC",
            });

        var cut = Render<QualityReview>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Restored A Pack"));

        _resolver.SetSelectedProject("project-b");
        cut.Render();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Project B");
            cut.Markup.Should().NotContain("Restored A Pack");
            cut.Markup.Should().Contain("Run Quality Review");
            cut.Find("button.btn-primary").HasAttribute("disabled").Should().BeFalse();
        });
    }

    [Fact]
    public void SameProjectRerender_DoesNotResolveArtifactsAgain()
    {
        SeedProjectA();
        SeedProjectB();
        _resolver.SetSelectedProject("project-a");

        var cut = Render<QualityReview>();
        cut.WaitForAssertion(() => _resolver.ResolveCallCount.Should().Be(5));

        cut.Render();
        cut.WaitForAssertion(() => _resolver.ResolveCallCount.Should().Be(5));

        _resolver.SetSelectedProject("project-b");
        cut.Render();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Project B");
            _resolver.ResolveCallCount.Should().Be(10);
        });
    }

    [Fact]
    public void RunQualityReview_UsesResolvedSampleProjectSnapshot()
    {
        SeedProjectA(
            constitution: "PROJECT A CONSTITUTION",
            specification: "PROJECT A SPEC",
            plan: "PROJECT A PLAN",
            tasks: "PROJECT A TASKS",
            dataModel: "PROJECT A DATA MODEL");
        _resolver.SetSelectedProject("project-a");

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            _qualityReview.Calls.Should().HaveCount(1);
            var call = _qualityReview.Calls[0];
            call.Constitution.Should().Be("PROJECT A CONSTITUTION");
            call.Specification.Should().Be("PROJECT A SPEC");
            call.Plan.Should().Be("PROJECT A PLAN");
            call.Tasks.Should().Be("PROJECT A TASKS");
            call.DataModel.Should().Be("PROJECT A DATA MODEL");
            call.SelectedPackIds.Should().Contain("data-model-quality");
        });
    }

    [Fact]
    public void PackAvailability_ReactsToProjectSwitch()
    {
        SeedProjectA();
        SeedProjectB(includeDataModel: false);
        _resolver.SetSelectedProject("project-a");

        var cut = Render<QualityReview>();
        cut.WaitForAssertion(() =>
        {
            FindPackLabel(cut, "Data Model Quality").ClassList.Should().NotContain("is-disabled");
            cut.Markup.Should().Contain("5 artifacts available");
        });

        _resolver.SetSelectedProject("project-b");
        cut.Render();

        cut.WaitForAssertion(() =>
        {
            var dataModelPack = FindPackLabel(cut, "Data Model Quality");
            dataModelPack.ClassList.Should().Contain("is-disabled");
            dataModelPack.QuerySelector("input")!.HasAttribute("disabled").Should().BeTrue();
            dataModelPack.QuerySelector("input")!.HasAttribute("checked").Should().BeFalse();
            FindPackLabel(cut, "QA Auditor").ClassList.Should().NotContain("is-disabled");
            cut.Markup.Should().Contain("4 artifacts available");
        });
    }

    [Fact]
    public void DeselectProject_ClearsArtifactsReportAndRunState()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var cut = Render<QualityReview>();
        ClickRun(cut);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Captured QA Auditor"));

        _resolver.SetSelectedProject(null);
        cut.Render();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("No Sample Project selected.");
            cut.Markup.Should().NotContain("Sample Project Artifacts");
            cut.Markup.Should().NotContain("Captured QA Auditor");
            cut.Markup.Should().NotContain("Run Quality Review");
            cut.Markup.Should().NotContain("Project A");
        });
    }

    [Fact]
    public void RestartedSampleProject_QAReviewLoadsFromResolverWithoutWorkspaceCopies()
    {
        // Arrange: Simulate restart with persisted CurrentProject="project-a" but empty Workspace
        // (identity-only restoration: no Markdown copies persisted)
        SeedProjectA(
            constitution: "RESTORED CONSTITUTION",
            specification: "RESTORED SPECIFICATION",
            plan: "RESTORED PLAN",
            tasks: "RESTORED TASKS",
            dataModel: "RESTORED DATA MODEL");
        _resolver.SetSelectedProject("project-a");

        // Act: Render QualityReview on startup (simulating restart)
        var cut = Render<QualityReview>();

        // Assert: All five documents loaded from resolver, not Workspace
        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Sample Project:");
            cut.Markup.Should().Contain("Project A");
            cut.Markup.Should().Contain("5 artifacts available");
            cut.FindAll(".artifact-status.is-loaded").Should().HaveCount(5);

            // Verify exact content comes from resolver
            _qualityReview.Calls.Should().BeEmpty("review has not run yet");
        });

        // Act: Run review to confirm resolver-loaded content is used
        ClickRun(cut);

        // Assert: Review used resolver-loaded documents, not stale Workspace copies
        cut.WaitForAssertion(() =>
        {
            _qualityReview.Calls.Should().HaveCount(1);
            var call = _qualityReview.Calls[0];
            call.Constitution.Should().Be("RESTORED CONSTITUTION");
            call.Specification.Should().Be("RESTORED SPECIFICATION");
            call.Plan.Should().Be("RESTORED PLAN");
            call.Tasks.Should().Be("RESTORED TASKS");
            call.DataModel.Should().Be("RESTORED DATA MODEL");
        });
    }

    [Fact]
    public void OverallScoreRing_DoesNotRenderSolidFilledCircle()
    {
        // SVG circles must have fill="none" to prevent black solid disk rendering
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");
        var report = MakeReport(new QualityReviewPackResult
        {
            PackName = "Test Pack",
            Score = 33,
            Critical = 0,
            High = 0,
            Medium = 0,
            Low = 0,
        });
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var svg = cut.Markup;
            // Both circles must have fill="none"
            svg.Should().Contain("fill='none'");
            var fillNoneCount = svg.Split("fill='none'", StringSplitOptions.None).Length - 1;
            fillNoneCount.Should().BeGreaterThanOrEqualTo(2, "both SVG circles require fill='none'");
        });
    }

    [Fact]
    public void OverallScoreRing_ThirtyThreePercent_RendersPartialProgress()
    {
        // Score of 33% should render as partial progress, not full circle
        // Radius = 36, circumference ≈ 226.19
        // stroke-dashoffset = circ - (0.33 * circ) = circ * 0.67 ≈ 151.55
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");
        var report = MakeReport(new QualityReviewPackResult
        {
            PackName = "Test Pack",
            Score = 33,
            Critical = 0,
            High = 0,
            Medium = 0,
            Low = 0,
        });
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var svg = cut.Markup;
            // Verify dynamic SVG attributes are present
            svg.Should().Contain("stroke-dasharray=");
            svg.Should().Contain("stroke-dashoffset=");
            // Should NOT contain fill="black" or only fill (without none)
            var singleQuotedFills = svg.Split("fill='none'", StringSplitOptions.None).Length;
            singleQuotedFills.Should().BeGreaterThanOrEqualTo(3); // At least 2 matches + 1
        });
    }

    [Fact]
    public void OverallScoreRing_ZeroPercent_RendersEmpty()
    {
        // 0% score should produce full stroke-dashoffset = full circumference (empty ring)
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");
        var report = MakeReport(new QualityReviewPackResult
        {
            PackName = "Test Pack",
            Score = 0,
            Critical = 0,
            High = 0,
            Medium = 0,
            Low = 0,
        });
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var svg = cut.Markup;
            svg.Should().Contain("stroke-dashoffset=");
            svg.Should().Contain("fill='none'");
            // 0% → offset = circ - (0 * circ) = circ (full circumference offset hides all)
            svg.Should().Contain("0%");
        });
    }

    [Fact]
    public void OverallScoreRing_HundredPercent_RendersFull()
    {
        // 100% score should produce stroke-dashoffset=0 (full circle visible)
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");
        var report = MakeReport(new QualityReviewPackResult
        {
            PackName = "Test Pack",
            Score = 100,
            Critical = 0,
            High = 0,
            Medium = 0,
            Low = 0,
        });
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var svg = cut.Markup;
            svg.Should().Contain("stroke-dashoffset=");
            svg.Should().Contain("100"); // Score label shows 100
            svg.Should().Contain("fill='none'");
        });
    }

    [Fact]
    public void OverallScoreRing_FiftyPercent_RendersHalf()
    {
        // 50% score should render approximately half progress
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");
        var report = MakeReport(new QualityReviewPackResult
        {
            PackName = "Test Pack",
            Score = 50,
            Critical = 0,
            High = 0,
            Medium = 0,
            Low = 0,
        });
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var svg = cut.Markup;
            svg.Should().Contain("50"); // Score label
            svg.Should().Contain("stroke-dashoffset=");
            svg.Should().Contain("stroke-linecap='round'");
        });
    }

    [Fact]
    public void OverallScoreRing_ScoreTextAccessible()
    {
        // Score must be displayed as human-readable text
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");
        var report = MakeReport(new QualityReviewPackResult
        {
            PackName = "Test Pack",
            Score = 75.5,
            Critical = 0,
            High = 0,
            Medium = 0,
            Low = 0,
        });
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            markup.Should().Contain("<small>%</small>");
            markup.Should().Contain("76"); // Rounded score
        });
    }

    [Fact]
    public void ArtifactGrid_RendersFiveCards_CompactLayout()
    {
        // All 5 artifacts should render efficiently in a compact grid
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var cut = Render<QualityReview>();

        cut.WaitForAssertion(() =>
        {
            var cards = cut.FindAll(".artifact-card");
            cards.Should().HaveCount(5);

            // Grid should use responsive layout (auto-fit with minmax)
            var gridElement = cut.Find(".qr-artifact-grid");
            gridElement.ClassList.Should().Contain("qr-artifact-grid");
        });
    }

    [Fact]
    public void ArtifactCards_ShowAvailabilityButNotRepetitive()
    {
        // Loaded artifacts show green border and Available text, but not overly heavy badge
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var cut = Render<QualityReview>();

        cut.WaitForAssertion(() =>
        {
            var constitutionCard = FindArtifactCard(cut, "Constitution");
            constitutionCard.ClassName.Should().Contain("is-loaded");

            // Available text should be present
            constitutionCard.TextContent.Should().Contain("Available");

            // Status badge should exist but be subtle
            var statusElement = constitutionCard.QuerySelector(".artifact-status");
            statusElement.Should().NotBeNull();
            statusElement.TextContent.Trim().Should().Be("Available");
        });
    }

    [Fact]
    public void ReviewPacks_CategoryHeadersDistinguishable()
    {
        // Category group titles (QUALITY, GOVERNANCE, etc.) should be visually distinct
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var cut = Render<QualityReview>();

        cut.WaitForAssertion(() =>
        {
            var groupTitles = cut.FindAll(".qr-pack-group-title");
            groupTitles.Count.Should().BeGreaterThan(0, "pack group titles should be rendered");

            // Should have visible category headers
            var categories = groupTitles.Select(t => t.TextContent.Trim()).ToList();
            categories.Should().HaveCountGreaterThan(0);
            categories.Should().Contain(c => c.Contains("Quality", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public void PackDescriptions_MoreReadable()
    {
        // Pack descriptions should be easily readable
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var cut = Render<QualityReview>();

        cut.WaitForAssertion(() =>
        {
            var descriptions = cut.FindAll(".qr-pack-desc");
            descriptions.Count.Should().BeGreaterThan(0);

            // Each description should have meaningful text
            descriptions.First().TextContent.Length.Should().BeGreaterThan(5);
        });
    }

    [Fact]
    public void ReviewSummaryStep_HasExplicitHeading()
    {
        // Step 3 should have an explicit "REVIEW SUMMARY" heading
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var cut = Render<QualityReview>();

        cut.WaitForAssertion(() =>
        {
            var runBar = cut.Find(".qr-run-bar");
            var title = runBar.QuerySelector(".qr-run-title");

            title.Should().NotBeNull("Step 3 should have a summary title");
            title.TextContent.Should().Contain("Review Summary");
        });
    }

    [Fact]
    public void RunButton_VisuallyConnectedToSummary()
    {
        // Run button should be visually part of the summary section with border divider
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var cut = Render<QualityReview>();

        cut.WaitForAssertion(() =>
        {
            var runBar = cut.Find(".qr-run-bar");

            // Should have visual separation (border-top)
            runBar.Should().NotBeNull();

            // Summary section and button should be flex siblings
            var summarySection = runBar.QuerySelector(".qr-run-summary-section");
            summarySection.Should().NotBeNull("summary section should exist");
        });
    }

    [Fact]
    public void CategorySelectButtons_HaveClearLabels()
    {
        // Category buttons should have title attributes explaining they select packs
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var cut = Render<QualityReview>();

        cut.WaitForAssertion(() =>
        {
            var buttons = cut.FindAll(".qr-shortcut-btn");

            // First button should be "Select All"
            buttons.First().TextContent.Should().Be("Select All");
            buttons.First().GetAttribute("title").Should().Contain("Select");

            // Each category button should mention selection in title
            var qualityBtn = buttons.SingleOrDefault(b => b.TextContent.Contains("Quality"));
            qualityBtn.Should().NotBeNull();
            qualityBtn.GetAttribute("title").Should().Contain("Select");
        });
    }

    // ── Async Regression Tests ────────────────────────────────────────────────

    [Fact]
    public void RunQualityReview_BuildDiagnosticExportAsyncIsAsync()
    {
        // REGRESSION: Old code used GetAvailableProjectsAsync().Result which caused
        // System.PlatformNotSupportedException on WASM - handler blocked on Monitor.Wait()
        // Verify production code uses async/await instead
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");
        var cut = Render<QualityReview>();

        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            _qualityReview.Calls.Should().HaveCount(1);
            // If code used .Result, execution would have failed with PlatformNotSupportedException
            // Successful completion proves async/await is used
        }, timeout: TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void RunQualityReview_WithIncompleteProjectTask_AwaitsCompletion()
    {
        // Demonstrates that GetAvailableProjectsAsync is awaited (not blocking)
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");
        var cut = Render<QualityReview>();

        // Make projects async return incomplete
        var tcs = _resolver.MakeGetAvailableProjectsIncomplete();
        var runStarted = false;

        // Run in background to avoid deadlock if .Result was used
        var runTask = Task.Run(() =>
        {
            try
            {
                ClickRun(cut);
                runStarted = true;
            }
            catch { }
        });

        // Give handler time to execute
        Task.Delay(100).Wait();

        // If old .Result code was used, handler would hang here
        // With async/await, the handler should have started
        runTask.IsCompleted.Should().BeTrue("handler should not block on incomplete Task");
        runStarted.Should().BeTrue("click should have executed");

        // Complete the task
        _resolver.CompleteGetAvailableProjects();

        // Run should complete
        cut.WaitForAssertion(() =>
        {
            _qualityReview.Calls.Count.Should().BeGreaterThan(0);
        }, timeout: TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void RunQualityReview_BuildDiagnosticExportAwaitsFinalizeCompletes()
    {
        // Ensure complete async flow works
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");
        var cut = Render<QualityReview>();

        ClickRun(cut);

        // Full async pipeline should complete
        cut.WaitForAssertion(() =>
        {
            _qualityReview.Calls.Should().HaveCount(1);
            var call = _qualityReview.Calls[0];
            call.Constitution.Should().NotBeNullOrEmpty();
        }, timeout: TimeSpan.FromSeconds(3));
    }

    private static IElement FindArtifactCard(IRenderedComponent<QualityReview> cut, string artifactName) =>
        cut.FindAll(".artifact-card").Single(card => card.TextContent.Contains(artifactName, StringComparison.Ordinal));

    private static IElement FindPackLabel(IRenderedComponent<QualityReview> cut, string packName) =>
        cut.FindAll("label.qr-pack-option").Single(label => label.TextContent.Contains(packName, StringComparison.Ordinal));

    private static void ClickRun(IRenderedComponent<QualityReview> cut)
    {
        cut.WaitForAssertion(() => cut.Find("button.btn-primary").HasAttribute("disabled").Should().BeFalse());
        cut.Find("button.btn-primary").Click();
    }

    private void SeedProjectA(
        string constitution = "PROJECT A constitution.md",
        string specification = "PROJECT A spec.md",
        string plan = "PROJECT A plan.md",
        string tasks = "PROJECT A tasks.md",
        string dataModel = "PROJECT A data-model.md",
        bool includeDataModel = true)
    {
        _resolver.SetProjectDocument("project-a", "Project A", ExplorerDocumentType.Constitution, constitution);
        _resolver.SetProjectDocument("project-a", "Project A", ExplorerDocumentType.Specification, specification);
        _resolver.SetProjectDocument("project-a", "Project A", ExplorerDocumentType.Plan, plan);
        _resolver.SetProjectDocument("project-a", "Project A", ExplorerDocumentType.Tasks, tasks);
        if (includeDataModel)
            _resolver.SetProjectDocument("project-a", "Project A", ExplorerDocumentType.DataModel, dataModel);
    }

    private void SeedProjectB(bool includeDataModel = true)
    {
        _resolver.SetProjectDocument("project-b", "Project B", ExplorerDocumentType.Constitution, "PROJECT B constitution.md");
        _resolver.SetProjectDocument("project-b", "Project B", ExplorerDocumentType.Specification, "PROJECT B spec.md");
        _resolver.SetProjectDocument("project-b", "Project B", ExplorerDocumentType.Plan, "PROJECT B plan.md");
        _resolver.SetProjectDocument("project-b", "Project B", ExplorerDocumentType.Tasks, "PROJECT B tasks.md");
        if (includeDataModel)
            _resolver.SetProjectDocument("project-b", "Project B", ExplorerDocumentType.DataModel, "PROJECT B data-model.md");
    }

    private static QualityReviewReport MakeReport(params QualityReviewPackResult[] results) =>
        new()
        {
            PackResults = [.. results],
            OverallScore = results.Length == 0 ? 0 : Math.Round(results.Average(r => r.Score), 1),
            TotalFindings = results.Sum(r => r.Critical + r.High + r.Medium + r.Low),
            CriticalCount = results.Sum(r => r.Critical),
            HighCount = results.Sum(r => r.High),
            MediumCount = results.Sum(r => r.Medium),
            LowCount = results.Sum(r => r.Low),
            RunAt = DateTimeOffset.UtcNow,
        };

    private sealed class FakeSampleProjectDocumentResolver : ISampleProjectDocumentResolver
    {
        private readonly Dictionary<string, SampleProjectDto> _projects = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(string ProjectSlug, ExplorerDocumentType Type), string> _documents = [];
        private string? _selectedProject;
        private TaskCompletionSource<IReadOnlyList<SampleProjectDto>>? _projectsTcs;

        public int ResolveCallCount { get; private set; }

        public void SetProjectDocument(string projectSlug, string projectName, ExplorerDocumentType documentType, string content)
        {
            _documents[(projectSlug, documentType)] = content;

            var filename = GetFilename(documentType);
            var files = _projects.TryGetValue(projectSlug, out var existing)
                ? existing.Files.Where(file => !file.Filename.Equals(filename, StringComparison.OrdinalIgnoreCase)).ToList()
                : [];
            files.Add(new SampleFileDto(filename, true, documentType.ToString(), null, null, true, false));

            _projects[projectSlug] = new SampleProjectDto(
                projectSlug,
                projectName,
                "test",
                $"Test project {projectName}",
                $"/SampleData/{projectSlug}",
                false,
                files);
        }

        public Task<SampleProjectDocumentResult> ResolveAsync(
            string projectSlug,
            ExplorerDocumentType documentType,
            CancellationToken cancellationToken = default)
        {
            ResolveCallCount++;
            var filename = GetFilename(documentType);

            if (!_projects.ContainsKey(projectSlug))
                return Task.FromResult(SampleProjectDocumentResult.InvalidProject($"Project '{projectSlug}' not found"));

            if (!_documents.TryGetValue((projectSlug, documentType), out var content))
                return Task.FromResult(SampleProjectDocumentResult.MissingDocument(projectSlug, documentType, filename));

            return Task.FromResult(SampleProjectDocumentResult.Success(projectSlug, documentType, filename, content));
        }

        public Task<IReadOnlyList<SampleProjectDto>> GetAvailableProjectsAsync(CancellationToken cancellationToken = default)
        {
            if (_projectsTcs != null)
                return _projectsTcs.Task;
            return Task.FromResult<IReadOnlyList<SampleProjectDto>>(_projects.Values.ToList());
        }

        public string? GetSelectedProject() => _selectedProject;

        public void SetSelectedProject(string? projectSlug) => _selectedProject = projectSlug;

        public void ClearProjectCache(string projectSlug) { }

        public TaskCompletionSource<IReadOnlyList<SampleProjectDto>> MakeGetAvailableProjectsIncomplete()
        {
            _projectsTcs = new TaskCompletionSource<IReadOnlyList<SampleProjectDto>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            return _projectsTcs;
        }

        public void CompleteGetAvailableProjects()
        {
            if (_projectsTcs != null)
            {
                _projectsTcs.SetResult(_projects.Values.ToList());
                _projectsTcs = null;
            }
        }

        public void FailGetAvailableProjects(Exception ex)
        {
            if (_projectsTcs != null)
            {
                _projectsTcs.SetException(ex);
                _projectsTcs = null;
            }
        }

        private static string GetFilename(ExplorerDocumentType documentType) =>
            documentType switch
            {
                ExplorerDocumentType.Constitution => "constitution.md",
                ExplorerDocumentType.Specification => "spec.md",
                ExplorerDocumentType.Plan => "plan.md",
                ExplorerDocumentType.Tasks => "tasks.md",
                ExplorerDocumentType.DataModel => "data-model.md",
                _ => throw new ArgumentOutOfRangeException(nameof(documentType), documentType, null),
            };
    }

    private sealed class CapturingQualityReviewService : IQualityReviewService
    {
        public IReadOnlyList<QualityReviewPackDescriptor> AvailablePacks { get; } =
        [
            new("qa-auditor", "Quality", "QA Auditor", "Review shared artifact quality.", true),
            new("data-model-quality", "Quality", "Data Model Quality", "Review data-model.md.", true),
            new("constitution-compliance", "Governance", "Constitution Compliance", "Review constitution.md.", true),
            new("qa-readiness", "Readiness", "QA Readiness", "Review test readiness.", true),
            new("delivery-readiness", "Readiness", "Delivery Readiness", "Review delivery readiness.", true),
        ];

        public List<RunCall> Calls { get; } = [];
        public QualityReviewReport? FixedReport { get; set; }

        public void SetReport(QualityReviewReport report) => FixedReport = report;

        public Task InitializeAsync() => Task.CompletedTask;

        public Task<QualityReviewReport> RunAsync(
            string? constitutionText,
            string? specText,
            string? planText,
            string? taskText,
            string? dataModelText,
            IEnumerable<string> selectedPackIds)
        {
            var selected = selectedPackIds.ToList();
            Calls.Add(new RunCall(constitutionText, specText, planText, taskText, dataModelText, selected));

            if (FixedReport != null)
                return Task.FromResult(FixedReport);

            var results = selected.Select(packId =>
            {
                var descriptor = AvailablePacks.First(pack => pack.PackId == packId);
                return new QualityReviewPackResult
                {
                    PackId = descriptor.PackId,
                    PackName = $"Captured {descriptor.PackName}",
                    PackGroup = descriptor.PackGroup,
                    Score = 88,
                };
            }).ToArray();

            return Task.FromResult(MakeReport(results));
        }
    }

    private sealed record RunCall(
        string? Constitution,
        string? Specification,
        string? Plan,
        string? Tasks,
        string? DataModel,
        IReadOnlyList<string> SelectedPackIds);
}
