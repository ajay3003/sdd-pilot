namespace BirkNext.Api.Models;

/// <summary>A source-derived proposal. Kept separate from human guide revisions so importing source cannot edit a draft or publication.</summary>
public sealed class SystemGuideCandidateRecord
{
    public Guid Id { get; set; }
    public string GuideKey { get; set; } = "";
    public Guid SourceSnapshotId { get; set; }
    public string SourceFingerprint { get; set; } = "";
    public string InputFingerprint { get; set; } = "";
    public string ContentFingerprint { get; set; } = "";
    public string ContentJson { get; set; } = "";
    public string ChangesJson { get; set; } = "[]";
    public string EvidenceJson { get; set; } = "[]";
    public string Status { get; set; } = "NeedsReview";
    public DateTimeOffset CreatedAt { get; set; }
    public string GeneratorVersion { get; set; } = "";
}
