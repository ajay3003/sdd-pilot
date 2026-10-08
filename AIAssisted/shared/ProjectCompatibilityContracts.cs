namespace BirkNext.ProjectImport;

public enum ProjectCompatibilityStatus { Pass, Partial, Unsupported, Fail, NotRun }

public sealed record ProjectCompatibilityScenario(string Name, ProjectCompatibilityStatus Status, string ExpectedBehavior,
    string ObservedBehavior, string? Notes = null, long DurationMilliseconds = 0);

public sealed record ProjectCompatibilityRun(Guid RunId, DateTimeOffset StartedAt, DateTimeOffset CompletedAt,
    ProjectCompatibilityStatus OverallStatus, IReadOnlyList<ProjectCompatibilityScenario> Scenarios,
    bool RealProjectFixtureUsed = false);
