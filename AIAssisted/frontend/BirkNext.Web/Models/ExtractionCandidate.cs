using BirkNext.Web.GraphQL;

namespace BirkNext.Web.Models;

public sealed class ExtractionCandidate
{
    public Guid CandidateId { get; } = Guid.NewGuid();
    public required string Title { get; init; }
    public required ScenarioKind Classification { get; init; }
    public required ClassificationSignal ClassificationSignal { get; init; }
    public string? ContextHeading { get; init; }
    public required BlockType SourceBlockType { get; init; }

    public bool IsSelected { get; set; } = false;
    public CandidateSaveState SaveState { get; set; } = CandidateSaveState.Pending;
    public string? SaveError { get; set; }
    public string? SavedScenarioId { get; set; }

    // Reserved for future AI-assisted classification; always null in v1.
    public float? Confidence { get; init; }
}
