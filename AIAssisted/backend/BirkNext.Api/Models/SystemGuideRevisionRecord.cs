namespace BirkNext.Api.Models;

/// <summary>
/// One revision of a System Understanding guide (table <c>system_guide_revisions</c>). Content is a typed document
/// (<see cref="BirkNext.SystemUnderstanding.SystemGuideContent"/>, serialized) with its SHA-256; the source basis lists the snapshot identities
/// it was written or reviewed against (no source content). Beyond Draft the content is immutable (database trigger); the database also allows
/// at most one open (Draft/InReview) and one Published revision per guide, so concurrent publishing cannot produce two current revisions.
/// </summary>
public sealed class SystemGuideRevisionRecord
{
    public Guid Id { get; set; }
    public string GuideKey { get; set; } = "";
    public int Revision { get; set; }
    public string State { get; set; } = "Draft";
    public string ContentFingerprint { get; set; } = "";
    public string ContentJson { get; set; } = "";
    public string SourceBasisJson { get; set; } = "[]";
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string? ChangeSummary { get; set; }
    public string? SubmittedBy { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public int? SupersededByRevision { get; set; }
}
