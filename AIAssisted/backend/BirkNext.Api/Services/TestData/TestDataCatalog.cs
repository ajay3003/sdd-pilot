using System.Security.Cryptography;
using System.Text;
using BirkNext.TestData;

namespace BirkNext.Api.Services.TestData;

/// <summary>
/// The test data that exists, from every provider: what the Test Data page shows and what consumers resolve against. Aliases are the identity:
/// an alias offered by more than one asset is disabled until it is unique, never picked arbitrarily. Summaries respect sensitivity — a
/// sensitive value never leaves this class except through <see cref="Reveal"/>, which execution code uses transiently.
/// </summary>
public sealed class TestDataCatalog(IEnumerable<ITestDataProvider> providers, TimeProvider? clock = null, TestDataRegistryStore? registry = null)
{
    private const int PreviewLimit = 16 * 1024;
    private readonly IReadOnlyList<ITestDataProvider> _providers = [.. providers];
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public IReadOnlyList<ITestDataProvider> Providers => _providers;

    /// <summary>Every asset, with an alias configured more than once disabled (no arbitrary pick).</summary>
    public IReadOnlyList<TestDataAsset> Assets() => Unique([.. _providers.SelectMany(provider => provider.Assets().Select(asset => Overlay(asset with { SourceId = provider.Id })))]);

    public TestDataRegistryStore? Registry => registry;

    /// <summary>
    /// Lifecycle decisions made in Test Data: an explicit approval applies to exactly the version it was given for (a changed asset is no
    /// longer approved by it), and deprecation keeps the asset for history while it is no longer offered for new usage.
    /// </summary>
    private TestDataAsset Overlay(TestDataAsset asset)
    {
        if (registry?.Lifecycle(asset.Id) is not { } decision) return asset;
        var approved = decision.ApprovedFingerprint is { } fingerprint && fingerprint == ApprovalFingerprint(asset);
        return asset with
        {
            Approval = approved && asset.Approval == TestDataApproval.Unapproved ? TestDataApproval.Approved : asset.Approval,
            ApprovalDetail = approved ? null : asset.ApprovalDetail,
            ReviewRequested = asset.ReviewRequested || decision.ApprovedFingerprint is not null && !approved,
            Deprecated = decision.Deprecated,
        };
    }

    /// <summary>The version an approval is given for: the value's fingerprint when the backend holds one, else the asset's metadata and structure.</summary>
    public static string ApprovalFingerprint(TestDataAsset asset) => TestDataCatalogRules.Fingerprint(asset.Value is { } value
        ? value.Reveal()
        : string.Join("|", asset.Alias, asset.Kind, asset.MediaType, asset.Environment, asset.Schema?.SchemaPath, asset.SchemaRef, string.Join(",", asset.Fields)));

    private static IReadOnlyList<TestDataAsset> Unique(List<TestDataAsset> all)
    {
        var duplicates = all.GroupBy(asset => asset.Alias, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        return [.. all.Select(asset => duplicates.Contains(asset.Alias)
            ? asset with { Approval = TestDataApproval.Disabled, ApprovalDetail = $"The alias {asset.Alias} is configured more than once; it is disabled until it is unique." }
            : asset)];
    }

    public IReadOnlyList<TestDataSet> Sets() => [.. _providers.SelectMany(provider => provider.Sets())];

    public TestDataResolutionResult Resolve(IReadOnlyList<TestDataRequirement> requirements, IReadOnlyList<TestDataConstraint>? constraints = null,
        IReadOnlyDictionary<string, string>? selections = null, IReadOnlyList<string>? dataSetCapabilities = null) =>
        TestDataResolver.Resolve(Assets(), requirements, constraints, selections, Sets(), dataSetCapabilities, _clock.GetUtcNow());

    public List<TestDataAsset> Candidates(TestDataRequirement requirement) => TestDataResolver.Candidates(Assets(), requirement, _clock.GetUtcNow());

    /// <summary>The value behind an asset alias, for execution only (never returned to a client, logged or stored).</summary>
    public string? Reveal(string alias) => Assets().FirstOrDefault(asset => string.Equals(asset.Alias, alias, StringComparison.Ordinal))?.Value?.Reveal();

    /// <summary>
    /// The page view. Each provider is read on its own: one that fails is reported as an unavailable source with a scoped problem, and
    /// the others still show. (Resolution keeps failing loudly — it never silently resolves against a partial catalog.)
    /// </summary>
    public TestDataCatalogView View()
    {
        var reads = _providers.Select(provider =>
        {
            try
            {
                var providerAssets = provider.Assets().Select(asset => Overlay(asset with { SourceId = provider.Id })).ToList();
                var providerSets = provider.Sets().ToList();
                return (Provider: provider, Assets: providerAssets, Sets: providerSets, Configured: provider.Configured, Problem: (string?)null);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return (Provider: provider, Assets: new List<TestDataAsset>(), Sets: new List<TestDataSet>(), Configured: true,
                    Problem: (string?)$"This source could not be read ({ex.GetType().Name}). Other sources are unaffected.");
            }
        }).ToList();
        var assets = Unique([.. reads.SelectMany(read => read.Assets)]);
        var sets = reads.SelectMany(read => read.Sets).ToList();
        return new(
            [.. assets.Select(Summary).OrderBy(asset => asset.Source, StringComparer.Ordinal).ThenBy(asset => asset.Alias, StringComparer.Ordinal)],
            [.. sets.Select(set => new TestDataSetSummary
            {
                Id = set.Id, Alias = set.Alias, Label = set.Label, Environment = set.Environment, Approval = set.Approval, Capabilities = set.Capabilities,
                Bindings = [.. set.Bindings.OrderBy(binding => binding.Key, StringComparer.Ordinal).Select(binding => new TestDataRoleBinding(binding.Key, binding.Value))], Source = set.Source,
            })],
            [.. reads.Select(read =>
            {
                var status = read.Problem is not null ? TestDataSourceStatus.Unavailable
                    : read.Assets.Count + read.Sets.Count > 0 ? TestDataSourceStatus.Available
                    : read.Configured ? TestDataSourceStatus.Empty : TestDataSourceStatus.NotConfigured;
                return new TestDataSourceSummary(read.Provider.Id, read.Provider.Label, read.Provider.Description, read.Assets.Count, read.Sets.Count, status, read.Provider.Detail, read.Problem)
                {
                    StatusLabel = status == TestDataSourceStatus.Unavailable ? null : read.Provider.StatusLabel(status),
                };
            })]);
    }

    public TestDataAssetDetail? Detail(string id)
    {
        if (Assets().FirstOrDefault(asset => asset.Id == id) is not { } asset) return null;
        var summary = Summary(asset);
        if (asset.Sensitivity == TestDataSensitivity.Sensitive) return new(summary, null, "Sensitive: the value stays in the backend and is never shown.");
        if (asset.Value is null) return new(summary, null, asset.ExpectsAbsence ? "Expects that nothing exists." : "No value is available.");
        var value = asset.Value.Reveal();
        return value.Length > PreviewLimit
            ? new(summary, value[..PreviewLimit], $"Preview of the first {PreviewLimit / 1024} KB.")
            : new(summary, value, "Full value (not sensitive).");
    }

    public TestDataAssetSummary Summary(TestDataAsset asset)
    {
        var (validation, detail) = TestDataValidator.Validate(asset);
        var approval = TestDataResolver.EffectiveApproval(asset, _clock.GetUtcNow());
        var structured = asset.Kind is not (TestDataKind.Scalar or TestDataKind.EntityReference) && asset.Value is not null;
        return new TestDataAssetSummary
        {
            Id = asset.Id, Alias = asset.Alias, Label = asset.Label, Kind = asset.Kind, ValueType = asset.ValueType, MediaType = asset.MediaType, EntityType = asset.EntityType,
            // Approval and validation stay separate facts: an approval reason never replaces the validation detail.
            Environment = asset.Environment, Approval = approval, Validation = validation, ValidationDetail = detail,
            ApprovalDetail = approval != TestDataApproval.Approved ? asset.ApprovalDetail ?? (approval == TestDataApproval.Expired ? "The approval has expired." : null) : null,
            Capabilities = [.. asset.Capabilities.Order(StringComparer.Ordinal)], Sensitivity = asset.Sensitivity, Source = asset.Source,
            SourceId = asset.SourceId, Provenance = [.. asset.Provenance], ValueAvailable = asset.Value is not null,
            IsDefault = asset.IsDefault, ExpiresAt = asset.ExpiresAt, ExpectsAbsence = asset.ExpectsAbsence,
            Fingerprint = structured ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(asset.Value!.Reveal()))).ToLowerInvariant() : null,
            DisplayValue = asset.Kind == TestDataKind.Scalar && asset.Sensitivity != TestDataSensitivity.Sensitive ? asset.Value?.Reveal() : null,
            Schema = asset.Schema,
            DataType = TestDataDataTypes.Of(asset.Kind, asset.MediaType),
            Status = TestDataCatalogRules.Status(approval, asset.Deprecated, registry?.Lifecycle(asset.Id)?.ApprovedFingerprint, ApprovalFingerprint(asset), asset.ReviewRequested),
            ApprovedAt = registry?.Lifecycle(asset.Id)?.ApprovedAt,
            ApprovedFingerprint = registry?.Lifecycle(asset.Id)?.ApprovedFingerprint,
            SchemaRef = asset.SchemaRef ?? asset.Schema?.SchemaPath,
            Fields = asset.Fields.Count > 0 ? [.. asset.Fields] : null,
            Tags = asset.Tags.Count > 0 ? [.. asset.Tags] : null,
            CandidateId = asset.CandidateId,
            Reviewable = true,
            Ownership = asset.Ownership,
            ValueVersion = asset.ValueVersion, ValueUpdatedAt = asset.ValueUpdatedAt, ValueModified = asset.ValueModified, ValueEditable = asset.ValueEditable,
        };
    }
}
