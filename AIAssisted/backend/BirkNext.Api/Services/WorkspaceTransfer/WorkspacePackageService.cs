using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.ContractTesting;
using BirkNext.Api.Services.CriticalE2E;
using BirkNext.Api.Services.ExternalIntegrations;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Services.TestData;
using BirkNext.CriticalE2E;
using BirkNext.Integrations;
using BirkNext.WorkspaceTransfer;
using Microsoft.EntityFrameworkCore;
using Path = System.IO.Path;

namespace BirkNext.Api.Services.WorkspaceTransfer;

public sealed class WorkspacePackageOptions
{
    /// <summary>Exports and import staging. Default: {ContentRoot}/App_Data/transfer (owner-only).</summary>
    public string? Directory { get; set; }
    public long MaxUploadBytes { get; set; } = 512L << 20;
    public int MaxEvidenceRuns { get; set; } = 200;
    public TimeSpan StagingLifetime { get; set; } = TimeSpan.FromHours(24);
}

/// <summary>
/// Portable workspace packages (operations B, C and D): project, integration and diagnostic .birknext packages. Each package is the server-side
/// dependency closure of one selected project (Source Analysis repository key) or one integration environment. A caller can never name the
/// individual records that go in, so no unrelated or cross-project data is reachable through a package. Export reads only. Import is two-phase:
/// <see cref="PreviewImportAsync"/> writes nothing but its operation record; <see cref="ApplyImportAsync"/> needs the plan fingerprint and an
/// unchanged destination. Nothing imported connects, executes or becomes approved.
/// </summary>
public sealed partial class WorkspacePackageService
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly Regex GuidPattern = new(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.Compiled);

    private readonly AppDbContext _db;
    private readonly ICriticalE2EStore _flows;
    private readonly TestDataRegistryStore _testData;
    private readonly AuthoredTestDataStore _authored;
    private readonly EnvironmentSetupService _setup;
    private readonly IReadOnlyList<IIntegrationJourneyPack> _packs;
    private readonly WorkspacePackageOptions _options;
    private readonly ILogger _logger;
    private readonly TimeProvider _clock;

    public WorkspacePackageService(AppDbContext db, ICriticalE2EStore flows, TestDataRegistryStore testData, AuthoredTestDataStore authored, EnvironmentSetupService setup,
        IEnumerable<IIntegrationJourneyPack> packs, WorkspacePackageOptions options, string directory, ILogger<WorkspacePackageService> logger, TimeProvider? clock = null)
    {
        _db = db;
        _flows = flows;
        _testData = testData;
        _authored = authored;
        _setup = setup;
        _packs = packs.ToList();
        _options = options;
        Directory = directory;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public string Directory { get; }
    private string Exports => Path.Combine(Directory, "exports");
    private string Staging => Path.Combine(Directory, "staging");

    /// <summary>A stable, non-secret id of this installation (recorded as the exporter / origin of imported history).</summary>
    public string InstanceId()
    {
        var path = Path.Combine(Directory, "instance-id");
        EnsureDirectory(Directory);
        if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: 32 } existing) return existing;
        var id = Guid.NewGuid().ToString("N");
        File.WriteAllText(path, id);
        return id;
    }

    // ───────────────────────────── Export: closure ─────────────────────────────

    private sealed class Closure
    {
        public List<PackageEntry> Entries { get; } = [];
        public List<DependencyEntry> Dependencies { get; } = [];
        public HashSet<string> Capabilities { get; } = new(StringComparer.Ordinal);
        public List<string> Excluded { get; } = [];
        public List<string> Projects { get; } = [];
        public List<string> Environments { get; } = [];
        public HashSet<string> Keys { get; } = new(StringComparer.Ordinal);

        public bool Has(string type, string id) => Keys.Contains(PackageIds.Key(type, id));

        public void Add(string type, string logicalId, string label, object payload, Portability portability, string? project = null, string? environment = null,
            int? revision = null, IEnumerable<PortableObjectRef>? dependencies = null)
        {
            if (!Keys.Add(PackageIds.Key(type, logicalId))) return;
            var node = JsonSerializer.SerializeToNode(payload, payload.GetType(), Web)!;
            Entries.Add(new PackageEntry(new PortableObject
            {
                Type = type, LogicalId = logicalId, Label = label, Revision = revision, OwningProject = project, EnvironmentId = environment, Portability = portability,
                Fingerprint = PackageIds.Fingerprint(node), Path = PackageIds.EntryName(type, logicalId), Dependencies = dependencies?.ToList() ?? [],
            }, node));
        }
    }

    public async Task<ExportPreview> PreviewExportAsync(ExportRequest request, CancellationToken ct)
    {
        var closure = await BuildClosureAsync(request, ct);
        var security = ScanAll(closure.Entries);
        var blockers = Blockers(closure, security, request);
        return new ExportPreview
        {
            Kind = request.Kind, Scope = ScopeLabel(request),
            Counts = closure.Entries.GroupBy(e => e.Object.Type).ToDictionary(g => g.Key, g => g.Count()),
            Objects = closure.Entries.Select(e => e.Object).ToList(), Dependencies = closure.Dependencies, RequiredCapabilities = closure.Capabilities.Order().ToList(),
            Security = security, Excluded = closure.Excluded, Blockers = blockers, Stages = await StagesAsync(closure, ct),
            Completeness = closure.Dependencies.Any(d => d.Required && d.Status is DependencyStatus.Missing or DependencyStatus.Unauthorized) ? "PARTIAL" : "COMPLETE",
        };
    }

    private static string ScopeLabel(ExportRequest r) => r.Kind switch
    {
        TransferKind.ProjectWorkspace => $"Project {r.ProjectKey}",
        TransferKind.IntegrationWorkspace => $"Integration environment {r.EnvironmentId}",
        TransferKind.Diagnostic => $"Diagnostic: {(r.SuiteKey ?? r.FlowId ?? r.ProjectKey ?? r.EnvironmentId)}",
        _ => r.Kind.ToString(),
    };

    private static List<string> Blockers(Closure closure, IReadOnlyList<SecurityFinding> security, ExportRequest request)
    {
        var blockers = new List<string>();
        if (closure.Entries.Count == 0) blockers.Add("Nothing exists in this scope to export.");
        if (security.Any(f => f.Severity == PackageSecretScanner.Blocking))
            blockers.Add($"{security.Count(f => f.Severity == PackageSecretScanner.Blocking)} value(s) look like secrets; the package cannot be created until they are removed at their source.");
        if (security.Any(f => f.Severity == PackageSecretScanner.Sensitive) && !request.AcknowledgeSensitive && request.Kind != TransferKind.Diagnostic)
            blockers.Add($"{security.Count(f => f.Severity == PackageSecretScanner.Sensitive)} identifier-shaped value(s) found; confirm they are synthetic to continue.");
        var missing = closure.Dependencies.Where(d => d.Required && d.Status is DependencyStatus.Missing or DependencyStatus.Unauthorized).ToList();
        if (missing.Count > 0 && !request.AllowPartial)
            blockers.Add($"{missing.Count} required dependenc{(missing.Count == 1 ? "y is" : "ies are")} missing; export as an explicitly PARTIAL package or resolve them first.");
        return blockers;
    }

    private static List<SecurityFinding> ScanAll(IEnumerable<PackageEntry> entries) =>
        entries.SelectMany(e => PackageSecretScanner.Scan($"{e.Object.Type}:{e.Object.LogicalId}", e.Payload)).Take(500).ToList();

    private async Task<Closure> BuildClosureAsync(ExportRequest request, CancellationToken ct)
    {
        var closure = new Closure();
        switch (request.Kind)
        {
            case TransferKind.ProjectWorkspace:
                if (string.IsNullOrWhiteSpace(request.ProjectKey)) throw new TransferRequestException("SCOPE", "Choose a project.");
                closure.Projects.Add(request.ProjectKey);
                await AddProjectAsync(closure, request.ProjectKey, request.IncludeEvidence, ct);
                break;
            case TransferKind.IntegrationWorkspace:
                if (string.IsNullOrWhiteSpace(request.EnvironmentId)) throw new TransferRequestException("SCOPE", "Choose an integration environment.");
                closure.Environments.Add(request.EnvironmentId);
                await AddIntegrationAsync(closure, request.EnvironmentId, request.IncludeEvidence, request.TargetProfileJson, ct);
                break;
            case TransferKind.Diagnostic:
                await AddDiagnosticAsync(closure, request, ct);
                break;
            default:
                throw new TransferRequestException("SCOPE", "Full backups are created from Backup, not as a package.");
        }
        closure.Excluded.AddRange(
        [
            "Secrets, tokens, cookies, passwords, client secrets, PATs and private keys (never stored as portable data)",
            "Browser Companion pairings, live tabs and browser profiles",
            "Original source archives (BirkNext never keeps them after import; the derived snapshot travels instead)",
            "Synthetic-key reservations (CDC / Active Event floors stay with the installation that used them)",
            request.IncludeEvidence ? "Evidence beyond the most recent runs in scope" : "Historical execution evidence (not selected)",
        ]);
        return closure;
    }

    private static async Task<List<(IqrSourceSnapshotRecord Record, IqrSourceSnapshot Snapshot)>> SourceSnapshotsAsync(AppDbContext db, CancellationToken ct)
    {
        var rows = await db.IqrSourceSnapshots.AsNoTracking().Where(r => r.IntegrationId == "source-analysis").ToListAsync(ct);
        return rows.Select(r => (r, JsonSerializer.Deserialize<IqrSourceSnapshot>(r.EvidenceJson, Web)!)).ToList();
    }

    private async Task AddSnapshotAsync(Closure closure, Guid id, CancellationToken ct, PortableObjectRef? from = null, bool required = true)
    {
        if (closure.Has(PackageTypes.Snapshot, id.ToString())) return;
        var record = await _db.IqrSourceSnapshots.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (record is null)
        {
            if (from is not null) closure.Dependencies.Add(new(from, new(PackageTypes.Snapshot, id.ToString()), DependencyStatus.Missing, required, "The source snapshot no longer exists in this installation."));
            return;
        }
        var snapshot = JsonSerializer.Deserialize<IqrSourceSnapshot>(record.EvidenceJson, Web)!;
        var schema = await _db.SourceSchemaArtifacts.AsNoTracking().FirstOrDefaultAsync(s => s.SnapshotId == id, ct);
        var documents = await _db.SourceContractDocuments.AsNoTracking().Where(d => d.SnapshotId == id).OrderBy(d => d.Path).ToListAsync(ct);
        var project = ContractRegistryService.ProjectKeyOf(snapshot);
        closure.Add(PackageTypes.Snapshot, id.ToString(), $"{snapshot.Archive.FileName} ({record.AnalyzedAt:yyyy-MM-dd HH:mm})",
            new SnapshotPayload(record, schema, documents), Portability.Portable, project: project);
        if (from is not null) closure.Dependencies.Add(new(from, new(PackageTypes.Snapshot, id.ToString()), DependencyStatus.Included, required, $"{snapshot.Archive.FileName} (project {project})"));
    }

    private async Task AddProjectAsync(Closure closure, string projectKey, bool evidence, CancellationToken ct)
    {
        var snapshots = (await SourceSnapshotsAsync(_db, ct)).Where(s => string.Equals(ContractRegistryService.ProjectKeyOf(s.Snapshot), projectKey, StringComparison.Ordinal)).ToList();
        foreach (var s in snapshots.OrderBy(s => s.Record.AnalyzedAt)) await AddSnapshotAsync(closure, s.Record.Id, ct);
        var snapshotIds = snapshots.Select(s => s.Record.Id).ToHashSet();

        foreach (var contract in await _db.ApiContracts.AsNoTracking().Where(c => c.ProjectKey == projectKey).OrderBy(c => c.Key).ToListAsync(ct))
            await AddContractAsync(closure, contract, environmentId: null, ct);

        foreach (var rules in await _db.ParquetRuleSets.AsNoTracking().Where(r => r.ProjectKey == projectKey).ToListAsync(ct))
            closure.Add(PackageTypes.ParquetRules, $"{rules.ProjectKey}:{rules.ContractKey}@{rules.ContractVersion}#v{rules.Version}", $"{rules.Name} v{rules.Version}", rules,
                Portability.Portable, project: projectKey, revision: rules.Version);

        foreach (var suite in await _db.IntegrationSuites.AsNoTracking().Where(s => snapshotIds.Contains(s.SnapshotId)).ToListAsync(ct))
            AddSuite(closure, suite, projectKey);

        foreach (var context in await _db.CompositeSourceContexts.AsNoTracking().Where(c => c.OwningProject == projectKey).ToListAsync(ct))
            await AddCompositeAsync(closure, context, includeOtherProjects: false, ct);

        foreach (var association in await _db.SourceRepositoryAssociations.AsNoTracking()
                     .Where(a => a.RevokedAt == null && (a.RepositoryKeyA == projectKey || a.RepositoryKeyB == projectKey)).ToListAsync(ct))
            closure.Add(PackageTypes.Association, $"{association.RepositoryKeyA}|{association.RepositoryKeyB}", $"{association.RepositoryA} ↔ {association.RepositoryB}", association,
                Portability.HistoricalDecision, project: projectKey);

        AddTestDataForSnapshots(closure, snapshotIds, projectKey);
        if (evidence) await AddSuiteEvidenceAsync(closure, ct);
    }

    private async Task AddContractAsync(Closure closure, ApiContractRow contract, string? environmentId, CancellationToken ct)
    {
        var logical = contract.Key;
        if (closure.Has(PackageTypes.Contract, logical)) return;
        var versions = await _db.ApiContractVersions.AsNoTracking().Where(v => v.ContractId == contract.Id).OrderBy(v => v.Version).ToListAsync(ct);
        var tests = await _db.ContractTests.AsNoTracking().Where(t => t.ContractId == contract.Id).OrderBy(t => t.Name).ToListAsync(ct);
        var testIds = tests.Select(t => t.Id).ToList();
        var revisions = await _db.ContractTestRevisions.AsNoTracking().Where(r => testIds.Contains(r.TestId)).OrderBy(r => r.TestId).ThenBy(r => r.Revision).ToListAsync(ct);
        // Endpoint bindings belong to an environment: only an integration package for that environment carries them.
        var bindings = environmentId is null ? [] : await _db.ContractEndpointBindings.AsNoTracking().Where(b => b.ContractId == contract.Id && b.EnvironmentId == environmentId).ToListAsync(ct);
        var me = new PortableObjectRef(PackageTypes.Contract, logical);
        var deps = versions.Where(v => v.SourceSnapshotId is not null).Select(v => new PortableObjectRef(PackageTypes.Snapshot, v.SourceSnapshotId!.Value.ToString())).Distinct().ToList();
        closure.Add(PackageTypes.Contract, logical, $"{contract.Name} ({versions.Count} version{(versions.Count == 1 ? "" : "s")}, {tests.Count} test{(tests.Count == 1 ? "" : "s")})",
            new ContractPayload(contract, versions, tests, revisions, bindings), bindings.Count > 0 ? Portability.EnvironmentDependent : Portability.Portable,
            project: contract.ProjectKey, environment: environmentId, revision: versions.Count == 0 ? null : versions.Max(v => v.Version), dependencies: deps);
        foreach (var dep in deps) await AddSnapshotAsync(closure, Guid.Parse(dep.LogicalId), ct, me);
        foreach (var decision in await _db.ContractReviewDecisions.AsNoTracking().Where(d => d.ContractId == contract.Id).ToListAsync(ct))
            AddHistory(closure, "contract-review-decision", decision.Id.ToString(), contract.ProjectKey, null, decision.DecidedAt, decision);
    }

    private static void AddSuite(Closure closure, IntegrationSuiteRecord suite, string? project) =>
        closure.Add(PackageTypes.Suite, $"{suite.SuiteKey}@{suite.Revision}", $"{suite.SuiteKey} r{suite.Revision}", suite, Portability.Portable, project: project,
            environment: suite.EnvironmentId, revision: suite.Revision, dependencies: [new(PackageTypes.Snapshot, suite.SnapshotId.ToString())]);

    private async Task AddCompositeAsync(Closure closure, CompositeSourceContextRecord context, bool includeOtherProjects, CancellationToken ct)
    {
        var logical = $"{context.ContextKey}@{context.Revision}";
        if (closure.Has(PackageTypes.Composite, logical)) return;
        var me = new PortableObjectRef(PackageTypes.Composite, logical);
        var referenced = GuidPattern.Matches(context.DefinitionJson).Select(m => Guid.Parse(m.Value)).Distinct().ToList();
        var existing = await _db.IqrSourceSnapshots.AsNoTracking().Where(s => referenced.Contains(s.Id)).Select(s => s.Id).ToListAsync(ct);
        closure.Add(PackageTypes.Composite, logical, $"{context.ContextKey} r{context.Revision}", context, Portability.Portable, project: context.OwningProject, revision: context.Revision,
            dependencies: existing.Select(id => new PortableObjectRef(PackageTypes.Snapshot, id.ToString())));
        foreach (var id in existing)
        {
            if (closure.Has(PackageTypes.Snapshot, id.ToString())) { closure.Dependencies.Add(new(me, new(PackageTypes.Snapshot, id.ToString()), DependencyStatus.Included, true, "Stage source")); continue; }
            if (includeOtherProjects) { await AddSnapshotAsync(closure, id, ct, me); continue; }
            // Another project's snapshot: never pulled into a project package (no cross-project export without an integration scope).
            closure.Dependencies.Add(new(me, new(PackageTypes.Snapshot, id.ToString()), DependencyStatus.ExternalReference, true,
                "A stage source of another project: referenced by id and archive fingerprint, not exported with this project. Export the integration to carry every stage."));
        }
    }

    private void AddTestDataForSnapshots(Closure closure, IReadOnlySet<Guid> snapshotIds, string? projectKey)
    {
        foreach (var asset in _testData.Assets().Where(a => a.SnapshotId is { } s && snapshotIds.Contains(s) || projectKey is not null && a.RepositoryKey == projectKey))
            closure.Add(PackageTypes.TestDataAsset, asset.Alias, asset.Label ?? asset.Alias, asset, asset.EnvironmentId is null ? Portability.Portable : Portability.EnvironmentDependent,
                project: projectKey, environment: asset.EnvironmentId);
    }

    private async Task AddSuiteEvidenceAsync(Closure closure, CancellationToken ct)
    {
        var suiteKeys = closure.Entries.Where(e => e.Object.Type == PackageTypes.Suite).Select(e => e.Object.LogicalId.Split('@')[0]).Distinct().ToList();
        var runs = await _db.IntegrationSuiteRuns.AsNoTracking().Where(r => suiteKeys.Contains(r.SuiteKey)).OrderByDescending(r => r.StartedAt).Take(_options.MaxEvidenceRuns).ToListAsync(ct);
        foreach (var run in runs)
        {
            AddHistory(closure, "integration-suite-run", run.Id.ToString(), null, null, run.StartedAt, run);
            var execution = await _db.ExecutionRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == run.RunId, ct);
            if (execution is null) continue;
            var calls = await _db.ExecutionCalls.AsNoTracking().Where(c => c.RunId == run.RunId).OrderBy(c => c.Sequence).ToListAsync(ct);
            AddHistory(closure, "execution-run", execution.Id.ToString(), null, execution.EnvironmentId, execution.StartedAt, new { run = execution, calls });
        }
    }

    private static void AddHistory(Closure closure, string kind, string originalId, string? project, string? environment, DateTimeOffset? at, object document) =>
        closure.Add(PackageTypes.History, $"{kind}:{originalId}", $"{kind} {originalId}",
            new HistoryPayload(kind, originalId, project, environment, at, JsonSerializer.SerializeToElement(document, document.GetType(), Web)),
            kind.Contains("decision", StringComparison.Ordinal) || kind.Contains("approval", StringComparison.Ordinal) ? Portability.HistoricalDecision : Portability.HistoricalEvidence,
            project: project, environment: environment);

    private async Task AddIntegrationAsync(Closure closure, string environmentId, bool evidence, string? profileJson, CancellationToken ct)
    {
        // Connected Systems through the existing sanitised setup package (portable, non-secret fields only).
        var (export, problem) = await _setup.ExportAsync(environmentId, SetupScope.Environment, null, null, SetupPackageFormat.Json, null, ct);
        if (export is not null)
        {
            var systems = (await _db.ExternalIntegrations.AsNoTracking().Where(i => i.EnvironmentId == environmentId).Select(i => i.Id).ToListAsync(ct)).ToList();
            closure.Add(PackageTypes.ConnectedSystems, "setup", $"{export.Dependencies.Count} dependencies; {systems.Count} stored system(s)",
                new ConnectedSystemsPayload(export.Content, systems, export.Excluded), Portability.EnvironmentDependent, environment: environmentId);
            foreach (var provider in Regex.Matches(export.Content, @"""provider""\s*:\s*""(?<p>[A-Za-z]+)""").Select(m => m.Groups["p"].Value).Distinct())
                if (Enum.TryParse<ExternalProviderType>(provider, true, out var type))
                    foreach (var capability in ExternalProviderCatalog.Descriptor(type).Capabilities) closure.Capabilities.Add($"provider:{type}:{capability}");
        }
        else closure.Excluded.Add($"Connected Systems: {problem}");

        foreach (var platform in await _db.IntegrationPlatforms.AsNoTracking().Where(p => p.EnvironmentId == environmentId).ToListAsync(ct))
            closure.Add(PackageTypes.Platform, platform.Id, platform.Name, platform, Portability.EnvironmentDependent, environment: environmentId);
        foreach (var definition in await _db.IntegrationDefinitions.AsNoTracking().Where(d => d.EnvironmentId == environmentId).ToListAsync(ct))
            closure.Add(PackageTypes.Integration, definition.Id, definition.DisplayName, definition, Portability.EnvironmentDependent, environment: environmentId,
                dependencies: definition.PlatformId is { } p ? [new(PackageTypes.Platform, p)] : null);

        var boundSnapshots = new HashSet<Guid>();
        foreach (var binding in await _db.IntegrationEvidenceBindings.AsNoTracking().Where(b => b.EnvironmentId == environmentId).ToListAsync(ct))
        {
            var me = new PortableObjectRef(PackageTypes.EvidenceBinding, binding.IntegrationId);
            closure.Add(PackageTypes.EvidenceBinding, binding.IntegrationId, $"{binding.IntegrationId} → source snapshot", binding, Portability.EnvironmentDependent,
                environment: environmentId, dependencies: [new(PackageTypes.Integration, binding.IntegrationId), new(PackageTypes.Snapshot, binding.SnapshotId.ToString())]);
            await AddSnapshotAsync(closure, binding.SnapshotId, ct, me);
            boundSnapshots.Add(binding.SnapshotId);
        }
        foreach (var artifact in await _db.IntegrationContractArtifacts.AsNoTracking().Where(a => a.EnvironmentId == environmentId).ToListAsync(ct))
            closure.Add(PackageTypes.ContractArtifact, $"{artifact.IntegrationId}:{artifact.Role}", $"{artifact.IntegrationId} ({artifact.Role})", artifact, Portability.EnvironmentDependent,
                environment: environmentId, dependencies: [new(PackageTypes.Integration, artifact.IntegrationId)]);
        foreach (var schema in await _db.GraphQlSchemaArtifacts.AsNoTracking().Where(a => a.EnvironmentId == environmentId).ToListAsync(ct))
            closure.Add(PackageTypes.GraphQlSchema, schema.TargetId, $"GraphQL schema {schema.TargetId}", schema, Portability.EnvironmentDependent, environment: environmentId);
        if (await _db.IntegrationMessageFlows.AsNoTracking().FirstOrDefaultAsync(f => f.EnvironmentId == environmentId, ct) is { } flows)
            closure.Add(PackageTypes.MessageFlows, "message-flows", "Reviewed message flows", flows, Portability.EnvironmentDependent, environment: environmentId);

        // Contracts bound to this environment (with their bindings) and their projects' sources.
        var boundContractIds = await _db.ContractEndpointBindings.AsNoTracking().Where(b => b.EnvironmentId == environmentId).Select(b => b.ContractId).Distinct().ToListAsync(ct);
        foreach (var contract in await _db.ApiContracts.AsNoTracking().Where(c => boundContractIds.Contains(c.Id)).ToListAsync(ct))
            await AddContractAsync(closure, contract, environmentId, ct);

        // Integration suites of this environment, and composite contexts whose stages use the bound sources: every stage source travels (explicit
        // integration scope), each in its own project, never merged.
        foreach (var suite in await _db.IntegrationSuites.AsNoTracking().Where(s => s.EnvironmentId == environmentId).ToListAsync(ct))
        {
            AddSuite(closure, suite, null);
            await AddSnapshotAsync(closure, suite.SnapshotId, ct, new(PackageTypes.Suite, $"{suite.SuiteKey}@{suite.Revision}"));
            boundSnapshots.Add(suite.SnapshotId);
        }
        var catalogIds = closure.Entries.Where(e => e.Object.Type is PackageTypes.Integration or PackageTypes.Platform).Select(e => e.Object.LogicalId).ToHashSet(StringComparer.Ordinal);
        foreach (var context in await _db.CompositeSourceContexts.AsNoTracking().ToListAsync(ct))
        {
            var referenced = GuidPattern.Matches(context.DefinitionJson).Select(m => Guid.Parse(m.Value)).ToHashSet();
            if (catalogIds.Contains(context.ScopeId) || referenced.Overlaps(boundSnapshots)) await AddCompositeAsync(closure, context, includeOtherProjects: true, ct);
        }
        foreach (var p in closure.Entries.Where(e => e.Object.Type == PackageTypes.Snapshot).Select(e => e.Object.OwningProject).OfType<string>().Distinct())
            if (!closure.Projects.Contains(p)) closure.Projects.Add(p);

        // Browser E2E: flows and modules of this environment (definitions, routes, steps, selectors, assertions, Test Data mappings).
        foreach (var flow in _flows.Flows(environmentId))
        {
            closure.Add(PackageTypes.Flow, flow.Id, $"{flow.Name} (v{flow.Version})", flow, Portability.EnvironmentDependent, environment: environmentId, revision: flow.Version);
            closure.Capabilities.Add($"critical-e2e:{flow.Mode}");
        }
        foreach (var module in _flows.RegisteredModules(environmentId))
            closure.Add(PackageTypes.Module, module.Name, module.Name, new ModulePayload(environmentId, module), Portability.EnvironmentDependent, environment: environmentId);

        // Test Data: assets and authored synthetic values of this environment, plus shared ones the flows map to.
        var flowAssetIds = _flows.Flows(environmentId).SelectMany(f => f.TestDataAssetIds ?? []).ToHashSet(StringComparer.Ordinal);
        foreach (var asset in _testData.Assets().Where(a => a.EnvironmentId == environmentId || flowAssetIds.Contains(a.Id)))
            closure.Add(PackageTypes.TestDataAsset, asset.Alias, asset.Label ?? asset.Alias, asset, asset.EnvironmentId is null ? Portability.Portable : Portability.EnvironmentDependent,
                environment: asset.EnvironmentId);
        foreach (var resource in _authored.Resources().Where(r => r.Versions.Any(v => v.EnvironmentId == environmentId)))
            closure.Add(PackageTypes.TestDataValues, resource.Alias, $"{resource.Alias} ({resource.Versions.Count} version{(resource.Versions.Count == 1 ? "" : "s")})", resource,
                Portability.EnvironmentDependent, environment: environmentId, revision: resource.Versions.Count);
        AddTestDataForSnapshots(closure, boundSnapshots, null);

        // Journey packs used in this environment: capability references only (packs are code in the destination build, never imported).
        foreach (var pack in await _db.IntegrationJourneyRuns.AsNoTracking().Where(r => r.EnvironmentId == environmentId).Select(r => r.PackId).Distinct().ToListAsync(ct))
            closure.Capabilities.Add($"journey-pack:{pack}");

        if (!string.IsNullOrWhiteSpace(profileJson))
        {
            var (sanitised, removed) = SanitiseProfile(profileJson);
            closure.Add(PackageTypes.Profile, "profile", "Target Environment profile (disabled draft)", new ProfilePayload(sanitised, removed), Portability.EnvironmentDependent,
                environment: environmentId);
        }

        if (evidence)
        {
            await AddSuiteEvidenceAsync(closure, ct);
            foreach (var run in _flows.History(environmentId, Math.Min(_options.MaxEvidenceRuns, 100)))
                AddHistory(closure, "critical-e2e-run", run.RunId, null, environmentId, run.StartedAt, run);
            foreach (var run in await _db.IntegrationJourneyRuns.AsNoTracking().Where(r => r.EnvironmentId == environmentId).OrderByDescending(r => r.StartedAt).Take(_options.MaxEvidenceRuns).ToListAsync(ct))
                AddHistory(closure, "integration-journey-run", run.Id.ToString(), null, environmentId, run.StartedAt, run);
        }
    }

    private async Task AddDiagnosticAsync(Closure closure, ExportRequest request, CancellationToken ct)
    {
        if (request.SuiteKey is { Length: > 0 } suiteKey)
        {
            var suite = await _db.IntegrationSuites.AsNoTracking().Where(s => s.SuiteKey == suiteKey).OrderByDescending(s => s.Revision).FirstOrDefaultAsync(ct)
                ?? throw new TransferRequestException("NOT_FOUND", "No integration suite with that key.");
            AddSuite(closure, suite, null);
            await AddSnapshotAsync(closure, suite.SnapshotId, ct, new(PackageTypes.Suite, $"{suite.SuiteKey}@{suite.Revision}"));
        }
        if (request.FlowId is { Length: > 0 } flowId)
        {
            var flow = _flows.Flow(flowId) ?? throw new TransferRequestException("NOT_FOUND", "No Critical E2E flow with that id.");
            closure.Add(PackageTypes.Flow, flow.Id, $"{flow.Name} (v{flow.Version})", flow, Portability.EnvironmentDependent, environment: flow.EnvironmentId, revision: flow.Version);
            closure.Capabilities.Add($"critical-e2e:{flow.Mode}");
            foreach (var asset in _testData.Assets().Where(a => (flow.TestDataAssetIds ?? []).Contains(a.Id)))
                closure.Add(PackageTypes.TestDataAsset, asset.Alias, asset.Label ?? asset.Alias, asset, Portability.Portable, environment: asset.EnvironmentId);
        }
        if (closure.Entries.Count == 0) throw new TransferRequestException("SCOPE", "Choose an integration suite or a Critical E2E flow for a diagnostic package.");
    }

    /// <summary>A Target Environment profile without anything secret-, identity- or trust-shaped (keys removed, names reported, values never kept).</summary>
    public static (string Json, IReadOnlyList<string> Removed) SanitiseProfile(string profileJson)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(profileJson, documentOptions: new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException) { throw new TransferRequestException("BAD_PROFILE", "The Target Environment profile is not valid JSON."); }
        if (root is not JsonObject obj) throw new TransferRequestException("BAD_PROFILE", "The Target Environment profile must be a JSON object.");
        var removed = new List<string>();
        void Clean(JsonNode? node, string path)
        {
            if (node is JsonObject o)
                foreach (var key in o.Select(kv => kv.Key).ToList())
                {
                    if (SetupProfiles.SetupProfileSchema.KeyIssue(key, path) is not null || key.Contains("token", StringComparison.OrdinalIgnoreCase) || key.Contains("cookie", StringComparison.OrdinalIgnoreCase))
                    { o.Remove(key); removed.Add($"{path}.{key}".TrimStart('.')); }
                    else Clean(o[key], $"{path}.{key}");
                }
            else if (node is JsonArray a)
                for (var i = 0; i < a.Count; i++) Clean(a[i], $"{path}[{i}]");
        }
        Clean(obj, "");
        return (obj.ToJsonString(), removed);
    }

    private async Task<List<StageSummary>> StagesAsync(Closure closure, CancellationToken ct)
    {
        // Stages exactly as stored in composite source contexts (stage name → source project); counts only from objects in this closure.
        var stages = new List<StageSummary>();
        foreach (var entry in closure.Entries.Where(e => e.Object.Type == PackageTypes.Composite))
        {
            var record = entry.Payload.Deserialize<CompositeSourceContextRecord>(Web)!;
            if (TryParse(record.DefinitionJson) is not JsonObject definition) continue;
            foreach (var stage in (definition["stages"] as JsonArray ?? definition["Stages"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var name = (stage["stage"] ?? stage["name"] ?? stage["Stage"] ?? stage["Name"])?.ToString() ?? "stage";
                var project = (stage["projectKey"] ?? stage["ProjectKey"] ?? stage["project"])?.ToString();
                var snapshotId = (stage["snapshotId"] ?? stage["SnapshotId"])?.ToString();
                var contracts = closure.Entries.Count(e => e.Object.Type == PackageTypes.Contract && e.Object.OwningProject == project);
                var tests = closure.Entries.Count(e => e.Object.Type is PackageTypes.Suite or PackageTypes.Flow && (project is null || e.Object.OwningProject == project));
                var status = snapshotId is not null && closure.Has(PackageTypes.Snapshot, snapshotId) ? "Source included"
                    : project is null && snapshotId is null ? "No source mapped" : "Source not in package";
                stages.Add(new StageSummary(name, project, 0, contracts, tests, status));
            }
        }
        await Task.CompletedTask;
        return stages.DistinctBy(s => (s.Stage, s.SourceProject)).ToList();
    }

    private static JsonNode? TryParse(string text)
    {
        try { return JsonNode.Parse(text); } catch (JsonException) { return null; }
    }

    // ───────────────────────────── Export: write ─────────────────────────────

    public async Task<ExportResult> ExportAsync(ExportRequest request, string actor, CancellationToken ct)
    {
        var closure = await BuildClosureAsync(request, ct);
        var security = ScanAll(closure.Entries);
        var blockers = Blockers(closure, security, request);
        if (blockers.Count > 0) throw new TransferRequestException("EXPORT_BLOCKED", string.Join(" ", blockers));

        var diagnostic = request.Kind == TransferKind.Diagnostic;
        if (diagnostic)
        {
            // Deterministic redaction; afterwards the package must scan clean, or it is not called redacted.
            for (var i = 0; i < closure.Entries.Count; i++)
            {
                var e = closure.Entries[i];
                var redacted = PackageSecretScanner.RedactIdentifiers(e.Payload.DeepClone())!;
                closure.Entries[i] = e with { Payload = redacted, Object = e.Object with { Fingerprint = PackageIds.Fingerprint(redacted) } };
            }
            security = ScanAll(closure.Entries);
            if (security.Count > 0) throw new TransferRequestException("REDACTION_INCOMPLETE",
                $"The diagnostic package still contains {security.Count} sensitive value(s) after redaction ({string.Join("; ", security.Take(3).Select(f => $"{f.Severity} at {f.Location}"))}); it is not created.");
        }

        var operationId = Guid.NewGuid();
        var now = _clock.GetUtcNow();
        var (version, commit) = FullBackupService.ApplicationIdentity();
        var head = await MigrationHeadAsync(ct);
        var completeness = closure.Dependencies.Any(d => d.Required && d.Status is DependencyStatus.Missing or DependencyStatus.Unauthorized) ? "PARTIAL" : "COMPLETE";
        EnsureDirectory(Exports);
        var kind = request.Kind switch { TransferKind.ProjectWorkspace => "project", TransferKind.IntegrationWorkspace => "integration", _ => "diagnostic" };
        var fileName = $"birknext-{kind}-{now:yyyyMMdd-HHmmss}-{operationId.ToString("N")[..8]}.birknext";
        var path = Path.Combine(Exports, fileName);
        var partial = path + ".partial";
        PackageManifest manifest;
        try
        {
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                using var zip = new TransferArchiveWriter(output);
                foreach (var entry in closure.Entries.OrderBy(e => PackageTypes.Order(e.Object.Type)).ThenBy(e => e.Object.LogicalId, StringComparer.Ordinal))
                    await zip.AddAsync(entry.Object.Path, s => JsonSerializer.SerializeAsync(s, entry.Payload, TransferJson.Options, ct));
                await zip.AddJsonAsync("inventory/objects.json", closure.Entries.Select(e => e.Object).ToList(), TransferJson.Options);
                await zip.AddJsonAsync("inventory/dependencies.json", closure.Dependencies, TransferJson.Options);
                manifest = new PackageManifest
                {
                    ExportType = request.Kind, BirkNextApplicationVersion = version, SourceCommit = commit, ExporterInstanceId = InstanceId(), ExportTimestamp = now,
                    Label = (request.Label ?? ScopeLabel(request)).Trim(), IncludedProjects = closure.Projects, IncludedIntegrationScopes = closure.Environments,
                    SchemaVersion = head, RequiredCapabilities = closure.Capabilities.Order().ToList(), ContentFingerprints = new Dictionary<string, string>(zip.Hashes),
                    DependencyManifest = closure.Dependencies, RedactionProfile = diagnostic ? "diagnostic-v1: identifier-shaped numbers replaced by stable markers; evidence bodies excluded" : "none",
                    OptionalEvidencePolicy = request.IncludeEvidence ? $"included: most recent runs in scope (≤ {_options.MaxEvidenceRuns}), as IMPORTED_HISTORICAL_EVIDENCE" : "excluded",
                    Completeness = completeness, Exclusions = closure.Excluded,
                };
                await zip.AddJsonAsync("manifest.json", manifest, TransferJson.Options);
                await zip.SealAsync(TransferJson.Options);
            }
            File.Move(partial, path);
        }
        catch { TryDelete(partial); throw; }

        var sha = await Sha256FileAsync(path, ct);
        await RecordAsync(new WorkspaceTransferOperationRecord
        {
            Id = operationId, Kind = "Export", State = "CREATED", Actor = actor, CreatedAt = now, UpdatedAt = now, Scope = request.Kind.ToString(),
            Summary = $"{ScopeLabel(request)}: {closure.Entries.Count} objects, {completeness}", PackageSha256 = sha,
            ResultJson = JsonSerializer.Serialize(new { fileName, counts = closure.Entries.GroupBy(e => e.Object.Type).ToDictionary(g => g.Key, g => g.Count()), security = security.Count }, Web),
        }, ct);
        return new ExportResult(operationId, fileName, sha, new FileInfo(path).Length, manifest, security);
    }

    public string ResolveExport(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName) || !fileName.EndsWith(".birknext", StringComparison.OrdinalIgnoreCase))
            throw new TransferRequestException("BAD_NAME", "Name a package from the export list.");
        var full = Path.Combine(Exports, fileName);
        return File.Exists(full) ? full : throw new TransferRequestException("NOT_FOUND", $"No package named {fileName}.");
    }

    private async Task<string> MigrationHeadAsync(CancellationToken ct)
    {
        try { return (await _db.Database.GetAppliedMigrationsAsync(ct)).LastOrDefault() ?? ""; }
        catch (Exception ex) when (ex is InvalidOperationException or Npgsql.NpgsqlException) { return ""; }
    }

    // ───────────────────────────── Operations log ─────────────────────────────

    public async Task<IReadOnlyList<TransferOperationSummary>> HistoryAsync(int take, CancellationToken ct) =>
        (await _db.WorkspaceTransferOperations.AsNoTracking().OrderByDescending(o => o.CreatedAt).Take(Math.Clamp(take, 1, 200)).ToListAsync(ct))
        .Select(o => new TransferOperationSummary(o.Id, o.Kind, o.State, o.Scope, o.Summary, o.Actor, o.CreatedAt, o.UpdatedAt, o.PackageSha256, o.Error)).ToList();

    private async Task RecordAsync(WorkspaceTransferOperationRecord record, CancellationToken ct)
    {
        _db.WorkspaceTransferOperations.Add(record);
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
    }

    private void EnsureDirectory(string path)
    {
        if (System.IO.Directory.Exists(path)) return;
        System.IO.Directory.CreateDirectory(path);
        try { LocalSecurity.LocalSessionService.RestrictToCurrentUser(path, isDirectory: true); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException or IOException)
        { _logger.LogWarning("Could not restrict {Path} to the current user: {Message}", path, ex.Message); }
    }

    private static async Task<string> Sha256FileAsync(string path, CancellationToken ct)
    {
        await using var f = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(f, ct)).ToLowerInvariant();
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
