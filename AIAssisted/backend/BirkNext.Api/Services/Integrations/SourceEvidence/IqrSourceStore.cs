using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Integrations;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.Integrations.SourceEvidence;

public sealed class InvalidSourceSelectionException(string message) : Exception(message);
public sealed class SourceSnapshotPersistenceException : Exception { }

public sealed class IqrSourceStore(AppDbContext db)
{
    /// <summary>The owner id of snapshots ingested by Source Analysis (the one source-upload entry point). Other owner ids are snapshots an
    /// earlier version uploaded per integration in Integration Quality Review: kept and readable, never offered for new review scopes.</summary>
    public const string SourceAnalysisOwner = "source-analysis";
    /// <summary>The stored environment of a Source Analysis snapshot: none. Rows from earlier versions keep the target id they were stored with,
    /// which no longer scopes anything.</summary>
    public const string NoEnvironment = "";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The workspace's Source Analysis snapshots, newest first (deterministic: id breaks ties). Source evidence is not runtime evidence:
    /// a Target Environment never scopes, owns or selects a source snapshot, so selecting, switching or deleting a target changes neither
    /// the history nor which snapshot is newest. Snapshots stored by earlier versions with a target's id are listed the same way.
    /// </summary>
    public async Task<IReadOnlyList<IqrSourceSnapshot>> ListSourceAnalysisAsync(int take = 100, CancellationToken ct = default)
    {
        var records = await db.IqrSourceSnapshots.AsNoTracking().Where(r => r.IntegrationId == SourceAnalysisOwner)
            .OrderByDescending(r => r.AnalyzedAt).ThenByDescending(r => r.Id).Take(take).ToListAsync(ct);
        return records.Select(r => JsonSerializer.Deserialize<IqrSourceSnapshot>(r.EvidenceJson, Json)!).ToList();
    }

    /// <summary>Exactly this Source Analysis snapshot, or null. Whatever target was active when it was created does not matter.</summary>
    public async Task<IqrSourceSnapshot?> FindSourceAnalysisAsync(Guid id, CancellationToken ct = default)
    {
        var record = await db.IqrSourceSnapshots.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.IntegrationId == SourceAnalysisOwner, ct);
        return record is null ? null : JsonSerializer.Deserialize<IqrSourceSnapshot>(record.EvidenceJson, Json);
    }
    /// <summary>Every snapshot available with this environment: the workspace's Source Analysis snapshots (whatever target, if any, was active
    /// when they were created) plus the legacy per-integration snapshots an earlier version stored for this environment. Newest first.</summary>
    public async Task<IReadOnlyList<IqrSourceSnapshot>> ListAsync(string environmentId, CancellationToken ct = default)
    {
        var records = await db.IqrSourceSnapshots.AsNoTracking().Where(r => r.IntegrationId == SourceAnalysisOwner || r.EnvironmentId == environmentId)
            .OrderByDescending(r => r.AnalyzedAt).Take(50).ToListAsync(ct);
        return records.Select(r => JsonSerializer.Deserialize<IqrSourceSnapshot>(r.EvidenceJson, Json)!).ToList();
    }
    public async Task<IqrSourceSnapshot?> GetAsync(string environmentId, string integrationId, Guid id, CancellationToken ct = default)
    {
        var record = await db.IqrSourceSnapshots.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.IntegrationId == integrationId
            && (integrationId == SourceAnalysisOwner || r.EnvironmentId == environmentId), ct);
        return record is null ? null : JsonSerializer.Deserialize<IqrSourceSnapshot>(record.EvidenceJson, Json);
    }
    internal static BirkNext.TestEvidence.SourceTestInventory DiscoverTests(IqrSourceSnapshot snapshot, string repository, IqrSourceArchiveReader.Workspace workspace, CancellationToken ct)
    {
        try { return BirkNext.Api.Services.TestEvidence.DotNetXunitTestDiscoveryProvider.Discover(snapshot.Id, snapshot.Archive.Sha256, repository, workspace, ct); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NullReferenceException or IndexOutOfRangeException)
        {
            return new BirkNext.TestEvidence.SourceTestInventory
            {
                SnapshotId = snapshot.Id, SnapshotFingerprint = snapshot.Archive.Sha256, RepositoryName = repository, Status = BirkNext.TestEvidence.SourceTestDiscoveryStatus.Partial,
                Limitations = [$"Source test discovery stopped on an unsupported pattern ({ex.GetType().Name}); no test definitions were recorded."],
            };
        }
    }

    public async Task<(IqrSourceSnapshot? Snapshot, string? Error)> AnalyzeAsync(string environmentId, string integrationId, string name, byte[] bytes, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(environmentId) || string.IsNullOrWhiteSpace(integrationId)) return (null, "Environment and integration identity are required.");
        var result = IqrSourceArchiveReader.ReadDetailed(name, bytes, ct);
        if (!result.IsValid) return (null, result.Failure?.Message);
        return (await AnalyzeValidatedAsync(environmentId, integrationId, name, bytes, result.Workspace!, ct), null);
    }

    /// <summary>Analyzes a validated workspace into a new immutable snapshot. Project Import passes its provenance (import identity and archive
    /// fingerprint shared with the imported artifacts); a standalone upload passes none. A Source Analysis snapshot needs no Target Environment
    /// and is stored without one (<see cref="NoEnvironment"/>); only the legacy per-integration snapshots keep the environment they were uploaded for.</summary>
    internal async Task<IqrSourceSnapshot> AnalyzeValidatedAsync(string? environmentId, string integrationId, string name, byte[] bytes,
        IqrSourceArchiveReader.Workspace workspace, CancellationToken ct = default, BirkNext.ProjectImport.ProjectImportProvenance? projectImport = null)
    {
        ct.ThrowIfCancellationRequested();
        environmentId = integrationId == SourceAnalysisOwner ? NoEnvironment : environmentId ?? NoEnvironment;
        var snapshot = IqrSourceAnalyzer.Analyze(integrationId, workspace, DateTimeOffset.UtcNow, ct);
        snapshot = snapshot with { DatabaseArchitecture = await DatabaseArchitecture.DatabaseArchitectureAnalyzer.AnalyzeAsync(snapshot.Id, workspace, snapshot.AnalyzedAt, ct) };
        // Source architecture (components, dependencies, messaging, datastores): its own model; datastores only link to the Database analysis above.
        snapshot = snapshot with { Architecture = SourceArchitecture.SourceArchitectureAnalyzer.Analyze(snapshot.Id, workspace, snapshot.AnalyzedAt, snapshot.DatabaseArchitecture, ct,
            out var architectureInput, out var architectureResults) };
        // Observability (correlation/tracing, logging quality, telemetry configuration): analysed once here over the same workspace and Architecture model.
        snapshot = snapshot with { Observability = SourceAnalysis.Observability.ObservabilitySourceAnalyzer.Analyze(snapshot.Id, workspace, architectureInput, snapshot.Architecture, snapshot.AnalyzedAt, ct) };
        // Reusable source-evidence domains (Infrastructure as Code, Configuration, CI/CD, Contracts, cross-domain links): analysed once here over the
        // same workspace, Architecture input and models — consumers read them from the snapshot, never by rescanning the archive.
        snapshot = snapshot with { EvidenceDomains = SourceAnalysis.Evidence.SourceEvidenceAnalyzer.Analyze(snapshot.Id, workspace, architectureInput, snapshot.AnalyzedAt, snapshot.Architecture,
            snapshot.DatabaseArchitecture, snapshot.Observability, snapshot.IntegrationPath, ct, out var configuration) };
        // Security Expectations reads identity/endpoint candidates from the normalized Configuration evidence (raw public identifiers in memory only).
        snapshot = snapshot with { SecurityExpectationsEvidence = SecurityExpectations.SecurityExpectationSourceAnalyzer.Analyze(snapshot, architectureInput,
            architectureResults.SelectMany(r => r.Facts).ToList(), workspace.Limitations, ct, configuration) };
        // Source integration discovery signals (capture-technology markers, orchestration-declared channels): identifiers only, read from the same workspace.
        snapshot = snapshot with { IntegrationSignals = SourceDiscovery.SourceIntegrationSignalExtractor.Extract(workspace, snapshot.Architecture) };
        // Technology inventory: every technology the archive shows, with the capabilities it implies — unsupported ones are reported, not dropped.
        snapshot = snapshot with { TechnologyCoverage = SourceAnalysis.Technology.TechnologyInventory.Detect(workspace, snapshot.Architecture, snapshot.EvidenceDomains) };
        // Source Analysis owns this snapshot-scoped path inventory. Paths are exhaustive for non-ignored
        // archive entries; content fingerprints are included only for files already read by this analysis.
        var indexedFiles = workspace.AllPaths?.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(path => new SourceFileTarget(path.Replace('\\', '/'), FingerprintFile(workspace, path))).ToList() ?? [];
        snapshot = snapshot with { TargetIndex = new SourceTargetIndex
        {
            SnapshotId = snapshot.Id, SnapshotFingerprint = snapshot.Archive.Sha256,
            FileInventoryComplete = workspace.AllPaths is not null,
            Files = indexedFiles,
            Limitations = workspace.AllPaths is null ? ["Source archive reader did not retain a complete path inventory."] : []
        }};
        // Repository identity and the dependency evidence Dependency Review consumes (manifests, redacted Renovate configs, automation summaries):
        // captured once here so Dependency Review never needs the archive again. Source Analysis supplies evidence; it does not review dependencies.
        var repository = DependencyReview.SourceDependencyEvidenceExtractor.Identity(name, bytes);
        snapshot = snapshot with { Repository = repository, DependencyEvidence = DependencyReview.SourceDependencyEvidenceExtractor.Extract(repository.DisplayName, bytes, name).Evidence };
        // Source test definitions (test projects, [Fact]/[Theory], traits, explicit requirement references): discovery only, consumed when execution
        // results are imported. A failure here never fails the snapshot; it is reported as a limitation of an empty inventory.
        snapshot = snapshot with { TestInventory = DiscoverTests(snapshot, repository.DisplayName, workspace, ct) };
        // Classification-relevant observations Security Classification consumes (facts with file:line, read by its own analyzer): captured once
        // here so Security Classification never needs the archive. Source Analysis neither shows nor judges them.
        snapshot = snapshot with { SecurityClassificationEvidence = SecurityClassification.ClassificationSourceAnalyzer.ExtractArchive(name, bytes) };
        // Integration Quality Review's application-messaging and SCIM observations of this snapshot, read by their own analyzers.
        snapshot = snapshot with
        {
            ApplicationMessagingEvidence = ApplicationMessaging.ApplicationMessagingStore.ExtractSnapshotEvidence(environmentId, name, bytes),
            ScimEvidence = Scim.ScimEvidenceService.ExtractSnapshotEvidence(environmentId, name, bytes),
        };
        snapshot = snapshot with { ProjectImport = projectImport };
        // Generated documentation (its own evidence type): detection, provenance, module-scoped freshness and generated-vs-source comparisons over
        // the same workspace and the models above. A failure never fails the snapshot; it is recorded as a limitation.
        snapshot = snapshot with { GeneratedDocumentation = AnalyzeGeneratedDocumentation(snapshot, workspace, architectureInput, architectureResults.SelectMany(r => r.Facts).ToList(), ct) };
        // Insert only. Identical archive hashes still create distinct evidence versions when analyzed again.
        db.IqrSourceSnapshots.Add(new IqrSourceSnapshotRecord { Id = snapshot.Id, EnvironmentId = environmentId, IntegrationId = integrationId,
            AnalyzedAt = snapshot.AnalyzedAt, EvidenceJson = JsonSerializer.Serialize(snapshot, Json) });
        try { await db.SaveChangesAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { throw new SourceSnapshotPersistenceException(); }
        return snapshot;
    }

    internal static BirkNext.GeneratedDocumentation.GeneratedDocumentationSnapshot AnalyzeGeneratedDocumentation(IqrSourceSnapshot snapshot, IqrSourceArchiveReader.Workspace workspace,
        SourceArchitecture.ArchitectureInput? input, IReadOnlyList<SourceArchitecture.ArchitectureFact> facts, CancellationToken ct)
    {
        try { return SourceAnalysis.GeneratedDocumentation.GeneratedDocumentationAnalyzer.Analyze(snapshot, workspace, input, facts, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new BirkNext.GeneratedDocumentation.GeneratedDocumentationSnapshot
            {
                SourceSnapshotId = snapshot.Id, SourceFingerprint = snapshot.Archive.Sha256, AnalyzerVersion = SourceAnalysis.GeneratedDocumentation.GeneratedDocumentationAnalyzer.Version,
                AnalyzedAt = snapshot.AnalyzedAt, Limitations = [$"Generated-documentation analysis stopped on an unsupported pattern ({ex.GetType().Name}); no generated-documentation evidence was recorded."],
            };
        }
    }

    private static string? FingerprintFile(IqrSourceArchiveReader.Workspace workspace, string path)
    {
        var file = (workspace.Files ?? []).Concat(workspace.ConfigurationFiles ?? []).Concat(workspace.EvidenceFiles ?? [])
            .FirstOrDefault(x => string.Equals(x.Path.Replace('\\', '/'), path.Replace('\\', '/'), StringComparison.Ordinal));
        return file is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file.Content))).ToLowerInvariant();
    }
}
