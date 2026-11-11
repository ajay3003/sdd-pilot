using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.TestData;
using BirkNext.Api.Services.WorkspaceTransfer;
using BirkNext.ContractTesting;
using BirkNext.ExecutionEvidence;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.ExecutionEvidence;

/// <summary>
/// Builds the immutable execution manifest of a run from the stored records it names — never from what a caller claims about them — and later
/// evaluates whether the run's result is still evidence for the current definitions. Every reference a run carries (contract versions, test and
/// chain revisions, Test Data versions, source snapshot) is resolved server-side to the stored record and its content fingerprint; references an
/// owner materialised the calls from are owner-resolved, everything else caller-declared. Evaluation reads only; it never changes a result.
/// </summary>
public sealed class ExecutionManifestService(AppDbContext db, AuthoredTestDataStore? authored = null, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public static string BindingRef(string contractKey, string environmentId) => $"binding:{contractKey}@{environmentId}";

    /// <summary>The content fingerprint of an endpoint binding as it applies to a run (effective base path, GraphQL path, authentication).</summary>
    public static string BindingFingerprint(string contractKey, string environmentId, string? basePath, string? graphQlPath, string authentication) =>
        Sha256($"{contractKey}\n{environmentId}\n{(basePath ?? "").Trim().TrimEnd('/')}\n{(graphQlPath ?? "").Trim()}\n{authentication}");

    public static string SuiteRef(string suiteKey, int revision) => $"suite:{suiteKey}@r{revision}";
    public static string CompositeRef(string contextKey, int revision) => $"composite:{contextKey}@r{revision}";

    /// <summary>The manifest of a run being created (call definitions as they will be persisted, i.e. sanitised).</summary>
    public async Task<ExecutionManifest> BuildAsync(ExecutionRunRequest request, ExecutionRunSummary summary, IReadOnlyList<ExecutionCallRecord> calls, CancellationToken ct)
    {
        var attested = request.OwnerAttestedRefs;
        ManifestDependencyOrigin Origin(string reference) => attested.Contains(reference) ? ManifestDependencyOrigin.OwnerResolved : ManifestDependencyOrigin.CallerDeclared;
        var deps = new List<ManifestDependency>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var contractRef in summary.ContractRefs.Concat(calls.Select(c => c.Definition.OperationRef).OfType<string>().Select(o => o.Split('#')[0])).Distinct(StringComparer.Ordinal))
            if (seen.Add(contractRef)) deps.Add(await ContractAsync(contractRef, Origin(contractRef), ct));
        foreach (var definitionRef in calls.Select(c => c.Definition.DefinitionVersion).OfType<string>().Distinct(StringComparer.Ordinal))
            if (seen.Add(definitionRef)) deps.Add(await DefinitionAsync(definitionRef, Origin(definitionRef), ct));
        if (summary.ChainRef is { Length: > 0 } chainRef && seen.Add(chainRef)) deps.Add(await ChainAsync(chainRef, Origin(chainRef), ct));
        foreach (var dataRef in calls.SelectMany(c => c.Definition.TestDataRefs).Distinct(StringComparer.Ordinal))
            if (seen.Add(dataRef)) deps.Add(TestDataDependency(dataRef, Origin(dataRef)));
        if (summary.SourceSnapshotId is { } snapshotId && seen.Add($"source-snapshot:{snapshotId:N}"))
        {
            var exists = await db.IqrSourceSnapshots.AsNoTracking().AnyAsync(s => s.Id == snapshotId, ct);
            deps.Add(new()
            {
                Kind = ManifestDependencyKind.SourceSnapshot, Ref = $"source-snapshot:{snapshotId:N}", Key = snapshotId.ToString("D"), Origin = ManifestDependencyOrigin.CallerDeclared,
                Resolution = exists ? ManifestResolution.Resolved : ManifestResolution.NotFound, Detail = exists ? "Named by the run's caller." : "Not found in the source snapshot store.",
            });
        }
        foreach (var d in request.OwnerDependencies)
            if (seen.Add(d.Ref)) deps.Add(d);

        var environment = new ManifestEnvironment(summary.EnvironmentId, summary.EnvironmentType, summary.TargetOrigin, summary.AuthenticationMode, summary.AuthenticationReference);
        var (version, commit) = FullBackupService.ApplicationIdentity();
        var build = BirkNext.Api.Services.SystemInfo.BuildInfoService.Current;
        var ordered = deps.OrderBy(d => d.Kind).ThenBy(d => d.Ref, StringComparer.Ordinal).ToList();
        return new ExecutionManifest
        {
            ManifestId = Guid.NewGuid(), Owner = ExecutionManifestSchema.ExecutionEvidenceOwner, RunId = summary.RunId, CreatedAt = summary.StartedAt, Initiator = summary.Initiator,
            Environment = environment, Dependencies = ordered,
            Build = new(version, commit) { Channel = build.Channel.ToString(), BuildId = build.BuildId, InformationalVersion = build.InformationalVersion }, DependencyFingerprint = DependencyFingerprint(environment, ordered),
        };
    }

    /// <summary>Stable over the environment and the dependencies only (ordered); independent of run id, time, initiator and build.</summary>
    public static string DependencyFingerprint(ManifestEnvironment environment, IEnumerable<ManifestDependency> dependencies) =>
        Sha256(JsonSerializer.Serialize(new { environment, dependencies = dependencies.OrderBy(d => d.Kind).ThenBy(d => d.Ref, StringComparer.Ordinal).ToList() }, ExecutionEvidenceStore.Json));

    public static ExecutionManifestRow Row(ExecutionManifest m) => new()
    {
        Id = m.ManifestId, Owner = m.Owner, RunId = m.RunId, SchemaVersion = m.SchemaVersion, DependencyFingerprint = m.DependencyFingerprint, CreatedAt = m.CreatedAt,
        DocumentJson = JsonSerializer.Serialize(m, ExecutionEvidenceStore.Json),
    };

    public async Task<ExecutionManifest?> GetAsync(Guid runId, CancellationToken ct, string owner = ExecutionManifestSchema.ExecutionEvidenceOwner) =>
        await db.ExecutionManifests.AsNoTracking().Where(m => m.Owner == owner && m.RunId == runId).Select(m => m.DocumentJson).FirstOrDefaultAsync(ct) is { } json
            ? JsonSerializer.Deserialize<ExecutionManifest>(json, ExecutionEvidenceStore.Json) : null;

    /// <summary>Whether the run's result is evidence for what is current now. The result itself is returned unchanged.</summary>
    public async Task<ExecutionApplicability?> ApplicabilityAsync(Guid runId, CancellationToken ct)
    {
        var run = await db.ExecutionRuns.AsNoTracking().Where(r => r.Id == runId).Select(r => new { r.State }).FirstOrDefaultAsync(ct);
        if (run is null) return null;
        ExecutionState? state = Enum.TryParse<ExecutionState>(run.State, out var s) ? s : null;
        var manifest = await GetAsync(runId, ct);
        if (manifest is null)
            return new() { RunId = runId, Owner = ExecutionManifestSchema.ExecutionEvidenceOwner, ResultState = state, Applicability = ResultApplicability.Unknown, EvaluatedAt = _clock.GetUtcNow(),
                Explanation = "This run has no execution manifest (it was created before manifests were recorded), so what it ran against cannot be compared with what is current." };
        var currency = new List<DependencyCurrency>();
        foreach (var d in manifest.Dependencies) currency.Add(await CurrencyAsync(d, manifest.Environment.EnvironmentId, ct));
        var (applicability, explanation) = ExecutionApplicabilityRules.Combine(currency);
        return new()
        {
            RunId = runId, Owner = manifest.Owner, ResultState = state, Applicability = applicability, Explanation = explanation, DependencyFingerprint = manifest.DependencyFingerprint,
            EvaluatedAt = _clock.GetUtcNow(), Dependencies = currency,
        };
    }

    // ── Resolution at run time ─────────────────────────────────────────────────────────────────────────────────────────

    private static (string Key, int Version)? ParseVersioned(string reference, string marker)
    {
        var at = reference.LastIndexOf(marker, StringComparison.Ordinal);
        return at > 0 && int.TryParse(reference[(at + marker.Length)..], out var v) ? (reference[..at], v) : null;
    }

    private async Task<ManifestDependency> ContractAsync(string contractRef, ManifestDependencyOrigin origin, CancellationToken ct)
    {
        var dep = new ManifestDependency { Kind = ManifestDependencyKind.Contract, Ref = contractRef, Origin = origin, Resolution = ManifestResolution.NotFound };
        if (ParseVersioned(contractRef, "@v") is not (string key, int version)) return dep with { Detail = "Not a contract reference (key@vN)." };
        var sha = await db.ApiContractVersions.AsNoTracking().Join(db.ApiContracts.AsNoTracking().Where(c => c.Key == key), v => v.ContractId, c => c.Id, (v, c) => v)
            .Where(v => v.Version == version).Select(v => v.Sha256).FirstOrDefaultAsync(ct);
        return sha is null ? dep with { Key = key, Version = version, Detail = "No such contract version in the registry." }
            : dep with { Key = key, Version = version, ContentFingerprint = sha, Resolution = ManifestResolution.Resolved };
    }

    private static Guid? TestIdOf(string definitionRef) =>
        definitionRef.StartsWith("contract-test:", StringComparison.Ordinal) && ParseVersioned(definitionRef, "@r") is (string key, _) && Guid.TryParseExact(key["contract-test:".Length..], "N", out var id) ? id : null;

    private async Task<ManifestDependency> DefinitionAsync(string definitionRef, ManifestDependencyOrigin origin, CancellationToken ct)
    {
        var dep = new ManifestDependency { Kind = ManifestDependencyKind.ContractTest, Ref = definitionRef, Origin = origin, Resolution = ManifestResolution.NotFound };
        if (TestIdOf(definitionRef) is not { } testId || ParseVersioned(definitionRef, "@r") is not (_, int revision)) return dep with { Detail = "Not a contract test reference." };
        var json = await TestRevisionJsonAsync(testId, revision, ct);
        return json is null ? dep with { Key = testId.ToString("D"), Version = revision, Detail = "No such test revision." }
            : dep with { Key = testId.ToString("D"), Version = revision, ContentFingerprint = Sha256(json), Resolution = ManifestResolution.Resolved };
    }

    /// <summary>The stored document of one test revision; a test from before revision history counts only for its current revision.</summary>
    private async Task<string?> TestRevisionJsonAsync(Guid testId, int revision, CancellationToken ct) =>
        await db.ContractTestRevisions.AsNoTracking().Where(r => r.TestId == testId && r.Revision == revision).Select(r => r.DocumentJson).FirstOrDefaultAsync(ct)
        ?? (await db.ContractTests.AsNoTracking().Where(t => t.Id == testId).Select(t => t.DocumentJson).FirstOrDefaultAsync(ct) is { } current
            && JsonSerializer.Deserialize<ContractTestDefinition>(current, ExecutionEvidenceStore.Json)?.Revision == revision ? current : null);

    private async Task<ManifestDependency> ChainAsync(string chainRef, ManifestDependencyOrigin origin, CancellationToken ct)
    {
        var dep = new ManifestDependency { Kind = ManifestDependencyKind.ContractChain, Ref = chainRef, Origin = origin, Resolution = ManifestResolution.NotFound };
        if (!chainRef.StartsWith("contract-chain:", StringComparison.Ordinal) || ParseVersioned(chainRef, "@r") is not (string key, int revision)
            || !Guid.TryParseExact(key["contract-chain:".Length..], "N", out var chainId)) return dep with { Detail = "Not a chain reference." };
        var json = await db.ContractTestChains.AsNoTracking().Where(c => c.ChainId == chainId && c.Revision == revision).Select(c => c.DocumentJson).FirstOrDefaultAsync(ct);
        return json is null ? dep with { Key = chainId.ToString("D"), Version = revision, Detail = "No such chain revision." }
            : dep with { Key = chainId.ToString("D"), Version = revision, ContentFingerprint = Sha256(json), Resolution = ManifestResolution.Resolved };
    }

    private ManifestDependency TestDataDependency(string dataRef, ManifestDependencyOrigin origin)
    {
        var dep = new ManifestDependency { Kind = ManifestDependencyKind.TestData, Ref = dataRef, Origin = origin, Resolution = ManifestResolution.NotFound };
        if (ParseVersioned(dataRef, "@v") is not (string alias, int version)) return dep with { Detail = "Not a Test Data reference (alias@vN)." };
        var v = authored?.Find(alias)?.Versions.FirstOrDefault(x => x.Version == version);
        return v is null ? dep with { Key = alias, Version = version, Detail = "No such authored Test Data version." }
            : dep with { Key = alias, Version = version, ContentFingerprint = v.Fingerprint, Resolution = ManifestResolution.Resolved };
    }

    // ── Currency now ───────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<DependencyCurrency> CurrencyAsync(ManifestDependency d, string environmentId, CancellationToken ct)
    {
        if (d.Resolution == ManifestResolution.NotFound)
            return new(d, DependencyCurrencyState.NotVerifiable, null, null, $"{d.Ref} could not be resolved when the run started");
        if (d.Origin == ManifestDependencyOrigin.CallerDeclared)
            return new(d, DependencyCurrencyState.NotVerifiable, null, null, $"{d.Ref} was named by the caller, not materialised by its owner");
        switch (d.Kind)
        {
            case ManifestDependencyKind.Contract:
            {
                var versions = await db.ApiContractVersions.AsNoTracking().Join(db.ApiContracts.AsNoTracking().Where(c => c.Key == d.Key), v => v.ContractId, c => c.Id, (v, c) => v)
                    .Select(v => new { v.Version, v.Sha256, v.Hidden }).ToListAsync(ct);
                var pinned = versions.FirstOrDefault(v => v.Version == d.Version);
                if (pinned is null) return new(d, DependencyCurrencyState.Removed, null, null, $"contract {d.Ref} no longer exists");
                if (pinned.Sha256 != d.ContentFingerprint) return new(d, DependencyCurrencyState.Changed, d.Ref, pinned.Sha256, $"contract {d.Ref} content differs from the run");
                var latest = versions.Where(v => !v.Hidden).OrderByDescending(v => v.Version).FirstOrDefault();
                return latest is not null && latest.Version > d.Version
                    ? new(d, DependencyCurrencyState.Superseded, ContractIdentity.ContractRef(d.Key!, latest.Version), latest.Sha256, $"contract {d.Key} is now v{latest.Version} (the run used v{d.Version})")
                    : new(d, DependencyCurrencyState.Unchanged, d.Ref, pinned.Sha256, $"contract {d.Ref} is current");
            }
            case ManifestDependencyKind.ContractTest:
            {
                var testId = Guid.Parse(d.Key!);
                var currentJson = await db.ContractTests.AsNoTracking().Where(t => t.Id == testId).Select(t => t.DocumentJson).FirstOrDefaultAsync(ct);
                if (currentJson is null) return new(d, DependencyCurrencyState.Removed, null, null, $"test {Short(testId)} was deleted");
                var current = JsonSerializer.Deserialize<ContractTestDefinition>(currentJson, ExecutionEvidenceStore.Json)!;
                if (current.Revision != d.Version)
                    return new(d, DependencyCurrencyState.Superseded, ContractIdentity.DefinitionRef(testId, current.Revision), null, $"test \"{current.Name}\" is now r{current.Revision} (the run used r{d.Version})");
                var json = await TestRevisionJsonAsync(testId, d.Version!.Value, ct);
                return json is not null && Sha256(json) == d.ContentFingerprint
                    ? new(d, DependencyCurrencyState.Unchanged, d.Ref, d.ContentFingerprint, $"test \"{current.Name}\" r{d.Version} is current")
                    : new(d, DependencyCurrencyState.Changed, d.Ref, json is null ? null : Sha256(json), $"test \"{current.Name}\" r{d.Version} content differs from the run");
            }
            case ManifestDependencyKind.ContractChain:
            {
                var chainId = Guid.Parse(d.Key!);
                var revisions = await db.ContractTestChains.AsNoTracking().Where(c => c.ChainId == chainId).Select(c => new { c.Revision, c.DocumentJson }).ToListAsync(ct);
                var pinned = revisions.FirstOrDefault(r => r.Revision == d.Version);
                if (pinned is null) return new(d, DependencyCurrencyState.Removed, null, null, $"chain revision {d.Ref} no longer exists");
                if (Sha256(pinned.DocumentJson) != d.ContentFingerprint) return new(d, DependencyCurrencyState.Changed, d.Ref, Sha256(pinned.DocumentJson), $"chain {d.Ref} content differs from the run");
                var latest = revisions.Max(r => r.Revision);
                return latest > d.Version
                    ? new(d, DependencyCurrencyState.Superseded, ContractIdentity.ChainRef(chainId, latest), null, $"the chain is now r{latest} (the run used r{d.Version})")
                    : new(d, DependencyCurrencyState.Unchanged, d.Ref, d.ContentFingerprint, $"chain r{d.Version} is current");
            }
            case ManifestDependencyKind.TestData:
            {
                var resource = authored?.Find(d.Key!);
                var pinned = resource?.Versions.FirstOrDefault(v => v.Version == d.Version);
                if (pinned is null) return new(d, DependencyCurrencyState.Removed, null, null, $"Test Data {d.Ref} no longer exists");
                if (pinned.Fingerprint != d.ContentFingerprint) return new(d, DependencyCurrencyState.Changed, d.Ref, pinned.Fingerprint, $"Test Data {d.Ref} content differs from the run");
                return resource!.Current.Version > d.Version
                    ? new(d, DependencyCurrencyState.Superseded, $"{d.Key}@v{resource.Current.Version}", resource.Current.Fingerprint, $"Test Data {d.Key} is now v{resource.Current.Version} (the run used v{d.Version})")
                    : new(d, DependencyCurrencyState.Unchanged, d.Ref, pinned.Fingerprint, $"Test Data {d.Ref} is current");
            }
            case ManifestDependencyKind.EndpointBinding:
            {
                var binding = await db.ContractEndpointBindings.AsNoTracking().Join(db.ApiContracts.AsNoTracking().Where(c => c.Key == d.Key), b => b.ContractId, c => c.Id, (b, c) => b)
                    .Where(b => b.EnvironmentId == environmentId).FirstOrDefaultAsync(ct);
                var now = BindingFingerprint(d.Key!, environmentId, binding?.BasePath, binding?.GraphQlPath, binding?.Authentication ?? nameof(ExecutionAuthenticationKind.None));
                return now == d.ContentFingerprint
                    ? new(d, DependencyCurrencyState.Unchanged, d.Ref, now, $"the endpoint binding of {d.Key} is unchanged")
                    : new(d, DependencyCurrencyState.Changed, d.Ref, now, $"the endpoint binding of {d.Key} changed (base path, GraphQL path or authentication)");
            }
            case ManifestDependencyKind.IntegrationSuite:
            {
                var latest = await db.IntegrationSuites.AsNoTracking().Where(s => s.SuiteKey == d.Key).OrderByDescending(s => s.Revision).Select(s => new { s.Revision, s.Fingerprint }).FirstOrDefaultAsync(ct);
                var pinned = await db.IntegrationSuites.AsNoTracking().AnyAsync(s => s.SuiteKey == d.Key && s.Revision == d.Version, ct);
                if (!pinned || latest is null) return new(d, DependencyCurrencyState.Removed, null, null, $"suite revision {d.Ref} no longer exists");
                return latest.Revision > d.Version
                    ? new(d, DependencyCurrencyState.Superseded, SuiteRef(d.Key!, latest.Revision), latest.Fingerprint, $"the suite is now r{latest.Revision} (the run used r{d.Version})")
                    : new(d, DependencyCurrencyState.Unchanged, d.Ref, latest.Fingerprint, $"suite r{d.Version} is current");
            }
            case ManifestDependencyKind.CompositeContext:
            {
                var revisions = await db.CompositeSourceContexts.AsNoTracking().Where(c => c.ContextKey == d.Key).Select(c => new { c.Revision, c.Fingerprint }).ToListAsync(ct);
                var pinned = revisions.FirstOrDefault(r => r.Revision == d.Version);
                if (pinned is null) return new(d, DependencyCurrencyState.Removed, null, null, $"composite context {d.Ref} no longer exists");
                var latest = revisions.MaxBy(r => r.Revision)!;
                return latest.Revision > d.Version
                    ? new(d, DependencyCurrencyState.Superseded, CompositeRef(d.Key!, latest.Revision), latest.Fingerprint, $"the composite context is now r{latest.Revision} (the run used r{d.Version})")
                    : new(d, DependencyCurrencyState.Unchanged, d.Ref, pinned.Fingerprint, $"composite context r{d.Version} is current");
            }
            default:
                return new(d, DependencyCurrencyState.NotVerifiable, null, null, $"{d.Ref} has no currency rule");
        }
    }

    private static string Short(Guid id) => id.ToString("N")[..8];
}
