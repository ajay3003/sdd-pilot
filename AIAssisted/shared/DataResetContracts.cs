using System.Text.Json.Serialization;

namespace BirkNext.Maintenance;

/// <summary>
/// What one Maintenance reset removes. The scope is chosen explicitly (never inferred from wording) and determines the confirmation:
/// <list type="bullet">
/// <item><see cref="FullIncludingProtected"/> — every resettable local application data domain, including protected and canonical Test Data.</item>
/// <item><see cref="FullPreserveProtected"/> — the same, but protected Test Data defaults and canonical fixtures survive with their current values.</item>
/// <item><see cref="DeliveryManagement"/> — only Delivery Management (Delivery Overview / Delivery Reports) data.</item>
/// <item><see cref="ProjectDataKeepSettings"/> — like <see cref="FullPreserveProtected"/>, but system settings survive too: Target Environments
/// (without their project binding), integrations, Altinn settings, Azure DevOps export preferences, retention, Setup Library, Parquet rule sets.</item>
/// </list>
/// Never the database schema, installation or provider settings, the Sample Project catalog, files outside BirkNext, or external systems.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DataResetMode { FullIncludingProtected, FullPreserveProtected, DeliveryManagement, ProjectDataKeepSettings }

/// <summary>The reviewable impact of a reset scope, shown before anything is deleted.</summary>
public sealed record DataResetPlan
{
    public DataResetMode Mode { get; init; }
    public string Title { get; init; } = "";
    public string Summary { get; init; } = "";
    public List<string> Removed { get; init; } = [];
    public List<string> Preserved { get; init; } = [];
    public List<string> Notes { get; init; } = [];
    /// <summary>Cheap counts of what will be removed (label → count); absent when not cheaply known.</summary>
    public Dictionary<string, int> Counts { get; init; } = [];
    /// <summary>The exact text the user must type, or null when an explicit confirmation button is enough.</summary>
    public string? ConfirmationPhrase { get; init; }
    public bool Allowed { get; init; } = true;
    public string? NotAllowedReason { get; init; }
    /// <summary>Manual test plans that exist now (active and archived). Every scope keeps them unless the request explicitly removes them.</summary>
    public int TestPlanCount { get; init; }
    /// <summary>True only for <see cref="DataResetMode.FullIncludingProtected"/>: the one scope that may also remove manual test plans, and only
    /// when the request opts in with <see cref="DataResetRequest.RemoveTestPlans"/> and the count the tester reviewed.</summary>
    public bool CanRemoveTestPlans { get; init; }
}

/// <param name="RemoveTestPlans">Also remove manual test plans (with revisions, snapshots and manual runs). Off by default; allowed only with
/// <see cref="DataResetMode.FullIncludingProtected"/>. A backup file is written first, and the reset is refused when it cannot be.</param>
/// <param name="ExpectedTestPlans">The <see cref="DataResetPlan.TestPlanCount"/> the tester reviewed: the removal is refused when plans were
/// added or removed since, so a stale dialog can never delete plans it did not show.</param>
public sealed record DataResetRequest(DataResetMode Mode, string? Confirmation, bool RemoveTestPlans = false, int? ExpectedTestPlans = null);

/// <summary>One domain's outcome: removed, failed (with a safe reason), or skipped.</summary>
public sealed record DataResetDomainResult(string Domain, string Status, string? Detail = null);

/// <summary>"Completed", "PartiallyCompleted", "Blocked", "Refused" or "Failed" — never Completed unless every domain was reset.</summary>
public sealed record DataResetResult
{
    public DataResetMode Mode { get; init; }
    public string Status { get; init; } = "Failed";
    public bool Success => Status == "Completed";
    public string Message { get; init; } = "";
    public DateTimeOffset? ResetAt { get; init; }
    /// <summary>The backend reset epoch after a full reset (0 for Delivery Management only, which never invalidates other tabs' project state).</summary>
    public int ResetEpoch { get; init; }
    public int DeletedRows { get; init; }
    public List<DataResetDomainResult> Domains { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    /// <summary>"Reset everything": built-in protected Test Data defaults removed (restorable in Test Data → Restore built-in defaults).</summary>
    public List<string> ProtectedDefaultsRemoved { get; init; } = [];
    /// <summary>Manual test plans kept by this reset (0 when they were removed or none existed).</summary>
    public int TestPlansPreserved { get; init; }
    /// <summary>Manual test plans removed (only with an explicit <see cref="DataResetRequest.RemoveTestPlans"/>).</summary>
    public int TestPlansRemoved { get; init; }
    /// <summary>The backup written before test plans were removed (file name only; it lives in the backend's App_Data/reset-backups).</summary>
    public string? TestPlanBackup { get; init; }
}
