using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Integrations;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.Integrations.SourceEvidence;

public sealed class InvalidSourceSelectionException(string message) : Exception(message);

public sealed class IqrSourceStore(AppDbContext db)
{
    /// <summary>The owner id of snapshots ingested by Source Analysis (the one source-upload entry point). Other owner ids are snapshots an
    /// earlier version uploaded per integration in Integration Quality Review: kept and readable, never offered for new review scopes.</summary>
    public const string SourceAnalysisOwner = "source-analysis";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Source Analysis snapshots of an environment, newest first (deterministic: id breaks ties).</summary>
    public async Task<IReadOnlyList<IqrSourceSnapshot>> ListSourceAnalysisAsync(string environmentId, int take = 100, CancellationToken ct = default)
    {
        var records = await db.IqrSourceSnapshots.AsNoTracking().Where(r => r.EnvironmentId == environmentId && r.IntegrationId == SourceAnalysisOwner)
            .OrderByDescending(r => r.AnalyzedAt).ThenByDescending(r => r.Id).Take(take).ToListAsync(ct);
        return records.Select(r => JsonSerializer.Deserialize<IqrSourceSnapshot>(r.EvidenceJson, Json)!).ToList();
    }

    /// <summary>Exactly this Source Analysis snapshot of the environment, or null.</summary>
    public async Task<IqrSourceSnapshot?> FindSourceAnalysisAsync(string environmentId, Guid id, CancellationToken ct = default)
    {
        var record = await db.IqrSourceSnapshots.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.EnvironmentId == environmentId && r.IntegrationId == SourceAnalysisOwner, ct);
        return record is null ? null : JsonSerializer.Deserialize<IqrSourceSnapshot>(record.EvidenceJson, Json);
    }
    public async Task<IReadOnlyList<IqrSourceSnapshot>> ListAsync(string environmentId, CancellationToken ct = default)
    {
        var records = await db.IqrSourceSnapshots.AsNoTracking().Where(r => r.EnvironmentId == environmentId)
            .OrderByDescending(r => r.AnalyzedAt).Take(50).ToListAsync(ct);
        return records.Select(r => JsonSerializer.Deserialize<IqrSourceSnapshot>(r.EvidenceJson, Json)!).ToList();
    }
    public async Task<IqrSourceSnapshot?> GetAsync(string environmentId, string integrationId, Guid id, CancellationToken ct = default)
    {
        var record = await db.IqrSourceSnapshots.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.EnvironmentId == environmentId && r.IntegrationId == integrationId, ct);
        return record is null ? null : JsonSerializer.Deserialize<IqrSourceSnapshot>(record.EvidenceJson, Json);
    }
    public async Task<(IqrSourceSnapshot? Snapshot, string? Error)> AnalyzeAsync(string environmentId, string integrationId, string name, byte[] bytes, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(environmentId) || string.IsNullOrWhiteSpace(integrationId)) return (null, "Environment and integration identity are required.");
        var (workspace, error) = IqrSourceArchiveReader.Read(name, bytes, ct);
        if (workspace is null) return (null, error);
        ct.ThrowIfCancellationRequested();
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
        // Repository identity and the dependency evidence Dependency Review consumes (manifests, redacted Renovate configs, automation summaries):
        // captured once here so Dependency Review never needs the archive again. Source Analysis supplies evidence; it does not review dependencies.
        var repository = DependencyReview.SourceDependencyEvidenceExtractor.Identity(name, bytes);
        snapshot = snapshot with { Repository = repository, DependencyEvidence = DependencyReview.SourceDependencyEvidenceExtractor.Extract(repository.DisplayName, bytes, name).Evidence };
        // Classification-relevant observations Security Classification consumes (facts with file:line, read by its own analyzer): captured once
        // here so Security Classification never needs the archive. Source Analysis neither shows nor judges them.
        snapshot = snapshot with { SecurityClassificationEvidence = SecurityClassification.ClassificationSourceAnalyzer.ExtractArchive(name, bytes) };
        // Integration Quality Review's application-messaging and SCIM observations of this snapshot, read by their own analyzers.
        snapshot = snapshot with
        {
            ApplicationMessagingEvidence = ApplicationMessaging.ApplicationMessagingStore.ExtractSnapshotEvidence(environmentId, name, bytes),
            ScimEvidence = Scim.ScimEvidenceService.ExtractSnapshotEvidence(environmentId, name, bytes),
        };
        // Insert only. Identical archive hashes still create distinct evidence versions when analyzed again.
        db.IqrSourceSnapshots.Add(new IqrSourceSnapshotRecord { Id = snapshot.Id, EnvironmentId = environmentId, IntegrationId = integrationId,
            AnalyzedAt = snapshot.AnalyzedAt, EvidenceJson = JsonSerializer.Serialize(snapshot, Json) });
        await db.SaveChangesAsync(ct);
        return (snapshot, null);
    }
}
