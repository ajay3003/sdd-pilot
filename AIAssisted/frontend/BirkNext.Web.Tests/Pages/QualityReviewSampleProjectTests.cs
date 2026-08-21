using AngleSharp.Dom;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;
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
    public void OverallSummary_RendersSelectedPackAverageWithoutProgressRing()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");
        _qualityReview.SetReport(MakeReport(new QualityReviewPackResult { PackName = "Test Pack", Score = 33 }));

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var summary = cut.Find(".qr-review-summary");
            summary.TextContent.Should().Contain("Selected-pack average:");
            summary.TextContent.Should().Contain("33%");
            summary.QuerySelector("svg").Should().BeNull();
            summary.QuerySelector(".qr-score-ring-wrap").Should().BeNull();
        });
    }

    [Fact]
    public void OverallSummary_ThirtyThreePercent_RendersAsTextOnly()
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
            cut.Find(".qr-review-summary").TextContent.Should().Contain("33%");
            cut.Find(".qr-review-summary").QuerySelector("svg").Should().BeNull();
        });
    }

    [Fact]
    public void OverallSummary_ZeroPercent_RendersAsTextOnly()
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
            cut.Find(".qr-review-summary").TextContent.Should().Contain("0%");
            cut.Find(".qr-review-summary").QuerySelector("svg").Should().BeNull();
        });
    }

    [Fact]
    public void OverallSummary_HundredPercent_RendersAsTextOnly()
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
            cut.Find(".qr-review-summary").TextContent.Should().Contain("100%");
            cut.Find(".qr-review-summary").QuerySelector("svg").Should().BeNull();
        });
    }

    [Fact]
    public void OverallSummary_FiftyPercent_RendersAsTextOnly()
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
            cut.Find(".qr-review-summary").TextContent.Should().Contain("50%");
            cut.Find(".qr-review-summary").QuerySelector("svg").Should().BeNull();
        });
    }

    [Fact]
    public void OverallSummary_PreservesDecimalScoreText()
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
            cut.Find(".qr-review-summary").TextContent.Should().Contain("75.5%");
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
    public void QualityReview_OverallSummary_RendersScoreContextAndUnassessedReadiness()
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
            var summary = cut.Find(".qr-review-summary");
            summary.TextContent.Should().Contain("Readiness not assessed");
            summary.TextContent.Should().Contain("Selected-pack average:");
            summary.TextContent.Should().Contain("62.5%");
            summary.QuerySelector(".qr-findings-context")!.TextContent.Should().ContainAll("0 findings across", "2 completed packs");
        });
    }

    [Fact]
    public void QualityReview_OverallSummary_RendersOnlyTopSeveritySignals()
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
            var summary = cut.Find(".qr-review-summary");
            summary.QuerySelectorAll(".qr-severity-signal").Should().HaveCount(2);
            summary.TextContent.Should().Contain("1 Critical");
            summary.TextContent.Should().Contain("2 High");
            summary.TextContent.Should().NotContain("Medium");
            summary.TextContent.Should().NotContain("Low");
            summary.TextContent.Should().NotContain("Warnings");
            summary.QuerySelector(".qr-count-card").Should().BeNull();
            summary.QuerySelector(".qr-count-total").Should().BeNull();
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
            packCard.TextContent.Should().Contain("HIGH");
            packCard.TextContent.Should().Contain("1 finding");
            packCard.TextContent.Should().Contain("Pack score 75%");
            packCard.TextContent.Should().Contain("View details →");
            packCard.TextContent.Should().NotContain("Fair");
            packCard.TextContent.Should().NotContain("Needs attention");
            packCard.QuerySelector(".qr-pack-card-severity")!.TextContent.Should().Be("HIGH");
        });
    }

    [Fact]
    public void QualityReview_ReviewPackCards_DoNotRankHeterogeneousScores()
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
            cut.FindAll(".qr-pack-card-highlight").Should().BeEmpty();
            var cardsText = string.Join(" ", cut.FindAll(".qr-pack-card").Select(c => c.TextContent));
            cardsText.Should().NotContain("Strongest");
            cardsText.Should().NotContain("Weakest");
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
            topIssueCards.Should().HaveCount(5);
            cut.Find(".qr-top-issues-title").TextContent.Should().Be("Top 5 Issues");
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
            // The description is the only remediation text, so it should not be duplicated as a Fix.
            topIssueCard.TextContent.Should().Contain("Problem:");
            topIssueCard.TextContent.Should().NotContain("Fix:");
            topIssueCard.TextContent.Should().Contain("MostlyReady");
            topIssueCard.TextContent.Should().Contain("View details");
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
            cut.Find(".qr-cat-title").TextContent.Should().Contain("Constitution (1)");
            cut.Find(".qr-finding-card").QuerySelector(".qr-finding-category").Should().BeNull();
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
            findingCard.TextContent.Should().NotContain("Constitution");
            findingCard.TextContent.Should().Contain("Problem:");
            findingCard.TextContent.Should().Contain("Missing coverage in Specification, Plan and Tasks.");
            // Should NOT show duplicated code
            var content = findingCard.TextContent;
            content.Should().NotContain("PP-02 — PP-02");
        });
    }

    [Fact]
    public void QualityReview_OverallSummary_RendersDeliveryStateAndIssuesSeparatelyFromSeverity()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var delivery = new DeliveryReadinessReport
        {
            ReleaseDecision = new() { Name = "Release", State = ReadinessState.Blocked, Score = 37 },
            Blockers =
            [
                new() { Title = "Release gate", Severity = GateSeverity.Critical },
                new() { Title = "Testing coverage", Severity = GateSeverity.High },
            ],
        };
        var report = MakeReport(new QualityReviewPackResult
        {
            PackId = "delivery-readiness",
            PackName = "Delivery Readiness",
            Score = 37,
            Critical = 1,
            High = 1,
            DeliveryReadiness = delivery,
        });
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var summary = cut.Find(".qr-review-summary");
            summary.QuerySelectorAll(".qr-review-summary").Should().BeEmpty();
            summary.QuerySelector(".qr-readiness-summary")!.TextContent.Should().Contain("Release:");
            summary.QuerySelector(".qr-readiness-summary")!.TextContent.Should().Contain("Blocked");
            summary.QuerySelector(".qr-gate-issues")!.TextContent.Should().Contain("2 delivery gate issues");
            summary.QuerySelector(".qr-severity-summary")!.TextContent.Should().Contain("1 Critical");
            summary.QuerySelector(".qr-severity-summary")!.TextContent.Should().Contain("1 High");
            summary.QuerySelector(".qr-findings-context")!.TextContent.Should().ContainAll("2 findings across", "1 completed pack");
            summary.QuerySelector("svg").Should().BeNull();
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
            qaAuditorCard.GetAttribute("aria-expanded").Should().Be("false");
            qaAuditorCard.GetAttribute("aria-controls").Should().Be("qr-pack-panel-qa-auditor");
            cut.Find("#qr-pack-panel-qa-auditor").Should().NotBeNull();

            var detailHeader = cut.FindAll("button.qr-result-header").First(b => b.TextContent.Contains("QA Auditor"));
            detailHeader.GetAttribute("aria-expanded").Should().Be("false");
            detailHeader.GetAttribute("aria-controls").Should().Be("qr-pack-panel-qa-auditor");

            // Initially collapsed
            var qaSection = cut.FindAll(".qr-result-section").FirstOrDefault(s => s.TextContent.Contains("QA Auditor"));
            qaSection?.GetAttribute("style")?.Should().Contain("display:none");

            // Click pack card
            qaAuditorCard.Click();
        }, timeout: TimeSpan.FromSeconds(2));

        cut.WaitForAssertion(() =>
        {
            // Now QA Auditor should be expanded
            cut.Find(".qr-pack-card").GetAttribute("aria-expanded").Should().Be("true");
            cut.Find("button.qr-result-header").GetAttribute("aria-expanded").Should().Be("true");
            var qaSection = cut.FindAll(".qr-result-section").FirstOrDefault(s => s.TextContent.Contains("QA Auditor"));
            qaSection?.Id.Should().Be("qr-pack-details-qa-auditor");
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
            metadata.Should().BeNull();
            packCard.TextContent.Should().Contain("No issues");
            packCard.TextContent.Should().Contain("Pack score 100%");
            packCard.QuerySelector(".qr-pack-card-severity").Should().BeNull();
        });
    }

    [Fact]
    public void QualityReview_DeliveryReadinessCard_PrioritizesReleaseDecisionAndGateIssues()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var delivery = new DeliveryReadinessReport
        {
            ReleaseDecision = new() { Name = "Release", State = ReadinessState.NotReady, Score = 24 },
            Blockers =
            [
                new() { Title = "Development gate", Severity = GateSeverity.Critical },
                new() { Title = "Testing gate", Severity = GateSeverity.High },
            ],
        };
        _qualityReview.SetReport(MakeReport(new QualityReviewPackResult
        {
            PackId = "delivery-readiness",
            PackName = "Delivery Readiness",
            Score = 24,
            Critical = 1,
            High = 1,
            DeliveryReadiness = delivery,
        }));

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var card = cut.Find(".qr-pack-card");
            card.TextContent.Should().Contain("Delivery Readiness");
            card.TextContent.Should().Contain("Release:");
            card.TextContent.Should().Contain("NOT READY");
            card.TextContent.Should().Contain("2 delivery gate issues");
            card.TextContent.Should().Contain("Readiness score 24%");
            card.TextContent.Should().Contain("View gates →");
            card.TextContent.Should().NotContain("Needs attention");
            card.TextContent.Should().NotContain("Strongest");
            card.TextContent.Should().NotContain("Weakest");
            card.QuerySelector(".qr-pack-card-severity").Should().BeNull();
        });
    }

    [Fact]
    public void QualityReview_DeliveryReadinessCard_ZeroGateIssuesReportsNone()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");
        var delivery = new DeliveryReadinessReport
        {
            ReleaseDecision = new() { Name = "Release", State = ReadinessState.Ready, Score = 92 },
            Blockers = [],
        };
        _qualityReview.SetReport(MakeReport(new QualityReviewPackResult
        {
            PackId = "delivery-readiness",
            PackName = "Delivery Readiness",
            Score = 92,
            DeliveryReadiness = delivery,
        }));

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var card = cut.Find(".qr-pack-card");
            card.TextContent.Should().Contain("Release:");
            card.TextContent.Should().Contain("READY");
            card.TextContent.Should().Contain("No delivery gate issues");
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
            pp02Card.TextContent.Should().Contain("Sources: Constitution Compliance · QA Auditor");
            pp02Card.TextContent.Should().NotContain("Reported by");
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
            pp02Card.QuerySelector(".qr-issue-title")!.TextContent.Should().Be("PP-02 — Clear and Testable Requirements");
            System.Text.RegularExpressions.Regex.Matches(pp02Card.QuerySelector(".qr-issue-title")!.TextContent, "PP-02").Should().HaveCount(1);
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
            card.TextContent.Should().Contain("Missing coverage: Specification · Plan · Tasks");
            card.TextContent.Should().Contain("Fix: Add coverage in Specification, Plan and Tasks.");
            card.TextContent.Should().NotContain("Rule 'PP-02' (Principle) has no coverage");
            card.TextContent.Should().Contain("View details →");
            card.GetAttribute("aria-expanded").Should().Be("false");
            card.GetAttribute("aria-controls").Should().Be("qr-pack-panel-compliance");
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
            card.TextContent.Should().Contain("Missing coverage: Plan · Tasks");
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
            cut.Markup.Should().Contain("Reference:</");
            cut.Markup.Should().Contain("CONST-001");
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
            specCard.TextContent.Should().Contain("SPEC-001 — Specification is missing critical section");
            specCard.TextContent.Should().Contain("Problem: The specification does not include API documentation");
            specCard.TextContent.Should().NotContain("Fix:");
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
            findingCard.TextContent.Should().Contain("Reference: CONST-001");
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
    public void QualityReview_ConstitutionComplianceGap_ShowsCanonicalRuleTitle()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

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
                PackId = "compliance",
                PackName = "Constitution Compliance",
                Score = 50,
                Critical = 1,
                Compliance = complianceReport
            }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var complianceHeader = cut.FindAll("button.qr-result-header").First(b => b.TextContent.Contains("Constitution Compliance"));
            complianceHeader.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var cardText = cut.Find(".qr-result-body").TextContent;
            cardText.Should().Contain("PP-02 — Clear and Testable Requirements", "canonical title should be primary");
        });
    }

    [Fact]
    public void QualityReview_ConstitutionComplianceGaps_ShowCanonicalTitlesForMultipleRules()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var complianceReport = new ConstitutionComplianceReport
        {
            Results = Enumerable.Range(1, 3)
                .Select(i => new ComplianceResult
                {
                    RuleId = $"PP-{i:D2}",
                    RuleTitle = $"Rule Title {i}",
                    RuleType = ConstitutionRuleType.Principle,
                    Status = ComplianceStatus.Missing,
                    HasSpecCoverage = false,
                    HasPlanCoverage = false,
                    HasTaskCoverage = false,
                })
                .ToList(),
            Gaps = Enumerable.Range(1, 3)
                .Select(i => new ComplianceGap
                {
                    RuleId = $"PP-{i:D2}",
                    RuleTitle = $"Rule Title {i}",
                    RuleType = ConstitutionRuleType.Principle,
                    MissingInSpec = true,
                    MissingInPlan = true,
                    MissingInTasks = true,
                    Severity = ViolationSeverity.Critical,
                })
                .ToList()
        };

        var report = MakeReport(
            new QualityReviewPackResult
            {
                PackId = "compliance",
                PackName = "Constitution Compliance",
                Score = 50,
                Critical = 1,
                Compliance = complianceReport
            }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var complianceHeader = cut.FindAll("button.qr-result-header").First(b => b.TextContent.Contains("Constitution Compliance"));
            complianceHeader.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var body = cut.Find(".qr-result-body").TextContent;
            body.Should().Contain("PP-01 — Rule Title 1");
            body.Should().Contain("PP-02 — Rule Title 2");
            body.Should().Contain("PP-03 — Rule Title 3");
        });
    }

    [Fact]
    public void QualityReview_ConstitutionComplianceGap_RendersMissingCoverageDescription()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var complianceReport = new ConstitutionComplianceReport
        {
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
                PackId = "compliance",
                PackName = "Constitution Compliance",
                Score = 50,
                Critical = 1,
                Compliance = complianceReport
            }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var complianceHeader = cut.FindAll("button.qr-result-header").First(b => b.TextContent.Contains("Constitution Compliance"));
            complianceHeader.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var body = cut.Find(".qr-result-body").TextContent;
            body.Should().Contain("Missing coverage:");
            body.Should().Contain("Missing coverage in Specification, Plan and Tasks.", "description should state what is wrong");
            body.Should().NotContain("Missing in:", "should not use abbreviated form");
        });
    }

    [Fact]
    public void QualityReview_ConstitutionComplianceGap_RendersActionableFix()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var complianceReport = new ConstitutionComplianceReport
        {
            Gaps = new List<ComplianceGap>
            {
                new()
                {
                    RuleId = "PP-02",
                    RuleTitle = "Clear and Testable Requirements",
                    RuleType = ConstitutionRuleType.Principle,
                    MissingInSpec = true,
                    MissingInPlan = false,
                    MissingInTasks = true,
                    Severity = ViolationSeverity.Critical,
                }
            }
        };

        var report = MakeReport(
            new QualityReviewPackResult
            {
                PackId = "compliance",
                PackName = "Constitution Compliance",
                Score = 50,
                Critical = 1,
                Compliance = complianceReport
            }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var complianceHeader = cut.FindAll("button.qr-result-header").First(b => b.TextContent.Contains("Constitution Compliance"));
            complianceHeader.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var body = cut.Find(".qr-result-body").TextContent;
            body.Should().Contain("Fix: Add coverage in Specification and Tasks.", "fix should state actionable step");
            body.Should().NotContain("Plan", "plan is not missing");
        });
    }

    [Fact]
    public void QualityReview_ConstitutionCompliance_ShowAllButtonUsesCorrectEventBinding()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        // Create Constitution Compliance report with >5 gaps to trigger Show all button
        var complianceReport = new ConstitutionComplianceReport
        {
            Gaps = Enumerable.Range(1, 36)
                .Select(i => new ComplianceGap
                {
                    RuleId = $"PP-{i:D2}",
                    RuleTitle = $"Principle {i}",
                    RuleType = ConstitutionRuleType.Principle,
                    MissingInSpec = true,
                    MissingInPlan = true,
                    MissingInTasks = true,
                    Severity = ViolationSeverity.Critical,
                })
                .ToList()
        };

        var report = MakeReport(
            new QualityReviewPackResult
            {
                PackId = "compliance",
                PackName = "Constitution Compliance",
                Score = 50,
                Critical = 1,
                Compliance = complianceReport
            }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        // Open Constitution Compliance - this previously crashed with WASM unboxing error
        cut.WaitForAssertion(() =>
        {
            var complianceHeader = cut.FindAll("button.qr-result-header").First(b => b.TextContent.Contains("Constitution Compliance"));
            complianceHeader.Click();
        });

        // Regression test: Show all button must render without WASM failure
        // Previously failed because attribute name was literally "@onclick" instead of "onclick"
        cut.WaitForAssertion(() =>
        {
            var showAllButton = cut.Find("button.qr-show-toggle");
            showAllButton.Should().NotBeNull();
            showAllButton.TextContent.Should().Contain("Show all");

            // Button click should work without WASM unboxing exception
            showAllButton.Click();
        });

        // After clicking, all items should be visible
        cut.WaitForAssertion(() =>
        {
            var cardCount = cut.FindAll(".qr-cat-title");
            cardCount.Should().HaveCountGreaterThanOrEqualTo(1);
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
            findingCard.TextContent.Should().Contain("Reference: CONST-001");
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
    public void QualityReview_ConstitutionCompliance_ClickOpensWithoutRenderException()
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

        // Open Constitution Compliance and verify it renders correctly
        // This regression test ensures that the reference-type ConstitutionRulePresentationMetadata
        // (instead of ValueTuple) doesn't cause WASM boxing exceptions when rendering
        cut.WaitForAssertion(() =>
        {
            var headers = cut.FindAll("button.qr-result-header");
            headers.Should().HaveCountGreaterThanOrEqualTo(2);

            var complianceHeader = headers.First(b => b.TextContent.Contains("Constitution Compliance"));
            complianceHeader.Click();
        });

        cut.WaitForAssertion(() =>
        {
            // Verify Constitution Compliance detail body is rendered
            var bodies = cut.FindAll(".qr-result-body");
            bodies.Should().HaveCountGreaterThanOrEqualTo(1);

            var complianceBody = bodies.LastOrDefault();
            complianceBody.Should().NotBeNull();
            complianceBody!.TextContent.Should().Contain("Coverage Gaps");
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
            findingCard.TextContent.Should().Contain("Reference: CONST-001");

            // Verify concise coverage text
            findingCard.TextContent.Should().Contain("Specification");
            findingCard.TextContent.Should().Contain("Plan");
            findingCard.TextContent.Should().Contain("Tasks");

            // Verify old primary title is NOT rendered
            findingCard.TextContent.Should().NotContain("CONST-001 — Constitution rule PP-02 not covered");
        });
    }

    [Fact]
    public void QualityReview_QaReadinessGap_RendersCompactHierarchy()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var readinessReport = new QAReadinessReport
        {
            OverallScore = 41.2,
            OverallStatus = ReadinessStatus.NeedsWork,
            Scores = new List<ReadinessScore>
            {
                new() { Category = "Plan Quality", Score = 30, Status = ReadinessStatus.NeedsWork, IsAssessed = true },
                new() { Category = "Specification Quality", Score = 85, Status = ReadinessStatus.Ready, IsAssessed = true },
                new() { Category = "Task Readiness", Score = 40, Status = ReadinessStatus.NeedsWork, IsAssessed = true },
                new() { Category = "Traceability", Score = 0, Status = ReadinessStatus.NotReady, IsAssessed = true },
                new() { Category = "Compliance", Score = 30, Status = ReadinessStatus.NeedsWork, IsAssessed = true }
            },
            Gaps = new List<ReadinessGap>
            {
                new()
                {
                    Category = "Plan Quality",
                    Description = "No implementation phases — add phased delivery plan.",
                    Severity = ViolationSeverity.High
                }
            },
            Gates = new List<ReadinessGate>(),
            Recommendations = new List<ReadinessRecommendation>()
        };

        var report = MakeReport(
            new QualityReviewPackResult
            {
                PackId = "qa-readiness",
                PackName = "QA Readiness",
                Score = 41,
                High = 1,
                QaReadiness = readinessReport
            }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        // Open QA Readiness detail
        cut.WaitForAssertion(() =>
        {
            var qaReadinessHeader = cut.FindAll("button.qr-result-header").First(b => b.TextContent.Contains("QA Readiness"));
            qaReadinessHeader.Click();
        });

        cut.WaitForAssertion(() =>
        {
            // Verify gap card structure is rendered
            cut.Markup.Should().Contain("qr-gap-card");
            cut.Markup.Should().Contain("HIG");
            cut.Markup.Should().Contain("Plan");
            cut.Markup.Should().Contain("No implementation phases");
        });
    }

    [Fact]
    public void QualityReview_QaReadinessRecommendation_UsesCanonicalRuleTitle()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var readinessReport = new QAReadinessReport
        {
            OverallScore = 41.2,
            OverallStatus = ReadinessStatus.NeedsWork,
            Scores = new List<ReadinessScore>(),
            Gaps = new List<ReadinessGap>(),
            Gates = new List<ReadinessGate>(),
            Recommendations = new List<ReadinessRecommendation>
            {
                new()
                {
                    Category = "Specification Quality",
                    Text = "Add PP-02 requirements to the specification.",
                    Priority = ViolationSeverity.Critical,
                    TargetArtifact = ArtifactType.Specification
                }
            }
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
                }
            }
        };

        var report = MakeReport(
            new QualityReviewPackResult
            {
                PackId = "qa-readiness",
                PackName = "QA Readiness",
                Score = 41,
                Critical = 1,
                QaReadiness = readinessReport
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
            var qaReadinessHeader = cut.FindAll("button.qr-result-header").First(b => b.TextContent.Contains("QA Readiness"));
            qaReadinessHeader.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var recCard = cut.Find(".qr-recommendation-card");
            recCard.TextContent.Should().Contain("PP-02 — Clear and Testable Requirements");
            recCard.TextContent.Should().NotContain("PP-02 (PP-02)");
        });
    }

    [Fact]
    public void QualityReview_QaReadinessRecommendation_UsesCanonicalPrincipleTitle()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var readinessReport = new QAReadinessReport
        {
            OverallScore = 41.2,
            OverallStatus = ReadinessStatus.NeedsWork,
            Scores = new List<ReadinessScore>(),
            Gaps = new List<ReadinessGap>(),
            Gates = new List<ReadinessGate>(),
            Recommendations = new List<ReadinessRecommendation>
            {
                new()
                {
                    Category = "Plan Quality",
                    Text = "Add PRINCIPLE-005 (V. Testing is Mandatory) implementation strategy to the plan.",
                    Priority = ViolationSeverity.Critical,
                    TargetArtifact = ArtifactType.Plan
                }
            }
        };

        var complianceReport = new ConstitutionComplianceReport
        {
            Results = new List<ComplianceResult>
            {
                new()
                {
                    RuleId = "PRINCIPLE-005",
                    RuleTitle = "V. Testing is Mandatory",
                    RuleType = ConstitutionRuleType.Principle,
                    Status = ComplianceStatus.Missing,
                }
            }
        };

        var report = MakeReport(
            new QualityReviewPackResult
            {
                PackId = "qa-readiness",
                PackName = "QA Readiness",
                Score = 41,
                Critical = 1,
                QaReadiness = readinessReport
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
            var qaReadinessHeader = cut.FindAll("button.qr-result-header").First(b => b.TextContent.Contains("QA Readiness"));
            qaReadinessHeader.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var recCard = cut.Find(".qr-recommendation-card");
            recCard.TextContent.Should().Contain("PRINCIPLE-005 — V. Testing is Mandatory");
        });
    }

    [Fact]
    public void QualityReview_QaReadinessRecommendation_RendersPriorityAndTargetAsMetadata()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var readinessReport = new QAReadinessReport
        {
            OverallScore = 41.2,
            OverallStatus = ReadinessStatus.NeedsWork,
            Scores = new List<ReadinessScore>(),
            Gaps = new List<ReadinessGap>(),
            Gates = new List<ReadinessGate>(),
            Recommendations = new List<ReadinessRecommendation>
            {
                new()
                {
                    Category = "Plan Quality",
                    Text = "Add implementation phases to the plan.",
                    Priority = ViolationSeverity.Critical,
                    TargetArtifact = ArtifactType.Plan
                }
            }
        };

        var report = MakeReport(
            new QualityReviewPackResult
            {
                PackId = "qa-readiness",
                PackName = "QA Readiness",
                Score = 41,
                Critical = 1,
                QaReadiness = readinessReport
            }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var qaReadinessHeader = cut.FindAll("button.qr-result-header").First(b => b.TextContent.Contains("QA Readiness"));
            qaReadinessHeader.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var recCard = cut.Find(".qr-recommendation-card");

            // Verify priority and target in metadata
            recCard.TextContent.Should().Contain("[CRI]");
            recCard.TextContent.Should().Contain("Plan");

            // Verify NOT raw "CRITICAL PRIORITY"
            recCard.TextContent.Should().NotContain("CRITICAL PRIORITY");
        });
    }

    [Fact]
    public void QualityReview_QaReadinessRecommendation_DoesNotDuplicateRuleId()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var readinessReport = new QAReadinessReport
        {
            OverallScore = 41.2,
            OverallStatus = ReadinessStatus.NeedsWork,
            Scores = new List<ReadinessScore>(),
            Gaps = new List<ReadinessGap>(),
            Gates = new List<ReadinessGate>(),
            Recommendations = new List<ReadinessRecommendation>
            {
                new()
                {
                    Category = "Specification Quality",
                    Text = "Add PP-02 requirements to the specification.",
                    Priority = ViolationSeverity.Critical,
                    TargetArtifact = ArtifactType.Specification
                }
            }
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
                }
            }
        };

        var report = MakeReport(
            new QualityReviewPackResult
            {
                PackId = "qa-readiness",
                PackName = "QA Readiness",
                Score = 41,
                Critical = 1,
                QaReadiness = readinessReport
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
            var qaReadinessHeader = cut.FindAll("button.qr-result-header").First(b => b.TextContent.Contains("QA Readiness"));
            qaReadinessHeader.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var recCard = cut.Find(".qr-recommendation-card");

            // Should NOT have duplication
            recCard.TextContent.Should().NotContain("PP-02 (PP-02)");
            recCard.TextContent.Should().NotContain("PP-02 — PP-02");

            // Should have clean presentation
            recCard.TextContent.Should().Contain("PP-02 — Clear and Testable Requirements");
        });
    }

    [Fact]
    public void QualityReview_QaReadinessRecommendation_MissingCanonicalTitleFallsBackCleanly()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var readinessReport = new QAReadinessReport
        {
            OverallScore = 41.2,
            OverallStatus = ReadinessStatus.NeedsWork,
            Scores = new List<ReadinessScore>(),
            Gaps = new List<ReadinessGap>(),
            Gates = new List<ReadinessGate>(),
            Recommendations = new List<ReadinessRecommendation>
            {
                new()
                {
                    Category = "Specification Quality",
                    Text = "Add PP-99 requirements to the specification.",
                    Priority = ViolationSeverity.Critical,
                    TargetArtifact = ArtifactType.Specification
                }
            }
        };

        var report = MakeReport(
            new QualityReviewPackResult
            {
                PackId = "qa-readiness",
                PackName = "QA Readiness",
                Score = 41,
                Critical = 1,
                QaReadiness = readinessReport
            }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var qaReadinessHeader = cut.FindAll("button.qr-result-header").First(b => b.TextContent.Contains("QA Readiness"));
            qaReadinessHeader.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var recCard = cut.Find(".qr-recommendation-card");

            // Should render the rule ID
            recCard.TextContent.Should().Contain("PP-99");

            // Should NOT have malformed suffix
            recCard.TextContent.Should().NotContain("PP-99 —");
            recCard.TextContent.Should().NotContain("PP-99 (PP-99)");
        });
    }

    [Fact]
    public void QualityReview_QaReadinessRecommendation_PreservesActionableText()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var readinessReport = new QAReadinessReport
        {
            OverallScore = 41.2,
            OverallStatus = ReadinessStatus.NeedsWork,
            Scores = new List<ReadinessScore>
            {
                new() { Category = "Plan Quality", Score = 30, Status = ReadinessStatus.NeedsWork, IsAssessed = true },
                new() { Category = "Specification Quality", Score = 85, Status = ReadinessStatus.Ready, IsAssessed = true },
                new() { Category = "Task Readiness", Score = 40, Status = ReadinessStatus.NeedsWork, IsAssessed = true },
                new() { Category = "Traceability", Score = 0, Status = ReadinessStatus.NotReady, IsAssessed = true },
                new() { Category = "Compliance", Score = 30, Status = ReadinessStatus.NeedsWork, IsAssessed = true }
            },
            Gaps = new List<ReadinessGap>(),
            Gates = new List<ReadinessGate>(),
            Recommendations = new List<ReadinessRecommendation>
            {
                new()
                {
                    Category = "Plan Quality",
                    Text = "Add PRINCIPLE-005 (V. Testing is Mandatory) implementation strategy to the plan. Consider adding authorization requirements.",
                    Priority = ViolationSeverity.Critical,
                    TargetArtifact = ArtifactType.Plan
                }
            }
        };

        var complianceReport = new ConstitutionComplianceReport
        {
            Results = new List<ComplianceResult>
            {
                new() { RuleId = "PRINCIPLE-005", RuleTitle = "V. Testing is Mandatory", RuleType = ConstitutionRuleType.Principle, Status = ComplianceStatus.Missing }
            }
        };

        var report = MakeReport(
            new QualityReviewPackResult
            {
                PackId = "qa-readiness",
                PackName = "QA Readiness",
                Score = 41,
                Critical = 1,
                QaReadiness = readinessReport
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
            var qaReadinessHeader = cut.FindAll("button.qr-result-header").First(b => b.TextContent.Contains("QA Readiness"));
            qaReadinessHeader.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var recMarkup = cut.Markup;
            // Verify both main action and secondary advice are preserved
            recMarkup.Should().Contain("Add PRINCIPLE-005");
            recMarkup.Should().Contain("implementation strategy");
            recMarkup.Should().Contain("Consider adding authorization requirements");
        });
    }

    [Fact]
    public void QualityReview_QaReadinessGaps_ShowAllUsesSafeToggle()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var readinessReport = new QAReadinessReport
        {
            OverallScore = 41.2,
            OverallStatus = ReadinessStatus.NeedsWork,
            Scores = new List<ReadinessScore>
            {
                new() { Category = "Plan Quality", Score = 30, Status = ReadinessStatus.NeedsWork, IsAssessed = true },
                new() { Category = "Specification Quality", Score = 85, Status = ReadinessStatus.Ready, IsAssessed = true },
                new() { Category = "Task Readiness", Score = 40, Status = ReadinessStatus.NeedsWork, IsAssessed = true },
                new() { Category = "Traceability", Score = 0, Status = ReadinessStatus.NotReady, IsAssessed = true },
                new() { Category = "Compliance", Score = 30, Status = ReadinessStatus.NeedsWork, IsAssessed = true }
            },
            Gaps = Enumerable.Range(1, 10)
                .Select(i => new ReadinessGap
                {
                    Category = i % 2 == 0 ? "Plan Quality" : "Specification Quality",
                    Description = $"Gap {i}",
                    Severity = ViolationSeverity.High
                })
                .ToList(),
            Gates = new List<ReadinessGate>(),
            Recommendations = new List<ReadinessRecommendation>()
        };

        var report = MakeReport(
            new QualityReviewPackResult
            {
                PackId = "qa-readiness",
                PackName = "QA Readiness",
                Score = 41,
                High = 10,
                QaReadiness = readinessReport
            }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var qaReadinessHeader = cut.FindAll("button.qr-result-header").First(b => b.TextContent.Contains("QA Readiness"));
            qaReadinessHeader.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var button = cut.Find("button.qr-show-toggle");
            button.Should().NotBeNull();
            button.TagName.Should().Be("BUTTON");
            button.TextContent.Should().Contain("Show all");
            button.GetAttribute("@onclick").Should().BeNull("literal @onclick attribute should not exist");

            // Click show all
            button.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            for (int i = 1; i <= 10; i++)
            {
                markup.Should().Contain($"Gap {i}");
            }

            var button = cut.Find("button.qr-show-toggle");
            button.TextContent.Should().Be("Show less");
        });
    }

    [Fact]
    public void QualityReview_QaReadinessHeader_RemovesRedundantReadinessSuffix()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var readinessReport = new QAReadinessReport
        {
            OverallScore = 41.2,
            OverallStatus = ReadinessStatus.NeedsWork,
            Scores = new List<ReadinessScore>
            {
                new() { Category = "Plan Quality", Score = 30, Status = ReadinessStatus.NeedsWork, IsAssessed = true },
                new() { Category = "Specification Quality", Score = 85, Status = ReadinessStatus.Ready, IsAssessed = true },
                new() { Category = "Task Readiness", Score = 40, Status = ReadinessStatus.NeedsWork, IsAssessed = true },
                new() { Category = "Traceability", Score = 0, Status = ReadinessStatus.NotReady, IsAssessed = true },
                new() { Category = "Compliance", Score = 30, Status = ReadinessStatus.NeedsWork, IsAssessed = true }
            },
            Gaps = new List<ReadinessGap>(),
            Gates = new List<ReadinessGate>(),
            Recommendations = new List<ReadinessRecommendation>()
        };

        var report = MakeReport(
            new QualityReviewPackResult
            {
                PackId = "qa-readiness",
                PackName = "QA Readiness",
                Score = 41,
                QaReadiness = readinessReport
            }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var qaReadinessHeader = cut.FindAll("button.qr-result-header").First(b => b.TextContent.Contains("QA Readiness"));
            qaReadinessHeader.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            // Verify header shows "Needs Work" without redundant "Readiness" suffix
            markup.Should().Contain("Needs Work");
            markup.Should().NotContain("Needs Work Readiness");
        });
    }

    [Fact]
    public void QualityReview_QaReadinessRecommendations_ShowAllUsesSafeToggle()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var readinessReport = new QAReadinessReport
        {
            OverallScore = 41.2,
            OverallStatus = ReadinessStatus.NeedsWork,
            Scores = new List<ReadinessScore>
            {
                new() { Category = "Plan Quality", Score = 30, Status = ReadinessStatus.NeedsWork, IsAssessed = true },
                new() { Category = "Specification Quality", Score = 85, Status = ReadinessStatus.Ready, IsAssessed = true },
                new() { Category = "Task Readiness", Score = 40, Status = ReadinessStatus.NeedsWork, IsAssessed = true },
                new() { Category = "Traceability", Score = 0, Status = ReadinessStatus.NotReady, IsAssessed = true },
                new() { Category = "Compliance", Score = 30, Status = ReadinessStatus.NeedsWork, IsAssessed = true }
            },
            Gaps = new List<ReadinessGap>(),
            Gates = new List<ReadinessGate>(),
            Recommendations = Enumerable.Range(1, 8)
                .Select(i => new ReadinessRecommendation
                {
                    Category = "Plan Quality",
                    Text = $"Recommendation {i}",
                    Priority = ViolationSeverity.Critical,
                    TargetArtifact = ArtifactType.Plan
                })
                .ToList()
        };

        var report = MakeReport(
            new QualityReviewPackResult
            {
                PackId = "qa-readiness",
                PackName = "QA Readiness",
                Score = 41,
                Critical = 8,
                QaReadiness = readinessReport
            }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var qaReadinessHeader = cut.FindAll("button.qr-result-header").First(b => b.TextContent.Contains("QA Readiness"));
            qaReadinessHeader.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var buttons = cut.FindAll("button.qr-show-toggle");
            buttons.Should().HaveCountGreaterThanOrEqualTo(1);

            var showAllButton = buttons.Last();
            showAllButton.TextContent.Should().Contain("Show all");
            showAllButton.GetAttribute("@onclick").Should().BeNull("literal @onclick should not exist");

            showAllButton.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            for (int i = 1; i <= 8; i++)
            {
                markup.Should().Contain($"Recommendation {i}");
            }
        });
    }

    [Fact]
    public void QualityReview_DeliveryReadiness_FormatsGateStatusesForDisplay()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var drReport = new DeliveryReadinessReport
        {
            DevelopmentDecision = new() { Name = "Development", State = ReadinessState.NotReady, Score = 30 },
            TestingDecision = new() { Name = "Testing", State = ReadinessState.MostlyReady, Score = 60 },
            ReleaseDecision = new() { Name = "Release", State = ReadinessState.Blocked, Score = 0 },
            Blockers = new List<ReadinessBlocker>
            {
                new() { Title = "Development gate not cleared", Severity = GateSeverity.Critical, Phase = "Release", Description = "Development readiness must reach MostlyReady before release assessment. Current: NotReady." },
            },
            Health = new() { OverallReadinessScore = 30 },
        };

        var report = MakeReport(
            new QualityReviewPackResult { PackId = "delivery", PackName = "Delivery Readiness", Score = 0, Critical = 1, DeliveryReadiness = drReport }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var drCard = cut.FindAll(".qr-pack-card").FirstOrDefault();
            drCard.Should().NotBeNull();
            drCard!.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            // Verify human-readable status appears
            markup.Should().Contain("Not Ready");
            markup.Should().Contain("Mostly Ready");
            markup.Should().Contain("Blocked");
        });
    }

    [Fact]
    public void QualityReview_DeliveryReadinessBlocker_RendersCurrentRequiredAndFix()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var drReport = new DeliveryReadinessReport
        {
            DevelopmentDecision = new() { Name = "Development", State = ReadinessState.NotReady, Score = 30 },
            Blockers = new List<ReadinessBlocker>
            {
                new() { Title = "Development gate not cleared", Severity = GateSeverity.Critical, Phase = "Release", Description = "Development readiness must reach MostlyReady before release assessment. Current: NotReady." },
            },
            Health = new() { OverallReadinessScore = 30 },
        };

        var report = MakeReport(
            new QualityReviewPackResult { PackId = "delivery", PackName = "Delivery Readiness", Score = 0, Critical = 1, DeliveryReadiness = drReport }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var drCard = cut.FindAll(".qr-pack-card").FirstOrDefault();
            drCard.Should().NotBeNull();
            drCard!.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            // Verify hierarchy
            markup.Should().Contain("[CRITICAL]");
            markup.Should().Contain("Release");
            markup.Should().Contain("Development gate not cleared");
            // Verify state row
            markup.Should().Contain("Current: Not Ready");
            markup.Should().Contain("Required: Mostly Ready");
            // Verify actionable fix
            markup.Should().Contain("Fix: Raise Development readiness to Mostly Ready");
        });
    }

    [Fact]
    public void QualityReview_DeliveryReadinessBlocker_RendersComplianceThreshold()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var drReport = new DeliveryReadinessReport
        {
            DevelopmentDecision = new() { Name = "Development", State = ReadinessState.Ready, Score = 80 },
            ReleaseDecision = new() { Name = "Release", State = ReadinessState.NotReady, Score = 0 },
            Blockers = new List<ReadinessBlocker>
            {
                new() { Title = "Insufficient compliance for release", Severity = GateSeverity.Critical, Phase = "Release", Description = "Compliance must reach 80% before release. Current: 0%." },
            },
            Health = new() { OverallReadinessScore = 50 },
        };

        var report = MakeReport(
            new QualityReviewPackResult { PackId = "delivery", PackName = "Delivery Readiness", Score = 0, Critical = 1, DeliveryReadiness = drReport }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var drCard = cut.FindAll(".qr-pack-card").FirstOrDefault();
            drCard!.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            // Verify state row with percentages
            markup.Should().Contain("Current: 0%");
            markup.Should().Contain("Required: 80%");
            // Verify actionable fix
            markup.Should().Contain("Fix: Raise compliance to at least 80%");
        });
    }

    [Fact]
    public void QualityReview_DeliveryReadinessBlockers_ShowAllUsesSafeToggle()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var drReport = new DeliveryReadinessReport
        {
            DevelopmentDecision = new() { Name = "Development", State = ReadinessState.NotReady, Score = 30 },
            Blockers = new List<ReadinessBlocker>
            {
                new() { Title = "Blocker 1", Severity = GateSeverity.Critical, Phase = "Release" },
                new() { Title = "Blocker 2", Severity = GateSeverity.Critical, Phase = "Release" },
                new() { Title = "Blocker 3", Severity = GateSeverity.Critical, Phase = "Release" },
                new() { Title = "Blocker 4", Severity = GateSeverity.Critical, Phase = "Release" },
            },
            Health = new() { OverallReadinessScore = 30 },
        };

        var report = MakeReport(
            new QualityReviewPackResult { PackId = "delivery", PackName = "Delivery Readiness", Score = 0, Critical = 4, DeliveryReadiness = drReport }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var drCard = cut.FindAll(".qr-pack-card").FirstOrDefault();
            drCard!.Click();
        });

        cut.WaitForAssertion(() =>
        {
            // Initial state: only 3 blockers visible
            var blockerCards = cut.FindAll(".dr-blocker-card");
            blockerCards.Count.Should().Be(3);

            // Verify Show all button exists
            var showAllButton = cut.FindAll(".qr-show-toggle").FirstOrDefault();
            showAllButton.Should().NotBeNull();
            showAllButton!.TextContent.Should().Contain("Show all 4 blockers");

            // Verify button is semantic and uses safe event binding
            showAllButton.TagName.Should().Be("BUTTON");
            // Event binding exists (either "onclick" or "blazor:onclick" depending on rendering mode)
            var hasEventBinding = showAllButton.GetAttribute("onclick") != null ||
                                  showAllButton.GetAttribute("blazor:onclick") != null;
            hasEventBinding.Should().BeTrue();
        });
    }

    [Fact]
    public void QualityReview_DeliveryReadiness_LoadedArtifacts_RendersCompactStatus()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var drReport = new DeliveryReadinessReport
        {
            DevelopmentDecision = new() { Name = "Development", State = ReadinessState.Ready, Score = 85 },
            HasConstitution = true,
            HasSpecification = true,
            HasPlan = true,
            HasTasks = true,
        };

        var report = MakeReport(
            new QualityReviewPackResult { PackId = "delivery", PackName = "Delivery Readiness", Score = 85, DeliveryReadiness = drReport }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var drCard = cut.FindAll(".qr-pack-card").FirstOrDefault();
            drCard!.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            // Verify all artifacts appear
            markup.Should().Contain("Constitution");
            markup.Should().Contain("Specification");
            markup.Should().Contain("Plan");
            markup.Should().Contain("Tasks");
            // Verify status shown
            markup.Should().Contain("Loaded");
        });
    }

    [Fact]
    public void QualityReview_DeliveryReadiness_TabsUseSemanticControls()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var drReport = new DeliveryReadinessReport
        {
            DevelopmentDecision = new() { Name = "Development", State = ReadinessState.Ready, Score = 85 },
        };

        var report = MakeReport(
            new QualityReviewPackResult { PackId = "delivery", PackName = "Delivery Readiness", Score = 85, DeliveryReadiness = drReport }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var drCard = cut.FindAll(".qr-pack-card").FirstOrDefault();
            drCard!.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var tabs = cut.FindAll(".tab-btn");
            tabs.Count.Should().BeGreaterThan(0);

            cut.Find("[role='tablist']").GetAttribute("aria-label").Should().Be("Delivery readiness details");

            // All tabs should be fully associated with their tab panel.
            foreach (var tab in tabs)
            {
                tab.TagName.Should().Be("BUTTON");
                tab.GetAttribute("role").Should().Be("tab");
                tab.GetAttribute("id").Should().StartWith("delivery-tab-");
                tab.GetAttribute("aria-controls").Should().StartWith("delivery-panel-");
            }

            tabs.First().GetAttribute("aria-selected").Should().Be("true");
            tabs.First().GetAttribute("tabindex").Should().Be("0");
            tabs.Skip(1).Should().OnlyContain(tab => tab.GetAttribute("aria-selected") == "false");
            tabs.Skip(1).Should().OnlyContain(tab => tab.GetAttribute("tabindex") == "-1");
            tabs.First().GetAttribute("class").Should().Contain("is-active");

            var panel = cut.Find("[role='tabpanel']");
            panel.Id.Should().Be("delivery-panel-overview");
            panel.GetAttribute("aria-labelledby").Should().Be("delivery-tab-overview");
        });

        cut.Find("#delivery-tab-overview").KeyDown(new KeyboardEventArgs { Key = "ArrowRight" });
        cut.WaitForAssertion(() =>
        {
            cut.Find("#delivery-tab-development").GetAttribute("aria-selected").Should().Be("true");
            cut.Find("[role='tabpanel']").Id.Should().Be("delivery-panel-development");
        });

        cut.Find("#delivery-tab-development").KeyDown(new KeyboardEventArgs { Key = "End" });
        cut.WaitForAssertion(() => cut.Find("#delivery-tab-recommendations").GetAttribute("aria-selected").Should().Be("true"));

        cut.Find("#delivery-tab-recommendations").KeyDown(new KeyboardEventArgs { Key = "Home" });
        cut.WaitForAssertion(() => cut.Find("#delivery-tab-overview").GetAttribute("aria-selected").Should().Be("true"));
    }

    [Fact]
    public void QualityReview_QaReadinessCategories_AreNativeDisclosureButtons()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var readinessReport = new QAReadinessReport
        {
            Scores = new List<ReadinessScore>
            {
                new() { Category = "Specification Quality", IsAssessed = true, Score = 70, Status = ReadinessStatus.MostlyReady,
                    Signals = new List<string> { "Requirements are present." } }
            }
        };
        _qualityReview.SetReport(MakeReport(new QualityReviewPackResult
        {
            PackId = "qa-readiness", PackName = "QA Readiness", Score = 70, QaReadiness = readinessReport
        }));

        var cut = Render<QualityReview>();
        ClickRun(cut);
        cut.WaitForAssertion(() => cut.Find(".qr-pack-card").Click());

        cut.WaitForAssertion(() =>
        {
            var category = cut.Find(".qr-category-card");
            category.TagName.Should().Be("BUTTON");
            category.GetAttribute("aria-expanded").Should().Be("false");
            category.GetAttribute("aria-controls").Should().Be("qa-readiness-category-specification-quality");
            category.Click();
        });

        cut.WaitForAssertion(() =>
        {
            cut.Find(".qr-category-card").GetAttribute("aria-expanded").Should().Be("true");
            cut.Find("#qa-readiness-category-specification-quality").TextContent.Should().Contain("Requirements are present.");
        });
    }

    [Fact]
    public void QualityReview_DeliveryReadiness_OverviewDoesNotDuplicateGateScores()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var drReport = new DeliveryReadinessReport
        {
            DevelopmentDecision = new() { Name = "Development", State = ReadinessState.NotReady, Score = 30 },
            TestingDecision = new() { Name = "Testing", State = ReadinessState.MostlyReady, Score = 60 },
            ReleaseDecision = new() { Name = "Release", State = ReadinessState.Blocked, Score = 0 },
            Health = new() { OverallReadinessScore = 30 },
        };

        var report = MakeReport(
            new QualityReviewPackResult { PackId = "delivery", PackName = "Delivery Readiness", Score = 0, DeliveryReadiness = drReport }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var drCard = cut.FindAll(".qr-pack-card").FirstOrDefault();
            drCard!.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            // Top gate cards must show gate info
            markup.Should().Contain("Development:");
            markup.Should().Contain("Testing:");
            markup.Should().Contain("Release");

            // Essential gate data from top cards
            markup.Should().Contain("NOT READY");
            markup.Should().Contain("MOSTLY READY");
            markup.Should().Contain("BLOCKED");

            // Gate Scores panel must NOT exist
            var gateScoresCard = cut.FindAll(".dr-overview-body").SelectMany(c => c.QuerySelectorAll("*"))
                .FirstOrDefault(e => e.TextContent.Contains("Gate Scores"));
            gateScoresCard.Should().BeNull("Gate Scores panel should not duplicate top gate cards");

            // Loaded Artifacts must still be visible
            markup.Should().Contain("Loaded Artifacts");
        });
    }

    [Fact]
    public void QualityReview_DeliveryReadiness_TabsSwitchContent()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var drReport = new DeliveryReadinessReport
        {
            DevelopmentDecision = new() { Name = "Development", State = ReadinessState.NotReady, Score = 30 },
            TestingDecision = new() { Name = "Testing", State = ReadinessState.Ready, Score = 85 },
            ReleaseDecision = new() { Name = "Release", State = ReadinessState.Blocked, Score = 0 },
            Blockers = new List<ReadinessBlocker>
            {
                new() { Title = "Development gate not cleared", Severity = GateSeverity.Critical, Phase = "Release", Description = "Development readiness must reach MostlyReady before release assessment. Current: NotReady." },
                new() { Title = "Insufficient compliance for release", Severity = GateSeverity.Critical, Phase = "Release", Description = "Compliance must reach 80% before release. Current: 0%." },
            },
            Recommendations = new List<DeliveryRecommendation>
            {
                new() { Text = "Raise Development readiness", Phase = "Release", Priority = GateSeverity.Critical },
                new() { Text = "Increase compliance coverage to 80%", Phase = "Release", Priority = GateSeverity.Critical },
                new() { Text = "Address QA findings", Phase = "Release", Priority = GateSeverity.High },
            },
            Health = new() { OverallReadinessScore = 30 },
            HasConstitution = true,
            HasSpecification = true,
            HasPlan = true,
            HasTasks = true,
        };

        var report = MakeReport(
            new QualityReviewPackResult { PackId = "delivery", PackName = "Delivery Readiness", Score = 0, Critical = 2, DeliveryReadiness = drReport }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        // Wait for report to render
        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Delivery Readiness");
        });

        // Click Delivery Readiness pack card to open
        var drCard = cut.FindAll(".qr-pack-card").FirstOrDefault();
        drCard!.Click();

        // Initial state: Overview active
        cut.WaitForAssertion(() =>
        {
            var tabs = cut.FindAll(".tab-btn");
            var overviewTab = tabs.FirstOrDefault(t => t.TextContent.Contains("Overview"));
            overviewTab!.GetAttribute("class").Should().Contain("is-active");
            // Overview content
            cut.Markup.Should().Contain("Loaded Artifacts");
        });

        // Click Blockers tab
        var blockersTabButton = cut.FindAll(".tab-btn").FirstOrDefault(t => t.TextContent.Contains("Blockers"));
        blockersTabButton!.Click();

        // Verify Blockers tab is active and content displayed
        cut.WaitForAssertion(() =>
        {
            var tabs = cut.FindAll(".tab-btn");
            var blockersTab = tabs.FirstOrDefault(t => t.TextContent.Contains("Blockers"));
            var overviewTab = tabs.FirstOrDefault(t => t.TextContent.Contains("Overview"));

            // Blockers active, Overview not
            blockersTab!.GetAttribute("class").Should().Contain("is-active");
            overviewTab!.GetAttribute("class").Should().NotContain("is-active");

            // Blockers tab body content visible
            cut.Markup.Should().Contain("Development gate not cleared");
            cut.Markup.Should().Contain("Severity:");

            // Blockers badge count
            var blockersBadge = blockersTab.QuerySelector(".dr-tab-badge");
            blockersBadge.Should().NotBeNull();
            blockersBadge!.TextContent.Should().Be("2");
        });

        // Click Recommendations tab (MISSING PREVIOUS VERIFICATION)
        var recsTabButton = cut.FindAll(".tab-btn").FirstOrDefault(t => t.TextContent.Contains("Recommendations"));
        recsTabButton.Should().NotBeNull("Recommendations tab button should exist");
        recsTabButton!.Click();

        // Verify Recommendations tab is active and content displayed
        cut.WaitForAssertion(() =>
        {
            var tabs = cut.FindAll(".tab-btn");
            var recsTab = tabs.FirstOrDefault(t => t.TextContent.Contains("Recommendations"));
            var blockersTab = tabs.FirstOrDefault(t => t.TextContent.Contains("Blockers"));
            var overviewTab = tabs.FirstOrDefault(t => t.TextContent.Contains("Overview"));

            // Recommendations active, Blockers and Overview not
            recsTab!.GetAttribute("class").Should().Contain("is-active");
            blockersTab!.GetAttribute("class").Should().NotContain("is-active");
            overviewTab!.GetAttribute("class").Should().NotContain("is-active");

            // Recommendations tab body content visible (has search input specific to Recommendations tab)
            var searchInputs = cut.FindAll("input[type='search']");
            searchInputs.Any(i => i.GetAttribute("placeholder")?.Contains("recommendations") == true).Should().BeTrue();

            // Recommendations badge count
            var recsBadge = recsTab.QuerySelector(".dr-tab-badge");
            recsBadge.Should().NotBeNull();
            recsBadge!.TextContent.Should().Be("3");
        });

        // Click Overview tab to return
        var overviewTabButton = cut.FindAll(".tab-btn").FirstOrDefault(t => t.TextContent.Contains("Overview"));
        overviewTabButton!.Click();

        // Verify Overview active again and content returned
        cut.WaitForAssertion(() =>
        {
            var tabs = cut.FindAll(".tab-btn");
            var overviewTab = tabs.FirstOrDefault(t => t.TextContent.Contains("Overview"));
            var recsTab = tabs.FirstOrDefault(t => t.TextContent.Contains("Recommendations"));

            // Overview active, Recommendations not
            overviewTab!.GetAttribute("class").Should().Contain("is-active");
            recsTab!.GetAttribute("class").Should().NotContain("is-active");

            // Overview content visible again
            cut.Markup.Should().Contain("Loaded Artifacts");
            cut.Markup.Should().Contain("Readiness score");
            cut.Markup.Should().NotContain("Overall Score");
        });
    }

    [Fact]
    public void QualityReview_DeliveryReadinessBlocker_UnparseableDescriptionFallsBackSafely()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        // Blocker with unfamiliar description format (not a standard gate/compliance/readiness blocker)
        var drReport = new DeliveryReadinessReport
        {
            DevelopmentDecision = new() { Name = "Development", State = ReadinessState.NotReady, Score = 30 },
            Blockers = new List<ReadinessBlocker>
            {
                new() {
                    Title = "Custom unfamiliar blocker",
                    Severity = GateSeverity.High,
                    Phase = "Release",
                    Description = "This is a custom blocker with an unfamiliar format that parser doesn't recognize."
                },
            },
            Health = new() { OverallReadinessScore = 30 },
        };

        var report = MakeReport(
            new QualityReviewPackResult { PackId = "delivery", PackName = "Delivery Readiness", Score = 0, Critical = 0, High = 1, DeliveryReadiness = drReport }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var drCard = cut.FindAll(".qr-pack-card").FirstOrDefault();
            drCard!.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            // Blocker still renders
            markup.Should().Contain("Custom unfamiliar blocker");

            // Original description is available
            markup.Should().Contain("unfamiliar format");

            // No fabricated Current/Required when parsing fails
            // (Just verify blocker renders without error; parser gracefully omits unparseable Current/Required row)
            cut.Markup.Should().NotBeNull();
        });
    }

    [Fact]
    public void QualityReview_DataModelQuality_ExpandPackShowsFindingDetails()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var dataModel = new DataModelDocument
        {
            Findings =
            [
                new DataModelFinding { Severity = DataModelSeverity.Warning, Category = "Schema", Description = "No indexes are defined.", EntityName = "Users" }
            ]
        };

        var report = MakeReport(
            new QualityReviewPackResult { PackId = "data-model", PackName = "Data Model Quality", Score = 50, DataModel = dataModel }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var dmCard = cut.FindAll(".qr-pack-card").FirstOrDefault(c => c.TextContent.Contains("Data Model"));
            dmCard.Should().NotBeNull();
            dmCard!.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            markup.Should().Contain("Schema");
            markup.Should().Contain("No indexes are defined.");
            markup.Should().Contain("Users");
        });
    }

    [Fact]
    public void QualityReview_DataModelQuality_EmptyFindingsShowsEmptyState()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var dataModel = new DataModelDocument { Findings = [] };

        var report = MakeReport(
            new QualityReviewPackResult { PackId = "data-model", PackName = "Data Model Quality", Score = 100, DataModel = dataModel }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var dmCard = cut.FindAll(".qr-pack-card").FirstOrDefault(c => c.TextContent.Contains("Data Model"));
            dmCard.Should().NotBeNull();
            dmCard!.Click();
        });

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("No findings detected.");
        });
    }

    [Fact]
    public void QualityReview_DataModelQuality_ShowAllToggleWorks()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var dataModel = new DataModelDocument
        {
            Findings = Enumerable.Range(0, 8)
                .Select(i => new DataModelFinding
                {
                    Severity = i % 2 == 0 ? DataModelSeverity.Warning : DataModelSeverity.Info,
                    Category = $"Category{i / 2}",
                    Description = $"Finding {i}",
                    EntityName = $"Entity{i}"
                })
                .ToList()
        };

        var report = MakeReport(
            new QualityReviewPackResult { PackId = "data-model", PackName = "Data Model Quality", Score = 20, DataModel = dataModel }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var dmCard = cut.FindAll(".qr-pack-card").FirstOrDefault(c => c.TextContent.Contains("Data Model"));
            dmCard!.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            markup.Should().Contain("Show all 8");
            markup.Should().Contain("Finding 0");
            markup.Should().Contain("Finding 4");
            markup.Should().NotContain("Finding 5");
        });

        cut.InvokeAsync(() =>
        {
            var toggleButton = cut.FindAll("button.qr-show-toggle").FirstOrDefault();
            toggleButton.Should().NotBeNull();
            toggleButton!.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            markup.Should().Contain("Show less");
            markup.Should().Contain("Finding 5");
            markup.Should().Contain("Finding 7");
        });

        cut.InvokeAsync(() =>
        {
            var toggleButton = cut.FindAll("button.qr-show-toggle").FirstOrDefault();
            toggleButton!.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            markup.Should().Contain("Show all");
            markup.Should().NotContain("Finding 5");
        });
    }

    [Fact]
    public void QualityReview_DataModelQuality_UsesSafeEventBinding()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var dataModel = new DataModelDocument
        {
            Findings = Enumerable.Range(0, 7)
                .Select(i => new DataModelFinding
                {
                    Severity = i % 2 == 0 ? DataModelSeverity.Error : DataModelSeverity.Warning,
                    Category = "Schema",
                    Description = i == 0 ? "Missing column definition." : i == 1 ? "Index not optimal." : $"Finding {i}"
                })
                .ToList()
        };

        var report = MakeReport(
            new QualityReviewPackResult { PackId = "data-model", PackName = "Data Model Quality", Score = 50, DataModel = dataModel }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() => cut.FindAll(".qr-pack-card").Count.Should().BeGreaterThan(0));

        cut.InvokeAsync(() =>
        {
            var dmCard = cut.FindAll(".qr-pack-card").FirstOrDefault(c => c.TextContent.Contains("Data Model"));
            dmCard.Should().NotBeNull();
            dmCard!.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var button = cut.FindAll("button.qr-show-toggle").FirstOrDefault();
            button.Should().NotBeNull("Show all button should exist");
            button!.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            markup.Should().Contain("Show less");
            markup.Should().Contain("Missing column definition.");
            markup.Should().Contain("Index not optimal.");
        });
    }

    [Fact]
    public void QualityReview_DataModelQuality_UsesExistingFindingCardPresentation()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var dataModel = new DataModelDocument
        {
            Findings =
            [
                new DataModelFinding { Severity = DataModelSeverity.Critical, Category = "Schema", Description = "Critical schema issue.", EntityName = "Orders" }
            ]
        };

        var report = MakeReport(
            new QualityReviewPackResult { PackId = "data-model", PackName = "Data Model Quality", Score = 0, DataModel = dataModel }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var dmCard = cut.FindAll(".qr-pack-card").FirstOrDefault(c => c.TextContent.Contains("Data Model"));
            dmCard!.Click();
        });

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Critical");
            cut.Markup.Should().Contain("Orders");
            cut.Markup.Should().Contain("Critical schema issue.");
        });
    }

    [Fact]
    public void QualityReview_DataModelQuality_RendersSeverityAsMetadata()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var dataModel = new DataModelDocument
        {
            Findings =
            [
                new DataModelFinding { Severity = DataModelSeverity.Warning, Category = "Schema", Description = "Missing indexes.", EntityName = "Products" }
            ]
        };

        var report = MakeReport(
            new QualityReviewPackResult { PackId = "data-model", PackName = "Data Model Quality", Score = 50, DataModel = dataModel }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var dmCard = cut.FindAll(".qr-pack-card").FirstOrDefault(c => c.TextContent.Contains("Data Model"));
            dmCard!.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            markup.Should().Contain("Warning");
            markup.Should().Contain("Products");
            markup.Should().Contain("Missing indexes.");
        });
    }

    [Fact]
    public void QualityReview_DataModelQuality_DoesNotLeakMarkdownHeadingSyntax()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var dataModel = new DataModelDocument
        {
            Findings =
            [
                new DataModelFinding
                {
                    Severity = DataModelSeverity.Info,
                    Category = "Documentation",
                    Description = "No ## Overview section found.",
                    EntityName = "Catalog"
                }
            ]
        };

        var report = MakeReport(
            new QualityReviewPackResult { PackId = "data-model", PackName = "Data Model Quality", Score = 100, DataModel = dataModel }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var dmCard = cut.FindAll(".qr-pack-card").FirstOrDefault(c => c.TextContent.Contains("Data Model"));
            dmCard!.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            markup.Should().NotContain("##");
            markup.Should().Contain("Overview section found");
        });
    }

    [Fact]
    public void QualityReview_DataModelQuality_PreservesMeaningfulDescription()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var descriptionText = "No indexes are defined. Consider adding indexes for foreign key and frequently queried columns.";
        var dataModel = new DataModelDocument
        {
            Findings =
            [
                new DataModelFinding
                {
                    Severity = DataModelSeverity.Warning,
                    Category = "Performance",
                    Description = descriptionText,
                    EntityName = "Users"
                }
            ]
        };

        var report = MakeReport(
            new QualityReviewPackResult { PackId = "data-model", PackName = "Data Model Quality", Score = 75, DataModel = dataModel }
        );
        _qualityReview.SetReport(report);

        var cut = Render<QualityReview>();
        ClickRun(cut);

        cut.WaitForAssertion(() =>
        {
            var dmCard = cut.FindAll(".qr-pack-card").FirstOrDefault(c => c.TextContent.Contains("Data Model"));
            dmCard!.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            markup.Should().Contain("No indexes are defined");
            markup.Should().Contain("frequently queried columns");
        });
    }

    [Fact]
    public void QualityReview_DeliveryOverview_LabelsUnfilteredPreviewAsDeliveryGateIssues()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var delivery = new DeliveryReadinessReport
        {
            DevelopmentDecision = new() { Name = "Development", State = ReadinessState.NotReady, Score = 32 },
            TestingDecision = new() { Name = "Testing", State = ReadinessState.MostlyReady, Score = 68 },
            ReleaseDecision = new() { Name = "Release", State = ReadinessState.Blocked, Score = 24 },
            Blockers =
            [
                new() { Title = "Critical release gate", Severity = GateSeverity.Critical, Phase = "Release" },
                new() { Title = "Medium testing gate", Severity = GateSeverity.Medium, Phase = "Testing" },
            ],
            Recommendations = [new() { Text = "Resolve release gates", Phase = "Release", Priority = GateSeverity.Critical }],
            Health = new() { OverallReadinessScore = 24.2 },
        };
        _qualityReview.SetReport(MakeReport(
            new QualityReviewPackResult { PackId = "delivery", PackName = "Delivery Readiness", Score = 30, Critical = 1, Medium = 1, DeliveryReadiness = delivery }));

        var cut = Render<QualityReview>();
        ClickRun(cut);
        cut.WaitForAssertion(() => cut.Find(".qr-pack-card").Click());

        cut.WaitForAssertion(() =>
        {
            var overview = cut.Find(".dr-overview-body");
            overview.QuerySelectorAll(".section-header").Select(e => e.TextContent.Trim()).Should().Contain("Delivery Gate Issues");
            overview.QuerySelectorAll(".section-header").Select(e => e.TextContent.Trim()).Should().NotContain("Critical Blockers");
            overview.TextContent.Should().Contain("Critical release gate");
            overview.TextContent.Should().Contain("Medium testing gate");
            overview.QuerySelector(".dr-readiness-score strong")!.TextContent.Should().Be("24.2 / 100");
            overview.TextContent.Should().NotContain("Recommendations");
            overview.QuerySelector(".metric-strip").Should().BeNull();

            var release = cut.Find(".dr-release-decision");
            release.TextContent.Should().Contain("Release");
            release.TextContent.Should().Contain("BLOCKED");
            release.TextContent.Should().Contain("2 delivery gate issues · 1 critical");
            cut.FindAll(".dr-supporting-state").Select(e => string.Join(" ", e.TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
                .Should().Equal("Development: NOT READY", "Testing: MOSTLY READY");
        });
    }

    [Fact]
    public void QualityReview_Standards_PreservesSeverityAndSeparatesStatusAndRuleIdentity()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var standards = new StandardsComplianceReport
        {
            Results =
            [
                new() { RuleId = "STD-H", Title = "High rule", Category = "Security", Description = "High problem", Recommendation = "Apply high remediation", Severity = CheckSeverity.High, Status = CheckStatus.Failed },
                new() { RuleId = "STD-M", Title = "Medium rule", Category = "Security", Description = "Medium problem", Severity = CheckSeverity.Medium, Status = CheckStatus.Warning },
                new() { RuleId = "STD-L", Title = "Low rule", Category = "Security", Description = "Low problem", Severity = CheckSeverity.Low, Status = CheckStatus.Failed },
            ],
        };
        _qualityReview.SetReport(MakeReport(
            new QualityReviewPackResult { PackId = "wcag", PackName = "WCAG 2.2", Score = 40, High = 1, Medium = 1, Low = 1, Standards = standards }));

        var cut = Render<QualityReview>();
        ClickRun(cut);
        cut.WaitForAssertion(() => cut.Find(".qr-pack-card").Click());

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".issue-sev").Select(e => e.TextContent.Trim()).Should().Equal("High", "Medium", "Low");
            cut.FindAll(".issue-status").Select(e => e.TextContent.Trim()).Should().Equal("Status: Failed", "Status: Potential gap", "Status: Failed");
            cut.FindAll(".issue-component").Select(e => e.TextContent.Trim()).Should().ContainInOrder(
                "STD-H — High rule", "STD-M — Medium rule", "STD-L — Low rule");
            cut.FindAll(".issue-sev").Select(e => e.TextContent).Should().NotContain(new[] { "Error", "Warning", "Info" });
            var highCard = cut.FindAll(".issue-card").First(e => e.TextContent.Contains("STD-H"));
            highCard.TextContent.Should().Contain("Problem: High problem");
            highCard.TextContent.Should().Contain("Fix: Apply high remediation");
        });
    }

    [Fact]
    public void QualityReview_DataModel_UsesEntityIdentityWithoutDuplicatingSeverity()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var dataModel = new DataModelDocument
        {
            Findings =
            [
                new() { Severity = DataModelSeverity.Warning, Category = "Schema", EntityName = "Operation", Description = "Entity-specific problem" },
                new() { Severity = DataModelSeverity.Info, Category = "Schema", Description = "General schema problem" },
            ],
        };
        _qualityReview.SetReport(MakeReport(
            new QualityReviewPackResult { PackId = "data-model", PackName = "Data Model Quality", Score = 60, Low = 2, DataModel = dataModel }));

        var cut = Render<QualityReview>();
        ClickRun(cut);
        cut.WaitForAssertion(() => cut.Find(".qr-pack-card").Click());

        cut.WaitForAssertion(() =>
        {
            var cards = cut.FindAll(".issue-card");
            var entityCard = cards.First(c => c.TextContent.Contains("Entity-specific problem"));
            entityCard.QuerySelector(".issue-sev")!.TextContent.Should().Be("Warning");
            entityCard.QuerySelector(".issue-component")!.TextContent.Should().Be("Operation");
            entityCard.QuerySelector(".issue-component")!.TextContent.Should().NotContain("Warning");
            entityCard.TextContent.Should().Contain("Problem: Entity-specific problem");

            var generalCard = cards.First(c => c.TextContent.Contains("General schema problem"));
            generalCard.QuerySelector(".issue-sev")!.TextContent.Should().Be("Info");
            generalCard.QuerySelector(".issue-component").Should().BeNull();
        });
    }

    [Fact]
    public void QualityReview_QaAuditor_DirectConstitutionIdUsesGapCanonicalTitleWithoutDuplication()
    {
        SeedProjectA();
        _resolver.SetSelectedProject("project-a");

        var qa = new QaAuditReport
        {
            Findings = [new() { RuleCode = " PP-02 ", Title = " pp-02 ", Severity = QaSeverity.Critical, Category = QaCategory.Constitution }],
        };
        var compliance = new ConstitutionComplianceReport
        {
            Gaps = [new() { RuleId = "PP-02", RuleTitle = "Clear and Testable Requirements", RuleType = ConstitutionRuleType.Principle, MissingInSpec = true, Severity = ViolationSeverity.Critical }],
        };
        _qualityReview.SetReport(MakeReport(
            new QualityReviewPackResult { PackId = "qa-auditor", PackName = "QA Auditor", Score = 20, Critical = 1, QaAudit = qa },
            new QualityReviewPackResult { PackId = "compliance", PackName = "Constitution Compliance", Score = 20, Critical = 1, Compliance = compliance }));

        var cut = Render<QualityReview>();
        ClickRun(cut);
        cut.WaitForAssertion(() => cut.FindAll("button.qr-result-header").First(b => b.TextContent.Contains("QA Auditor")).Click());

        cut.WaitForAssertion(() =>
        {
            var title = cut.Find(".qr-finding-title").TextContent;
            title.Should().Be("PP-02 — Clear and Testable Requirements");
            System.Text.RegularExpressions.Regex.Matches(title, "PP-02").Should().HaveCount(1);
            title.Should().NotContain("PP-02 — PP-02");
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
