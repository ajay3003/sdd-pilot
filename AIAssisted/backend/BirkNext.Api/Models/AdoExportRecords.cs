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

/// <summary>
/// The link between one BirkNext test case and one Azure DevOps test case in one organization + project (table <c>ado_test_case_links</c>,
/// 2026-11-25): what «Send direkte» created or updated, the case text revision and the remote work item revision at that time, and the plan/suite
/// it was added to. State «Uncertain» = a create was sent and its answer was lost; nothing is created for that case again until it is checked.
/// Kept with the test plans by every reset (losing it would let a later transfer create duplicates).
/// </summary>
public sealed class AdoTestCaseLinkRecord
{
    public Guid Id { get; set; }
    public string ProjectKey { get; set; } = "";
    /// <summary>"{plan id:N}/{case id}" (the same key as the file export and the mapping report).</summary>
    public string CaseKey { get; set; } = "";
    public string Organization { get; set; } = "";
    public string AdoProject { get; set; } = "";
    public string State { get; set; } = "Linked";
    public int? TestCaseId { get; set; }
    public int? RemoteRevision { get; set; }
    public string? CaseRevision { get; set; }
    public int PlanRevision { get; set; }
    public int? AdoPlanId { get; set; }
    public int? AdoSuiteId { get; set; }
    public string Title { get; set; } = "";
    public DateTimeOffset? AttemptedAt { get; set; }
    public string UpdatedBy { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// A test result fetched back from Azure DevOps (table <c>external_test_results</c>, phase 2, 2026-11-25): «Gjennomført i Azure DevOps», kept apart
/// from BirkNext's own runs. Immutable once read; unique per Azure DevOps run + result, so fetching again never duplicates. <see cref="ExportedRevision"/>
/// is the case's text fingerprint at its last export or transfer: the result counts for the current case only while that is still the case's text.
/// </summary>
public sealed class ExternalTestResultRecord
{
    public Guid Id { get; set; }
    public string Source { get; set; } = "AzureDevOps";
    public string ProjectKey { get; set; } = "";
    public Guid DraftId { get; set; }
    public string CaseKey { get; set; } = "";
    public string Organization { get; set; } = "";
    public string AdoProject { get; set; } = "";
    public int TestCaseId { get; set; }
    public int PlanId { get; set; }
    public int SuiteId { get; set; }
    public int PointId { get; set; }
    public int RunId { get; set; }
    public int ResultId { get; set; }
    public string Outcome { get; set; } = "";
    public string? Tester { get; set; }
    public string? Configuration { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? ExportedRevision { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
    public string FetchedBy { get; set; } = "";
}
