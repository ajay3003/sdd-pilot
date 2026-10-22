using BirkNext.Api.Services.DependencyReview;
using BirkNext.Api.Services.PipelineReview;
using BirkNext.Dependencies;
using BirkNext.Integrations;
using FluentAssertions;
using Xunit;

namespace BirkNext.Api.Tests.Services.DependencyReview;

/// <summary>
/// Shared dependency evidence: a provider family whose source provider extracts declarations once at Source Analysis time; normalized internal
/// package producer → consumer relationships (versions kept apart, never "conflict"); and consumers (Pipeline Review, Impact) that read stored
/// evidence instead of re-parsing manifests.
/// </summary>
public sealed class DependencyEvidenceArchitectureTests
{
    private const string Props = """
        <Project>
          <PropertyGroup><PackageId>Contoso.Common</PackageId><Version>2.4.1</Version></PropertyGroup>
        </Project>
        """;

    private static string Consumer(string version) => $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <ItemGroup><PackageReference Include="Contoso.Common" Version="{version}" /><PackageReference Include="Serilog" /></ItemGroup>
        </Project>
        """;

    private const string Central = """
        <Project>
          <ItemGroup><PackageVersion Include="Serilog" Version="4.0.0" /></ItemGroup>
        </Project>
        """;

    private static SourceDependencyEvidence Producer() => SourceDependencyEvidenceExtractor.FromFiles("Contoso.Common",
        [new("Directory.Build.props", Props), new("src/Contoso.Common/Contoso.Common.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>")]);

    private static SourceDependencyEvidence Consumers() => SourceDependencyEvidenceExtractor.FromFiles("Contoso.Orders",
    [
        new("Directory.Packages.props", Central),
        .. Enumerable.Range(1, 3).Select(i => new RepositoryFile($"src/Api{i}/Api{i}.csproj", Consumer("2.4.1"))),
        .. Enumerable.Range(4, 2).Select(i => new RepositoryFile($"src/Worker{i}/Worker{i}.csproj", Consumer("2.3.0"))),
    ]);

    [Fact]
    public void The_provider_family_covers_every_layer_and_mode_with_what_each_cannot_prove()
    {
        DependencyEvidenceProviders.All.Select(p => p.Layer).Should().Contain([DependencyEvidenceLayer.Declared, DependencyEvidenceLayer.Resolved, DependencyEvidenceLayer.Sbom, DependencyEvidenceLayer.Deployed,
            DependencyEvidenceLayer.Policy, DependencyEvidenceLayer.PolicyExecution, DependencyEvidenceLayer.Registry, DependencyEvidenceLayer.Security, DependencyEvidenceLayer.License, DependencyEvidenceLayer.InternalRelationship]);
        foreach (var mode in new[] { DependencyEvidenceProviders.SourceMode, DependencyEvidenceProviders.InventoryMode, DependencyEvidenceProviders.SbomMode, DependencyEvidenceProviders.DeployedMode })
            DependencyEvidenceProviders.ForMode(mode).Should().NotBeEmpty(mode);
        DependencyEvidenceProviders.Get("source-snapshot").RequiresNetwork.Should().BeFalse("source extraction works offline");
        DependencyEvidenceProviders.Get("renovate-policy").CannotProve.Should().Contain("never executed");
        DependencyEvidenceProviders.Get("sbom").Formats.Should().Contain(f => f.Contains("not a full SBOM"), "a lock file is a resolved inventory, not an SBOM");
        new SourceSnapshotDependencyProvider().Descriptor.Key.Should().Be("source-snapshot");
        ((IDependencyEvidenceProvider)new NuGetRegistryProvider(new HttpClient())).Descriptor.Layer.Should().Be(DependencyEvidenceLayer.Registry);
    }

    [Fact]
    public void Central_package_management_counts_each_consumer_once_and_keeps_its_declared_versions()
    {
        var sources = new[] { new DependencySourceEvidence(Guid.NewGuid(), "Contoso.Orders.zip", DateTimeOffset.UtcNow, Consumers()) };
        var serilog = DependencyEvidenceQueries.ConsumersForPackage(sources, new DependencyCoordinate("nuget", "Serilog")).ToList();
        serilog.Should().HaveCount(5, "one central version, five consuming projects — never double-counted").And.OnlyContain(d => d.DeclaredVersion == "4.0.0" && d.OwnerFile == "Directory.Packages.props");
        DependencyEvidenceQueries.DependenciesForProject(sources, "Worker4").Select(d => d.Package.Name).Should().BeEquivalentTo(["Contoso.Common", "Serilog"]);
    }

    [Fact]
    public void Internal_packages_are_mapped_across_snapshots_with_version_differences_never_conflicts()
    {
        var producer = new DependencySourceEvidence(Guid.NewGuid(), "Contoso.Common.zip", DateTimeOffset.UtcNow, Producer());
        var consumer = new DependencySourceEvidence(Guid.NewGuid(), "Contoso.Orders.zip", DateTimeOffset.UtcNow, Consumers());
        var mapping = DependencyEvidenceQueries.InternalPackageMappings([producer, consumer]).Should().ContainSingle().Subject;
        mapping.ProducerVersion.Should().Be("2.4.1");
        mapping.ProducerSnapshotId.Should().Be(producer.SnapshotId);
        mapping.Consumers.Should().HaveCount(5).And.OnlyContain(c => c.SnapshotId == consumer.SnapshotId, "snapshots are related, never merged");
        mapping.Aligned.Should().Be(3);
        mapping.VersionDifferences.Should().Be(2);
        DependencyEvidenceQueries.VersionDifferences([mapping]).Select(c => c.DeclaredVersion).Should().OnlyContain(v => v == "2.3.0");
        DependencyEvidenceQueries.State("2.4.1", "[2.0,3.0)", true).Should().Be(InternalConsumerState.VersionNotComparable);
    }

    [Fact]
    public void Re_analyses_of_an_identical_archive_are_one_source_never_double_counted()
    {
        var producer = new DependencySourceEvidence(Guid.NewGuid(), "Contoso.Common.zip", DateTimeOffset.UtcNow.AddHours(-1), Producer(), "AA");
        var producerAgain = new DependencySourceEvidence(Guid.NewGuid(), "Contoso.Common.zip", DateTimeOffset.UtcNow, Producer(), "aa");
        var consumer = new DependencySourceEvidence(Guid.NewGuid(), "Contoso.Orders.zip", DateTimeOffset.UtcNow.AddHours(-1), Consumers(), "BB");
        var consumerAgain = new DependencySourceEvidence(Guid.NewGuid(), "Contoso.Orders.zip", DateTimeOffset.UtcNow, Consumers(), "BB");
        var mapping = DependencyEvidenceQueries.InternalPackageMappings([producer, consumer, producerAgain, consumerAgain]).Should().ContainSingle().Subject;
        mapping.ProducerSnapshotId.Should().Be(producer.SnapshotId, "the first in the caller's order is kept, not the newest");
        mapping.Consumers.Should().HaveCount(5).And.OnlyContain(c => c.SnapshotId == consumer.SnapshotId);

        var changedConsumer = consumerAgain with { ArchiveSha256 = "CC" };
        DependencyEvidenceQueries.InternalPackageMappings([producer, consumer, changedConsumer]).Single().Consumers.Should().HaveCount(10, "different archive content stays a separate source");
    }

    [Fact]
    public void Advisory_coverage_with_unavailable_lookups_is_incomplete_never_zero_vulnerabilities()
    {
        var items = Enumerable.Range(0, 32).Select(i => new DependencyHealthItem
        {
            Key = $"nuget:p{i}@1.0.0", Dependency = new InventoryDependency { PackageName = $"p{i}", Version = "1.0.0", PackageManager = "nuget" },
            Security = new SecurityEvidence { State = i == 0 ? AdvisoryState.NoMatchedAdvisoryObserved : AdvisoryState.AdvisorySourceUnavailable },
            License = new LicenseEvidence { State = i < 30 ? LicenseState.Detected : LicenseState.Unknown },
        }).ToList();
        var run = new DependencyHealthRun { Items = items, Inventory = new DependencyInventorySnapshot { Name = "inventory" } };
        var advisory = DependencyEvidenceQueries.AdvisoryCoverage(run);
        advisory.Text.Should().Be("No matched advisories in available evidence · advisory coverage incomplete (31 of 32 not assessed or unavailable)");
        advisory.Complete.Should().BeFalse();
        DependencyEvidenceQueries.LicenseCoverage(run).Text.Should().Be("94% license metadata available · 2 unknown/not declared");
        DependencyEvidenceQueries.Coverage(run).Select(d => d.Dimension).Should().Equal("Inventory coverage", "Registry coverage", "Advisory coverage", "License coverage", "Policy coverage", "Deployment coverage");
    }

    [Fact]
    public void Pipeline_review_reads_dependency_automation_and_package_publication_as_configuration()
    {
        const string pipeline = """
            trigger:
              branches: { include: [main] }
            steps:
              - script: dotnet restore
              - script: osv-scanner --lockfile packages.lock.json
              - script: dotnet CycloneDX src/Contoso.Common/Contoso.Common.csproj
              - script: dotnet pack src/Contoso.Common/Contoso.Common.csproj
            """;
        var snapshot = BirkNext.Api.Tests.Services.PipelineReview.PipelineReviewFixtures.Snapshot(("azure-pipelines.yml", pipeline), ("Directory.Build.props", Props),
            ("src/Contoso.Common/Contoso.Common.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"), ("renovate.json", "{\"extends\":[\"config:recommended\"]}"));
        snapshot = snapshot with { DependencyEvidence = Producer() };
        var review = PipelineReviewBuilder.Build(snapshot);
        var items = review.DependencyAutomation;
        items.Should().Contain(i => i.Kind == "Restore / install");
        items.Should().Contain(i => i.Kind == "Dependency scan" && i.State == "Dependency quality gate configured" && i.Detail.Contains("not assessed"));
        items.Should().Contain(i => i.Kind == "SBOM generation");
        items.Should().Contain(i => i.Kind == "Package publication" && i.State == "Candidate" && i.Detail.Contains("Contoso.Common 2.4.1") && i.Detail.Contains("not proven published"));
        items.Should().Contain(i => i.Kind == "Update automation" && i.Detail.Contains("not assessed"));
        items.Should().NotContain(i => i.State.Contains("passed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Traceability_and_documentation_use_normalized_evidence_without_raw_manifests()
    {
        var consumer = new DependencySourceEvidence(Guid.NewGuid(), "Contoso.Orders.zip", DateTimeOffset.UtcNow, Consumers());
        var producer = new DependencySourceEvidence(Guid.NewGuid(), "Contoso.Common.zip", DateTimeOffset.UtcNow, Producer());
        DependencyTraceability.For([producer, consumer], "Dependency versions must be pinned.").Single().Implementation.Should().Contain("exact");
        var markdown = DependencyDocumentation.Markdown(producer, DependencyEvidenceQueries.InternalPackageMappings([producer, consumer]));
        markdown.Should().Contain("Contoso.Common 2.4.1 produced by Contoso.Common.zip: 5 consumer(s), 3 mapped, 2 mapped with version difference").And.NotContain("<Project");
    }
}
