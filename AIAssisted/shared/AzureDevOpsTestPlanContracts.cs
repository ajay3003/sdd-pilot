using System.Text.Json.Serialization;

namespace BirkNext.TestPlanning;

// ── Azure DevOps Test Plans: destination, plan/suite discovery and creation, direct transfer (2026-11-25) ─────────────────────────────────
// One Azure DevOps connection (the stored Delivery Reporting connection: organization + encrypted PAT) is reused; there is no second login.
// Saving a destination is LOCAL only. Creating a test plan, a test suite or test cases in Azure DevOps happens only through an explicit,
// confirmed action, and an answer that was lost is recorded as «uncertain» and must be checked before anything is created again.

/// <summary>Where manual test cases go in Azure DevOps. Names are shown; ids are kept for the API (never typed by the user).</summary>
public sealed record AdoTestPlanDestination
{
    public Guid ConnectionId { get; init; }
    public string Organization { get; init; } = "";
    public string Project { get; init; } = "";
    public int PlanId { get; init; }
    public string PlanName { get; init; } = "";
    public int SuiteId { get; init; }
    public string SuiteName { get; init; } = "";
    /// <summary>"Root › Parent › Suite" for display (names only).</summary>
    public string? SuitePath { get; init; }

    [JsonIgnore] public bool Complete => ConnectionId != Guid.Empty && Organization.Length > 0 && Project.Length > 0 && PlanId > 0 && SuiteId > 0;
}

/// <summary>Which saved destination applies: an explicit choice wins over the project's default, which wins over the system default.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AdoDestinationSource { None, System, Project, Explicit }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AdoDestinationScope { System, Project }

/// <summary>A saved default destination with who saved it and when.</summary>
public sealed record AdoSavedDestination(AdoTestPlanDestination Destination, string SavedBy, DateTimeOffset SavedAt);

public sealed record AdoSaveDestinationRequest(AdoDestinationScope Scope, string? ProjectKey, AdoTestPlanDestination? Destination);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AdoPendingKind { Plan, Suite }

/// <summary>A create whose answer was lost: it may exist in Azure DevOps. Nothing with this name is created again until it has been checked.</summary>
public sealed record AdoPendingCreation
{
    public Guid Id { get; init; }
    public AdoPendingKind Kind { get; init; }
    public Guid ConnectionId { get; init; }
    public string Organization { get; init; } = "";
    public string Project { get; init; } = "";
    public int? PlanId { get; init; }
    public int? ParentSuiteId { get; init; }
    public string Name { get; init; } = "";
    public DateTimeOffset AttemptedAt { get; init; }
    public string AttemptedBy { get; init; } = "";
}

/// <summary>System Settings → Azure DevOps – Testplaner: connections (names only), saved defaults and creates that must be checked.</summary>
public sealed record AdoTestPlanSettingsView
{
    public List<AzureDevOpsConnectionInfo> Connections { get; init; } = [];
    public AdoSavedDestination? SystemDefault { get; init; }
    public AdoSavedDestination? ProjectDefault { get; init; }
    public string? ProjectKey { get; init; }
    public List<AdoPendingCreation> Pending { get; init; } = [];
}

/// <summary>A stored connection as the Test Plans settings show it (no credential; only whether one is stored).</summary>
public sealed record AzureDevOpsConnectionInfo(Guid Id, string DisplayName, string Organization, bool CredentialConfigured, string? LastValidationStatus, DateTimeOffset? LastValidatedAt);

public sealed record AdoProjectInfo(string Id, string Name);

public sealed record AdoTestPlanInfo(int Id, string Name, string? State, int RootSuiteId, string? AreaPath, string? Iteration, string? WebUrl);

public sealed record AdoTestSuiteInfo(int Id, string Name, int? ParentId, string SuiteType);

/// <summary>A list from Azure DevOps or why it could not be read (Norwegian, safe: never the request or the token).</summary>
public sealed record AdoListResult<T>(List<T> Items, string? Error, string? ErrorCode = null);

public sealed record AdoCreatePlanRequest(Guid ConnectionId, string Project, string Name, string? Description, bool Confirmed);

public sealed record AdoCreateSuiteRequest(Guid ConnectionId, string Project, int PlanId, int ParentSuiteId, string Name, bool Confirmed);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AdoCreateOutcome
{
    /// <summary>Azure DevOps confirmed the create and returned its id.</summary>
    Created,
    /// <summary>One with this name already exists: nothing was created (use it instead).</summary>
    AlreadyExists,
    /// <summary>The answer was lost (timeout, broken connection, server error): it may exist. Recorded as pending; check before trying again.</summary>
    Uncertain,
    /// <summary>Azure DevOps refused it (or it was not sent): nothing was created.</summary>
    Failed,
    /// <summary>A create with this name is pending a check, or another create of it is running: nothing was sent.</summary>
    Blocked,
}

public sealed record AdoCreateResult(AdoCreateOutcome Outcome, string Message)
{
    public AdoTestPlanInfo? Plan { get; init; }
    public AdoTestSuiteInfo? Suite { get; init; }
    public AdoPendingCreation? Pending { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AdoVerifyOutcome { Found, NotFound, StillUnknown }

public sealed record AdoVerifyResult(AdoVerifyOutcome Outcome, string Message)
{
    public AdoTestPlanInfo? Plan { get; init; }
    public AdoTestSuiteInfo? Suite { get; init; }
}

// ── Direct transfer of test cases ─────────────────────────────────────────────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AdoTransferAction
{
    /// <summary>«Opprett nytt»: a new Azure DevOps test case.</summary>
    Create,
    /// <summary>«Oppdater eksisterende»: update the linked test case (only when it was not changed in Azure DevOps since the last transfer).</summary>
    Update,
    /// <summary>«Hopp over».</summary>
    Skip,
}

/// <summary>What BirkNext knows about a case in the chosen organization + project.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AdoLinkState
{
    /// <summary>Never transferred to this organization + project.</summary>
    None,
    /// <summary>Linked to an Azure DevOps test case id.</summary>
    Linked,
    /// <summary>A create was sent and its answer was lost: check before creating it again.</summary>
    Uncertain,
}

public sealed record AdoCaseLink
{
    public AdoLinkState State { get; init; }
    public int? TestCaseId { get; init; }
    public string? WebUrl { get; init; }
    /// <summary>The case's text fingerprint (12 hex) at the last transfer; differs from the current one when the case changed in BirkNext.</summary>
    public string? CaseRevision { get; init; }
    public int? RemoteRevision { get; init; }
    public int? PlanId { get; init; }
    public int? SuiteId { get; init; }
    public DateTimeOffset? At { get; init; }
    public string? By { get; init; }
}

public sealed record AdoTransferRequest
{
    public string ProjectKey { get; init; } = "";
    public Guid DraftId { get; init; }
    /// <summary>The saved plan revision the user reviewed (a newer save is refused so nothing differs from the preview).</summary>
    public int PlanRevision { get; init; }
    /// <summary>Selected case ids; empty = every eligible case.</summary>
    public List<string> CaseIds { get; init; } = [];
    public bool IncludeNotApproved { get; init; }
    public string? Language { get; init; }
    /// <summary>Field values (Area Path, Assigned To, priority, preconditions, source references) from the project's export settings.</summary>
    public AdoExportProfile Profile { get; init; } = new();
    /// <summary>The destination the user sees (prefilled from the saved default; may be changed). Required.</summary>
    public AdoTestPlanDestination? Destination { get; init; }
    /// <summary>The user's choice per case id; a case without a choice gets the suggested one.</summary>
    public Dictionary<string, AdoTransferAction> Actions { get; init; } = [];
    /// <summary>Transfer only: the user confirmed the reviewed summary («Bekreft og send til Azure DevOps»).</summary>
    public bool Confirmed { get; init; }
    /// <summary>Hierarchical plans (2026-11-26): place each case in a Static Test Suite under the destination suite that mirrors its BirkNext suite path
    /// (existing suites with the same name under the same parent are reused; missing ones are created only in a confirmed transfer). False = every
    /// case in the destination suite, as before.</summary>
    public bool PreserveSuites { get; init; }
    /// <summary>Transfer only: «Jeg bekrefter at testleder har godkjent denne overføringen» — a DECLARATION by the person transferring, renewed for every
    /// transfer and recorded with that person. It is not a verified test lead approval and never marks the plan as approved.</summary>
    public bool LeadApprovalDeclared { get; init; }
}

/// <summary>The declaration text recorded with a direct transfer.</summary>
public static class AdoTransferDeclaration
{
    public const string Text = "Jeg bekrefter at testleder har godkjent denne overføringen.";
}

public sealed record AdoTransferCase
{
    public string CaseId { get; init; } = "";
    public string CaseKey { get; init; } = "";
    public string Title { get; init; } = "";
    public string? ScenarioTitle { get; init; }
    public int Steps { get; init; }
    public string? Priority { get; init; }
    public string Language { get; init; } = AdoExportCase.OriginalLanguage;
    /// <summary>Selected and nothing blocks it.</summary>
    public bool Ready { get; init; }
    public string? NotReadyReason { get; init; }
    public AdoCaseLink Link { get; init; } = new();
    /// <summary>Linked, and the case text changed in BirkNext since the last transfer.</summary>
    public bool ChangedSinceTransfer { get; init; }
    public string CurrentRevision { get; init; } = "";
    public List<AdoTransferAction> Allowed { get; init; } = [];
    public AdoTransferAction Suggested { get; init; }
    public string? Note { get; init; }
}

public sealed record AdoTransferPreview
{
    public AdoTestPlanDestination? Destination { get; init; }
    public AdoDestinationSource DestinationSource { get; init; }
    public string? DestinationProblem { get; init; }
    public string PlanName { get; init; } = "";
    public int PlanRevision { get; init; }
    public List<AdoTransferCase> Cases { get; init; } = [];
    public int ToCreate { get; init; }
    public int ToUpdate { get; init; }
    public int ToSkip { get; init; }
    public int NeedReview { get; init; }
    /// <summary>Warnings and notes from the same checks the file export uses (case text, language, priority).</summary>
    public List<AdoExportIssue> Issues { get; init; } = [];
    public bool CanTransfer { get; init; }
    /// <summary>With <see cref="AdoTransferRequest.PreserveSuites"/>: the suite tree as it will be under the destination suite — what exists (read
    /// from Azure DevOps, nothing written) and what a confirmed transfer would create.</summary>
    public List<AdoSuitePlanItem> SuitePlan { get; init; } = [];
}

/// <summary>One suite of the proposed remote tree: its BirkNext path and depth, the cases it receives, and whether it exists already.</summary>
public sealed record AdoSuitePlanItem(string LocalSuiteId, string Path, int Depth, int Cases, bool Exists, int? RemoteSuiteId);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AdoTransferOutcome
{
    Created,
    Updated,
    Skipped,
    /// <summary>Azure DevOps refused it or it was not sent: nothing changed for this case.</summary>
    Failed,
    /// <summary>Not changed: it must be looked at first (changed in Azure DevOps, deleted there, or an earlier uncertain create).</summary>
    NeedsReview,
    /// <summary>The answer was lost: the test case may exist. Check it before transferring again.</summary>
    Uncertain,
    /// <summary>The test case was created or updated, but adding it to the test suite failed (transfer again to add it; nothing is duplicated).</summary>
    NotInSuite,
}

public sealed record AdoTransferCaseResult(string CaseId, string Title, AdoTransferOutcome Outcome, string Message)
{
    public int? TestCaseId { get; init; }
    public string? WebUrl { get; init; }
}

public sealed record AdoTransferResult
{
    public AdoTestPlanDestination Destination { get; init; } = new();
    public string? SuiteUrl { get; init; }
    public List<AdoTransferCaseResult> Cases { get; init; } = [];
    /// <summary>With suite placement: each suite's outcome (created, reused or failed); a failed suite leaves its cases «Ikke i testgruppen».</summary>
    public List<AdoSuiteResult> Suites { get; init; } = [];
    public int Succeeded => Cases.Count(c => c.Outcome is AdoTransferOutcome.Created or AdoTransferOutcome.Updated);
    public int Problems => Cases.Count(c => c.Outcome is AdoTransferOutcome.Failed or AdoTransferOutcome.NeedsReview or AdoTransferOutcome.Uncertain or AdoTransferOutcome.NotInSuite);
}

/// <summary>Check an uncertain create of one case: adopt the test case that was found, or clear it so it can be created.</summary>
public sealed record AdoReconcileRequest(string ProjectKey, Guid DraftId, string CaseId, AdoTestPlanDestination Destination, int? AdoptTestCaseId = null);

public sealed record AdoReconcileResult(AdoVerifyOutcome Outcome, string Message, AdoCaseLink Link, List<int> Candidates);

// ── Results fetched back from Azure DevOps (phase 2, 2026-11-25): read-only, kept apart from BirkNext runs ───────────────────────────────

/// <summary>One Azure DevOps test result for a BirkNext case («Gjennomført i Azure DevOps»). Mapped only through a verified link (BirkNext's own
/// transfer or an id the tester confirmed); <see cref="Current"/> only when the case's text at its last export/transfer is the text the case has now.</summary>
public sealed record AdoCaseResult
{
    public string CaseId { get; init; } = "";
    public string CaseKey { get; init; } = "";
    public int TestCaseId { get; init; }
    public int PlanId { get; init; }
    public int SuiteId { get; init; }
    public int PointId { get; init; }
    public int RunId { get; init; }
    public int ResultId { get; init; }
    /// <summary>Azure DevOps' own outcome word (Passed, Failed, Blocked, NotApplicable, Paused, …) — never reinterpreted.</summary>
    public string Outcome { get; init; } = "";
    public string? Tester { get; init; }
    public string? Configuration { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public DateTimeOffset FetchedAt { get; init; }
    public string? WebUrl { get; init; }
    /// <summary>The case text revision (fingerprint) at its last export or transfer; null when unknown.</summary>
    public string? ExportedRevision { get; init; }
    public bool Current { get; init; }
}

public sealed record AdoResultsFetchRequest(string ProjectKey, Guid DraftId, AdoTestPlanDestination? Destination = null);

public sealed record AdoResultsFetchResult
{
    public int Read { get; init; }
    public int New { get; init; }
    public int AlreadyKnown { get; init; }
    public int NotLinked { get; init; }
    public int NeverRun { get; init; }
    public List<string> Problems { get; init; } = [];
    public List<AdoCaseResult> Results { get; init; } = [];
}

public static class AdoResultOutcome
{
    /// <summary>Plain Norwegian for Azure DevOps' outcome; anything else is shown as Azure DevOps recorded it (never «Bestått»).</summary>
    public static string Label(string outcome) => outcome switch
    {
        "Passed" => "Bestått", "Failed" => "Feilet", "Blocked" => "Blokkert", "NotExecuted" => "Ikke kjørt", "NotApplicable" => "Ikke relevant",
        "Paused" => "Satt på pause", "InProgress" => "Pågår", _ => outcome,
    };
}

/// <summary>The outcome of one suite of a transfer with suite placement.</summary>
public sealed record AdoSuiteResult(string LocalSuiteId, string Path, AdoCreateOutcome Outcome, int? RemoteSuiteId, string Message);
