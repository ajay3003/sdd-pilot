using BirkNext.TestEvidence;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Records provider test evidence (source test definitions and imported executions) in the one workspace SDD lifecycle and answers questions
/// over it. Four separate dimensions: designed (specification), source-discovered (Source Analysis), executed (imported result) and result.
/// Only Confirmed/StronglySupported source references create requirement links; Inferred ones stay candidates. Executions are immutable:
/// a later run adds evidence, a source change marks evidence potentially stale, nothing rewrites an old result.
/// </summary>
public static class SddTestEvidenceService
{
    public const string RequirementTestedBySource = "RequirementTestedBySourceTest";
    public const string RequirementVerifiedBy = "RequirementVerifiedBy";
    public const string AcceptanceCriterionVerifiedBy = "AcceptanceCriterionVerifiedBy";

    private static void Ensure(SddLifecycleState state)
    {
        state.TestDefinitions ??= [];
        state.TestResultArtifacts ??= [];
        state.TestRuns ??= [];
        state.TestExecutions ??= [];
        state.Links ??= [];
    }

    private static bool Strong(TestEvidenceConfidence c) => c is TestEvidenceConfidence.Confirmed or TestEvidenceConfidence.StronglySupported;

    // ── Source test definitions ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Records the definitions of a snapshot's inventory and re-resolves earlier definitions of the same repository against it: unchanged → the old
    /// record becomes Historical; changed fingerprint or no longer found → PotentiallyStale, and executions correlated to it become PotentiallyStale.
    /// Results are never altered. Idempotent per snapshot.
    /// </summary>
    public static int RecordDefinitions(SddLifecycleState state, SourceTestInventory inventory, IEnumerable<string> requirementIds, IEnumerable<string> acceptanceCriterionIds)
    {
        Ensure(state);
        var snapshot = inventory.SnapshotId.ToString();
        if (state.TestDefinitions.Any(d => d.SourceSnapshotId == snapshot)) return 0;
        var byId = inventory.Definitions.GroupBy(d => d.TestDefinitionId).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var old in state.TestDefinitions.Where(d => d.Currentness == "Current" && d.RepositoryName == inventory.RepositoryName && d.SourceSnapshotId != snapshot))
        {
            if (byId.TryGetValue(old.Definition.TestDefinitionId, out var now))
            {
                var changed = now.SourceFingerprint != old.Definition.SourceFingerprint;
                old.Currentness = changed ? "PotentiallyStale" : "Historical";
                old.CurrentnessReason = changed ? $"The test source changed in snapshot {Short(snapshot)}." : $"Re-resolved unchanged in snapshot {Short(snapshot)}.";
                if (changed) MarkExecutions(state, old.Definition.TestDefinitionId, "The correlated test's source changed after this execution; the result is retained.");
            }
            else
            {
                old.Currentness = "PotentiallyStale";
                old.CurrentnessReason = $"The test no longer resolves in snapshot {Short(snapshot)}.";
                MarkExecutions(state, old.Definition.TestDefinitionId, "The correlated test no longer exists in the current source; the result is retained as history, not current verification.");
            }
        }
        foreach (var definition in inventory.Definitions)
            state.TestDefinitions.Add(new SddTestDefinitionEvidence
            {
                Definition = definition, SourceSnapshotId = snapshot, SourceSnapshotFingerprint = inventory.SnapshotFingerprint, RepositoryName = inventory.RepositoryName,
                DiscoveryProviderId = inventory.ProviderId, Currentness = "Current",
            });
        LinkDefinitions(state, inventory.Definitions, requirementIds, acceptanceCriterionIds);
        return inventory.Definitions.Count;
    }

    private static void MarkExecutions(SddLifecycleState state, string definitionId, string reason)
    {
        foreach (var execution in state.TestExecutions.Where(e => e.TestDefinitionId == definitionId && e.Currentness == "Current"))
        {
            execution.Currentness = "PotentiallyStale";
            execution.CurrentnessReason = reason;
        }
    }

    private static void LinkDefinitions(SddLifecycleState state, IEnumerable<SourceTestDefinition> definitions, IEnumerable<string> requirementIds, IEnumerable<string> acIds)
    {
        var requirements = requirementIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var criteria = acIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions)
            foreach (var reference in definition.References.Where(r => Strong(r.Confidence)))
                if (requirements.Contains(reference.Id) || criteria.Contains(reference.Id))
                    AddLink(state, reference.Id, definition.StableIdentity, RequirementTestedBySource, reference.Confidence.ToString(), $"SourceTestReference: {reference.Basis}");
    }

    // ── Import ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Records a previewed artifact: one artifact, one run, one immutable execution per result row. The same file (fingerprint) or the same
    /// provider run id is never imported twice; execution rows are additionally deduplicated by provider result id.</summary>
    public static SddTestImportSummary Import(SddLifecycleState state, TestResultArtifactPreview preview, IEnumerable<string> requirementIds, IEnumerable<string> acceptanceCriterionIds)
    {
        Ensure(state);
        if (preview.Status != TestResultImportStatus.Valid || preview.Run is null)
            throw new ArgumentException(preview.Error ?? "The artifact is not a valid test result file.");
        var requirements = requirementIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var criteria = acceptanceCriterionIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var duplicateOf = state.TestResultArtifacts.FirstOrDefault(a => a.Fingerprint == preview.Fingerprint);
        var sameRun = preview.Run.ProviderRunId.Length > 0 ? state.TestRuns.FirstOrDefault(r => r.ProviderId == preview.ProviderId && r.ProviderRunId == preview.Run.ProviderRunId) : null;
        if (duplicateOf is not null || sameRun is not null)
            return Summary(preview, alreadyImported: true, duplicates: preview.Executions.Count, requirementLinked: 0, acLinked: 0, runId: sameRun?.RunId,
                extraWarning: duplicateOf is not null ? "Already imported: this file was imported before." : "Already imported: this test run id was imported from another file.");

        var bound = preview.SourceBinding == TestSourceBinding.Provided && preview.CorrelationSnapshotId is not null;
        var artifact = new SddTestResultArtifact
        {
            FileName = preview.FileName, Format = preview.Format, Fingerprint = preview.Fingerprint, SizeBytes = preview.SizeBytes, ProviderId = preview.ProviderId,
            ProviderVersion = preview.ProviderVersion, BuildReference = preview.BuildReference, BuildBinding = preview.BuildBinding.ToString(),
            CommitReference = preview.CommitReference, CommitBinding = preview.CommitBinding.ToString(), EnvironmentReference = preview.EnvironmentReference,
            SourceSnapshotId = bound ? preview.CorrelationSnapshotId!.ToString() : null, SourceFingerprint = bound ? preview.CorrelationSnapshotFingerprint : null,
            SourceBinding = bound ? "Provided" : "Unknown", CorrelationSnapshotId = preview.CorrelationSnapshotId?.ToString(), RepositoryName = preview.CorrelationRepositoryName,
            Warnings = preview.Warnings.ToList(), Limitations = preview.Limitations.ToList(),
        };
        var run = new SddTestRunEvidence
        {
            ArtifactId = artifact.ArtifactId, ProviderId = preview.ProviderId, ProviderRunId = preview.Run.ProviderRunId, StartedAt = preview.Run.StartedAt, FinishedAt = preview.Run.FinishedAt,
            DurationMs = preview.Run.DurationMs, RunState = preview.Run.RunState, ProviderOutcome = preview.Run.ProviderOutcome, Counts = preview.Run.Counts,
            BuildReference = preview.BuildReference, EnvironmentReference = preview.EnvironmentReference, SourceSnapshotId = artifact.SourceSnapshotId,
            SourceBinding = artifact.SourceBinding, RepositoryName = preview.CorrelationRepositoryName, Messages = preview.Run.Messages.ToList(),
        };
        state.TestResultArtifacts.Add(artifact);
        state.TestRuns.Add(run);

        // The correlated definitions become source evidence of this workspace too (if the snapshot was not recorded already).
        var definitions = preview.MatchedDefinitions.ToDictionary(d => d.TestDefinitionId, StringComparer.Ordinal);
        if (preview.CorrelationSnapshotId is { } correlationSnapshot)
            foreach (var definition in preview.MatchedDefinitions.Where(d => !state.TestDefinitions.Any(x => x.Definition.TestDefinitionId == d.TestDefinitionId && x.SourceSnapshotId == correlationSnapshot.ToString())))
                state.TestDefinitions.Add(new SddTestDefinitionEvidence { Definition = definition, SourceSnapshotId = correlationSnapshot.ToString(),
                    SourceSnapshotFingerprint = preview.CorrelationSnapshotFingerprint ?? "", RepositoryName = preview.CorrelationRepositoryName, Currentness = "Current" });
        LinkDefinitions(state, preview.MatchedDefinitions, requirements, criteria);

        int duplicates = 0, requirementLinked = 0, acLinked = 0;
        foreach (var e in preview.Executions)
        {
            var providerResultId = $"{preview.Run.ProviderRunId}:{e.ProviderExecutionId}:{e.ProviderTestId}";
            if (state.TestExecutions.Any(x => x.ProviderId == preview.ProviderId && x.ProviderResultId == providerResultId)) { duplicates++; continue; }
            var definition = e.Correlation.State == TestCorrelationState.Confirmed && e.Correlation.TestDefinitionId is { } id ? definitions.GetValueOrDefault(id) : null;
            var strong = definition?.References.Where(r => Strong(r.Confidence)).ToList() ?? [];
            var requirementRefs = strong.Where(r => r.Kind == TestReferenceKind.Requirement || requirements.Contains(r.Id)).Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var acRefs = strong.Where(r => r.Kind == TestReferenceKind.AcceptanceCriterion || criteria.Contains(r.Id)).Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var candidates = definition?.References.Where(r => r.Confidence == TestEvidenceConfidence.Inferred && (requirements.Contains(r.Id) || criteria.Contains(r.Id)))
                .Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
            var testId = definition?.StableIdentity ?? e.FullyQualifiedName ?? (e.ClassName is null ? e.TestName : $"{e.ClassName}::{e.TestName}");
            var execution = new SddTestExecutionEvidence
            {
                TestId = testId, TestName = e.TestName, RequirementReferences = requirementRefs, AcceptanceCriterionReferences = acRefs, EvidenceKind = "Executed",
                ExecutionState = e.ExecutionState, Result = e.Result, ProviderId = preview.ProviderId, ResultSource = preview.FileName, ProviderResultId = providerResultId,
                EnvironmentReference = preview.EnvironmentReference, BuildReference = preview.BuildReference,
                SourceSnapshotId = artifact.SourceSnapshotId, SourceFingerprint = artifact.SourceFingerprint,
                StartedAt = e.StartedAt, FinishedAt = e.FinishedAt, ExecutedAt = e.FinishedAt ?? e.StartedAt ?? preview.Run.FinishedAt ?? preview.Run.StartedAt,
                Currentness = "Current", Fingerprint = $"{preview.Fingerprint}:{e.ProviderExecutionId}:{e.ProviderTestId}",
                RunId = run.RunId, ArtifactId = artifact.ArtifactId, FullyQualifiedTestName = e.FullyQualifiedName, DataRowLabel = e.DataRowLabel, ProviderOutcome = e.ProviderOutcome,
                DurationMs = e.DurationMs, ErrorMessage = e.ErrorMessage, StackTrace = e.StackTrace, TestDefinitionId = definition?.TestDefinitionId,
                CorrelationState = e.Correlation.State.ToString(), CorrelationBasis = e.Correlation.Basis, CandidateRequirementReferences = candidates,
                SourceCurrentness = bound ? "BoundToSnapshot" : "NotAssessed",
                CurrentnessReason = bound ? null : "Source version unknown: not verification of a specific source snapshot.",
            };
            state.TestExecutions.Add(execution);
            foreach (var reference in strong.Where(r => requirements.Contains(r.Id)))
                AddLink(state, reference.Id, testId, RequirementVerifiedBy, reference.Confidence.ToString(), $"{preview.ProviderId}: correlated source test reference ({reference.Basis})");
            foreach (var reference in strong.Where(r => criteria.Contains(r.Id)))
                AddLink(state, reference.Id, testId, AcceptanceCriterionVerifiedBy, reference.Confidence.ToString(), $"{preview.ProviderId}: correlated source test reference ({reference.Basis})");
            if (requirementRefs.Any(requirements.Contains)) requirementLinked++;
            if (acRefs.Any(criteria.Contains)) acLinked++;
        }
        return Summary(preview, false, duplicates, requirementLinked, acLinked, run.RunId, null);
    }

    private static SddTestImportSummary Summary(TestResultArtifactPreview preview, bool alreadyImported, int duplicates, int requirementLinked, int acLinked, Guid? runId, string? extraWarning)
    {
        var counts = preview.Run?.Counts ?? new TestResultCounts();
        var warnings = preview.Warnings.ToList();
        if (extraWarning is not null) warnings.Insert(0, extraWarning);
        return new SddTestImportSummary(alreadyImported, preview.FileName, preview.ProviderId, counts.Total, counts.Passed, counts.Failed, counts.Skipped + counts.NotExecuted,
            counts.Total - counts.Passed - counts.Failed - counts.Skipped - counts.NotExecuted,
            preview.Executions.Count(e => e.Correlation.State is TestCorrelationState.Unresolved or TestCorrelationState.NotAssessed),
            preview.Executions.Count(e => e.Correlation.State == TestCorrelationState.Ambiguous),
            requirementLinked, acLinked, duplicates, warnings, preview.Limitations, runId);
    }

    private static void AddLink(SddLifecycleState state, string from, string to, string relationship, string confidence, string provenance)
    {
        if (state.Links.Any(l => l.FromId.Equals(from, StringComparison.OrdinalIgnoreCase) && l.ToId == to && l.Relationship == relationship)) return;
        state.Links.Add(new SddTraceabilityLink { FromId = from, ToId = to, Relationship = relationship, Confidence = confidence, Provenance = provenance, Currentness = "Current", LastValidatedAt = DateTimeOffset.UtcNow });
    }

    // ── Queries ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Key that groups the executions of one logical test (a theory's rows share it).</summary>
    public static string TestKey(SddTestExecutionEvidence e) => e.TestDefinitionId ?? e.FullyQualifiedTestName ?? e.TestId;

    /// <summary>
    /// Current relevant result of one test. Stale/historical executions are excluded; executions bound to the currently selected source snapshot
    /// win; otherwise the latest run is used and the reason says the source version is unknown. All rows of the chosen run are returned (theory).
    /// </summary>
    public static SddCurrentTestResult CurrentResult(SddLifecycleState state, string testKey)
    {
        Ensure(state);
        var all = state.TestExecutions.Where(e => TestKey(e) == testKey).ToList();
        if (all.Count == 0) return new(null, "No execution evidence", "No imported run contains this test. That is not NotExecuted: no artifact proves it was intended to run.", []);
        var usable = all.Where(e => e.Currentness == "Current").ToList();
        if (usable.Count == 0) return new(null, "No current result", $"{all.Count} historical or potentially stale execution(s) retained; none is current.", []);
        var currentFingerprint = state.SourceSnapshots.FirstOrDefault(s => s.Currentness == "Current")?.Fingerprint;
        var bound = currentFingerprint is null ? [] : usable.Where(e => string.Equals(e.SourceFingerprint, currentFingerprint, StringComparison.OrdinalIgnoreCase)).ToList();
        var pool = bound.Count > 0 ? bound : usable;
        var latestRun = pool.Where(e => e.RunId is not null).GroupBy(e => e.RunId).OrderByDescending(g => g.Max(e => e.ExecutedAt ?? DateTimeOffset.MinValue)).FirstOrDefault();
        var rows = latestRun?.ToList() ?? [pool.OrderByDescending(e => e.ExecutedAt ?? DateTimeOffset.MinValue).First()];
        var result = TestEvidenceCorrelation.Aggregate(rows.Select(r => r.ExecutionState == "Completed" ? r.Result : r.ExecutionState));
        var reason = bound.Count > 0
            ? "Latest run bound to the currently selected source snapshot."
            : currentFingerprint is null ? "Latest imported run; no source snapshot is selected, so the source version is not assessed."
            : "Latest imported run; it is not bound to the selected source snapshot (source version unknown), so it is not current verification.";
        if (pool.Count > rows.Count) reason += $" {pool.Count - rows.Count} earlier execution(s) retained in history.";
        return new(rows[0], result, reason, rows);
    }

    public static IReadOnlyList<SddRequirementTestEvidenceRow> RequirementRows(SddLifecycleState state, IReadOnlyList<SddRequirementGraphRow> graph)
    {
        Ensure(state);
        return graph.Select(row =>
        {
            var acIds = row.Requirement.LinkedAcceptanceScenarios.Select(ac => ac.Id ?? ac.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool Mentions(SddTestDefinitionEvidence d, Func<TestEvidenceConfidence, bool> confidence) =>
                d.Definition.References.Any(r => confidence(r.Confidence) && (r.Id.Equals(row.Requirement.Id, StringComparison.OrdinalIgnoreCase) || acIds.Contains(r.Id)));
            var latest = state.TestDefinitions.Where(d => d.Currentness is "Current" or "PotentiallyStale").ToList();
            var source = latest.Where(d => Mentions(d, Strong)).ToList();
            var candidates = latest.Where(d => Mentions(d, c => c == TestEvidenceConfidence.Inferred) && !source.Contains(d)).ToList();
            var executions = row.Executions.ToList();
            var keys = executions.Select(TestKey).Concat(source.Select(d => d.Definition.TestDefinitionId)).Distinct(StringComparer.Ordinal).ToList();
            var current = keys.Select(k => (k, CurrentResult(state, k))).ToList();
            var currentness = executions.Any(e => e.Currentness == "PotentiallyStale") || source.Any(d => d.Currentness == "PotentiallyStale") ? "Potentially stale"
                : executions.Count > 0 && executions.All(e => e.SourceCurrentness == "NotAssessed") ? "Source version not assessed"
                : executions.Count == 0 ? "No execution evidence" : "Current";
            return new SddRequirementTestEvidenceRow(row.Requirement.Id, row.Requirement.Text, row.DesignedTests, source, candidates, executions, current, currentness);
        }).ToList();
    }

    /// <summary>
    /// Deterministic test findings for the shared Quality Review slice. Only a Confirmed correlation with a strong requirement reference makes a test
    /// "linked"; only a current result bound to the selected source counts as a current failure ("Failed"). Unknown source, stale evidence, missing
    /// executions and unconfirmed candidate links are review or coverage observations — never project failures.
    /// </summary>
    public static IReadOnlyList<SddQualityReviewFinding> QualityFindings(SddLifecycleState state, IReadOnlyList<SddRequirementGraphRow> graph)
    {
        Ensure(state);
        var findings = new List<SddQualityReviewFinding>();
        if (state.TestDefinitions.Count == 0 && !state.TestExecutions.Any(e => e.RunId is not null)) return findings;
        foreach (var row in RequirementRows(state, graph))
        {
            foreach (var (key, current) in row.CurrentResults)
            {
                if (current.Execution is not { } e) continue;
                var failed = current.RunExecutions.Any(x => x.ExecutionState == "Completed" && x.Result == "Failed");
                if (failed && e.SourceCurrentness == "BoundToSnapshot")
                    findings.Add(new("CurrentLinkedTestFailed", "Failed", row.RequirementId, $"{Name(e)} failed in its current run ({current.Result}). {current.Reason}", Reference(e)));
                else if (failed)
                    findings.Add(new("LinkedTestFailedSourceUnknown", "NeedsReview", row.RequirementId, $"{Name(e)} failed ({current.Result}) in a run whose source version is unknown; not a current-source result.", Reference(e)));
                else if (e.SourceCurrentness == "NotAssessed")
                    findings.Add(new("TestResultSourceUnknown", "Information", row.RequirementId, $"{Name(e)}: {current.Result}. The run is not bound to a source snapshot.", Reference(e)));
            }
            foreach (var definition in row.SourceTests.Where(d => d.Currentness == "Current" && !row.Executions.Any(e => e.TestDefinitionId == d.Definition.TestDefinitionId)))
                findings.Add(new("LinkedSourceTestWithoutExecutionEvidence", "NotAssessed", row.RequirementId,
                    $"{definition.Definition.FullyQualifiedName} references this requirement but no imported run contains it.", $"test-definition:{definition.Definition.TestDefinitionId}"));
            // One observation per requirement: informal mentions are review cues, not one finding per test.
            var candidates = row.CandidateSourceTests.Where(d => d.Currentness == "Current").ToList();
            if (candidates.Count > 0)
                findings.Add(new("RequirementTestLinkUnresolved", "NeedsReview", row.RequirementId,
                    $"{candidates.Count} source test(s) mention this requirement only informally (comments or strings), e.g. {candidates[0].Definition.FullyQualifiedName}; add an explicit reference (trait or structured comment) to link them.",
                    $"test-definition:{candidates[0].Definition.TestDefinitionId}"));
            foreach (var definition in row.SourceTests.Where(d => d.Currentness == "PotentiallyStale"))
                findings.Add(new("StaleSourceTestEvidence", "NeedsReview", row.RequirementId, definition.CurrentnessReason ?? "The source test may be stale.", $"test-definition:{definition.Definition.TestDefinitionId}"));
        }
        return findings;
    }

    private static string Name(SddTestExecutionEvidence e) => e.FullyQualifiedTestName ?? e.TestName;
    /// <summary>Exact evidence reference: execution, run and artifact ids (Quality Review links resolve it to the test detail).</summary>
    public static string Reference(SddTestExecutionEvidence e) => $"test-execution:{e.Id}";
    private static string Short(string id) => id.Length > 8 ? id[..8] : id;
}

/// <summary>Raised after test evidence changes (import, recorded definitions) so sibling views over the same lifecycle re-render.</summary>
public sealed class SddTestEvidenceNotifier
{
    public event Action? Changed;
    public void Notify() => Changed?.Invoke();
}
