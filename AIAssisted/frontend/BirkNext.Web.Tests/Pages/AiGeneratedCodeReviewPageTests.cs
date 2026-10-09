using BirkNext.AiCodeReview;
using BirkNext.SourceEvidence;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>AI-Generated Code Review page: honest pre-run states, snapshot/baseline selection, results with provenance, export and wording.</summary>
public sealed class AiGeneratedCodeReviewPageTests : BunitContext
{
    private readonly Mock<IAiCodeReviewApiService> _api = new();
    private readonly WorkspaceArtifactRepository _workspace = new();
    private static readonly Guid Current = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Older = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid Other = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");

    public AiGeneratedCodeReviewPageTests()
    {
        var context = new Mock<IReviewContextProvider>();
        context.Setup(x => x.GetCurrent()).Returns(new ReviewContext());
        Services.AddSingleton(context.Object);
        Services.AddSingleton<IWorkspaceSessionService>(_workspace);
        Services.AddSingleton(_api.Object);
        Services.AddScoped<SddEvidenceGraphService>();
        Services.AddSingleton(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Projection(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Loaded("Imported", "import-1", "Contoso Shop", WorkspaceArtifactType.Specification)).Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
        _api.Setup(x => x.HistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
    }

    private static ReviewSourceSnapshot Snap(Guid id, string repo, int daysAgo, bool latest, bool evidence = true) => new()
    {
        SnapshotId = id, RepositoryKey = repo.ToLowerInvariant(), Repository = repo, ArchiveName = $"{repo}.zip", Fingerprint = new string('a', 64),
        AnalyzedAt = DateTimeOffset.UtcNow.AddDays(-daysAgo), SourceStatus = "Ready", Latest = latest, HasConsumerEvidence = evidence,
        ConsumerEvidenceNote = evidence ? null : "Analyzed before code-risk observations existed: source-level rules are not assessed until the source is analyzed again.",
    };

    private void Sources(params ReviewSourceSnapshot[] snapshots) =>
        _api.Setup(x => x.SourcesAsync(It.IsAny<CancellationToken>())).ReturnsAsync((new ReviewSourceOptions { Snapshots = [.. snapshots] }, (string?)null));

    private static AiCodeReviewResult Result(bool change = false) => new()
    {
        Mode = change ? AiReviewMode.SnapshotChange : AiReviewMode.CurrentSnapshot, CompletedAt = DateTimeOffset.UtcNow, ProjectName = "Contoso Shop",
        Current = new(Current, "Shop", "Shop.zip", new string('a', 64), DateTimeOffset.UtcNow, true),
        Baseline = change ? new(Older, "Shop", "Shop.zip", new string('b', 64), DateTimeOffset.UtcNow.AddDays(-2), true) : null,
        Categories =
        [
            new(AiCodeReviewCategory.Security, AiCategoryStatus.Findings, 1, "12 production and 3 test C# file(s)", 0, "4 of 5 rule(s) executed."),
            new(AiCodeReviewCategory.RequirementsAlignment, AiCategoryStatus.NotAssessed, 0, "snapshot metadata", 0, "No Specification is loaded in the current workspace."),
            new(AiCodeReviewCategory.DeadCode, AiCategoryStatus.NoIndicators, 0, "12 production and 3 test C# file(s)", 1, AiCodeReviewText.NoIndicators),
            new(AiCodeReviewCategory.ContractDrift, AiCategoryStatus.NotApplicable, 0, "—", 0, "Needs a baseline snapshot of the same repository."),
        ],
        Rules =
        [
            new("AIC-SEC-001", AiCodeReviewCategory.Security, "Authorization protection removed in source evidence", AiRuleExecutionState.Executed, null, 1, [AiEvidenceSource.Source]),
            new("AIC-TEST-006", AiCodeReviewCategory.Tests, "Tests mirroring the implementation", AiRuleExecutionState.Unsupported, "No AST comparison.", 0, [AiEvidenceSource.Tests]),
        ],
        Findings =
        [
            new AiCodeFinding
            {
                LogicalId = "x", RuleId = "AIC-SEC-001", Category = AiCodeReviewCategory.Security, Severity = AiFindingSeverity.High,
                Title = "Authorization protection removed in source evidence: GET OrdersController.List", Description = "Baseline: [Authorize] on the type. Current: no authorization metadata.",
                Rationale = "Authorization metadata of the same operation compared between snapshots.", EvidenceSources = [AiEvidenceSource.Source, AiEvidenceSource.SnapshotDiff],
                SourceSnapshotId = Current, Locations = [new("Shop/Controllers/OrdersController.cs", 8, "GET OrdersController.List")], Change = change ? AiChangeKind.Changed : null,
                Baseline = change ? "[Authorize] on the type" : null, Current = change ? "no authorization metadata" : null,
                Limitation = "Source authorization metadata only — runtime enforcement is not verified.", Recommendation = "Restore authorization.",
            },
        ],
        Changes = change ? [new("Files", 2, 0, 3, 10)] : [],
        Attention = change ? AiReviewAttention.High : null, AttentionBasis = change ? "Derived only from finding severities — not a score." : null,
        Limitations = ["TypeScript: 4 file(s) present — No TypeScript code-risk analyzer."],
    };

    [Fact]
    public void Without_a_source_snapshot_the_page_asks_for_source_analysis_and_never_offers_upload()
    {
        Sources();
        var cut = Render<AiGeneratedCodeReview>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=aic-source-empty]"));
        cut.Find("[data-testid=aic-open-source-analysis]").GetAttribute("href").Should().Contain("source-analysis");
        cut.FindAll("input[type=file]").Should().BeEmpty("source comes only from Source Analysis");
        cut.FindAll("[data-testid=aic-run]").Should().BeEmpty();
        cut.Find("[data-testid=aic-ready][data-key=source]").GetAttribute("data-available").Should().Be("false");
        cut.Find("[data-testid=aic-disclaimer]").TextContent.Should().Contain("does not detect whether code was written by AI");
    }

    [Fact]
    public void Current_snapshot_is_preselected_and_baselines_are_limited_to_the_same_repository()
    {
        Sources(Snap(Current, "Shop", 0, true), Snap(Older, "Shop", 2, false), Snap(Other, "Billing", 1, true));
        var cut = Render<AiGeneratedCodeReview>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=aic-current]"));
        cut.Find("[data-testid=aic-current]").GetAttribute("value").Should().Be(Current.ToString());
        var baselines = cut.FindAll("[data-testid=aic-baseline] option").Select(o => o.GetAttribute("value")).ToList();
        baselines.Should().Equal("", Older.ToString());
        cut.Find("[data-testid=aic-scope-changed]").HasAttribute("disabled").Should().BeTrue("changed-files scope needs a baseline");
        cut.Find("[data-testid=aic-run]").HasAttribute("disabled").Should().BeFalse();
        cut.Find("[data-testid=aic-mode-value]").TextContent.Should().Be("Current snapshot review");

        cut.Find("[data-testid=aic-baseline]").Change(Older.ToString());
        cut.Find("[data-testid=aic-mode-value]").TextContent.Should().Contain("Snapshot change review");
        cut.Find("[data-testid=aic-scope-changed]").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void A_snapshot_without_code_risk_evidence_is_flagged_as_needing_reanalysis()
    {
        Sources(Snap(Current, "Shop", 0, true, evidence: false));
        var cut = Render<AiGeneratedCodeReview>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=aic-snapshot-old]").TextContent.Should().Contain("analyzed again"));
        cut.Find("[data-testid=aic-ready][data-key=code-risk]").GetAttribute("data-available").Should().Be("false");
    }

    [Fact]
    public void Run_sends_the_workspace_requirement_summary_and_renders_categories_findings_and_rules()
    {
        Sources(Snap(Current, "Shop", 0, true), Snap(Older, "Shop", 2, false));
        _workspace.Set(WorkspaceArtifactType.Specification, "# Spec\n\n## Requirements\n\n- **FR-001**: The system MUST list orders.\n");
        AiCodeReviewRequest? sent = null;
        _api.Setup(x => x.RunAsync(It.IsAny<AiCodeReviewRequest>(), It.IsAny<CancellationToken>())).Callback<AiCodeReviewRequest, CancellationToken>((r, _) => sent = r)
            .ReturnsAsync((Result(change: true), (string?)null));
        var cut = Render<AiGeneratedCodeReview>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=aic-baseline]"));
        cut.Find("[data-testid=aic-baseline]").Change(Older.ToString());
        cut.Find("[data-testid=aic-scope-changed]").Change(true);
        cut.Find("[data-testid=aic-run]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=aic-result]"));
        sent!.CurrentSnapshotId.Should().Be(Current);
        sent.BaselineSnapshotId.Should().Be(Older);
        sent.Scope.Should().Be(AiReviewScope.ChangedFiles);
        sent.Workspace!.SpecificationAvailable.Should().BeTrue();
        sent.Workspace.ProjectName.Should().Be("Contoso Shop");

        cut.FindAll("[data-testid=aic-category]").Select(c => c.GetAttribute("data-status")).Should().Contain(["Findings", "NotAssessed", "NoIndicators", "NotApplicable"]);
        cut.Find("[data-testid=aic-category][data-category=RequirementsAlignment]").TextContent.Should().Contain("No Specification is loaded");
        cut.Find("[data-testid=aic-attention]").TextContent.Should().Contain("not a score");
        var finding = cut.Find("[data-testid=aic-finding]");
        finding.GetAttribute("data-rule").Should().Be("AIC-SEC-001");
        finding.TextContent.Should().Contain("Shop/Controllers/OrdersController.cs:8").And.Contain("runtime enforcement is not verified").And.Contain("Snapshot diff");
        cut.Find("[data-testid=aic-before-after]").TextContent.Should().Contain("[Authorize] on the type").And.Contain("no authorization metadata");
        cut.Find("[data-testid=aic-changes]").TextContent.Should().Contain("Files");
        cut.Find("[data-testid=aic-rule][data-rule=AIC-TEST-006]").GetAttribute("data-state").Should().Be("Unsupported");
        cut.Find("[data-testid=aic-limitations]").TextContent.Should().Contain("TypeScript");
        cut.Markup.Should().NotMatchRegex(@"\d+\s*%", "there is no score");
    }

    [Fact]
    public void Category_filter_and_export_work_on_the_shown_result()
    {
        Sources(Snap(Current, "Shop", 0, true));
        _api.Setup(x => x.RunAsync(It.IsAny<AiCodeReviewRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync((Result(), (string?)null));
        var cut = Render<AiGeneratedCodeReview>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=aic-run]"));
        cut.Find("[data-testid=aic-run]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=aic-result]"));

        cut.Find("[data-testid=aic-category][data-category=DeadCode]").Click();
        cut.Find("[data-testid=aic-category][data-category=DeadCode]").GetAttribute("aria-pressed").Should().Be("true");
        cut.Find("[data-testid=aic-no-findings]").TextContent.Should().Contain("does not mean the code is correct");
        cut.Find("[data-testid=aic-clear-category]").Click();
        cut.FindAll("[data-testid=aic-finding]").Should().HaveCount(1);

        cut.Find("[data-testid=aic-export]").Click();
        var call = JSInterop.Invocations.Single(i => i.Identifier == "downloadHtmlFile");
        var html = (string)call.Arguments[1]!;
        html.Should().Contain("Contoso Shop").And.Contain(Current.ToString()).And.Contain("AIC-SEC-001").And.Contain("Rule execution").And.Contain("Unsupported")
            .And.Contain("does not detect whether code was written by AI");
    }

    [Fact]
    public void Run_errors_are_shown_without_inventing_a_result()
    {
        Sources(Snap(Current, "Shop", 0, true));
        _api.Setup(x => x.RunAsync(It.IsAny<AiCodeReviewRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(((AiCodeReviewResult?)null, "The selected current snapshot is unavailable; nothing is substituted."));
        var cut = Render<AiGeneratedCodeReview>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=aic-run]"));
        cut.Find("[data-testid=aic-run]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=aic-error]").TextContent.Should().Contain("nothing is substituted"));
        cut.FindAll("[data-testid=aic-result]").Should().BeEmpty();
    }

    [Fact]
    public void Presentation_run_gate_and_workspace_summary_follow_the_evidence()
    {
        AiCodeReviewPresentation.RunBlockedReason(null, null, AiReviewScope.EntireSource, false).Should().Contain("Choose");
        AiCodeReviewPresentation.RunBlockedReason(Current, null, AiReviewScope.ChangedFiles, false).Should().Contain("baseline");
        AiCodeReviewPresentation.RunBlockedReason(Current, Current, AiReviewScope.EntireSource, false).Should().Contain("different");
        AiCodeReviewPresentation.RunBlockedReason(Current, Older, AiReviewScope.ChangedFiles, false).Should().BeNull();

        var empty = AiCodeReviewPresentation.WorkspaceEvidence(_workspace, [], null);
        empty.SpecificationAvailable.Should().BeFalse();
        empty.TestExecution.Should().BeNull("no imported results means not executed, never zero failures");
    }
}
