using System.Security.Cryptography;
using System.Text;
using BirkNext.TestData;

namespace BirkNext.Api.Services.TestData;

/// <summary>
/// The test data that exists, from every provider: what the Test Data page shows and what consumers resolve against. Aliases are the identity:
/// an alias offered by more than one asset is disabled until it is unique, never picked arbitrarily. Summaries respect sensitivity — a
/// sensitive value never leaves this class except through <see cref="Reveal"/>, which execution code uses transiently.
/// </summary>
public sealed class TestDataCatalog(IEnumerable<ITestDataProvider> providers, TimeProvider? clock = null)
{
    private const int PreviewLimit = 16 * 1024;
    private readonly IReadOnlyList<ITestDataProvider> _providers = [.. providers];
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public IReadOnlyList<ITestDataProvider> Providers => _providers;

    /// <summary>Every asset, with an alias configured more than once disabled (no arbitrary pick).</summary>
    public IReadOnlyList<TestDataAsset> Assets()
    {
        var all = _providers.SelectMany(provider => provider.Assets()).ToList();
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

    public TestDataCatalogView View()
    {
        var assets = Assets();
        var sets = Sets();
        return new(
            [.. assets.Select(Summary).OrderBy(asset => asset.Source, StringComparer.Ordinal).ThenBy(asset => asset.Alias, StringComparer.Ordinal)],
            [.. sets.Select(set => new TestDataSetSummary
            {
                Id = set.Id, Alias = set.Alias, Label = set.Label, Environment = set.Environment, Approval = set.Approval, Capabilities = set.Capabilities,
                Bindings = [.. set.Bindings.OrderBy(binding => binding.Key, StringComparer.Ordinal).Select(binding => new TestDataRoleBinding(binding.Key, binding.Value))], Source = set.Source,
            })],
            [.. _providers.Select(provider => new TestDataSourceSummary(provider.Id, provider.Label, provider.Description, provider.Assets().Count, provider.Sets().Count))]);
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
            Environment = asset.Environment, Approval = approval, Validation = validation,
            ValidationDetail = approval != TestDataApproval.Approved && asset.ApprovalDetail is { } why ? why : detail,
            Capabilities = [.. asset.Capabilities.Order(StringComparer.Ordinal)], Sensitivity = asset.Sensitivity, Source = asset.Source, ValueAvailable = asset.Value is not null,
            IsDefault = asset.IsDefault, ExpiresAt = asset.ExpiresAt, ExpectsAbsence = asset.ExpectsAbsence,
            Fingerprint = structured ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(asset.Value!.Reveal()))).ToLowerInvariant() : null,
            DisplayValue = asset.Kind == TestDataKind.Scalar && asset.Sensitivity != TestDataSensitivity.Sensitive ? asset.Value?.Reveal() : null,
            Schema = asset.Schema,
        };
    }
}
