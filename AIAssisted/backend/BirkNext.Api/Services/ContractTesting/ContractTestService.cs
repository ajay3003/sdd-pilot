using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.ApiQuality;
using BirkNext.Api.Services.ExecutionEvidence;
using BirkNext.Api.Services.TestData;
using BirkNext.ApiReview;
using BirkNext.ContractTesting;
using BirkNext.ExecutionEvidence;
using BirkNext.LocalHttpsProxy;
using BirkNext.SourceDomains;
using BirkNext.TestData;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.ContractTesting;

/// <summary>
/// Contract tests: drafts generated from a contract version's operations, bound to Test Data pinned by version, checked for readiness against
/// the contract, the Test Data and the selected Target Environment, and run through <see cref="ExecutionRunService"/> — the same safety policy,
/// mutating-call confirmation and evidence capture as every execution run. Results are read back from Execution Evidence by definition reference.
/// </summary>
public sealed class ContractTestService(AppDbContext db, ContractRegistryService registry, ExecutionEvidenceStore evidence, ExecutionRunService runs,
    IApiEnvironmentSafetyPolicy safety, AuthoredTestDataStore authored, TestDataCatalog catalog, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private static readonly string[] DatasetKinds = [nameof(TestDataResourceKind.Dataset), nameof(TestDataResourceKind.KeyValue)];

    /// <summary>Drafts for the given operations (all REST operations when none are named) of a contract version. An operation that already has a
    /// test of the same kind is skipped, so generating twice never duplicates.</summary>
    public async Task<(List<ContractTestDefinition> Created, List<string> Skipped)> GenerateAsync(string key, int? version, IReadOnlyCollection<string>? operations, CancellationToken ct)
    {
        if (await db.ApiContracts.AsNoTracking().FirstOrDefaultAsync(c => c.Key == key, ct) is not { } contract) return ([], [$"Contract {key} does not exist."]);
        var v = version ?? await registry.LatestVersionAsync(key, ct) ?? 0;
        if (await registry.DocumentAsync(key, v, ct) is not { } doc) return ([], [$"Version {v} of {key} does not exist."]);
        var existing = (await db.ContractTests.AsNoTracking().Where(t => t.ContractId == contract.Id).Select(t => new { t.Operation, t.Kind }).ToListAsync(ct))
            .Select(t => (t.Operation, t.Kind)).ToHashSet();
        var created = new List<ContractTestDefinition>(); var skipped = new List<string>();
        var now = _clock.GetUtcNow();
        foreach (var op in doc.Operations.Where(o => operations is not { Count: > 0 } || operations.Contains(o.Name)))
        {
            if (!ContractTestRules.IsRest(op)) { skipped.Add($"{op.Name}: not a REST operation."); continue; }
            foreach (var test in ContractTestRules.Generate(doc, key, v, op, now))
            {
                if (existing.Contains((test.Operation, test.Kind.ToString()))) { skipped.Add($"{test.Name}: already exists."); continue; }
                db.ContractTests.Add(Row(contract.Id, test));
                created.Add(test);
            }
        }
        await db.SaveChangesAsync(ct);
        return (created, skipped);
    }

    public async Task<IReadOnlyList<ContractTestView>> ListAsync(string key, ContractTestEnvironment? environment, CancellationToken ct)
    {
        if (await db.ApiContracts.AsNoTracking().FirstOrDefaultAsync(c => c.Key == key, ct) is not { } contract) return [];
        var tests = (await db.ContractTests.AsNoTracking().Where(t => t.ContractId == contract.Id).OrderBy(t => t.Operation).ThenBy(t => t.Kind).Select(t => t.DocumentJson).ToListAsync(ct))
            .Select(d => JsonSerializer.Deserialize<ContractTestDefinition>(d, ExecutionEvidenceStore.Json)!).ToList();
        var docs = new Dictionary<int, SourceContract?>();
        var latest = await registry.LatestVersionAsync(key, ct) ?? 0;
        var views = new List<ContractTestView>();
        foreach (var t in tests) views.Add(await ViewAsync(t, latest, docs, environment, ct));
        return views;
    }

    public async Task<ContractTestView?> GetAsync(Guid id, ContractTestEnvironment? environment, CancellationToken ct) =>
        await LoadAsync(id, ct) is { } t ? await ViewAsync(t, await registry.LatestVersionAsync(t.ContractKey, ct) ?? 0, [], environment, ct) : null;

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        if (await db.ContractTests.FirstOrDefaultAsync(t => t.Id == id, ct) is not { } row) return false;
        db.ContractTests.Remove(row);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// The datasets the picker offers for a test: authored datasets and key-value collections with an approved version (environment-scoped ones
    /// only for that environment), each with the values it provides and the required values it lacks. Compatible = nothing required missing.
    /// </summary>
    public async Task<IReadOnlyList<ContractDatasetOption>> DatasetOptionsAsync(Guid id, string? environmentId, CancellationToken ct)
    {
        if (await LoadAsync(id, ct) is not { } test) return [];
        var assets = catalog.Assets().Where(a => a.SourceId == AuthoredTestDataProvider.ProviderId).ToDictionary(a => a.Alias, StringComparer.Ordinal);
        var options = new List<ContractDatasetOption>();
        foreach (var r in authored.Resources().Where(r => DatasetKinds.Contains(r.Current.Content.Kind.ToString())))
        {
            if (r.Current.EnvironmentId is { } env && environmentId is not null && env != environmentId) continue;
            assets.TryGetValue(r.Alias, out var asset);
            var approved = asset?.Approval == TestDataApproval.Approved ? asset.Version : null;
            var version = approved is { } a ? r.Versions.FirstOrDefault(x => x.Version == a) ?? r.Current : r.Current;
            var (values, problem) = Values(version.Content, r.Alias);
            var (missingRequired, missingOptional) = ContractTestRules.Missing(test, values.Keys.ToHashSet(StringComparer.Ordinal));
            problem ??= approved is null ? "No approved version: approve it in Test Data first." : asset?.Deprecated == true ? "Deprecated." : null;
            options.Add(new(r.Alias, version.Label, TestDataResourceRules.Label(r.Current.Content.Kind), approved, r.Current.Version,
                problem is null && missingRequired.Count == 0, [.. values.Keys.Order(StringComparer.Ordinal)], missingRequired, missingOptional, r.Current.EnvironmentId, problem));
        }
        return [.. options.OrderByDescending(o => o.Compatible).ThenBy(o => o.MissingRequired.Count).ThenBy(o => o.Alias, StringComparer.Ordinal)];
    }

    /// <summary>Binds a dataset pinned to its approved version. Changing the binding is a new revision of the test (evidence tells them apart).</summary>
    public async Task<(ContractTestDefinition? Test, string? Error)> BindAsync(Guid id, ContractTestBindRequest bind, CancellationToken ct)
    {
        if (await db.ContractTests.FirstOrDefaultAsync(t => t.Id == id, ct) is not { } row) return (null, "Not found.");
        var test = JsonSerializer.Deserialize<ContractTestDefinition>(row.DocumentJson, ExecutionEvidenceStore.Json)!;
        if (authored.Find(bind.Alias) is not { } resource || !DatasetKinds.Contains(resource.Current.Content.Kind.ToString()))
            return (null, $"\"{bind.Alias}\" is not a dataset or key-value collection added in Test Data.");
        var asset = catalog.Assets().FirstOrDefault(a => a.Alias == bind.Alias && a.SourceId == AuthoredTestDataProvider.ProviderId);
        if (asset?.Approval != TestDataApproval.Approved || asset.Version != bind.Version)
            return (null, $"Only the approved version of {bind.Alias} can be pinned{(asset?.Approval == TestDataApproval.Approved ? $" (v{asset.Version})" : " — it has none")}.");
        if (asset.Deprecated) return (null, $"{bind.Alias} is deprecated.");
        test = test with { Binding = new(bind.Alias, bind.Version), Revision = test.Revision + 1, UpdatedAt = _clock.GetUtcNow() };
        Update(row, test);
        await db.SaveChangesAsync(ct);
        return (test, null);
    }

    /// <summary>
    /// Re-derives a test from the latest contract version after a change (keeps its id, name, kind and Test Data binding; new revision). A test
    /// whose operation no longer exists cannot be regenerated.
    /// </summary>
    public async Task<(ContractTestDefinition? Test, string? Error)> RegenerateAsync(Guid id, CancellationToken ct)
    {
        if (await db.ContractTests.FirstOrDefaultAsync(t => t.Id == id, ct) is not { } row) return (null, "Not found.");
        var test = JsonSerializer.Deserialize<ContractTestDefinition>(row.DocumentJson, ExecutionEvidenceStore.Json)!;
        var latest = await registry.LatestVersionAsync(test.ContractKey, ct) ?? 0;
        if (await registry.DocumentAsync(test.ContractKey, latest, ct) is not { } doc || doc.Operations.FirstOrDefault(o => o.Name == test.Operation) is not { } op)
            return (null, $"{test.Operation} does not exist in v{latest}; delete the test or generate one for another operation.");
        var fresh = ContractTestRules.Generate(doc, test.ContractKey, latest, op, _clock.GetUtcNow()).FirstOrDefault(t => t.Kind == test.Kind);
        if (fresh is null) return (null, $"v{latest} no longer gives {test.Operation} a {test.Kind} test (no required value to omit).");
        test = fresh with { Id = test.Id, Name = test.Name, Binding = test.Binding, Revision = test.Revision + 1, CreatedAt = test.CreatedAt };
        Update(row, test);
        await db.SaveChangesAsync(ct);
        return (test, null);
    }

    /// <summary>
    /// Runs the selected tests that are Ready for the environment, as one Execution Evidence run. Each call carries its operation reference,
    /// the pinned Test Data reference and its definition revision. Tests that are not ready are listed, never sent.
    /// </summary>
    public async Task<ContractTestRunResult> RunAsync(ContractTestRunRequest request, CancellationToken ct)
    {
        var notRun = new List<string>();
        var basePath = (request.BasePath ?? "").Trim().TrimEnd('/');
        if (basePath.Length > 0 && (!basePath.StartsWith('/') || basePath.StartsWith("//", StringComparison.Ordinal) || basePath.Contains("://", StringComparison.Ordinal)
            || basePath.IndexOfAny(['\\', '?', '#']) >= 0))
            return new(null, ["The API base path is a path inside the Target Environment (starts with a single '/', no host, query or fragment)."]);
        var calls = new List<ExecutionCallDefinition>();
        var contractRefs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in request.TestIds.Distinct())
        {
            if (await LoadAsync(id, ct) is not { } test) { notRun.Add($"{id}: not found."); continue; }
            var latest = await registry.LatestVersionAsync(test.ContractKey, ct) ?? 0;
            var view = await ViewAsync(test, latest, [], request.Environment, ct);
            if (view.Readiness.State != ContractTestReadinessState.Ready)
            { notRun.Add($"{test.Name}: {string.Join(" ", view.Readiness.Items.Where(i => i.State is ReadinessItemState.Missing or ReadinessItemState.Blocked).Select(i => i.Text))}"); continue; }
            var (values, _) = BoundValues(test);
            var (call, missing) = ContractTestRules.Materialize(test, values);
            if (call is null) { notRun.Add($"{test.Name}: missing {string.Join(", ", missing)}."); continue; }
            calls.Add(call with
            {
                Path = basePath + call.Path, Name = test.Name, OperationRef = ContractIdentity.OperationRef(test.ContractKey, test.ContractVersion, test.Operation),
                TestDataRefs = test.Binding is { } b ? [$"{b.Alias}@v{b.Version}"] : [], DefinitionVersion = ContractIdentity.DefinitionRef(test.Id, test.Revision),
            });
            contractRefs.Add(ContractIdentity.ContractRef(test.ContractKey, test.ContractVersion));
        }
        if (calls.Count == 0) return new(null, notRun.Count > 0 ? notRun : ["Select at least one test."]);
        var e = request.Environment;
        var started = await runs.StartAsync(new ExecutionRunRequest
        {
            Name = string.IsNullOrWhiteSpace(request.Name) ? $"Contract tests · {string.Join(", ", contractRefs.Order(StringComparer.Ordinal))}" : request.Name.Trim(),
            EnvironmentId = e.EnvironmentId, TargetUrl = e.TargetUrl, EnvironmentType = e.EnvironmentType, IsProduction = e.IsProduction, ProfileId = e.ProfileId,
            ContextFingerprint = e.ContextFingerprint, Calls = calls, ContractRefs = [.. contractRefs.Order(StringComparer.Ordinal)], ConfirmMutatingCalls = request.ConfirmMutatingCalls,
            CapturePolicy = CapturePolicy.BoundedBody,
        }, "Local session", ct);
        return new(started, notRun);
    }

    public async Task<ContractVersionDiff?> DiffAsync(string key, int from, int to, CancellationToken ct)
    {
        if (await registry.DocumentAsync(key, from, ct) is not { } before || await registry.DocumentAsync(key, to, ct) is not { } after) return null;
        var impacts = new List<ContractTestImpact>();
        foreach (var t in (await ListDefinitionsAsync(key, ct)).Where(t => t.ContractVersion == from))
            impacts.Add(ContractTestRules.Impact(t, before, after, to));
        return new(key, from, to, ContractRegistryService.Changes(before, after), impacts);
    }

    private async Task<List<ContractTestDefinition>> ListDefinitionsAsync(string key, CancellationToken ct) =>
        (await db.ContractTests.AsNoTracking().Join(db.ApiContracts.AsNoTracking().Where(c => c.Key == key), t => t.ContractId, c => c.Id, (t, c) => t.DocumentJson).ToListAsync(ct))
            .Select(d => JsonSerializer.Deserialize<ContractTestDefinition>(d, ExecutionEvidenceStore.Json)!).ToList();

    private async Task<ContractTestView> ViewAsync(ContractTestDefinition test, int latest, Dictionary<int, SourceContract?> docs, ContractTestEnvironment? environment, CancellationToken ct)
    {
        async Task<SourceContract?> Doc(int v) { if (!docs.TryGetValue(v, out var d)) docs[v] = d = await registry.DocumentAsync(test.ContractKey, v, ct); return d; }
        var current = await Doc(test.ContractVersion);
        var newest = latest == test.ContractVersion ? current : await Doc(latest);
        var impact = current is not null && newest is not null && latest != test.ContractVersion ? ContractTestRules.Impact(test, current, newest, latest) : null;
        var op = (newest ?? current)?.Operations.FirstOrDefault(o => o.Name == test.Operation) ?? current?.Operations.FirstOrDefault(o => o.Name == test.Operation);
        var readiness = Readiness(test, latest, impact, op, environment);
        ContractTestLastResult? last = null;
        if (await evidence.LatestByDefinitionAsync(ContractIdentity.DefinitionPrefix(test.Id), ct) is { } hit)
            last = new(hit.RunId, hit.Call.CallId, hit.Call.State, hit.Call.Attempts.LastOrDefault()?.Response?.StatusCode, hit.Call.CompletedAt ?? hit.StartedAt,
                hit.Call.Definition.DefinitionVersion, hit.Call.Definition.OperationRef, hit.Call.Definition.TestDataRefs);
        return new(test, readiness, impact, last);
    }

    private ContractTestReadiness Readiness(ContractTestDefinition test, int latest, ContractTestImpact? impact, ContractOperation? op, ContractTestEnvironment? environment)
    {
        var items = new List<ContractReadinessItem>();
        var missingValues = new List<string>();
        // Contract
        if (impact is null) items.Add(new("Contract", ReadinessItemState.Ok, $"Generated from v{test.ContractVersion}, the latest version."));
        else items.Add(impact.Level switch
        {
            ContractTestImpactLevel.Broken => new("Contract", ReadinessItemState.Blocked, $"v{latest} removed {test.Operation}. {string.Join(" ", impact.Reasons)}"),
            ContractTestImpactLevel.NeedsUpdate => new("Contract", ReadinessItemState.Missing, $"v{latest} changed {test.Operation}: {string.Join(" ", impact.Reasons)} Regenerate the test from v{latest}."),
            ContractTestImpactLevel.Review => new("Contract", ReadinessItemState.Warning, $"v{latest} changed the response: {string.Join(" ", impact.Reasons)}"),
            _ => new("Contract", ReadinessItemState.Ok, $"v{latest} does not affect this test (generated from v{test.ContractVersion}). {string.Join(" ", impact.Reasons)}"),
        });
        // Test Data
        if (test.Inputs.Count == 0) items.Add(new("Test Data", ReadinessItemState.Ok, "The operation needs no input values."));
        else if (test.Binding is not { } b)
        {
            missingValues.AddRange(test.Inputs.Where(i => i.Required).Select(i => i.Name));
            items.Add(new("Test Data", ReadinessItemState.Missing, $"Select a dataset that provides {string.Join(", ", test.Inputs.Where(i => i.Required).Select(i => i.Name).DefaultIfEmpty("the optional values"))}."));
        }
        else
        {
            var (values, problem) = BoundValues(test);
            var asset = catalog.Assets().FirstOrDefault(a => a.Alias == b.Alias && a.SourceId == AuthoredTestDataProvider.ProviderId);
            if (problem is not null) items.Add(new("Test Data", ReadinessItemState.Blocked, problem));
            else if (asset?.Deprecated == true) items.Add(new("Test Data", ReadinessItemState.Blocked, $"{b.Alias} is deprecated."));
            else
            {
                var (required, _) = ContractTestRules.Missing(test, values.Keys.ToHashSet(StringComparer.Ordinal));
                missingValues.AddRange(required);
                if (required.Count > 0) items.Add(new("Test Data", ReadinessItemState.Missing, $"{b.Alias}@v{b.Version} lacks required value(s): {string.Join(", ", required)}."));
                else if (asset?.Approval == TestDataApproval.Approved && asset.Version != b.Version)
                    items.Add(new("Test Data", ReadinessItemState.Warning, $"Pinned to {b.Alias}@v{b.Version}; v{asset.Version} is now the approved version. Runs keep using v{b.Version} until you pin again."));
                else items.Add(new("Test Data", ReadinessItemState.Ok, $"{b.Alias}@v{b.Version} provides every required value."));
            }
        }
        // Target Environment (backend safety policy — never relaxed here)
        if (environment is null || string.IsNullOrWhiteSpace(environment.EnvironmentId))
            items.Add(new("Target Environment", ReadinessItemState.Missing, "Select a Target Environment."));
        else
        {
            var decision = safety.Evaluate(new ApiReviewRunRequest
            {
                Environment = new() { EnvironmentId = environment.EnvironmentId, EnvironmentType = environment.EnvironmentType, TargetUrl = environment.TargetUrl, IsProduction = environment.IsProduction },
                Identity = new(AuthenticatedTestingMethod.ManagedEdgeCdp, environment.ProfileId, environment.ContextFingerprint),
            });
            items.Add(decision.ActiveTestingAllowed ? new("Target Environment", ReadinessItemState.Ok, $"{decision.EnvironmentType ?? environment.EnvironmentType}: active testing allowed.")
                : new("Target Environment", ReadinessItemState.Blocked, decision.Reason));
        }
        // Authentication: runs are anonymous; an operation that declares security is not run without it.
        if (op?.Security is { Count: > 0 } security)
            items.Add(new("Authentication", ReadinessItemState.Blocked, $"The operation requires {string.Join(", ", security)}. Contract test runs cannot use the Target Environment's credential yet, so it is not run anonymously."));
        else items.Add(new("Authentication", ReadinessItemState.Ok, "The contract declares no security requirement for this operation."));
        if (ExecutionRunService.Mutating(test.Template))
            items.Add(new("Changes data", ReadinessItemState.Warning, $"{test.Template.Method} can change data: it runs only when you confirm data-changing calls for the run."));
        var state = items.Any(i => i.State == ReadinessItemState.Blocked) ? ContractTestReadinessState.Blocked
            : items.Any(i => i.State == ReadinessItemState.Missing) ? ContractTestReadinessState.NotReady : ContractTestReadinessState.Ready;
        return new(state, items, missingValues);
    }

    /// <summary>The bound dataset's values at the pinned version (never the latest unless that is the pinned one).</summary>
    private (Dictionary<string, TestDataTypedEntry> Values, string? Problem) BoundValues(ContractTestDefinition test)
    {
        if (test.Binding is not { } b) return (new(StringComparer.Ordinal), null);
        if (authored.Find(b.Alias) is not { } r) return (new(StringComparer.Ordinal), $"{b.Alias} no longer exists in Test Data.");
        if (r.Versions.FirstOrDefault(v => v.Version == b.Version) is not { } version) return (new(StringComparer.Ordinal), $"{b.Alias} has no version {b.Version}.");
        return Values(version.Content, b.Alias);
    }

    /// <summary>Name → typed value of a dataset (inline members, members referencing an added single value at its pinned or approved version) or a
    /// key-value collection. A member that cannot be resolved is absent (and reported as missing), never a default.</summary>
    private (Dictionary<string, TestDataTypedEntry> Values, string? Problem) Values(TestDataResourceContent content, string alias)
    {
        var values = new Dictionary<string, TestDataTypedEntry>(StringComparer.Ordinal);
        if (content.Kind == TestDataResourceKind.KeyValue)
        {
            foreach (var e in content.Entries.Where(e => TestDataResourceRules.TypeError(e.Type, e.Value, e.EnumValues) is null)) values.TryAdd(e.Name, e);
            return (values, null);
        }
        foreach (var m in content.Members)
        {
            if (m.Inline is { } inline) { values.TryAdd(m.Name, inline with { Name = m.Name }); continue; }
            if (m.Reference is not { } reference) continue;
            var parts = reference.Split('@');
            if (authored.Find(parts[0]) is not { } target) continue;
            var pinned = parts.Length > 1 && int.TryParse(parts[1].TrimStart('v'), out var n) ? n
                : catalog.Assets().FirstOrDefault(a => a.Alias == parts[0] && a.SourceId == AuthoredTestDataProvider.ProviderId && a.Approval == TestDataApproval.Approved)?.Version;
            if (pinned is null || target.Versions.FirstOrDefault(v => v.Version == pinned) is not { Content.Kind: TestDataResourceKind.SingleValue, Content.Value: { } value }) continue;
            values.TryAdd(m.Name, value with { Name = m.Name });
        }
        return (values, null);
    }

    private async Task<ContractTestDefinition?> LoadAsync(Guid id, CancellationToken ct) =>
        await db.ContractTests.AsNoTracking().Where(t => t.Id == id).Select(t => t.DocumentJson).FirstOrDefaultAsync(ct) is { } d
            ? JsonSerializer.Deserialize<ContractTestDefinition>(d, ExecutionEvidenceStore.Json) : null;

    private static ContractTestRow Row(Guid contractId, ContractTestDefinition t) => new()
    {
        Id = t.Id, ContractId = contractId, Operation = t.Operation, Name = t.Name.Length > 300 ? t.Name[..300] : t.Name, Kind = t.Kind.ToString(), Revision = t.Revision,
        DocumentJson = JsonSerializer.Serialize(t, ExecutionEvidenceStore.Json), CreatedAt = t.CreatedAt, UpdatedAt = t.UpdatedAt,
    };

    private static void Update(ContractTestRow row, ContractTestDefinition t)
    {
        row.Operation = t.Operation; row.Name = t.Name.Length > 300 ? t.Name[..300] : t.Name; row.Revision = t.Revision; row.UpdatedAt = t.UpdatedAt;
        row.DocumentJson = JsonSerializer.Serialize(t, ExecutionEvidenceStore.Json);
    }
}
