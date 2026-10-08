using BirkNext.Integrations;
using BirkNext.ProjectImport;
using BirkNext.Web.Services.Explorers;
using BirkNext.Web.Services.SampleProjects;

namespace BirkNext.Web.Services.ProjectImport;

/// <summary>
/// Overall outcome of an import. Never one score: documents and source are reported separately below it.
/// <list type="bullet">
/// <item><see cref="Imported"/>: everything found was activated without anything to follow up.</item>
/// <item><see cref="ImportedWithNotes"/>: imported, with something partial (a role to choose, documents not classified, unsupported
/// technology, a source snapshot not created yet). Partial is never failed.</item>
/// <item><see cref="NothingToImport"/>: a valid archive with neither supported documents nor source. Neutral; nothing was activated.</item>
/// <item><see cref="Rejected"/>: the archive failed validation or could not be read. Nothing was staged or activated.</item>
/// </list>
/// </summary>
public enum ProjectImportOutcome { Imported, ImportedWithNotes, NothingToImport, Rejected }

/// <summary>Visual tone of a status. Colour is never the only signal: every tone is shown with its text label.</summary>
public enum ProjectImportTone { Available, Neutral, Partial, Rejected }

public sealed record ProjectImportRoleRow(WorkspaceArtifactType Role, string Label, ProjectImportTone Tone, string Status, string Detail);

public sealed record ProjectImportSourceRow(ProjectImportTone Tone, string Status, string Detail);

public static class ProjectImportPresentation
{
    /// <summary>Whether the preview has anything to import: at least one detected artifact document or detected source.</summary>
    public static bool HasImportableContent(ProjectImportPreview preview, ProjectImportArtifactDiscovery discovery) =>
        discovery.HasArtifacts || preview.Source.Detected;

    public static ProjectImportOutcome Outcome(ProjectImportPreview preview, ProjectImportArtifactDiscovery discovery, ProjectImportSourceResult? source)
    {
        if (!HasImportableContent(preview, discovery)) return ProjectImportOutcome.NothingToImport;
        return Notes(preview, discovery, source).Count == 0 ? ProjectImportOutcome.Imported : ProjectImportOutcome.ImportedWithNotes;
    }

    public static string OutcomeTitle(ProjectImportOutcome outcome) => outcome switch
    {
        ProjectImportOutcome.Imported => "Project imported",
        ProjectImportOutcome.ImportedWithNotes => "Project imported — some items need attention",
        ProjectImportOutcome.NothingToImport => "Nothing to import",
        _ => "Archive rejected",
    };

    public static ProjectImportTone OutcomeTone(ProjectImportOutcome outcome) => outcome switch
    {
        ProjectImportOutcome.Imported => ProjectImportTone.Available,
        ProjectImportOutcome.ImportedWithNotes => ProjectImportTone.Partial,
        ProjectImportOutcome.NothingToImport => ProjectImportTone.Neutral,
        _ => ProjectImportTone.Rejected,
    };

    /// <summary>What is partial about an import, in plain sentences. Empty for a clean import. A neutral absence (no source, no documents) is not a note.</summary>
    public static IReadOnlyList<string> Notes(ProjectImportPreview preview, ProjectImportArtifactDiscovery discovery, ProjectImportSourceResult? source)
    {
        var notes = new List<string>();
        foreach (var role in discovery.AmbiguousRoles)
            notes.Add(HasExplorer(role.Role)
                ? $"{ArtifactExplorerRoles.Label(role.Role)}: {role.Documents.Count} documents were detected. Choose one in the {ArtifactExplorerRoles.Label(role.Role)} Explorer."
                : $"{ArtifactExplorerRoles.Label(role.Role)}: {role.Documents.Count} documents were detected and kept; none is selected.");
        if (discovery.NeedsReview.Count > 0)
            notes.Add($"{Count(discovery.NeedsReview.Count, "document")} could fit more than one role and were not imported as artifacts.");
        if (discovery.Skipped.Count > 0)
            notes.Add($"{Count(discovery.Skipped.Count, "document")} could not be read (too large, binary or over the document limit).");
        if (source is { State: ProjectImportSourceState.NotCreated or ProjectImportSourceState.Failed } && !string.IsNullOrWhiteSpace(source.Message))
            notes.Add(source.Message!);
        else if (source is null && preview.Source.Detected)
            notes.Add("Source was detected, but no source snapshot was created.");
        if (source is { State: ProjectImportSourceState.Created or ProjectImportSourceState.Reused, SnapshotStatus: { } status } && status != SourceAnalysisStatus.Ready)
            notes.Add(status == SourceAnalysisStatus.Failed
                ? "The source snapshot was created, but Source Analysis found no production project it supports (see Technology Coverage)."
                : "The source snapshot is partial: Source Analysis could not analyze everything in the archive (see Technology Coverage).");
        if (preview.Source.UnsupportedSourceFiles > 0)
            notes.Add($"{Count(preview.Source.UnsupportedSourceFiles, "source file")} in a language Source Analysis does not analyze.");
        return notes;
    }

    /// <summary>One row per supported role: detected, several to choose from, or not found (neutral — every role is optional).</summary>
    public static IReadOnlyList<ProjectImportRoleRow> Roles(ProjectImportArtifactDiscovery discovery) =>
        discovery.Roles.Select(role => role.State switch
        {
            SampleRoleState.Detected => new ProjectImportRoleRow(role.Role, ArtifactExplorerRoles.Label(role.Role), ProjectImportTone.Available, "Detected",
                role.Documents[0].RelativePath),
            SampleRoleState.Multiple => new ProjectImportRoleRow(role.Role, ArtifactExplorerRoles.Label(role.Role), ProjectImportTone.Partial, $"{role.Documents.Count} candidates",
                HasExplorer(role.Role) ? "Imported without a selection — choose one in the explorer" : "Imported without a selection"),
            _ => new ProjectImportRoleRow(role.Role, ArtifactExplorerRoles.Label(role.Role), ProjectImportTone.Neutral, "Not found", "Optional"),
        }).ToList();

    /// <summary>The source part before the import runs.</summary>
    public static ProjectImportSourceRow SourcePreview(ProjectImportSourceDetection source, bool hasTargetEnvironment, bool sourceAnalysisAvailable = true)
    {
        if (!source.Detected)
            return new(ProjectImportTone.Neutral, "No source detected", "This archive has no source Source Analysis reads. Project documents can still be imported.");
        if (!sourceAnalysisAvailable)
            return new(ProjectImportTone.Partial, "Source detected", "Source Analysis is turned off, so no source snapshot will be created.");
        if (!hasTargetEnvironment)
            return new(ProjectImportTone.Partial, "Source detected — Target Environment needed",
                "Source snapshots belong to a Target Environment. Import now and create the snapshot after selecting one (no new upload needed), or select one first.");
        return new(ProjectImportTone.Available, "Source detected", $"{Count(source.SourceFiles, "source file")} — a new Source Analysis snapshot will be created.");
    }

    /// <summary>The source part after the import ran.</summary>
    public static ProjectImportSourceRow SourceResult(ProjectImportSourceResult source) => source.State switch
    {
        ProjectImportSourceState.NotDetected => new(ProjectImportTone.Neutral, "No source detected", "No source snapshot was created for this import. That is expected for a documents-only project."),
        ProjectImportSourceState.Created => new(source.SnapshotStatus == SourceAnalysisStatus.Ready ? ProjectImportTone.Available : ProjectImportTone.Partial,
            source.SnapshotStatus == SourceAnalysisStatus.Ready ? "Snapshot created" : "Snapshot created — partial coverage",
            "A new immutable Source Analysis snapshot was created from this archive. Created is not reviewed: run the source-based reviews next."),
        ProjectImportSourceState.Reused => new(source.SnapshotStatus == SourceAnalysisStatus.Ready ? ProjectImportTone.Available : ProjectImportTone.Partial,
            "Current snapshot reused", "The same archive is already the current Source Analysis snapshot of this Target Environment, so it was not analyzed again."),
        ProjectImportSourceState.NotCreated => new(ProjectImportTone.Partial, "Snapshot not created", source.Message ?? "Source was detected, but no snapshot was created."),
        _ => new(ProjectImportTone.Partial, "Snapshot not created — retry available", source.Message ?? "Source analysis did not complete. No snapshot was created."),
    };

    /// <summary>Research artifacts are kept and counted, but no explorer opens them.</summary>
    private static bool HasExplorer(WorkspaceArtifactType role) => role != WorkspaceArtifactType.Research;

    public static string ToneClass(ProjectImportTone tone) => tone switch
    {
        ProjectImportTone.Available => "available",
        ProjectImportTone.Partial => "partial",
        ProjectImportTone.Rejected => "rejected",
        _ => "neutral",
    };

    public static string NameBasis(ProjectNameBasis basis) => basis == ProjectNameBasis.ArchiveRoot ? "from the archive's root folder" : "from the archive file name";

    public static string ShortFingerprint(string sha256) => sha256.Length > 12 ? sha256[..12] : sha256;

    public static string Size(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / (1024d * 1024):0.0} MB" : $"{Math.Max(1, bytes / 1024d):0} KB";

    private static string Count(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";
}
