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
    public const string RulesVersion = "task-spec-rules-v3";

    private static readonly string[] InfrastructureTerms =
    [
        "csproj", "sln", "solution", "project setup", "project skeleton",
        "package reference", "nuget", "npm", "yarn", "appsettings", "configuration",
        "program.cs", "di registration", "service extension", "migration",
        "dbcontext", "healthcheck", "health check", "logging", "telemetry",
        "opentelemetry", "key vault", "managed identity", "pipeline", "dockerfile",
        "test project", "test fixture", "testcontainers",
        "options class", "placeholder config", "gitignore"
    ];

    /// <summary>Generated or scaffolding work only. Domain words such as "model", "record" or "schema" are not technical by themselves.</summary>
    private static readonly string[] GeneratedCodeTerms = ["generated", "scaffold", "stub", "boilerplate", "dto"];

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
            DesignLinkedTasks = results.Count(f => f.Status == AlignmentStatus.DesignLinked),
            TechnicalOnlyTasks = results.Count(f => f.Status == AlignmentStatus.TechnicalOnly),
            TestImplementationTasks = results.Count(f => f.Purpose == TaskPurpose.TestImplementation),
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
        var design = DesignLinks(task, context);
        var purpose = TaskPurposeOf(task, design);
        TaskFinding With(TaskFinding f) => new()
        {
            TaskId = f.TaskId, Title = f.Title, TaskText = f.TaskText, Status = f.Status, Risk = f.Risk, Reason = f.Reason, RecommendedAction = f.RecommendedAction,
            ClassificationBasis = f.ClassificationBasis, ClassificationSignal = f.ClassificationSignal, Matches = f.Matches, UnresolvedReferences = f.UnresolvedReferences,
            AffectedAreas = f.AffectedAreas, RecommendedTests = f.RecommendedTests, ImpactLevel = f.ImpactLevel, MatchReason = f.MatchReason, RiskReason = f.RiskReason,
            IsRegressionCandidate = f.IsRegressionCandidate, DesignLinks = design, Purpose = purpose, Deviation = f.Deviation, UnlinkedBusinessTask = f.UnlinkedBusinessTask,
        };

        // Possible deviation needs an explicit authored reference AND a deterministic conflict between the two authored statements.
        if (matches.Count > 0 && Conflict(task, matches, context) is { } conflict)
            return With(BuildDeviationResult(task, matches, conflict, topics));

        if (matches.Count > 0)
            return With(BuildLinkedResult(task, matches, unresolved, topics));

        if (unresolved.Count > 0)
            return With(BuildUnresolvedResult(task, unresolved, topics));

        if (design.Count > 0)
            return With(BuildDesignLinkedResult(task, design, topics));

        var technicalSignal = TechnicalSignal(task);
        if (technicalSignal is not null)
            return With(BuildTechnicalResult(task, technicalSignal, topics));

        var behaviorSignal = BehaviorSignal(task);
        if (behaviorSignal is not null)
            return With(BuildUnlinkedBusinessResult(task, behaviorSignal, topics));

        return With(BuildNeedsReviewResult(task, topics));
    }

    // ── Design artifact links ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Authored design artifacts the task names exactly: a Data Model structure (whole-word, case-sensitive name) or a Constitution rule ID
    /// that exists. Exact names only — never a topic or keyword similarity.
    /// </summary>
    private static List<DesignArtifactLink> DesignLinks(TaskItem task, ReviewContext context)
    {
        var text = $"{task.Title} {task.Description}";
        var links = new List<DesignArtifactLink>();
        foreach (var entity in context.GetDataEntities().Where(e => e.Name.Length >= 4).OrderByDescending(e => e.Name.Length))
        {
            if (links.Any(l => l.Name.Contains(entity.Name, StringComparison.Ordinal))) continue;
            if (System.Text.RegularExpressions.Regex.IsMatch(text, $@"(?<![\w.]){System.Text.RegularExpressions.Regex.Escape(entity.Name)}(?![\w])"))
                links.Add(new("Data Model", entity.Name, (string.IsNullOrWhiteSpace(context.DataModel.Title) ? null : context.DataModel.Title)));
        }
        foreach (var rule in context.GetConstitutionRules().Where(r => r.Id.Length > 0))
            if (System.Text.RegularExpressions.Regex.IsMatch(text, $@"\b{System.Text.RegularExpressions.Regex.Escape(rule.Id)}\b"))
                links.Add(new("Constitution", rule.Id, (string.IsNullOrWhiteSpace(context.Constitution.Title) ? null : context.Constitution.Title)));
        return links;
    }

    private static TaskFinding BuildDesignLinkedResult(TaskItem task, List<DesignArtifactLink> design, List<AffectedArea> topics)
    {
        var priority = TopicPriority(AlignmentStatus.DesignLinked, topics);
        var named = string.Join(", ", design.Select(d => $"{d.Name} ({d.ArtifactType})"));
        return new TaskFinding
        {
            TaskId = task.Id, Title = task.Title, TaskText = task.Description,
            Status = AlignmentStatus.DesignLinked, Risk = AlignmentRisk.Low,
            Reason = $"The task names {named}, defined in the selected authored design artifacts. No explicit Requirement/SC reference was found. This is valid design alignment, not requirement coverage.",
            RecommendedAction = "None for alignment, unless the task should also trace to a Requirement or Success Criterion.",
            ClassificationBasis = "Exact authored design-artifact name in the task (Data Model structure or Constitution rule)",
            AffectedAreas = topics, RecommendedTests = BuildTests(topics, 5), ImpactLevel = priority,
            MatchReason = "Design artifact link", RiskReason = PriorityReason(AlignmentStatus.DesignLinked, priority, topics),
            IsRegressionCandidate = priority == ImpactLevel.High,
        };
    }

    // ── Possible deviation: deterministic conflicting authored statements only ─────────────────────────────────────────

    private static readonly System.Text.RegularExpressions.Regex DeniedContext = new(@"\b(unauthori[sz]ed|not authori[sz]ed|forbidden|denied|without (permission|access))\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex StatusCode = new(@"\b([1-5]\d\d)\b");
    private static readonly System.Text.RegularExpressions.Regex FailClosed = new(@"\b(fail[- ]closed|deny by default|default deny)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex FailOpen = new(@"\b(fail[- ]open|fallback allow|allow on error|allow by default|grant access on (error|failure))\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex MustNot = new(@"\b(?:MUST|SHALL) NOT ((?:[a-z]+ ){1,3}[a-z]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// A deterministic conflict between the task and a Specification item it references: a different HTTP status for the same denied-access
    /// case, fail-open against a fail-closed rule, or the task instructing exactly what a MUST NOT forbids. Ambiguity is never a deviation.
    /// </summary>
    private static DeviationEvidence? Conflict(TaskItem task, List<SpecMatch> matches, ReviewContext context)
    {
        var taskText = $"{task.Title} {task.Description}";
        foreach (var match in matches.Where(m => m.MatchType is SpecMatchType.Requirement or SpecMatchType.SuccessCriterion))
        {
            var authored = match.MatchType == SpecMatchType.Requirement
                ? (context.GetRequirement(match.ItemId) ?? context.Specification.Requirements.FirstOrDefault(r => r.Id.Equals(match.ItemId, StringComparison.OrdinalIgnoreCase)))?.Text
                : context.GetSuccessCriteria().FirstOrDefault(sc => sc.Id.Equals(match.ItemId, StringComparison.OrdinalIgnoreCase))?.Text;
            if (string.IsNullOrWhiteSpace(authored)) continue;

            if (DeniedContext.IsMatch(authored) && DeniedContext.IsMatch(taskText)
                && StatusCode.Match(authored) is { Success: true } specCode && StatusCode.Match(taskText) is { Success: true } taskCode
                && specCode.Value != taskCode.Value)
                return new("Different response for the same denied-access case", taskText.Trim(), match.ItemId, authored.Trim());
            if (FailClosed.IsMatch(authored) && FailOpen.IsMatch(taskText))
                return new("Fail-open instruction against a fail-closed requirement", taskText.Trim(), match.ItemId, authored.Trim());
            foreach (System.Text.RegularExpressions.Match forbidden in MustNot.Matches(authored))
            {
                var phrase = forbidden.Groups[1].Value.Trim();
                var at = taskText.IndexOf(phrase, StringComparison.OrdinalIgnoreCase);
                if (phrase.Split(' ').Length >= 2 && at >= 0 && !System.Text.RegularExpressions.Regex.IsMatch(taskText[..at], @"\b(not|never|no|without|prevent|ensure no)\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    return new($"Task instructs what {match.ItemId} forbids (\"MUST NOT {phrase}\")", taskText.Trim(), match.ItemId, authored.Trim());
            }
        }
        return null;
    }

    // ── Primary purpose (separate from secondary topics) ────────────────────────────────────────────────────────────

    private static TaskPurpose TaskPurposeOf(TaskItem task, List<DesignArtifactLink> design)
    {
        var text = TaskTopicTaxonomy.TaskText(task);
        bool Has(params string[] terms) => TaskTopicTaxonomy.MatchedTerm(text, terms) is not null;
        if (TaskTopicTaxonomy.HasTestIntent(task)) return TaskPurpose.TestImplementation;
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"\b(stop and validate|checkpoint|verify that|validate that)\b")) return TaskPurpose.Verification;
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"\bcreate\b[^.;]{0,30}\bsolution\b|\.sln\b|\.csproj\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            || Has("create solution", "initialize", "initialise", "project setup", "project skeleton", "package reference", "nuget", "csproj", "dockerfile", "pipeline", "scaffold", "gitignore")) return TaskPurpose.InfrastructureSetup;
        if (Has("migration")) return TaskPurpose.Migration;
        if (design.Any(d => d.ArtifactType == "Data Model")) return TaskPurpose.DataModel;
        if (Has("security", "kode 6", "kode 7", "authorization", "authorize", "permission", "access control")) return TaskPurpose.Security;
        if (Has("event", "publish", "consume", "service bus", "event hub", "cdc", "http client", "integration", "outbox")) return TaskPurpose.Integration;
        if (Has("logging", "telemetry", "opentelemetry", "health check", "healthcheck", "metric", "monitor")) return TaskPurpose.Observability;
        if (Has("readme", "documentation", "docs/")) return TaskPurpose.Documentation;
        if (Has("appsettings", "configuration", "program.cs", "di registration", "dbcontext", "options class")) return TaskPurpose.InfrastructureSetup;
        if (BehaviorSignal(task) is not null) return TaskPurpose.BusinessBehavior;
        return TaskPurpose.Unknown;
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
                matches.Add(new SpecMatch { ItemId = AuthoredStoryId(userStory, task.UserStoryId), Title = Shorten(userStory.Title), MatchType = SpecMatchType.UserStory });
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
            topics.Add(TaskTopicTaxonomy.HasTestIntent(task) ? AffectedArea.Testing : AffectedArea.Infrastructure);

        return new TaskFinding
        {
            TaskId = task.Id,
            Title = task.Title,
            TaskText = task.Description,
            Status = AlignmentStatus.TechnicalOnly,
            Risk = AlignmentRisk.Low,
            Reason = "Setup, infrastructure, generated-code or test-harness work. No direct Requirement/SC or authored design-artifact link is expected.",
            RecommendedAction = "None, unless the task also introduces user-visible behavior — then link it to the Specification.",
            ClassificationBasis = "No spec or design-artifact reference; the task matches a setup, infrastructure or test-harness keyword",
            ClassificationSignal = signal,
            AffectedAreas = topics,
            RecommendedTests = BuildTests(topics, 5),
            ImpactLevel = ImpactLevel.Low,
            MatchReason = "Technical keyword",
            IsRegressionCandidate = false,
        };
    }

    private static TaskFinding BuildDeviationResult(TaskItem task, List<SpecMatch> matches, DeviationEvidence evidence, List<AffectedArea> topics) => new()
    {
        TaskId = task.Id,
        Title = task.Title,
        TaskText = task.Description,
        Status = AlignmentStatus.PossibleDeviation,
        Risk = AlignmentRisk.High,
        Reason = $"Task says: {evidence.TaskSays} — {evidence.AuthoredReference} says: {evidence.AuthoredSays}. These authored statements appear inconsistent and require review.",
        RecommendedAction = "Review both statements with the author; correct the task or the Specification. Neither side is assumed correct.",
        ClassificationBasis = $"Deterministic conflict: {evidence.Rule}",
        Matches = matches,
        Deviation = evidence,
        AffectedAreas = topics,
        RecommendedTests = BuildTests(topics, 6),
        ImpactLevel = ImpactLevel.High,
        MatchReason = "Conflicting authored statements",
        RiskReason = PriorityReason(AlignmentStatus.PossibleDeviation, ImpactLevel.High, topics),
        IsRegressionCandidate = true,
    };

    private static TaskFinding BuildUnlinkedBusinessResult(TaskItem task, string signal, List<AffectedArea> topics)
    {
        var priority = TopicPriority(AlignmentStatus.NeedsReview, topics);
        return new TaskFinding
        {
            TaskId = task.Id,
            Title = task.Title,
            TaskText = task.Description,
            Status = AlignmentStatus.NeedsReview,
            Risk = AlignmentRisk.Medium,
            Reason = "The task appears to define business behavior, but no explicit authored target (Requirement, Success Criterion, user story or design artifact) could be resolved. The task may be valid; the alignment cannot be established deterministically.",
            RecommendedAction = "Link the task to the relevant Requirement/SC or authored design artifact if such a relationship is intended.",
            ClassificationBasis = "Unlinked business task: no authored reference; the task matches a behavior keyword",
            ClassificationSignal = signal,
            AffectedAreas = topics,
            RecommendedTests = BuildTests(topics, 6),
            ImpactLevel = priority,
            MatchReason = "Unlinked business task",
            RiskReason = PriorityReason(AlignmentStatus.NeedsReview, priority, topics),
            IsRegressionCandidate = priority is ImpactLevel.High or ImpactLevel.Medium,
            UnlinkedBusinessTask = true,
        };
    }

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
            Reason = "The task may be valid, but the authored alignment cannot be established deterministically (no spec or design reference, no deciding keyword).",
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

    /// <summary>The authored user-story reference: the Specification's own ID, else the task's authored tag (US1). A generated internal id is never shown.</summary>
    internal static string AuthoredStoryId(SemanticUserStory story, string authoredTag) =>
        SpecExplorerService.IsGeneratedUserStoryId(story.Id) ? authoredTag : story.Id;

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
            reasons.Add("Conflicting authored statements.");

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
