using BirkNext.Dependencies;
using BirkNext.Integrations;
using BirkNext.Web.Components.SourceDomains;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Shared dependency evidence consumed outside Dependency Review: Source Analysis → Dependencies shows the snapshot's stored declarations, the
/// internal packages it produces or consumes across the stored snapshots (never merged) and version differences — never as incompatibility.
/// </summary>
public sealed class DependencyEvidenceUiTests : BunitContext
{
    private static readonly Guid CommonId = Guid.Parse("c0000000-0000-0000-0000-000000000001");
    private static readonly Guid AppId = Guid.Parse("a0000000-0000-0000-0000-000000000002");

    private static IqrSourceSnapshot Common() => new()
    {
        Id = CommonId, Archive = new SourceArchive("M2LB.Common.zip", new string('c', 64), 1), AnalyzedAt = DateTimeOffset.Parse("2026-10-22T06:00:00Z"),
        DependencyEvidence = new SourceDependencyEvidence
        {
            PublishedPackages = [new("M2LB.Common", "src/M2LB.Common/M2LB.Common.csproj", "2.4.1")],
            Dependencies = [new DeclaredDependency { Manager = "nuget", PackageName = "Azure.Identity", CurrentValue = "1.12.0", OwnerFile = "Directory.Packages.props", ReferencedBy = ["src/M2LB.Common/M2LB.Common.csproj"] }],
            Managers = [new("nuget", ["Directory.Packages.props", "src/M2LB.Common/M2LB.Common.csproj"])],
        },
    };

    private static IqrSourceSnapshot App() => new()
    {
        Id = AppId, Archive = new SourceArchive("M2LB.zip", new string('a', 64), 1), AnalyzedAt = DateTimeOffset.Parse("2026-10-22T06:10:00Z"),
        DependencyEvidence = new SourceDependencyEvidence
        {
            Dependencies =
            [
                .. Enumerable.Range(1, 15).Select(i => new DeclaredDependency { Manager = "nuget", PackageName = "M2LB.Common", CurrentValue = "2.4.1", OwnerFile = $"src/P{i}/P{i}.csproj", ReferencedBy = [$"src/P{i}/P{i}.csproj"] }),
                .. Enumerable.Range(16, 6).Select(i => new DeclaredDependency { Manager = "nuget", PackageName = "M2LB.Common", CurrentValue = "2.3.0", OwnerFile = $"src/P{i}/P{i}.csproj", ReferencedBy = [$"src/P{i}/P{i}.csproj"] }),
                new DeclaredDependency { Manager = "npm", PackageName = "M2LB.Common", CurrentValue = "9.9.9", OwnerFile = "web/package.json", ReferencedBy = ["web/package.json"] },
            ],
            Managers = [new("nuget", ["src/P1/P1.csproj"]), new("npm", ["web/package.json"])],
            RenovateFiles = [new("renovate.json", new string('b', 64), "{}")],
        },
    };

    private static IReadOnlyList<DependencySourceEvidence> Sources() =>
        [.. new[] { Common(), App() }.Select(s => new DependencySourceEvidence(s.Id, s.Archive.FileName, s.AnalyzedAt, s.DependencyEvidence!))];

    [Fact]
    public void Internal_package_mapping_counts_aligned_and_version_different_consumers_per_ecosystem()
    {
        var mapping = DependencyEvidenceQueries.InternalPackageMappings(Sources()).Should().ContainSingle().Subject;
        mapping.Package.Should().Be(new DependencyCoordinate("nuget", "M2LB.Common"));
        mapping.ProducerVersion.Should().Be("2.4.1");
        mapping.Consumers.Should().HaveCount(21, "the npm package with the same name is a different coordinate");
        mapping.Aligned.Should().Be(15);
        mapping.VersionDifferences.Should().Be(6);
        mapping.ConsumerVersions.Should().Equal("2.3.0", "2.4.1");
        DependencyEvidenceQueries.Label(InternalConsumerState.MappedWithVersionDifference).Should().Be("Mapped with version difference");
        Enum.GetNames<InternalConsumerState>().Should().NotContain(n => n.Contains("Conflict") || n.Contains("Broken") || n.Contains("Incompatible"));
    }

    [Fact]
    public void Dependency_impact_is_source_impact_never_a_breakage_claim()
    {
        var impact = DependencyEvidenceQueries.FindDependencyImpact(Sources(), new DependencyCoordinate("nuget", "M2LB.Common"));
        impact.Should().HaveCount(21).And.OnlyContain(i => i.Confidence == "High-confidence source impact" && i.Reason.Contains("Compatibility is not assessed"));
        DependencyEvidenceQueries.FindDependencyImpact(Sources(), new DependencyCoordinate("npm", "M2LB.Common")).Should().ContainSingle();
    }

    [Fact]
    public void Traceability_answers_update_automation_with_configuration_and_never_execution()
    {
        var evidence = DependencyTraceability.For(Sources(), "Dependencies shall be updated automatically.");
        evidence.Should().ContainSingle(e => e.Control == "Automated dependency updates" && e.Implementation.Contains("configured in source")).Which.Execution.Should().Be("Not assessed");
        DependencyTraceability.For(Sources(), "Internal package versions shall be consistent.").Single().Implementation.Should().Contain("6 consumer(s) with a version difference");
    }

    [Fact]
    public void Source_analysis_dependencies_view_shows_mappings_from_stored_evidence()
    {
        var cut = Render<DependencyEvidenceWorkspace>(p => p.Add(x => x.Snapshot, Common()).Add(x => x.Snapshots, [Common(), App()]));
        cut.Find("[data-testid=sd-deps-overview]").TextContent.Should().Contain("Internal packages produced1").And.Contain("Internal package mappings1").And.Contain("Version differences6");
        var row = cut.Find("[data-testid=sd-deps-mapping]");
        row.TextContent.Should().Contain("M2LB.Common").And.Contain("2.4.1").And.Contain("21").And.Contain("versions 2.3.0, 2.4.1").And.Contain("in 1 source snapshot(s)");
        cut.Find("[data-testid=sd-deps-review]").GetAttribute("href").Should().Be("dependency-review");
        cut.Find("[data-testid=sd-deps-doc-text]").TextContent.Should().Contain("15 mapped, 6 mapped with version difference").And.NotContain("Conflict");
        cut.Markup.Should().NotContain("Conflict").And.NotContain("Broken", "a version difference is never labelled a conflict or a break");

        var app = Render<DependencyEvidenceWorkspace>(p => p.Add(x => x.Snapshot, App()).Add(x => x.Snapshots, [Common(), App()]));
        app.Find("[data-testid=sd-deps-policy]").TextContent.Should().Contain("renovate.json").And.Contain("never executed");
    }

    [Fact]
    public void A_re_analyzed_identical_archive_does_not_double_the_consumers()
    {
        var appAgain = App() with { Id = Guid.NewGuid(), AnalyzedAt = DateTimeOffset.Parse("2026-10-22T07:00:00Z") };
        var cut = Render<DependencyEvidenceWorkspace>(p => p.Add(x => x.Snapshot, Common()).Add(x => x.Snapshots, [Common(), App(), appAgain]));
        cut.Find("[data-testid=sd-deps-mapping]").TextContent.Should().Contain("21").And.Contain("in 1 source snapshot(s)");

        var viewed = Render<DependencyEvidenceWorkspace>(p => p.Add(x => x.Snapshot, appAgain).Add(x => x.Snapshots, [Common(), App(), appAgain]));
        viewed.Find("[data-testid=sd-deps-differences]").TextContent.Should().Be("6", "the viewed re-analysis stands for its archive content");
    }

    [Fact]
    public void A_snapshot_from_before_dependency_evidence_says_so()
    {
        var cut = Render<DependencyEvidenceWorkspace>(p => p.Add(x => x.Snapshot, Common() with { DependencyEvidence = null }));
        cut.Find("[data-testid=sd-deps-not-assessed]").TextContent.Should().Contain("predates dependency evidence");
    }
}
