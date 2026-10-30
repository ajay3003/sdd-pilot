using BirkNext.Applicability;
using BirkNext.Technology;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

// ── Evidence-aware review navigator ─────────────────────────────────────────────────────────────────────────────────────
//
// One model behind Recommended Workflow and the Dashboard: what each review needs, whether it applies, whether it can run now,
// whether it is required, and whether a human decision is recorded for the current evidence. The dimensions are independent:
// a review can be Optional + Ready or Recommended + Needs setup; Available never means reviewed, passed or required. Project
// context is the only universal prerequisite; documentation, source, runtime and change evidence are separate branches.

/// <summary>Evidence branch a review belongs to (its sidebar section, or runtime when it needs a configured target).</summary>
public enum ReviewSection { Documentation, Traceability, Source, Runtime, Change, QualityTesting, Extensions }

public enum ReviewApplicabilityState { Applicable, NotApplicable, Unknown }

/// <summary>Can the review run now? Only real engine prerequisites count — never another review's decision.</summary>
public enum ReviewReadiness { Ready, NeedsSetup, WaitingForEvidence, EvidenceChanged, NotApplicable }

/// <summary>Independent of readiness. RequiredByPolicy only with explicit policy evidence (none is configured today).</summary>
public enum ReviewRequiredness { RequiredByCapability, RequiredByPolicy, Recommended, Optional }

/// <summary>Human decision on the exact current evidence. Analysis existing is not a decision.</summary>
public enum ReviewDecisionState { NoDecisionTracked, NotReviewed, Reviewed, EvidenceChanged }

/// <summary>One review/capability as the navigator presents it.</summary>
public sealed record ReviewCapability(
    string Id, string DisplayName, ReviewSection Section, ReviewApplicabilityState Applicability, ReviewReadiness Readiness,
    ReviewRequiredness Requiredness, ReviewDecisionState Decision, string Route, IReadOnlyList<string> MissingPrerequisites,
    string Reason, string? Note = null, bool IsCore = false)
{
    public bool IsActionable => Applicability != ReviewApplicabilityState.NotApplicable && Readiness is ReviewReadiness.Ready or ReviewReadiness.EvidenceChanged;

    /// <summary>"Available", "Needs setup: Target Environment", "Reviewed", "Evidence changed — review again", "Not applicable".</summary>
    public string StatusLabel => Decision switch
    {
        ReviewDecisionState.Reviewed => "Reviewed",
        ReviewDecisionState.EvidenceChanged => "Evidence changed — review again",
        _ => Readiness switch
        {
            ReviewReadiness.Ready => "Available",
            ReviewReadiness.NeedsSetup => MissingPrerequisites.Count > 0 ? $"Needs setup: {string.Join(", ", MissingPrerequisites)}" : "Needs setup",
            ReviewReadiness.WaitingForEvidence => "Waiting for evidence",
            ReviewReadiness.EvidenceChanged => "Evidence changed",
            _ => Applicability == ReviewApplicabilityState.NotApplicable ? "Not applicable" : "Not available",
        },
    };

    public string RequirednessLabel => Requiredness switch
    {
        ReviewRequiredness.RequiredByPolicy => "Required by policy",
        ReviewRequiredness.RequiredByCapability => "Required",
        ReviewRequiredness.Recommended => "Recommended",
        _ => "Optional",
    };
}

/// <summary>A deterministic, explainable recommendation: reason code, why, and what it unlocks.</summary>
public sealed record WorkflowRecommendation(string ReasonCode, string Title, string Why, string Unlocks, string Route, string ActionLabel, string? StepKey = null);

public static class ReviewNavigator
{
    public const string TargetEnvironment = "Target Environment";
    public const string SourceBinding = "Source binding";
    public const string SourceSnapshot = "Source snapshot";

    public static string SectionLabel(ReviewSection section) => section switch
    {
        ReviewSection.Documentation => "Documentation reviews",
        ReviewSection.Traceability => "Traceability",
        ReviewSection.Source => "Source evidence",
        ReviewSection.Runtime => "Runtime & environment reviews",
        ReviewSection.Change => "Change & lifecycle evidence",
        ReviewSection.QualityTesting => "Quality & testing reviews",
        _ => "Extensions",
    };

    /// <summary>The review needs a configured browser or API target to execute (from its declared requirements, never its name).</summary>
    public static bool NeedsTarget(string? reviewId) =>
        reviewId is not null && ReviewCatalog.Find(reviewId)?.Requires.Any(c => c is Capability.BrowserTarget or Capability.ApiTarget) == true;

    /// <summary>Decision state of a manual review step: reviewed only for the exact current revisions; an earlier decision is evidence changed.</summary>
    public static ReviewDecisionState DecisionOf(WorkflowStepViewModel? step)
    {
        if (step is null || !WorkflowReadinessService.IsReviewGate(step)) return ReviewDecisionState.NoDecisionTracked;
        var review = ArtifactReviewPresentation.Of(step);
        return review.State == ArtifactReviewState.Stale ? ReviewDecisionState.EvidenceChanged
            : review.IsDecided ? ReviewDecisionState.Reviewed
            : ReviewDecisionState.NotReviewed;
    }

    /// <summary>
    /// Every capability the navigator knows, in sidebar order, built from the shared sources: the backend review steps (documents,
    /// traceability, decisions), the shared applicability evaluation (sidebar rows with a ReviewId) and the project inputs. Rows hidden by
    /// Feature & Menu Visibility are left out — never recommended, never counted.
    /// </summary>
    public static IReadOnlyList<ReviewCapability> Build(
        IReadOnlyList<WorkflowStepViewModel> steps, ProjectInputs inputs, SourceBindingState binding, ChangeProvenanceState change,
        Func<string, ReviewApplicability?> applicability, FeatureVisibilityService? features, bool generatedDocumentationPresent, bool comparableDocuments)
    {
        var capabilities = new List<ReviewCapability>();
        var targetReady = inputs.Target.Status == ProjectInputStatus.Ready;
        bool Visible(string route) => features is null
            || NavigationCatalog.Sections.SelectMany(s => s.Items).FirstOrDefault(i => string.Equals(i.Route, route, StringComparison.OrdinalIgnoreCase)) is not { } item
            || item.Visible(features);

        // Documentation and traceability: the backend review steps (artifact availability per Artifact Pack + decisions on exact revisions).
        foreach (var step in steps.Where(s => s.StepType is "Explorer" or "Analysis" && Visible(s.Route)))
        {
            var applies = step.Status != WorkflowStepStatus.NotApplicable;
            var decision = DecisionOf(step);
            var section = step.StepType == "Explorer" ? ReviewSection.Documentation : ReviewSection.Traceability;
            var missing = applies ? [] : step.RequiredArtifacts.Where(a => step.DisabledReason?.Contains(a, StringComparison.OrdinalIgnoreCase) == true).ToList();
            var readiness = !applies ? ReviewReadiness.NotApplicable
                : step.Status == WorkflowStepStatus.Locked ? ReviewReadiness.NeedsSetup
                : decision == ReviewDecisionState.EvidenceChanged ? ReviewReadiness.EvidenceChanged : ReviewReadiness.Ready;
            var note = step.Key == "ImplementationReview" ? "Recommended after Requirements Traceability, but can be run independently." : null;
            capabilities.Add(new(step.Key, step.Title, section, applies ? ReviewApplicabilityState.Applicable : ReviewApplicabilityState.NotApplicable,
                readiness, step.IsOptional ? ReviewRequiredness.Optional : ReviewRequiredness.Recommended, decision, step.Route,
                step.Status == WorkflowStepStatus.Locked && !string.IsNullOrWhiteSpace(step.DisabledReason) ? [step.DisabledReason] : missing,
                applies ? step.Description : $"Not present in the current Artifact Pack ({step.DisabledReason}).", note, IsCore: true));
        }

        // Source evidence: Source Analysis owns the snapshot; Implementation Evidence Review needs the pack bound to source.
        if (Visible(ProjectInputPresentation.SourceAnalysisRoute))
        {
            var sourceState = inputs.Source.Status;
            capabilities.Add(new("source-analysis", "Source Analysis", ReviewSection.Source,
                sourceState is ProjectInputStatus.Absent or ProjectInputStatus.Unknown ? ReviewApplicabilityState.Unknown : ReviewApplicabilityState.Applicable,
                sourceState switch { ProjectInputStatus.Ready => ReviewReadiness.Ready, ProjectInputStatus.NeedsAttention => ReviewReadiness.EvidenceChanged, _ => ReviewReadiness.WaitingForEvidence },
                ReviewRequiredness.Recommended, ReviewDecisionState.NoDecisionTracked, inputs.Source.Route, sourceState == ProjectInputStatus.Ready ? [] : [SourceSnapshot],
                inputs.Source.Detail, IsCore: true));
        }
        capabilities.Add(new("implementation-evidence", "Implementation Evidence Review", ReviewSection.Source,
            binding == SourceBindingState.NoSource ? ReviewApplicabilityState.Unknown : ReviewApplicabilityState.Applicable,
            binding switch { SourceBindingState.Established => ReviewReadiness.Ready, SourceBindingState.NotEstablished => ReviewReadiness.NeedsSetup, _ => ReviewReadiness.WaitingForEvidence },
            ReviewRequiredness.Recommended, ReviewDecisionState.NoDecisionTracked, WorkflowReadinessService.ImplementationEvidenceRoute,
            binding switch { SourceBindingState.Established => [], SourceBindingState.NotEstablished => [SourceBinding], _ => [SourceSnapshot] },
            "Assess documented requirements against explicit source-backed implementation and test evidence. A task marked complete, a source test or a filename match is not implementation proof.",
            IsCore: true));

        // Every sidebar review with shared applicability (sidebar order): section from the sidebar, runtime when it needs a target.
        foreach (var (section, item) in NavigationCatalog.Sections.SelectMany(s => s.Items.Select(i => (s, i))))
        {
            if (item.ReviewId is not { } id || capabilities.Any(c => c.Route == item.Route) || features is not null && !item.Visible(features)) continue;
            var a = applicability(id);
            var runtime = NeedsTarget(id);
            if (FeatureAvailabilityCatalog.IsUnavailable(item.FeatureId))
            {
                capabilities.Add(new(id, item.Label, section.Id == "source-review" ? ReviewSection.Source : runtime ? ReviewSection.Runtime : ReviewSection.QualityTesting,
                    ReviewApplicabilityState.NotApplicable, ReviewReadiness.NotApplicable, ReviewRequiredness.Optional, ReviewDecisionState.NoDecisionTracked,
                    item.Route, [], "Unavailable in this installation."));
                continue;
            }
            var mapped = section.Id switch
            {
                "document-review" => ReviewSection.Documentation,
                "traceability" => ReviewSection.Traceability,
                "source-review" => ReviewSection.Source,
                "extensions" => ReviewSection.Extensions,
                _ => runtime ? ReviewSection.Runtime : ReviewSection.QualityTesting,
            };
            var (appl, readiness) = a?.Status switch
            {
                ApplicabilityStatus.Applicable or ApplicabilityStatus.PartiallyApplicable =>
                    (ReviewApplicabilityState.Applicable, runtime && !targetReady ? ReviewReadiness.NeedsSetup : ReviewReadiness.Ready),
                ApplicabilityStatus.NeedsConfiguration => (ReviewApplicabilityState.Applicable, ReviewReadiness.NeedsSetup),
                ApplicabilityStatus.NeedsRefresh => (ReviewApplicabilityState.Applicable, ReviewReadiness.EvidenceChanged),
                ApplicabilityStatus.NotApplicable or ApplicabilityStatus.Unsupported => (ReviewApplicabilityState.NotApplicable, ReviewReadiness.NotApplicable),
                _ => (ReviewApplicabilityState.Unknown, ReviewReadiness.WaitingForEvidence),
            };
            IReadOnlyList<string> missing = readiness == ReviewReadiness.NeedsSetup && runtime && !targetReady ? [TargetEnvironment] : [];
            capabilities.Add(new(id, item.Label, mapped, appl, readiness, ReviewRequiredness.Optional, ReviewDecisionState.NoDecisionTracked,
                item.Route, missing, a?.Reason ?? ""));
        }

        // Optional documentation capabilities: applicable from their own evidence, never part of the core path. Generated Documentation
        // Health runs over the source snapshot, so it sits with the source reviews (as in the sidebar).
        if (Visible(NavigationCatalog.GeneratedDocumentationHealthRoute))
            capabilities.Add(new("generated-documentation-health", "Generated Documentation Health", ReviewSection.Source,
                generatedDocumentationPresent ? ReviewApplicabilityState.Applicable : ReviewApplicabilityState.NotApplicable,
                generatedDocumentationPresent ? ReviewReadiness.Ready : ReviewReadiness.NotApplicable, ReviewRequiredness.Optional,
                ReviewDecisionState.NoDecisionTracked, NavigationCatalog.GeneratedDocumentationHealthRoute, [],
                generatedDocumentationPresent ? "Generated documentation detected in the current project." : "No generated documentation in the current project."));
        if (Visible("documentation-drift"))
            capabilities.Add(new("documentation-drift", "Documentation Drift", ReviewSection.Documentation,
                comparableDocuments ? ReviewApplicabilityState.Applicable : ReviewApplicabilityState.NotApplicable,
                comparableDocuments ? ReviewReadiness.Ready : ReviewReadiness.NotApplicable, ReviewRequiredness.Optional,
                ReviewDecisionState.NoDecisionTracked, "documentation-drift", [],
                comparableDocuments ? "Comparable documentation is available (revisions or authored ↔ generated)." : "No comparable revisions yet."));
        if (change != ChangeProvenanceState.Disabled && Visible(WorkflowReadinessService.ChangeTraceabilityRoute))
            capabilities.Add(new("change-traceability", "Change Traceability", ReviewSection.Change,
                change == ChangeProvenanceState.Configured ? ReviewApplicabilityState.Applicable : ReviewApplicabilityState.Unknown,
                change == ChangeProvenanceState.Configured ? ReviewReadiness.Ready : ReviewReadiness.NeedsSetup, ReviewRequiredness.Optional,
                ReviewDecisionState.NoDecisionTracked, WorkflowReadinessService.ChangeTraceabilityRoute,
                change == ChangeProvenanceState.Configured ? [] : ["Azure DevOps (optional)"],
                "Trace Azure DevOps work items through pull requests, commits and changed files. Not configured is neutral."));
        return capabilities;
    }

    /// <summary>Why a recommendation is made and what it unlocks, from its reason code. Every recommendation is explainable.</summary>
    public static WorkflowRecommendation Explain(WorkflowStepViewModel step, string packLabel)
    {
        var (code, why, unlocks) = step.Key switch
        {
            WorkflowReadinessService.LoadWorkspaceKey or WorkflowReadinessService.LoadSampleProjectKey =>
                ("PROJECT_CONTEXT_MISSING", "No project context yet: every review reads a project, module or Artifact Pack.", "Every documentation, source and runtime review."),
            WorkflowReadinessService.ChooseArtifactsKey =>
                ("ARTIFACT_SELECTION_REQUIRED", step.Description, "Deterministic document review and traceability for the selected scope."),
            WorkflowReadinessService.RefreshSourceKey =>
                ("SOURCE_EVIDENCE_STALE", "The analyzed source snapshot is outdated for this project.", "Current source-backed reviews."),
            WorkflowReadinessService.EstablishSourceBindingKey =>
                ("SOURCE_BINDING_MISSING", $"Source Analysis has completed, but {packLabel} has no source scope yet.", "Implementation Evidence Review and source-backed requirement assessment."),
            WorkflowPaths.ChoosePathKey =>
                ("REVIEW_PATH_CHOICE", "Several review paths are open with the evidence available, and none is a prerequisite of the others.", "Only the work on the path you choose."),
            var key when key.StartsWith(WorkflowPaths.RuntimeSetupKeyPrefix, StringComparison.Ordinal) =>
                ("RUNTIME_SETUP_STEP", step.Description, WorkflowPaths.RuntimeUnlocks),
            var key when key.StartsWith("Path:", StringComparison.Ordinal) =>
                ("REVIEW_PATH_NEXT", step.Description, "The reviews on this path."),
            WorkflowReadinessService.CompleteTargetKey =>
                ("TARGET_REQUIRED_FOR_RUNTIME_REVIEW", step.Description, "Runtime reviews that execute or observe the deployed system."),
            var key when key.StartsWith(WorkflowReadinessService.OpenReviewKeyPrefix, StringComparison.Ordinal) =>
                ("REVIEW_APPLICABLE", step.Description, "Assessment of this review's evidence."),
            _ when step.Status == WorkflowStepStatus.Stale =>
                ("EVIDENCE_CHANGED", $"{step.Title} was reviewed on an earlier revision; the current evidence has not been reviewed.", "A current review decision for governance."),
            _ => ("REVIEW_DECISION_OPEN", $"{step.Title} is available and has no review decision on the current evidence.", "A recorded review decision for governance and readiness."),
        };
        return new(code, step.Title, why, unlocks, step.Route, step.ActionLabel, step.Key);
    }
}
