using BirkNext.Integrations;
using BirkNext.TestData;

namespace BirkNext.Api.Services.SecurityClassification;

/// <summary>A domain that consumes the shared Security Classification evidence. It declares which shared requirements matter to it and adds
/// its own domain requirements; it never re-reads source, keeps no source selection, test context or identities of its own.</summary>
public interface ISecurityClassificationConsumer
{
    SecurityClassificationConsumerDefinition Definition { get; }
}

/// <summary>M01 / M2LB graded access: the capability's original consumer. Its rules are the shared review itself (no extra domain rules).</summary>
public sealed class M01SecurityClassificationConsumer : ISecurityClassificationConsumer
{
    public SecurityClassificationConsumerDefinition Definition { get; } = new("m01", "M01 – Gradert tilgang (Kode 6/7)", "M2LB graded access and Kode 6/7 classification",
        Enum.GetValues<SecurityRequirementKind>(), []);
}

/// <summary>
/// Every consumer's assessment from the same shared evidence: the latest stored review of the environment, the one canonical (in-memory) test
/// context, and the approved controlled identity references in the shared Test Data catalog. Read-only — nothing runs, nothing is stored.
/// </summary>
public sealed class SecurityClassificationConsumerService(IClassificationReviewService reviews, IEnumerable<ISecurityClassificationConsumer> consumers,
    BirkNext.Api.Services.TestData.TestDataCatalog? testData = null)
{
    /// <summary>The Test Data capability of a controlled protected-identity reference (approved by a test lead; never generated).</summary>
    public const string ProtectedIdentityCapability = "protected-identity";

    public async Task<IReadOnlyList<SecurityClassificationConsumerAssessment>> AssessAsync(string environmentId, CancellationToken ct)
    {
        var overview = await reviews.OverviewAsync(environmentId, ct);
        var context = ClassificationContextSummary.From(overview.Context);
        var current = context.Environment is null && !context.Approved && !context.EndpointConfigured ? null : context;
        int? identities = null;
        if (testData is not null)
        {
            try
            {
                identities = testData.View().Assets.Count(asset => asset.Approval == TestDataApproval.Approved && asset.Kind == TestDataKind.EntityReference
                    && asset.Capabilities.Contains(ProtectedIdentityCapability, StringComparer.OrdinalIgnoreCase));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { identities = null; }
        }
        return [.. consumers.Select(c => SecurityClassificationAssessments.For(c.Definition, overview.Latest, current, identities))];
    }
}
