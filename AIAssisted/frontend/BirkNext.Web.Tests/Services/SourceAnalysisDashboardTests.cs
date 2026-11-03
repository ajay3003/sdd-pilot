using BirkNext.Integrations;
using BirkNext.SourceDomains;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.ProjectEvidence;
using BirkNext.Web.Services.ProjectImport;
using BirkNext.Web.Tests.Components;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Source Analysis Overview: one status taxonomy (no generic Partial/Complete), and detected / resolved / limited by source / needs review kept
/// apart — an unclassified entry or a heuristic parse is never counted as a decision, and the same item is never counted twice.
/// </summary>
public sealed class SourceAnalysisDashboardTests
{
    private static IqrSourceSnapshot Snapshot(SourceEvidenceDomainsSnapshot evidence) => new()
    {
        Id = evidence.SourceSnapshotId, IntegrationId = "source-analysis", Archive = new SourceArchive("shop.zip", new string('a', 64), 42), EvidenceDomains = evidence,
    };

    [Fact]
    public void Every_state_has_a_label_meaning_and_tone_and_none_is_a_generic_partial_or_complete()
    {
        foreach (var state in Enum.GetValues<SourceEvidenceState>())
        {
            SourceEvidenceStates.Label(state).Should().NotBeNullOrWhiteSpace().And.NotBe("Partial").And.NotBe("Complete");
            SourceEvidenceStates.Meaning(state).Should().NotBeNullOrWhiteSpace();
            SourceEvidenceStates.Tone(state).Should().BeOneOf("established", "info", "limited", "conflict", "neutral");
        }
        SourceEvidenceStates.Tone(SourceEvidenceState.ClassificationIncomplete).Should().NotBe("conflict", "a classification gap is never shown as a defect");
        SourceEvidenceStates.Tone(SourceEvidenceState.Heuristic).Should().Be("info", "heuristic is a parse approach");
    }

    [Fact]
    public void Unclassified_configuration_is_a_limitation_and_only_conflicts_are_review()
    {
        var evidence = SourceEvidenceDomainsUiTests.Evidence();
        var baseUnknown = SourceAnalysisReview.Unknown(evidence.Configuration);
        var unknown = Enumerable.Range(0, 3).Select(i => new BirkNext.SourceDomains.ConfigurationEntry { Id = $"u{i}", Key = $"Custom:Key{i}", Category = ConfigurationCategory.Unknown, Technology = "ASP.NET Core appsettings" });
        evidence = evidence with { Configuration = evidence.Configuration with { Entries = [.. evidence.Configuration.Entries, .. unknown] } };

        var card = SourceAnalysisDashboard.Configuration(Snapshot(evidence));

        card.State.Should().Be(SourceEvidenceState.ClassificationIncomplete);
        card.Metrics.Single(m => m.Key == "unclassified").Value.Should().Be(baseUnknown + 3);
        card.Limitations.Sum(l => l.Count).Should().Be(baseUnknown + 3, "every unclassified entry is in exactly one cause group");
        card.ReviewCount.Should().Be(evidence.Configuration.Conflicts.Count, "unclassified entries are not decisions");
        var total = evidence.Configuration.Entries.Count; // already includes the 3 unclassified entries
        card.Resolution.Should().Be(new SourceResolution("entries", total, total - baseUnknown - 3, baseUnknown + 3, evidence.Configuration.Conflicts.Count));
        card.StateNote.Should().Contain("not runtime verified");
    }

    [Fact]
    public void Heuristic_contracts_are_an_approach_and_partial_results_are_counted_once_as_confirmations()
    {
        var evidence = SourceEvidenceDomainsUiTests.Evidence();
        var card = SourceAnalysisDashboard.Contracts(Snapshot(evidence));

        card.State.Should().Be(SourceEvidenceState.Heuristic);
        card.Metrics.Single(m => m.Key == "heuristic").Value.Should().Be(SourceAnalysisReview.Heuristic(evidence.Contracts));
        card.ReviewCount.Should().Be(card.Metrics.Single(m => m.Key == "confirm").Value);
        card.Limitations.Should().BeEmpty("no contract failed to parse; partial results are the confirmations, never also limitations");
    }

    [Fact]
    public void Infrastructure_absent_here_points_to_associated_packages_and_never_merges_or_reimports()
    {
        var evidence = SourceEvidenceDomainsUiTests.Evidence(infrastructure: false);
        var here = Snapshot(evidence);
        static PackageAssociation Package(string name, Guid snapshot, EvidenceAssociationState state, params ProjectEvidenceCapability[] caps) => new(
            new ProjectEvidencePackage($"import-{name}", name, $"{name}.zip", "sha", DateTimeOffset.UtcNow, snapshot, [.. caps.Select(c => new PackageCapability(c, PackageCapabilityStatus.Ready, ""))],
                new Dictionary<WorkspaceArtifactType, int>(), 0, [], [], [], null),
            state, PackageEvidenceRole.Infrastructure, "");
        var infra = Guid.NewGuid();
        var associations = new List<PackageAssociation>
        {
            Package("Terraform-iac-m2lb", infra, EvidenceAssociationState.Associated, ProjectEvidenceCapability.Infrastructure),
            Package("Other-app-infra", Guid.NewGuid(), EvidenceAssociationState.NotAssociated, ProjectEvidenceCapability.Infrastructure),
            Package("M2LB", here.Id, EvidenceAssociationState.Associated, ProjectEvidenceCapability.ApplicationSource),
        };

        var related = SourceAnalysisDashboard.InfrastructureElsewhere(here, associations);
        var card = SourceAnalysisDashboard.Infrastructure(here, related);

        related.Should().ContainSingle().Which.Should().Be(new SourceRelatedPackage("Terraform-iac-m2lb", infra, $"source-analysis?snapshot={infra}&area=Infrastructure"));
        card.State.Should().Be(SourceEvidenceState.NotDetectedInSnapshot);
        card.StateNote.Should().Be("Not detected in this snapshot · infrastructure evidence available in 1 associated package");
        card.Metrics.Should().BeEmpty("not detected is not zero resources");
        (card.StateNote + card.ActionLabel).Should().NotContainAny("import", "Import");
    }

    [Fact]
    public void Review_lists_only_decisions_and_coverage_is_four_dimensions_with_runtime_never_verified()
    {
        var snapshot = Snapshot(SourceEvidenceDomainsUiTests.Evidence());
        var areas = SourceAnalysisDashboard.Areas(snapshot);

        SourceAnalysisDashboard.Review(areas).Should().OnlyContain(r => areas.Single(a => a.Key == r.Key).ReviewCount == r.Count && r.Count > 0);
        SourceAnalysisDashboard.Review(areas).Select(r => r.Text).Should().NotContain(t => t.Contains("classification") || t.Contains("unresolved"));
        areas.Select(a => a.Key).Should().Equal("architecture", "data", "observability", "configuration", "cicd", "contracts", "dependencies", "tests", "infrastructure");
        areas.Where(a => a.Key is "architecture" or "observability" or "dependencies" or "tests").Should().OnlyContain(a => a.State == SourceEvidenceState.NotAssessed && a.Metrics.Count == 0,
            "an area the snapshot predates has no counts");
        SourceAnalysisDashboard.Coverage(snapshot).Select(d => (d.Key, d.State)).Last().Should().Be(("runtime", SourceEvidenceState.NotVerifiedAtRuntime));
        SourceAnalysisDashboard.Target("cicd").Should().Be((SourceAnalysisAreas.Quality, "CI/CD"));
    }

    [Fact]
    public void An_unsupported_observability_snapshot_is_not_assessed_never_detected_with_counts()
    {
        var snapshot = Snapshot(SourceEvidenceDomainsUiTests.Evidence()) with
        {
            Observability = new BirkNext.SourceObservability.SourceObservabilitySnapshot { Status = BirkNext.SourceArchitecture.ArchitectureStatus.Unsupported },
        };

        var card = SourceAnalysisDashboard.Observability(snapshot);

        card.State.Should().Be(SourceEvidenceState.NotAssessed);
        card.Metrics.Should().BeEmpty("not assessed is not zero");
        card.StateNote.Should().Contain("No supported project type");
    }
}
