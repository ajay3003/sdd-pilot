using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Delivery;
using BirkNext.Integrations;
using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.PipelineReview;

/// <summary>A pipeline &amp; delivery evidence provider: what it reads, what it proves and what it cannot.</summary>
public interface IPipelineEvidenceProvider
{
    DeliveryEvidenceProviderDescriptor Descriptor { get; }
}

/// <summary>Pipeline source: the Source Analysis CI/CD analyzer's stored definitions (parsed once per immutable snapshot).</summary>
public sealed class SourcePipelineEvidenceProvider(PipelinePlatform platform) : IPipelineEvidenceProvider
{
    public DeliveryEvidenceProviderDescriptor Descriptor => DeliveryEvidenceProviders.Get(DeliveryEvidenceProviders.ForPlatform(platform));

    public static IReadOnlyList<PipelineDefinition> Definitions(IqrSourceSnapshot snapshot) => snapshot.EvidenceDomains?.CiCd.Pipelines ?? [];
}

/// <summary>The delivery derivation (template composition, gates, lineage) over stored definitions, projected into shared delivery evidence.</summary>
public sealed class DeliveryDerivationProvider : IPipelineEvidenceProvider
{
    public DeliveryEvidenceProviderDescriptor Descriptor => DeliveryEvidenceProviders.Get("delivery-derivation");

    public static DeliveryEvidence For(IqrSourceSnapshot snapshot) =>
        DeliveryEvidenceQueries.WithCanonicalTests(DeliveryEvidenceProjection.From(PipelineReviewBuilder.Build(snapshot), SourcePipelineEvidenceProvider.Definitions(snapshot)), snapshot.TestInventory);
}

/// <summary>Read-only delivery evidence for one exact snapshot (never the newest in its place). Runtime metadata stays a separate provider.</summary>
public sealed class DeliveryEvidenceService(IPipelineReviewService reviews, IReviewSourceEvidenceProvider sources)
{
    public static IReadOnlyList<DeliveryEvidenceProviderDescriptor> Providers() => DeliveryEvidenceProviders.All;

    public async Task<DeliveryEvidence?> GetAsync(string environmentId, Guid snapshotId, CancellationToken ct)
    {
        if (await sources.ResolveAsync(environmentId, snapshotId, ct) is not { } snapshot) return null;
        var review = await reviews.ReviewAsync(environmentId, snapshotId, false, ct);
        return review is null ? null : DeliveryEvidenceQueries.WithCanonicalTests(DeliveryEvidenceProjection.From(review, SourcePipelineEvidenceProvider.Definitions(snapshot)), snapshot.TestInventory);
    }
}
