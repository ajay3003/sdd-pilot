using BirkNext.Web.GraphQL;

namespace BirkNext.Web.Models;

public sealed class ExtractionPipelineResult
{
    public PipelineStatus Status { get; }
    public IReadOnlyList<ExtractionCandidate> Candidates { get; }
    public int InputLengthChars { get; }
    public int InputLineCount { get; }
    public long DurationMs { get; }
    public int RequirementCount { get; }
    public int TestCount { get; }
    public int NeedsClarificationCount { get; }
    public ExtractionProfile Profile { get; }

    private ExtractionPipelineResult(
        PipelineStatus status,
        IReadOnlyList<ExtractionCandidate> candidates,
        int inputLengthChars,
        int inputLineCount,
        long durationMs,
        int requirementCount,
        int testCount,
        int needsClarificationCount,
        ExtractionProfile profile)
    {
        if (requirementCount + testCount + needsClarificationCount != candidates.Count)
            throw new ArgumentException(
                "requirementCount + testCount + needsClarificationCount must equal candidates.Count.");

        Status = status;
        Candidates = candidates;
        InputLengthChars = inputLengthChars;
        InputLineCount = inputLineCount;
        DurationMs = durationMs;
        RequirementCount = requirementCount;
        TestCount = testCount;
        NeedsClarificationCount = needsClarificationCount;
        Profile = profile;
    }

    public static ExtractionPipelineResult Success(
        IReadOnlyList<ExtractionCandidate> candidates,
        int inputLengthChars,
        int inputLineCount,
        long durationMs,
        int requirementCount,
        int testCount,
        int needsClarificationCount,
        ExtractionProfile profile = ExtractionProfile.Default)
        => new(
            PipelineStatus.Success,
            candidates,
            inputLengthChars,
            inputLineCount,
            durationMs,
            requirementCount,
            testCount,
            needsClarificationCount,
            profile);

    public static ExtractionPipelineResult Restore(ExtractionSessionSnapshot snapshot)
    {
        var candidates = snapshot.Candidates.Select(c => new ExtractionCandidate
        {
            CandidateId           = c.CandidateId,
            Title                 = c.Title,
            Classification        = c.Classification,
            ClassificationSignal  = c.ClassificationSignal,
            ContextHeading        = c.ContextHeading,
            SourceBlockType       = c.SourceBlockType,
            Confidence            = c.Confidence,
            IsSelected            = c.IsSelected,
            ReviewStatus          = c.ReviewStatus,
            SaveState             = c.SaveState,
            SaveError             = c.SaveError,
            SavedScenarioId       = c.SavedScenarioId,
        }).ToList();

        return Success(
            candidates,
            snapshot.InputLengthChars,
            snapshot.InputLineCount,
            snapshot.DurationMs,
            snapshot.Candidates.Count(c => c.Classification == ScenarioKind.Requirement),
            snapshot.Candidates.Count(c => c.Classification == ScenarioKind.Test),
            snapshot.Candidates.Count(c => c.Classification == ScenarioKind.NeedsClarification),
            snapshot.Profile);
    }

    public static ExtractionPipelineResult NonSuccess(
        PipelineStatus status,
        int inputLengthChars,
        int inputLineCount,
        long durationMs,
        ExtractionProfile profile = ExtractionProfile.Default)
    {
        if (status == PipelineStatus.Success)
            throw new ArgumentException("Use Success() factory for PipelineStatus.Success.");

        return new(status, [], inputLengthChars, inputLineCount, durationMs, 0, 0, 0, profile);
    }
}
