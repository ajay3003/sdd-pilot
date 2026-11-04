using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BirkNext.Interpretation;

namespace BirkNext.DeliveryReporting;

// ── Delivery scoped views: Source + Functional scope over one report (or one snapshot) ───────────────────────────────────
// Membership is resolved on the server from canonical ids; the whole overview (metrics, charts, readiness, attention, change this period)
// is then computed from the scoped projection of the same frozen report — never from global numbers. Current and baseline are scoped with
// the same selection; a source or scope missing on one side is stated, never shown as an ordinary zero.

/// <summary>A source a Delivery snapshot was imported from: stable id from repository identity (Azure DevOps) or the canonical package key of
/// the archive (<c>M2LB-language (2).zip</c> → <c>package:m2lb-language</c>); the file name stays provenance.</summary>
public sealed partial record DeliverySourceIdentity(string Id, string Name, string Provenance)
{
    public static DeliverySourceIdentity Of(DeliverySourceType type, string fileName, DeliveryAzureDevOpsSource? ado)
    {
        if (ado is not null)
            return new($"repo:{Key($"{ado.Organization}/{ado.Project}/{ado.Repository}")}@{Key(ado.Branch)}", $"{ado.Repository} · {ado.Branch}",
                $"Azure DevOps {ado.Project}/{ado.Repository} @ {ado.Branch}");
        var stem = ArchiveStem(fileName);
        return new($"package:{Key(stem)}", stem.Length == 0 ? "Unnamed source" : stem, fileName.Length == 0 ? DeliveryReportingText.Source(type) : $"Imported from {fileName}");
    }

    public static DeliverySourceIdentity Of(DeliveryReportSnapshotRef r) => Of(r.SourceType, r.SourceFileName, r.AzureDevOps);
    public static DeliverySourceIdentity Of(DeliverySnapshotSummary s) => Of(s.SourceType, s.SourceFileName, s.AzureDevOps);

    /// <summary>The archive name without extension and without a browser copy suffix: the same rule as the canonical Package identity.</summary>
    public static string ArchiveStem(string fileName)
    {
        var name = fileName.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return CopySuffix().Replace(name, "").Trim();
    }

    private static string Key(string text) => CanonicalKeys.Normalize(text);

    [GeneratedRegex(@"\s*\(\d+\)$")]
    private static partial Regex CopySuffix();
}

/// <summary>Work type: Non-technical (functional and quality value) or Technical (migrations, refactoring, infrastructure, translation,
/// pipelines, developer tooling); Delivery Overview defaults to Non-technical.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeliveryWorkType { All, NonTechnical, Technical }

/// <summary>The second-level filter: work type, then (for non-technical work) one quality category. Classification is the shared
/// <see cref="FunctionalQualityClassifier.ClassifyFeature"/> plus canonical technical change intents — never a Delivery-only rule.</summary>
public sealed record DeliveryWorkFilter(DeliveryWorkType WorkType = DeliveryWorkType.All, QualityCategory? Quality = null)
{
    [JsonIgnore] public bool IsActive => WorkType != DeliveryWorkType.All || Quality is not null;

    public bool Includes(QualityCategory category) =>
        (WorkType switch
        {
            DeliveryWorkType.NonTechnical => category != QualityCategory.Technical,
            DeliveryWorkType.Technical => category == QualityCategory.Technical,
            _ => true,
        }) && (Quality is null || Quality == category);
}

/// <summary>One feature's work classification and why ("fq.technical.change-intent" = member of a technical change intent).</summary>
public sealed record DeliveryFeatureWork(string FeatureKey, QualityCategory Category, string Rule);

/// <summary>Work type distribution of the features selected by Source + Functional scope (before the work filter): "21 of 38 features".</summary>
public sealed record DeliveryWorkSummary(int Total, int NonTechnical, int Technical, Dictionary<QualityCategory, int> ByCategory, int Shown);

public sealed record DeliveryScopeSourceOption(string Id, string Name, string Provenance, int CurrentFeatures, int BaselineFeatures);

public sealed record DeliveryFunctionalScopeOption(string Id, string Name, FunctionalScopeKind Kind, string Detail, int CurrentFeatures, int BaselineFeatures,
    int PendingCandidates);

/// <summary>Why a feature is in the selected scope (shown as "Why included").</summary>
public sealed record DeliveryScopeMember(string FeatureKey, string Title, string Why);

/// <summary>The scope a frozen report was generated for: exact ids plus the labels and interpretation they had then (renames never change it).</summary>
public sealed record DeliveryReportScope
{
    public string? SourceId { get; init; }
    public string? SourceName { get; init; }
    public string? FunctionalScopeId { get; init; }
    public string? FunctionalScopeName { get; init; }
    public FunctionalScopeKind? FunctionalScopeKind { get; init; }
    /// <summary>Canonical interpretation the membership was read from (scope key and rule set), for lineage.</summary>
    public string? CanonicalScope { get; init; }
    public string? RuleSetVersion { get; init; }
    public List<string> CurrentFeatureKeys { get; init; } = [];
    public List<string> BaselineFeatureKeys { get; init; } = [];
    public bool SourceInBaseline { get; init; } = true;
    public bool ScopeInBaseline { get; init; } = true;
    /// <summary>Work type / quality category the report covers (null = all work).</summary>
    public DeliveryWorkType? WorkType { get; init; }
    public QualityCategory? Quality { get; init; }
    public string? ClassificationRuleSet { get; init; }

    [JsonIgnore] public string Label => string.Join(" · ", new[]
    {
        SourceName, FunctionalScopeName,
        WorkType switch { DeliveryWorkType.NonTechnical => "Non-technical", DeliveryWorkType.Technical => "Technical", _ => null },
        Quality is { } q ? FunctionalQualityClassifier.Label(q) : null,
    }.Where(x => !string.IsNullOrEmpty(x)));
}

/// <summary>The scoped Delivery view: options with scoped counts, the applied selection and its notices, and the scoped projection.</summary>
public sealed record DeliveryScopeView
{
    public CanonicalScopeFilter Filter { get; init; } = new();
    public List<DeliveryScopeSourceOption> Sources { get; init; } = [];
    public List<DeliveryFunctionalScopeOption> Scopes { get; init; } = [];
    /// <summary>A selected id that does not exist in this context ("Selected functional scope is not available in this snapshot.").</summary>
    public List<string> InvalidFilters { get; init; } = [];
    public List<string> Notices { get; init; } = [];
    public List<DeliveryScopeMember> Members { get; init; } = [];
    /// <summary>Candidate features of the selected functional scope awaiting a person's decision (not counted).</summary>
    public List<DeliveryScopeMember> Candidates { get; init; } = [];
    public DeliveryReportScope? Scope { get; init; }
    /// <summary>The scoped report (when the view reads a report) …</summary>
    public WeeklyDeliveryReport? Report { get; init; }
    /// <summary>… or the scoped snapshot (no report yet).</summary>
    public DeliverySnapshotDetail? Snapshot { get; init; }
    public string? CanonicalScope { get; init; }
    public string? InterpretationNote { get; init; }
    public DeliveryWorkFilter Work { get; init; } = new();
    public DeliveryWorkSummary? WorkSummary { get; init; }
    /// <summary>Work classification of every feature selected by Source + Functional scope.</summary>
    public List<DeliveryFeatureWork> Classifications { get; init; } = [];
}

/// <summary>
/// Projects a frozen report onto a feature set: rows, attention and scope changes of those features; every metric recomputed from the scoped
/// rows with the report builder's rules (a metric the projection cannot recompute is dropped, never left global). Report-level attention
/// (no feature) stays, labelled report-level by the consumer; comparison warnings are comparison-level by definition.
/// </summary>
public static class DeliveryReportScoping
{
    /// <summary>The story kind of a scenario heading (same value as the parser's DeliveryWorkItemRelationships.ScenarioKind).</summary>
    public const string ScenarioKind = "Scenario";

    public static WeeklyDeliveryReport Project(WeeklyDeliveryReport report, IReadOnlySet<string> currentKeys, IReadOnlySet<string> baselineKeys, DeliveryReportScope scope)
    {
        bool In(DeliveryStoryRow s) => s.RemovedFeature ? baselineKeys.Contains(s.FeatureKey) : currentKeys.Contains(s.FeatureKey);
        var stories = report.Stories.Where(In).ToList();
        var keys = stories.Select(s => s.FeatureKey).ToHashSet(StringComparer.Ordinal);
        var attention = report.Attention.Where(a => a.FeatureKey is null || keys.Contains(a.FeatureKey)).ToList();
        var scopeChanges = report.ScopeChanges.Where(c => keys.Contains(c.FeatureKey)).ToList();
        var scoped = report with
        {
            Stories = stories, Attention = attention, ScopeChanges = scopeChanges, ExecutiveSummary = [],
            NextWeekFocus = [.. report.NextWeekFocus.Select(f => f with { Features = [.. f.Features.Where(keys.Contains)] }).Where(f => f.Features.Count > 0)],
            Scope = scope,
        };
        return scoped with { Metrics = [.. report.Metrics.Select(m => Recompute(m, scoped)).OfType<DeliveryReportMetric>()] };
    }

    public static bool IsReportLevel(DeliveryAttentionItem item) => item.FeatureKey is null;

    /// <summary>The report builder's rule for each metric key, over the (scoped) report rows. Null = not recomputable from rows.</summary>
    public static DeliveryReportMetric? Recompute(DeliveryReportMetric metric, WeeklyDeliveryReport r)
    {
        var stories = r.Stories;
        var live = stories.Where(s => !s.RemovedFeature).ToList();
        var paired = stories.Where(s => !s.NewFeature && !s.RemovedFeature && s.Hierarchy is { Captured: true }).Select(s => s.Hierarchy!).ToList();
        var currentTasks = live.SelectMany(s => s.Hierarchy?.Tasks ?? []).Where(t => t.Change != DeliveryTaskChange.Removed).ToList();
        var liveStories = live.SelectMany(s => s.Hierarchy?.Stories ?? []).Where(x => x.Change != DeliveryItemChange.Removed && !x.Removed).ToList();
        int Rel(DeliveryTaskRelationship rel) => currentTasks.Count(t => t.Relationship == rel);
        int Status(DeliveryStatus st) => live.Count(s => s.CurrentStatus == st);
        int? value = metric.Key switch
        {
            "features-changed" => stories.Count(s => s.Changed),
            "moved-forward" => stories.Count(s => s.Movement is DeliveryMovement.Progressed or DeliveryMovement.Completed),
            "completed" => stories.Count(s => s.Movement == DeliveryMovement.Completed),
            "tasks-completed" => stories.Sum(s => s.TasksCompletedThisPeriod),
            "new-features" => stories.Count(s => s.NewFeature),
            "removed-features" => stories.Count(s => s.RemovedFeature),
            "specifications-changed" => stories.Count(s => s.SpecificationChanged && !s.NewFeature && !s.RemovedFeature),
            "stories-changed" => paired.Sum(h => h.StoriesAdded + h.StoriesRemoved + h.StoriesChanged),
            "criteria-changed" => paired.Sum(h => h.CriteriaAdded + h.CriteriaChanged + h.CriteriaRemoved),
            "tasks-added" => paired.Sum(h => h.TasksAdded),
            "tasks-removed" => paired.Sum(h => h.TasksRemoved),
            "tasks-reopened" => paired.Sum(h => h.TasksReopened),
            "inventory-features" => live.Count,
            "inventory-stories" => liveStories.Count(x => x.Kind != ScenarioKind),
            "inventory-scenarios" => liveStories.Count(x => x.Kind == ScenarioKind),
            "inventory-tasks" => live.Sum(s => s.CurrentTaskTotal),
            "inventory-checked" => live.Sum(s => s.CurrentTaskCompleted),
            "inventory-unchecked" => live.Sum(s => s.CurrentTaskTotal - s.CurrentTaskCompleted),
            "inventory-no-tasks" => live.Count(s => s.Evidence.TasksPath is null),
            "quality-scope-review" => r.ScopeChanges.Count,
            "quality-traceability" => r.Attention.Count(a => DeliveryAttentionCategories.Of(a.Kind) == DeliveryAttentionCategory.DataQuality),
            "tasks-explicit-story" => Rel(DeliveryTaskRelationship.ExplicitStory),
            "tasks-story-phase" => Rel(DeliveryTaskRelationship.StoryPhase),
            "tasks-shared" => Rel(DeliveryTaskRelationship.SharedFoundational),
            "tasks-functional-group" => Rel(DeliveryTaskRelationship.FunctionalGroup),
            "tasks-scenario-group" => Rel(DeliveryTaskRelationship.ScenarioGroup),
            "quality-removed-story-references" => Rel(DeliveryTaskRelationship.RemovedStoryReference),
            "quality-unresolved-references" => Rel(DeliveryTaskRelationship.UnresolvedReference),
            "quality-direction" => metric.Value, // a property of the comparison, not of features
            "ready-for-test" => Status(DeliveryStatus.ReadyForTest),
            "testing" => Status(DeliveryStatus.Testing),
            "blocked" => Status(DeliveryStatus.Blocked),
            "needs-clarification" => Status(DeliveryStatus.NeedsClarification),
            "attention" => r.Attention.Count,
            _ => null,
        };
        return value is { } v ? metric with { Value = v } : null;
    }
}
