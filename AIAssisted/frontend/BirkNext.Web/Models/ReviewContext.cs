namespace BirkNext.Web.Models;

/// <summary>
/// Unified container for all semantic models.
/// Single source of truth aggregating Constitution, Specification, Plan, Tasks, and DataModel semantic models.
/// ReviewContext is consumed by all review pages instead of parsing markdown directly.
/// </summary>
public sealed class ReviewContext
{
    // ── Semantic Models ─────────────────────────────────────────────────────
    public ConstitutionSemanticModel Constitution { get; init; } = new();
    public SpecificationSemanticModel Specification { get; init; } = new();
    public PlanSemanticModel Plan { get; init; } = new();
    public TaskSemanticModel Tasks { get; init; } = new();
    public DataModelSemanticModel DataModel { get; init; } = new();

    // ── Aggregated Coverage Metrics ─────────────────────────────────────────
    public ReviewCoverageSummary Coverage { get; init; } = new();

    // ── Cross-Artifact Relationships ────────────────────────────────────────
    public Dictionary<string, List<string>> SpecToConstitution { get; init; } = [];  // FR/SC to Constitution Rules
    public Dictionary<string, List<string>> SpecToPlan { get; init; } = [];          // FR/SC to Plan Decisions
    public Dictionary<string, List<string>> SpecToTasks { get; init; } = [];         // FR/SC to Tasks
    public Dictionary<string, List<string>> SpecToDataModel { get; init; } = [];     // FR/SC to Data Entities
    public Dictionary<string, List<string>> PlanToTasks { get; init; } = [];         // Plan Phases to Tasks
    public Dictionary<string, List<string>> ConstitutionToTasks { get; init; } = []; // Constitution Rules to Tasks

    // ── Derived Metrics (Convenience Properties) ────────────────────────────
    // These are helper properties that avoid code duplication in pages.
    // They derive from semantic models or coverage metrics above.

    public int RequirementsWithTests => Specification.RequirementsWithTests;
    public int RequirementsWithSuccessCriteria => Specification.RequirementsWithSuccessCriteria;
    public int MissingTests => Specification.TotalRequirements - RequirementsWithTests;
    public int MissingSuccessCriteria => Specification.TotalRequirements - RequirementsWithSuccessCriteria;

    // ── Navigation API (Single Source of Truth for Queries) ───────────────────
    // Pages should use these methods instead of directly traversing semantic models.

    // Core collections
    public IReadOnlyList<SemanticRequirement> GetRequirements() => Specification.Requirements;
    public SemanticRequirement? GetRequirement(string id) => Specification.Requirements.FirstOrDefault(r => r.Id == id);
    public IReadOnlyList<SemanticUserStory> GetUserStories() => Specification.UserStories;
    public IReadOnlyList<SemanticSuccessCriterion> GetSuccessCriteria() => Specification.SuccessCriteria;
    public IReadOnlyList<SemanticAcceptanceScenario> GetTests() => Specification.AcceptanceScenarios;
    public IReadOnlyList<SemanticClarification> GetClarifications() => Specification.Clarifications;
    public IReadOnlyList<SemanticConstitutionRule> GetConstitutionRules() => Constitution.Rules;
    public IReadOnlyList<TaskItem> GetTasks() => Tasks.AllTasks;
    public IReadOnlyList<SemanticDataEntity> GetDataEntities() => DataModel.Entities;

    // Related items by ID
    public IReadOnlyList<SemanticAcceptanceScenario> GetTests(string requirementId) =>
        GetRequirement(requirementId)?.LinkedAcceptanceScenarios ?? [];

    public IReadOnlyList<SemanticSuccessCriterion> GetSuccessCriteria(string requirementId) =>
        GetRequirement(requirementId)?.LinkedSuccessCriteria ?? [];

    public IReadOnlyList<SemanticUserStory> GetUserStories(string requirementId) =>
        GetRequirement(requirementId)?.LinkedUserStories ?? [];

    public IReadOnlyList<string> GetLinkedConstitutionRules(string requirementId) =>
        SpecToConstitution.TryGetValue(requirementId, out var rules) ? rules : [];

    public IReadOnlyList<string> GetLinkedTasks(string requirementId) =>
        SpecToTasks.TryGetValue(requirementId, out var tasks) ? tasks : [];

    public IReadOnlyDictionary<string, List<string>> GetSpecLinks() => SpecToTasks;

    public IReadOnlyList<string> GetSpecLinks(string specItemId) =>
        SpecToTasks.TryGetValue(specItemId, out var tasks) ? tasks : [];

    public IReadOnlyList<string> GetLinkedPlans(string requirementId) =>
        SpecToPlan.TryGetValue(requirementId, out var plans) ? plans : [];

    public IReadOnlyList<string> GetLinkedDataEntities(string requirementId) =>
        SpecToDataModel.TryGetValue(requirementId, out var entities) ? entities : [];

    // Filtered collections
    public IEnumerable<SemanticRequirement> GetRequirementsWithTests() =>
        Specification.Requirements.Where(r => r.LinkedAcceptanceScenarios.Count > 0);

    public IEnumerable<SemanticRequirement> GetRequirementsWithoutTests() =>
        Specification.Requirements.Where(r => r.LinkedAcceptanceScenarios.Count == 0);

    public IEnumerable<SemanticRequirement> GetRequirementsWithSuccessCriteria() =>
        Specification.Requirements.Where(r => r.LinkedSuccessCriteria.Count > 0);

    public IEnumerable<SemanticRequirement> GetRequirementsWithoutSuccessCriteria() =>
        Specification.Requirements.Where(r => r.LinkedSuccessCriteria.Count == 0);

    public IEnumerable<SemanticRequirement> GetRequirementsWithUserStories() =>
        Specification.Requirements.Where(r => r.LinkedUserStories.Count > 0);

    public IEnumerable<SemanticRequirement> GetRequirementsWithoutUserStories() =>
        Specification.Requirements.Where(r => r.LinkedUserStories.Count == 0);

    public IEnumerable<SemanticSuccessCriterion> GetSuccessCriteriaWithoutRequirements() =>
        Specification.SuccessCriteria.Where(s => s.LinkedRequirements.Count == 0);

    public IEnumerable<SemanticSuccessCriterion> GetSuccessCriteriaWithoutTests() =>
        Specification.SuccessCriteria.Where(s => s.LinkedRequirements.All(r => r.LinkedAcceptanceScenarios.Count == 0));

    public IEnumerable<SemanticAcceptanceScenario> GetTestsWithoutRequirements() =>
        Specification.AcceptanceScenarios.Where(a => a.LinkedRequirements.Count == 0);

    // Gap analysis
    public int GetOrphanedTestCount() => GetTestsWithoutRequirements().Count();
    public int GetOrphanedSuccessCriteriaCount() => GetSuccessCriteriaWithoutRequirements().Count();
    public int GetRequirementsWithoutCoverageCount() => GetRequirementsWithoutTests().Count();
    public int GetRequirementsWithoutSuccessCriteriaCount() => GetRequirementsWithoutSuccessCriteria().Count();

    // Relationship queries
    public bool IsLinkedToConstitution(string requirementId) => SpecToConstitution.ContainsKey(requirementId);
    public bool IsLinkedToTasks(string requirementId) => SpecToTasks.ContainsKey(requirementId);
    public bool IsLinkedToPlans(string requirementId) => SpecToPlan.ContainsKey(requirementId);
    public bool IsLinkedToDataModel(string requirementId) => SpecToDataModel.ContainsKey(requirementId);

    // Coverage state for a single requirement
    public bool HasTestCoverage(string requirementId)
    {
        var req = GetRequirement(requirementId);
        return req != null && req.LinkedAcceptanceScenarios.Count > 0;
    }

    public bool HasSuccessCriteria(string requirementId)
    {
        var req = GetRequirement(requirementId);
        return req != null && req.LinkedSuccessCriteria.Count > 0;
    }

    public bool HasUserStoryLink(string requirementId)
    {
        var req = GetRequirement(requirementId);
        return req != null && req.LinkedUserStories.Count > 0;
    }
}

/// <summary>
/// Aggregated coverage metrics across all artifacts.
/// </summary>
public sealed class ReviewCoverageSummary
{
    // ── Specification Coverage ──────────────────────────────────────────────
    public int TotalUserStories { get; init; }
    public int TotalRequirements { get; init; }
    public int TotalSuccessCriteria { get; init; }
    public int TotalAcceptanceScenarios { get; init; }
    public int TotalClarifications { get; init; }

    // ── Traceability Coverage ───────────────────────────────────────────────
    public int RequirementsLinkedToUserStories { get; init; }
    public int SuccessCriteriaLinkedToRequirements { get; init; }
    public int AcceptanceScenariosLinkedToRequirements { get; init; }
    public int RequirementsLinkedToTasks { get; init; }
    public int RequirementsLinkedToConstitution { get; init; }

    // ── Quality Metrics ─────────────────────────────────────────────────────
    public int TotalRisks { get; init; }
    public int CriticalRisks { get; init; }
    public int HighRisks { get; init; }
    public int TotalComplexityFactors { get; init; }
    public int HighComplexityAreas { get; init; }

    // ── Governance Coverage ─────────────────────────────────────────────────
    public int TotalConstitutionRules { get; init; }
    public int CompliantRules { get; init; }
    public int NonCompliantRules { get; init; }
    public int ConstitutionGatesPass { get; init; }
    public int ConstitutionGatesFail { get; init; }

    // ── Implementation Coverage ─────────────────────────────────────────────
    public int TotalTasks { get; init; }
    public int CompletedTasks { get; init; }
    public int OpenTasks { get; init; }
    public int TasksLinkedToRequirements { get; init; }
    public int UnlinkedTasks { get; init; }

    // ── Data Model Coverage ─────────────────────────────────────────────────
    public int TotalEntities { get; init; }
    public int EntitiesWithValidation { get; init; }
    public int OrphanEntities { get; init; }

    // ── Computed Metrics ────────────────────────────────────────────────────
    public int SpecificationCompleteness => ComputeSpecificationCompleteness();
    public int TraceabilityCompleteness => ComputeTraceabilityCompleteness();
    public int GovernanceCompleteness => ComputeGovernanceCompleteness();
    public int ImplementationCompleteness => ComputeImplementationCompleteness();
    public int OverallCompleteness => (SpecificationCompleteness + TraceabilityCompleteness + GovernanceCompleteness + ImplementationCompleteness) / 4;

    // ── Helper Methods ──────────────────────────────────────────────────────
    private int ComputeSpecificationCompleteness()
    {
        if (TotalRequirements == 0) return 0;
        return (RequirementsLinkedToUserStories * 100) / TotalRequirements;
    }

    private int ComputeTraceabilityCompleteness()
    {
        if (TotalRequirements == 0) return 0;
        var linked = RequirementsLinkedToTasks + RequirementsLinkedToConstitution;
        return Math.Min(100, (linked * 100) / TotalRequirements);
    }

    private int ComputeGovernanceCompleteness()
    {
        if (TotalConstitutionRules == 0) return 0;
        return (CompliantRules * 100) / TotalConstitutionRules;
    }

    private int ComputeImplementationCompleteness()
    {
        if (TotalTasks == 0) return 0;
        return (CompletedTasks * 100) / TotalTasks;
    }
}

/// <summary>
/// Provides factory methods to create ReviewContext from parsed artifacts.
/// </summary>
public static class ReviewContextFactory
{
    /// <summary>
    /// Create ReviewContext from individual semantic models.
    /// </summary>
    public static ReviewContext Create(
        ConstitutionSemanticModel constitution,
        SpecificationSemanticModel specification,
        PlanSemanticModel plan,
        TaskSemanticModel tasks,
        DataModelSemanticModel dataModel)
    {
        var coverage = BuildCoverageSummary(constitution, specification, plan, tasks, dataModel);

        return new ReviewContext
        {
            Constitution = constitution,
            Specification = specification,
            Plan = plan,
            Tasks = tasks,
            DataModel = dataModel,
            Coverage = coverage,
            SpecToConstitution = BuildSpecToConstitutionLinks(specification, constitution),
            SpecToPlan = BuildSpecToPlanLinks(specification, plan),
            SpecToTasks = BuildSpecToTasksLinks(specification, tasks),
            SpecToDataModel = BuildSpecToDataModelLinks(specification, dataModel),
            PlanToTasks = BuildPlanToTasksLinks(plan, tasks),
            ConstitutionToTasks = BuildConstitutionToTasksLinks(constitution, tasks),
        };
    }

    /// <summary>
    /// Build aggregated coverage summary from all semantic models.
    /// </summary>
    private static ReviewCoverageSummary BuildCoverageSummary(
        ConstitutionSemanticModel constitution,
        SpecificationSemanticModel specification,
        PlanSemanticModel plan,
        TaskSemanticModel tasks,
        DataModelSemanticModel dataModel)
    {
        return new ReviewCoverageSummary
        {
            // Specification
            TotalUserStories = specification.UserStories.Count,
            TotalRequirements = specification.Requirements.Count,
            TotalSuccessCriteria = specification.SuccessCriteria.Count,
            TotalAcceptanceScenarios = specification.AcceptanceScenarios.Count,
            TotalClarifications = specification.Clarifications.Count,

            // Traceability
            RequirementsLinkedToUserStories = specification.Requirements.Count(r => r.LinkedUserStories.Count > 0),
            SuccessCriteriaLinkedToRequirements = specification.SuccessCriteria.Count(s => s.LinkedRequirements.Count > 0),
            AcceptanceScenariosLinkedToRequirements = specification.AcceptanceScenarios.Count(a => a.LinkedRequirements.Count > 0),
            RequirementsLinkedToTasks = specification.Requirements.Count(r => r.LinkedTasks.Count > 0),
            RequirementsLinkedToConstitution = specification.Requirements.Count(r => r.LinkedConstitutionRules.Count > 0),

            // Quality
            TotalRisks = plan.Risks.Count,
            CriticalRisks = plan.CriticalRisks,
            HighRisks = plan.HighRisks,
            TotalComplexityFactors = plan.ComplexityFactors.Count,
            HighComplexityAreas = plan.HighComplexityAreas,

            // Governance
            TotalConstitutionRules = constitution.Rules.Count,
            CompliantRules = constitution.CompliantChecks,
            NonCompliantRules = constitution.NonCompliantChecks,
            ConstitutionGatesPass = constitution.PassedGates,
            ConstitutionGatesFail = constitution.FailedGates,

            // Implementation
            TotalTasks = tasks.AllTasks.Count,
            CompletedTasks = tasks.CompletedTasks,
            OpenTasks = tasks.OpenTasks,
            TasksLinkedToRequirements = tasks.FRLinkedTasks + tasks.SCLinkedTasks,
            UnlinkedTasks = tasks.UnlinkedTasks,

            // Data Model
            TotalEntities = dataModel.Entities.Count,
            EntitiesWithValidation = dataModel.EntitiesWithValidation,
            OrphanEntities = dataModel.OrphanEntities,
        };
    }

    /// <summary>
    /// Link specification elements to constitution rules.
    /// </summary>
    private static Dictionary<string, List<string>> BuildSpecToConstitutionLinks(
        SpecificationSemanticModel specification,
        ConstitutionSemanticModel constitution)
    {
        var links = new Dictionary<string, List<string>>();

        foreach (var requirement in specification.Requirements)
        {
            if (requirement.LinkedConstitutionRules.Count > 0)
            {
                links[requirement.Id] = [..requirement.LinkedConstitutionRules];
            }
        }

        return links;
    }

    /// <summary>
    /// Link specification elements to plan decisions.
    /// </summary>
    private static Dictionary<string, List<string>> BuildSpecToPlanLinks(
        SpecificationSemanticModel specification,
        PlanSemanticModel plan)
    {
        var links = new Dictionary<string, List<string>>();

        foreach (var requirement in specification.Requirements)
        {
            var architectureDecisions = plan.ArchitectureDecisions
                .Where(d => d.RelatedRequirementIds.Contains(requirement.Id))
                .Select(d => d.Id)
                .ToList();

            if (architectureDecisions.Count > 0)
            {
                links[requirement.Id] = architectureDecisions;
            }

            var linkedArchDecisions = requirement.LinkedArchitectureDecisions;
            if (linkedArchDecisions.Count > 0)
            {
                if (!links.ContainsKey(requirement.Id))
                {
                    links[requirement.Id] = [];
                }
                links[requirement.Id].AddRange(linkedArchDecisions);
            }
        }

        return links;
    }

    /// <summary>
    /// Link specification elements to tasks.
    /// </summary>
    private static Dictionary<string, List<string>> BuildSpecToTasksLinks(
        SpecificationSemanticModel specification,
        TaskSemanticModel tasks)
    {
        var links = new Dictionary<string, List<string>>();

        foreach (var requirement in specification.Requirements)
        {
            var linkedTasks = new List<string>();

            if (tasks.FRToTasks.TryGetValue(requirement.Id, out var frTasks))
            {
                linkedTasks.AddRange(frTasks);
            }

            if (linkedTasks.Count > 0)
            {
                links[requirement.Id] = linkedTasks;
            }
        }

        foreach (var criterion in specification.SuccessCriteria)
        {
            var linkedTasks = new List<string>();

            if (tasks.SCToTasks.TryGetValue(criterion.Id, out var scTasks))
            {
                linkedTasks.AddRange(scTasks);
            }

            if (linkedTasks.Count > 0)
            {
                links[criterion.Id] = linkedTasks;
            }
        }

        return links;
    }

    /// <summary>
    /// Link specification elements to data model entities.
    /// </summary>
    private static Dictionary<string, List<string>> BuildSpecToDataModelLinks(
        SpecificationSemanticModel specification,
        DataModelSemanticModel dataModel)
    {
        var links = new Dictionary<string, List<string>>();

        // Link requirements to entities that implement them
        foreach (var requirement in specification.Requirements)
        {
            var linkedEntities = dataModel.Entities
                .Where(e => e.RelatedRequirementIds.Contains(requirement.Id))
                .Select(e => e.Id)
                .ToList();

            if (linkedEntities.Count > 0)
            {
                links[requirement.Id] = linkedEntities;
            }
        }

        return links;
    }

    /// <summary>
    /// Link plan phases to tasks.
    /// </summary>
    private static Dictionary<string, List<string>> BuildPlanToTasksLinks(
        PlanSemanticModel plan,
        TaskSemanticModel tasks)
    {
        var links = new Dictionary<string, List<string>>();

        foreach (var phase in plan.Phases)
        {
            links[$"Phase{phase.PhaseNumber}"] = [..phase.TaskIds];
        }

        return links;
    }

    /// <summary>
    /// Link constitution rules to tasks that implement them.
    /// </summary>
    private static Dictionary<string, List<string>> BuildConstitutionToTasksLinks(
        ConstitutionSemanticModel constitution,
        TaskSemanticModel tasks)
    {
        var links = new Dictionary<string, List<string>>();

        foreach (var rule in constitution.Rules)
        {
            var linkedTasks = tasks.AllTasks
                .Where(t => t.LinkedFRIds.Any(fr => constitution.RuleToRequirements.TryGetValue(rule.Id, out var reqs) && reqs.Contains(fr)))
                .Select(t => t.Id)
                .ToList();

            if (linkedTasks.Count > 0)
            {
                links[rule.Id] = linkedTasks;
            }
        }

        return links;
    }
}
