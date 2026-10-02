using BirkNext.TestEvidence;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using BirkNext.Web.Tests.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using S = BirkNext.Web.Tests.Services.SddTestEvidenceServiceTests;

namespace BirkNext.Web.Tests.Pages;

/// <summary>Import panel, per-requirement test dimensions, Implementation Review / Requirements Traceability / Quality Review integration.</summary>
public sealed class TestEvidenceUiTests : BunitContext
{
    private readonly WorkspaceArtifactRepository _repository;
    private readonly Mock<ITestEvidenceApiService> _api = new();
    private readonly Mock<IWorkspaceAutoSaveService> _autosave = new();
    private TestResultUploadContext? _lastContext;

    public TestEvidenceUiTests()
    {
        var (_, graph, repository) = S.Workspace();
        _repository = repository;
        var context = new Mock<IReviewContextProvider>();
        context.Setup(x => x.GetCurrent()).Returns(() => GraphContext(graph));
        Services.AddSingleton(context.Object);
        Services.AddSingleton<IWorkspaceSessionService>(_repository);
        _autosave.Setup(x => x.SaveNowAsync()).ReturnsAsync(true);
        Services.AddSingleton(_autosave.Object);
        Services.AddSingleton(new Mock<ISddEvidenceApiService>().Object);
        Services.AddSingleton(_api.Object);
        Services.AddScoped<SddEvidenceGraphService>();
        Services.AddScoped<SddTestEvidenceNotifier>();
        _api.Setup(x => x.PreviewAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<TestResultUploadContext>(), It.IsAny<CancellationToken>()))
            .Callback<string, Stream, TestResultUploadContext, CancellationToken>((_, _, c, _) => _lastContext = c)
            .ReturnsAsync((S.FirstRun(bound: false), (string?)null));
        _api.Setup(x => x.SourceTestsAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((S.Inventory(S.SnapshotA, "sha-a", S.Discount, S.Rounding, S.Square, S.Plain), (string?)null));
    }

    // The graph service reads requirements from the review context; reuse the fixture's specification. One open clarification without an id
    // (as in real Spec-Kit "- Q: … → A: …" sessions) guards the Implementation Review clarification inputs.
    private static ReviewContext GraphContext(SddEvidenceGraphService _) => new()
    {
        Specification = new SpecificationSemanticModel
        {
            Clarifications = [new SemanticClarification { Question = "Should both events be added?", Answer = "Yes" }],
            Requirements = new[] { "FR-023", "FR-026", "FR-031", "JIRA-123" }.Select(id => new SemanticRequirement
            {
                Id = id, Text = $"Requirement {id}",
                LinkedAcceptanceScenarios = id == "JIRA-123" ? [new SemanticAcceptanceScenario { Id = "AC-007", Title = "Squares" }] : [],
            }).ToList(),
        },
    };

    private IRenderedComponent<SddTestEvidencePanel> OpenPreview(bool bind = false)
    {
        var cut = Render<SddTestEvidencePanel>();
        cut.Find("[data-testid=te-import-open]").Click();
        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("<TestRun />", "Unit-test-results.trx"));
        cut.Find("[data-testid=te-snapshot]").Change(S.SnapshotA.ToString());
        if (bind) cut.Find("[data-testid=te-bind]").Change(true);
        cut.Find("[data-testid=te-build]").Change("20260930.1");
        cut.Find("[data-testid=te-preview-run]").Click();
        cut.WaitForElement("[data-testid=te-preview]");
        return cut;
    }

    [Fact]
    public void ImportDialog_IsLabelledAndKeyboardOperable()
    {
        var cut = Render<SddTestEvidencePanel>();
        var open = cut.Find("[data-testid=te-import-open]");
        open.GetAttribute("aria-expanded").Should().Be("false");
        open.Click();
        cut.Find("[data-testid=te-import-open]").GetAttribute("aria-expanded").Should().Be("true");
        cut.Find("label[for=te-file]").TextContent.Should().Contain("Result file (.trx)");
        cut.Find("#te-file").GetAttribute("accept").Should().Be(".trx");
        cut.Find("label[for=te-snapshot]").Should().NotBeNull();
        cut.Find("#te-bind").GetAttribute("aria-describedby").Should().Be("te-bind-help");
        cut.Find("#te-bind").HasAttribute("disabled").Should().BeTrue("binding needs a selected snapshot");
        cut.Find("[data-testid=te-import]").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });
        cut.FindAll("[data-testid=te-import]").Should().BeEmpty();
    }

    [Fact]
    public void Preview_ShowsProviderRunCorrelationAndBinding_BeforeAnythingIsRecorded()
    {
        var cut = OpenPreview();
        _lastContext.Should().Be(new TestResultUploadContext("dev", S.SnapshotA, false, "20260930.1", "", ""));
        cut.Find("[data-testid=te-preview-counts]").TextContent.Should().Be("7 total · 5 passed · 1 failed · 1 not executed/skipped");
        cut.Find("[data-testid=te-preview-matched]").TextContent.Should().Contain("6 matched · 1 without a source match · 0 ambiguous");
        cut.Find("[data-testid=te-preview-links]").TextContent.Should().StartWith("5 result(s)");
        cut.Find("[data-testid=te-preview-binding]").TextContent.Should().Contain("version not established");
        _repository.SddLifecycle.TestExecutions.Should().BeEmpty("preview never records");
    }

    [Fact]
    public void Import_ShowsTheSummary_RunHistoryAndPersists()
    {
        var cut = OpenPreview();
        cut.Find("[data-testid=te-import-confirm]").Click();

        cut.Find("[data-testid=te-summary-found]").TextContent.Should().Be("7");
        cut.Find("[data-testid=te-summary-unresolved]").TextContent.Should().Be("1 (+0 ambiguous)");
        cut.Find("[data-testid=te-summary-req]").TextContent.Should().Be("5");
        cut.Find("[data-testid=te-summary-dupes]").TextContent.Should().Be("0");
        _repository.SddLifecycle.TestExecutions.Should().HaveCount(7);
        _autosave.Verify(x => x.SaveNowAsync(), Times.AtLeastOnce);

        var run = cut.Find("[data-testid=te-run-row]");
        run.TextContent.Should().Contain("run-1.trx").And.Contain("20260930.1").And.Contain("Unknown (not bound)");
        cut.FindAll("[data-testid=te-result-row]").Should().HaveCount(7, "the imported run opens with its results");
        cut.Find("[data-testid=te-filter]").Change("unmatched");
        cut.FindAll("[data-testid=te-result-row]").Should().ContainSingle().Which.GetAttribute("data-correlation").Should().Be("Unresolved");
    }

    [Fact]
    public void AlreadyImportedArtifact_CannotBeImportedAgain()
    {
        SddTestEvidenceService.Import(_repository.SddLifecycle, S.FirstRun(bound: false), ["FR-023"], []);
        var cut = OpenPreview();
        cut.Find("[data-testid=te-preview-duplicate]").TextContent.Should().Contain("Already imported");
        cut.Find("[data-testid=te-import-confirm]").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void InvalidArtifact_IsShownAsAFileProblem_NotATestFailure()
    {
        _api.Setup(x => x.PreviewAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<TestResultUploadContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new TestResultArtifactPreview { Status = TestResultImportStatus.InvalidArtifact, Error = "Not well-formed XML." }, (string?)null));
        var cut = OpenPreview();
        cut.Find("[data-testid=te-preview-error]").TextContent.Should().Contain("Invalid file").And.Contain("not a test failure");
        cut.FindAll("[data-testid=te-import-confirm]").Should().BeEmpty();
    }

    [Fact]
    public void TestDetail_ShowsDefinitionReferencesAndExecutionHistory()
    {
        SddTestEvidenceService.RecordDefinitions(_repository.SddLifecycle, S.Inventory(S.SnapshotA, "sha-a", S.Square), ["JIRA-123"], ["AC-007"]);
        SddTestEvidenceService.Import(_repository.SddLifecycle, S.FirstRun(), ["JIRA-123"], ["AC-007"]);
        var cut = Render<SddTestEvidenceDetail>(p => p.Add(x => x.TestKey, S.Square.TestDefinitionId));
        cut.Find("[data-testid=te-detail-kind]").TextContent.Should().Contain("Unit");
        cut.Find("[data-testid=te-detail-refs]").TextContent.Should().Contain("JIRA-123").And.Contain("AC-007").And.Contain("acceptance criterion");
        cut.FindAll("[data-testid=te-history-row]").Should().HaveCount(3);
        cut.Find("[data-testid=te-detail-current]").TextContent.Should().StartWith("1 of 3 failed");
    }

    [Fact]
    public void RecordSourceDefinitions_AddsSourceDiscoveredDimension()
    {
        var cut = Render<SddTestEvidencePanel>();
        cut.Find("[data-testid=te-record-definitions]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=te-message]").TextContent.Should().Contain("Recorded 4 source test definition(s)").And.Contain("Discovery is not execution"));
        _repository.SddLifecycle.TestDefinitions.Should().HaveCount(4);
    }

    [Fact]
    public void SourceTests_LoadsSnapshotsAndRecordsTheSelectedSourceWithItsDefinitions()
    {
        var snapshot = new BirkNext.Integrations.IqrSourceSnapshot
        {
            Id = S.SnapshotB, Archive = new BirkNext.Integrations.SourceArchive("contoso.zip", "sha-b", 10), AnalyzedAt = DateTimeOffset.Parse("2026-10-01T08:00:00Z"),
            Status = BirkNext.Integrations.SourceAnalysisStatus.Ready, TestInventory = S.Inventory(S.SnapshotB, "sha-b", S.Discount, S.Square),
        };
        var sources = new Mock<ISddEvidenceApiService>();
        sources.Setup(x => x.SourceSnapshotsAsync("pilot", It.IsAny<CancellationToken>())).ReturnsAsync([snapshot]);
        Services.AddSingleton(sources.Object);
        var cut = Render<SddTestEvidencePanel>();
        cut.Find("[data-testid=te-source-env]").GetAttribute("value").Should().Be("dev", "prefilled from the current snapshot");
        cut.Find("[data-testid=te-source-env]").Change("pilot");
        cut.Find("[data-testid=te-source-load]").Click();
        cut.WaitForElement("[data-testid=te-source-pick]").TextContent.Should().Contain("2 tests");
        cut.Find("[data-testid=te-record-definitions]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=te-message]").TextContent.Should().Contain("Recorded 2 source test definition(s)"));
        _repository.SddLifecycle.SourceSnapshots.Single(s => s.Currentness == "Current").SnapshotId.Should().Be(S.SnapshotB.ToString());
        _repository.SddLifecycle.TestDefinitions.Should().HaveCount(2).And.OnlyContain(d => d.SourceSnapshotId == S.SnapshotB.ToString());
        cut.Find("[data-testid=te-source]").TextContent.Should().Contain("2 current source test definition(s)");
    }

    [Fact]
    public void RequirementTable_KeepsDesignedSourceExecutedAndResultSeparate()
    {
        SddTestEvidenceService.RecordDefinitions(_repository.SddLifecycle, S.Inventory(S.SnapshotA, "sha-a", S.Discount, S.Rounding, S.Square), ["FR-023", "FR-026", "FR-031", "JIRA-123"], ["AC-007"]);
        SddTestEvidenceService.Import(_repository.SddLifecycle, S.FirstRun(), ["FR-023", "FR-026", "FR-031", "JIRA-123"], ["AC-007"]);
        _repository.SddLifecycle.TestEvidence.Add(new SddTestEvidence { RequirementId = "JIRA-123", AcceptanceCriterionId = "AC-007", TestReference = "AC-007", State = "Designed" });
        var cut = Render<SddRequirementTestEvidence>();
        string Cell(string requirement, string column) => cut.Find($"[data-requirement='{requirement}'] [data-testid={column}]").TextContent;

        Cell("JIRA-123", "te-req-designed").Should().Be("1 designed");
        Cell("JIRA-123", "te-req-source").Should().Be("1 source test(s)");
        Cell("JIRA-123", "te-req-result").Should().Be("1 of 3 failed");
        Cell("JIRA-123", "te-req-currentness").Should().Be("Current");
        Cell("FR-031", "te-req-source").Should().Contain("None").And.Contain("1 candidate(s) to confirm");
        Cell("FR-031", "te-req-executed").Should().Be("No execution evidence");
        Cell("FR-026", "te-req-result").Should().Be("Passed");

        var expand = cut.Find("[data-requirement='JIRA-123'] [data-testid=te-req-expand]");
        expand.GetAttribute("aria-expanded").Should().Be("false");
        expand.Click();
        cut.Find("[data-requirement='JIRA-123'] [data-testid=te-req-expand]").GetAttribute("aria-expanded").Should().Be("true");
        cut.Markup.Should().Contain("Latest run bound to the currently selected source snapshot");
        cut.Markup.Should().Contain("implementation-review?test-execution=");
    }

    [Fact]
    public void ImplementationReview_HostsTheSharedTestEvidence()
    {
        var cut = Render<ImplementationReview>();
        cut.Find("[data-testid=te-panel]").Should().NotBeNull();
        cut.Find("[data-testid=te-req]").Should().NotBeNull();
        cut.Find("#test-result-import").Should().NotBeNull("the generic structured import remains");
        cut.FindAll("input[id^=resolution-Q-]").Should().HaveCount(1, "an open clarification without an id renders its resolution input instead of throwing");
    }

    [Fact]
    public void ImplementationReview_RequirementTableFollowsChangesMadeInThePanel()
    {
        var cut = Render<ImplementationReview>();
        cut.Find("[data-requirement='FR-023'] [data-testid=te-req-source]").TextContent.Should().Be("None");
        cut.Find("[data-testid=te-record-definitions]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-requirement='FR-023'] [data-testid=te-req-source]").TextContent.Should().Be("1 source test(s)"));
    }

    [Fact]
    public void Traceability_CountsUnreferencedResultsInsteadOfListingThemAsOrphans()
    {
        SddTestEvidenceService.Import(_repository.SddLifecycle, S.FirstRun(), ["FR-023", "FR-026", "FR-031", "JIRA-123"], ["AC-007"]);
        var cut = Render<Traceability>();
        cut.Find("[data-testid=trace-unreferenced-executions]").TextContent.Should().Contain("2 imported result(s)");
        cut.Find("[data-testid=te-req]").Should().NotBeNull();
        cut.Markup.Should().NotContain("Other.Test ·", "unreferenced provider results are not listed one by one");
    }

    [Fact]
    public void QualityReviewFinding_LinksToTheExactTestEvidence()
    {
        var panel = Render<SddTestEvidencePanel>();
        SddTestEvidenceService.Import(_repository.SddLifecycle, S.FirstRun(bound: true), ["FR-023", "FR-026", "FR-031", "JIRA-123"], ["AC-007"]);
        var failing = _repository.SddLifecycle.TestExecutions.First(e => e.Result == "Failed");
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"/implementation-review?test-execution={failing.Id}#test-evidence");
        var deepLinked = Render<SddTestEvidencePanel>();
        deepLinked.Find("[data-testid=te-detail]").TextContent.Should().Contain("Contoso.Tests.OrderTests.Square");
        deepLinked.Find("[data-testid=te-run-tests]").Should().NotBeNull();
        // Same page, new query string (requirement-row link): the already rendered panel follows it too.
        panel.WaitForAssertion(() => panel.Find("[data-testid=te-detail]").TextContent.Should().Contain("Contoso.Tests.OrderTests.Square"));
        var rounding = _repository.SddLifecycle.TestExecutions.First(e => e.TestDefinitionId == S.Rounding.TestDefinitionId);
        nav.NavigateTo($"/implementation-review?test-execution={rounding.Id}#test-evidence");
        panel.WaitForAssertion(() => panel.Find("[data-testid=te-detail]").TextContent.Should().Contain("Contoso.Tests.OrderTests.Rounding"));
    }
}
