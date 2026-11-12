namespace BirkNext.Api.Models;

/// <summary>
/// A Test Plan Suggestion draft (table <c>test_plan_drafts</c>): the mutable working proposal of one system guide. <see cref="Revision"/> is an
/// optimistic concurrency token: a save names the revision it started from, and a save from an older revision is refused (nothing is lost).
/// <see cref="ContentJson"/> is a <see cref="BirkNext.TestPlanning.TestPlanDraft"/>; generated scenario content is rebuilt by the server.
/// </summary>
public sealed class TestPlanDraftRecord
{
    public Guid Id { get; set; }
    public string GuideKey { get; set; } = "";
    public string PlanName { get; set; } = "";
    public string? DeliveryLabel { get; set; }
    public int Revision { get; set; }
    public string ContentJson { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string UpdatedBy { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// An immutable Test Plan Suggestion snapshot (table <c>test_plan_snapshots</c>): the complete proposal at one moment, with its content
/// fingerprint. Written once; the database refuses any UPDATE (trigger). A draft cannot hold two snapshots with the same content, and sequence
/// numbers are unique per draft, so concurrent saves cannot fork or overwrite history. DELETE stays possible only for Local Data Reset.
/// </summary>
public sealed class TestPlanSnapshotRecord
{
    public Guid Id { get; set; }
    public Guid DraftId { get; set; }
    public string GuideKey { get; set; } = "";
    public int Sequence { get; set; }
    public string PlanName { get; set; } = "";
    public string? DeliveryLabel { get; set; }
    public int DraftRevision { get; set; }
    public int GuideRevision { get; set; }
    public string RuleVersion { get; set; } = "";
    public int CaseCount { get; set; }
    public string ContentFingerprint { get; set; } = "";
    public string ContentJson { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string? Note { get; set; }
}
