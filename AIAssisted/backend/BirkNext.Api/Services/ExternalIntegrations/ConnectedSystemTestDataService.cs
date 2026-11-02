using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Services.TestData;
using BirkNext.Integrations;
using BirkNext.TestData;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.ExternalIntegrations;

/// <summary>
/// Connected Systems ↔ Test Data: the provider's requirements, the persisted bindings stored on the integration (stable asset ids — never a value)
/// and deterministic suggestions from Test Data metadata (semantic role, data type, "Applies to", scope, approval). A suggestion is only persisted
/// by an explicit Apply, an existing binding always wins and is never replaced by a newer suggestion. The one write on read is the deterministic
/// reference migration (<see cref="MigrateAsync"/>): legacy alias-only bindings get their asset id, and — once per document — the asset the
/// provider's readiness already consumed is recorded as its binding.
/// </summary>
public sealed class ConnectedSystemTestDataService(ExternalIntegrationService integrations, TestDataCatalogService catalog, TimeProvider clock)
{
    /// <summary>The reference migration that recorded consumed-but-unpersisted usage (see <see cref="ExternalIntegration.TestDataReferenceVersion"/>).</summary>
    public const int ReferenceVersion = 2;

    public async Task<IReadOnlyList<ConnectedSystemTestDataRequirement>> RequirementsAsync(ExternalIntegration integration, CancellationToken ct) =>
        await integrations.Handler(integration.ProviderType).TestDataRequirementsAsync(integration, ct);

    /// <summary>The Test Data section of every view (one catalog read for all of them).</summary>
    public async Task<ExternalIntegrationsView> AttachAsync(ExternalIntegrationsView view, CancellationToken ct)
    {
        if (view.Integrations.Count == 0) return view;
        var assets = (await catalog.ViewAsync(ct)).Assets;
        var list = new List<ExternalIntegrationView>();
        foreach (var item in view.Integrations)
        {
            var requirements = await RequirementsAsync(item.Integration, ct);
            var integration = await MigrateAsync(item.Integration, requirements, assets, ct);
            list.Add(item with { Integration = integration, TestData = ConnectedSystemTestDataMatcher.View(integration, requirements, assets) });
        }
        return view with { Integrations = list };
    }

    public async Task<ExternalIntegrationView> AttachAsync(ExternalIntegrationView view, CancellationToken ct)
    {
        var assets = (await catalog.ViewAsync(ct)).Assets;
        var requirements = await RequirementsAsync(view.Integration, ct);
        var integration = await MigrateAsync(view.Integration, requirements, assets, ct);
        return view with { Integration = integration, TestData = ConnectedSystemTestDataMatcher.View(integration, requirements, assets) };
    }

    public async Task<ConnectedSystemTestDataView?> ViewAsync(string environmentId, string id, CancellationToken ct)
    {
        if (await integrations.GetAsync(environmentId, id, ct) is not { } integration) return null;
        var assets = (await catalog.ViewAsync(ct)).Assets;
        var requirements = await RequirementsAsync(integration, ct);
        return ConnectedSystemTestDataMatcher.View(await MigrateAsync(integration, requirements, assets, ct), requirements, assets);
    }

    /// <summary>
    /// Deterministic reference migration. (1) A binding saved with an alias only gets the stable id of the one asset that alias resolves to (none
    /// → left as is and shown as a missing reference; never a similarly named asset). (2) Once per document: a requirement without a binding whose
    /// asset the provider's readiness actually consumed (<see cref="IExternalProviderHandler.ConsumedTestDataAsync"/>) is bound to it, when that
    /// asset is a compatible candidate — so Consumers reflects real usage. Nothing is inferred from roles, names or "Applies to". Writes only
    /// when something changed.
    /// </summary>
    public async Task<ExternalIntegration> MigrateAsync(ExternalIntegration integration, IReadOnlyList<ConnectedSystemTestDataRequirement> requirements,
        IReadOnlyList<TestDataAssetSummary> assets, CancellationToken ct)
    {
        var bindings = (integration.TestDataBindings ?? []).ToList();
        var changed = false;
        for (var i = 0; i < bindings.Count; i++)
            if (bindings[i].AssetId is null && assets.Where(a => a.Alias == bindings[i].AssetAlias).ToList() is [var only])
            {
                bindings[i] = bindings[i] with { AssetId = only.Id };
                changed = true;
            }
        var version = integration.TestDataReferenceVersion;
        if ((version ?? 0) < ReferenceVersion)
        {
            var consumed = await integrations.Handler(integration.ProviderType).ConsumedTestDataAsync(integration, ct);
            foreach (var (requirementId, alias) in consumed)
            {
                if (bindings.Any(b => b.RequirementId == requirementId) || requirements.FirstOrDefault(r => r.Id == requirementId) is not { } requirement) continue;
                if (assets.Where(a => a.Alias == alias).ToList() is not [var asset]) continue;
                if (ConnectedSystemTestDataMatcher.Candidates(requirement, assets, integration.TargetEnvironmentId, integration.EnvironmentName).All(c => c.Alias != asset.Alias)) continue;
                var now = clock.GetUtcNow();
                bindings.Add(new(requirementId, asset.Alias)
                {
                    AssetId = asset.Id, BoundAt = now, UpdatedAt = now, BoundBy = ConnectedSystemTestDataBindingOrigins.Migration,
                    ContextMatched = ConnectedSystemTestDataMatcher.ContextMatches(asset, requirement),
                });
            }
            version = ReferenceVersion;
            changed = true;
        }
        if (!changed) return integration;
        var migrated = integration with { TestDataBindings = Ordered(bindings), TestDataReferenceVersion = version };
        await integrations.PersistAsync(migrated, ct);
        return migrated;
    }

    private static List<ConnectedSystemTestDataBinding>? Ordered(IEnumerable<ConnectedSystemTestDataBinding> bindings) =>
        bindings.Any() ? [.. bindings.OrderBy(b => b.RequirementId, StringComparer.Ordinal)] : null;

    /// <summary>
    /// Bind / change / clear. Each alias must be a compatible asset for its requirement (same semantic role and data type, usable in the environment;
    /// an asset without the matching "Applies to" may still be bound explicitly). The binding persists the asset's stable id. Clearing a required
    /// binding is allowed and shows as Missing.
    /// </summary>
    public async Task<(ConnectedSystemTestDataView? View, string? Problem)> UpdateAsync(string environmentId, string id, ConnectedSystemTestDataUpdate update, CancellationToken ct)
    {
        if (await integrations.GetAsync(environmentId, id, ct) is not { } integration) return (null, "The connected system does not exist.");
        var requirements = await RequirementsAsync(integration, ct);
        var assets = (await catalog.ViewAsync(ct)).Assets;
        var bindings = (integration.TestDataBindings ?? []).ToDictionary(b => b.RequirementId, StringComparer.Ordinal);
        foreach (var change in update.Changes ?? [])
        {
            if (requirements.FirstOrDefault(r => r.Id == change.RequirementId) is not { } requirement) return (null, $"{change.RequirementId} is not a Test Data requirement of this system.");
            if (string.IsNullOrWhiteSpace(change.AssetAlias)) { bindings.Remove(requirement.Id); continue; }
            if (assets.FirstOrDefault(a => a.Alias == change.AssetAlias) is not { } asset) return (null, $"{change.AssetAlias} does not exist in Test Data.");
            if (ConnectedSystemTestDataMatcher.Candidates(requirement, assets, integration.TargetEnvironmentId, integration.EnvironmentName).All(c => c.Alias != asset.Alias))
                return (null, $"{asset.Alias} is not compatible with {requirement.DisplayName}: it needs {TestDataSemanticRoles.Label(requirement.SemanticRole)} · "
                    + $"{TestDataDataTypes.Label(requirement.DataType)} usable in {integration.EnvironmentName ?? integration.TargetEnvironmentId}.");
            if (bindings.GetValueOrDefault(requirement.Id) is { } existing && existing.AssetId == asset.Id) continue;
            var now = clock.GetUtcNow();
            bindings[requirement.Id] = new(requirement.Id, asset.Alias)
            {
                AssetId = asset.Id, BoundAt = now, UpdatedAt = now, BoundBy = ConnectedSystemTestDataBindingOrigins.User,
                ContextMatched = ConnectedSystemTestDataMatcher.ContextMatches(asset, requirement),
            };
        }
        var saved = integration with { TestDataBindings = Ordered(bindings.Values) };
        await integrations.PersistAsync(saved, ct);
        return (ConnectedSystemTestDataMatcher.View(saved, requirements, assets), null);
    }
}

/// <summary>Reads one persisted Connected System Test Data binding (for provider readiness that must consume the binding, not a lookup).</summary>
public sealed class ConnectedSystemTestDataBindingReader(AppDbContext db)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ConnectedSystemTestDataBinding?> BindingAsync(string integrationId, string requirementId, CancellationToken ct) =>
        await db.ExternalIntegrations.AsNoTracking().SingleOrDefaultAsync(r => r.Id == integrationId, ct) is { } row
            ? JsonSerializer.Deserialize<ExternalIntegration>(row.DocumentJson, Json)?.TestDataBindings?.FirstOrDefault(b => b.RequirementId == requirementId)
            : null;
}

/// <summary>
/// Connected Systems as Test Data consumers: every persisted binding is a usage ("Used by"), keyed by the bound asset id, so Delete warns, Reset
/// reports it, and Replace re-points it. Suggestions are never usages. Reads the stored integration documents only.
/// </summary>
public sealed class ConnectedSystemTestDataReferences(AppDbContext db, TestDataCatalog? catalog = null) : ITestDataReferenceSource
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private async Task<List<(Models.ExternalIntegrationRecord Row, ExternalIntegration Integration)>> AllAsync(CancellationToken ct) =>
        [.. (await db.ExternalIntegrations.ToListAsync(ct)).Select(row => (row, JsonSerializer.Deserialize<ExternalIntegration>(row.DocumentJson, Json)))
            .Where(x => x.Item2?.TestDataBindings is { Count: > 0 }).Select(x => (x.row, x.Item2!))];

    public async Task<IReadOnlyList<TestDataAssetUsage>> UsagesAsync(CancellationToken ct) =>
        [.. (await AllAsync(ct)).SelectMany(x => x.Integration.TestDataBindings!.Select(b =>
            new TestDataAssetUsage(b.AssetAlias, ConnectedSystemTestDataMatcher.ConsumerType, x.Integration.Id, x.Integration.Name, RequirementLabel(b.RequirementId))
            { AssetId = b.AssetId, EnvironmentId = x.Integration.TargetEnvironmentId, EnvironmentName = x.Integration.EnvironmentName }))];

    /// <summary>"Reporter identity" from "reporter" (the requirement ids are stable provider ids).</summary>
    public static string RequirementLabel(string requirementId) => requirementId switch
    {
        AltinnTestDataRequirements.Reporter => "Reporter identity",
        AltinnTestDataRequirements.Child => "Child identity",
        AltinnTestDataRequirements.ReportPayload => "Valid report payload",
        _ => requirementId,
    };

    /// <summary>Re-points every binding of <paramref name="fromAlias"/> to <paramref name="toAlias"/> and its stable id, in one save.</summary>
    public async Task<int> ReplaceAsync(string fromAlias, string toAlias, CancellationToken ct)
    {
        var toId = catalog?.Assets().FirstOrDefault(a => a.Alias == toAlias)?.Id;
        var fromId = catalog?.Assets().FirstOrDefault(a => a.Alias == fromAlias)?.Id;
        bool Matches(ConnectedSystemTestDataBinding b) => b.AssetAlias == fromAlias && (b.AssetId is null || fromId is null || b.AssetId == fromId) || fromId is not null && b.AssetId == fromId;
        var changed = 0;
        foreach (var (row, integration) in await AllAsync(ct))
        {
            if (!integration.TestDataBindings!.Any(Matches)) continue;
            var updated = integration with { TestDataBindings = [.. integration.TestDataBindings!.Select(b => Matches(b) ? b with { AssetAlias = toAlias, AssetId = toId } : b)] };
            row.DocumentJson = JsonSerializer.Serialize(updated, Json);
            changed++;
        }
        if (changed > 0) await db.SaveChangesAsync(ct);
        return changed;
    }
}

/// <summary>The Altinn intake (Skolenærvær) Test Data requirements, from the Skolenærvær Test Pack's roles: Reporter and Child are required for
/// submission; when the system declares a form contract, the valid report payload (XML fixture) is required too — Altinn readiness (manual
/// submission preparation) already needs it, so it is part of TEST DATA readiness as a persisted binding, not a contract lookup.</summary>
public static class AltinnTestDataRequirements
{
    public const string Reporter = "reporter", Child = "child", ReportPayload = "report-payload";

    public static IReadOnlyList<ConnectedSystemTestDataRequirement> For(IReadOnlyDictionary<string, string> settings) =>
    [
        new(Reporter, "Reporter identity", TestDataSemanticRoles.Reporter, TestDataDataType.SimpleValues, true, [TestDataContexts.AltinnIntake, TestDataContexts.Skolenaervaer],
            "Which TestID reporter signs in and submits the report. The credential stays with authentication; this is the identity only."),
        new(Child, "Child identity", TestDataSemanticRoles.Child, TestDataDataType.SimpleValues, true, [TestDataContexts.AltinnIntake, TestDataContexts.SkolenaervaerReport],
            "The synthetic child the report is about (business identity in the report payload — never an authentication identity)."),
        .. settings.ContainsKey("formDataType")
            ? new[] { new ConnectedSystemTestDataRequirement(ReportPayload, "Valid report payload", TestDataSemanticRoles.ReportXml, TestDataDataType.XmlPayload, true,
                [TestDataContexts.SkolenaervaerReport], "The approved XML report fixture for the declared form, referenced by its stable Test Data id: a newly approved version is used without rebinding; a draft is not used until it is approved.") }
            : [],
    ];
}
