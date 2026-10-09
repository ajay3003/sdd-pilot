using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BirkNext.Integrations;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public sealed record SddCodeLinkSourceReference(string LinkId, string RequirementReference, string FilePath, string ScenarioKind, string Origin, DateTimeOffset CreatedAt);
public sealed record SddRequirementGraphRow(
    SemanticRequirement Requirement,
    IReadOnlyList<string> PlanReferences,
    IReadOnlyList<string> TaskReferences,
    IReadOnlyList<SddImplementationEvidence> Implementation,
    IReadOnlyList<SddTestEvidence> DesignedTests,
    IReadOnlyList<SddTestExecutionEvidence> Executions,
    bool NeedsClarification);

public sealed record SddLifecycleProjection(LifecycleProjectionMetadata Metadata, IReadOnlyList<SddRequirementGraphRow> Rows);

/// <summary>One projection of the workspace SDD lifecycle used by Implementation Review, Requirements Traceability and Quality Review.</summary>
public sealed class SddEvidenceGraphService(IReviewContextProvider contexts, IPlanAnalysisService? planAnalysis = null)
{
    public SddLifecycleProjection Project(IWorkspaceSessionService workspace, LifecycleProjectionMode mode = LifecycleProjectionMode.ActiveWorkspace, Guid? baselineId = null)
    {
        var limitations = new List<string>();
        IReadOnlyList<Guid> revisionIds = [];
        IReadOnlyList<string> fingerprints = [];
        Guid? resolvedBaselineId = null;
        DateTimeOffset? evidenceCutoff = null;
        ReviewContext? context;
        if (mode == LifecycleProjectionMode.ActiveWorkspace)
        {
            context = contexts.GetCurrent();
            var active = workspace.SddLifecycle.Revisions.Where(x => x.IsCurrentSelection).ToList();
            revisionIds = active.Select(x => x.RevisionId).ToList(); fingerprints = active.Select(x => x.Fingerprint).ToList();
        }
        else
        {
            var state = workspace.SddLifecycle;
            SddBaselineManifest? manifest;
            if (mode == LifecycleProjectionMode.AuthoritativeBaseline)
            {
                manifest = state.Baselines.FirstOrDefault(x => x.Status == "Current");
                if (manifest is null)
                {
                    var explicitBaselineRevisions = state.Revisions.Where(x => x.Authority == "Baseline").ToList();
                    if (explicitBaselineRevisions.Count == 0)
                    {
                        limitations.Add("No authoritative baseline selected; active workspace artifacts were used.");
                        context = contexts.GetCurrent();
                        return Projection(workspace, context, LifecycleProjectionMode.ActiveWorkspace, null, [], [], limitations);
                    }
                    limitations.Add("Baseline uses explicitly-authoritative role revisions; no aggregate manifest has been captured.");
                    revisionIds = explicitBaselineRevisions.Select(x => x.RevisionId).ToList();
                    resolvedBaselineId = null;
                    context = BuildFromRevisions(explicitBaselineRevisions, limitations);
                    return Projection(workspace, context, mode, resolvedBaselineId, revisionIds, explicitBaselineRevisions.Select(x => x.Fingerprint).ToList(), limitations);
                }
            }
            else
            {
                manifest = baselineId is null ? null : state.Baselines.FirstOrDefault(x => x.BaselineId == baselineId);
                if (manifest is null)
                {
                    limitations.Add("Requested historical baseline is unavailable.");
                    return Projection(workspace, null, mode, baselineId, [], [], limitations);
                }
            }
            var revisions = manifest.ArtifactRevisionIds.Select(id => state.Revisions.FirstOrDefault(x => x.RevisionId == id)).ToList();
            if (revisions.Any(x => x is null)) limitations.Add("One or more baseline artifact revisions are missing; projection is partial.");
            var found = revisions.OfType<SddArtifactRevision>().ToList();
            revisionIds = found.Select(x => x.RevisionId).ToList(); fingerprints = found.Select(x => x.Fingerprint).ToList();
            resolvedBaselineId = manifest.BaselineId;
            evidenceCutoff = manifest.CreatedAt;
            if (state.Questions.Count > 0 || state.Decisions.Count > 0)
                limitations.Add("Question and decision lifecycle state is not revisioned; the projection uses current persisted state and may not reconstruct historical resolution state.");
            context = BuildFromRevisions(found, limitations);
        }
        return Projection(workspace, context, mode, resolvedBaselineId, revisionIds, fingerprints, limitations, evidenceCutoff);
    }

    private SddLifecycleProjection Projection(IWorkspaceSessionService workspace, ReviewContext? context, LifecycleProjectionMode mode, Guid? baselineId,
        IReadOnlyList<Guid> ids, IReadOnlyList<string> fingerprints, IReadOnlyList<string> limitations, DateTimeOffset? evidenceCutoff = null) =>
        new(new(mode, baselineId, ids, fingerprints, DateTimeOffset.UtcNow, limitations), BuildRows(workspace, context, mode, evidenceCutoff));

    private ReviewContext? BuildFromRevisions(IReadOnlyList<SddArtifactRevision> revisions, List<string> limitations)
    {
        try
        {
            string Content(string role) => revisions.LastOrDefault(x => x.Role.Equals(role, StringComparison.OrdinalIgnoreCase))?.Content ?? "";
            var specText = Content(WorkspaceArtifactType.Specification.ToString());
            var planText = Content(WorkspaceArtifactType.Plan.ToString());
            var taskText = Content(WorkspaceArtifactType.Tasks.ToString());
            var specification = string.IsNullOrWhiteSpace(specText) ? new SpecificationSemanticModel() : SpecExplorerService.BuildSemanticModel(SpecExplorerService.Parse(specText), specText);
            var plan = string.IsNullOrWhiteSpace(planText) || planAnalysis is null ? new PlanSemanticModel() : PlanAnalysisService.BuildSemanticModel(planAnalysis.Parse(planText));
            var tasks = string.IsNullOrWhiteSpace(taskText) ? new TaskSemanticModel() : TaskExplorerService.BuildSemanticModel(TaskExplorerService.Parse(taskText));
            if (!string.IsNullOrWhiteSpace(planText) && planAnalysis is null) limitations.Add("Plan revision is bound, but plan parsing is unavailable in this graph service.");
            return ReviewContextFactory.Create(new(), specification, plan, tasks, new());
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
        {
            limitations.Add($"A baseline artifact could not be parsed ({ex.GetType().Name}); projection is partial.");
            return null;
        }
    }

    private static IReadOnlyList<SddRequirementGraphRow> BuildRows(IWorkspaceSessionService workspace, ReviewContext? context, LifecycleProjectionMode mode, DateTimeOffset? evidenceCutoff)
    {
        var state = workspace.SddLifecycle;
        return context?.GetRequirements().Select(r => new SddRequirementGraphRow(r,
            mode == LifecycleProjectionMode.ActiveWorkspace
                ? state.Links.Where(x => x.FromId.Equals(r.Id, StringComparison.OrdinalIgnoreCase) && x.Relationship == "RequirementPlansTo" && x.Currentness == "Current").Select(x => x.ToId).Distinct().ToArray()
                : context.GetLinkedPlans(r.Id).ToArray(),
            mode == LifecycleProjectionMode.ActiveWorkspace
                ? state.Links.Where(x => x.FromId.Equals(r.Id, StringComparison.OrdinalIgnoreCase) && x.Relationship == "RequirementDecomposedInto" && x.Currentness == "Current").Select(x => x.ToId).Distinct().ToArray()
                : context.GetLinkedTasks(r.Id).ToArray(),
            state.ImplementationEvidence.Where(x => x.RequirementId.Equals(r.Id, StringComparison.OrdinalIgnoreCase) && (evidenceCutoff is null || (x.RecordedAt ?? x.ObservedAt) is not { } recorded || recorded <= evidenceCutoff)).ToArray(),
            state.TestEvidence.Where(x => x.RequirementId.Equals(r.Id, StringComparison.OrdinalIgnoreCase) && (evidenceCutoff is null || x.Timestamp is null || x.Timestamp <= evidenceCutoff)).ToArray(),
            state.TestExecutions.Where(x => (evidenceCutoff is null || x.ExecutedAt is null || x.ExecutedAt <= evidenceCutoff) && (x.RequirementReferences.Contains(r.Id, StringComparer.OrdinalIgnoreCase) ||
                r.LinkedAcceptanceScenarios.Any(ac => x.AcceptanceCriterionReferences.Contains(ac.Id ?? ac.Title, StringComparer.OrdinalIgnoreCase)))).ToArray(),
            state.Questions.Any(q => (q.Status is "Open" or "NeedsAnswer") && q.RequirementIds.Contains(r.Id, StringComparer.OrdinalIgnoreCase)))).ToArray() ?? [];
    }
    public void SynchronizeCurrentArtifacts(IWorkspaceSessionService workspace)
    {
        var context = contexts.GetCurrent();
        var state = workspace.SddLifecycle;
        foreach (var requirement in context?.GetRequirements() ?? [])
        {
            foreach (var plan in context?.GetLinkedPlans(requirement.Id) ?? [])
                SddLifecycleReviewService.AddOrRefreshLink(state, requirement.Id, plan, "RequirementPlansTo", "StronglySupported");
            foreach (var task in context?.GetLinkedTasks(requirement.Id) ?? [])
                SddLifecycleReviewService.AddOrRefreshLink(state, requirement.Id, task, "RequirementDecomposedInto", "StronglySupported");
            foreach (var ac in requirement.LinkedAcceptanceScenarios)
            {
                var acId = ac.Id ?? ac.Title;
                SddLifecycleReviewService.AddOrRefreshLink(state, requirement.Id, acId, "TestDesignedFor", "StronglySupported");
                if (!state.TestEvidence.Any(x => x.RequirementId == requirement.Id && x.AcceptanceCriterionId == acId && x.State == "Designed"))
                    state.TestEvidence.Add(new SddTestEvidence { RequirementId = requirement.Id, AcceptanceCriterionId = acId, TestReference = acId, State = "Designed", Provenance = "SpecificationAcceptanceScenario" });
            }
        }
    }

    public IReadOnlyList<SddRequirementGraphRow> Build(IWorkspaceSessionService workspace)
        => Project(workspace).Rows;

    public IReadOnlyList<SddRequirementGraphRow> Build(IWorkspaceSessionService workspace, LifecycleProjectionMode mode, Guid? baselineId = null)
        => Project(workspace, mode, baselineId).Rows;

    public IReadOnlyList<SddQualityReviewFinding> QualityFindings(IWorkspaceSessionService workspace)
    {
        return QualityFindings(workspace, LifecycleProjectionMode.ActiveWorkspace);
    }

    public IReadOnlyList<SddQualityReviewFinding> QualityFindings(IWorkspaceSessionService workspace, LifecycleProjectionMode mode, Guid? baselineId = null)
    {
        var rows = Project(workspace, mode, baselineId).Rows;
        var findings = new List<SddQualityReviewFinding>();
        foreach (var row in rows)
        {
            if (row.NeedsClarification)
                findings.Add(new("UnresolvedQuestion", "Information", row.Requirement.Id, "An unresolved clarification affects this requirement.", row.Requirement.Id));
            if (row.Implementation.All(x => x.Currentness != "Current"))
                findings.Add(new("RequirementWithoutCurrentImplementationEvidence", "NotAssessed", row.Requirement.Id, "No current source-backed implementation evidence is linked.", row.Requirement.Id));
            if (row.Executions.All(x => x.Currentness != "Current"))
                findings.Add(new("RequirementWithoutCurrentExecutionEvidence", "NotAssessed", row.Requirement.Id, "No current executed-test evidence is linked.", row.Requirement.Id));
            foreach (var stale in row.Implementation.Where(x => x.Currentness == "PotentiallyStale"))
                findings.Add(new("StaleImplementationEvidence", "NeedsReview", row.Requirement.Id, stale.CurrentnessReason ?? "Source evidence may be stale.", stale.Id.ToString()));
            foreach (var stale in row.Executions.Where(x => x.Currentness == "PotentiallyStale"))
                findings.Add(new("StaleTestEvidence", "NeedsReview", row.Requirement.Id, stale.CurrentnessReason ?? "Test execution evidence may be stale.", stale.Id.ToString()));
        }
        findings.AddRange(SddTestEvidenceService.QualityFindings(workspace.SddLifecycle, rows));
        return findings;
    }

    public string Fingerprint(IWorkspaceSessionService workspace)
        => Fingerprint(workspace, LifecycleProjectionMode.ActiveWorkspace);

    public string Fingerprint(IWorkspaceSessionService workspace, LifecycleProjectionMode mode, Guid? baselineId = null)
    {
        var projection = Project(workspace, mode, baselineId);
        var payload = JsonSerializer.Serialize(new
        {
            Projection = new { projection.Metadata.Mode, projection.Metadata.BaselineId, projection.Metadata.ArtifactRevisionIds, projection.Metadata.ArtifactFingerprints, projection.Metadata.Limitations },
            Rows = projection.Rows
                .OrderBy(row => row.Requirement.Id, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Requirement.Id, StringComparer.Ordinal)
                .Select(row => new
                {
                    Requirement = new
                    {
                        row.Requirement.Id,
                        row.Requirement.Text,
                        row.Requirement.Category,
                        SuccessCriteria = row.Requirement.LinkedSuccessCriteria
                            .Select(item => new { item.Id, item.Text, Tasks = Sorted(item.LinkedTasks) })
                            .OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Id, StringComparer.Ordinal),
                        UserStories = row.Requirement.LinkedUserStories
                            .Select(item => new
                            {
                                item.Id, item.Title, item.Priority, item.Description, item.Why, item.IndependentTest,
                                AcceptanceScenarios = item.LinkedAcceptanceScenarios.Select(scenario => new
                                {
                                    Id = scenario.Id ?? scenario.Title, scenario.Title, scenario.Given, scenario.When, scenario.Then
                                }).OrderBy(scenario => scenario.Id, StringComparer.OrdinalIgnoreCase).ThenBy(scenario => scenario.Id, StringComparer.Ordinal)
                            })
                            .OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Id, StringComparer.Ordinal),
                        AcceptanceScenarios = row.Requirement.LinkedAcceptanceScenarios.Select(scenario => new
                        {
                            Id = scenario.Id ?? scenario.Title, scenario.Title, scenario.Given, scenario.When, scenario.Then
                        }).OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Id, StringComparer.Ordinal),
                        EdgeCases = row.Requirement.LinkedEdgeCases
                            .Select(item => new { item.Title, item.Description, RelatedRequirementIds = Sorted(item.RelatedRequirementIds) })
                            .OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Title, StringComparer.Ordinal),
                        Security = row.Requirement.LinkedSecurityConsiderations
                            .Select(item => new { item.Title, item.Description, AffectedRequirementIds = Sorted(item.AffectedRequirementIds) })
                            .OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Title, StringComparer.Ordinal),
                        ConstitutionRules = Sorted(row.Requirement.LinkedConstitutionRules),
                        Tasks = Sorted(row.Requirement.LinkedTasks),
                        ArchitectureDecisions = Sorted(row.Requirement.LinkedArchitectureDecisions),
                        DataEntities = Sorted(row.Requirement.LinkedDataEntities)
                    },
                    PlanReferences = Sorted(row.PlanReferences),
                    TaskReferences = Sorted(row.TaskReferences),
                    Implementation = row.Implementation.Select(item => new
                    {
                        item.Id, item.RequirementId, item.EvidenceType, item.Reference, item.SourceSnapshotId, item.SourceFingerprint,
                        item.SourceEvidenceId, item.ProviderId, item.ProviderVersion, item.ObservedAt, item.RecordedAt, item.FilePath,
                        item.SourceValidation, item.StableKey, item.CurrentnessReason, item.Confidence, item.Provenance, item.Currentness,
                        item.TargetValidation,
                        TargetResolutions = item.TargetResolutions.Select(target => new
                        {
                            target.OldSnapshotId, target.NewSnapshotId, target.OldFingerprint, target.NewFingerprint, target.ProviderId,
                            target.State, target.TargetPath, target.MatchedPath, target.ContentChanged, target.Reason
                        }).OrderBy(target => target.TargetPath, StringComparer.OrdinalIgnoreCase)
                          .ThenBy(target => target.TargetPath, StringComparer.Ordinal)
                    }).OrderBy(item => item.StableKey, StringComparer.Ordinal),
                    DesignedTests = row.DesignedTests.Select(item => new
                    {
                        item.Id, item.RequirementId, item.AcceptanceCriterionId, item.TestReference, item.State, item.Result,
                        item.Timestamp, item.Provenance, item.Currentness, item.CurrentnessReason
                    }).OrderBy(item => item.Id),
                    Executions = row.Executions.Select(item => new
                    {
                        item.Id, item.TestId, item.TestName, RequirementReferences = Sorted(item.RequirementReferences),
                        AcceptanceCriterionReferences = Sorted(item.AcceptanceCriterionReferences), item.EvidenceKind, item.ExecutionState,
                        item.Result, item.ProviderId, item.ResultSource, item.ProviderResultId, item.EnvironmentReference, item.BuildReference,
                        item.SourceSnapshotId, item.SourceFingerprint, item.StartedAt, item.FinishedAt, item.ExecutedAt, item.Currentness,
                        item.CurrentnessReason, item.Fingerprint, item.RunId, item.ArtifactId, item.FullyQualifiedTestName, item.DataRowLabel,
                        item.ProviderOutcome, item.DurationMs, item.ErrorMessage, item.StackTrace, item.TestDefinitionId, item.CorrelationState,
                        item.CorrelationBasis, CandidateRequirementReferences = Sorted(item.CandidateRequirementReferences), item.SourceCurrentness
                    }).OrderBy(item => item.Id),
                    row.NeedsClarification
                })
        });
        return Fingerprint(payload);

        static IReadOnlyList<string> Sorted(IEnumerable<string> values) => values.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value, StringComparer.Ordinal).ToArray();
    }

    public static int ImportExecutions(SddLifecycleState state, IEnumerable<SddTestExecutionImportRecord> records)
    {
        var added = 0;
        foreach (var record in records)
        {
            if (string.IsNullOrWhiteSpace(record.TestId) || string.IsNullOrWhiteSpace(record.ResultSource))
                throw new ArgumentException("Each test result needs a TestId and ResultSource.");
            if (!ExecutionStates.Contains(record.ExecutionState, StringComparer.Ordinal) || !Results.Contains(record.Result, StringComparer.Ordinal))
                throw new ArgumentException($"Unsupported execution state/result for test '{record.TestId}'.");
            if (record.ExecutionState == "ExecutionFailed" && record.Result != "Unknown")
                throw new ArgumentException("A provider execution failure must use Result=Unknown.");
            var fingerprint = Fingerprint(JsonSerializer.Serialize(record));
            if (state.TestExecutions.Any(x => (record.ProviderResultId is not null && x.ProviderId == (record.ProviderId ?? "ManualImport") && x.ProviderResultId == record.ProviderResultId) || x.Fingerprint == fingerprint)) continue;
            state.TestExecutions.Add(new SddTestExecutionEvidence
            {
                TestId = record.TestId.Trim(), TestName = record.TestName.Trim(),
                RequirementReferences = record.RequirementReferences.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                AcceptanceCriterionReferences = record.AcceptanceCriterionReferences.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                ExecutionState = record.ExecutionState, Result = record.Result, ProviderId = record.ProviderId ?? "ManualImport",
                ResultSource = record.ResultSource.Trim(), ProviderResultId = record.ProviderResultId, EnvironmentReference = record.EnvironmentReference,
                BuildReference = record.BuildReference, SourceSnapshotId = record.SourceSnapshotId, SourceFingerprint = record.SourceFingerprint,
                StartedAt = record.StartedAt, FinishedAt = record.FinishedAt, ExecutedAt = record.ExecutedAt,
                Fingerprint = fingerprint
            });
            foreach (var req in record.RequirementReferences.Distinct(StringComparer.OrdinalIgnoreCase))
                SddLifecycleReviewService.AddOrRefreshLink(state, req, record.TestId, "RequirementVerifiedBy", "Confirmed");
            foreach (var ac in record.AcceptanceCriterionReferences.Distinct(StringComparer.OrdinalIgnoreCase))
                SddLifecycleReviewService.AddOrRefreshLink(state, ac, record.TestId, "AcceptanceCriterionVerifiedBy", "Confirmed");
            added++;
        }
        return added;
    }

    public static int BindCodeLinks(SddLifecycleState state, IqrSourceSnapshot snapshot, IEnumerable<SddCodeLinkSourceReference> codeLinks, IEnumerable<string> requirementIds)
    {
        var known = requirementIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fingerprint = snapshot.Archive.Sha256;
        var added = 0;
        foreach (var codeLink in codeLinks.Where(x => x.ScenarioKind.Equals("Requirement", StringComparison.OrdinalIgnoreCase)))
        {
            var reqRef = known.FirstOrDefault(id => ContainsExplicitReference(codeLink.RequirementReference, id));
            if (reqRef is null) continue;
            var key = $"codelink:{codeLink.LinkId}:{snapshot.Id}";
            if (state.ImplementationEvidence.Any(x => x.StableKey == key)) continue;
            var previous = state.ImplementationEvidence.Where(x => x.StableKey?.StartsWith($"codelink:{codeLink.LinkId}:", StringComparison.Ordinal) == true && x.Currentness == "Current").ToList();
            if (previous.Any(x => x.SourceFingerprint == fingerprint)) continue;
            foreach (var old in previous)
            {
                if (old.SourceFingerprint == fingerprint) continue;
                old.Currentness = "PotentiallyStale";
                old.SourceValidation = "PotentiallyStale";
                old.CurrentnessReason = "A different Source Analysis archive fingerprint is selected; this snapshot does not retain a complete file inventory for deterministic target re-resolution.";
            }
            state.ImplementationEvidence.Add(new SddImplementationEvidence
            {
                RequirementId = reqRef, EvidenceType = "CodeLink", Reference = codeLink.FilePath,
                SourceSnapshotId = snapshot.Id.ToString(), SourceFingerprint = fingerprint, SourceEvidenceId = codeLink.LinkId,
                ProviderId = "CodeTraceability", ObservedAt = snapshot.AnalyzedAt, FilePath = codeLink.FilePath,
                RecordedAt = DateTimeOffset.UtcNow,
                Confidence = "StronglySupported", Provenance = $"PersistedCodeLink:{codeLink.Origin}", ProviderVersion = snapshot.AnalyzerVersion.ToString(), Currentness = "Current",
                SourceValidation = "NotAssessed", TargetValidation = "NotAssessed", StableKey = key,
                CurrentnessReason = "The CodeLink is explicit; source target resolution has not been assessed."
            });
            var evidence = state.ImplementationEvidence[^1];
            if (snapshot.TargetIndex is { } index)
            {
                var target = ResolveFileTarget(index, codeLink.FilePath);
                evidence.TargetValidation = target.State;
                evidence.SourceValidation = target.State;
                evidence.CurrentnessReason = target.Reason;
                target.OldSnapshotId = snapshot.Id.ToString(); target.NewSnapshotId = snapshot.Id.ToString();
                target.OldFingerprint = fingerprint; target.NewFingerprint = fingerprint;
                evidence.TargetResolutions.Add(target);
            }
            SddLifecycleReviewService.AddOrRefreshLink(state, reqRef, codeLink.FilePath, "RequirementImplementedBy", "StronglySupported");
            added++;
        }
        return added;
    }

    public static void SourceSnapshotChanged(SddLifecycleState state, Guid currentSnapshotId, string currentFingerprint)
    {
        foreach (var reference in state.SourceSnapshots.Where(x => x.Currentness == "Current" && x.SnapshotId != currentSnapshotId.ToString()))
            reference.Currentness = "Historical";
        var selected = state.SourceSnapshots.FirstOrDefault(x => x.SnapshotId == currentSnapshotId.ToString());
        if (selected is not null) selected.Currentness = "Current";
        foreach (var evidence in state.ImplementationEvidence.Where(x => x.ProviderId == "CodeTraceability" && x.Currentness == "Current" && x.SourceSnapshotId != currentSnapshotId.ToString()))
        {
            if (evidence.SourceFingerprint == currentFingerprint) continue;
            if (evidence.TargetResolutions.Any(x => x.NewSnapshotId == currentSnapshotId.ToString())) continue;
            evidence.Currentness = "PotentiallyStale";
            evidence.SourceValidation = "PotentiallyStale";
            evidence.CurrentnessReason = "A newer source snapshot is selected; target re-resolution is unavailable from the retained Source Analysis projection.";
        }
        foreach (var execution in state.TestExecutions.Where(x => x.Currentness == "Current" &&
                     x.SourceSnapshotId is not null && x.SourceSnapshotId != currentSnapshotId.ToString() &&
                     !string.Equals(x.SourceFingerprint, currentFingerprint, StringComparison.OrdinalIgnoreCase)))
        {
            execution.Currentness = "PotentiallyStale";
            execution.CurrentnessReason = "The executed test is bound to a different source snapshot fingerprint.";
        }
    }

    public static void RecordSourceSnapshot(SddLifecycleState state, IqrSourceSnapshot snapshot, string environmentReference)
    {
        var existing = state.SourceSnapshots.FirstOrDefault(x => x.SnapshotId == snapshot.Id.ToString());
        if (existing is not null)
        {
            // Newer Source Analysis may have persisted a target index after this workspace first recorded the snapshot.
            // Enrich only the normalized index/provenance; snapshot identity and prior evidence remain immutable.
            if (existing.TargetIndex is null && snapshot.TargetIndex is not null)
            {
                existing.TargetIndex = snapshot.TargetIndex;
                existing.AnalyzerVersion = snapshot.AnalyzerVersion.ToString();
                existing.Limitations = snapshot.Limitations.ToList();
            }
            SourceSnapshotChanged(state, snapshot, environmentReference);
            return;
        }
        state.SourceSnapshots.Add(new SddSourceSnapshotReference
        {
            SnapshotId = snapshot.Id.ToString(), Fingerprint = snapshot.Archive.Sha256, EnvironmentReference = environmentReference,
            AnalysisStatus = snapshot.Status.ToString(), AnalyzerVersion = snapshot.AnalyzerVersion.ToString(), AnalyzedAt = snapshot.AnalyzedAt,
            Limitations = snapshot.Limitations.ToList(), Currentness = "Current", TargetIndex = snapshot.TargetIndex
        });
        SourceSnapshotChanged(state, snapshot, environmentReference);
    }

    public static void SourceSnapshotChanged(SddLifecycleState state, IqrSourceSnapshot snapshot, string environmentReference = "")
    {
        foreach (var reference in state.SourceSnapshots.Where(x => x.Currentness == "Current" && x.SnapshotId != snapshot.Id.ToString())) reference.Currentness = "Historical";
        var currentRef = state.SourceSnapshots.FirstOrDefault(x => x.SnapshotId == snapshot.Id.ToString());
        if (currentRef is not null) currentRef.Currentness = "Current";
        foreach (var evidence in state.ImplementationEvidence.Where(x => x.ProviderId == "CodeTraceability" && x.SourceSnapshotId != snapshot.Id.ToString()))
        {
            if (evidence.TargetResolutions.Any(r => r.NewSnapshotId == snapshot.Id.ToString())) continue;
            var old = state.SourceSnapshots.FirstOrDefault(x => x.SnapshotId == evidence.SourceSnapshotId);
            var resolution = old?.TargetIndex is null || snapshot.TargetIndex is null
                ? new SddSourceTargetResolution { OldSnapshotId = evidence.SourceSnapshotId ?? "", NewSnapshotId = snapshot.Id.ToString(), OldFingerprint = evidence.SourceFingerprint ?? "", NewFingerprint = snapshot.Archive.Sha256, TargetPath = evidence.FilePath ?? "", State = old is null ? "SnapshotUnavailable" : "NotAssessed", Reason = old is null ? "The source snapshot referenced by this evidence is unavailable." : "One or both snapshots do not contain a target index." }
                : ResolveFileTarget(old.TargetIndex, evidence.FilePath ?? "", old.SnapshotId, old.Fingerprint, snapshot.TargetIndex, snapshot.Id.ToString(), snapshot.Archive.Sha256);
            evidence.TargetResolutions.Add(resolution);
            evidence.TargetValidation = resolution.State;
            evidence.SourceValidation = resolution.State;
            switch (resolution.State)
            {
                case "ResolvedExact" when resolution.ContentChanged == false:
                    if (evidence.Currentness == "Current")
                        evidence.CurrentnessReason = "The exact referenced file path and content fingerprint remain in the newer source snapshot. This does not verify implementation behavior.";
                    else
                        evidence.CurrentnessReason = "The source target resolves exactly in the newer snapshot, but prior evidence was already stale for another reason; target resolution does not reconfirm the traceability claim.";
                    break;
                case "ResolvedExact":
                case "ResolvedEquivalent":
                case "NotFound":
                case "Ambiguous":
                    evidence.Currentness = "PotentiallyStale";
                    evidence.CurrentnessReason = resolution.Reason;
                    break;
                default:
                    evidence.CurrentnessReason = resolution.Reason;
                    break;
            }
        }
        SourceSnapshotChanged(state, snapshot.Id, snapshot.Archive.Sha256);
    }

    public static SddSourceTargetResolution ResolveFileTarget(SourceTargetIndex current, string path) =>
        ResolveFileTarget(current, path, current.SnapshotId.ToString(), current.SnapshotFingerprint, current, current.SnapshotId.ToString(), current.SnapshotFingerprint);

    public static SddSourceTargetResolution ResolveFileTarget(SourceTargetIndex oldIndex, string path, string oldId, string oldFingerprint,
        SourceTargetIndex newIndex, string newId, string newFingerprint)
    {
        var result = new SddSourceTargetResolution { OldSnapshotId = oldId, NewSnapshotId = newId, OldFingerprint = oldFingerprint,
            NewFingerprint = newFingerprint, ProviderId = newIndex.ProviderId, TargetPath = path, ResolvedAt = DateTimeOffset.UtcNow };
        if (!oldIndex.Files.Any(x => NormalizePath(x.RelativePath) == NormalizePath(path)))
        { result.State = "NotAssessed"; result.Reason = "The target path is not present in the source snapshot bound to this link."; return result; }
        var exact = newIndex.Files.Where(x => NormalizePath(x.RelativePath) == NormalizePath(path)).ToList();
        if (exact.Count == 1)
        {
            var oldTarget = oldIndex.Files.First(x => NormalizePath(x.RelativePath) == NormalizePath(path)); var target = exact[0];
            result.State = "ResolvedExact"; result.MatchedPath = target.RelativePath;
            result.ContentChanged = oldTarget.ContentFingerprint is null || target.ContentFingerprint is null
                ? null : oldTarget.ContentFingerprint != target.ContentFingerprint;
            result.Reason = result.ContentChanged == true ? "The file path still resolves, but its content fingerprint changed; review the implementation evidence." : "The exact file path resolves in the newer snapshot; target location only, not implementation correctness.";
            return result;
        }
        if (exact.Count > 1) { result.State = "Ambiguous"; result.Reason = "Multiple normalized paths match the target."; return result; }
        var oldFile = oldIndex.Files.First(x => NormalizePath(x.RelativePath) == NormalizePath(path));
        if (oldFile.ContentFingerprint is not null)
        {
            var equivalents = newIndex.Files.Where(x => x.ContentFingerprint == oldFile.ContentFingerprint).ToList();
            if (equivalents.Count == 1) { result.State = "ResolvedEquivalent"; result.MatchedPath = equivalents[0].RelativePath; result.ContentChanged = false; result.Reason = "A unique file with identical content fingerprint resolves at a different path; review the moved target."; return result; }
            if (equivalents.Count > 1) { result.State = "Ambiguous"; result.Reason = "Multiple files share the prior target fingerprint."; return result; }
        }
        if (!newIndex.FileInventoryComplete) { result.State = "Unsupported"; result.Reason = "The new snapshot does not have exhaustive file-path coverage, so absence cannot be established."; return result; }
        result.State = "NotFound"; result.Reason = "The target path was not found in the exhaustive non-ignored file inventory. This does not establish that the requirement is unimplemented."; return result;
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('/');

    private static readonly string[] ExecutionStates = ["NotExecuted", "Queued", "Running", "Completed", "Cancelled", "TimedOut", "ExecutionFailed", "Unknown"];
    private static readonly string[] Results = ["Passed", "Failed", "Skipped", "Inconclusive", "NotApplicable", "Unknown"];
    private static bool ContainsExplicitReference(string candidate, string id) =>
        candidate.Equals(id, StringComparison.OrdinalIgnoreCase) ||
        System.Text.RegularExpressions.Regex.IsMatch(candidate, $@"(?<![\w-]){System.Text.RegularExpressions.Regex.Escape(id)}(?![\w-])", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
