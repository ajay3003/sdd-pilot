namespace BirkNext.Api.Services.WasmPerformance;

public enum ReadinessState
{
    Ready            = 0,
    MostlyReady      = 1,
    NeedsImprovement = 2,
    HighRisk         = 3,
    NotAssessed      = 4
}

public sealed class PerformanceCategorySummary
{
    public required string        CategoryName  { get; init; }
    public PerformanceCategory    Category      { get; init; }
    public int                    Score         { get; init; }
    public ReadinessState         State         { get; init; }
    public int                    FindingsCount { get; init; }
    public int                    CriticalCount { get; init; }
    public int                    HighCount     { get; init; }
    public int                    MediumCount   { get; init; }
    public int                    LowCount      { get; init; }
    public bool                   WasAssessed   { get; init; }
}

public sealed class PerformanceReadinessHealth
{
    public int OverallScore      { get; init; }
    public int StartupScore      { get; init; }
    public int ApiScore          { get; init; }
    public int GraphQlScore      { get; init; }
    public int CachingScore      { get; init; }
    public int CompressionScore  { get; init; }
    public int ArchitectureScore { get; init; }
    public int CriticalFindings  { get; init; }
    public int HighFindings      { get; init; }
    public int MediumFindings    { get; init; }
    public int LowFindings       { get; init; }
}

public sealed class PerformanceReadinessReport
{
    public int                                  OverallScore        { get; init; }
    public ReadinessState                       OverallState        { get; init; }
    public List<PerformanceCategorySummary>     Categories          { get; init; } = [];
    public List<PerformanceFinding>             TopRisks            { get; init; } = [];
    public List<PerformanceRecommendation>      TopRecommendations  { get; init; } = [];
    public PerformanceReadinessHealth           Health              { get; init; } = new();
    public bool                                 HasData             { get; init; }
}
