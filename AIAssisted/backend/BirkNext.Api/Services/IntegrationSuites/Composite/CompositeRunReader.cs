using BirkNext.ExecutionEvidence;
using BirkNext.IntegrationSuites;

namespace BirkNext.Api.Services.IntegrationSuites.Composite;

/// <summary>
/// Reads one composite run from its Execution Evidence (pure): stage states from the calls of the plan's steps, boundary correlation from the
/// recorded outputs and resolved inputs (an upstream identifier carried into the downstream request, and — where the downstream response echoes
/// it — compared), and the verdict. FULL_E2E_VERIFIED needs a full-E2E run in which every stage succeeded and every boundary is carried or
/// confirmed; a timeout is "not observed", never "failed"; an unanswered data-changing call is UNKNOWN_OUTCOME and is never re-sent.
/// </summary>
public static class CompositeRunReader
{
    public static CompositeRunView Read(Guid runId, Guid suiteId, int suiteRevision, DateTimeOffset startedAt, string initiator, CompositeRunKind kind, List<string> stages,
        List<string> stepIds, CompositePlan plan, List<SuiteSourceBinding> sources, List<string> mandatory, ExecutionRunDetail? detail)
    {
        var calls = (detail?.Calls ?? []).Where(c => c.Definition.StepId is not null).GroupBy(c => c.Definition.StepId!).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var stageRuns = new List<CompositeStageRun>();
        var labels = plan.Steps.ToDictionary(s => s.StepId, s => s, StringComparer.Ordinal);
        foreach (var stage in stages)
        {
            var steps = plan.Steps.Where(s => s.StageId == stage && stepIds.Contains(s.StepId)).ToList();
            if (steps.Count == 0) { stageRuns.Add(new(stage, stage, CompositeStageState.NotObservable, [], 0, "No step of the plan executes or observes this stage.")); continue; }
            var states = steps.Select(s => State(s, calls.GetValueOrDefault(s.StepId))).ToList();
            var worst = states.OrderByDescending(x => Rank(x.State)).First();
            stageRuns.Add(new(stage, stage, worst.State, [.. steps.Select(s => calls.GetValueOrDefault(s.StepId)?.CallId).OfType<Guid>()],
                steps.Sum(s => calls.GetValueOrDefault(s.StepId)?.Attempts.Count ?? 0), worst.Detail));
        }
        var correlation = new List<CompositeCorrelationLink>();
        foreach (var step in plan.Steps.Where(s => stepIds.Contains(s.StepId) && s.FromStep is not null && s.FromField is not null))
        {
            var boundary = $"{(labels.TryGetValue(step.FromStep!, out var from) ? from.StageId : step.FromStep)} → {step.StageId}";
            var upstream = calls.GetValueOrDefault(step.FromStep!);
            var downstream = calls.GetValueOrDefault(step.StepId);
            var output = CompositePlanner.Output(step.FromField!);
            var produced = upstream?.Outputs.FirstOrDefault(o => o.Name == output);
            if (produced is not { Found: true })
            {
                correlation.Add(new(boundary, step.FromStep!, step.StepId, step.FromField!, CorrelationState.Missing, null,
                    upstream is null ? "The upstream step was not executed." : $"The upstream response did not provide {step.FromField}: {produced?.Problem ?? "not extracted"}."));
                continue;
            }
            var sent = downstream?.Definition.ResolvedValues.FirstOrDefault(v => v.Name == $"steps.{step.FromStep}.{output}");
            if (downstream is null || sent is null || downstream.State is ExecutionState.Blocked or ExecutionState.Skipped or ExecutionState.Planned or ExecutionState.Queued)
            {
                correlation.Add(new(boundary, step.FromStep!, step.StepId, step.FromField!, CorrelationState.NotObserved, produced.Preview, "The downstream step did not send the identifier."));
                continue;
            }
            if (sent.Preview != produced.Preview)
            {
                correlation.Add(new(boundary, step.FromStep!, step.StepId, step.FromField!, CorrelationState.WrongCorrelation, produced.Preview, $"Sent {sent.Preview}, produced {produced.Preview}."));
                continue;
            }
            if (downstream.State != ExecutionState.Passed)
            {
                correlation.Add(new(boundary, step.FromStep!, step.StepId, step.FromField!, CorrelationState.NotObserved, produced.Preview,
                    $"Carried into {step.Parameter}, but the downstream step did not observe it ({downstream.State})."));
                continue;
            }
            if (step.EchoField is { } echo)
            {
                var echoed = downstream.Outputs.FirstOrDefault(o => o.Name == CompositePlanner.EchoOutput(echo));
                correlation.Add(echoed is { Found: true } && echoed.Preview == produced.Preview
                    ? new(boundary, step.FromStep!, step.StepId, step.FromField!, CorrelationState.Confirmed, produced.Preview, $"{step.Parameter} = {step.FromField} of {step.FromStep}; the response echoes {echo} = the same value.")
                    : new(boundary, step.FromStep!, step.StepId, step.FromField!, CorrelationState.WrongCorrelation, produced.Preview,
                        echoed is { Found: true } ? $"The response echoes {echo} = {echoed.Preview}, not {produced.Preview}." : $"The response does not echo {echo} ({echoed?.Problem ?? "missing"})."));
                continue;
            }
            correlation.Add(new(boundary, step.FromStep!, step.StepId, step.FromField!, CorrelationState.Carried, produced.Preview,
                $"{step.Parameter} = {step.FromField} of {step.FromStep}; the downstream record was found by it (no echo field documented)."));
        }
        var (verdict, reason) = Verdict(kind, stageRuns, correlation, mandatory, detail);
        return new CompositeRunView
        {
            RunId = runId, SuiteId = suiteId, SuiteRevision = suiteRevision, Kind = kind, Verdict = verdict, VerdictReason = reason, StartedAt = startedAt, Initiator = initiator,
            ExecutionState = detail?.Run.State.ToString() ?? "Evidence not found", Stages = stageRuns, Correlation = correlation, Sources = sources, ChainRef = detail?.Run.ChainRef,
        };
    }

    private static int Rank(CompositeStageState s) => s switch
    {
        CompositeStageState.Failed => 9, CompositeStageState.UnknownOutcome => 8, CompositeStageState.TimedOut => 7, CompositeStageState.Blocked => 6,
        CompositeStageState.Observing => 5, CompositeStageState.Waiting => 4, CompositeStageState.NotRun => 3, CompositeStageState.NotObservable => 2, _ => 0,
    };

    public static (CompositeStageState State, string Detail) State(CompositePlanStep step, ExecutionCallRecord? call)
    {
        if (call is null) return (CompositeStageState.NotRun, "Not part of the recorded run.");
        var last = call.Attempts.LastOrDefault();
        var status = last?.Response?.StatusCode;
        return call.State switch
        {
            ExecutionState.Planned or ExecutionState.Queued => (CompositeStageState.Waiting, "Waiting for earlier steps."),
            ExecutionState.Running => (CompositeStageState.Observing, step.Role == "Observer" ? "Observing (bounded reads)." : "Sending."),
            ExecutionState.Passed => (CompositeStageState.Succeeded, $"{step.Role}: {step.Operation} → {status}{(call.Attempts.Count > 1 ? $" after {call.Attempts.Count} reads" : "")}."),
            ExecutionState.Failed when step.Role == "Observer" && status is { } s && CompositePlanner.ObserveOnStatus.Contains(s) =>
                (CompositeStageState.TimedOut, $"Not observed within {call.Attempts.Count} read(s) (last {s}). A timeout is not proof that the message failed to arrive."),
            ExecutionState.Failed => (CompositeStageState.Failed, $"{step.Operation} → {status?.ToString() ?? "no response"}: {string.Join("; ", call.Assertions.Where(a => a.Outcome != AssertionOutcome.Passed).Select(a => a.Explanation).Take(2))}".TrimEnd(' ', ':')),
            ExecutionState.Error or ExecutionState.TimedOut when step.Mutating && (last?.Error?.Contains("Outcome unknown", StringComparison.Ordinal) ?? false) =>
                (CompositeStageState.UnknownOutcome, "The data-changing request was sent but no response arrived: it may or may not have been applied, and it is not sent again."),
            ExecutionState.Error or ExecutionState.TimedOut when step.Role == "Observer" => (CompositeStageState.TimedOut, $"No answer from the observer ({last?.Error ?? call.Reason})."),
            ExecutionState.Error or ExecutionState.TimedOut => (CompositeStageState.Failed, last?.Error ?? call.Reason ?? "Error."),
            ExecutionState.Blocked or ExecutionState.Skipped => (CompositeStageState.Blocked, call.Reason ?? "Not sent: an earlier step did not pass."),
            ExecutionState.Cancelled or ExecutionState.Interrupted => (CompositeStageState.NotRun, call.Reason ?? "Cancelled."),
            ExecutionState.NotAssessed => (CompositeStageState.UnknownOutcome, "Executed, but its outcome could not be assessed (no assertion result)."),
            _ => (CompositeStageState.NotRun, call.State.ToString()),
        };
    }

    public static (CompositeRunVerdict Verdict, string Reason) Verdict(CompositeRunKind kind, List<CompositeStageRun> stages, List<CompositeCorrelationLink> links,
        List<string> mandatory, ExecutionRunDetail? detail)
    {
        if (detail is null) return (CompositeRunVerdict.Incomplete, "The run's evidence is no longer stored.");
        if (stages.Any(s => s.State is CompositeStageState.Waiting or CompositeStageState.Observing)) return (CompositeRunVerdict.Running, "The run is in progress.");
        if (links.FirstOrDefault(l => l.State == CorrelationState.WrongCorrelation) is { } wrong) return (CompositeRunVerdict.WrongCorrelation, $"{wrong.Boundary}: {wrong.Evidence}");
        if (stages.FirstOrDefault(s => s.State == CompositeStageState.UnknownOutcome) is { } unknown) return (CompositeRunVerdict.UnknownOutcome, $"{unknown.StageId}: {unknown.Detail}");
        if (stages.FirstOrDefault(s => s.State == CompositeStageState.Failed) is { } failed) return (CompositeRunVerdict.Failed, $"{failed.StageId}: {failed.Detail}");
        if (stages.FirstOrDefault(s => s.State is CompositeStageState.TimedOut or CompositeStageState.Blocked or CompositeStageState.NotRun or CompositeStageState.NotObservable) is { } open)
            return (CompositeRunVerdict.Incomplete, $"{open.StageId}: {open.Detail}");
        if (links.FirstOrDefault(l => l.State is CorrelationState.Missing or CorrelationState.NotObserved) is { } gap) return (CompositeRunVerdict.Incomplete, $"{gap.Boundary}: {gap.Evidence}");
        if (kind == CompositeRunKind.FullE2E && mandatory.All(m => stages.Any(s => s.StageId == m && s.State == CompositeStageState.Succeeded)))
            return (CompositeRunVerdict.FullE2EVerified, $"One continuous transaction: {stages.Count} stage(s) succeeded and {links.Count} boundary identifier(s) were carried or confirmed in this run.");
        return (CompositeRunVerdict.SubflowVerified, $"SUBFLOW_TEST: {string.Join(" → ", stages.Select(s => s.StageId))} verified; this is not a full E2E result.");
    }
}
