using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Moq;

namespace BirkNext.Web.Tests.Services;

/// <summary>Quality Review pre-run semantics: shared SDD evidence aggregation and pack readiness derived from real pack inputs.</summary>
public sealed class QualityReviewPreRunTests
{
    private static readonly ReviewArtifactPresence All = new(true, true, true, true, true);

    private static QualityReviewPackDescriptor Pack(string id, string group = "Quality") => new(id, group, id, "description", true);

    private static (WorkspaceArtifactRepository Workspace, SddEvidenceGraphService Graph) Graph(int requirements)
    {
        var context = new ReviewContext();
        for (var i = 1; i <= requirements; i++)
            context.Specification.Requirements.Add(new SemanticRequirement { Id = $"FR-{i:000}", Text = $"Requirement {i}" });
        var provider = new Mock<IReviewContextProvider>();
        provider.Setup(x => x.GetCurrent()).Returns(context);
        return (new WorkspaceArtifactRepository(), new SddEvidenceGraphService(provider.Object));
    }

    // ── Shared SDD evidence aggregation ──────────────────────────────────────

    [Fact]
    public void Aggregation_TwentyOneRequirementsWithoutEvidence_SummarizeAsUniqueRequirementCounts_NotFortyTwoRows()
    {
        var (workspace, graph) = Graph(21);
        var rows = graph.Project(workspace).Rows;
        var observations = graph.QualityFindings(workspace);

        var summary = SharedSddEvidenceSummary.Build(rows, observations);

        observations.Should().HaveCount(42, "each requirement has one implementation and one execution observation");
        summary.ObservationCount.Should().Be(42);
        summary.RequirementsEvaluated.Should().Be(21);
        var implementation = summary.Categories.Single(c => c.Category == SddObservationCategory.ImplementationEvidence);
        var execution = summary.Categories.Single(c => c.Category == SddObservationCategory.TestEvidence);
        implementation.RequirementCount.Should().Be(21);
        execution.RequirementCount.Should().Be(21);
        implementation.Groups.Single().Title.Should().Be("Implementation evidence not assessed");
        execution.Groups.Single().Title.Should().Be("Execution evidence not assessed");
        implementation.Groups.Single().StatusLabel.Should().Be("Not assessed");
    }

    [Fact]
    public void Aggregation_RepeatedObservationsForOneRequirement_DoNotInflateTheRequirementCount()
    {
        SddQualityReviewFinding Stale(string req, string reference) => new("StaleTestEvidence", "NeedsReview", req, "May be stale.", reference);
        var summary = SharedSddEvidenceSummary.Build([], [Stale("FR-001", "a"), Stale("FR-001", "b"), Stale("FR-001", "c"), Stale("FR-002", "d")]);

        var group = summary.Categories.Single().Groups.Single();
        group.ObservationCount.Should().Be(4);
        group.RequirementCount.Should().Be(2);
        group.Requirements.Single(r => r.RequirementId == "FR-001").Observations.Should().HaveCount(3);
        summary.Categories.Single().RequirementCount.Should().Be(2);
    }

    [Fact]
    public void NotAssessed_IsNeutral_OnlyAnActualFailedResultUsesTheFailedTone()
    {
        var summary = SharedSddEvidenceSummary.Build([],
        [
            new("RequirementWithoutCurrentImplementationEvidence", "NotAssessed", "FR-001", "none", "FR-001"),
            new("UnresolvedQuestion", "Information", "FR-001", "open", "FR-001"),
            new("StaleImplementationEvidence", "NeedsReview", "FR-002", "stale", "x"),
            new("CurrentLinkedTestFailed", "Failed", "FR-003", "failed", "test-execution:1"),
        ]);

        var groups = summary.Categories.SelectMany(c => c.Groups).ToDictionary(g => g.Code);
        groups["RequirementWithoutCurrentImplementationEvidence"].Tone.Should().Be(SddObservationTone.Neutral);
        groups["RequirementWithoutCurrentImplementationEvidence"].StatusLabel.Should().Be("Not assessed");
        groups["UnresolvedQuestion"].Tone.Should().Be(SddObservationTone.Neutral);
        groups["StaleImplementationEvidence"].Tone.Should().Be(SddObservationTone.NeedsReview);
        groups["CurrentLinkedTestFailed"].Tone.Should().Be(SddObservationTone.Failed);
        groups.Values.Where(g => g.Tone != SddObservationTone.Failed).Should().OnlyContain(g => g.StatusLabel != "Failed");
    }

    [Fact]
    public void Labels_NeverUseTheInternalRuleIdAsTheTitle_AndPolicyStaysInformational()
    {
        var (workspace, graph) = Graph(3);
        var summary = SharedSddEvidenceSummary.Build(graph.Project(workspace).Rows, graph.QualityFindings(workspace));

        summary.Categories.SelectMany(c => c.Groups).Should().OnlyContain(g => g.Title != g.Code && !g.Title.Contains("Requirement" + "Without"));
        summary.PolicySignificance.Should().Be("Informational");
        summary.PolicySignificanceExplanation.Should().Contain("not Quality Review findings");
    }

    [Fact]
    public void Requirements_KeepTheirSpecificationOrder()
    {
        var (workspace, graph) = Graph(12);
        var summary = SharedSddEvidenceSummary.Build(graph.Project(workspace).Rows, graph.QualityFindings(workspace));

        summary.Categories.First().Groups.Single().Requirements.Select(r => r.RequirementId)
            .Should().Equal(Enumerable.Range(1, 12).Select(i => $"FR-{i:000}"));
    }

    [Fact]
    public void CurrentEvidence_IsNotCountedAsMissing_StaleEvidenceIsNotCurrent()
    {
        var (workspace, graph) = Graph(2);
        workspace.SddLifecycle.ImplementationEvidence.Add(new SddImplementationEvidence { RequirementId = "FR-001", Currentness = "Current" });
        workspace.SddLifecycle.ImplementationEvidence.Add(new SddImplementationEvidence { RequirementId = "FR-002", Currentness = "PotentiallyStale" });

        var summary = SharedSddEvidenceSummary.Build(graph.Project(workspace).Rows, graph.QualityFindings(workspace));

        var implementation = summary.Categories.Single(c => c.Category == SddObservationCategory.ImplementationEvidence);
        implementation.Groups.Single(g => g.Code == "RequirementWithoutCurrentImplementationEvidence").Requirements.Select(r => r.RequirementId).Should().Equal("FR-002");
        implementation.Groups.Single(g => g.Code == "StaleImplementationEvidence").Requirements.Select(r => r.RequirementId).Should().Equal("FR-002");
        implementation.RequirementCount.Should().Be(1);
    }

    // ── Pack readiness ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("qa-auditor", "Quality")]
    [InlineData("constitution-compliance", "Governance")]
    [InlineData("qa-readiness", "Readiness")]
    [InlineData("delivery-readiness", "Readiness")]
    [InlineData("data-model-quality", "Quality")]
    [InlineData("WCAG22", "Standards")]
    public void Ready_WhenEveryInputThePackReadsIsPresent_AndSaysNothingAboutPassing(string packId, string group)
    {
        var readiness = QualityReviewPackReadiness.Evaluate(Pack(packId, group), All);

        readiness.State.Should().Be(PackReadinessState.Ready);
        readiness.IsRunnable.Should().BeTrue();
        readiness.Summary.Should().NotContainAny("pass", "Pass", "compliant");
    }

    [Fact]
    public void Partial_WhenTheCoreInputIsPresentButOtherReadInputsAreMissing_StillRunnable()
    {
        var specOnly = new ReviewArtifactPresence(false, true, false, false, false);

        var qa = QualityReviewPackReadiness.Evaluate(Pack("qa-readiness", "Readiness"), specOnly);
        var auditor = QualityReviewPackReadiness.Evaluate(Pack("qa-auditor"), specOnly);

        qa.State.Should().Be(PackReadinessState.Partial);
        qa.IsRunnable.Should().BeTrue();
        qa.Limitations.Should().Contain("No Plan: its readiness category is reported Not assessed.");
        auditor.State.Should().Be(PackReadinessState.Partial);
        auditor.Summary.Should().Contain("Constitution, Plan or Tasks");
    }

    [Fact]
    public void Blocked_WhenTheCoreInputIsAbsent_IsNotRunnable_AndIsNotDescribedAsFailure()
    {
        var noConstitution = new ReviewArtifactPresence(false, true, true, true, false);

        var compliance = QualityReviewPackReadiness.Evaluate(Pack("constitution-compliance", "Governance"), noConstitution);
        var dataModel = QualityReviewPackReadiness.Evaluate(Pack("data-model-quality"), noConstitution);

        compliance.State.Should().Be(PackReadinessState.Blocked);
        compliance.IsRunnable.Should().BeFalse();
        compliance.Summary.Should().Be("Needs a Constitution artifact.");
        dataModel.State.Should().Be(PackReadinessState.Blocked);
        new[] { compliance.Summary, dataModel.Summary }.Should().OnlyContain(s => !s.Contains("fail", StringComparison.OrdinalIgnoreCase));
        QualityReviewPackReadiness.StateLabel(PackReadinessState.Blocked).Should().Be("Blocked");
    }

    [Fact]
    public void NotApplicable_IsNeutralAndCountedSeparately()
    {
        var summary = QualityReviewPackReadiness.Summarize(
        [
            new("a", "A", PackReadinessState.NotApplicable, "Does not apply.", [], []),
            new("b", "B", PackReadinessState.Ready, "ok", [], []),
        ]);

        QualityReviewPackReadiness.StateLabel(PackReadinessState.NotApplicable).Should().Be("Not applicable");
        summary.NotApplicable.Should().Be(1);
        summary.Blocked.Should().Be(0);
        summary.Runnable.Should().Be(1);
        new PackReadiness("a", "A", PackReadinessState.NotApplicable, "", [], []).IsRunnable.Should().BeFalse();
    }

    [Fact]
    public void MixedReadiness_SummaryMatchesTheSelectedPacks_AndLimitationsComeOnlyFromPartialSelectedPacks()
    {
        var presence = new ReviewArtifactPresence(false, true, true, false, true);
        var selected = new[]
        {
            QualityReviewPackReadiness.Evaluate(Pack("data-model-quality"), presence),               // Ready
            QualityReviewPackReadiness.Evaluate(Pack("WCAG22", "Standards"), presence),               // Partial
            QualityReviewPackReadiness.Evaluate(Pack("delivery-readiness", "Readiness"), presence),   // Partial
            QualityReviewPackReadiness.Evaluate(Pack("constitution-compliance", "Governance"), presence), // Blocked
        };
        var notSelected = QualityReviewPackReadiness.Evaluate(Pack("qa-readiness", "Readiness"), presence);

        var summary = QualityReviewPackReadiness.Summarize(selected);

        summary.Selected.Should().Be(4);
        summary.Ready.Should().Be(1);
        summary.Partial.Should().Be(2);
        summary.Blocked.Should().Be(1);
        summary.Runnable.Should().Be(3);
        summary.ExpectedLimitations.Should().HaveCount(2).And.OnlyContain(l => l.StartsWith("WCAG22:") || l.StartsWith("delivery-readiness:"));
        summary.ExpectedLimitations.Should().NotContain(l => l.StartsWith(notSelected.PackName));
    }

    [Fact]
    public void StandardsPacks_AreDocumentChecks_NoSourceOrRuntimeEvidenceIsAPrerequisite()
    {
        var oneDocument = new ReviewArtifactPresence(false, true, false, false, false);

        var wcag = QualityReviewPackReadiness.Evaluate(Pack("WCAG22", "Standards"), oneDocument);
        var owasp = QualityReviewPackReadiness.Evaluate(Pack("OWASP", "Standards"), All);

        wcag.State.Should().Be(PackReadinessState.Partial, "the keyword checks still run on the Specification text");
        wcag.IsRunnable.Should().BeTrue();
        owasp.State.Should().Be(PackReadinessState.Ready, "no source snapshot or runtime evidence is an input to a Standards pack");
        owasp.Summary.Should().Contain("not a compliance verdict");
    }

    [Fact]
    public void ManualImport_ReadinessDependsOnlyOnArtifactPresence_NotOnASampleProject()
    {
        // Presence is built from whatever artifact content the workspace resolved: imported files work the same as Sample Project files.
        var imported = new ReviewArtifactPresence(false, false, true, true, false);

        QualityReviewPackReadiness.Evaluate(Pack("delivery-readiness", "Readiness"), imported).IsRunnable.Should().BeTrue();
        QualityReviewPackReadiness.Evaluate(Pack("qa-readiness", "Readiness"), imported).IsRunnable.Should().BeTrue();
        QualityReviewPackReadiness.Evaluate(Pack("data-model-quality"), imported).State.Should().Be(PackReadinessState.Blocked);
    }
}
