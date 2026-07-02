using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Implementation Review analysis over the canonical ReviewContext.
/// This service must not parse markdown or rebuild spec/task relationships.
/// </summary>
public sealed class TaskSpecAlignmentService
{
    private static readonly string[] InfrastructureTerms =
    [
        "csproj", "sln", "solution", "project setup", "project skeleton",
        "package reference", "nuget", "npm", "yarn", "appsettings", "configuration",
        "program.cs", "di registration", "service extension", "migration",
        "dbcontext", "healthcheck", "health check", "logging", "telemetry",
        "opentelemetry", "key vault", "managed identity", "pipeline", "dockerfile",
        "test project", "test fixture", "testcontainers", "fake", "mock",
        "options class", "placeholder config", "gitignore"
    ];

    private static readonly string[] GeneratedCodeTerms =
    [
        "generated", "scaffold", "stub", "boilerplate", "dto", "record",
        "enum", "options", "model", "contract", "schema"
    ];

    private static readonly string[] BehavioralTerms =
    [
        "endpoint", "api", "route", "process", "consume", "publish", "deliver",
        "map", "translate", "reject", "authorize", "validate", "business rule",
        "cdc", "event", "fault queue", "checkpoint", "full load", "retry",
        "health", "alert", "security", "kode 6", "kode 7", "permission"
    ];

    private static readonly (AffectedArea Area, string[] Terms)[] AreaTerms =
    [
        (AffectedArea.Security, ["security", "kode 6", "kode 7", "sikkerhetsnivaa", "managed identity", "secret"]),
        (AffectedArea.Authorization, ["authorize", "authorization", "permission", "access", "bearer", "token"]),
        (AffectedArea.Ingestion, ["cdc", "ingest", "full load", "batch", "event hubs", "eventhub"]),
        (AffectedArea.DomainEvents, ["event", "publish", "consumer", "processor", "service bus"]),
        (AffectedArea.Audit, ["audit", "revisjon"]),
        (AffectedArea.HealthMonitoring, ["health", "metric", "telemetry", "opentelemetry", "monitor"]),
        (AffectedArea.Infrastructure, ["csproj", "sln", "appsettings", "configuration", "program.cs", "di", "migration", "dbcontext", "key vault"]),
        (AffectedArea.Validation, ["validation", "validate", "validator"]),
        (AffectedArea.Testing, ["test", "xunit", "nsubstitute", "testcontainers", "fixture", "mock", "fake"]),
        (AffectedArea.ExceptionHandling, ["exception", "error", "failure", "fault"]),
    ];

    public AlignmentReport Analyse(ReviewContext reviewContext)
    {
        ArgumentNullException.ThrowIfNull(reviewContext);

        var findings = reviewContext.GetTasks()
            .Select(task => ClassifyTask(task, reviewContext))
            .ToList();

        return new AlignmentReport
        {
            TotalTasks = findings.Count,
            LinkedTasks = findings.Count(f => f.Status == AlignmentStatus.Linked),
            TechnicalOnlyTasks = findings.Count(f => f.Status == AlignmentStatus.TechnicalOnly),
            NeedsReviewTasks = findings.Count(f => f.Status == AlignmentStatus.NeedsReview),
            PossibleDeviations = findings.Count(f => f.Status == AlignmentStatus.PossibleDeviation),
            HighImpactTasks = findings.Count(f => f.ImpactLevel == ImpactLevel.High),
            MediumImpactTasks = findings.Count(f => f.ImpactLevel == ImpactLevel.Medium),
            LowImpactTasks = findings.Count(f => f.ImpactLevel == ImpactLevel.Low),
            UnknownImpactTasks = findings.Count(f => f.ImpactLevel == ImpactLevel.Unknown),
            RegressionCandidates = findings.Count(f => f.IsRegressionCandidate),
            Findings = findings,
        };
    }

    private static TaskFinding ClassifyTask(TaskItem task, ReviewContext context)
    {
        var matches = BuildSpecMatches(task, context);
        var areas = DetectAreas(task);

        if (matches.Count > 0)
            return BuildLinkedFinding(task, matches, areas);

        if (IsTechnicalOnly(task))
            return BuildTechnicalFinding(task, areas);

        if (IntroducesBehavior(task))
            return BuildDeviationFinding(task, areas);

        return BuildNeedsReviewFinding(task, areas);
    }

    private static List<SpecMatch> BuildSpecMatches(TaskItem task, ReviewContext context)
    {
        var matches = new List<SpecMatch>();

        foreach (var requirementId in task.LinkedFRIds)
        {
            var requirement = context.GetRequirement(requirementId);
            if (requirement is null)
                continue;

            if (context.GetLinkedTasks(requirement.Id).Contains(task.Id, StringComparer.OrdinalIgnoreCase)
                || requirement.LinkedTasks.Contains(task.Id, StringComparer.OrdinalIgnoreCase)
                || task.LinkedFRIds.Contains(requirement.Id, StringComparer.OrdinalIgnoreCase))
            {
                matches.Add(new SpecMatch
                {
                    ItemId = requirement.Id,
                    Title = Shorten(requirement.Text),
                    MatchType = SpecMatchType.Requirement,
                });
            }
        }

        foreach (var criterionId in task.LinkedSCIds)
        {
            var criterion = context.GetSuccessCriteria()
                .FirstOrDefault(sc => sc.Id.Equals(criterionId, StringComparison.OrdinalIgnoreCase));
            if (criterion is null)
                continue;

            if (context.GetSpecLinks(criterion.Id).Contains(task.Id, StringComparer.OrdinalIgnoreCase)
                || criterion.LinkedTasks.Contains(task.Id, StringComparer.OrdinalIgnoreCase)
                || task.LinkedSCIds.Contains(criterion.Id, StringComparer.OrdinalIgnoreCase))
            {
                matches.Add(new SpecMatch
                {
                    ItemId = criterion.Id,
                    Title = Shorten(criterion.Text),
                    MatchType = SpecMatchType.SuccessCriterion,
                });
            }
        }

        var userStory = FindUserStory(task.UserStoryId, context);
        if (userStory is not null)
        {
            matches.Add(new SpecMatch
            {
                ItemId = userStory.Id,
                Title = Shorten(userStory.Title),
                MatchType = SpecMatchType.UserStory,
            });
        }

        return matches
            .GroupBy(match => match.ItemId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static TaskFinding BuildLinkedFinding(TaskItem task, List<SpecMatch> matches, List<AffectedArea> areas)
    {
        var hasRequirementOrCriterion = matches.Any(m => m.MatchType is SpecMatchType.Requirement or SpecMatchType.SuccessCriterion);
        var confidence = hasRequirementOrCriterion ? 0.95 : 0.85;
        var risk = areas.Any(a => AreaImpact(a) == ImpactLevel.High) ? AlignmentRisk.Medium : AlignmentRisk.Low;

        return new TaskFinding
        {
            TaskId = task.Id,
            Title = task.Title,
            Status = AlignmentStatus.Linked,
            Risk = risk,
            Reason = $"Task is linked through ReviewContext to {string.Join(", ", matches.Select(m => m.ItemId))}.",
            RecommendedAction = "No action required — task is covered by the canonical ReviewContext relationships.",
            Confidence = confidence,
            Matches = matches,
            AffectedAreas = areas,
            RecommendedTests = BuildTests(areas, 6),
            ImpactLevel = ComputeImpactLevel(AlignmentStatus.Linked, areas),
            MatchReason = "ReviewContext semantic relationship",
            RiskReason = GenerateRiskReason(AlignmentStatus.Linked, risk, areas),
            IsRegressionCandidate = risk == AlignmentRisk.Medium,
        };
    }

    private static TaskFinding BuildTechnicalFinding(TaskItem task, List<AffectedArea> areas)
    {
        if (!areas.Contains(AffectedArea.Infrastructure))
            areas.Add(AffectedArea.Infrastructure);

        return new TaskFinding
        {
            TaskId = task.Id,
            Title = task.Title,
            Status = AlignmentStatus.TechnicalOnly,
            Risk = AlignmentRisk.Low,
            Reason = "Task is infrastructure, setup, generated-code, or test scaffolding with no ReviewContext spec relationship.",
            RecommendedAction = "No spec update required unless this task introduces user-visible behavior.",
            Confidence = 0.80,
            AffectedAreas = areas,
            RecommendedTests = BuildTests(areas, 5),
            ImpactLevel = ImpactLevel.Low,
            MatchReason = "Technical classification helper",
            IsRegressionCandidate = false,
        };
    }

    private static TaskFinding BuildDeviationFinding(TaskItem task, List<AffectedArea> areas)
    {
        return new TaskFinding
        {
            TaskId = task.Id,
            Title = task.Title,
            Status = AlignmentStatus.PossibleDeviation,
            Risk = AlignmentRisk.High,
            Reason = "Task appears to introduce behavior, but ReviewContext has no linked requirement, success criterion, or user story.",
            RecommendedAction = "Link this task to existing specification coverage or update spec.md to document the behavior.",
            Confidence = 0.75,
            AffectedAreas = areas,
            RecommendedTests = BuildTests(areas, 6),
            ImpactLevel = ImpactLevel.High,
            MatchReason = "No ReviewContext relationship",
            RiskReason = GenerateRiskReason(AlignmentStatus.PossibleDeviation, AlignmentRisk.High, areas),
            IsRegressionCandidate = true,
        };
    }

    private static TaskFinding BuildNeedsReviewFinding(TaskItem task, List<AffectedArea> areas)
    {
        return new TaskFinding
        {
            TaskId = task.Id,
            Title = task.Title,
            Status = AlignmentStatus.NeedsReview,
            Risk = AlignmentRisk.Medium,
            Reason = "ReviewContext has no semantic spec relationship for this task, and the remaining helper signals are inconclusive.",
            RecommendedAction = "Review the task against the specification and either link it, mark it technical-only, or add missing spec coverage.",
            Confidence = 0.45,
            AffectedAreas = areas,
            RecommendedTests = BuildTests(areas, 6),
            ImpactLevel = ComputeImpactLevel(AlignmentStatus.NeedsReview, areas),
            MatchReason = "No canonical relationship",
            RiskReason = GenerateRiskReason(AlignmentStatus.NeedsReview, AlignmentRisk.Medium, areas),
            IsRegressionCandidate = areas.Any(a => AreaImpact(a) <= ImpactLevel.Medium),
        };
    }

    private static SemanticUserStory? FindUserStory(string? userStoryId, ReviewContext context)
    {
        if (string.IsNullOrWhiteSpace(userStoryId))
            return null;

        var normalized = NormalizeId(userStoryId);
        var userStories = context.GetUserStories();
        var directMatch = userStories
            .FirstOrDefault(story => NormalizeId(story.Id) == normalized);

        if (directMatch is not null)
            return directMatch;

        var ordinal = ExtractOrdinal(userStoryId);
        if (ordinal is null || ordinal < 1 || ordinal > userStories.Count)
            return null;

        return userStories[ordinal.Value - 1];
    }

    private static string NormalizeId(string id)
    {
        var chars = id.Where(char.IsLetterOrDigit).ToArray();
        var compact = new string(chars).ToUpperInvariant();
        var prefix = new string(compact.TakeWhile(char.IsLetter).ToArray());
        var digits = new string(compact.SkipWhile(char.IsLetter).ToArray()).TrimStart('0');
        return string.IsNullOrEmpty(digits) ? compact : $"{prefix}{digits}";
    }

    private static int? ExtractOrdinal(string id)
    {
        var digits = new string(id.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var ordinal) ? ordinal : null;
    }

    private static bool IsTechnicalOnly(TaskItem task)
    {
        var text = TaskText(task);
        return task.IsTestingTask
               || HasAny(text, InfrastructureTerms)
               || HasAny(text, GeneratedCodeTerms)
               || task.RelatedFileIds.Any(file => EndsWithAny(file, ".csproj", ".sln", ".json", ".yaml", ".yml"));
    }

    private static bool IntroducesBehavior(TaskItem task)
    {
        var text = TaskText(task);
        return task.IsSecurityTask || HasAny(text, BehavioralTerms);
    }

    private static List<AffectedArea> DetectAreas(TaskItem task)
    {
        var text = TaskText(task);
        var areas = AreaTerms
            .Where(definition => HasAny(text, definition.Terms))
            .Select(definition => definition.Area)
            .Distinct()
            .ToList();

        if (task.IsTestingTask && !areas.Contains(AffectedArea.Testing))
            areas.Add(AffectedArea.Testing);
        if (task.IsSecurityTask && !areas.Contains(AffectedArea.Security))
            areas.Add(AffectedArea.Security);
        if (task.RelatedFileIds.Any() && !areas.Contains(AffectedArea.Infrastructure))
            areas.Add(AffectedArea.Infrastructure);

        return areas;
    }

    private static string TaskText(TaskItem task) =>
        $"{task.Title} {task.Description} {string.Join(' ', task.RelatedFileIds)}".ToLowerInvariant();

    private static bool HasAny(string text, IEnumerable<string> terms) =>
        terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static bool EndsWithAny(string text, params string[] suffixes) =>
        suffixes.Any(suffix => text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    private static ImpactLevel ComputeImpactLevel(AlignmentStatus status, List<AffectedArea> areas)
    {
        if (status == AlignmentStatus.PossibleDeviation)
            return ImpactLevel.High;
        if (status == AlignmentStatus.TechnicalOnly)
            return ImpactLevel.Low;
        if (areas.Count == 0)
            return ImpactLevel.Unknown;
        return areas.Select(AreaImpact).Min();
    }

    private static ImpactLevel AreaImpact(AffectedArea area) => area switch
    {
        AffectedArea.Security
            or AffectedArea.Authorization
            or AffectedArea.DomainEvents
            or AffectedArea.Audit => ImpactLevel.High,
        AffectedArea.Ingestion
            or AffectedArea.HealthMonitoring
            or AffectedArea.Validation
            or AffectedArea.ExceptionHandling => ImpactLevel.Medium,
        _ => ImpactLevel.Low,
    };

    private static List<string> BuildTests(List<AffectedArea> areas, int maxCount) =>
        areas.SelectMany(TestsForArea)
            .Distinct()
            .Take(maxCount)
            .ToList();

    private static IEnumerable<string> TestsForArea(AffectedArea area) => area switch
    {
        AffectedArea.Security => ["Security classification negative test", "No sensitive data in logs"],
        AffectedArea.Authorization => ["Unauthorized request rejection", "Permission boundary test"],
        AffectedArea.Ingestion => ["Idempotent ingestion", "Invalid event handling"],
        AffectedArea.DomainEvents => ["Event payload contract test", "Duplicate event handling"],
        AffectedArea.HealthMonitoring => ["Health endpoint status test", "Dependency failure health test"],
        AffectedArea.Infrastructure => ["Application starts with expected configuration", "Missing configuration fails safely"],
        AffectedArea.Testing => ["Test suite compiles and runs"],
        AffectedArea.ExceptionHandling => ["Error response format test"],
        AffectedArea.Validation => ["Invalid input rejection test"],
        _ => [],
    };

    private static string GenerateRiskReason(AlignmentStatus status, AlignmentRisk risk, List<AffectedArea> areas)
    {
        if (risk == AlignmentRisk.Low)
            return string.Empty;

        var reasons = new List<string>();
        if (status == AlignmentStatus.PossibleDeviation)
            reasons.Add("Behavior has no canonical ReviewContext specification coverage.");

        foreach (var area in areas.Take(3))
        {
            var reason = area switch
            {
                AffectedArea.Security => "Touches security-sensitive behavior.",
                AffectedArea.Authorization => "Touches authorization or access control.",
                AffectedArea.DomainEvents => "Touches event publication or consumption.",
                AffectedArea.Ingestion => "Touches ingestion or data synchronization.",
                AffectedArea.HealthMonitoring => "Touches operational health visibility.",
                _ => string.Empty,
            };
            if (!string.IsNullOrEmpty(reason))
                reasons.Add(reason);
        }

        if (reasons.Count == 0)
            reasons.Add("Manual confirmation is required.");

        return string.Join(" ", reasons);
    }

    private static string Shorten(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var clean = text.ReplaceLineEndings(" ").Trim();
        return clean.Length <= 140 ? clean : $"{clean[..137]}...";
    }
}
