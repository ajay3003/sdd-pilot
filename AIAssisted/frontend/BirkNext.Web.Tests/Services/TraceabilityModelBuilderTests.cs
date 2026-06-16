using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

public sealed class TraceabilityModelBuilderTests
{
    [Fact]
    public void Clarification_NotIncludedInCoverage()
    {
        var requirement = Candidate("FR-001: The system MUST validate input.");
        var clarification = Candidate(
            "Should retry behavior be configurable?",
            ScenarioKind.NeedsClarification,
            ClassificationSignal.ClarificationSignal,
            "Clarifications");

        var model = TraceabilityModelBuilder.Build(null, [requirement, clarification], []);

        model.EligibleCount.Should().Be(1);
        model.ClarificationCount.Should().Be(1);
        model.Requirements.Single(r => r.CandidateId == clarification.CandidateId).Status
            .Should().Be(TraceCoverageStatus.NotEligible);
    }

    [Fact]
    public void Decision_NotMarkedMissingTest()
    {
        var decision = Candidate(
            "The service MUST use GraphQL for presentation queries.",
            contextHeading: "Decisions");

        var model = TraceabilityModelBuilder.Build(null, [decision], []);

        model.DecisionCount.Should().Be(1);
        model.EligibleCount.Should().Be(0);
        model.Requirements.Single().Status.Should().Be(TraceCoverageStatus.NotEligible);
    }

    [Fact]
    public void Assumption_NotCountedAsGap()
    {
        var assumption = Candidate(
            "Infrastructure sizing MUST validate the p95 response time target.",
            contextHeading: "Assumptions");

        var model = TraceabilityModelBuilder.Build(null, [assumption], []);

        model.AssumptionCount.Should().Be(1);
        model.GapCount.Should().Be(0);
        model.Requirements.Single().ArtifactType.Should().Be(TraceArtifactType.Assumption);
    }

    [Fact]
    public async Task Coverage_UsesRequirementsOnly()
    {
        var specMarkdown = await File.ReadAllTextAsync(FindPersonSpecPath());
        var extraction = await BuildExtractionService().ExtractAsync(specMarkdown);

        extraction.Status.Should().Be(PipelineStatus.Success);

        var requirement = extraction.Candidates.First(c =>
            c.Classification == ScenarioKind.Requirement &&
            c.ContextHeading == "Functional Requirements" &&
            c.Title.StartsWith("FR-001", StringComparison.OrdinalIgnoreCase));
        var test = extraction.Candidates.First(c => c.Classification == ScenarioKind.Test);

        var model = TraceabilityModelBuilder.Build(
            specMarkdown,
            extraction.Candidates,
            [new CandidateLinkEntry(requirement.CandidateId, test.CandidateId, CandidateLinkType.RequirementTest)]);

        model.EligibleCount.Should().Be(model.RequirementCount);
        model.Requirements.Where(r => !r.IsEligible).Should().AllSatisfy(r =>
            r.Status.Should().Be(TraceCoverageStatus.NotEligible));
        model.CoveragePercent.Should().Be(model.CoveredCount * 100 / model.RequirementCount);
        model.Requirements.Where(r => r.Status == TraceCoverageStatus.MissingTests)
            .Should().OnlyContain(r => r.ArtifactType == TraceArtifactType.Requirement);
    }

    [Fact]
    public void QAPair_NotTreatedAsRequirement()
    {
        var qaPair = Candidate(
            "Q: Which API MUST serve presentation reads? A: GraphQL handles presentation reads.",
            contextHeading: "Clarifications");

        var model = TraceabilityModelBuilder.Build(null, [qaPair], []);

        model.DecisionCount.Should().Be(1);
        model.EligibleCount.Should().Be(0);
        model.Requirements.Single().Status.Should().Be(TraceCoverageStatus.NotEligible);
    }

    [Fact]
    public async Task Traceability_DefaultCoverageDenominator_UsesExplicitFrsOnly()
    {
        var specMarkdown = await File.ReadAllTextAsync(FindPersonSpecPath());
        var extraction = await BuildExtractionService().ExtractAsync(specMarkdown);

        extraction.Status.Should().Be(PipelineStatus.Success);

        var model = TraceabilityModelBuilder.Build(specMarkdown, extraction.Candidates, []);

        model.EligibleCount.Should().Be(33);
        model.RequirementCount.Should().Be(33);
        model.Requirements.Where(r => r.IsEligible)
            .Select(r => r.FrId)
            .Should().BeEquivalentTo(ExpectedPersonSpecFrIds());
    }

    [Fact]
    public async Task Traceability_DefaultCoverageDenominator_Is33ExplicitFrs()
    {
        var model = await BuildPersonTraceabilityModelAsync();

        model.EligibleCount.Should().Be(33);
        model.RequirementCount.Should().Be(33);
        model.Requirements.Where(r => r.IsEligible).Should().OnlyContain(r => r.FrId != null);
    }

    [Fact]
    public async Task Traceability_DoesNotRenderFr002BulletsAsRows()
    {
        var model = await BuildPersonTraceabilityModelAsync();

        model.Requirements.Where(r => r.IsEligible && r.Title.StartsWith("Levels 0 and 1", StringComparison.OrdinalIgnoreCase))
            .Should().BeEmpty();
        model.Requirements.Where(r => r.IsEligible && r.Title.StartsWith("Kode 6 / Kode 7", StringComparison.OrdinalIgnoreCase))
            .Should().BeEmpty();
        model.Requirements.Single(r => r.FrId == "FR-002").FullContent.Should().Contain("Levels 0 and 1");
    }

    [Fact]
    public async Task Traceability_DoesNotRenderFr025EventListAsRows()
    {
        var model = await BuildPersonTraceabilityModelAsync();

        model.Requirements.Where(r => r.IsEligible && r.Title.Contains("person.person", StringComparison.OrdinalIgnoreCase))
            .Should().ContainSingle(r => r.FrId == "FR-025");
        model.Requirements.Where(r => r.IsEligible && r.FrId != "FR-025")
            .Should().NotContain(r => r.Title.Contains("PersonOpprettet", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Traceability_DoesNotRenderFr029OperationsAsRows()
    {
        var model = await BuildPersonTraceabilityModelAsync();

        model.Requirements.Where(r => r.IsEligible)
            .Should().NotContain(r => r.Title.TrimStart().StartsWith("- `Person:", StringComparison.OrdinalIgnoreCase));
        Eligible(model, "FR-029").FullContent.Should().Contain("Person:SeRevisjonslogg");
    }

    [Fact]
    public async Task Traceability_MatrixRowsAreExplicitFrsOnly()
    {
        var model = await BuildPersonTraceabilityModelAsync();

        model.Requirements.Where(r => r.IsEligible).Should().HaveCount(33);
        model.Requirements.Where(r => r.IsEligible).Select(r => r.FrId)
            .Should().BeEquivalentTo(ExpectedPersonSpecFrIds());
    }

    [Fact]
    public async Task Traceability_UserStoryColumnPopulatesForKnownFrs()
    {
        var model = await BuildPersonTraceabilityModelAsync();

        Eligible(model, "FR-001").UserStoryId.Should().Be("US1");
        Eligible(model, "FR-008").UserStoryId.Should().Be("US2");
        Eligible(model, "FR-013").UserStoryId.Should().Be("US3");
        Eligible(model, "FR-017").UserStoryId.Should().Be("US4");
        Eligible(model, "FR-020").UserStoryId.Should().Be("US5");
        Eligible(model, "FR-025").UserStoryId.Should().Be("US6");
        Eligible(model, "FR-029").UserStoryId.Should().Be("Cross-cutting / Platform");
    }

    [Fact]
    public async Task Traceability_SuccessCriteriaLinksPopulateForKnownFrs()
    {
        var model = await BuildPersonTraceabilityModelAsync();

        Eligible(model, "FR-001").LinkedScIds.Should().Contain("SC-001");
        Eligible(model, "FR-029").LinkedScIds.Should().Contain("SC-007");
        Eligible(model, "FR-026").LinkedScIds.Should().Contain("SC-006");
    }

    private static ExtractionCandidate Candidate(
        string title,
        ScenarioKind kind = ScenarioKind.Requirement,
        ClassificationSignal signal = ClassificationSignal.Rfc2119Uppercase,
        string? contextHeading = null) =>
        new()
        {
            Title = title,
            Classification = kind,
            ClassificationSignal = signal,
            ContextHeading = contextHeading,
            SourceBlockType = BlockType.UnorderedListItem,
        };

    private static ScenarioExtractionService BuildExtractionService() =>
        new(new ExtractionConfiguration
        {
            MaxInputLengthChars = 50_000,
            MinCandidateLengthChars = 3,
            MaxLineLengthForPatternMatching = 2_000,
        });

    private static async Task<TraceabilityModel> BuildPersonTraceabilityModelAsync()
    {
        var specMarkdown = await File.ReadAllTextAsync(FindPersonSpecPath());
        var extraction = await BuildExtractionService().ExtractAsync(specMarkdown);
        extraction.Status.Should().Be(PipelineStatus.Success);
        return TraceabilityModelBuilder.Build(specMarkdown, extraction.Candidates, []);
    }

    private static TracedRequirement Eligible(TraceabilityModel model, string frId) =>
        model.Requirements.Single(r => r.IsEligible && r.FrId == frId);

    private static string FindPersonSpecPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "examples", "personSpec.md");
            if (File.Exists(path))
                return path;
            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate examples/personSpec.md from test output directory.");
    }

    private static string[] ExpectedPersonSpecFrIds() =>
    [
        "FR-001", "FR-002", "FR-003", "FR-004", "FR-005", "FR-006", "FR-007", "FR-008", "FR-009",
        "FR-010", "FR-011", "FR-012", "FR-013", "FR-014", "FR-015", "FR-016", "FR-017", "FR-018",
        "FR-019", "FR-020", "FR-021", "FR-022", "FR-023", "FR-024", "FR-025", "FR-026", "FR-027",
        "FR-028", "FR-029", "FR-030", "FR-031", "FR-032", "FR-033",
    ];

    // =========================================================================
    // T016 — Rejected artifacts are excluded from Traceability calculations
    // =========================================================================

    [Fact]
    public void RejectedArtifacts_AreExcludedFromTraceability()
    {
        var accepted = Candidate("FR-001: The system MUST allow login");
        var rejected = Candidate("FR-002: The system MUST validate email");
        rejected.ReviewStatus = CandidateReviewStatus.Rejected;
        var test = Candidate("Verify login succeeds", ScenarioKind.Test);

        var model = TraceabilityModelBuilder.Build(null, [accepted, rejected, test], []);

        model.Requirements.Should().NotContain(r => r.CandidateId == rejected.CandidateId,
            "Rejected candidates must not appear in Traceability requirements");
        model.Requirements.Should().ContainSingle(r => r.CandidateId == accepted.CandidateId,
            "Non-rejected requirements must still appear");
        model.TotalCandidates.Should().Be(2,
            "TotalCandidates count must reflect only non-rejected candidates");
    }

    // =========================================================================
    // T017 — NeedsReview artifacts are flagged with NeedsReviewWarning
    // =========================================================================

    [Fact]
    public void NeedsReviewArtifacts_AreFlaggedOrHandledConsistently()
    {
        var needsReview = Candidate("FR-003: The system MUST cache results");
        needsReview.ReviewStatus = CandidateReviewStatus.NeedsReview;

        var model = TraceabilityModelBuilder.Build(null, [needsReview], []);

        var traced = model.Requirements.Single(r => r.CandidateId == needsReview.CandidateId);
        traced.NeedsReviewWarning.Should().BeTrue(
            "NeedsReview candidates must have NeedsReviewWarning=true in the Traceability model");
    }

    // =========================================================================
    // T018 — Traceability works with all AutoAccepted candidates (no manual review)
    // =========================================================================

    [Fact]
    public void Traceability_WorksWithAutoAcceptedCandidates()
    {
        var req = Candidate("FR-001: The system MUST process orders");
        var test = Candidate("Verify order processing", ScenarioKind.Test);
        // Both default to AutoAccepted (new default)

        var model = TraceabilityModelBuilder.Build(null, [req, test], []);

        model.Requirements.Should().ContainSingle(r => r.CandidateId == req.CandidateId,
            "AutoAccepted requirements must appear in Traceability");
        model.EligibleCount.Should().Be(1);
    }
}
