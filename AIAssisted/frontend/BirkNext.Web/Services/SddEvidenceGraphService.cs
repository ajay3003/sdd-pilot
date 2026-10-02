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

/// <summary>One projection of the workspace SDD lifecycle used by Implementation Review, Requirements Traceability and Quality Review.</summary>
public sealed class SddEvidenceGraphService(IReviewContextProvider contexts)
{
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
    {
        var context = contexts.GetCurrent();
        var requirements = context?.GetRequirements() ?? [];
        var state = workspace.SddLifecycle;
        return requirements.Select(r => new SddRequirementGraphRow(
            r,
            state.Links.Where(x => x.FromId.Equals(r.Id, StringComparison.OrdinalIgnoreCase) && x.Relationship == "RequirementPlansTo" && x.Currentness == "Current").Select(x => x.ToId).Distinct().ToArray(),
            state.Links.Where(x => x.FromId.Equals(r.Id, StringComparison.OrdinalIgnoreCase) && x.Relationship == "RequirementDecomposedInto" && x.Currentness == "Current").Select(x => x.ToId).Distinct().ToArray(),
            state.ImplementationEvidence.Where(x => x.RequirementId.Equals(r.Id, StringComparison.OrdinalIgnoreCase)).ToArray(),
            state.TestEvidence.Where(x => x.RequirementId.Equals(r.Id, StringComparison.OrdinalIgnoreCase)).ToArray(),
            state.TestExecutions.Where(x => x.RequirementReferences.Contains(r.Id, StringComparer.OrdinalIgnoreCase) ||
                r.LinkedAcceptanceScenarios.Any(ac => x.AcceptanceCriterionReferences.Contains(ac.Id ?? ac.Title, StringComparer.OrdinalIgnoreCase))).ToArray(),
            state.Questions.Any(q => (q.Status is "Open" or "NeedsAnswer") && q.RequirementIds.Contains(r.Id, StringComparer.OrdinalIgnoreCase)))).ToArray();
    }

    public IReadOnlyList<SddQualityReviewFinding> QualityFindings(IWorkspaceSessionService workspace)
    {
        var rows = Build(workspace);
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
        return findings;
    }

    public string Fingerprint(IWorkspaceSessionService workspace)
    {
        var state = workspace.SddLifecycle;
        var payload = JsonSerializer.Serialize(new
        {
            Rows = Build(workspace).Select(row => new { Id = row.Requirement.Id, row.Requirement.Text, row.PlanReferences, row.TaskReferences }),
            state.Revisions, state.Links, state.ImplementationEvidence, state.TestEvidence, state.TestExecutions, state.SourceSnapshots, state.Questions, state.Decisions
        });
        return Fingerprint(payload);
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
                Confidence = "StronglySupported", Provenance = $"PersistedCodeLink:{codeLink.Origin}", ProviderVersion = snapshot.AnalyzerVersion.ToString(), Currentness = "Current",
                SourceValidation = "NotAssessed", StableKey = key, CurrentnessReason = "The CodeLink is explicit; target file/symbol could not be checked against a retained source file inventory."
            });
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
        if (state.SourceSnapshots.Any(x => x.SnapshotId == snapshot.Id.ToString())) return;
        state.SourceSnapshots.Add(new SddSourceSnapshotReference
        {
            SnapshotId = snapshot.Id.ToString(), Fingerprint = snapshot.Archive.Sha256, EnvironmentReference = environmentReference,
            AnalysisStatus = snapshot.Status.ToString(), AnalyzerVersion = snapshot.AnalyzerVersion.ToString(), AnalyzedAt = snapshot.AnalyzedAt,
            Limitations = snapshot.Limitations.ToList(), Currentness = "Current"
        });
        SourceSnapshotChanged(state, snapshot.Id, snapshot.Archive.Sha256);
    }

    private static readonly string[] ExecutionStates = ["NotExecuted", "Queued", "Running", "Completed", "Cancelled", "TimedOut", "ExecutionFailed", "Unknown"];
    private static readonly string[] Results = ["Passed", "Failed", "Skipped", "Inconclusive", "NotApplicable", "Unknown"];
    private static bool ContainsExplicitReference(string candidate, string id) =>
        candidate.Equals(id, StringComparison.OrdinalIgnoreCase) ||
        System.Text.RegularExpressions.Regex.IsMatch(candidate, $@"(?<![\w-]){System.Text.RegularExpressions.Regex.Escape(id)}(?![\w-])", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
