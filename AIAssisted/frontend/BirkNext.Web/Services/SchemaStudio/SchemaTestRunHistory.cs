using System.Text;
using System.Text.Json;

namespace BirkNext.Web.Services.SchemaStudio;

public sealed record SchemaTestRunResult(string TestId, string Path, string Rule, bool ExpectedValid,
    string Actual, string Outcome, string Diagnostic);

public sealed record SchemaRunProvenance(Guid? SourceSnapshotId, string? SourceSnapshotFingerprint,
    string? SelectedContractPath, string ContractFingerprint);

public sealed record SavedSchemaTestRun(Guid Id, string PackageFingerprint,
    SchemaRunProvenance? Provenance, string ProfileName, int? Seed, DateTimeOffset CreatedAt,
    IReadOnlyList<SchemaTestRunResult> Results, string? ProjectScope = null, Guid? ProfileId = null);

/// <summary>Bounded validation history. Generated XML is deliberately excluded; runs retain only sanitized summaries.</summary>
public sealed class SchemaTestRunHistory
{
    public const int MaxRuns = 20;
    public const int MaxResultsPerRun = 500;
    public const int MaxDocumentBytes = 4 * 1024 * 1024;
    private const int Version = 1;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly List<SavedSchemaTestRun> _runs = [];

    public IReadOnlyList<SavedSchemaTestRun> Runs => _runs.OrderByDescending(r => r.CreatedAt).ToArray();

    public SavedSchemaTestRun Add(SchemaPackage package, string profileName, int? seed,
        IEnumerable<SchemaTestResult> results, string? projectScope = null, Guid? profileId = null)
    {
        var items = results.Take(MaxResultsPerRun + 1).ToArray();
        if (items.Length > MaxResultsPerRun) throw new InvalidDataException("A saved Schema Studio run is limited to 500 results.");
        var provenance = package.Provenance is { SourceSnapshotId: not null } source
            ? new SchemaRunProvenance(source.SourceSnapshotId, Limit(source.SourceSnapshotFingerprint, 128),
                Limit(source.SelectedContractPath, 1024), Limit(source.ContractFingerprint, 128))
            : null;
        var run = new SavedSchemaTestRun(Guid.NewGuid(), Limit(package.Fingerprint, 128),
            provenance, Limit(profileName, 120), seed, DateTimeOffset.UtcNow,
            items.Select(r => new SchemaTestRunResult(Limit(r.TestId, 512), Limit(r.Path, 2048), Limit(r.Rule, 128),
                r.ExpectedValid, Limit(r.Actual, 32), Limit(r.Outcome, 32), "Validated locally against the recorded XSD fingerprint.")).ToArray(), NormalizeScope(projectScope), profileId);
        var previous = _runs.ToArray();
        if (_runs.Count >= MaxRuns) _runs.Remove(_runs.OrderBy(r => r.CreatedAt).First());
        _runs.Add(run);
        try { EnsureSize(); }
        catch { _runs.Clear(); _runs.AddRange(previous); throw; }
        return run;
    }

    public string SnapshotJson() => SnapshotJson(null, includeAllWhenScopeOmitted: true);

    public string SnapshotJson(string? projectScope) => SnapshotJson(projectScope, includeAllWhenScopeOmitted: false);

    private string SnapshotJson(string? projectScope, bool includeAllWhenScopeOmitted)
    {
        EnsureSize();
        var scope = NormalizeScope(projectScope);
        var selected = includeAllWhenScopeOmitted ? _runs : _runs.Where(run => run.ProjectScope == scope).ToList();
        return JsonSerializer.Serialize(new Document(Version, selected), Json);
    }

    public void RestoreJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) { _runs.Clear(); return; }
        if (Encoding.UTF8.GetByteCount(json) > MaxDocumentBytes) throw new InvalidDataException("Saved Schema Studio test history exceeds the 4 MB limit.");
        var doc = JsonSerializer.Deserialize<Document>(json, Json) ?? throw new InvalidDataException("Saved Schema Studio test history is empty.");
        if (doc.Version != Version || doc.Runs is null || doc.Runs.Count > MaxRuns
            || doc.Runs.Any(r => r is null || r.Id == Guid.Empty || r.Results is null || r.Results.Count > MaxResultsPerRun
                || string.IsNullOrWhiteSpace(r.PackageFingerprint) || r.PackageFingerprint.Length > 128
                || string.IsNullOrWhiteSpace(r.ProfileName) || r.ProfileName.Length > 120 || r.ProjectScope is { Length: > 512 }
                || r.ProfileId == Guid.Empty
                || r.Provenance is { } p && (p.SourceSnapshotId is not null && string.IsNullOrWhiteSpace(p.SourceSnapshotFingerprint)
                    || p.SourceSnapshotFingerprint is { Length: > 128 } || p.SelectedContractPath is { Length: > 1024 }
                    || string.IsNullOrWhiteSpace(p.ContractFingerprint) || p.ContractFingerprint.Length > 128)))
            throw new InvalidDataException("Saved Schema Studio test history has invalid metadata or exceeds its limits.");
        if (doc.Runs.Any(r => r.Results.Any(x => x is null || string.IsNullOrWhiteSpace(x.TestId) || x.TestId.Length > 512
            || string.IsNullOrWhiteSpace(x.Path) || x.Path.Length > 2048 || string.IsNullOrWhiteSpace(x.Rule) || x.Rule.Length > 128
            || string.IsNullOrWhiteSpace(x.Actual) || x.Actual.Length > 32 || string.IsNullOrWhiteSpace(x.Outcome) || x.Outcome.Length > 32
            || x.Diagnostic is null || x.Diagnostic.Length > 1024)))
            throw new InvalidDataException("Saved Schema Studio test history contains oversized result metadata.");
        if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(doc, Json)) > MaxDocumentBytes)
            throw new InvalidDataException("Saved Schema Studio test history exceeds the 4 MB limit.");
        _runs.Clear();
        _runs.AddRange(doc.Runs);
    }

    private void EnsureSize()
    {
        if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(new Document(Version, _runs), Json)) > MaxDocumentBytes)
            throw new InvalidDataException("Saved Schema Studio test history exceeds the 4 MB limit.");
    }

    private static string Limit(string? value, int length) => string.IsNullOrEmpty(value) ? "" : value.Length <= length ? value : value[..length];
    private static string? NormalizeScope(string? scope) => string.IsNullOrWhiteSpace(scope) ? null : scope.Trim();
    private sealed record Document(int Version, IReadOnlyList<SavedSchemaTestRun>? Runs);
}
