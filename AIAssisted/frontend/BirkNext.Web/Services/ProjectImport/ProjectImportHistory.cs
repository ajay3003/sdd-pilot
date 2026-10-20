using BirkNext.Integrations;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services.ProjectImport;

/// <summary>Where an earlier import came from: a Project Import recorded in this workspace, or a stored Source Analysis snapshot only.</summary>
public enum PreviousImportOrigin { ProjectImport, SourceAnalysis }

/// <summary>
/// One earlier import, as Import Project lists it. The logical sources are the test packs' recognition of its snapshot (by content, never by
/// archive name); <see cref="BoundIntegrations"/> are the canonical IQR bindings of the selected target environment that use the snapshot.
/// </summary>
public sealed record PreviousImportRow(
    string Key,
    PreviousImportOrigin Origin,
    string? ImportId,
    string DisplayName,
    string ArchiveFileName,
    string Fingerprint,
    DateTimeOffset ImportedAt,
    Guid? SnapshotId,
    string SourceStatus,
    string Documents,
    bool HasSource,
    IReadOnlyList<string> LogicalSources,
    IReadOnlyList<string> BoundIntegrations,
    IReadOnlyList<string> InfrastructureFormats)
{
    /// <summary>Environment labels the source's configuration declares (Development, QA, Local …). Informational: not deployed environments.</summary>
    public IReadOnlyList<string> ConfigurationEnvironments { get; init; } = [];

    /// <summary>Only a Project Import recorded in this workspace can become the current project again (its documents are in the repository).</summary>
    public bool CanUseAsCurrent => Origin == PreviousImportOrigin.ProjectImport && ImportId is not null;
}

/// <summary>A preview whose exact archive (same fingerprint) is already imported or analyzed. Shown before importing; never chosen silently.</summary>
public sealed record ProjectImportDuplicate(string ArchiveFileName, string Fingerprint, DateTimeOffset ImportedAt, string? ImportId, Guid? SnapshotId, bool IsCurrent);

/// <summary>
/// Import history as Import Project shows it: the workspace's recorded imports (source-only ones included) and stored Source Analysis snapshots
/// that no recorded import accounts for, minus the current project. Pure: reads the lifecycle records and the backend's source setup only.
/// </summary>
public static class ProjectImportHistory
{
    public static IReadOnlyList<PreviousImportRow> Previous(IReadOnlyList<SddProjectImportRecord> imports, string? currentImportId, SourceSetupView? setup)
    {
        var snapshots = setup?.Snapshots ?? [];
        var rows = new List<PreviousImportRow>();
        var accounted = new HashSet<Guid>();
        var current = imports.FirstOrDefault(i => i.ImportId == currentImportId);
        foreach (var record in imports)
        {
            var mine = SnapshotsOf(record, snapshots);
            accounted.UnionWith(mine.Select(s => s.SnapshotId));
            if (record.ImportId == currentImportId) continue;
            var snapshot = mine.FirstOrDefault(s => s.SnapshotId == record.SourceSnapshotId) ?? mine.FirstOrDefault();
            rows.Add(new PreviousImportRow($"import:{record.ImportId}", PreviousImportOrigin.ProjectImport, record.ImportId, record.ProjectName, record.ArchiveFileName,
                record.ArchiveSha256, record.ImportedAt, record.SourceSnapshotId ?? snapshot?.SnapshotId, snapshot?.Status ?? SourceStateText(record),
                DocumentsText(record), record.SourceSnapshotId is not null || snapshot is not null,
                LogicalSources(setup, snapshot?.SnapshotId ?? record.SourceSnapshotId), snapshot?.BoundIntegrations ?? [], snapshot?.InfrastructureFormats ?? [])
            { ConfigurationEnvironments = snapshot?.ConfigurationEnvironments ?? [] });
        }
        // Stored snapshots no recorded import accounts for (a Source Analysis upload, or an import made in another workspace). The same archive
        // analyzed twice is one row: its newest snapshot. The current project's archive never reappears here.
        foreach (var group in snapshots.Where(s => !accounted.Contains(s.SnapshotId) && (current is null || !string.Equals(s.Fingerprint, current.ArchiveSha256, StringComparison.Ordinal)))
                     .GroupBy(s => s.Fingerprint, StringComparer.Ordinal))
        {
            var snapshot = group.OrderByDescending(s => s.AnalyzedAt).ThenBy(s => s.SnapshotId).First();
            rows.Add(new PreviousImportRow($"snapshot:{snapshot.SnapshotId:D}", PreviousImportOrigin.SourceAnalysis, snapshot.ImportId, snapshot.Repository ?? StripZip(snapshot.ArchiveFileName),
                snapshot.ArchiveFileName, snapshot.Fingerprint, snapshot.AnalyzedAt, snapshot.SnapshotId, snapshot.Status, "No project documents imported", true,
                LogicalSources(setup, snapshot.SnapshotId), snapshot.BoundIntegrations, snapshot.InfrastructureFormats)
            { ConfigurationEnvironments = snapshot.ConfigurationEnvironments });
        }
        return rows.OrderByDescending(r => r.ImportedAt).ThenBy(r => r.Key, StringComparer.Ordinal).ToList();
    }

    /// <summary>The stored snapshots a recorded import created: its recorded snapshot id, or a snapshot carrying its import id.</summary>
    public static IReadOnlyList<SourceSetupSnapshot> SnapshotsOf(SddProjectImportRecord record, IReadOnlyList<SourceSetupSnapshot> snapshots) =>
        snapshots.Where(s => s.SnapshotId == record.SourceSnapshotId || (s.ImportId is not null && s.ImportId == record.ImportId)).ToList();

    /// <summary>The logical sources (pack source roles) that recognized this snapshot, across every pack, by label.</summary>
    public static IReadOnlyList<string> LogicalSources(SourceSetupView? setup, Guid? snapshotId) =>
        snapshotId is not { } id || setup is null ? [] :
        setup.Packs.SelectMany(p => p.Sources).Where(s => s.Candidates.Any(c => c.SnapshotId == id)).Select(s => s.Role.Label).Distinct().ToList();

    /// <summary>An earlier import or stored snapshot with exactly this archive content, if any (the current project included, flagged).</summary>
    public static ProjectImportDuplicate? Duplicate(string fingerprint, IReadOnlyList<SddProjectImportRecord> imports, string? currentImportId, SourceSetupView? setup)
    {
        if (string.IsNullOrWhiteSpace(fingerprint)) return null;
        if (imports.Where(i => string.Equals(i.ArchiveSha256, fingerprint, StringComparison.Ordinal)).OrderByDescending(i => i.ImportedAt).FirstOrDefault() is { } record)
            return new(record.ArchiveFileName, record.ArchiveSha256, record.ImportedAt, record.ImportId, record.SourceSnapshotId, record.ImportId == currentImportId);
        if ((setup?.Snapshots ?? []).Where(s => string.Equals(s.Fingerprint, fingerprint, StringComparison.Ordinal)).OrderByDescending(s => s.AnalyzedAt).FirstOrDefault() is { } snapshot)
            return new(snapshot.ArchiveFileName, snapshot.Fingerprint, snapshot.AnalyzedAt, null, snapshot.SnapshotId, false);
        return null;
    }

    /// <summary>"Used by 1 IQR integration" — or why bindings are not shown.</summary>
    public static string BindingsText(IReadOnlyList<string> bound, bool bindingsRead) =>
        !bindingsRead ? "Select a target environment to see IQR bindings"
        : bound.Count == 0 ? "Not bound in IQR"
        : $"Used by {bound.Count} IQR integration{(bound.Count == 1 ? "" : "s")}: {string.Join(", ", bound)}";

    public static string DocumentsText(SddProjectImportRecord record) =>
        record.ArtifactDocumentCount == 0 ? "No project documents"
        : $"{record.ArtifactDocumentCount} document{(record.ArtifactDocumentCount == 1 ? "" : "s")}" + (record.AmbiguousRoles.Count > 0 ? $" · {record.AmbiguousRoles.Count} role(s) to choose" : "");

    public static string ShortId(Guid id) => id.ToString("N")[..8];

    private static string SourceStateText(SddProjectImportRecord record) => record.SourceState switch
    {
        "NotDetected" or "" => "No source",
        "NotCreated" or "Failed" => "No snapshot",
        _ => "Snapshot not found",
    };

    private static string StripZip(string fileName) => fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? fileName[..^4] : fileName;
}

/// <summary>Plain-language wording for a pack's logical sources on Import Project. Names no pack, product or file.</summary>
public static class SourceSetupPresentation
{
    public static string RequirementLabel(TestPackSourceRequirement requirement) => requirement switch
    {
        TestPackSourceRequirement.Optional => "Optional",
        TestPackSourceRequirement.Conditional => "Conditional",
        _ => "Required",
    };

    /// <summary>The requirement with its scope or condition: "Required for …", "Optional — …", "Conditional — needed only when …".</summary>
    public static string RequirementText(TestPackSourceRole role) => (role.Requirement, role.RequirementCondition) switch
    {
        (TestPackSourceRequirement.Conditional, { Length: > 0 } condition) => $"Conditional — needed only when {condition.TrimEnd('.')}.",
        (_, { Length: > 0 } condition) => condition,
        (TestPackSourceRequirement.Optional, _) => "Optional — adds evidence, never blocks.",
        (TestPackSourceRequirement.Conditional, _) => "Conditional.",
        _ => "Required by the pack's journeys.",
    };

    /// <summary>State wording that never reads as a failure for an optional source and never says "missing" when the source exists.</summary>
    public static string StateLabel(TestPackSourceState state) => state switch
    {
        TestPackSourceState.Bound => "Bound",
        TestPackSourceState.AvailableNotBound => "Available, not bound",
        TestPackSourceState.Available => "Available · no binding needed",
        TestPackSourceState.ReviewRequired => "Review required",
        TestPackSourceState.NotProvided => "Not provided",
        _ => "Missing",
    };

    public static string StateTone(TestPackSourceState state) => state switch
    {
        TestPackSourceState.Bound or TestPackSourceState.Available => "available",
        TestPackSourceState.AvailableNotBound or TestPackSourceState.ReviewRequired => "partial",
        TestPackSourceState.Missing => "rejected",
        _ => "neutral",
    };

    /// <summary>The next action as link text that names the source ("Bind M2LB in IQR Evidence"), and where it goes. Null when nothing is to do.
    /// A missing source is imported on this page (<paramref name="importAnchor"/>).</summary>
    public static (string Text, string Href)? Action(TestPackSourceStatus status, string importAnchor)
    {
        var label = status.Role.Label;
        return (status.State, status.NextActionTarget) switch
        {
            (TestPackSourceState.Missing, _) => ($"Import {label} source package", importAnchor),
            (TestPackSourceState.NotProvided, _) => ($"Optional: import {label}", importAnchor),
            (TestPackSourceState.Bound, _) => ($"Open {label} evidence in IQR", Href("iqr-evidence")!),
            (TestPackSourceState.Available, _) when status.Candidates.Count > 0 => ($"Open {label} in Source Analysis", ProjectImportPresentation.SourceAnalysisHref(status.Candidates[0].SnapshotId)),
            (_, "target-environment") => ($"Select a target environment to bind {label}", Href("target-environment")!),
            (_, "target-environment-integrations") => ($"Configure integrations for {label}", Href("target-environment-integrations")!),
            (TestPackSourceState.ReviewRequired, _) => ($"Review {label} snapshots in IQR Evidence", Href("iqr-evidence")!),
            (_, "iqr-evidence") => ($"Bind {label} in IQR Evidence", Href("iqr-evidence")!),
            (_, { } target) when Href(target) is { } href => ($"{status.NextAction} ({label})", href),
            _ => null,
        };
    }

    public static string? Href(string? target) => target switch
    {
        "target-environment" => "/admin/system-settings?section=target-environments",
        _ => SourceGuidancePresentation.ActionHref(target),
    };

    /// <summary>What the source packages are: the bound one first, then the other matching packages.</summary>
    public static string PackagesText(TestPackSourceStatus status)
    {
        if (status.Candidates.Count == 0) return $"Expected: {status.Role.ExpectedArtifact}";
        var bound = status.Candidates.Where(c => c.Bound).Select(c => c.ArchiveFileName).Distinct().ToList();
        var others = status.Candidates.Where(c => !c.Bound).Select(c => c.ArchiveFileName).Distinct().ToList();
        if (bound.Count > 0) return $"Bound package: {string.Join(", ", bound)}" + (others.Count > 0 ? $" · also available: {string.Join(", ", others)}" : "");
        return $"Available package{(others.Count == 1 ? "" : "s")}: {string.Join(", ", others)}";
    }

    public static string JourneysText(TestPackSourceStatus status, IReadOnlyDictionary<string, string> names) =>
        string.Join(", ", status.Role.Journeys.Select(id => names.TryGetValue(id, out var name) ? name : id));
}
