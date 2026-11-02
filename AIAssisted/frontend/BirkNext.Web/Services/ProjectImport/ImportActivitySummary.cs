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

    /// <summary>"Supporting/shared source · 4 documents · Source Analysis Partial · 3 items need attention".</summary>
    public string Line
    {
        get
        {
            var parts = new List<string> { RoleLabel, Documents == 0 ? "no project documents" : $"{Documents} document{(Documents == 1 ? "" : "s")}" };
            if (AnalysisStatus is { } status) parts.Add($"Source Analysis {status}");
            if (Attention > 0) parts.Add($"{Attention} item{(Attention == 1 ? "" : "s")} need{(Attention == 1 ? "s" : "")} attention");
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
        Status = Attention > 0 ? ProjectActivityStatus.Attention : ProjectActivityStatus.Imported, SourceModule = "Import Project",
        Title = $"Imported {PackageName}", Summary = Line, PackageId = record.ImportId, PackageName = PackageName,
        RelatedEntityType = "ImportedPackage", RelatedEntityId = record.ImportId, Route = ProjectImportSession.Route,
        IdempotencyKey = $"import:{record.ImportId}:{committedAt.UtcTicks}",
    };
}
