namespace BirkNext.Web.Services;

public interface IExtractionConfiguration
{
    int MaxInputLengthChars { get; }
    int MinCandidateLengthChars { get; }
    int MaxLineLengthForPatternMatching { get; }
}

public sealed class ExtractionConfiguration : IExtractionConfiguration
{
    public int MaxInputLengthChars { get; init; } = 50_000;
    public int MinCandidateLengthChars { get; init; } = 3;
    public int MaxLineLengthForPatternMatching { get; init; } = 2_000;
}
