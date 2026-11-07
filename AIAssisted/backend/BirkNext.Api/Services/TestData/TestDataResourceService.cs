using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.SourceDomains;
using BirkNext.TestData;

namespace BirkNext.Api.Services.TestData;

/// <summary>
/// Add data / Edit data: validates with the shared rules (the editor's rules), then saves Draft versions. Never approves, binds or executes.
/// Aliases are unique across the whole catalog, not only among authored resources.
/// </summary>
public sealed class TestDataResourceService(AuthoredTestDataStore store, TestDataCatalog catalog, IReviewSourceEvidenceProvider? sources = null)
{
    public IReadOnlyList<TestDataResourceView> List() => [.. store.Resources().OrderBy(r => r.Alias, StringComparer.Ordinal).Select(View)];
    public TestDataResourceView? Get(string alias) => store.Find(alias) is { } r ? View(r) : null;

    public async Task<TestDataResourceValidation> ValidateAsync(TestDataResourceDraft draft, CancellationToken ct) =>
        TestDataResourceRules.Validate(draft, await ContextAsync(draft, ct));

    public async Task<TestDataResourceSaveResult> CreateAsync(TestDataResourceDraft draft, CancellationToken ct)
    {
        var validation = await ValidateAsync(draft, ct);
        if (validation.Blocking) return new(false, null, validation, "Fix the failed checks before saving.");
        if (catalog.Assets().Any(a => a.Alias == draft.Alias))
            return new(false, null, validation, $"\"{draft.Alias}\" is already used in Test Data. Choose another name.");
        var (outcome, resource) = store.Create(draft with { ExpectedVersion = null });
        return outcome == AuthoredTestDataStore.Outcome.Saved
            ? new(true, View(resource!), validation, "Saved as draft (version 1). It is not approved or bound to any consumer.")
            : new(false, null, validation, outcome == AuthoredTestDataStore.Outcome.AliasTaken ? $"\"{draft.Alias}\" already exists." : "The Test Data file could not be written; nothing was saved.");
    }

    /// <summary>Saves a new version (the alias never changes; the kind never changes, so consumers keep a stable shape).</summary>
    public async Task<TestDataResourceSaveResult> UpdateAsync(string alias, TestDataResourceDraft draft, CancellationToken ct)
    {
        if (store.Find(alias) is not { } existing) return new(false, null, new([]), "Not found.");
        draft = draft with { Alias = alias };
        if (draft.Content.Kind != existing.Current.Content.Kind)
            return new(false, View(existing), new([]), $"The data kind of {alias} is {TestDataResourceRules.Label(existing.Current.Content.Kind)}; use Duplicate to start a different kind.");
        var validation = await ValidateAsync(draft, ct);
        if (validation.Blocking) return new(false, View(existing), validation, "Fix the failed checks before saving.");
        if (AuthoredTestDataStore.Fingerprint(draft.Content) == existing.Current.Fingerprint && draft.Label == existing.Current.Label && draft.Description == existing.Current.Description
            && draft.EnvironmentId == existing.Current.EnvironmentId && draft.AppliesTo.SequenceEqual(existing.Current.AppliesTo))
            return new(false, View(existing), validation, "Nothing changed.");
        var (outcome, resource) = store.AddVersion(alias, draft);
        return outcome switch
        {
            AuthoredTestDataStore.Outcome.Saved => new(true, View(resource!), validation, $"Saved as version {resource!.Current.Version} (draft). Earlier versions are unchanged; an approved version stays in use until this one is approved."),
            AuthoredTestDataStore.Outcome.Conflict => new(false, View(resource!), validation, $"{alias} was changed elsewhere (now version {resource!.Current.Version}). Reload it and apply your change again.", Conflict: true),
            _ => new(false, null, validation, "The Test Data file could not be written; nothing was saved."),
        };
    }

    /// <summary>Copies the current content as a new resource (version 1, Draft). Approval and bindings are never copied.</summary>
    public async Task<TestDataResourceSaveResult> DuplicateAsync(string alias, string newAlias, CancellationToken ct)
    {
        if (store.Find(alias) is not { } existing) return new(false, null, new([]), "Not found.");
        var v = existing.Current;
        return await CreateAsync(new TestDataResourceDraft
        {
            Alias = newAlias, Label = v.Label, Description = v.Description, EnvironmentId = v.EnvironmentId, AppliesTo = v.AppliesTo, Content = v.Content,
            ChangeNote = $"Duplicated from {alias} v{v.Version}",
        }, ct);
    }

    /// <summary>REST (OpenAPI) or GraphQL operations of the newest source snapshot (or the given one) with their declared parameters.</summary>
    public async Task<IReadOnlyList<TestDataOperationOption>> OperationsAsync(string kind, Guid? snapshotId, CancellationToken ct)
    {
        var snapshot = await SnapshotAsync(snapshotId, ct);
        if (snapshot?.EvidenceDomains?.Contracts.Contracts is not { } contracts) return [];
        var graphQl = kind.Equals("graphql", StringComparison.OrdinalIgnoreCase);
        return [.. contracts.Where(c => graphQl ? c.Type is SourceContractType.GraphQlOperations or SourceContractType.GraphQlSchema : c.Type == SourceContractType.OpenApi)
            .SelectMany(c => c.Operations.Where(o => !graphQl || o.Kind is "query" or "mutation").Select(o => new TestDataOperationOption(snapshot.Id, c.Id, c.Name, c.Type.ToString(), o,
                graphQl ? GraphQlTypes(contracts, c) : c.Types)))
            .OrderBy(o => o.ContractName, StringComparer.Ordinal).ThenBy(o => o.Operation.Name, StringComparer.Ordinal).Take(1000)];
    }

    /// <summary>
    /// Whether a resource can be used for an operation now: contract checks of the version consumers would get (the approved one, else the
    /// latest) against the operation in the newest (or given) source snapshot, plus approval. Ready needs no failed check and an approved version.
    /// </summary>
    public async Task<TestDataResourceReadiness?> ReadinessAsync(string alias, TestDataOperationRef operation, CancellationToken ct)
    {
        if (store.Find(alias) is not { } r) return null;
        var asset = catalog.Assets().FirstOrDefault(a => a.Alias == alias && a.SourceId == AuthoredTestDataProvider.ProviderId);
        var v = r.Versions.FirstOrDefault(x => x.Version == asset?.Version) ?? r.Current;
        var draft = new TestDataResourceDraft { Alias = alias, Label = v.Label, EnvironmentId = v.EnvironmentId, Content = v.Content with { Operation = operation } };
        var validation = await ValidateAsync(draft, ct);
        var approved = asset?.Approval == TestDataApproval.Approved;
        var blockers = validation.Checks.Where(c => c.State == TestDataCheckState.Failed).Select(c => c.Message).ToList();
        if (!approved) blockers.Add("Not approved: approve a version in Test Data before a consumer uses it.");
        if (asset?.Deprecated == true) blockers.Add("Deprecated: not offered for new usage.");
        return new(alias, v.Version, approved, blockers.Count == 0, validation, blockers);
    }

    private async Task<TestDataValidationContext> ContextAsync(TestDataResourceDraft draft, CancellationToken ct)
    {
        var context = new TestDataValidationContext
        {
            KnownAliases = catalog.Assets().Select(a => a.Alias).ToHashSet(StringComparer.Ordinal),
            DatasetReferences = store.Resources().Where(r => r.Current.Content.Kind == TestDataResourceKind.Dataset)
                .ToDictionary(r => r.Alias, r => (IReadOnlyList<string>)[.. r.Current.Content.Members.Where(m => m.Reference is not null).Select(m => m.Reference!.Split('@')[0])], StringComparer.Ordinal),
        };
        if (draft.Content.Operation is not { } reference) return context;
        var snapshot = await SnapshotAsync(reference.SnapshotId, ct);
        var contracts = snapshot?.EvidenceDomains?.Contracts.Contracts ?? [];
        var contract = contracts.FirstOrDefault(c => c.Id == reference.ContractId);
        var op = contract?.Operations.FirstOrDefault(o => o.Name == reference.Operation);
        return op is null ? context with { OperationMissing = true }
            : context with { Operation = op, Types = contract!.Type is SourceContractType.GraphQlOperations or SourceContractType.GraphQlSchema ? GraphQlTypes(contracts, contract) : contract.Types };
    }

    /// <summary>Input and enum types for GraphQL validation: an operations document declares none, so the snapshot's schemas supply them.</summary>
    private static List<ContractTypeShape> GraphQlTypes(IReadOnlyList<SourceContract> contracts, SourceContract contract) =>
        [.. contract.Types.Concat(contracts.Where(c => c.Type == SourceContractType.GraphQlSchema && c.Id != contract.Id).SelectMany(c => c.Types))
            .GroupBy(t => t.Name, StringComparer.Ordinal).Select(g => g.First())];

    private async Task<BirkNext.Integrations.IqrSourceSnapshot?> SnapshotAsync(Guid? snapshotId, CancellationToken ct)
    {
        if (sources is null || !sources.SourceAnalysisEnabled) return null;
        return snapshotId is { } id ? await sources.ResolveAsync(string.Empty, id, ct) : (await sources.ListAsync(string.Empty, ct)).FirstOrDefault();
    }

    private TestDataResourceView View(AuthoredTestDataResource r) => new()
    {
        Alias = r.Alias, AssetId = catalog.Assets().FirstOrDefault(a => a.Alias == r.Alias && a.SourceId == AuthoredTestDataProvider.ProviderId)?.AssetId,
        Label = r.Current.Label, Description = r.Current.Description, EnvironmentId = r.Current.EnvironmentId, AppliesTo = r.Current.AppliesTo, Content = r.Current.Content,
        Version = r.Current.Version, CreatedAt = r.CreatedAt, UpdatedAt = r.Current.CreatedAt,
        Versions = [.. r.Versions.Select(v => new TestDataResourceVersionInfo(v.Version, v.CreatedAt, v.Fingerprint[..12], v.ChangeNote))],
    };
}

/// <summary>Delete and Reset Test Data for authored resources (through the lifecycle service like every other local asset).</summary>
public sealed class AuthoredTestDataEditor(AuthoredTestDataStore store) : ITestDataAssetEditor
{
    public string ProviderId => AuthoredTestDataProvider.ProviderId;
    public IReadOnlyList<string> RoleCapabilities => [];
    public string? Role(TestDataAsset asset) => null;
    public string? Edit(TestDataAsset asset, string? label, string? value, DateTimeOffset now) => "Added data is edited with Edit data, which saves a new version.";
    public bool CanRestoreDefault(TestDataAsset asset) => false;
    public string? RestoreDefault(TestDataAsset asset, DateTimeOffset now, out bool changed) { changed = false; return "Added data has no default value."; }
    public bool Remove(TestDataAsset asset) => store.Remove(asset.Alias);
    public int ClearLocal() { var all = store.Resources(); return all.Count(r => store.Remove(r.Alias)); }
}
