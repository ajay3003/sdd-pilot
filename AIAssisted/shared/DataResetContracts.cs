using System.Text.Json.Serialization;

namespace BirkNext.Maintenance;

/// <summary>
/// What one Maintenance reset removes. The scope is chosen explicitly (never inferred from wording) and determines the confirmation:
/// <list type="bullet">
/// <item><see cref="FullIncludingProtected"/> — every resettable local application data domain, including protected and canonical Test Data.</item>
/// <item><see cref="FullPreserveProtected"/> — the same, but protected Test Data defaults and canonical fixtures survive with their current values.</item>
/// <item><see cref="DeliveryManagement"/> — only Delivery Management (Delivery Overview / Delivery Reports) data.</item>
/// </list>
/// Never the database schema, installation or provider settings, the Sample Project catalog, files outside BirkNext, or external systems.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DataResetMode { FullIncludingProtected, FullPreserveProtected, DeliveryManagement }

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
}

public sealed record DataResetRequest(DataResetMode Mode, string? Confirmation);

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
    public int DeletedRows { get; init; }
    public List<DataResetDomainResult> Domains { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    /// <summary>Product-defined defaults that exist again after the reset (they are installation content, not preserved user data).</summary>
    public List<string> SystemDefaultsRecreated { get; init; } = [];
}
