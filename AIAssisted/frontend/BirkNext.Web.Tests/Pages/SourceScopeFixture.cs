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
    public static readonly ReviewSourceSnapshot App = new()
    {
        SnapshotId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), RepositoryKey = "m2lb", Repository = "M2LB", IdentityBasis = "Archive file name", ArchiveName = "M2LB (1).zip",
        Fingerprint = "c850a1b2" + new string('0', 56), AnalyzedAt = DateTimeOffset.Parse("2026-10-01T11:14:00Z"), SourceStatus = "Partial", HasConsumerEvidence = true, ConsumerSummary = "210 declared dependencies · 1 Renovate configuration file(s)", Latest = true,
    };
    public static readonly ReviewSourceSnapshot Common = new()
    {
        SnapshotId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002"), RepositoryKey = "m2lb.common", Repository = "M2LB.Common", IdentityBasis = "Root solution file M2LB.Common.slnx", ArchiveName = "M2LB.Common.zip",
        Fingerprint = "a91c0000" + new string('1', 56), AnalyzedAt = DateTimeOffset.Parse("2026-10-01T09:00:00Z"), SourceStatus = "Ready", HasConsumerEvidence = true, ConsumerSummary = "31 declared dependencies · 1 Renovate configuration file(s)", Latest = true,
    };
    public static readonly RelatedSourceCandidate CommonCandidate = new()
    {
        RepositoryKey = "m2lb.common", Repository = "M2LB.Common", State = RelatedSourceState.SnapshotAvailable, Reason = "Referenced by 15 project(s) in the primary source.",
        Evidence = [new("PackageReference", "Package M2LB.Common 2.1.0, which this repository's snapshot declares it produces (exact package ID).", 15, ["Person/src/Person.Api/Person.Api.csproj"])],
        MatchingSnapshotIds = [Common.SnapshotId],
    };

    /// <summary>
    /// What the backend's shared provider answers for a scope: the exact snapshots as the resolved scope (unknown ids → an error, never a
    /// substitute), the primary's candidates, and newer snapshots of the scope's repositories. Fakes use it so pages see real semantics.
    /// </summary>
    public static ReviewSourceOptions Resolve(ReviewSourceOptions options, ReviewSourceScopeRequest? scope, List<RelatedSourceCandidate> candidates)
    {
        if (scope is null) return options;
        var ids = new[] { scope.PrimarySnapshotId }.Concat(scope.RelatedSnapshotIds).ToList();
        var selected = ids.Select(id => options.Snapshots.FirstOrDefault(s => s.SnapshotId == id)).ToList();
        if (selected.Any(s => s is null)) return options with { Candidates = candidates, Error = "Source snapshot is unavailable in Source Analysis. Repair the source scope; nothing is substituted." };
        static SourceScopeEntry Entry(ReviewSourceSnapshot s) => new() { SnapshotId = s.SnapshotId, RepositoryKey = s.RepositoryKey, Repository = s.Repository, ArchiveName = s.ArchiveName, Fingerprint = s.Fingerprint, AnalyzedAt = s.AnalyzedAt, SourceStatus = s.SourceStatus };
        var notIncluded = candidates.Where(c => selected.Skip(1).All(s => s!.RepositoryKey != c.RepositoryKey)).ToList();
        return options with
        {
            Candidates = candidates, Newer = ReviewSourceScopes.Newer(options.Snapshots, ids),
            Scope = new ReviewSourceScope
            {
                Primary = Entry(selected[0]!), Related = selected.Skip(1).Select(s => Entry(s!)).ToList(), ExcludedSuggestions = scope.ExcludedSuggestions,
                Limitations = notIncluded.Select(c => $"Related source detected but not included in this review scope: {c.Repository} (snapshot available).").ToList(),
            },
        };
    }

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
