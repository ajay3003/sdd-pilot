namespace BirkNext.Api.Models;

/// <summary>Azure DevOps export preferences of one BirkNext project (table <c>ado_export_profiles</c>): destination label, target version, Area
/// Path, Assigned To, initial State, format and options. Never credentials or tokens.</summary>
public sealed class AdoExportProfileRecord
{
    public string ProjectKey { get; set; } = "";
    public string ProfileJson { get; set; } = "";
    public string UpdatedBy { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One generated Azure DevOps import file (table <c>ado_export_records</c>): what was exported (plan revision, case revisions, format,
/// mode, counts, destination label) and by whom. The file itself is not stored, and a download is never recorded as an import.</summary>
public sealed class AdoExportRecord
{
    public Guid Id { get; set; }
    public string ProjectKey { get; set; } = "";
    public Guid DraftId { get; set; }
    public string PlanName { get; set; } = "";
    public int PlanRevision { get; set; }
    public string Format { get; set; } = "";
    public string Mode { get; set; } = "";
    public int Cases { get; set; }
    public int Rows { get; set; }
    public string? Destination { get; set; }
    public string ContentJson { get; set; } = "";
    public string ExportedBy { get; set; } = "";
    public DateTimeOffset ExportedAt { get; set; }
}
