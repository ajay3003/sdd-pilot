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
        if (contract.Lifecycle == nameof(ContractLifecycle.Archived)) return ([], [$"{contract.Name} is archived: it is read-only."]);
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

    /// <summary>
    /// Negative proposals for one operation (from its positive test, which is generated first when missing). Saved as Drafts; a proposal that already
    /// exists for the same operation, kind and input is skipped. Nothing is run.
    /// </summary>
    public async Task<(List<ContractTestDefinition> Created, List<string> Skipped)> ProposeNegativesAsync(string key, string operation, CancellationToken ct)
    {
        if (await db.ApiContracts.AsNoTracking().FirstOrDefaultAsync(c => c.Key == key, ct) is not { } contract) return ([], [$"Contract {key} does not exist."]);
        if (contract.Lifecycle == nameof(ContractLifecycle.Archived)) return ([], [$"{contract.Name} is archived: it is read-only."]);
        var version = await registry.LatestVersionAsync(key, ct) ?? 0;
        if (await registry.RawAsync(key, version, ct) is not { } raw || raw.Document.Operations.FirstOrDefault(o => o.Name == operation) is not { } op) return ([], [$"{operation} is not in v{version}."]);
        var existing = (await db.ContractTests.AsNoTracking().Where(t => t.ContractId == contract.Id && t.Operation == operation).Select(t => t.DocumentJson).ToListAsync(ct))
            .Select(d => JsonSerializer.Deserialize<ContractTestDefinition>(d, ExecutionEvidenceStore.Json)!).ToList();
        var positive = existing.FirstOrDefault(t => t.Kind == ContractTestKind.Positive && t.ContractVersion == version)
            ?? ContractTestRules.GenerateAny(raw.Document, key, version, op, _clock.GetUtcNow()).FirstOrDefault(t => t.Kind == ContractTestKind.Positive);
        if (positive is null) return ([], [$"{operation}: no test can be generated for this operation."]);
        OpenApiSchemaValidator? validator = null;
        if (raw.Document.Type == SourceContractType.OpenApi && raw.Text.Length > 0)
            try { validator = OpenApiSchemaValidator.Parse(raw.Text) is { } root ? new OpenApiSchemaValidator(root) : null; } catch (Exception ex) when (ex is JsonException or FormatException) { }
        var created = new List<ContractTestDefinition>(); var skipped = new List<string>();
        foreach (var proposal in ContractNegativeRules.Propose(raw.Document, validator, positive, op, _clock.GetUtcNow()))
        {
            if (existing.Any(t => t.Kind == proposal.Kind && t.Variant == proposal.Variant)) { skipped.Add($"{proposal.Name}: already exists."); continue; }
            db.ContractTests.Add(Row(contract.Id, proposal));
            db.ContractTestRevisions.Add(RevisionRow(proposal, $"Proposed from contract v{version}."));
            created.Add(proposal);
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

    /// <summary>
    /// Evidence-backed coverage of one contract version. Each metric states its numerator, denominator, scope and how many items were not assessed
    /// (no execution evidence). Only calls whose Execution Evidence names this exact contract version count; nothing is inferred from test existence alone
    /// except the "operations with tests" metric, which says so.
    /// </summary>
    public async Task<ContractCoverageReport?> CoverageAsync(string key, int? version, CancellationToken ct)
    {
        if (await db.ApiContracts.AsNoTracking().FirstOrDefaultAsync(c => c.Key == key, ct) is not { } contract) return null;
        var v = version ?? await registry.LatestVersionAsync(key, ct) ?? 0;
        if (await registry.DocumentAsync(key, v, ct) is not { } doc) return null;
        var protocol = Enum.Parse<ApiContractProtocol>(contract.Protocol);
        var ops = doc.Operations.Where(o => o.Kind != "subscription").ToList();
        var names = ops.Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
        var reference = ContractIdentity.ContractRef(key, v);
        var tests = (await db.ContractTests.AsNoTracking().Where(t => t.ContractId == contract.Id).Select(t => t.DocumentJson).ToListAsync(ct))
            .Select(d => JsonSerializer.Deserialize<ContractTestDefinition>(d, ExecutionEvidenceStore.Json)!).Where(t => t.ContractVersion == v && names.Contains(t.Operation)).ToList();
        var marker = $"\"operationRef\":\"{reference}#";
        var calls = (await db.ExecutionCalls.AsNoTracking().Where(c => c.DocumentJson.Contains(marker)).Select(c => c.DocumentJson).ToListAsync(ct))
            .Select(d => JsonSerializer.Deserialize<ExecutionCallRecord>(d, ExecutionEvidenceStore.Json)!)
            .Where(c => c.Definition.OperationRef?.StartsWith(reference + "#", StringComparison.Ordinal) == true).ToList();
        static string Op(ExecutionCallRecord c) => c.Definition.OperationRef![(c.Definition.OperationRef!.IndexOf('#') + 1)..];
        var executed = calls.Where(c => c.Attempts.Any(a => a.Response is not null)).ToList();
        // The latest executed call per test (by definition prefix), or per operation for calls without a definition reference.
        var latest = executed.GroupBy(c => c.Definition.DefinitionVersion is { } d && d.LastIndexOf("@r", StringComparison.Ordinal) is var i and > 0 ? d[..i] : Op(c))
            .Select(g => g.OrderByDescending(c => c.CompletedAt ?? c.StartedAt).First()).ToList();
        var testedOps = tests.Select(t => t.Operation).ToHashSet(StringComparer.Ordinal);
        var executedOps = executed.Select(Op).Where(names.Contains).ToHashSet(StringComparer.Ordinal);
        var passingOps = latest.GroupBy(Op).Where(g => names.Contains(g.Key) && g.All(c => c.State == ExecutionState.Passed)).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var negativeOps = tests.Where(t => t.Kind != ContractTestKind.Positive).Select(t => t.Operation).ToHashSet(StringComparer.Ordinal);
        string scope = $"{ops.Count} operation(s) of {reference}" + (doc.Operations.Count > ops.Count ? " (subscriptions excluded)" : "");
        const string Evidence = "Execution Evidence (calls naming this contract version)";
        var metrics = new List<ContractCoverageMetric>
        {
            new("operations-with-tests", "Operations with a stored test", testedOps.Count, ops.Count, scope, 0, "Stored contract tests bound to this version",
                "A stored test is not evidence that the operation works."),
            new("operations-executed", "Operations executed", executedOps.Count, ops.Count, scope, ops.Count - executedOps.Count, Evidence),
            new("operations-passing", "Operations whose latest results all passed", passingOps.Count, ops.Count, scope, ops.Count - executedOps.Count, Evidence),
            new("operations-with-negative-tests", "Operations with a negative test", negativeOps.Count, ops.Count, scope, 0, "Stored contract tests bound to this version"),
        };
        if (protocol == ApiContractProtocol.Rest)
        {
            var documented = ops.SelectMany(o => o.ResponseCodes.Where(c => c.Length == 3 && c.All(char.IsDigit)).Select(c => (Op: o.Name, Code: int.Parse(c)))).ToList();
            var observed = executed.Select(c => (Op: Op(c), Code: c.Attempts.LastOrDefault(a => a.Response is not null)?.Response?.StatusCode ?? 0)).ToHashSet();
            var hit = documented.Count(observed.Contains);
            var unassessed = documented.Count(d => !executedOps.Contains(d.Op));
            metrics.Add(new("documented-responses-observed", "Documented response codes observed", hit, documented.Count, $"{documented.Count} explicit response code(s) of {reference}",
                unassessed, Evidence, "Ranges (2XX) and \"default\" are not counted; unassessed = codes of operations never executed."));
        }
        var responseReports = latest.Select(c => c.ContractValidation.FirstOrDefault(r => r.Direction == "Response")).ToList();
        metrics.Add(new("responses-valid", "Latest responses valid against the contract", responseReports.Count(r => r is { State: ContractValidationState.Valid, Unsupported.Count: 0 }),
            latest.Count, $"{latest.Count} latest executed call(s)", responseReports.Count(r => r is null or { State: ContractValidationState.NotAssessed } or { Unsupported.Count: > 0 }),
            Evidence, "Not assessed includes responses checked with unsupported schema keywords."));
        var assertions = latest.SelectMany(c => c.Assertions).ToList();
        metrics.Add(new("assertions-passed", "Assertions passed (latest results)", assertions.Count(a => a.Outcome == AssertionOutcome.Passed), assertions.Count,
            $"{assertions.Count} assertion(s) in {latest.Count} latest executed call(s)", assertions.Count(a => a.Outcome == AssertionOutcome.NotAssessed), Evidence));
        return new(key, v, protocol, metrics, _clock.GetUtcNow());
    }

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
            var (_, problem) = Values(version.Content, r.Alias);
            var provided = Provided(version.Content, r.Alias);
            var (missingRequired, missingOptional) = ContractTestRules.Missing(test, provided);
            problem ??= approved is null ? "No approved version: approve it in Test Data first." : asset?.Deprecated == true ? "Deprecated." : null;
            options.Add(new(r.Alias, version.Label, TestDataResourceRules.Label(r.Current.Content.Kind), approved, r.Current.Version,
                problem is null && missingRequired.Count == 0, [.. provided.Order(StringComparer.Ordinal)], missingRequired, missingOptional, r.Current.EnvironmentId, problem,
                version.Content.Kind == TestDataResourceKind.Dataset ? version.Content.Rows.Count : 0));
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
        var limit = Math.Clamp(request.MaxExecutions ?? ExecutionRunService.MaxCalls, 1, ExecutionRunService.MaxCalls);
        var contractRefs = new HashSet<string>(StringComparer.Ordinal);
        var kinds = new HashSet<ExecutionAuthenticationKind>();
        foreach (var id in request.TestIds.Distinct())
        {
            if (await LoadAsync(id, ct) is not { } test) { notRun.Add($"{id}: not found."); continue; }
            var latest = await registry.LatestVersionAsync(test.ContractKey, ct) ?? 0;
            var view = await ViewAsync(test, latest, [], request.Environment, ct);
            if (view.Readiness.State != ContractTestReadinessState.Ready)
            { notRun.Add($"{test.Name}: {string.Join(" ", view.Readiness.Items.Where(i => i.State is ReadinessItemState.Missing or ReadinessItemState.Blocked).Select(i => i.Text))}"); continue; }
            var binding = await BindingAsync(test.ContractKey, request.Environment.EnvironmentId, ct);
            var basePath = (request.BasePath ?? binding?.BasePath ?? "").Trim().TrimEnd('/');
            var added = false;
            // A data-driven test runs once per dataset row (selected rows only, when the run names them); each row is its own logical call.
            foreach (var (rowId, values, rowNames) in BoundVariants(test))
            {
                if (rowId is not null && request.RowIds.Count > 0 && !request.RowIds.Contains(rowId, StringComparer.Ordinal)) continue;
                var label = rowId is null ? test.Name : $"{test.Name} · {rowId}";
                var (call, missing) = ContractTestRules.Materialize(test, values);
                if (call is null) { notRun.Add($"{label}: missing {string.Join(", ", missing)}."); continue; }
                if (calls.Count >= limit) { notRun.Add($"{label}: over the run's limit of {limit} executions; not run."); continue; }
                var path = call.Protocol == ExecutionProtocol.GraphQl && (request.GraphQlPath ?? binding?.GraphQlPath) is { Length: > 0 } endpoint ? endpoint.Trim() : basePath + call.Path;
                var source = test.Binding is { } bound ? $"{bound.Alias}@v{bound.Version}" : "";
                calls.Add(call with
                {
                    Path = path, Name = label, OperationRef = ContractIdentity.OperationRef(test.ContractKey, test.ContractVersion, test.Operation),
                    TestDataRefs = test.Binding is { } b ? [$"{b.Alias}@v{b.Version}"] : [], DefinitionVersion = ContractIdentity.DefinitionRef(test.Id, test.Revision),
                    WithoutAuthentication = test.WithoutAuthentication, DatasetRow = rowId is null ? null : $"{source}#{rowId}",
                    ResolvedValues = ContractTestRules.Resolved(test, values, name => rowNames.Contains(name) ? $"row {rowId} of {source}" : $"member of {source}"),
                });
                added = true;
            }
            if (!added) continue;
            // A missing-authentication negative is sent without the credential inside the same run; it never decides the run's authentication.
            if (!test.WithoutAuthentication) kinds.Add(binding?.Authentication ?? ExecutionAuthenticationKind.None);
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
            CapturePolicy = CapturePolicy.BoundedBody, Authentication = kinds.SingleOrDefault(), ReauthenticationWaitSeconds = request.ReauthenticationWaitSeconds,
            StopOnFirstFailure = request.StopOnFirstFailure,
        }, "Local session", ct);
        return new(started, notRun);
    }

    public async Task<ContractVersionDiff?> DiffAsync(string key, int from, int to, CancellationToken ct)
    {
        if (await registry.DocumentAsync(key, from, ct) is not { } before || await registry.DocumentAsync(key, to, ct) is not { } after) return null;
        var impacts = new List<ContractTestImpact>();
        foreach (var t in (await ListDefinitionsAsync(key, ct)).Where(t => t.ContractVersion == from))
            impacts.Add(ContractTestRules.Impact(t, before, after, to, BoundStrings(t)));
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
        var impact = current is not null && newest is not null && latest != test.ContractVersion ? ContractTestRules.Impact(test, current, newest, latest, BoundStrings(test)) : null;
        var op = (newest ?? current)?.Operations.FirstOrDefault(o => o.Name == test.Operation) ?? current?.Operations.FirstOrDefault(o => o.Name == test.Operation);
        var binding = environment is { EnvironmentId.Length: > 0 } ? await BindingAsync(test.ContractKey, environment.EnvironmentId, ct) : null;
        var readiness = Readiness(test, latest, impact, op, environment, binding);
        if (await registry.LifecycleAsync(test.ContractKey, ct) is { } lifecycle and not ContractLifecycle.Active)
        {
            var item = lifecycle == ContractLifecycle.Archived ? new ContractReadinessItem("Contract", ReadinessItemState.Blocked, "The contract is archived: its tests are kept but not run.")
                : new ContractReadinessItem("Contract", ReadinessItemState.Warning, "The contract is deprecated.");
            var items = readiness.Items.Prepend(item).ToList();
            readiness = readiness with { Items = items, State = items.Any(i => i.State == ReadinessItemState.Blocked) ? ContractTestReadinessState.Blocked : readiness.State };
        }
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
            ContractTestImpactLevel.Definite when impact.Reasons.Any(r => r.Contains("no longer exists", StringComparison.Ordinal)) =>
                new("Contract", ReadinessItemState.Blocked, $"v{latest} removed {test.Operation}. Delete or retire the test."),
            ContractTestImpactLevel.Definite => new("Contract", ReadinessItemState.Missing, $"Contract v{latest} breaks this test (it is bound to v{test.ContractVersion}): {string.Join(" ", impact.Reasons.Where(r => r.StartsWith("Definite", StringComparison.Ordinal)))} Review the update proposal."),
            ContractTestImpactLevel.Potential => new("Contract", ReadinessItemState.Warning, $"Contract v{latest} may affect this test (bound to v{test.ContractVersion}): {string.Join(" ", impact.Reasons.Where(r => r.StartsWith("Potential", StringComparison.Ordinal)))}"),
            ContractTestImpactLevel.NeedsReview => new("Contract", ReadinessItemState.Warning, $"Contract v{latest} needs a human review for this test: {string.Join(" ", impact.Reasons.Where(r => r.StartsWith("Needs", StringComparison.Ordinal)))}"),
            _ => new("Contract", ReadinessItemState.Ok, $"No evidence that v{latest} affects this test (bound to v{test.ContractVersion})."),
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
            var (_, problem) = BoundValues(test);
            var variants = BoundVariants(test);
            var provided = variants.Select(v => v.Values.Keys.ToHashSet(StringComparer.Ordinal)).Aggregate((a, x) => { a.IntersectWith(x); return a; });
            var rows = variants.Count > 1 || variants[0].RowId is not null;
            var asset = catalog.Assets().FirstOrDefault(a => a.Alias == b.Alias && a.SourceId == AuthoredTestDataProvider.ProviderId);
            if (problem is not null) items.Add(new("Test Data", ReadinessItemState.Blocked, problem));
            else if (asset?.Deprecated == true) items.Add(new("Test Data", ReadinessItemState.Blocked, $"{b.Alias} is deprecated."));
            else
            {
                var (required, _) = ContractTestRules.Missing(test, provided);
                missingValues.AddRange(required);
                if (required.Count > 0) items.Add(new("Test Data", ReadinessItemState.Missing, $"{b.Alias}@v{b.Version} lacks required value(s){(rows ? $" in at least one of its {variants.Count} rows" : "")}: {string.Join(", ", required)}."));
                else if (asset?.Approval == TestDataApproval.Approved && asset.Version != b.Version)
                    items.Add(new("Test Data", ReadinessItemState.Warning, $"Pinned to {b.Alias}@v{b.Version}; v{asset.Version} is now the approved version. Runs keep using v{b.Version} until you pin again."));
                else items.Add(new("Test Data", ReadinessItemState.Ok, $"{b.Alias}@v{b.Version} provides every required value{(rows ? $" in each of its {variants.Count} rows" : "")}."));
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
        if (test.WithoutAuthentication)
            items.Add(new("Authentication", ReadinessItemState.Ok, "Sent without any credential on purpose (a missing-authentication test); run it separately from authenticated tests."));
        else items.Add(AuthenticationItem(test, op, environment, binding?.Authentication ?? ExecutionAuthenticationKind.None));
        if (test.Template.Assertions.Count == 0)
            items.Add(new("Expected outcome", ReadinessItemState.Missing, $"{test.Template.ExpectedOutcome ?? "No assertion is configured."} Edit the test and add the expected status or assertions; without them the result would be Not assessed."));
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
    public async Task<ContractTestRevisionDiff?> CompareAsync(Guid id, int from, int to, CancellationToken ct) =>
        await RevisionAsync(id, from, ct) is { } a && await RevisionAsync(id, to, ct) is { } b ? new(id, from, to, Differences(a, b)) : null;

    /// <summary>
    /// A reviewable update of a test to the latest contract version: the definition regenerated from it (same id, name, kind, variant, description
    /// and Test Data pin), keeping the tester's own assertions unless they check something the new version removed. Nothing is saved.
    /// </summary>
    public async Task<ContractTestMigrationProposal?> MigrationAsync(Guid id, CancellationToken ct)
    {
        if (await LoadAsync(id, ct) is not { } test) return null;
        var latest = await registry.LatestVersionAsync(test.ContractKey, ct) ?? 0;
        var notes = new List<string>();
        if (latest == test.ContractVersion) return new(id, test.ContractVersion, latest, test, [], ["The test is already bound to the latest contract version."]);
        if (await registry.RawAsync(test.ContractKey, latest, ct) is not { } raw || raw.Document.Operations.FirstOrDefault(o => o.Name == test.Operation) is not { } op)
            return new(id, test.ContractVersion, latest, test, [], [$"{test.Operation} does not exist in v{latest}: there is nothing to migrate to; retire or delete the test."]);
        var now = _clock.GetUtcNow();
        var generated = ContractTestRules.GenerateAny(raw.Document, test.ContractKey, latest, op, now);
        var fresh = generated.FirstOrDefault(t => t.Kind == test.Kind && test.Variant.Length == 0);
        if (fresh is null && generated.FirstOrDefault(t => t.Kind == ContractTestKind.Positive) is { } positive)
        {
            OpenApiSchemaValidator? validator = null;
            if (raw.Document.Type == SourceContractType.OpenApi && raw.Text.Length > 0)
                try { validator = OpenApiSchemaValidator.Parse(raw.Text) is { } root ? new OpenApiSchemaValidator(root) : null; } catch (Exception ex) when (ex is JsonException or FormatException) { }
            fresh = ContractNegativeRules.Propose(raw.Document, validator, positive, op, now).FirstOrDefault(t => t.Kind == test.Kind && t.Variant == test.Variant);
        }
        if (fresh is null) return new(id, test.ContractVersion, latest, test, [], [$"v{latest} no longer gives {test.Operation} a {test.Kind} test{(test.Variant.Length > 0 ? $" for {test.Variant}" : "")}; edit the test by hand or retire it."]);
        var generatedIds = fresh.Template.Assertions.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var removedFields = SourceEvidenceDiff.ContractChanges((await registry.DocumentAsync(test.ContractKey, test.ContractVersion, ct))!, raw.Document)
            .Where(c => c.ContractChange == ContractChangeClass.FieldRemoved).Select(c => c.Key.Split('|').Last().Split('.').Last()).ToHashSet(StringComparer.Ordinal);
        var kept = new List<AssertionDefinition>();
        foreach (var a in test.Template.Assertions.Where(a => !generatedIds.Contains(a.Id)))
        {
            if (a.Target is { } target && removedFields.Any(f => target.EndsWith($".{f}", StringComparison.Ordinal))) notes.Add($"Assertion {a.Id} ({a.Target}) is dropped: v{latest} removed that field.");
            else { kept.Add(a); notes.Add($"Your assertion {a.Id} is kept."); }
        }
        var proposed = fresh with
        {
            Id = test.Id, Name = test.Name, Description = test.Description, Binding = test.Binding, Revision = test.Revision + 1, CreatedAt = test.CreatedAt, ReviewState = ContractTestReviewState.Draft,
            Template = fresh.Template with { Assertions = [.. fresh.Template.Assertions, .. kept] }, Variant = test.Variant, WithoutAuthentication = test.WithoutAuthentication,
        };
        var (values, _) = BoundValues(proposed);
        var (missing, _) = ContractTestRules.Missing(proposed, values.Keys.ToHashSet(StringComparer.Ordinal));
        if (missing.Count > 0) notes.Add($"The pinned Test Data {(proposed.Binding is { } b ? $"{b.Alias}@v{b.Version}" : "(none)")} lacks {string.Join(", ", missing)}: pin a dataset that provides them after applying.");
        notes.Add("Applying creates a new revision bound to v" + latest + "; earlier revisions and their executions stay bound to v" + test.ContractVersion + ".");
        return new(id, test.ContractVersion, latest, proposed, Differences(test, proposed), notes);
    }

    /// <summary>Applies the reviewed migration proposal as a new revision (optimistic concurrency); never done automatically.</summary>
    public async Task<ContractTestSaveResult> ApplyMigrationAsync(Guid id, int expectedRevision, CancellationToken ct)
    {
        if (await db.ContractTests.FirstOrDefaultAsync(t => t.Id == id, ct) is not { } row) return new(false, false, null, "Not found.", []);
        var current = JsonSerializer.Deserialize<ContractTestDefinition>(row.DocumentJson, ExecutionEvidenceStore.Json)!;
        if (expectedRevision != current.Revision) return new(false, true, current, $"The test was changed elsewhere (now revision {current.Revision}).", []);
        var proposal = await MigrationAsync(id, ct);
        if (proposal is null || proposal.Proposed.ContractVersion == current.ContractVersion)
            return new(false, false, current, proposal?.Notes.FirstOrDefault() ?? "Nothing to migrate.", []);
        var migrated = proposal.Proposed with { UpdatedAt = _clock.GetUtcNow() };
        await PersistAsync(row, migrated, $"Migrated from v{proposal.FromVersion} to v{proposal.ToVersion} (reviewed proposal).", ct);
        return new(true, false, migrated, $"Saved as revision {migrated.Revision}, bound to v{proposal.ToVersion}.", []);
    }

    private static List<string> Differences(ContractTestDefinition a, ContractTestDefinition b)
    {
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
        return differences;
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

    /// <summary>The bound values as text (for impact: which value a changed enum or parameter receives).</summary>
    private Dictionary<string, string?> BoundStrings(ContractTestDefinition test) => BoundValues(test).Values.ToDictionary(v => v.Key, v => v.Value.Value, StringComparer.Ordinal);

    /// <summary>The bound dataset's values at the pinned version (never the latest unless that is the pinned one).</summary>
    private (Dictionary<string, TestDataTypedEntry> Values, string? Problem) BoundValues(ContractTestDefinition test)
    {
        if (test.Binding is not { } b) return (new(StringComparer.Ordinal), null);
        if (authored.Find(b.Alias) is not { } r) return (new(StringComparer.Ordinal), $"{b.Alias} no longer exists in Test Data.");
        if (r.Versions.FirstOrDefault(v => v.Version == b.Version) is not { } version) return (new(StringComparer.Ordinal), $"{b.Alias} has no version {b.Version}.");
        return Values(version.Content, b.Alias);
    }

    /// <summary>
    /// The executions of a bound test: one per dataset row (members overlaid by the row's values; RowNames = the names the row supplied), or one
    /// made of the members when the dataset has no rows. A row value that does not match its type is absent (reported missing), never coerced.
    /// </summary>
    private List<(string? RowId, Dictionary<string, TestDataTypedEntry> Values, HashSet<string> RowNames)> BoundVariants(ContractTestDefinition test)
    {
        var (members, _) = BoundValues(test);
        if (test.Binding is not { } b || authored.Find(b.Alias)?.Versions.FirstOrDefault(v => v.Version == b.Version)?.Content is not { Kind: TestDataResourceKind.Dataset, Rows.Count: > 0 } content)
            return [(null, members, [])];
        return [.. content.Rows.Select(row =>
        {
            var values = new Dictionary<string, TestDataTypedEntry>(members, StringComparer.Ordinal);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in row.Values.Where(e => TestDataResourceRules.TypeError(e.Type, e.Value, e.EnumValues) is null)) { values[e.Name] = e; names.Add(e.Name); }
            foreach (var e in row.Values.Where(e => TestDataResourceRules.TypeError(e.Type, e.Value, e.EnumValues) is not null)) values.Remove(e.Name);
            return ((string?)row.Id, values, names);
        })];
    }

    /// <summary>Names every execution of a dataset can supply: the members plus the columns present in every row.</summary>
    private HashSet<string> Provided(TestDataResourceContent content, string alias)
    {
        var members = Values(content, alias).Values.Keys.ToHashSet(StringComparer.Ordinal);
        if (content.Kind != TestDataResourceKind.Dataset || content.Rows.Count == 0) return members;
        var common = content.Rows.Select(r => r.Values.Where(e => TestDataResourceRules.TypeError(e.Type, e.Value, e.EnumValues) is null).Select(e => e.Name).ToHashSet(StringComparer.Ordinal))
            .Aggregate((a, x) => { a.IntersectWith(x); return a; });
        members.UnionWith(common);
        return members;
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

    // ── Chains ──────────────────────────────────────────────────────────────────────────────────────────────────────

    public const int MaxChainSteps = 50;

    private static ContractTestChain Chain(ContractTestChainRow row) => JsonSerializer.Deserialize<ContractTestChain>(row.DocumentJson, ExecutionEvidenceStore.Json)!;

    /// <summary>The current revision of every chain.</summary>
    public async Task<IReadOnlyList<ContractTestChain>> ChainsAsync(CancellationToken ct) =>
        [.. (await db.ContractTestChains.AsNoTracking().ToListAsync(ct)).GroupBy(r => r.ChainId).Select(g => Chain(g.MaxBy(r => r.Revision)!)).OrderBy(c => c.Name, StringComparer.Ordinal)];

    /// <summary>A chain at a revision (null = the current one).</summary>
    public async Task<ContractTestChain?> ChainAsync(Guid id, int? revision, CancellationToken ct)
    {
        var rows = db.ContractTestChains.AsNoTracking().Where(r => r.ChainId == id);
        var row = revision is { } rev ? await rows.FirstOrDefaultAsync(r => r.Revision == rev, ct) : await rows.OrderByDescending(r => r.Revision).FirstOrDefaultAsync(ct);
        return row is null ? null : Chain(row);
    }

    /// <summary>Creates a chain or saves the next revision of one. Refused with every problem found (unknown tests, outputs or inputs, sensitive
    /// outputs, cycles); a stale expected revision is a conflict.</summary>
    public async Task<ContractChainSaveResult> SaveChainAsync(Guid? id, ContractChainSave save, CancellationToken ct)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(save.Name) || save.Name.Trim().Length > 200) problems.Add("Name the chain (max 200 characters).");
        problems.AddRange(await ChainProblemsAsync(save.Steps, ct));
        ContractTestChain? current = null;
        if (id is { } existing)
        {
            current = await ChainAsync(existing, null, ct);
            if (current is null) return new(null, ["Not found."]);
            if (save.ExpectedRevision != current.Revision) return new(current, [$"The chain changed meanwhile (now r{current.Revision}); reload it."], Conflict: true);
        }
        if (problems.Count > 0) return new(null, problems);
        var now = _clock.GetUtcNow();
        var chain = new ContractTestChain
        {
            Id = current?.Id ?? Guid.NewGuid(), Name = save.Name.Trim(), Description = save.Description?.Trim(), Revision = (current?.Revision ?? 0) + 1,
            Steps = save.Steps, CreatedAt = current?.CreatedAt ?? now, UpdatedAt = now,
        };
        db.ContractTestChains.Add(new ContractTestChainRow { Id = Guid.NewGuid(), ChainId = chain.Id, Revision = chain.Revision, Name = chain.Name,
            DocumentJson = JsonSerializer.Serialize(chain, ExecutionEvidenceStore.Json), CreatedAt = now });
        await db.SaveChangesAsync(ct);
        return new(chain, []);
    }

    /// <summary>Deletes a chain with all its revisions. Runs keep their own copy of every step they executed.</summary>
    public async Task<bool> DeleteChainAsync(Guid id, CancellationToken ct)
    {
        var rows = await db.ContractTestChains.Where(r => r.ChainId == id).ToListAsync(ct);
        if (rows.Count == 0) return false;
        db.ContractTestChains.RemoveRange(rows);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<List<string>> ChainProblemsAsync(List<ContractChainStep> steps, CancellationToken ct)
    {
        var problems = new List<string>();
        if (steps.Count is 0 or > MaxChainSteps) { problems.Add($"A chain has 1 to {MaxChainSteps} steps."); return problems; }
        foreach (var dup in steps.GroupBy(s => s.StepId, StringComparer.Ordinal).Where(g => g.Count() > 1)) problems.Add($"Step id {dup.Key} is used twice.");
        var outputs = steps.GroupBy(s => s.StepId ?? "", StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.SelectMany(s => s.Extract.Select(e => e.Name)).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var step in steps)
        {
            if (!ExecutionOutputs.StepId().IsMatch(step.StepId ?? "")) problems.Add($"Step id \"{step.StepId}\": letters, digits, '_' or '-' (max 64).");
            if (await LoadAsync(step.TestId, ct) is not { } test) { problems.Add($"{step.StepId}: the test does not exist."); continue; }
            if (step.RowId is { } rowId && BoundVariants(test).All(v => v.RowId != rowId)) problems.Add($"{step.StepId}: the bound dataset has no row {rowId}.");
            if (step.Extract.Count > ExecutionOutputs.MaxExtractions) problems.Add($"{step.StepId}: at most {ExecutionOutputs.MaxExtractions} outputs.");
            problems.AddRange(step.Extract.Select(ExecutionOutputs.DefinitionProblem).OfType<string>().Select(p => $"{step.StepId}: {p}"));
            foreach (var dup in step.Extract.GroupBy(e => e.Name, StringComparer.Ordinal).Where(g => g.Count() > 1)) problems.Add($"{step.StepId}: output {dup.Key} is defined twice.");
            foreach (var input in step.Inputs)
            {
                if (test.Inputs.All(i => i.Name != input.Input)) problems.Add($"{step.StepId}: the test has no input {input.Input}.");
                if (input.FromStep == step.StepId) problems.Add($"{step.StepId}: a step cannot use its own output.");
                else if (!outputs.TryGetValue(input.FromStep, out var provided)) problems.Add($"{step.StepId}: step {input.FromStep} does not exist.");
                else if (!provided.Contains(input.Output)) problems.Add($"{step.StepId}: step {input.FromStep} provides no output {input.Output}.");
            }
            foreach (var dep in step.DependsOn.Where(d => d == step.StepId || !outputs.ContainsKey(d))) problems.Add($"{step.StepId}: dependency {dep} is not another step of the chain.");
        }
        if (problems.Count == 0 && Cycle(steps) is { } cycle) problems.Add($"The steps depend on each other in a cycle: {string.Join(" → ", cycle)}.");
        return problems;
    }

    private static IEnumerable<string> Dependencies(ContractChainStep s) => s.DependsOn.Concat(s.Inputs.Select(i => i.FromStep)).Distinct(StringComparer.Ordinal);

    /// <summary>The first dependency cycle found (as a path that returns to its start), or null.</summary>
    internal static List<string>? Cycle(IReadOnlyList<ContractChainStep> steps)
    {
        var map = steps.ToDictionary(s => s.StepId, StringComparer.Ordinal);
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var path = new List<string>();
        List<string>? Visit(string id)
        {
            if (state.TryGetValue(id, out var s)) return s == 1 ? [.. path.Skip(path.IndexOf(id)), id] : null;
            state[id] = 1; path.Add(id);
            foreach (var dep in map.TryGetValue(id, out var step) ? Dependencies(step) : [])
                if (Visit(dep) is { } found) return found;
            path.RemoveAt(path.Count - 1); state[id] = 2;
            return null;
        }
        return steps.Select(s => Visit(s.StepId)).FirstOrDefault(c => c is not null);
    }

    /// <summary>Dependency order, keeping the authored order wherever dependencies allow it (the steps are known to be acyclic).</summary>
    internal static List<ContractChainStep> Order(IReadOnlyList<ContractChainStep> steps)
    {
        var done = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<ContractChainStep>();
        while (ordered.Count < steps.Count)
        {
            var next = steps.First(s => !done.Contains(s.StepId) && Dependencies(s).All(done.Contains));
            ordered.Add(next); done.Add(next.StepId);
        }
        return ordered;
    }

    /// <summary>
    /// Runs the current revision of a chain as one Execution Evidence run, steps in dependency order. Every step must be ready (contract,
    /// environment, authentication, its own Test Data except the inputs taken from earlier steps); otherwise nothing is run. Step outputs are
    /// resolved by the run at send time; a step whose dependency did not pass is Blocked and never sent.
    /// </summary>
    public async Task<ContractTestRunResult> RunChainAsync(Guid id, ContractChainRunRequest request, CancellationToken ct)
    {
        if (await ChainAsync(id, null, ct) is not { } chain) return new(null, ["Not found."]);
        if (PathProblem(request.BasePath, "The API base path") is { } baseProblem) return new(null, [baseProblem]);
        if (PathProblem(request.GraphQlPath, "The GraphQL endpoint path") is { } gqlProblem) return new(null, [gqlProblem]);
        if (await ChainProblemsAsync(chain.Steps, ct) is { Count: > 0 } invalid) return new(null, invalid);
        var notRun = new List<string>();
        var calls = new List<ExecutionCallDefinition>();
        var contractRefs = new HashSet<string>(StringComparer.Ordinal);
        var kinds = new HashSet<ExecutionAuthenticationKind>();
        foreach (var step in Order(chain.Steps))
        {
            var test = (await LoadAsync(step.TestId, ct))!;
            var latest = await registry.LatestVersionAsync(test.ContractKey, ct) ?? 0;
            var view = await ViewAsync(test, latest, [], request.Environment, ct);
            var refusals = view.Readiness.Items.Where(i => i.State == ReadinessItemState.Blocked || i.State == ReadinessItemState.Missing && i.Area != "Test Data").Select(i => i.Text).ToList();
            if (refusals.Count > 0) { notRun.Add($"{step.StepId} ({test.Name}): {string.Join(" ", refusals)}"); continue; }
            var variants = BoundVariants(test);
            var (rowId, values, rowNames) = step.RowId is { } wanted ? variants.First(v => v.RowId == wanted) : variants[0];
            // Inputs taken from earlier steps become step placeholders the run resolves at send time.
            string? Map(string? text) => text is null ? null : step.Inputs.Aggregate(text, (t, i) => t.Replace($"{{{{dataset.{i.Input}}}}}", $"{{{{steps.{i.FromStep}.{i.Output}}}}}", StringComparison.Ordinal));
            var mapped = test with
            {
                Template = test.Template with
                {
                    Path = Map(test.Template.Path)!, Body = Map(test.Template.Body), GraphQlVariablesJson = Map(test.Template.GraphQlVariablesJson),
                    Query = test.Template.Query.ToDictionary(q => q.Key, q => Map(q.Value)!, StringComparer.Ordinal),
                    Headers = test.Template.Headers.ToDictionary(h => h.Key, h => Map(h.Value)!, StringComparer.OrdinalIgnoreCase),
                },
            };
            var (call, missing) = ContractTestRules.Materialize(mapped, values);
            if (call is null) { notRun.Add($"{step.StepId} ({test.Name}): missing {string.Join(", ", missing)}."); continue; }
            var binding = await BindingAsync(test.ContractKey, request.Environment.EnvironmentId, ct);
            if (!test.WithoutAuthentication) kinds.Add(binding?.Authentication ?? ExecutionAuthenticationKind.None);
            var basePath = (request.BasePath ?? binding?.BasePath ?? "").Trim().TrimEnd('/');
            var path = call.Protocol == ExecutionProtocol.GraphQl && (request.GraphQlPath ?? binding?.GraphQlPath) is { Length: > 0 } endpoint ? endpoint.Trim() : basePath + call.Path;
            var source = test.Binding is { } bound ? $"{bound.Alias}@v{bound.Version}" : "";
            calls.Add(call with
            {
                Path = path, Name = $"{step.StepId}: {test.Name}", OperationRef = ContractIdentity.OperationRef(test.ContractKey, test.ContractVersion, test.Operation),
                TestDataRefs = test.Binding is { } b ? [$"{b.Alias}@v{b.Version}"] : [], DefinitionVersion = ContractIdentity.DefinitionRef(test.Id, test.Revision),
                WithoutAuthentication = test.WithoutAuthentication, DatasetRow = rowId is null ? null : $"{source}#{rowId}",
                ResolvedValues = ContractTestRules.Resolved(mapped, values, name => rowNames.Contains(name) ? $"row {rowId} of {source}" : $"member of {source}"),
                StepId = step.StepId, DependsOn = [.. Dependencies(step)], Extract = step.Extract,
            });
            contractRefs.Add(ContractIdentity.ContractRef(test.ContractKey, test.ContractVersion));
        }
        if (notRun.Count > 0) return new(null, [.. notRun, "The chain was not run: every step must be ready, because later steps depend on earlier ones."]);
        if (kinds.Count > 1) return new(null, ["The chain's steps use different authentication in this environment; a chain runs with one authentication."]);
        var e = request.Environment;
        var started = await runs.StartAsync(new ExecutionRunRequest
        {
            Name = string.IsNullOrWhiteSpace(request.Name) ? $"Chain · {chain.Name}" : request.Name.Trim(),
            EnvironmentId = e.EnvironmentId, TargetUrl = e.TargetUrl, EnvironmentType = e.EnvironmentType, IsProduction = e.IsProduction, ProfileId = e.ProfileId,
            ContextFingerprint = e.ContextFingerprint, Calls = calls, ContractRefs = [.. contractRefs.Order(StringComparer.Ordinal)], ConfirmMutatingCalls = request.ConfirmMutatingCalls,
            CapturePolicy = CapturePolicy.BoundedBody, Authentication = kinds.SingleOrDefault(), ReauthenticationWaitSeconds = request.ReauthenticationWaitSeconds,
            StopOnFirstFailure = request.StopOnFirstFailure, ChainRef = ContractIdentity.ChainRef(chain.Id, chain.Revision),
        }, "Local session", ct);
        return new(started, []);
    }
}
