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
    IApiEnvironmentSafetyPolicy safety, AuthoredTestDataStore authored, TestDataCatalog catalog, TimeProvider? clock = null, ExecutionCredentialResolver? credentials = null)
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
            if (!ContractTestRules.IsRest(op) && !ContractTestRules.IsGraphQl(op))
            { skipped.Add(op.Kind == "subscription" ? $"{op.Name}: subscriptions need a streaming transport, which contract test runs do not support." : $"{op.Name}: not a REST operation or GraphQL query/mutation."); continue; }
            foreach (var test in ContractTestRules.GenerateAny(doc, key, v, op, now))
            {
                if (existing.Contains((test.Operation, test.Kind.ToString()))) { skipped.Add($"{test.Name}: already exists."); continue; }
                db.ContractTests.Add(Row(contract.Id, test));
                db.ContractTestRevisions.Add(RevisionRow(test, $"Generated from v{v}."));
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
        await PersistAsync(row, test, $"Test Data pinned to {bind.Alias}@v{bind.Version}.", ct);
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
        var fresh = ContractTestRules.GenerateAny(doc, test.ContractKey, latest, op, _clock.GetUtcNow()).FirstOrDefault(t => t.Kind == test.Kind);
        if (fresh is null) return (null, $"v{latest} no longer gives {test.Operation} a {test.Kind} test (no required value to omit).");
        test = fresh with { Id = test.Id, Name = test.Name, Description = test.Description, Binding = test.Binding, Revision = test.Revision + 1, CreatedAt = test.CreatedAt };
        await PersistAsync(row, test, $"Regenerated from contract v{latest}.", ct);
        return (test, null);
    }

    /// <summary>
    /// Runs the selected tests that are Ready for the environment, as one Execution Evidence run. Each call carries its operation reference,
    /// the pinned Test Data reference and its definition revision. Tests that are not ready are listed, never sent.
    /// </summary>
    public async Task<ContractTestRunResult> RunAsync(ContractTestRunRequest request, CancellationToken ct)
    {
        var notRun = new List<string>();
        if (PathProblem(request.BasePath, "The API base path") is { } baseProblem) return new(null, [baseProblem]);
        if (PathProblem(request.GraphQlPath, "The GraphQL endpoint path") is { } gqlProblem) return new(null, [gqlProblem]);
        var calls = new List<ExecutionCallDefinition>();
        var contractRefs = new HashSet<string>(StringComparer.Ordinal);
        var kinds = new HashSet<ExecutionAuthenticationKind>();
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
            var binding = await BindingAsync(test.ContractKey, request.Environment.EnvironmentId, ct);
            kinds.Add(binding?.Authentication ?? ExecutionAuthenticationKind.None);
            var basePath = (request.BasePath ?? binding?.BasePath ?? "").Trim().TrimEnd('/');
            var path = call.Protocol == ExecutionProtocol.GraphQl && (request.GraphQlPath ?? binding?.GraphQlPath) is { Length: > 0 } endpoint ? endpoint.Trim() : basePath + call.Path;
            calls.Add(call with
            {
                Path = path, Name = test.Name, OperationRef = ContractIdentity.OperationRef(test.ContractKey, test.ContractVersion, test.Operation),
                TestDataRefs = test.Binding is { } b ? [$"{b.Alias}@v{b.Version}"] : [], DefinitionVersion = ContractIdentity.DefinitionRef(test.Id, test.Revision),
            });
            contractRefs.Add(ContractIdentity.ContractRef(test.ContractKey, test.ContractVersion));
        }
        if (calls.Count == 0) return new(null, notRun.Count > 0 ? notRun : ["Select at least one test."]);
        if (kinds.Count > 1) return new(null, [.. notRun, "The selected tests use different authentication in this environment; run the tests of one authentication at a time."]);
        var e = request.Environment;
        var started = await runs.StartAsync(new ExecutionRunRequest
        {
            Name = string.IsNullOrWhiteSpace(request.Name) ? $"Contract tests · {string.Join(", ", contractRefs.Order(StringComparer.Ordinal))}" : request.Name.Trim(),
            EnvironmentId = e.EnvironmentId, TargetUrl = e.TargetUrl, EnvironmentType = e.EnvironmentType, IsProduction = e.IsProduction, ProfileId = e.ProfileId,
            ContextFingerprint = e.ContextFingerprint, Calls = calls, ContractRefs = [.. contractRefs.Order(StringComparer.Ordinal)], ConfirmMutatingCalls = request.ConfirmMutatingCalls,
            CapturePolicy = CapturePolicy.BoundedBody, Authentication = kinds.Single(),
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
        var binding = environment is { EnvironmentId.Length: > 0 } ? await BindingAsync(test.ContractKey, environment.EnvironmentId, ct) : null;
        var readiness = Readiness(test, latest, impact, op, environment, binding);
        ContractTestLastResult? last = null;
        if (await evidence.LatestByDefinitionAsync(ContractIdentity.DefinitionPrefix(test.Id), ct) is { } hit)
            last = new(hit.RunId, hit.Call.CallId, hit.Call.State, hit.Call.Attempts.LastOrDefault()?.Response?.StatusCode, hit.Call.CompletedAt ?? hit.StartedAt,
                hit.Call.Definition.DefinitionVersion, hit.Call.Definition.OperationRef, hit.Call.Definition.TestDataRefs);
        return new(test, readiness, impact, last);
    }

    private ContractTestReadiness Readiness(ContractTestDefinition test, int latest, ContractTestImpact? impact, ContractOperation? op, ContractTestEnvironment? environment,
        ContractEndpointBinding? binding)
    {
        var items = new List<ContractReadinessItem>();
        var missingValues = new List<string>();
        if (test.ReviewState == ContractTestReviewState.Retired) items.Add(new("Review", ReadinessItemState.Blocked, "Retired: kept with its history, never run. Change the review state to run it again."));
        else if (test.ReviewState == ContractTestReviewState.Draft) items.Add(new("Review", ReadinessItemState.Warning, "Draft: generated or edited and not reviewed yet."));
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
        items.Add(AuthenticationItem(test, op, environment, binding?.Authentication ?? ExecutionAuthenticationKind.None));
        if (ExecutionRunService.Mutating(test.Template))
            items.Add(new("Changes data", ReadinessItemState.Warning, $"{test.Template.Method} can change data: it runs only when you confirm data-changing calls for the run."));
        var state = items.Any(i => i.State == ReadinessItemState.Blocked) ? ContractTestReadinessState.Blocked
            : items.Any(i => i.State == ReadinessItemState.Missing) ? ContractTestReadinessState.NotReady : ContractTestReadinessState.Ready;
        return new(state, items, missingValues);
    }

    /// <summary>
    /// Authentication readiness for the operation's actual requirement: the contract's security schemes (bearer / OAuth2 / OpenID Connect need a
    /// bearer credential — the attended session or unattended client credentials; apiKey needs an API key; Basic is not supported) against the
    /// authentication bound for this contract in this environment, then whether that credential is usable now. GraphQL schemas declare no
    /// security, so the bound authentication is used as configured.
    /// </summary>
    private ContractReadinessItem AuthenticationItem(ContractTestDefinition test, ContractOperation? op, ContractTestEnvironment? environment, ExecutionAuthenticationKind kind)
    {
        var schemes = (op?.Security ?? []).Select(s => s.Contains('(') ? s[(s.LastIndexOf('(') + 1)..].TrimEnd(')').Trim().ToLowerInvariant() : s.ToLowerInvariant()).ToList();
        if (schemes.Count > 0)
        {
            var bearer = schemes.Any(s => s is "bearer" or "oauth2" or "openidconnect" or "jwt");
            var apiKey = schemes.Contains("apikey");
            if (kind == ExecutionAuthenticationKind.None)
                return new("Authentication", ReadinessItemState.Blocked, $"The operation requires {string.Join(", ", op!.Security)}. Choose its authentication for this contract in this environment; it is never sent anonymously instead.");
            if (schemes.All(s => s == "basic"))
                return new("Authentication", ReadinessItemState.Blocked, "The operation requires HTTP Basic authentication, which contract test runs do not support.");
            var fits = kind is ExecutionAuthenticationKind.AttendedSession or ExecutionAuthenticationKind.ClientCredentials ? bearer : apiKey;
            if (!fits)
                return new("Authentication", ReadinessItemState.Blocked, $"The operation requires {string.Join(", ", op!.Security)}; {ExecutionCredentialResolver.Label(kind)} does not provide that.");
        }
        if (kind == ExecutionAuthenticationKind.None)
            return new("Authentication", ReadinessItemState.Ok, test.Template.Protocol == ExecutionProtocol.GraphQl
                ? "No authentication selected; the run is anonymous (a GraphQL schema does not declare authentication), and an authentication error from the API shows as a failed test."
                : "The contract declares no security requirement for this operation; the run is anonymous.");
        if (environment is null || !Uri.TryCreate(environment.TargetUrl, UriKind.Absolute, out var target))
            return new("Authentication", ReadinessItemState.Missing, $"{ExecutionCredentialResolver.Label(kind)}: select a Target Environment to check it.");
        var prepared = credentials?.Prepare(new ExecutionRunRequest
        {
            EnvironmentId = environment.EnvironmentId, EnvironmentType = environment.EnvironmentType, TargetUrl = environment.TargetUrl, ProfileId = environment.ProfileId,
            ContextFingerprint = environment.ContextFingerprint, Authentication = kind,
        }, new Uri(target.GetLeftPart(UriPartial.Authority)));
        return prepared is null ? new("Authentication", ReadinessItemState.Blocked, "Authenticated execution is not available in this BirkNext instance.")
            : prepared.Problem is { } problem ? new("Authentication", ReadinessItemState.Blocked, $"{prepared.Mode}: {problem}")
            : new("Authentication", ReadinessItemState.Ok, $"{prepared.Mode} ({prepared.Reference}).");
    }

    /// <summary>A path inside the Target Environment: one leading '/', no host, scheme, query, fragment or backslash. Null = fine (or not given).</summary>
    public static string? PathProblem(string? path, string what)
    {
        var p = (path ?? "").Trim();
        if (p.Length == 0) return null;
        return !p.StartsWith('/') || p.StartsWith("//", StringComparison.Ordinal) || p.Contains("://", StringComparison.Ordinal) || p.IndexOfAny(['\\', '?', '#']) >= 0 || p.Length > 300
            ? $"{what} is a path inside the Target Environment (starts with a single '/', no host, query or fragment)." : null;
    }

    public async Task<ContractEndpointBinding?> BindingAsync(string key, string environmentId, CancellationToken ct) =>
        await db.ContractEndpointBindings.AsNoTracking().Where(b => b.EnvironmentId == environmentId)
            .Join(db.ApiContracts.AsNoTracking().Where(c => c.Key == key), b => b.ContractId, c => c.Id, (b, c) => b).FirstOrDefaultAsync(ct) is { } row
            ? new(key, row.EnvironmentId, row.BasePath, row.GraphQlPath, Enum.Parse<ExecutionAuthenticationKind>(row.Authentication), row.UpdatedAt) : null;

    public async Task<(ContractEndpointBinding? Binding, string? Error)> SaveBindingAsync(string key, ContractEndpointBindingSave save, CancellationToken ct)
    {
        if (await db.ApiContracts.AsNoTracking().FirstOrDefaultAsync(c => c.Key == key, ct) is not { } contract) return (null, "Not found.");
        if (string.IsNullOrWhiteSpace(save.EnvironmentId)) return (null, "Select a Target Environment.");
        if ((PathProblem(save.BasePath, "The API base path") ?? PathProblem(save.GraphQlPath, "The GraphQL endpoint path")) is { } problem) return (null, problem);
        var row = await db.ContractEndpointBindings.FirstOrDefaultAsync(b => b.ContractId == contract.Id && b.EnvironmentId == save.EnvironmentId, ct);
        if (row is null) { row = new ContractEndpointBindingRow { Id = Guid.NewGuid(), ContractId = contract.Id, EnvironmentId = save.EnvironmentId }; db.ContractEndpointBindings.Add(row); }
        row.BasePath = string.IsNullOrWhiteSpace(save.BasePath) ? null : save.BasePath.Trim().TrimEnd('/');
        row.GraphQlPath = string.IsNullOrWhiteSpace(save.GraphQlPath) ? null : save.GraphQlPath.Trim();
        row.Authentication = save.Authentication.ToString();
        row.UpdatedAt = _clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return (await BindingAsync(key, save.EnvironmentId, ct), null);
    }

    /// <summary>Base paths the contract's declared servers suggest: the path part only (a declared host is metadata, never a runtime target).</summary>
    public static List<ContractServerProposal> ServerProposals(SourceContract doc) =>
        [.. doc.Servers.Select(s => new ContractServerProposal(s, (Uri.TryCreate(s, UriKind.Absolute, out var u) ? u.AbsolutePath : s.Split('?')[0]).TrimEnd('/')))
            .Where(p => PathProblem(p.BasePath, "") is null).DistinctBy(p => p.BasePath)];

    // ── Editing and revisions ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Saves an edit as a new revision. Conflict when the test changed since <see cref="ContractTestEdit.ExpectedRevision"/>; nothing is saved
    /// when the edited content is invalid. Earlier revisions and every execution that used them stay exactly as they were.</summary>
    public async Task<ContractTestSaveResult> UpdateAsync(Guid id, ContractTestEdit edit, CancellationToken ct)
    {
        if (await db.ContractTests.FirstOrDefaultAsync(t => t.Id == id, ct) is not { } row) return new(false, false, null, "Not found.", []);
        var current = JsonSerializer.Deserialize<ContractTestDefinition>(row.DocumentJson, ExecutionEvidenceStore.Json)!;
        if (edit.ExpectedRevision != current.Revision)
            return new(false, true, current, $"The test was changed elsewhere (now revision {current.Revision}). Reload it and apply your change again.", []);
        var template = edit.Template with { Name = edit.Name.Trim(), OperationRef = null, TestDataRefs = [], DefinitionVersion = null };
        var problems = TemplateProblems(edit.Name, template);
        if (problems.Count > 0) return new(false, false, current, "Fix the problems before saving.", problems);
        var updated = current with
        {
            Name = edit.Name.Trim(), Description = string.IsNullOrWhiteSpace(edit.Description) ? null : edit.Description.Trim(), Template = template, ReviewState = edit.ReviewState,
            Inputs = Inputs(template, current.Inputs), Revision = current.Revision + 1, UpdatedAt = _clock.GetUtcNow(),
        };
        if (JsonSerializer.Serialize(updated with { Revision = current.Revision, UpdatedAt = current.UpdatedAt }, ExecutionEvidenceStore.Json) == JsonSerializer.Serialize(current, ExecutionEvidenceStore.Json))
            return new(false, false, current, "Nothing changed.", []);
        await PersistAsync(row, updated, string.IsNullOrWhiteSpace(edit.ChangeNote) ? "Edited." : edit.ChangeNote.Trim(), ct);
        return new(true, false, updated, $"Saved as revision {updated.Revision}.", []);
    }

    /// <summary>A copy as a new Draft test (revision 1) with the same content, contract version and Test Data pin.</summary>
    public async Task<(ContractTestDefinition? Test, string? Error)> DuplicateAsync(Guid id, CancellationToken ct)
    {
        if (await db.ContractTests.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct) is not { } row) return (null, "Not found.");
        var source = JsonSerializer.Deserialize<ContractTestDefinition>(row.DocumentJson, ExecutionEvidenceStore.Json)!;
        var now = _clock.GetUtcNow();
        var copy = source with { Id = Guid.NewGuid(), Name = $"Copy of {source.Name}", Revision = 1, ReviewState = ContractTestReviewState.Draft, CreatedAt = now, UpdatedAt = now };
        db.ContractTests.Add(Row(row.ContractId, copy));
        db.ContractTestRevisions.Add(RevisionRow(copy, $"Duplicated from {source.Name} r{source.Revision}."));
        await db.SaveChangesAsync(ct);
        return (copy, null);
    }

    public async Task<IReadOnlyList<ContractTestRevisionInfo>> RevisionsAsync(Guid id, CancellationToken ct)
    {
        var rows = await db.ContractTestRevisions.AsNoTracking().Where(r => r.TestId == id).OrderByDescending(r => r.Revision).ToListAsync(ct);
        if (rows.Count == 0 && await LoadAsync(id, ct) is { } legacy)
            return [new(legacy.Revision, legacy.UpdatedAt, "Current (recorded before revision history existed).", legacy.Name, legacy.ReviewState, legacy.ContractVersion, legacy.Binding is { } lb ? $"{lb.Alias}@v{lb.Version}" : null)];
        return [.. rows.Select(r => { var d = JsonSerializer.Deserialize<ContractTestDefinition>(r.DocumentJson, ExecutionEvidenceStore.Json)!;
            return new ContractTestRevisionInfo(r.Revision, r.CreatedAt, r.ChangeNote, d.Name, d.ReviewState, d.ContractVersion, d.Binding is { } b ? $"{b.Alias}@v{b.Version}" : null); })];
    }

    public async Task<ContractTestDefinition?> RevisionAsync(Guid id, int revision, CancellationToken ct) =>
        await db.ContractTestRevisions.AsNoTracking().Where(r => r.TestId == id && r.Revision == revision).Select(r => r.DocumentJson).FirstOrDefaultAsync(ct) is { } doc
            ? JsonSerializer.Deserialize<ContractTestDefinition>(doc, ExecutionEvidenceStore.Json)
            : await LoadAsync(id, ct) is { } current && current.Revision == revision ? current : null;

    /// <summary>What differs between two revisions, field by field (assertions by id).</summary>
    public async Task<ContractTestRevisionDiff?> CompareAsync(Guid id, int from, int to, CancellationToken ct)
    {
        if (await RevisionAsync(id, from, ct) is not { } a || await RevisionAsync(id, to, ct) is not { } b) return null;
        var differences = new List<string>();
        void Field(string name, object? x, object? y)
        {
            var (sx, sy) = (x is string s1 ? s1 : JsonSerializer.Serialize(x, ExecutionEvidenceStore.Json), y is string s2 ? s2 : JsonSerializer.Serialize(y, ExecutionEvidenceStore.Json));
            if (sx != sy) differences.Add($"{name}: {Clip(sx)} → {Clip(sy)}");
        }
        Field("Name", a.Name, b.Name); Field("Description", a.Description ?? "", b.Description ?? ""); Field("Review state", a.ReviewState.ToString(), b.ReviewState.ToString());
        Field("Contract version", $"v{a.ContractVersion}", $"v{b.ContractVersion}"); Field("Test Data", a.Binding is { } x1 ? $"{x1.Alias}@v{x1.Version}" : "none", b.Binding is { } y1 ? $"{y1.Alias}@v{y1.Version}" : "none");
        Field("Method", a.Template.Method, b.Template.Method); Field("Path", a.Template.Path, b.Template.Path); Field("Query", a.Template.Query, b.Template.Query);
        Field("Headers", a.Template.Headers, b.Template.Headers); Field("Content type", a.Template.ContentType ?? "", b.Template.ContentType ?? ""); Field("Body", a.Template.Body ?? "", b.Template.Body ?? "");
        Field("GraphQL document", a.Template.GraphQlQuery ?? "", b.Template.GraphQlQuery ?? ""); Field("GraphQL variables", a.Template.GraphQlVariablesJson ?? "", b.Template.GraphQlVariablesJson ?? "");
        Field("Timeout (ms)", a.Template.TimeoutMs.ToString(System.Globalization.CultureInfo.InvariantCulture), b.Template.TimeoutMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Field("Retry", a.Template.Retry, b.Template.Retry); Field("Contract check", a.Template.ContractCheck.ToString(), b.Template.ContractCheck.ToString());
        var assertionsA = a.Template.Assertions.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var assertionsB = b.Template.Assertions.ToDictionary(x => x.Id, StringComparer.Ordinal);
        foreach (var added in assertionsB.Keys.Except(assertionsA.Keys)) differences.Add($"Assertion added: {Describe(assertionsB[added])}");
        foreach (var removed in assertionsA.Keys.Except(assertionsB.Keys)) differences.Add($"Assertion removed: {Describe(assertionsA[removed])}");
        foreach (var both in assertionsA.Keys.Intersect(assertionsB.Keys).Where(k => assertionsA[k] != assertionsB[k])) differences.Add($"Assertion changed: {Describe(assertionsA[both])} → {Describe(assertionsB[both])}");
        return new(id, from, to, differences);
        static string Clip(string s) => s.Length > 120 ? s[..120] + "…" : s;
        static string Describe(AssertionDefinition x) => $"{x.Id} {x.Kind} {x.Operator}{(x.Target is null ? "" : " " + x.Target)}{(x.Expected is null ? "" : " " + x.Expected)}";
    }

    /// <summary>Revert = a new revision whose content is an earlier revision's (history is never rewritten).</summary>
    public async Task<ContractTestSaveResult> RevertAsync(Guid id, int revision, int expectedRevision, CancellationToken ct)
    {
        if (await db.ContractTests.FirstOrDefaultAsync(t => t.Id == id, ct) is not { } row) return new(false, false, null, "Not found.", []);
        var current = JsonSerializer.Deserialize<ContractTestDefinition>(row.DocumentJson, ExecutionEvidenceStore.Json)!;
        if (expectedRevision != current.Revision) return new(false, true, current, $"The test was changed elsewhere (now revision {current.Revision}).", []);
        if (await RevisionAsync(id, revision, ct) is not { } old) return new(false, false, current, $"Revision {revision} does not exist.", []);
        var reverted = old with { Revision = current.Revision + 1, UpdatedAt = _clock.GetUtcNow(), CreatedAt = current.CreatedAt };
        await PersistAsync(row, reverted, $"Reverted to revision {revision}.", ct);
        return new(true, false, reverted, $"Saved as revision {reverted.Revision} with the content of revision {revision}.", []);
    }

    /// <summary>What a saved template must satisfy: the run service's own call rules (relative path, no credential headers, supported method, GraphQL
    /// document present, variables an object), plus well-formed JSON, assertions, timeout and retry, and no literal credential values.</summary>
    public static List<string> TemplateProblems(string name, ExecutionCallDefinition t)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 300) problems.Add("Name: 1–300 characters.");
        if (ExecutionRunService.CallBlock(t, new ExecutionRunRequest { ConfirmMutatingCalls = true }) is { } block) problems.Add(block);
        if (t.Query.Keys.Any(string.IsNullOrWhiteSpace) || t.Headers.Keys.Any(string.IsNullOrWhiteSpace)) problems.Add("Every query parameter and header needs a name.");
        foreach (var (where, text) in new[] { ("Body", t.Body), ("GraphQL variables", t.GraphQlVariablesJson) }.Concat(t.Query.Select(q => ($"Query {q.Key}", (string?)q.Value))).Concat(t.Headers.Select(h => ($"Header {h.Key}", (string?)h.Value))))
            if (text is { Length: > 0 } && ExecutionEvidenceRedaction.Text(text).Masked > 0) problems.Add($"{where} contains a credential-like value. Credentials come from the authentication binding, never the test.");
        if (t.Protocol == ExecutionProtocol.Rest && t.Body is { Length: > 0 } body && (t.ContentType ?? "").Contains("json", StringComparison.OrdinalIgnoreCase) && !ContractTestRules.IsWholePlaceholder(body))
            try { System.Text.Json.Nodes.JsonNode.Parse(body); } catch (JsonException ex) { problems.Add($"Body is not valid JSON ({ex.Message.Split('.')[0]})."); }
        if (t.Protocol == ExecutionProtocol.GraphQl)
            try { HotChocolate.Language.Utf8GraphQLParser.Parse(t.GraphQlQuery ?? ""); } catch (HotChocolate.Language.SyntaxException ex) { problems.Add($"GraphQL document: {ex.Message}"); }
        if (t.TimeoutMs is < 500 or > 120000) problems.Add("Timeout: 500–120000 ms.");
        if (t.Retry.MaxAttempts is < 1 or > 5 || t.Retry.DelayMs is < 0 or > 10000 || t.Retry.RetryOnStatus?.Any(c => c is < 100 or > 599) == true) problems.Add("Retry: 1–5 attempts, delay 0–10000 ms, status codes 100–599.");
        foreach (var dup in t.Assertions.GroupBy(a => a.Id, StringComparer.Ordinal).Where(g => g.Count() > 1 || string.IsNullOrWhiteSpace(g.Key))) problems.Add($"Assertion ids must be unique and non-empty ({dup.Key}).");
        foreach (var a in t.Assertions)
        {
            if (a.Kind is AssertionKind.Header or AssertionKind.JsonPath or AssertionKind.GraphQlDataPath or AssertionKind.XPath && string.IsNullOrWhiteSpace(a.Target)) problems.Add($"Assertion {a.Id}: {a.Kind} needs a target.");
            if (a.Operator is not (AssertionOperator.Exists or AssertionOperator.NotExists) && a.Expected is null) problems.Add($"Assertion {a.Id}: {a.Operator} needs an expected value.");
        }
        return problems;
    }

    /// <summary>The inputs a template needs: every placeholder it uses (kept with its earlier location/type/requiredness when known).</summary>
    private static List<ContractTestInput> Inputs(ExecutionCallDefinition t, IReadOnlyList<ContractTestInput> previous)
    {
        var names = ContractTestRules.Placeholders(string.Join('\n', new[] { t.Path, t.Body, t.GraphQlVariablesJson }.Concat(t.Query.Values).Concat(t.Headers.Values)));
        return [.. names.Select(n => previous.FirstOrDefault(p => p.Name == n) ?? new ContractTestInput(n, "template", true, null))];
    }

    private async Task PersistAsync(ContractTestRow row, ContractTestDefinition test, string note, CancellationToken ct)
    {
        // Tests created before revision history existed: record their current content once, so the history starts complete.
        var previousRevision = test.Revision - 1;
        if (previousRevision >= 1 && !await db.ContractTestRevisions.AnyAsync(r => r.TestId == row.Id, ct))
        {
            var before = JsonSerializer.Deserialize<ContractTestDefinition>(row.DocumentJson, ExecutionEvidenceStore.Json)!;
            db.ContractTestRevisions.Add(RevisionRow(before, "Recorded before revision history existed."));
        }
        Update(row, test);
        db.ContractTestRevisions.Add(RevisionRow(test, note));
        await db.SaveChangesAsync(ct);
    }

    private ContractTestRevisionRow RevisionRow(ContractTestDefinition t, string note) => new()
    {
        Id = Guid.NewGuid(), TestId = t.Id, Revision = t.Revision, DocumentJson = JsonSerializer.Serialize(t, ExecutionEvidenceStore.Json), ChangeNote = note.Length > 500 ? note[..500] : note,
        CreatedAt = _clock.GetUtcNow(),
    };

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
