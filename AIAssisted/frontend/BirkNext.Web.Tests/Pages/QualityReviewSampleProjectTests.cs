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

    [Fact]
    public void QualityReview_OverallSummary_RendersScoreWithStatus()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var report = MakeReport(
            new QualityReviewPackResult { PackId = "qa-auditor", PackName = "QA Auditor", Score = 75 },
            new QualityReviewPackResult { PackId = "compliance", PackName = "Constitution Compliance", Score = 50 }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            // Overall summary should contain score, status, and findings info
            cut.Markup.Should().Contain("qr-overall");
            cut.Markup.Should().Contain("qr-score-status");
            cut.Markup.Should().Contain("Fair");
            cut.Markup.Should().Contain("2 packs");
        });
    }

    [Fact]
    public void QualityReview_OverallSummary_RendersAllSevenMetrics()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var qaReport = new QaAuditReport
        {
            Findings = new List<QaFinding>
            {
                new() { RuleCode = "QA-1", Title = "T1", Description = "D1", Severity = QaSeverity.Critical, Category = QaCategory.Constitution },
                new() { RuleCode = "QA-2", Title = "T2", Description = "D2", Severity = QaSeverity.High, Category = QaCategory.Constitution },
                new() { RuleCode = "QA-3", Title = "T3", Description = "D3", Severity = QaSeverity.High, Category = QaCategory.Constitution },
                new() { RuleCode = "QA-4", Title = "T4", Description = "D4", Severity = QaSeverity.Medium, Category = QaCategory.Constitution },
                new() { RuleCode = "QA-5", Title = "T5", Description = "D5", Severity = QaSeverity.Low, Category = QaCategory.Constitution },
            },
            HasConstitution = true,
        };

        var report = MakeReport(new QualityReviewPackResult { PackId = "qa-auditor", PackName = "QA Auditor", Score = 60, Critical = 1, High = 2, Medium = 1, Low = 1, QaAudit = qaReport });
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            // All 7 metrics should render: Findings, Critical, High, Medium, Low, Blockers, Warnings
            cut.FindAll(".qr-count-card").Should().HaveCount(7);
            cut.Markup.Should().Contain("Findings");
            cut.Markup.Should().Contain("Critical");
            cut.Markup.Should().Contain("High");
            cut.Markup.Should().Contain("Medium");
            cut.Markup.Should().Contain("Low");
            cut.Markup.Should().Contain("Blockers");
            cut.Markup.Should().Contain("Warnings");
        });
    }

    [Fact]
    public void QualityReview_ReviewPackCards_RenderCompactSummary()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var qaReport = new QaAuditReport
        {
            Findings = new List<QaFinding>
            {
                new() { RuleCode = "QA-1", Title = "Issue 1", Description = "Desc", Severity = QaSeverity.High, Category = QaCategory.Constitution },
            },
            HasConstitution = true,
        };

        var report = MakeReport(new QualityReviewPackResult
        {
            PackId = "qa-auditor",
            PackName = "QA Auditor",
            Score = 75,
            High = 1,
            QaAudit = qaReport
        });
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var packCard = cut.Find(".qr-pack-card");
            packCard.TextContent.Should().Contain("QA Auditor");
            packCard.TextContent.Should().Contain("75");
            packCard.TextContent.Should().Contain("1 finding");
            packCard.TextContent.Should().Contain("Highest: High");
        });
    }

    [Fact]
    public void QualityReview_ReviewPackCards_RenderStrongestAndWeakestBadges()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var report = MakeReport(
            new QualityReviewPackResult { PackId = "qa-auditor", PackName = "QA Auditor", Score = 95 },
            new QualityReviewPackResult { PackId = "compliance", PackName = "Compliance", Score = 30 }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var badges = cut.FindAll(".qr-pack-card-highlight");
            badges.Count.Should().Be(2);
            badges.Any(b => b.TextContent == "Strongest").Should().BeTrue();
            badges.Any(b => b.TextContent == "Weakest").Should().BeTrue();
        });
    }

    [Fact]
    public void QualityReview_QaAuditorPreview_WiresToQaFindingPreviewList()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var qaFindings = new List<QaFinding>
        {
            new() { RuleCode = "QA-001", Title = "Finding 1", Description = "D1", Severity = QaSeverity.High, Category = QaCategory.Constitution },
            new() { RuleCode = "QA-002", Title = "Finding 2", Description = "D2", Severity = QaSeverity.Medium, Category = QaCategory.Constitution },
            new() { RuleCode = "QA-003", Title = "Finding 3", Description = "D3", Severity = QaSeverity.High, Category = QaCategory.Constitution },
            new() { RuleCode = "QA-004", Title = "Finding 4", Description = "D4", Severity = QaSeverity.Medium, Category = QaCategory.Constitution },
            new() { RuleCode = "QA-005", Title = "Finding 5", Description = "D5", Severity = QaSeverity.High, Category = QaCategory.Constitution },
            new() { RuleCode = "QA-006", Title = "Finding 6", Description = "D6", Severity = QaSeverity.Low, Category = QaCategory.Constitution },
            new() { RuleCode = "QA-007", Title = "Finding 7", Description = "D7", Severity = QaSeverity.Low, Category = QaCategory.Constitution },
            new() { RuleCode = "QA-008", Title = "Finding 8", Description = "D8", Severity = QaSeverity.Info, Category = QaCategory.Constitution },
        };

        var qaReport = new QaAuditReport
        {
            Findings = qaFindings,
            HasConstitution = true,
            HasSpecification = true,
            HasPlan = true,
            HasTasks = true,
        };

        var report = MakeReport(new QualityReviewPackResult
        {
            PackId = "qa-auditor",
            PackName = "QA Auditor",
            PackGroup = "Quality",
            Score = 75,
            Critical = 0,
            High = 4,
            Medium = 2,
            Low = 1,
            Info = 1,
            QaAudit = qaReport,
        });

        _qualityReview.SetReport(report);
        var cut = Render<QualityReview>();

        // Select QA Auditor pack
        cut.WaitForAssertion(() =>
        {
            var qaLabel = FindPackLabel(cut, "QA Auditor");
            var qaCheckbox = qaLabel.QuerySelector("input[type=checkbox]");
            if (qaCheckbox != null && !qaCheckbox.HasAttribute("checked"))
                qaCheckbox.Click();
        });

        // Run review
        ClickRun(cut);

        // Open QA Auditor pack to see findings
        cut.WaitForAssertion(() =>
        {
            var qaAuditorHeader = cut.FindAll("button.qr-result-header").First(b => b.TextContent.Contains("QA Auditor"));
            qaAuditorHeader.Click();
        });

        cut.WaitForAssertion(() =>
        {
            // Initially, only 5 findings visible (QaFindingPreviewList preview limit)
            cut.Markup.Should().Contain("QA-001");
            cut.Markup.Should().Contain("QA-005");
            cut.Markup.Should().NotContain("QA-006");
            cut.Markup.Should().NotContain("QA-008");

            // Show all button from QaFindingPreviewList is present and clickable
            var showAllButton = cut.Find("button.qr-show-toggle");
            showAllButton.Should().NotBeNull();
            showAllButton.TextContent.Should().Contain("Show all");

            // Click Show all
            showAllButton.Click();
        }, timeout: TimeSpan.FromSeconds(3));

        // After toggle, all 8 findings visible
        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("QA-006");
            cut.Markup.Should().Contain("QA-008");
        });
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

    [Fact]
    public void QualityReview_TopIssues_DeduplicatesSameRuleAcrossPacks()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var qaReport = new QaAuditReport
        {
            Findings = new List<QaFinding>
            {
                new() { RuleCode = "PP-02", Title = "Principle 2", Description = "Missing coverage", Severity = QaSeverity.Critical, Category = QaCategory.Constitution },
            },
            HasConstitution = true,
        };

        var compReport = new ConstitutionComplianceReport
        {
            Gaps = new List<ComplianceGap>
            {
                new() { RuleId = "PP-02", RuleTitle = "Principle 2", RuleType = ConstitutionRuleType.Principle, MissingInSpec = true, MissingInPlan = true, Severity = ViolationSeverity.Critical },
            },
        };

        var report = MakeReport(
            new QualityReviewPackResult { PackId = "qa-auditor", PackName = "QA Auditor", Score = 60, Critical = 1, QaAudit = qaReport },
            new QualityReviewPackResult { PackId = "compliance", PackName = "Constitution Compliance", Score = 50, Critical = 1, Compliance = compReport }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var topIssueCards = cut.FindAll(".qr-issue-card");
            // Should have only ONE top issue for PP-02, not duplicated across packs
            var pp02Cards = topIssueCards.Where(c => c.TextContent.Contains("PP-02")).ToList();
            pp02Cards.Count.Should().Be(1);
        });
    }

    [Fact]
    public void QualityReview_TopIssues_ShowsAllReportingPacks()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var qaReport = new QaAuditReport
        {
            Findings = new List<QaFinding>
            {
                new() { RuleCode = "PP-02", Title = "Principle 2", Description = "Missing coverage", Severity = QaSeverity.Critical, Category = QaCategory.Constitution },
            },
            HasConstitution = true,
        };

        var compReport = new ConstitutionComplianceReport
        {
            Gaps = new List<ComplianceGap>
            {
                new() { RuleId = "PP-02", RuleTitle = "Principle 2", RuleType = ConstitutionRuleType.Principle, MissingInSpec = true, Severity = ViolationSeverity.Critical },
            },
        };

        var report = MakeReport(
            new QualityReviewPackResult { PackId = "qa-auditor", PackName = "QA Auditor", Score = 60, Critical = 1, QaAudit = qaReport },
            new QualityReviewPackResult { PackId = "compliance", PackName = "Constitution Compliance", Score = 50, Critical = 1, Compliance = compReport }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var topIssueCard = cut.FindAll(".qr-issue-card").FirstOrDefault(c => c.TextContent.Contains("PP-02"));
            topIssueCard.Should().NotBeNull();
            // Should show both source packs
            topIssueCard!.TextContent.Should().Contain("QA Auditor");
            topIssueCard.TextContent.Should().Contain("Constitution Compliance");
        });
    }

    [Fact]
    public void QualityReview_TopIssues_DuplicateRuleUsesHighestSeverity()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var qaReport = new QaAuditReport
        {
            Findings = new List<QaFinding>
            {
                new() { RuleCode = "PP-02", Title = "Principle 2", Severity = QaSeverity.High, Category = QaCategory.Constitution },
            },
            HasConstitution = true,
        };

        var compReport = new ConstitutionComplianceReport
        {
            Gaps = new List<ComplianceGap>
            {
                new() { RuleId = "PP-02", RuleTitle = "Principle 2", RuleType = ConstitutionRuleType.Principle, MissingInSpec = true, Severity = ViolationSeverity.Critical },
            },
        };

        var report = MakeReport(
            new QualityReviewPackResult { PackId = "qa-auditor", PackName = "QA Auditor", Score = 60, High = 1, QaAudit = qaReport },
            new QualityReviewPackResult { PackId = "compliance", PackName = "Constitution Compliance", Score = 50, Critical = 1, Compliance = compReport }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var topIssueCard = cut.Find(".qr-issue-card");
            // Should show highest severity (Critical, not High)
            topIssueCard.TextContent.ToUpper().Should().Contain("CRITICAL");
        });
    }

    [Fact]
    public void QualityReview_TopIssues_LimitsToFiveUniqueLogicalIssues()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var qaReport = new QaAuditReport
        {
            Findings = Enumerable.Range(1, 10)
                .Select(i => new QaFinding
                {
                    RuleCode = $"PP-{i:D2}",
                    Title = $"Principle {i}",
                    Severity = QaSeverity.Critical,
                    Category = QaCategory.Constitution,
                })
                .ToList(),
            HasConstitution = true,
        };

        var report = MakeReport(
            new QualityReviewPackResult { PackId = "qa-auditor", PackName = "QA Auditor", Score = 60, Critical = 10, QaAudit = qaReport }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var topIssueCards = cut.FindAll(".qr-issue-card");
            // Should show at most 5 unique logical issues
            topIssueCards.Count.Should().BeLessThanOrEqualTo(5);
        });
    }

    [Fact]
    public void QualityReview_TopIssues_ShowsRuleCodeAndDescription()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var compReport = new ConstitutionComplianceReport
        {
            Gaps = new List<ComplianceGap>
            {
                new() { RuleId = "PP-02", RuleTitle = "Principle 2 — Clarity", RuleType = ConstitutionRuleType.Principle, MissingInSpec = true, MissingInPlan = true, Severity = ViolationSeverity.Critical },
            },
        };

        var report = MakeReport(
            new QualityReviewPackResult { PackId = "compliance", PackName = "Constitution Compliance", Score = 50, Critical = 1, Compliance = compReport }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var topIssueCard = cut.Find(".qr-issue-card");
            // Should show code AND meaningful title/description
            topIssueCard.TextContent.Should().Contain("PP-02");
            topIssueCard.TextContent.Should().Contain("Clarity");
            topIssueCard.TextContent.Should().Contain("Missing");
        });
    }

    [Fact]
    public void QualityReview_TopIssues_DoesNotRenderDuplicateRuleCodeTitle()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var qaReport = new QaAuditReport
        {
            Findings = new List<QaFinding>
            {
                new() { RuleCode = "TEST-001", Title = "TEST-001", Severity = QaSeverity.Critical, Category = QaCategory.Constitution },
            },
            HasConstitution = true,
        };

        var report = MakeReport(
            new QualityReviewPackResult { PackId = "qa-auditor", PackName = "QA Auditor", Score = 60, Critical = 1, QaAudit = qaReport }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var topIssueCard = cut.Find(".qr-issue-card");
            // Should NOT show duplicated code like "TEST-001 — TEST-001"
            var content = topIssueCard.TextContent;
            content.Should().NotContain("TEST-001 — TEST-001");
        });
    }

    [Fact]
    public void QualityReview_TopIssues_DoesNotFillSlotsWithDuplicateSources()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var qaReport = new QaAuditReport
        {
            Findings = new List<QaFinding>
            {
                new() { RuleCode = "PP-02", Title = "Principle 2", Severity = QaSeverity.Critical, Category = QaCategory.Constitution },
                new() { RuleCode = "PP-04", Title = "Principle 4", Severity = QaSeverity.High, Category = QaCategory.Constitution },
            },
            HasConstitution = true,
        };

        var compReport = new ConstitutionComplianceReport
        {
            Gaps = new List<ComplianceGap>
            {
                new() { RuleId = "PP-02", RuleTitle = "Principle 2", RuleType = ConstitutionRuleType.Principle, MissingInSpec = true, Severity = ViolationSeverity.Critical },
                new() { RuleId = "PP-04", RuleTitle = "Principle 4", RuleType = ConstitutionRuleType.Principle, MissingInPlan = true, Severity = ViolationSeverity.High },
            },
        };

        var drReport = new DeliveryReadinessReport
        {
            Blockers = new List<ReadinessBlocker>
            {
                new() { Title = "Release gate", Severity = GateSeverity.Critical },
            },
        };

        var report = MakeReport(
            new QualityReviewPackResult { PackId = "qa-auditor", PackName = "QA Auditor", Score = 60, Critical = 2, High = 1, QaAudit = qaReport },
            new QualityReviewPackResult { PackId = "compliance", PackName = "Constitution Compliance", Score = 50, Critical = 1, High = 1, Compliance = compReport },
            new QualityReviewPackResult { PackId = "delivery", PackName = "Delivery Readiness", Score = 70, Critical = 1, DeliveryReadiness = drReport }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var topIssueCards = cut.FindAll(".qr-issue-card");
            // Should have exactly 3 unique logical issues, NOT 5 with duplicate fills
            topIssueCards.Count.Should().Be(3);

            // Verify the three are the expected unique logical IDs
            var content = string.Concat(topIssueCards.Select(c => c.TextContent));
            content.Should().Contain("PP-02");
            content.Should().Contain("PP-04");
            content.Should().Contain("Release gate");
        });
    }

    [Fact]
    public void QualityReview_TopIssue_ClickOpensCanonicalPack()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var qaReport = new QaAuditReport
        {
            Findings = new List<QaFinding>
            {
                new() { RuleCode = "PP-02", Title = "Principle 2", Severity = QaSeverity.Critical, Category = QaCategory.Constitution },
            },
            HasConstitution = true,
        };

        var compReport = new ConstitutionComplianceReport
        {
            Gaps = new List<ComplianceGap>
            {
                new() { RuleId = "PP-02", RuleTitle = "Principle 2", RuleType = ConstitutionRuleType.Principle, MissingInSpec = true, Severity = ViolationSeverity.Critical },
            },
        };

        var report = MakeReport(
            new QualityReviewPackResult { PackId = "qa-auditor", PackName = "QA Auditor", Score = 60, Critical = 1, QaAudit = qaReport },
            new QualityReviewPackResult { PackId = "compliance", PackName = "Constitution Compliance", Score = 50, Critical = 1, Compliance = compReport }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            // Initial state: packs collapsed
            var complianceSection = cut.FindAll(".qr-result-section").FirstOrDefault(s => s.TextContent.Contains("Constitution Compliance"));
            complianceSection.Should().NotBeNull();

            // Find and click the PP-02 Top Issue card (should be semantic button)
            var topIssueButton = cut.FindAll(".qr-issue-card").FirstOrDefault(c => c.TextContent.Contains("PP-02"));
            topIssueButton.Should().NotBeNull();

            // Verify it's a button element
            topIssueButton!.TagName.Should().Be("BUTTON");

            // Click it
            topIssueButton.Click();

            // After click: Constitution Compliance should be expanded, QA Auditor collapsed
            cut.WaitForAssertion(() =>
            {
                var qaSection = cut.FindAll(".qr-result-section").FirstOrDefault(s => s.TextContent.Contains("QA Auditor"));
                var compSection = cut.FindAll(".qr-result-section").FirstOrDefault(s => s.TextContent.Contains("Constitution Compliance"));

                // Verify Constitution Compliance opened
                compSection?.GetAttribute("style").Should().NotContain("display:none");
            });
        });
    }

    [Fact]
    public void QualityReview_TopIssues_DeliveryBlockerKeepsFix()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var drReport = new DeliveryReadinessReport
        {
            Blockers = new List<ReadinessBlocker>
            {
                new() { Title = "Development gate not cleared", Severity = GateSeverity.Critical, Description = "Development readiness must reach MostlyReady before release assessment." },
            },
        };

        var report = MakeReport(
            new QualityReviewPackResult { PackId = "delivery", PackName = "Delivery Readiness", Score = 70, Critical = 1, DeliveryReadiness = drReport }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var topIssueCard = cut.Find(".qr-issue-card");
            // Should show blocker title
            topIssueCard.TextContent.Should().Contain("Development gate");
            // Should show Delivery Readiness source
            topIssueCard.TextContent.Should().Contain("Delivery Readiness");
            // Should show Fix: and recommendation
            topIssueCard.TextContent.Should().Contain("Fix:");
            topIssueCard.TextContent.Should().Contain("MostlyReady");
        });
    }

    [Fact]
    public void QualityReview_QaAuditorCoverageFinding_RendersConciseDescription()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var qaReport = new QaAuditReport
        {
            Findings = new List<QaFinding>
            {
                new() { RuleCode = "PP-02", Title = "Principle 2", Description = "Rule 'PP-02' (Principle) has no coverage in the Specification, Plan, or Tasks.", Severity = QaSeverity.Critical, Category = QaCategory.Constitution },
            },
            HasConstitution = true,
        };

        var report = MakeReport(
            new QualityReviewPackResult { PackId = "qa-auditor", PackName = "QA Auditor", Score = 60, Critical = 1, QaAudit = qaReport }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        // Open QA Auditor section to see findings
        cut.WaitForAssertion(() =>
        {
            var qaAuditorHeader = cut.FindAll("button.qr-result-header").First(b => b.TextContent.Contains("QA Auditor"));
            qaAuditorHeader.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var findingCard = cut.Find(".qr-finding-card");
            // Should show concise coverage description
            findingCard.TextContent.Should().Contain("Missing coverage in");
            // Should NOT show the redundant raw sentence as primary
            findingCard.TextContent.Should().NotContain("Rule 'PP-02' (Principle) has no coverage");
        });
    }

    [Fact]
    public void QualityReview_QaAuditorFinding_ShowsCanonicalRuleTitle()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var qaReport = new QaAuditReport
        {
            Findings = new List<QaFinding>
            {
                new() { RuleCode = "PRINCIPLE-001", Title = "Headless API Communication", Severity = QaSeverity.High, Category = QaCategory.Specification },
            },
            HasConstitution = true,
        };

        var report = MakeReport(
            new QualityReviewPackResult { PackId = "qa-auditor", PackName = "QA Auditor", Score = 60, High = 1, QaAudit = qaReport }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var qaAuditorHeader = cut.FindAll("button.qr-result-header").First(b => b.TextContent.Contains("QA Auditor"));
            qaAuditorHeader.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var findingCard = cut.Find(".qr-finding-card");
            findingCard.TextContent.Should().Contain("PRINCIPLE-001");
            findingCard.TextContent.Should().Contain("Headless API Communication");
        });
    }

    [Fact]
    public void QualityReview_QaAuditorFinding_DoesNotDuplicateRuleCode()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var qaReport = new QaAuditReport
        {
            Findings = new List<QaFinding>
            {
                new() { RuleCode = "PP-02", Title = "PP-02", Severity = QaSeverity.Critical, Category = QaCategory.Constitution },
            },
            HasConstitution = true,
        };

        var report = MakeReport(
            new QualityReviewPackResult { PackId = "qa-auditor", PackName = "QA Auditor", Score = 60, Critical = 1, QaAudit = qaReport }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var qaAuditorHeader = cut.FindAll("button.qr-result-header").First(b => b.TextContent.Contains("QA Auditor"));
            qaAuditorHeader.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var findingCard = cut.Find(".qr-finding-card");
            var content = findingCard.TextContent;
            // Should not show "PP-02 — PP-02"
            content.Should().NotContain("PP-02 — PP-02");
            // But should show PP-02 at least once
            content.Should().Contain("PP-02");
        });
    }

    [Fact]
    public void QualityReview_QaAuditorFinding_CategoryVisible()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var qaReport = new QaAuditReport
        {
            Findings = new List<QaFinding>
            {
                new() { RuleCode = "PP-02", Title = "Principle 2", Severity = QaSeverity.Critical, Category = QaCategory.Constitution },
            },
            HasConstitution = true,
        };

        var report = MakeReport(
            new QualityReviewPackResult { PackId = "qa-auditor", PackName = "QA Auditor", Score = 60, Critical = 1, QaAudit = qaReport }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var qaAuditorHeader = cut.FindAll("button.qr-result-header").First(b => b.TextContent.Contains("QA Auditor"));
            qaAuditorHeader.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var categoryElement = cut.Find(".qr-finding-category");
            categoryElement.TextContent.Should().Contain("Constitution");
        });
    }

    [Fact]
    public void QualityReview_QaAuditorShowAll_UsesSemanticButton()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var qaReport = new QaAuditReport
        {
            Findings = Enumerable.Range(1, 10)
                .Select(i => new QaFinding
                {
                    RuleCode = $"TEST-{i:D3}",
                    Title = $"Finding {i}",
                    Severity = QaSeverity.High,
                    Category = QaCategory.Specification,
                })
                .ToList(),
            HasConstitution = true,
        };

        var report = MakeReport(
            new QualityReviewPackResult { PackId = "qa-auditor", PackName = "QA Auditor", Score = 60, High = 10, QaAudit = qaReport }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var qaAuditorHeader = cut.FindAll("button.qr-result-header").First(b => b.TextContent.Contains("QA Auditor"));
            qaAuditorHeader.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var showAllButton = cut.Find(".qr-show-toggle");
            // Should be a semantic button element
            showAllButton.TagName.Should().Be("BUTTON");
            // Should show the count
            showAllButton.TextContent.Should().Contain("10");
            // Should have aria-label
            showAllButton.HasAttribute("aria-label").Should().BeTrue();
        });
    }

    [Fact]
    public void QualityReview_QaAuditorFinding_UsesCompactPreviewPresentation()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var qaReport = new QaAuditReport
        {
            Findings = new List<QaFinding>
            {
                new() { RuleCode = "PP-02", Title = "Principle 2", Description = "Rule 'PP-02' (Principle) has no coverage in the Specification, Plan and Tasks.", Severity = QaSeverity.Critical, Category = QaCategory.Constitution },
            },
            HasConstitution = true,
            HasSpecification = true,
            HasPlan = true,
            HasTasks = true,
        };

        var report = MakeReport(
            new QualityReviewPackResult { PackId = "qa-auditor", PackName = "QA Auditor", Score = 60, Critical = 1, QaAudit = qaReport }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var qaAuditorHeader = cut.FindAll("button.qr-result-header").First(b => b.TextContent.Contains("QA Auditor"));
            qaAuditorHeader.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var findingCard = cut.Find(".qr-finding-card");
            // Should show compact presentation
            findingCard.TextContent.Should().Contain("PP-02");
            findingCard.TextContent.Should().Contain("Constitution");
            findingCard.TextContent.Should().Contain("Missing coverage in Specification, Plan and Tasks.");
            // Should NOT show duplicated code
            var content = findingCard.TextContent;
            content.Should().NotContain("PP-02 — PP-02");
        });
    }

    [Fact]
    public void QualityReview_OverallSummary_RenderScoreAndStatusTogether()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var report = MakeReport(
            new QualityReviewPackResult { PackId = "qa-auditor", PackName = "QA Auditor", Score = 37 }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var overallSummary = cut.Find(".qr-overall");
            overallSummary.Should().NotBeNull();

            var scoreRing = overallSummary.QuerySelector(".qr-score-ring-wrap");
            scoreRing.Should().NotBeNull("score ring visible");

            var scoreStatus = overallSummary.QuerySelector(".qr-score-status");
            scoreStatus.Should().NotBeNull("status text visible");
            scoreStatus.TextContent.Should().Contain("Needs attention");

            var overallContainer = overallSummary.QuerySelector(".qr-overall-meta");
            overallContainer.TextContent.Should().Contain("finding");
            overallContainer.TextContent.Should().Contain("pack");
        });
    }

    [Fact]
    public void QualityReview_ReviewPackCard_ClickOpensPack()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var qaReport = new QaAuditReport
        {
            Findings = new List<QaFinding> { new() { RuleCode = "QA-001", Title = "Finding 1", Severity = QaSeverity.High, Category = QaCategory.Constitution } },
            HasConstitution = true,
        };

        var report = MakeReport(
            new QualityReviewPackResult { PackId = "qa-auditor", PackName = "QA Auditor", Score = 75, High = 1, QaAudit = qaReport }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var qaAuditorCard = cut.FindAll(".qr-pack-card").FirstOrDefault(c => c.TextContent.Contains("QA Auditor"));
            qaAuditorCard.Should().NotBeNull();
            qaAuditorCard.TagName.Should().Be("BUTTON");

            // Initially collapsed
            var qaSection = cut.FindAll(".qr-result-section").FirstOrDefault(s => s.TextContent.Contains("QA Auditor"));
            qaSection?.GetAttribute("style")?.Should().Contain("display:none");

            // Click pack card
            qaAuditorCard.Click();
        }, timeout: TimeSpan.FromSeconds(2));

        cut.WaitForAssertion(() =>
        {
            // Now QA Auditor should be expanded
            var qaSection = cut.FindAll(".qr-result-section").FirstOrDefault(s => s.TextContent.Contains("QA Auditor"));
            qaSection?.GetAttribute("style")?.Should().NotContain("display:none");
        });
    }

    [Fact]
    public void QualityReview_ReviewPackCard_SingleOpenBehavior()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var qaReport = new QaAuditReport { HasConstitution = true };
        var compReport = new ConstitutionComplianceReport { };

        var report = MakeReport(
            new QualityReviewPackResult { PackId = "qa-auditor", PackName = "QA Auditor", Score = 75, QaAudit = qaReport },
            new QualityReviewPackResult { PackId = "compliance", PackName = "Constitution Compliance", Score = 50, Compliance = compReport }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var qaCard = cut.FindAll(".qr-pack-card").FirstOrDefault(c => c.TextContent.Contains("QA Auditor"));
            qaCard.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var qaSection = cut.FindAll(".qr-result-section").FirstOrDefault(s => s.TextContent.Contains("QA Auditor"));
            qaSection?.GetAttribute("style")?.Should().NotContain("display:none");
        });

        cut.WaitForAssertion(() =>
        {
            var compCard = cut.FindAll(".qr-pack-card").FirstOrDefault(c => c.TextContent.Contains("Constitution Compliance"));
            compCard.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var qaSection = cut.FindAll(".qr-result-section").FirstOrDefault(s => s.TextContent.Contains("QA Auditor"));
            var compSection = cut.FindAll(".qr-result-section").FirstOrDefault(s => s.TextContent.Contains("Constitution Compliance"));

            compSection?.GetAttribute("style")?.Should().NotContain("display:none");
            qaSection?.GetAttribute("style")?.Should().Contain("display:none");
        });
    }

    [Fact]
    public void QualityReview_ReviewPackCard_ZeroFindings_CompactMetadata()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var report = MakeReport(
            new QualityReviewPackResult { PackId = "qa-auditor", PackName = "QA Auditor", Score = 100, Critical = 0, High = 0, Medium = 0, Low = 0, QaAudit = new QaAuditReport { HasConstitution = true } }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var packCard = cut.Find(".qr-pack-card");
            var metadata = packCard.QuerySelector(".qr-pack-card-metadata");
            metadata.Should().NotBeNull();
            metadata.TextContent.Should().Contain("No issues");
            metadata.TextContent.Should().NotContain("Highest:");
        });
    }

    [Fact]
    public void QualityReview_SampleProjectArtifacts_CollapsedStateCompact()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var report = MakeReport(
            new QualityReviewPackResult { PackId = "qa-auditor", PackName = "QA Auditor", Score = 75 },
            new QualityReviewPackResult { PackId = "compliance", PackName = "Constitution Compliance", Score = 50 }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var inputPanel = cut.Find(".qr-input-panel");
            inputPanel.ClassList.Should().Contain("qr-input-panel");

            var header = inputPanel.QuerySelector(".qr-input-panel-header");
            header.Should().NotBeNull();
            header.TextContent.Should().Contain("Sample Project Artifacts");

            var toggle = header.QuerySelector(".qr-input-panel-toggle");
            toggle.Should().NotBeNull("toggle button should exist in input panel header");
            toggle.TextContent.Should().Contain("Expand");
        });
    }

    [Fact]
    public void QualityReview_TopIssues_QaAuditorConst001AndCompliancePp02_DeduplicateByAffectedRule()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var qaReport = new QaAuditReport
        {
            Findings = new List<QaFinding>
            {
                new()
                {
                    RuleCode = "CONST-001",
                    Title = "Constitution rule PP-02 not covered by any artifact",
                    Description = "Rule 'PP-02' (Principle) has no coverage...",
                    Severity = QaSeverity.Critical,
                    Category = QaCategory.Constitution,
                    AffectedArtifact = "PP-02",
                }
            },
            HasConstitution = true,
        };

        var complianceReport = new ConstitutionComplianceReport
        {
            Results = new List<ComplianceResult>
            {
                new()
                {
                    RuleId = "PP-02",
                    RuleTitle = "Clear and Testable Requirements",
                    RuleType = ConstitutionRuleType.Principle,
                    Status = ComplianceStatus.Missing,
                    HasSpecCoverage = false,
                    HasPlanCoverage = false,
                    HasTaskCoverage = false,
                }
            },
            Gaps = new List<ComplianceGap>
            {
                new()
                {
                    RuleId = "PP-02",
                    RuleTitle = "Clear and Testable Requirements",
                    RuleType = ConstitutionRuleType.Principle,
                    MissingInSpec = true,
                    MissingInPlan = true,
                    MissingInTasks = true,
                    Severity = ViolationSeverity.Critical,
                }
            }
        };

        var report = MakeReport(
            new QualityReviewPackResult
            {
                PackId = "qa-auditor",
                PackName = "QA Auditor",
                Score = 50,
                Critical = 1,
                QaAudit = qaReport
            },
            new QualityReviewPackResult
            {
                PackId = "compliance",
                PackName = "Constitution Compliance",
                Score = 50,
                Compliance = complianceReport
            }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var topIssueCards = cut.FindAll(".qr-issue-card");
            // Must contain exactly ONE PP-02 issue (not separate CONST-001 + PP-02)
            var pp02Cards = topIssueCards.Where(c => c.TextContent.Contains("PP-02")).ToList();
            pp02Cards.Should().HaveCount(1, "PP-02 from QA Auditor and Constitution Compliance should deduplicate");

            var card = pp02Cards.First();
            card.TextContent.Should().Contain("PP-02");
            card.TextContent.Should().Contain("Clear and Testable Requirements");
            card.TextContent.Should().NotContain("CONST-001");
        });
    }

    [Fact]
    public void QualityReview_TopIssues_RuntimeCoverageDuplicate_ShowsBothReportingPacks()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var qaReport = new QaAuditReport
        {
            Findings = new List<QaFinding>
            {
                new()
                {
                    RuleCode = "CONST-001",
                    Title = "Constitution rule PP-02 not covered by any artifact",
                    Description = "Rule 'PP-02' (Principle) has no coverage...",
                    Severity = QaSeverity.Critical,
                    Category = QaCategory.Constitution,
                    AffectedArtifact = "PP-02",
                }
            },
            HasConstitution = true,
        };

        var complianceReport = new ConstitutionComplianceReport
        {
            Results = new List<ComplianceResult>
            {
                new()
                {
                    RuleId = "PP-02",
                    RuleTitle = "Clear and Testable Requirements",
                    RuleType = ConstitutionRuleType.Principle,
                    Status = ComplianceStatus.Missing,
                    HasSpecCoverage = false,
                    HasPlanCoverage = false,
                    HasTaskCoverage = false,
                }
            },
            Gaps = new List<ComplianceGap>
            {
                new()
                {
                    RuleId = "PP-02",
                    RuleTitle = "Clear and Testable Requirements",
                    RuleType = ConstitutionRuleType.Principle,
                    MissingInSpec = true,
                    MissingInPlan = true,
                    MissingInTasks = true,
                    Severity = ViolationSeverity.Critical,
                }
            }
        };

        var report = MakeReport(
            new QualityReviewPackResult
            {
                PackId = "qa-auditor",
                PackName = "QA Auditor",
                Score = 50,
                Critical = 1,
                QaAudit = qaReport
            },
            new QualityReviewPackResult
            {
                PackId = "compliance",
                PackName = "Constitution Compliance",
                Score = 50,
                Compliance = complianceReport
            }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var pp02Card = cut.FindAll(".qr-issue-card").First(c => c.TextContent.Contains("PP-02"));
            pp02Card.TextContent.Should().Contain("QA Auditor");
            pp02Card.TextContent.Should().Contain("Constitution Compliance");
            pp02Card.TextContent.Should().Contain("Reported by 2 packs");
        });
    }

    [Fact]
    public void QualityReview_TopIssues_ConstitutionCoverage_UsesCanonicalRuleTitle()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var qaReport = new QaAuditReport
        {
            Findings = new List<QaFinding>
            {
                new()
                {
                    RuleCode = "CONST-001",
                    Title = "Constitution rule PP-02 not covered by any artifact",
                    Description = "Rule 'PP-02' (Principle) has no coverage...",
                    Severity = QaSeverity.Critical,
                    Category = QaCategory.Constitution,
                    AffectedArtifact = "PP-02",
                }
            },
            HasConstitution = true,
        };

        var complianceReport = new ConstitutionComplianceReport
        {
            Results = new List<ComplianceResult>
            {
                new()
                {
                    RuleId = "PP-02",
                    RuleTitle = "Clear and Testable Requirements",
                    RuleType = ConstitutionRuleType.Principle,
                    Status = ComplianceStatus.Missing,
                    HasSpecCoverage = false,
                    HasPlanCoverage = false,
                    HasTaskCoverage = false,
                }
            },
            Gaps = new List<ComplianceGap>
            {
                new()
                {
                    RuleId = "PP-02",
                    RuleTitle = "Clear and Testable Requirements",
                    RuleType = ConstitutionRuleType.Principle,
                    MissingInSpec = true,
                    MissingInPlan = true,
                    MissingInTasks = true,
                    Severity = ViolationSeverity.Critical,
                }
            }
        };

        var report = MakeReport(
            new QualityReviewPackResult
            {
                PackId = "qa-auditor",
                PackName = "QA Auditor",
                Score = 50,
                Critical = 1,
                QaAudit = qaReport
            },
            new QualityReviewPackResult
            {
                PackId = "compliance",
                PackName = "Constitution Compliance",
                Score = 50,
                Compliance = complianceReport
            }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var pp02Card = cut.FindAll(".qr-issue-card").First(c => c.TextContent.Contains("PP-02"));
            pp02Card.TextContent.Should().Contain("PP-02 — Clear and Testable Requirements");
        });
    }

    [Fact]
    public void QualityReview_TopIssues_ConstitutionCoverage_EnrichesMultipleRuleTitles()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var qaReport = new QaAuditReport
        {
            Findings = new List<QaFinding>
            {
                new()
                {
                    RuleCode = "CONST-001",
                    Title = "Constitution rule PP-02 not covered by any artifact",
                    Description = "Rule 'PP-02' (Principle) has no coverage...",
                    Severity = QaSeverity.Critical,
                    Category = QaCategory.Constitution,
                    AffectedArtifact = "PP-02",
                },
                new()
                {
                    RuleCode = "CONST-001",
                    Title = "Constitution rule PP-04 not covered by any artifact",
                    Description = "Rule 'PP-04' (Principle) has no coverage...",
                    Severity = QaSeverity.High,
                    Category = QaCategory.Constitution,
                    AffectedArtifact = "PP-04",
                },
                new()
                {
                    RuleCode = "CONST-001",
                    Title = "Constitution rule PP-09 not covered by any artifact",
                    Description = "Rule 'PP-09' (Standard) has no coverage...",
                    Severity = QaSeverity.Medium,
                    Category = QaCategory.Constitution,
                    AffectedArtifact = "PP-09",
                }
            },
            HasConstitution = true,
        };

        var complianceReport = new ConstitutionComplianceReport
        {
            Results = new List<ComplianceResult>
            {
                new() { RuleId = "PP-02", RuleTitle = "Title A", RuleType = ConstitutionRuleType.Principle, Status = ComplianceStatus.Missing, HasSpecCoverage = false, HasPlanCoverage = false, HasTaskCoverage = false },
                new() { RuleId = "PP-04", RuleTitle = "Title B", RuleType = ConstitutionRuleType.Principle, Status = ComplianceStatus.Missing, HasSpecCoverage = false, HasPlanCoverage = false, HasTaskCoverage = false },
                new() { RuleId = "PP-09", RuleTitle = "Title C", RuleType = ConstitutionRuleType.Standard, Status = ComplianceStatus.Missing, HasSpecCoverage = false, HasPlanCoverage = false, HasTaskCoverage = false },
            },
            Gaps = new List<ComplianceGap>
            {
                new() { RuleId = "PP-02", RuleTitle = "Title A", RuleType = ConstitutionRuleType.Principle, MissingInSpec = true, MissingInPlan = true, MissingInTasks = true, Severity = ViolationSeverity.Critical },
                new() { RuleId = "PP-04", RuleTitle = "Title B", RuleType = ConstitutionRuleType.Principle, MissingInSpec = true, MissingInPlan = true, MissingInTasks = true, Severity = ViolationSeverity.High },
                new() { RuleId = "PP-09", RuleTitle = "Title C", RuleType = ConstitutionRuleType.Standard, MissingInSpec = true, MissingInPlan = true, MissingInTasks = true, Severity = ViolationSeverity.Medium },
            }
        };

        var report = MakeReport(
            new QualityReviewPackResult
            {
                PackId = "qa-auditor",
                PackName = "QA Auditor",
                Score = 30,
                Critical = 1,
                High = 1,
                Medium = 1,
                QaAudit = qaReport
            },
            new QualityReviewPackResult
            {
                PackId = "compliance",
                PackName = "Constitution Compliance",
                Score = 30,
                Compliance = complianceReport
            }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var cards = cut.FindAll(".qr-issue-card");
            cards.Select(c => c.TextContent).Should().Contain(t => t.Contains("PP-02 — Title A"));
            cards.Select(c => c.TextContent).Should().Contain(t => t.Contains("PP-04 — Title B"));
            cards.Select(c => c.TextContent).Should().Contain(t => t.Contains("PP-09 — Title C"));
        });
    }

    [Fact]
    public void QualityReview_TopIssues_ConstitutionCoverage_MissingCanonicalTitleFallsBackToRuleId()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var qaReport = new QaAuditReport
        {
            Findings = new List<QaFinding>
            {
                new()
                {
                    RuleCode = "CONST-001",
                    Title = "Constitution rule PP-99 not covered by any artifact",
                    Description = "Rule 'PP-99' has no coverage...",
                    Severity = QaSeverity.Medium,
                    Category = QaCategory.Constitution,
                    AffectedArtifact = "PP-99",
                }
            },
            HasConstitution = true,
        };

        var report = MakeReport(
            new QualityReviewPackResult
            {
                PackId = "qa-auditor",
                PackName = "QA Auditor",
                Score = 50,
                Medium = 1,
                QaAudit = qaReport
            }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var card = cut.FindAll(".qr-issue-card").First(c => c.TextContent.Contains("PP-99"));
            // Without canonical title, should just show rule ID
            card.TextContent.Should().Contain("PP-99");
            // Should NOT show "PP-99 — " with empty title
            card.TextContent.Should().NotContain("PP-99 — ");
        });
    }

    [Fact]
    public void QualityReview_TopIssues_ConstitutionCoverage_RendersActionableFix()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var qaReport = new QaAuditReport
        {
            Findings = new List<QaFinding>
            {
                new()
                {
                    RuleCode = "CONST-001",
                    Title = "Constitution rule PP-02 not covered by any artifact",
                    Description = "Rule 'PP-02' (Principle) has no coverage...",
                    Severity = QaSeverity.Critical,
                    Category = QaCategory.Constitution,
                    AffectedArtifact = "PP-02",
                }
            },
            HasConstitution = true,
        };

        var complianceReport = new ConstitutionComplianceReport
        {
            Results = new List<ComplianceResult>
            {
                new()
                {
                    RuleId = "PP-02",
                    RuleTitle = "Clear and Testable Requirements",
                    RuleType = ConstitutionRuleType.Principle,
                    Status = ComplianceStatus.Missing,
                    HasSpecCoverage = false,
                    HasPlanCoverage = false,
                    HasTaskCoverage = false,
                }
            },
            Gaps = new List<ComplianceGap>
            {
                new()
                {
                    RuleId = "PP-02",
                    RuleTitle = "Clear and Testable Requirements",
                    RuleType = ConstitutionRuleType.Principle,
                    MissingInSpec = true,
                    MissingInPlan = true,
                    MissingInTasks = true,
                    Severity = ViolationSeverity.Critical,
                }
            }
        };

        var report = MakeReport(
            new QualityReviewPackResult
            {
                PackId = "qa-auditor",
                PackName = "QA Auditor",
                Score = 50,
                Critical = 1,
                QaAudit = qaReport
            },
            new QualityReviewPackResult
            {
                PackId = "compliance",
                PackName = "Constitution Compliance",
                Score = 50,
                Compliance = complianceReport
            }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var card = cut.FindAll(".qr-issue-card").First(c => c.TextContent.Contains("PP-02"));
            card.TextContent.Should().Contain("Fix: Add coverage in Specification, Plan and Tasks.");
            card.TextContent.Should().NotContain("Rule 'PP-02' (Principle) has no coverage");
        });
    }

    [Fact]
    public void QualityReview_TopIssues_ConstitutionCoverage_FixUsesActualMissingArtifacts()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var qaReport = new QaAuditReport
        {
            Findings = new List<QaFinding>
            {
                new()
                {
                    RuleCode = "CONST-001",
                    Title = "Constitution rule PP-02 not covered by any artifact",
                    Description = "Rule 'PP-02' (Principle) has no coverage...",
                    Severity = QaSeverity.Critical,
                    Category = QaCategory.Constitution,
                    AffectedArtifact = "PP-02",
                }
            },
            HasConstitution = true,
        };

        var complianceReport = new ConstitutionComplianceReport
        {
            Results = new List<ComplianceResult>
            {
                new()
                {
                    RuleId = "PP-02",
                    RuleTitle = "Clear and Testable Requirements",
                    RuleType = ConstitutionRuleType.Principle,
                    Status = ComplianceStatus.Missing,
                    HasSpecCoverage = true,  // Only Spec is covered
                    HasPlanCoverage = false,
                    HasTaskCoverage = false,
                }
            },
            Gaps = new List<ComplianceGap>
            {
                new()
                {
                    RuleId = "PP-02",
                    RuleTitle = "Clear and Testable Requirements",
                    RuleType = ConstitutionRuleType.Principle,
                    MissingInSpec = false,
                    MissingInPlan = true,
                    MissingInTasks = true,
                    Severity = ViolationSeverity.Critical,
                }
            }
        };

        var report = MakeReport(
            new QualityReviewPackResult
            {
                PackId = "qa-auditor",
                PackName = "QA Auditor",
                Score = 50,
                Critical = 1,
                QaAudit = qaReport
            },
            new QualityReviewPackResult
            {
                PackId = "compliance",
                PackName = "Constitution Compliance",
                Score = 50,
                Compliance = complianceReport
            }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var card = cut.FindAll(".qr-issue-card").First(c => c.TextContent.Contains("PP-02"));
            card.TextContent.Should().Contain("Fix: Add coverage in Plan and Tasks.");
            card.TextContent.Should().NotContain("Specification");
        });
    }

    [Fact]
    public void QualityReview_TopIssue_RuntimeConst001Duplicate_ClickOpensConstitutionCompliance()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var qaReport = new QaAuditReport
        {
            Findings = new List<QaFinding>
            {
                new()
                {
                    RuleCode = "CONST-001",
                    Title = "Constitution rule PP-02 not covered by any artifact",
                    Description = "Rule 'PP-02' (Principle) has no coverage...",
                    Severity = QaSeverity.Critical,
                    Category = QaCategory.Constitution,
                    AffectedArtifact = "PP-02",
                }
            },
            HasConstitution = true,
        };

        var complianceReport = new ConstitutionComplianceReport
        {
            Results = new List<ComplianceResult>
            {
                new()
                {
                    RuleId = "PP-02",
                    RuleTitle = "Clear and Testable Requirements",
                    RuleType = ConstitutionRuleType.Principle,
                    Status = ComplianceStatus.Missing,
                    HasSpecCoverage = false,
                    HasPlanCoverage = false,
                    HasTaskCoverage = false,
                }
            },
            Gaps = new List<ComplianceGap>
            {
                new()
                {
                    RuleId = "PP-02",
                    RuleTitle = "Clear and Testable Requirements",
                    RuleType = ConstitutionRuleType.Principle,
                    MissingInSpec = true,
                    MissingInPlan = true,
                    MissingInTasks = true,
                    Severity = ViolationSeverity.Critical,
                }
            }
        };

        var report = MakeReport(
            new QualityReviewPackResult
            {
                PackId = "qa-auditor",
                PackName = "QA Auditor",
                Score = 50,
                Critical = 1,
                QaAudit = qaReport
            },
            new QualityReviewPackResult
            {
                PackId = "compliance",
                PackName = "Constitution Compliance",
                Score = 50,
                Compliance = complianceReport
            }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var pp02Card = cut.FindAll(".qr-issue-card").First(c => c.TextContent.Contains("PP-02"));
            pp02Card.Click();
        });

        cut.WaitForAssertion(() =>
        {
            // After clicking, Constitution Compliance section should be expanded
            var constitutionResults = cut.FindAll(".qr-result-name").Where(e => e.TextContent.Contains("Constitution Compliance")).ToList();
            constitutionResults.Should().NotBeEmpty();
        });
    }

    [Fact]
    public void QualityReview_TopIssues_RuntimeDedup_DoesNotModifyUnderlyingPackFindings()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var qaReport = new QaAuditReport
        {
            Findings = new List<QaFinding>
            {
                new()
                {
                    RuleCode = "CONST-001",
                    Title = "Constitution rule PP-02 not covered by any artifact",
                    Description = "Rule 'PP-02' (Principle) has no coverage...",
                    Severity = QaSeverity.Critical,
                    Category = QaCategory.Constitution,
                    AffectedArtifact = "PP-02",
                }
            },
            HasConstitution = true,
        };

        var complianceReport = new ConstitutionComplianceReport
        {
            Results = new List<ComplianceResult>
            {
                new()
                {
                    RuleId = "PP-02",
                    RuleTitle = "Clear and Testable Requirements",
                    RuleType = ConstitutionRuleType.Principle,
                    Status = ComplianceStatus.Missing,
                    HasSpecCoverage = false,
                    HasPlanCoverage = false,
                    HasTaskCoverage = false,
                }
            },
            Gaps = new List<ComplianceGap>
            {
                new()
                {
                    RuleId = "PP-02",
                    RuleTitle = "Clear and Testable Requirements",
                    RuleType = ConstitutionRuleType.Principle,
                    MissingInSpec = true,
                    MissingInPlan = true,
                    MissingInTasks = true,
                    Severity = ViolationSeverity.Critical,
                }
            }
        };

        var report = MakeReport(
            new QualityReviewPackResult
            {
                PackId = "qa-auditor",
                PackName = "QA Auditor",
                Score = 50,
                Critical = 1,
                QaAudit = qaReport
            },
            new QualityReviewPackResult
            {
                PackId = "compliance",
                PackName = "Constitution Compliance",
                Score = 50,
                Compliance = complianceReport
            }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            // Verify Top Issues shows ONE PP-02 entry
            var topIssueCards = cut.FindAll(".qr-issue-card").Where(c => c.TextContent.Contains("PP-02")).ToList();
            topIssueCards.Should().HaveCount(1);

            // Verify underlying pack findings are still separate
            var qaAuditorHeader = cut.FindAll("button.qr-result-header").First(b => b.TextContent.Contains("QA Auditor"));
            qaAuditorHeader.Click();
        });

        cut.WaitForAssertion(() =>
        {
            // QA Auditor should still show its CONST-001 finding with enriched presentation
            cut.Markup.Should().Contain("PP-02 — Clear and Testable Requirements");
            cut.Markup.Should().Contain("Check CONST-001");
        });

        cut.WaitForAssertion(() =>
        {
            var complianceHeader = cut.FindAll("button.qr-result-header").First(b => b.TextContent.Contains("Constitution Compliance"));
            complianceHeader.Click();
        });

        cut.WaitForAssertion(() =>
        {
            // Constitution Compliance should still show its PP-02 gap
            cut.Markup.Should().Contain("Clear and Testable Requirements");
        });
    }

    [Fact]
    public void QualityReview_TopIssues_NonConstitutionQaFinding_DoesNotStripDetectorIdentity()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var qaReport = new QaAuditReport
        {
            Findings = new List<QaFinding>
            {
                new()
                {
                    RuleCode = "SPEC-001",
                    Title = "Specification is missing critical section",
                    Description = "The specification does not include API documentation",
                    Severity = QaSeverity.High,
                    Category = QaCategory.Specification,
                    AffectedArtifact = null,
                }
            },
            HasSpecification = true,
        };

        var report = MakeReport(
            new QualityReviewPackResult
            {
                PackId = "qa-auditor",
                PackName = "QA Auditor",
                Score = 60,
                High = 1,
                QaAudit = qaReport
            }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var specCard = cut.FindAll(".qr-issue-card").First(c => c.TextContent.Contains("SPEC-001"));
            // Non-Constitution findings should keep their detector code
            specCard.TextContent.Should().Contain("SPEC-001");
            specCard.TextContent.Should().Contain("Specification is missing critical section");
        });
    }

    [Fact]
    public void QualityReview_QaAuditorConstitutionFinding_UsesAffectedRuleAsPrimaryIdentity()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var qaReport = new QaAuditReport
        {
            Findings = new List<QaFinding>
            {
                new()
                {
                    RuleCode = "CONST-001",
                    Title = "Constitution rule PP-02 not covered by any artifact",
                    Description = "Rule 'PP-02' (Principle) has no coverage...",
                    Severity = QaSeverity.Critical,
                    Category = QaCategory.Constitution,
                    AffectedArtifact = "PP-02",
                }
            },
            HasConstitution = true,
        };

        var complianceReport = new ConstitutionComplianceReport
        {
            Results = new List<ComplianceResult>
            {
                new()
                {
                    RuleId = "PP-02",
                    RuleTitle = "Clear and Testable Requirements",
                    RuleType = ConstitutionRuleType.Principle,
                    Status = ComplianceStatus.Missing,
                    HasSpecCoverage = false,
                    HasPlanCoverage = false,
                    HasTaskCoverage = false,
                }
            }
        };

        var report = MakeReport(
            new QualityReviewPackResult
            {
                PackId = "qa-auditor",
                PackName = "QA Auditor",
                Score = 50,
                Critical = 1,
                QaAudit = qaReport
            },
            new QualityReviewPackResult
            {
                PackId = "compliance",
                PackName = "Constitution Compliance",
                Score = 50,
                Compliance = complianceReport
            }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var qaAuditorHeader = cut.FindAll("button.qr-result-header").First(b => b.TextContent.Contains("QA Auditor"));
            qaAuditorHeader.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var findingCard = cut.Find(".qr-finding-card");
            // Primary title should use affected rule
            findingCard.TextContent.Should().Contain("PP-02 — Clear and Testable Requirements");
            // Should NOT show CONST-001 as primary
            findingCard.TextContent.Should().NotContain("CONST-001 — Constitution rule PP-02");
        });
    }

    [Fact]
    public void QualityReview_QaAuditorConstitutionFinding_RendersDetectorAsSecondaryMetadata()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var qaReport = new QaAuditReport
        {
            Findings = new List<QaFinding>
            {
                new()
                {
                    RuleCode = "CONST-001",
                    Title = "Constitution rule PP-02 not covered by any artifact",
                    Description = "Rule 'PP-02' (Principle) has no coverage...",
                    Severity = QaSeverity.Critical,
                    Category = QaCategory.Constitution,
                    AffectedArtifact = "PP-02",
                }
            },
            HasConstitution = true,
        };

        var complianceReport = new ConstitutionComplianceReport
        {
            Results = new List<ComplianceResult>
            {
                new()
                {
                    RuleId = "PP-02",
                    RuleTitle = "Clear and Testable Requirements",
                    RuleType = ConstitutionRuleType.Principle,
                    Status = ComplianceStatus.Missing,
                    HasSpecCoverage = false,
                    HasPlanCoverage = false,
                    HasTaskCoverage = false,
                }
            }
        };

        var report = MakeReport(
            new QualityReviewPackResult
            {
                PackId = "qa-auditor",
                PackName = "QA Auditor",
                Score = 50,
                Critical = 1,
                QaAudit = qaReport
            },
            new QualityReviewPackResult
            {
                PackId = "compliance",
                PackName = "Constitution Compliance",
                Score = 50,
                Compliance = complianceReport
            }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var qaAuditorHeader = cut.FindAll("button.qr-result-header").First(b => b.TextContent.Contains("QA Auditor"));
            qaAuditorHeader.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var findingCard = cut.Find(".qr-finding-card");
            // Detector should appear as secondary metadata
            findingCard.TextContent.Should().Contain("Check CONST-001");
        });
    }

    [Fact]
    public void QualityReview_QaAuditorConstitutionFinding_DoesNotRepeatAffectedRuleInFooter()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var qaReport = new QaAuditReport
        {
            Findings = new List<QaFinding>
            {
                new()
                {
                    RuleCode = "CONST-001",
                    Title = "Constitution rule PP-02 not covered by any artifact",
                    Description = "Rule 'PP-02' (Principle) has no coverage...",
                    Severity = QaSeverity.Critical,
                    Category = QaCategory.Constitution,
                    AffectedArtifact = "PP-02",
                }
            },
            HasConstitution = true,
        };

        var complianceReport = new ConstitutionComplianceReport
        {
            Results = new List<ComplianceResult>
            {
                new()
                {
                    RuleId = "PP-02",
                    RuleTitle = "Clear and Testable Requirements",
                    RuleType = ConstitutionRuleType.Principle,
                    Status = ComplianceStatus.Missing,
                    HasSpecCoverage = false,
                    HasPlanCoverage = false,
                    HasTaskCoverage = false,
                }
            }
        };

        var report = MakeReport(
            new QualityReviewPackResult
            {
                PackId = "qa-auditor",
                PackName = "QA Auditor",
                Score = 50,
                Critical = 1,
                QaAudit = qaReport
            },
            new QualityReviewPackResult
            {
                PackId = "compliance",
                PackName = "Constitution Compliance",
                Score = 50,
                Compliance = complianceReport
            }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var qaAuditorHeader = cut.FindAll("button.qr-result-header").First(b => b.TextContent.Contains("QA Auditor"));
            qaAuditorHeader.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var findingCard = cut.Find(".qr-finding-card");
            // Should NOT have redundant "CONST-001 · PP-02" in footer
            findingCard.TextContent.Should().NotContain("CONST-001 · PP-02");
        });
    }

    [Fact]
    public void QualityReview_QaAuditorConstitutionFinding_ShowsCanonicalPrincipleTitle()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var qaReport = new QaAuditReport
        {
            Findings = new List<QaFinding>
            {
                new()
                {
                    RuleCode = "CONST-001",
                    Title = "Constitution rule PRINCIPLE-001 not covered by any artifact",
                    Description = "Rule 'PRINCIPLE-001' has no coverage...",
                    Severity = QaSeverity.High,
                    Category = QaCategory.Constitution,
                    AffectedArtifact = "PRINCIPLE-001",
                }
            },
            HasConstitution = true,
        };

        var complianceReport = new ConstitutionComplianceReport
        {
            Results = new List<ComplianceResult>
            {
                new()
                {
                    RuleId = "PRINCIPLE-001",
                    RuleTitle = "I. Headless API Communication",
                    RuleType = ConstitutionRuleType.Principle,
                    Status = ComplianceStatus.Missing,
                    HasSpecCoverage = false,
                    HasPlanCoverage = false,
                    HasTaskCoverage = false,
                }
            }
        };

        var report = MakeReport(
            new QualityReviewPackResult
            {
                PackId = "qa-auditor",
                PackName = "QA Auditor",
                Score = 50,
                High = 1,
                QaAudit = qaReport
            },
            new QualityReviewPackResult
            {
                PackId = "compliance",
                PackName = "Constitution Compliance",
                Score = 50,
                Compliance = complianceReport
            }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var qaAuditorHeader = cut.FindAll("button.qr-result-header").First(b => b.TextContent.Contains("QA Auditor"));
            qaAuditorHeader.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var findingCard = cut.Find(".qr-finding-card");
            findingCard.TextContent.Should().Contain("PRINCIPLE-001 — I. Headless API Communication");
        });
    }

    [Fact]
    public void QualityReview_QaAuditorNonConstitutionFinding_PreservesExistingPrimaryTitle()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var qaReport = new QaAuditReport
        {
            Findings = new List<QaFinding>
            {
                new()
                {
                    RuleCode = "PLAN-001",
                    Title = "Missing implementation phases",
                    Description = "The plan does not define phases",
                    Severity = QaSeverity.Medium,
                    Category = QaCategory.Plan,
                    AffectedArtifact = null,
                }
            },
            HasPlan = true,
        };

        var report = MakeReport(
            new QualityReviewPackResult
            {
                PackId = "qa-auditor",
                PackName = "QA Auditor",
                Score = 60,
                Medium = 1,
                QaAudit = qaReport
            }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var qaAuditorHeader = cut.FindAll("button.qr-result-header").First(b => b.TextContent.Contains("QA Auditor"));
            qaAuditorHeader.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var findingCard = cut.Find(".qr-finding-card");
            // Non-Constitution findings preserve original behavior
            findingCard.TextContent.Should().Contain("PLAN-001 — Missing implementation phases");
        });
    }

    [Fact]
    public void QualityReview_QaAuditorConstitutionFindings_UseCanonicalTitlesForMultipleRules()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var qaReport = new QaAuditReport
        {
            Findings = new List<QaFinding>
            {
                new()
                {
                    RuleCode = "CONST-001",
                    Title = "Constitution rule PP-02 not covered by any artifact",
                    Description = "Rule 'PP-02' has no coverage...",
                    Severity = QaSeverity.Critical,
                    Category = QaCategory.Constitution,
                    AffectedArtifact = "PP-02",
                },
                new()
                {
                    RuleCode = "CONST-001",
                    Title = "Constitution rule PP-04 not covered by any artifact",
                    Description = "Rule 'PP-04' has no coverage...",
                    Severity = QaSeverity.High,
                    Category = QaCategory.Constitution,
                    AffectedArtifact = "PP-04",
                },
                new()
                {
                    RuleCode = "CONST-001",
                    Title = "Constitution rule PP-09 not covered by any artifact",
                    Description = "Rule 'PP-09' has no coverage...",
                    Severity = QaSeverity.Medium,
                    Category = QaCategory.Constitution,
                    AffectedArtifact = "PP-09",
                }
            },
            HasConstitution = true,
        };

        var complianceReport = new ConstitutionComplianceReport
        {
            Results = new List<ComplianceResult>
            {
                new()
                {
                    RuleId = "PP-02",
                    RuleTitle = "Title A",
                    RuleType = ConstitutionRuleType.Principle,
                    Status = ComplianceStatus.Missing,
                    HasSpecCoverage = false,
                    HasPlanCoverage = false,
                    HasTaskCoverage = false,
                },
                new()
                {
                    RuleId = "PP-04",
                    RuleTitle = "Title B",
                    RuleType = ConstitutionRuleType.Principle,
                    Status = ComplianceStatus.Missing,
                    HasSpecCoverage = false,
                    HasPlanCoverage = false,
                    HasTaskCoverage = false,
                },
                new()
                {
                    RuleId = "PP-09",
                    RuleTitle = "Title C",
                    RuleType = ConstitutionRuleType.Standard,
                    Status = ComplianceStatus.Missing,
                    HasSpecCoverage = false,
                    HasPlanCoverage = false,
                    HasTaskCoverage = false,
                }
            }
        };

        var report = MakeReport(
            new QualityReviewPackResult
            {
                PackId = "qa-auditor",
                PackName = "QA Auditor",
                Score = 30,
                Critical = 1,
                High = 1,
                Medium = 1,
                QaAudit = qaReport
            },
            new QualityReviewPackResult
            {
                PackId = "compliance",
                PackName = "Constitution Compliance",
                Score = 30,
                Compliance = complianceReport
            }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var qaAuditorHeader = cut.FindAll("button.qr-result-header").First(b => b.TextContent.Contains("QA Auditor"));
            qaAuditorHeader.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var findingCards = cut.FindAll(".qr-finding-card");
            findingCards.Should().HaveCountGreaterThanOrEqualTo(3);

            var cardTexts = string.Join(" ", findingCards.Select(c => c.TextContent));
            cardTexts.Should().Contain("PP-02 — Title A");
            cardTexts.Should().Contain("PP-04 — Title B");
            cardTexts.Should().Contain("PP-09 — Title C");
        });
    }

    [Fact]
    public void QualityReview_QaAuditorConstitutionFinding_MissingCanonicalTitleFallsBackToRuleId()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var qaReport = new QaAuditReport
        {
            Findings = new List<QaFinding>
            {
                new()
                {
                    RuleCode = "CONST-001",
                    Title = "Constitution rule PP-99 not covered by any artifact",
                    Description = "Rule 'PP-99' has no coverage...",
                    Severity = QaSeverity.Medium,
                    Category = QaCategory.Constitution,
                    AffectedArtifact = "PP-99",
                }
            },
            HasConstitution = true,
        };

        var report = MakeReport(
            new QualityReviewPackResult
            {
                PackId = "qa-auditor",
                PackName = "QA Auditor",
                Score = 50,
                Medium = 1,
                QaAudit = qaReport
            }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var qaAuditorHeader = cut.FindAll("button.qr-result-header").First(b => b.TextContent.Contains("QA Auditor"));
            qaAuditorHeader.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var findingCard = cut.Find(".qr-finding-card");
            findingCard.TextContent.Should().Contain("PP-99");
            findingCard.TextContent.Should().Contain("Check CONST-001");
            // Should NOT have empty title suffix
            findingCard.TextContent.Should().NotContain("PP-99 — ");
        });
    }

    [Fact]
    public void QualityReview_QaAuditorConstitutionFinding_MissingAffectedArtifactUsesSafeFallback()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var qaReport = new QaAuditReport
        {
            Findings = new List<QaFinding>
            {
                new()
                {
                    RuleCode = "CONST-001",
                    Title = "Constitution coverage issue with original title",
                    Description = "Some coverage problem",
                    Severity = QaSeverity.Low,
                    Category = QaCategory.Constitution,
                    AffectedArtifact = null,  // Missing affected artifact
                }
            },
            HasConstitution = true,
        };

        var report = MakeReport(
            new QualityReviewPackResult
            {
                PackId = "qa-auditor",
                PackName = "QA Auditor",
                Score = 60,
                Low = 1,
                QaAudit = qaReport
            }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var qaAuditorHeader = cut.FindAll("button.qr-result-header").First(b => b.TextContent.Contains("QA Auditor"));
            qaAuditorHeader.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var findingCard = cut.Find(".qr-finding-card");
            // Should preserve original meaningful title
            findingCard.TextContent.Should().Contain("Constitution coverage issue with original title");
            // Should NOT fabricate a Constitution rule ID
            findingCard.TextContent.Should().NotContain("PP-");
            findingCard.TextContent.Should().NotContain("PRINCIPLE-");
        });
    }

    [Fact]
    public void QualityReview_QaAuditorConstitutionFinding_FullPageWiringUsesCanonicalMetadata()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var qaReport = new QaAuditReport
        {
            Findings = new List<QaFinding>
            {
                new()
                {
                    RuleCode = "CONST-001",
                    Title = "Constitution rule PP-02 not covered by any artifact",
                    Description = "Rule 'PP-02' (Principle) has no coverage in the Specification, Plan, or Tasks.",
                    Severity = QaSeverity.Critical,
                    Category = QaCategory.Constitution,
                    AffectedArtifact = "PP-02",
                }
            },
            HasConstitution = true,
        };

        var complianceReport = new ConstitutionComplianceReport
        {
            Results = new List<ComplianceResult>
            {
                new()
                {
                    RuleId = "PP-02",
                    RuleTitle = "Clear and Testable Requirements",
                    RuleType = ConstitutionRuleType.Principle,
                    Status = ComplianceStatus.Missing,
                    HasSpecCoverage = false,
                    HasPlanCoverage = false,
                    HasTaskCoverage = false,
                }
            },
            Gaps = new List<ComplianceGap>
            {
                new()
                {
                    RuleId = "PP-02",
                    RuleTitle = "Clear and Testable Requirements",
                    RuleType = ConstitutionRuleType.Principle,
                    MissingInSpec = true,
                    MissingInPlan = true,
                    MissingInTasks = true,
                    Severity = ViolationSeverity.Critical,
                }
            }
        };

        var report = MakeReport(
            new QualityReviewPackResult
            {
                PackId = "qa-auditor",
                PackName = "QA Auditor",
                Score = 50,
                Critical = 1,
                QaAudit = qaReport
            },
            new QualityReviewPackResult
            {
                PackId = "compliance",
                PackName = "Constitution Compliance",
                Score = 50,
                Compliance = complianceReport
            }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            // Open QA Auditor through rendered UI
            var qaAuditorHeader = cut.FindAll("button.qr-result-header").First(b => b.TextContent.Contains("QA Auditor"));
            qaAuditorHeader.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var findingCard = cut.Find(".qr-finding-card");

            // Verify canonical title with metadata
            findingCard.TextContent.Should().Contain("PP-02 — Clear and Testable Requirements");

            // Verify detector as secondary metadata
            findingCard.TextContent.Should().Contain("Check CONST-001");

            // Verify concise coverage text
            findingCard.TextContent.Should().Contain("Specification");
            findingCard.TextContent.Should().Contain("Plan");
            findingCard.TextContent.Should().Contain("Tasks");

            // Verify old primary title is NOT rendered
            findingCard.TextContent.Should().NotContain("CONST-001 — Constitution rule PP-02 not covered");
        });
    }

    private sealed record RunCall(
        string? Constitution,
        string? Specification,
        string? Plan,
        string? Tasks,
        string? DataModel,
        IReadOnlyList<string> SelectedPackIds);
}
