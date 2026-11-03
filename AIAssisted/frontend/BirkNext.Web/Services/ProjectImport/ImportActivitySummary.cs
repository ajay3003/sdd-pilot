using BirkNext.Integrations;
using BirkNext.ProjectActivity;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services.ProjectImport;

/// <summary>
/// What one import means, in one line and as an activity event: the package's role for its application, what it holds, its own Source Analysis
/// status and how many application setup items need attention. "Imported" (or "Imported with attention") — never reviewed, tested, passed or
/// ready. Source Analysis that ran as part of the import is summarized here, not recorded as a second event.
/// </summary>
public sealed record ImportActivitySummary(string ApplicationId, string ApplicationName, string PackageName, PackageEvidenceRole Role, int Documents,
    string? AnalysisStatus, int Attention)
{
    public string RoleLabel => ApplicationContexts.RoleLabel(Role);

    /// <summary>
    /// "Supporting/shared source · 4 SDD documents · Source Analysis Partial". Only the package's own facts: application-wide setup items
    /// (<see cref="Attention"/>) belong to the application, not to every package, and a role that never carries SDD documents (infrastructure,
    /// contracts, supporting source) does not report their absence.
    /// </summary>
    public string Line
    {
        get
        {
            var parts = new List<string> { RoleLabel };
            if (Documents > 0) parts.Add($"{Documents} SDD document{(Documents == 1 ? "" : "s")}");
            else if (Role is PackageEvidenceRole.PrimaryApplicationSource or PackageEvidenceRole.RelatedApplicationSource or PackageEvidenceRole.Documentation)
                parts.Add("No SDD project documents detected");
            // "Partial · no application code": an infrastructure, contracts or supporting package never claims application code.
            if (AnalysisStatus is { } status)
                parts.Add($"Source Analysis {(Role is PackageEvidenceRole.PrimaryApplicationSource or PackageEvidenceRole.RelatedApplicationSource ? status : status.Replace(" · no application code", ""))}");
            return string.Join(" · ", parts);
        }
    }

    public static ImportActivitySummary? Of(SddLifecycleState lifecycle, SddProjectImportRecord record, SourceSetupView? setup, DateTimeOffset now)
    {
        if (ApplicationContexts.Find(lifecycle, record.ApplicationId) is not { } application) return null;
        var package = ApplicationContexts.Packages(lifecycle, application.Id, setup).FirstOrDefault(p => p.ImportId == record.ImportId);
        var source = ProjectImportPresentation.CurrentSource(record, now);
        var snapshot = setup is null ? null : ProjectImportHistory.SnapshotsOf(record, setup.Snapshots).OrderByDescending(s => s.SnapshotId == record.SourceSnapshotId).FirstOrDefault();
        var analysis = ImportProjectOverview.SourceAnalysis(source, snapshot);
        // Application-wide setup (every package of the workspace counts), never this package alone; the environment note is not an attention item.
        var attention = ImportProjectOverview.Attention(null, setup, source, environmentSelected: true).Count(a => !a.Informational);
        return new(application.Id, application.Name, string.IsNullOrWhiteSpace(record.ProjectName) ? record.ArchiveFileName : record.ProjectName,
            package?.Role ?? PackageEvidenceRole.NotClassified, record.ArtifactDocumentCount,
            analysis.State == ImportAnalysisState.NoSource ? null : analysis.Status, attention);
    }

    /// <summary>The import's activity event. One per completed import commit (the commit's own time keys it, so a retry of recording is one event).</summary>
    public ProjectActivityRecordRequest Request(SddProjectImportRecord record, DateTimeOffset committedAt) => new()
    {
        ProjectContextId = ApplicationId, ProjectName = ApplicationName, Type = ProjectActivityType.ImportCompleted,
        // Attention only for the package's own problem (its source analysis was not created or failed), never for application-wide setup.
        Status = AnalysisStatus is "Not created" || AnalysisStatus?.StartsWith("Failed", StringComparison.Ordinal) == true ? ProjectActivityStatus.Attention : ProjectActivityStatus.Imported, SourceModule = "Import Project",
        Title = $"Imported {PackageName}", Summary = Line, PackageId = record.ImportId, PackageName = PackageName,
        RelatedEntityType = "ImportedPackage", RelatedEntityId = record.ImportId, Route = ProjectImportSession.Route,
        IdempotencyKey = $"import:{record.ImportId}:{committedAt.UtcTicks}",
    };
}
