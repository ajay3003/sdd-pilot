namespace BirkNext.Web.Services;

public interface IExtractionConfiguration
{
    int MaxInputLengthChars { get; }
    int MinCandidateLengthChars { get; }
    int MaxLineLengthForPatternMatching { get; }
}

public sealed class ExtractionConfiguration : IExtractionConfiguration
{
    /// <summary>The largest specification the deterministic extraction reads. It is linear in the input (about 30 ms for a 92 000-character
    /// specification on .NET; real M2LB service specifications reach 98 000 characters), so the bound only guards against non-documents: it
    /// matches the 1 MB limit of a specification file import.</summary>
    public const int DefaultMaxInputLengthChars = 1_000_000;

    public int MaxInputLengthChars { get; init; } = DefaultMaxInputLengthChars;
    public int MinCandidateLengthChars { get; init; } = 3;
    public int MaxLineLengthForPatternMatching { get; init; } = 2_000;
}
