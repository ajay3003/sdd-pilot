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

    private ExtractionPipelineResult(
        PipelineStatus status,
        IReadOnlyList<ExtractionCandidate> candidates,
        int inputLengthChars,
        int inputLineCount,
        long durationMs,
        int requirementCount,
        int testCount,
        int needsClarificationCount)
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
    }

    public static ExtractionPipelineResult Success(
        IReadOnlyList<ExtractionCandidate> candidates,
        int inputLengthChars,
        int inputLineCount,
        long durationMs,
        int requirementCount,
        int testCount,
        int needsClarificationCount)
        => new(
            PipelineStatus.Success,
            candidates,
            inputLengthChars,
            inputLineCount,
            durationMs,
            requirementCount,
            testCount,
            needsClarificationCount);

    public static ExtractionPipelineResult NonSuccess(
        PipelineStatus status,
        int inputLengthChars,
        int inputLineCount,
        long durationMs)
    {
        if (status == PipelineStatus.Success)
            throw new ArgumentException("Use Success() factory for PipelineStatus.Success.");

        return new(status, [], inputLengthChars, inputLineCount, durationMs, 0, 0, 0);
    }
}
