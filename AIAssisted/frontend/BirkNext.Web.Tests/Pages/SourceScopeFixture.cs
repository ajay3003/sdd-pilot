using BirkNext.Dependencies;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>Source Analysis snapshots as Dependency Review receives them (generic repositories; no project-specific production logic).</summary>
internal static class SourceScopeFixture
{
    public static readonly SourceScopeSnapshot App = new()
    {
        SnapshotId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), RepositoryKey = "m2lb", Repository = "M2LB", IdentityBasis = "Archive file name", ArchiveName = "M2LB (1).zip",
        Fingerprint = "c850a1b2" + new string('0', 56), AnalyzedAt = DateTimeOffset.Parse("2026-10-01T11:14:00Z"), SourceStatus = "Partial", HasDependencyEvidence = true, DeclaredDependencies = 210, RenovateFiles = 1, Latest = true,
    };
    public static readonly SourceScopeSnapshot Common = new()
    {
        SnapshotId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002"), RepositoryKey = "m2lb.common", Repository = "M2LB.Common", IdentityBasis = "Root solution file M2LB.Common.slnx", ArchiveName = "M2LB.Common.zip",
        Fingerprint = "a91c0000" + new string('1', 56), AnalyzedAt = DateTimeOffset.Parse("2026-10-01T09:00:00Z"), SourceStatus = "Ready", HasDependencyEvidence = true, DeclaredDependencies = 31, RenovateFiles = 1, Latest = true,
    };
    public static readonly RelatedSourceCandidate CommonCandidate = new()
    {
        RepositoryKey = "m2lb.common", Repository = "M2LB.Common", State = RelatedSourceState.SnapshotAvailable, Reason = "Referenced by 15 project(s) in the primary source.",
        Evidence = [new("PackageReference", "Package M2LB.Common 2.1.0, which this repository's snapshot declares it produces (exact package ID).", 15, ["Person/src/Person.Api/Person.Api.csproj"])],
        MatchingSnapshotIds = [Common.SnapshotId],
    };

    /// <summary>The active Target Environment and default feature visibility the page reads.</summary>
    public static void Register(BunitContext context, string? environmentId = "dev", bool sourceAnalysisEnabled = true)
    {
        var factory = new Mock<IFrontendAnalysisContextFactory>();
        factory.Setup(f => f.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext { ActiveProfile = environmentId is null ? null : new FrontendAnalysisProfile { Id = environmentId, Name = "Dev" } });
        context.Services.AddSingleton(factory.Object);
        var flags = new FeatureVisibilityService();
        flags.ApplyLocalFlags(new FeatureVisibilityDto { SourceAnalysis = sourceAnalysisEnabled });
        context.Services.AddSingleton(flags);
    }
}
