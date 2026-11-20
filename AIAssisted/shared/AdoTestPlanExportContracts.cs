using System.Text.Json.Serialization;

namespace BirkNext.TestPlanning;

// ── Azure DevOps Test Plans export (offline XLSX / CSV) ─────────────────────────────────────────────────────────────
// Builds an import file for Azure Test Plans ("Import test cases from CSV/XLSX" in a test suite) from SAVED test cases of one saved plan
// revision. Read-only: nothing is changed in BirkNext and nothing is sent to Azure DevOps. Destination values (Area Path, Assigned To, State)
// are checked for form only; BirkNext cannot prove they exist in the destination organization. Format per Microsoft Learn "Bulk import or
// export test cases" (2026-08-10): one row per test step, case fields repeated on every row, blank ID creates a new test case.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AdoTarget
{
    /// <summary>Azure DevOps Services: Area Path is a required import column.</summary>
    Services,
    /// <summary>Azure DevOps Server 2022 / current Server: Area Path is not required and not modified by the import.</summary>
    Server,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AdoExportFormat { Xlsx, Csv }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AdoExportMode
{
    /// <summary>New test cases: the ID column is empty.</summary>
    CreateNew,
    /// <summary>Update existing Azure DevOps test cases by an ID the user confirmed. Re-importing replaces ALL steps of that test case.</summary>
    UpdateExisting,
}

/// <summary>How a case's preconditions travel. The documented import columns have no preconditions field.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AdoPreconditionsMode
{
    /// <summary>Not in the import file; listed in the mapping report (default, nothing invented).</summary>
    MappingReport,
    /// <summary>Explicit choice: a first step "Check the preconditions: …" with the expected result "The preconditions are met".</summary>
    FirstStep,
}

/// <summary>Project-level export preferences (no credentials, no tokens). Stored per BirkNext project.</summary>
public sealed record AdoExportProfile
{
    /// <summary>A label for the destination (organization / project), shown in the file name and history only.</summary>
    public string? DestinationLabel { get; init; }
    public AdoTarget Target { get; init; } = AdoTarget.Services;
    public string? AreaPath { get; init; }
    public string? AssignedTo { get; init; }
    /// <summary>The initial state of new test cases; it must be in the destination process's Proposed category (Design by default).</summary>
    public string State { get; init; } = "Design";
    public AdoExportFormat Format { get; init; } = AdoExportFormat.Xlsx;
    public AdoPreconditionsMode Preconditions { get; init; } = AdoPreconditionsMode.MappingReport;
    /// <summary>Add a Priority column (optional field; map it in the import wizard). Off by default.</summary>
    public bool IncludePriority { get; init; }
    /// <summary>CSV only: prefix cells starting with = + - @ with an apostrophe so spreadsheet programs never run them (Azure DevOps then keeps
    /// the apostrophe as text). XLSX never needs this: its cells are stored as text, never as formulas.</summary>
    public bool CsvSpreadsheetSafe { get; init; } = true;
    /// <summary>Add a Description column with where each test case came from (specification file, acceptance scenario, requirements with their
    /// files, the BirkNext case) so the origin stays visible in Azure DevOps. On by default; map it to Description in the import wizard.</summary>
    public bool IncludeSourceReferences { get; init; } = true;
    /// <summary>Azure DevOps test case ids the tester confirmed in an earlier update export, by BirkNext case key ("{plan id}/{case id}"): offered
    /// again so an id is entered once. Never guessed; the tester can change or clear one.</summary>
    public Dictionary<string, string> KnownTestCaseIds { get; init; } = [];
}

public sealed record AdoExportRequest
{
    public string ProjectKey { get; init; } = "";
    public Guid DraftId { get; init; }
    /// <summary>The saved plan revision the user reviewed; a newer saved revision is refused (409) so the file never differs from the preview.</summary>
    public int PlanRevision { get; init; }
    /// <summary>Selected case ids; empty = every case that is eligible.</summary>
    public List<string> CaseIds { get; init; } = [];
    /// <summary>Also export cases that are prepared but not approved (explicit choice).</summary>
    public bool IncludeNotApproved { get; init; }
    public AdoExportMode Mode { get; init; } = AdoExportMode.CreateNew;
    /// <summary>Update mode: Azure DevOps test case id per case id, as the user verified them in Azure DevOps.</summary>
    public Dictionary<string, string> ExistingIds { get; init; } = [];
    /// <summary>Update mode: the user confirms the ids were verified in Azure DevOps and that the import replaces their steps.</summary>
    public bool ExistingIdsConfirmed { get; init; }
    public AdoExportProfile Profile { get; init; } = new();
    /// <summary>"nb" = export each case's Norwegian text where the case has a complete one (else its original, with a note); null = the original.</summary>
    public string? Language { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AdoIssueLevel { Blocking, Warning, Info }

/// <summary>Whether a destination value was checked: only its form can be checked offline.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AdoFieldCheck { LocallyValid, NeedsConfirmationInAzureDevOps, Invalid, Missing, NotRequired }

public sealed record AdoExportIssue(AdoIssueLevel Level, string Message, string? CaseId = null);

public sealed record AdoFieldStatus(string Field, string? Value, AdoFieldCheck Check, string Explanation);

/// <summary>One import row (one test step). Exactly what both the XLSX and the CSV file contain.</summary>
public sealed record AdoExportRow(string Id, string WorkItemType, string Title, int TestStep, string StepAction, string StepExpected, string AreaPath, string AssignedTo,
    string State, string? Priority, string? Description = null);

public sealed record AdoExportCase
{
    public string CaseId { get; init; } = "";
    public string CaseKey { get; init; } = "";
    public string Title { get; init; } = "";
    public string Review { get; init; } = "";
    /// <summary>In the file (ready) or not, and why not.</summary>
    public bool Included { get; init; }
    public string? ExcludedReason { get; init; }
    public int Steps { get; init; }
    public string? AzureDevOpsId { get; init; }
    public List<string> Requirements { get; init; } = [];
    public List<string> TestData { get; init; } = [];
    public string? Preconditions { get; init; }
    public string? Priority { get; init; }
    public List<AdoExportIssue> Issues { get; init; } = [];
    /// <summary>Where the case came from (pinned the same way a manual run pins it): the specification file and acceptance scenario, and each
    /// requirement with its file — in the Description column and the mapping report.</summary>
    public string? SourceSpecification { get; init; }
    public string? SourceScenario { get; init; }
    public List<string> RequirementSources { get; init; } = [];
    /// <summary>The Azure DevOps id came from an earlier confirmed export (not typed now).</summary>
    public bool AzureDevOpsIdRemembered { get; init; }
    /// <summary>The language this case is exported in ("Original" or "Norwegian").</summary>
    public string Language { get; init; } = "Original";
    /// <summary>The scenario the case belongs to (null = none established) and the saved plan revision the text comes from.</summary>
    public string? ScenarioKey { get; init; }
    public string? ScenarioTitle { get; init; }
    public int CaseRevision { get; init; }
}

public sealed record AdoExportPreview
{
    public string ProjectKey { get; init; } = "";
    public Guid DraftId { get; init; }
    public string PlanName { get; init; } = "";
    public int PlanRevision { get; init; }
    public int TotalCases { get; init; }
    public int SelectedCases { get; init; }
    public int ReadyCases { get; init; }
    public int CasesNeedingAttention { get; init; }
    public List<AdoExportCase> Cases { get; init; } = [];
    public List<AdoExportRow> Rows { get; init; } = [];
    public List<AdoFieldStatus> Fields { get; init; } = [];
    public List<AdoExportIssue> Issues { get; init; } = [];
    /// <summary>True when nothing blocks the download (at least one ready case, valid destination form, mode rules met).</summary>
    public bool CanDownload { get; init; }
    public List<string> Headers { get; init; } = [];
    public string Boundary { get; init; } = AdoExport.Boundary;
}

/// <summary>A generated file (base64 for the browser download).</summary>
public sealed record AdoExportFile(string FileName, string ContentType, string Base64, int Rows, int Cases);

public sealed record AdoExportHistoryEntry(Guid Id, Guid DraftId, string PlanName, int PlanRevision, string Format, string Mode, int Cases, int Rows, string? Destination,
    string ExportedBy, DateTimeOffset ExportedAt, List<string> CaseRevisions);

public static class AdoExport
{
    public const string WorkItemType = "Test Case";
    public const string Boundary = "BirkNext creates an import file only. Import it in Azure DevOps: Test Plans → select the test plan and test suite → Import test cases from CSV/XLSX. Downloading is not importing; BirkNext cannot check that the Area Path, user or state exist in your organization, and the live import has not been tested from BirkNext.";
    public static readonly IReadOnlyList<string> Headers = ["ID", "Work Item Type", "Title", "Test Step", "Step Action", "Step Expected", "Area Path", "Assigned To", "State"];
    public const string PriorityHeader = "Priority";
    public const string DescriptionHeader = "Description";

    /// <summary>BirkNext priority words → Azure DevOps Priority 1–4. Anything else is not mapped (left empty, with a warning).</summary>
    public static readonly IReadOnlyDictionary<string, string> PriorityMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["1"] = "1", ["Critical"] = "1", ["Highest"] = "1", ["2"] = "2", ["High"] = "2", ["3"] = "3", ["Medium"] = "3", ["Normal"] = "3", ["4"] = "4", ["Low"] = "4", ["Lowest"] = "4",
    };
}
