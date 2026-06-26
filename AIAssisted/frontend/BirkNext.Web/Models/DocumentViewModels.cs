namespace BirkNext.Web.Models;

public enum DocumentArtifactType
{
    Requirement,
    UserStory,
    AcceptanceTest,
    SuccessCriterion,
    Clarification,
    Decision,
    Entity,
    ApiSurfaceItem,
    EdgeCase,
    Assumption,
}

public sealed class DocumentArtifact
{
    public required DocumentArtifactType ArtifactType { get; init; }
    public required string Title { get; init; }
    public string Excerpt { get; set; } = string.Empty;
    public string? FullContent { get; set; }
    public string? SpecItemId { get; init; }
    public string? QuestionText { get; init; }
    public string? AnswerText { get; init; }
    public ExtractionCandidate? LinkedCandidate { get; set; }
}

public sealed class DocumentSection
{
    public required string Title { get; init; }
    public int HeadingLevel { get; init; }
    public SectionSemantics Semantics { get; init; } = SectionSemantics.Generic;
    public bool IsDecisionSection { get; init; }
    public List<DocumentArtifact> Artifacts { get; } = [];
    public List<DocumentSection> SubSections { get; } = [];

    public bool HasContent => Artifacts.Count > 0 || SubSections.Any(s => s.HasContent);
}

public sealed class DocumentViewModel
{
    public List<DocumentSection> Sections { get; init; } = [];
    public List<DocumentArtifact> UnmatchedArtifacts { get; init; } = [];
    public bool HasSpecTree { get; init; }

    public int RequirementCount { get; init; }
    public int UserStoryCount { get; init; }
    public int TestCount { get; init; }
    public int SuccessCriteriaCount { get; init; }
    public int ClarificationCount { get; init; }
    public int DecisionCount { get; init; }
    public int EntityCount { get; init; }
    public int ApiSurfaceItemCount { get; init; }

    public int TotalArtifacts => RequirementCount + UserStoryCount + TestCount
                               + SuccessCriteriaCount + ClarificationCount
                               + DecisionCount + EntityCount + ApiSurfaceItemCount;
}
