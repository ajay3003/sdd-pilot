using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Services.TestData;
using BirkNext.Integrations;
using BirkNext.TestData;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.ExternalIntegrations;

/// <summary>
/// Connected Systems ↔ Test Data: the provider's requirements, the explicit bindings stored on the integration (aliases only — never a value)
/// and deterministic suggestions from Test Data metadata (semantic role, data type, "Applies to", scope, approval). Reading never writes;
/// a suggestion is only persisted by an explicit Apply, an existing binding always wins and is never replaced by a newer suggestion.
/// </summary>
public sealed class ConnectedSystemTestDataService(ExternalIntegrationService integrations, TestDataCatalogService catalog, TimeProvider clock)
{
    public async Task<IReadOnlyList<ConnectedSystemTestDataRequirement>> RequirementsAsync(ExternalIntegration integration, CancellationToken ct) =>
        await integrations.Handler(integration.ProviderType).TestDataRequirementsAsync(integration, ct);

    /// <summary>The Test Data section of every view (one catalog read for all of them).</summary>
    public async Task<ExternalIntegrationsView> AttachAsync(ExternalIntegrationsView view, CancellationToken ct)
    {
        if (view.Integrations.Count == 0) return view;
        var assets = (await catalog.ViewAsync(ct)).Assets;
        var list = new List<ExternalIntegrationView>();
        foreach (var item in view.Integrations) list.Add(item with { TestData = ConnectedSystemTestDataMatcher.View(item.Integration, await RequirementsAsync(item.Integration, ct), assets) });
        return view with { Integrations = list };
    }

    public async Task<ExternalIntegrationView> AttachAsync(ExternalIntegrationView view, CancellationToken ct) =>
        view with { TestData = ConnectedSystemTestDataMatcher.View(view.Integration, await RequirementsAsync(view.Integration, ct), (await catalog.ViewAsync(ct)).Assets) };

    public async Task<ConnectedSystemTestDataView?> ViewAsync(string environmentId, string id, CancellationToken ct) =>
        await integrations.GetAsync(environmentId, id, ct) is { } integration
            ? ConnectedSystemTestDataMatcher.View(integration, await RequirementsAsync(integration, ct), (await catalog.ViewAsync(ct)).Assets) : null;

    /// <summary>
    /// Bind / change / clear. Each alias must be a compatible asset for its requirement (same semantic role and data type, usable in the environment;
    /// an asset without the matching "Applies to" may still be bound explicitly). Clearing a required binding is allowed and shows as Missing.
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
            if (bindings.GetValueOrDefault(requirement.Id)?.AssetAlias == asset.Alias) continue;
            bindings[requirement.Id] = new(requirement.Id, asset.Alias) { BoundAt = clock.GetUtcNow(), ContextMatched = ConnectedSystemTestDataMatcher.ContextMatches(asset, requirement) };
        }
        var saved = integration with { TestDataBindings = bindings.Count == 0 ? null : [.. bindings.Values.OrderBy(b => b.RequirementId, StringComparer.Ordinal)] };
        await integrations.PersistAsync(saved, ct);
        return (ConnectedSystemTestDataMatcher.View(saved, requirements, assets), null);
    }
}

/// <summary>
/// Connected Systems as Test Data consumers: every explicit binding is a usage ("Used by"), so Delete warns, Reset reports it, and Replace
/// re-points it. Reads the stored integration documents only.
/// </summary>
public sealed class ConnectedSystemTestDataReferences(AppDbContext db) : ITestDataReferenceSource
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private async Task<List<(Models.ExternalIntegrationRecord Row, ExternalIntegration Integration)>> AllAsync(CancellationToken ct) =>
        [.. (await db.ExternalIntegrations.ToListAsync(ct)).Select(row => (row, JsonSerializer.Deserialize<ExternalIntegration>(row.DocumentJson, Json)))
            .Where(x => x.Item2?.TestDataBindings is { Count: > 0 }).Select(x => (x.row, x.Item2!))];

    public async Task<IReadOnlyList<TestDataAssetUsage>> UsagesAsync(CancellationToken ct) =>
        [.. (await AllAsync(ct)).SelectMany(x => x.Integration.TestDataBindings!.Select(b =>
            new TestDataAssetUsage(b.AssetAlias, ConnectedSystemTestDataMatcher.ConsumerType, x.Integration.Id, x.Integration.Name, RequirementLabel(b.RequirementId))
            { EnvironmentId = x.Integration.TargetEnvironmentId }))];

    /// <summary>"Reporter identity" from "reporter" (the requirement ids are stable provider ids).</summary>
    public static string RequirementLabel(string requirementId) => requirementId switch
    {
        AltinnTestDataRequirements.Reporter => "Reporter identity",
        AltinnTestDataRequirements.Child => "Child identity",
        AltinnTestDataRequirements.ReportPayload => "Report payload (XML fixture)",
        _ => requirementId,
    };

    public async Task<int> ReplaceAsync(string fromAlias, string toAlias, CancellationToken ct)
    {
        var changed = 0;
        foreach (var (row, integration) in await AllAsync(ct))
        {
            if (integration.TestDataBindings!.All(b => b.AssetAlias != fromAlias)) continue;
            var updated = integration with { TestDataBindings = [.. integration.TestDataBindings!.Select(b => b.AssetAlias == fromAlias ? b with { AssetAlias = toAlias } : b)] };
            row.DocumentJson = JsonSerializer.Serialize(updated, Json);
            changed++;
        }
        if (changed > 0) await db.SaveChangesAsync(ct);
        return changed;
    }
}

/// <summary>The Altinn intake (Skolenærvær) Test Data requirements, from the Skolenærvær Test Pack's roles: Reporter and Child are required for
/// submission; the report payload fixture applies only when the system declares a form contract, and is optional (it can be generated).</summary>
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
            ? new[] { new ConnectedSystemTestDataRequirement(ReportPayload, "Report payload (XML fixture)", TestDataSemanticRoles.ReportXml, TestDataDataType.XmlPayload, false,
                [TestDataContexts.SkolenaervaerReport], "An approved XML report fixture for the declared form. Needed for automated submission (see Automated execution); optional for manual reporter login, and a fixture can also be generated for the run.") }
            : [],
    ];
}
