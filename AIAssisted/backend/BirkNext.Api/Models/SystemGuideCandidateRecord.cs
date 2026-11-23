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
    /// <summary>Reviewer decisions per proposed change (<see cref="BirkNext.SystemUnderstanding.GuideChangeDecision"/>); null = none yet.</summary>
    public string? DecisionsJson { get; set; }
    /// <summary>Supporting (non-scenario) evidence the generator read, in plain words; null for older candidates.</summary>
    public string? SupportingJson { get; set; }
    /// <summary>The newer candidate of the same guide that made this one stale (a newer source version).</summary>
    public Guid? SupersededBy { get; set; }
    public string? SourceLabel { get; set; }
}

/// <summary>
/// A durable System Understanding generation job (table <c>system_guide_generation_jobs</c>): one per guide, source snapshot and generator
/// version. Import records it (fast, after the snapshot is saved); a background worker runs it; a failure keeps its diagnostics and can be
/// retried. Survives restarts. Status: Pending, Running, Succeeded, Failed, Stale (a newer source version of the same guide arrived first).
/// </summary>
public sealed class SystemGuideGenerationJobRecord
{
    public Guid Id { get; set; }
    public string GuideKey { get; set; } = "";
    public string? ApplicationKey { get; set; }
    public Guid SnapshotId { get; set; }
    public string? SnapshotLabel { get; set; }
    public string Status { get; set; } = "Pending";
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public string GeneratorVersion { get; set; } = "";
    public Guid? CandidateId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
