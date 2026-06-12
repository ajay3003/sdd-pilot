using BirkNext.Web.GraphQL;

namespace BirkNext.Web.Models;

public sealed class ExtractionSessionSnapshot
{
    public string SessionId { get; init; } = Guid.NewGuid().ToString();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public ExtractionProfile Profile { get; init; }
    public PipelineStatus PipelineStatus { get; init; }
    public int InputLengthChars { get; init; }
    public int InputLineCount { get; init; }
    public long DurationMs { get; init; }
    public List<CandidateSnapshot> Candidates { get; init; } = [];
    public List<LinkSnapshot> Links { get; init; } = [];
    public List<Guid> SelectedIds { get; init; } = [];
    public ScenarioKind? ActiveFilter { get; init; }
    public CandidateReviewStatus? ActiveReviewFilter { get; init; }
    public LinkFilter? ActiveLinkFilter { get; init; }
    public string SearchTerm { get; init; } = string.Empty;
    public Dictionary<string, bool> SectionExpanded { get; init; } = [];
    public Dictionary<string, bool> ReqSubsectionExpanded { get; init; } = [];
    public Dictionary<string, bool> TestSubsectionExpanded { get; init; } = [];
    public Dictionary<string, bool> ClrSubsectionExpanded { get; init; } = [];
    public Dictionary<string, bool> CapabilityGroupExpanded { get; init; } = [];
    public Dictionary<string, bool> ArchitectureGroupExpanded { get; init; } = [];
    public ExtractionViewMode ActiveViewMode { get; init; } = ExtractionViewMode.Extraction;
    public string SpecMarkdown { get; init; } = string.Empty;
}

public sealed record CandidateSnapshot(
    Guid CandidateId,
    string Title,
    ScenarioKind Classification,
    ClassificationSignal ClassificationSignal,
    string? ContextHeading,
    BlockType SourceBlockType,
    float? Confidence,
    bool IsSelected,
    CandidateReviewStatus ReviewStatus,
    CandidateSaveState SaveState,
    string? SaveError,
    string? SavedScenarioId);

public sealed record LinkSnapshot(Guid SourceId, Guid TargetId, CandidateLinkType LinkType);
