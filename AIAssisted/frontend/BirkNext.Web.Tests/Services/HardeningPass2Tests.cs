using BirkNext.Integrations;
using BirkNext.Snapshots;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>Hardening Pass 2 (NEXT42): semantics that several modules share, pinned with their own words.</summary>
public sealed class HardeningPass2Tests
{
    [Theory]
    [InlineData(0, 14, "14 jobs without explicit stages")]
    [InlineData(0, 1, "1 job without explicit stages")]
    [InlineData(0, 0, "No explicit stages or jobs (steps only)")]
    [InlineData(3, 5, "3 stages · 5 jobs")]
    [InlineData(1, 1, "1 stage · 1 job")]
    public void A_pipeline_structure_always_has_units_and_never_calls_several_jobs_a_single_job(int stages, int jobs, string expected) =>
        PipelineReviewPresentation.Structure(stages, jobs).Should().Be(expected);

    [Fact]
    public void Snapshot_pickers_show_the_registry_outcome_not_the_code_analyzers_status()
    {
        var terraform = new IqrSourceSnapshotSummary
        {
            Archive = new("Terraform-iac-m2lb.zip", "aa8b1e87ffff", 174), AnalyzedAt = DateTimeOffset.UnixEpoch,
            Status = SourceAnalysisStatus.Failed, Outcome = SnapshotProcessingStatus.Partial,
        };
        SourceAnalysisOverview.OptionLabel(terraform).Should().Contain("· Partial ·").And.NotContain("No production project found",
            "an infrastructure-only package is not a failed analysis; the picker agrees with the Snapshots page");

        // An older backend without the outcome keeps the analyzer's own words.
        SourceAnalysisOverview.OptionLabel(terraform with { Outcome = null }).Should().Contain("No production project found");
        SourceAnalysisOverview.Outcome(SnapshotProcessingStatus.Completed).Should().Be("Analyzed");
    }
}
