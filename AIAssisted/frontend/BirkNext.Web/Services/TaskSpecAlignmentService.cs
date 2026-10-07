using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Implementation Review (task → spec) analysis over the canonical ReviewContext. Every task gets one result;
/// only Needs review and Possible deviation results are findings. The analysis checks whether a task's explicit
/// references resolve to the Specification — it does not read source code or verify implementation, and a
/// direct spec link is not coverage. This service must not parse markdown or rebuild spec/task relationships.
/// </summary>
public sealed class TaskSpecAlignmentService
{
    /// <summary>Bumped whenever classification rules change, so a stored result can say which rules produced it.</summary>
    public const string RulesVersion = "task-spec-rules-v2";

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

    /// <summary>Signals of new externally reachable behavior; they override setup keywords, because an endpoint is never "setup only".</summary>
    private static readonly string[] ExposedBehaviorTerms =
    [
        "endpoint", "controller", "route", "post /", "get /", "put /", "patch /", "delete /"
    ];

    private static readonly string[] BehavioralTerms =
    [
        "endpoint", "api", "route", "process", "consume", "publish", "deliver",
        "map", "translate", "reject", "authorize", "validate", "business rule",
        "cdc", "event", "fault queue", "checkpoint", "full load", "retry",
        "health", "alert", "security", "kode 6", "kode 7", "permission"
    ];

    public AlignmentReport Analyse(ReviewContext reviewContext)
    {
        ArgumentNullException.ThrowIfNull(reviewContext);

        var results = reviewContext.GetTasks()
            .Select(task => ClassifyTask(task, reviewContext))
            .ToList();

        return new AlignmentReport
        {
            TotalTasks = results.Count,
            LinkedTasks = results.Count(f => f.Status == AlignmentStatus.Linked),
            TechnicalOnlyTasks = results.Count(f => f.Status == AlignmentStatus.TechnicalOnly),
            NeedsReviewTasks = results.Count(f => f.Status == AlignmentStatus.NeedsReview),
            PossibleDeviations = results.Count(f => f.Status == AlignmentStatus.PossibleDeviation),
            HighImpactTasks = results.Count(f => f.ImpactLevel == ImpactLevel.High),
            MediumImpactTasks = results.Count(f => f.ImpactLevel == ImpactLevel.Medium),
            LowImpactTasks = results.Count(f => f.ImpactLevel == ImpactLevel.Low),
            UnknownImpactTasks = results.Count(f => f.ImpactLevel == ImpactLevel.Unknown),
            RegressionCandidates = results.Count(f => f.IsRegressionCandidate),
            Findings = results,
        };
    }

    private static TaskFinding ClassifyTask(TaskItem task, ReviewContext context)
    {
        var (matches, unresolved) = ResolveSpecLinks(task, context);
        var topics = TaskTopicTaxonomy.Detect(task);

        if (matches.Count > 0)
            return BuildLinkedResult(task, matches, unresolved, topics);

        if (unresolved.Count > 0)
            return BuildUnresolvedResult(task, unresolved, topics);

        var technicalSignal = TechnicalSignal(task);
        if (technicalSignal is not null)
            return BuildTechnicalResult(task, technicalSignal, topics);

        var behaviorSignal = BehaviorSignal(task);
        if (behaviorSignal is not null)
            return BuildDeviationResult(task, behaviorSignal, topics);

        return BuildNeedsReviewResult(task, topics);
    }

    /// <summary>
    /// The task's explicit references (FR, SC, user-story tag) split into those that resolve to an item in the
    /// Specification and those that do not. Only resolved references are direct spec links.
    /// </summary>
    private static (List<SpecMatch> Matches, List<string> Unresolved) ResolveSpecLinks(TaskItem task, ReviewContext context)
    {
        var matches = new List<SpecMatch>();
        var unresolved = new List<string>();

        foreach (var requirementId in task.LinkedFRIds)
        {
            var requirement = context.GetRequirement(requirementId)
                ?? context.Specification.Requirements.FirstOrDefault(r => r.Id.Equals(requirementId, StringComparison.OrdinalIgnoreCase));
            if (requirement is null)
            {
                unresolved.Add(requirementId);
                continue;
            }

            matches.Add(new SpecMatch { ItemId = requirement.Id, Title = Shorten(requirement.Text), MatchType = SpecMatchType.Requirement });
        }

        foreach (var criterionId in task.LinkedSCIds)
        {
            var criterion = context.GetSuccessCriteria()
                .FirstOrDefault(sc => sc.Id.Equals(criterionId, StringComparison.OrdinalIgnoreCase));
            if (criterion is null)
            {
                unresolved.Add(criterionId);
                continue;
            }

            matches.Add(new SpecMatch { ItemId = criterion.Id, Title = Shorten(criterion.Text), MatchType = SpecMatchType.SuccessCriterion });
        }

        if (!string.IsNullOrWhiteSpace(task.UserStoryId))
        {
            var userStory = FindUserStory(task.UserStoryId, context);
            if (userStory is null)
                unresolved.Add(task.UserStoryId);
            else
                matches.Add(new SpecMatch { ItemId = userStory.Id, Title = Shorten(userStory.Title), MatchType = SpecMatchType.UserStory });
        }

        return (
            matches.GroupBy(match => match.ItemId, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList(),
            unresolved.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static TaskFinding BuildLinkedResult(TaskItem task, List<SpecMatch> matches, List<string> unresolved, List<AffectedArea> topics)
    {
        var explicitReference = matches.Any(m => m.MatchType is SpecMatchType.Requirement or SpecMatchType.SuccessCriterion);
        var priority = TopicPriority(AlignmentStatus.Linked, topics);
        var risk = priority == ImpactLevel.High ? AlignmentRisk.Medium : AlignmentRisk.Low;

        return new TaskFinding
        {
            TaskId = task.Id,
            Title = task.Title,
            TaskText = task.Description,
            Status = AlignmentStatus.Linked,
            Risk = risk,
            Reason = $"The task references {string.Join(", ", matches.Select(m => m.ItemId))}, which resolve{(matches.Count == 1 ? "s" : "")} to the Specification.",
            RecommendedAction = unresolved.Count > 0
                ? $"Check the references that do not resolve: {string.Join(", ", unresolved)}."
                : "None for spec alignment. The link shows which spec items the task claims; it does not show the task implements them.",
            ClassificationBasis = explicitReference
                ? "Explicit FR/SC reference in the task resolved in the Specification"
                : "User story tag in the task resolved to a user story in the Specification",
            Matches = matches,
            UnresolvedReferences = unresolved,
            AffectedAreas = topics,
            RecommendedTests = BuildTests(topics, 6),
            ImpactLevel = priority,
            MatchReason = explicitReference ? "Explicit reference" : "User story tag",
            RiskReason = PriorityReason(AlignmentStatus.Linked, priority, topics),
            IsRegressionCandidate = priority == ImpactLevel.High,
        };
    }

    private static TaskFinding BuildUnresolvedResult(TaskItem task, List<string> unresolved, List<AffectedArea> topics)
    {
        var priority = TopicPriority(AlignmentStatus.NeedsReview, topics);
        return new TaskFinding
        {
            TaskId = task.Id,
            Title = task.Title,
            TaskText = task.Description,
            Status = AlignmentStatus.NeedsReview,
            Risk = AlignmentRisk.Medium,
            Reason = $"The task references {string.Join(", ", unresolved)}, but no such item exists in the Specification.",
            RecommendedAction = "Correct the reference in the Task artifact, or add the missing item to the Specification.",
            ClassificationBasis = "Reference in the task does not resolve in the Specification",
            UnresolvedReferences = unresolved,
            AffectedAreas = topics,
            RecommendedTests = BuildTests(topics, 6),
            ImpactLevel = priority,
            MatchReason = "Unresolved reference",
            RiskReason = PriorityReason(AlignmentStatus.NeedsReview, priority, topics),
            IsRegressionCandidate = priority is ImpactLevel.High or ImpactLevel.Medium,
        };
    }

    private static TaskFinding BuildTechnicalResult(TaskItem task, string signal, List<AffectedArea> topics)
    {
        if (!topics.Contains(AffectedArea.Infrastructure) && !topics.Contains(AffectedArea.Testing))
            topics.Add(task.IsTestingTask ? AffectedArea.Testing : AffectedArea.Infrastructure);

        return new TaskFinding
        {
            TaskId = task.Id,
            Title = task.Title,
            TaskText = task.Description,
            Status = AlignmentStatus.TechnicalOnly,
            Risk = AlignmentRisk.Low,
            Reason = "Setup, infrastructure, generated-code or test work. A direct spec link is not expected for this kind of task.",
            RecommendedAction = "None, unless the task also introduces user-visible behavior — then link it to the Specification.",
            ClassificationBasis = "No spec reference; the task matches a setup, infrastructure or test keyword",
            ClassificationSignal = signal,
            AffectedAreas = topics,
            RecommendedTests = BuildTests(topics, 5),
            ImpactLevel = ImpactLevel.Low,
            MatchReason = "Technical keyword",
            IsRegressionCandidate = false,
        };
    }

    private static TaskFinding BuildDeviationResult(TaskItem task, string signal, List<AffectedArea> topics) => new()
    {
        TaskId = task.Id,
        Title = task.Title,
        TaskText = task.Description,
        Status = AlignmentStatus.PossibleDeviation,
        Risk = AlignmentRisk.High,
        Reason = "The task appears to introduce behavior, but it has no reference to a requirement, success criterion or user story.",
        RecommendedAction = "Link the task to the requirement it implements, or document the behavior in the Specification.",
        ClassificationBasis = "No spec reference; the task matches a behavior keyword",
        ClassificationSignal = signal,
        AffectedAreas = topics,
        RecommendedTests = BuildTests(topics, 6),
        ImpactLevel = ImpactLevel.High,
        MatchReason = "No spec reference",
        RiskReason = PriorityReason(AlignmentStatus.PossibleDeviation, ImpactLevel.High, topics),
        IsRegressionCandidate = true,
    };

    private static TaskFinding BuildNeedsReviewResult(TaskItem task, List<AffectedArea> topics)
    {
        var priority = TopicPriority(AlignmentStatus.NeedsReview, topics);
        return new TaskFinding
        {
            TaskId = task.Id,
            Title = task.Title,
            TaskText = task.Description,
            Status = AlignmentStatus.NeedsReview,
            Risk = AlignmentRisk.Medium,
            Reason = "The task has no spec reference, and no setup or behavior keyword decides whether it needs one.",
            RecommendedAction = "Review the task against the Specification: link it, or confirm it is technical work.",
            ClassificationBasis = "No spec reference and no deciding keyword",
            AffectedAreas = topics,
            RecommendedTests = BuildTests(topics, 6),
            ImpactLevel = priority,
            MatchReason = "No spec reference",
            RiskReason = PriorityReason(AlignmentStatus.NeedsReview, priority, topics),
            IsRegressionCandidate = priority is ImpactLevel.High or ImpactLevel.Medium,
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

        // Spec Kit numbers user stories by position ("User Story 1"), so "US1" resolves to the first story.
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

    /// <summary>
    /// The keyword or file that makes an unlinked task technical-only, or null. Exposed behavior (an endpoint, a
    /// controller, a route) overrides setup keywords — except in test tasks, which test behavior rather than add it.
    /// </summary>
    private static string? TechnicalSignal(TaskItem task)
    {
        var text = TaskTopicTaxonomy.TaskText(task);
        var setup = TaskTopicTaxonomy.MatchedTerm(text, InfrastructureTerms)
            ?? TaskTopicTaxonomy.MatchedTerm(text, GeneratedCodeTerms)
            ?? task.RelatedFileIds.FirstOrDefault(file => TaskTopicTaxonomy.HasInfrastructureFile(new TaskItem { RelatedFileIds = [file] }));

        if (task.IsTestingTask)
            return setup ?? "test task";
        if (TaskTopicTaxonomy.MatchedTerm(text, ExposedBehaviorTerms) is not null)
            return null;
        return setup;
    }

    private static string? BehaviorSignal(TaskItem task)
    {
        var text = TaskTopicTaxonomy.TaskText(task);
        return TaskTopicTaxonomy.MatchedTerm(text, ExposedBehaviorTerms)
            ?? TaskTopicTaxonomy.MatchedTerm(text, BehavioralTerms)
            ?? (task.IsSecurityTask ? "security task" : null);
    }

    private static ImpactLevel TopicPriority(AlignmentStatus status, List<AffectedArea> topics)
    {
        if (status == AlignmentStatus.PossibleDeviation)
            return ImpactLevel.High;
        if (status == AlignmentStatus.TechnicalOnly)
            return ImpactLevel.Low;
        if (topics.Count == 0)
            return ImpactLevel.Unknown;
        return topics.Select(TaskTopicTaxonomy.TestPriority).Min();
    }

    private static List<string> BuildTests(List<AffectedArea> topics, int maxCount) =>
        topics.SelectMany(TestsForTopic)
            .Distinct()
            .Take(maxCount)
            .ToList();

    private static IEnumerable<string> TestsForTopic(AffectedArea topic) => topic switch
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

    private static string PriorityReason(AlignmentStatus status, ImpactLevel priority, List<AffectedArea> topics)
    {
        if (priority is ImpactLevel.Low or ImpactLevel.Unknown && status != AlignmentStatus.PossibleDeviation)
            return string.Empty;

        var reasons = new List<string>();
        if (status == AlignmentStatus.PossibleDeviation)
            reasons.Add("Behavior without a spec reference.");

        var prioritized = topics.Where(t => TaskTopicTaxonomy.TestPriority(t) != ImpactLevel.Low).Take(3).ToList();
        if (prioritized.Count > 0)
            reasons.Add($"Touches {string.Join(", ", prioritized.Select(t => TaskTopicTaxonomy.Label(t).ToLowerInvariant()))} (keyword topics).");

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
