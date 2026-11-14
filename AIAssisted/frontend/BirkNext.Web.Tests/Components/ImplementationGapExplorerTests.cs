using BirkNext.ImplementationGaps;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Implementation gaps: the current import's evaluation renders a blocked, contract-backed feature with its gate, separate task/implementation/
/// verification columns and "Not assessed" runtime execution; findings are filterable and link to the exact task document, specification,
/// source inventory and gate; a review is recorded and reloaded; re-evaluation sends the workspace's recorded executions; the Quality Review
/// variant shows readiness only; without the API client nothing renders. Synthetic data shaped like the real Skole case.
/// </summary>
public sealed class ImplementationGapExplorerTests : BunitContext
{
    private static readonly Guid EvalId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid SnapshotId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private const string Doc = "Skole/specs/001-skoletjenesten/tasks.md";
    private readonly Mock<IImplementationGapApiService> _api = new();
    private readonly SddLifecycleState _lifecycle = new() { CurrentProjectImportId = "import-7516b48d6bf77426" };

    private static GapTaskAssessment T(string id, int line, bool test = false) => new()
    {
        TaskId = id, Source = new(Doc, line), TaskState = GapTaskState.NotStarted, Implementation = GapImplementationEvidence.NoMatchingSource, ScopeAbsentFromArchive = true,
        Verification = GapVerificationEvidence.TestNotFound, IsTestTask = test, GateIds = ["ADR-028"], DeclaredScopes = ["Skole/src/"],
        Artifacts = [new() { Kind = test ? GapArtifactKind.Test : GapArtifactKind.File, Expected = $"Skole/src/X/{id}.cs", Relationship = GapRelationship.Missing, Detail = "Not in the archive." }],
    };

    private static GapFinding Finding(string id, GapFindingType type, GapFindingSeverity severity, string title, string? task = null, string? gate = null, bool review = false) => new()
    {
        Id = id, Type = type, Severity = severity, Title = title, TaskId = task, GateId = gate, FeatureId = "gate:" + Doc + ":ADR-028", Expected = "expected", Rule = GapRules.Rules[type],
        Confidence = review ? GapConfidence.Low : GapConfidence.High, NeedsHumanReview = review, Guidance = "guidance", SnapshotId = SnapshotId, EvaluatedAt = DateTimeOffset.UnixEpoch,
        Sources = [new(Doc, 600)], MissingEvidence = ["No file under Skole/src/"],
    };

    private static GapEvaluation Evaluation(string review = "Not reviewed") => new()
    {
        Id = EvalId, ScopeKey = "import:import-7516b48d6bf77426", SnapshotId = SnapshotId, ArchiveName = "M2LB Skole.zip", ArchiveSha256 = "7516b48d6bf77426feef67bca89d68070486896a0430b30c41085f103149dcd8",
        EvaluatedAt = DateTimeOffset.UnixEpoch, TaskCount = 159, CompletedTaskCount = 0,
        Tasks = [T("T105", 527, test: true), T("T119", 600), T("T120", 601), T("T121", 606), T("T122", 607)],
        Gates = [new() { GateId = "ADR-028", Label = "ADR-028 Parquet.Net sign-off (T014 drafts it)", Status = GapGateStatus.Pending, BlockedTaskIds = ["T105", "T119", "T120", "T121", "T122"],
            Evidence = [new(Doc, 38)], Reason = "The decision register contains no entry for ADR-028 (last recorded: ADR-027)." }],
        Features = [new()
        {
            FeatureId = "gate:" + Doc + ":ADR-028", Name = "Parquet.Net for the Bufdata delivery", Basis = "Tasks gated by ADR-028", DocumentPath = Doc,
            TaskIds = ["T105", "T119", "T120", "T121", "T122"], ImplementationTaskIds = ["T119", "T120", "T121", "T122"], TestTaskIds = ["T105"], GateIds = ["ADR-028"],
            Contracts = [new("Skole/specs/001-skoletjenesten/contracts/bufdata-leveranse.md")], RequirementIds = ["FR-019", "FR-036"], Readiness = GapReadiness.Blocked,
            Explanation = "Not ready: gate ADR-028 is Pending.", SourceImplementation = "Implementation not evidenced in supplied archive: Skole/src/ has no implementation file in the archive.",
            Tests = "0 of 1 test task(s) have their test file in the snapshot; 0 executed.", RuntimeExecution = "Not assessed — no runtime execution was observed or recorded.",
            DependencyEvidence = "Parquet.Net: Missing", Dimensions = [new("Source implementation coverage", 0, 4, 0, "basis"), new("Test execution coverage", 0, null, 0, "basis")],
        }],
        Findings =
        [
            Finding("GAP-l", GapFindingType.IntegrationNotImplemented, GapFindingSeverity.High, "Parquet.Net for the Bufdata delivery — implementation not evidenced in supplied archive") with { ReviewStatus = review },
            Finding("GAP-k", GapFindingType.DecisionNotApproved, GapFindingSeverity.High, "ADR-028 — no recorded approval (Pending)", gate: "ADR-028"),
            Finding("GAP-n", GapFindingType.ArtifactMovedOrRenamed, GapFindingSeverity.Low, "T120: ParquetSkrivere.cs found elsewhere", task: "T120", review: true),
        ],
    };

    public ImplementationGapExplorerTests()
    {
        var workspace = new Mock<IWorkspaceSessionService>();
        workspace.SetupGet(w => w.SddLifecycle).Returns(_lifecycle);
        Services.AddSingleton(workspace.Object);
        Services.AddSingleton(_api.Object);
        _api.Setup(a => a.ListAsync("import:import-7516b48d6bf77426", It.IsAny<CancellationToken>())).ReturnsAsync(([new GapEvaluationSummary(EvalId, "import:x", "x", SnapshotId, "M2LB Skole.zip", DateTimeOffset.UnixEpoch, 159, 3, 1)], null));
        _api.Setup(a => a.GetAsync(EvalId, It.IsAny<CancellationToken>())).ReturnsAsync((Evaluation(), null));
    }

    [Fact]
    public void A_blocked_feature_shows_contract_tasks_gate_and_not_evidenced_source_without_a_runtime_pass()
    {
        var cut = Render<ImplementationGapExplorer>();
        // The overview lists the feature compactly; its full evidence opens on demand.
        cut.WaitForElement("[data-testid=ig-overview-row]");
        cut.Find("[data-testid=ig-overview-row]").GetAttribute("data-readiness").Should().Be("Blocked");
        cut.Find("[data-testid=ig-overview-gates]").TextContent.Should().Contain("ADR-028 (Pending)");
        cut.FindAll("[data-testid=ig-feature]").Should().BeEmpty("detail is on demand, not one large card per feature");
        cut.Find("[data-testid=ig-overview-open]").Click();
        var feature = cut.Find("[data-testid=ig-feature]");
        feature.GetAttribute("data-readiness").Should().Be("Blocked");
        cut.Find("[data-testid=ig-feature-name]").TextContent.Should().Be("Parquet.Net for the Bufdata delivery");
        cut.Find("[data-testid=ig-contract]").TextContent.Should().Contain("contracts/bufdata-leveranse.md");
        cut.Find("[data-testid=ig-tasks]").TextContent.Should().Be("T105, T119–T122");
        cut.Find("[data-testid=ig-gates]").TextContent.Should().Contain("ADR-028 · Pending");
        cut.Find("[data-testid=ig-source]").TextContent.Should().StartWith("Implementation not evidenced in supplied archive");
        cut.Find("[data-testid=ig-runtime]").TextContent.Should().StartWith("Not assessed");
        cut.Find("[data-testid=ig-context]").TextContent.Should().Contain("SHA-256 7516b48d6bf7").And.Contain("159 tasks (0 checked)");
        cut.FindAll("[data-testid=ig-task-row]").Should().HaveCount(5);
        var row = cut.Find("[data-testid=ig-task-row][data-task=T120]");
        row.QuerySelector("[data-testid=ig-task-impl]")!.TextContent.Should().Be("No matching source — not evidenced in supplied archive");
        row.QuerySelector("[data-testid=ig-task-verification]")!.TextContent.Should().Be("Test not found");
        row.TextContent.Should().Contain("Not started");
        cut.Find("[data-testid=ig-dimensions]").TextContent.Should().Contain("0 of 4").And.Contain("Not assessed");
        cut.Find("[data-testid=ig-gate][data-status=Pending]").TextContent.Should().Contain("ADR-027");
        cut.Find("[data-testid=ig-panel]").TextContent.Should().NotContain("Runtime verified").And.NotContain("%", "readiness is never a percentage score");
    }

    [Fact]
    public void Findings_filter_and_link_to_the_exact_task_document_specification_source_and_gate()
    {
        var cut = Render<ImplementationGapExplorer>();
        cut.WaitForElement("[data-testid=ig-finding]");
        cut.FindAll("[data-testid=ig-finding]").Should().HaveCount(3);
        var moved = cut.Find("[data-testid=ig-finding][data-id=GAP-n]");
        moved.QuerySelector("[data-testid=ig-open-task]")!.GetAttribute("href").Should().Be($"/task-explorer?task=T120&document={Uri.EscapeDataString(Doc)}");
        moved.QuerySelector("[data-testid=ig-open-spec]")!.GetAttribute("href").Should().Contain(Uri.EscapeDataString("Skole/specs/001-skoletjenesten/spec.md"));
        moved.QuerySelector("[data-testid=ig-open-source]")!.GetAttribute("href").Should().Be($"/source-analysis?snapshot={SnapshotId:D}");
        cut.Find("[data-testid=ig-finding][data-id=GAP-k] [data-testid=ig-open-gate]").GetAttribute("href").Should().EndWith("#ig-gate-adr-028");

        cut.Find("[data-testid=ig-filter-severity]").Change("Low");
        cut.FindAll("[data-testid=ig-finding]").Should().ContainSingle().Which.GetAttribute("data-id").Should().Be("GAP-n");
        cut.Find("[data-testid=ig-filter-severity]").Change("");
        cut.Find("[data-testid=ig-filter-review]").Change(true);
        cut.FindAll("[data-testid=ig-finding]").Should().ContainSingle().Which.GetAttribute("data-id").Should().Be("GAP-n");
    }

    [Fact]
    public void A_review_is_recorded_and_the_evaluation_reloaded()
    {
        _api.Setup(a => a.ReviewAsync(EvalId, It.Is<GapReviewRequest>(r => r.FindingId == "GAP-l" && r.Decision == "IssueFound" && r.Note == "Sign-off requested"), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null).Callback(() => _api.Setup(a => a.GetAsync(EvalId, It.IsAny<CancellationToken>())).ReturnsAsync((Evaluation("IssueFound"), null)));
        var cut = Render<ImplementationGapExplorer>();
        cut.WaitForElement("[data-testid=ig-finding]");
        cut.Find("[data-testid=ig-finding][data-id=GAP-l] [data-testid=ig-review]").Click();
        cut.Find("[data-testid=ig-review-note]").Change("Sign-off requested");
        cut.Find("[data-testid=ig-review-save]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=ig-finding][data-id=GAP-l]").GetAttribute("data-review").Should().Be("IssueFound"));
        _api.VerifyAll();
    }

    [Fact]
    public void Re_evaluation_sends_only_recorded_executions_with_their_source()
    {
        _lifecycle.TestExecutions.Add(new SddTestExecutionEvidence { FullyQualifiedTestName = "Demo.Unit.ExporterTests.A", Result = "Passed", EvidenceKind = "Executed", ResultSource = "demo.trx" });
        _lifecycle.TestExecutions.Add(new SddTestExecutionEvidence { FullyQualifiedTestName = null, Result = "Passed", EvidenceKind = "Executed" });
        GapReevaluateRequest? sent = null;
        _api.Setup(a => a.ReevaluateAsync(EvalId, It.IsAny<GapReevaluateRequest>(), It.IsAny<CancellationToken>())).Callback<Guid, GapReevaluateRequest, CancellationToken>((_, r, _) => sent = r)
            .ReturnsAsync((Evaluation() with { Id = Guid.NewGuid(), ExecutionSources = ["demo.trx"] }, null));
        var cut = Render<ImplementationGapExplorer>();
        cut.WaitForElement("[data-testid=ig-reevaluate]");
        cut.Find("[data-testid=ig-reevaluate]").TextContent.Should().Contain("(1)");
        cut.Find("[data-testid=ig-reevaluate]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=ig-executions]").TextContent.Should().Be("executions: demo.trx"));
        sent!.Executions.Should().ContainSingle().Which.Should().Be(new GapExecutedTest("Demo.Unit.ExporterTests.A", GapExecutionOutcome.Passed, "demo.trx", null));
    }

    [Fact]
    public void The_quality_review_variant_shows_readiness_only_with_a_link_to_the_explorer()
    {
        var cut = Render<ImplementationGapExplorer>(p => p.Add(c => c.ReadinessOnly, true));
        cut.WaitForElement("[data-testid=ig-overview-row]");
        cut.FindAll("[data-testid=ig-finding]").Should().BeEmpty();
        cut.Find("[data-testid=ig-open-explorer]").GetAttribute("href").Should().Be("/implementation-review#ig-h");
    }

    [Fact]
    public void Without_an_evaluation_it_says_how_one_is_made_and_without_the_client_nothing_renders()
    {
        _api.Setup(a => a.ListAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync((new List<GapEvaluationSummary>(), null));
        var cut = Render<ImplementationGapExplorer>();
        cut.WaitForElement("[data-testid=ig-none]").TextContent.Should().Contain("tasks.md");

        using var bare = new BunitContext();
        bare.Render<ImplementationGapExplorer>().Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void Features_filter_by_status_and_missing_execution_and_a_zero_confirmation_is_not_called_not_implemented()
    {
        ImplementationGapExplorer.ReadinessLabel(GapReadiness.NotImplemented).Should().Be("Implementation not confirmed");
        var cut = Render<ImplementationGapExplorer>();
        cut.WaitForElement("[data-testid=ig-overview-row]");
        cut.Find("[data-testid=ig-filter-missing-tests]").Change(true);
        cut.FindAll("[data-testid=ig-overview-row]").Should().HaveCount(1, "the blocked feature has no executed test");
        cut.Find("[data-testid=ig-filter-readiness]").Change("Verified");
        cut.Find("[data-testid=ig-overview-none]");
        cut.Find("[data-testid=ig-filter-readiness]").Change("");
        cut.Find("[data-testid=ig-feature-search]").Input("parquet");
        cut.FindAll("[data-testid=ig-overview-row]").Should().HaveCount(1);
        cut.Find("[data-testid=ig-overview]").TextContent.Should().NotContain("%");
    }
}
