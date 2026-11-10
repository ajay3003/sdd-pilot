using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.ContractTesting;
using BirkNext.Api.Services.ExecutionEvidence;
using BirkNext.Api.Services.IntegrationSuites.Composite;
using BirkNext.ContractTesting;
using BirkNext.ExecutionEvidence;
using BirkNext.IntegrationSuites;
using BirkNext.SourceDomains;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.IntegrationSuites;

/// <summary>
/// Multi-source (composite) suites: generation from an approved Composite Source Context, the composite section (stage views, three readiness
/// levels, actionable blockers, the transaction plan as a Draft Contract Testing chain, coverage), explicitly authorised composite runs through the
/// existing chain runner (one Execution Evidence run = one continuous transaction), and the run read model (stage states, boundary correlation,
/// verdict). Separate runs are never combined; a subflow is never a full E2E result.
/// </summary>
public sealed partial class IntegrationSuiteService
{
    public const string CompositeE2EKind = "composite-e2e";
    public const string CompositeSubflowKind = "composite-subflow";

    private sealed record CompositeRunPlan(CompositeRunKind Kind, List<string> Stages, List<string> StepIds, CompositePlan Plan, List<SuiteSourceBinding> Sources, List<string> MandatoryStages);

    // ── Generation ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<IntegrationSuiteGenerateResult> GenerateCompositeAsync(IntegrationSuiteGenerateRequest request, Guid contextId, IntegrationSuiteScope scope, string initiator, CancellationToken ct)
    {
        if (composites is null) return new(null, false, "Composite source contexts are not available in this host.");
        if (await composites.GetAsync(contextId, ct) is not { } context) return new(null, false, "The composite source context was not found.");
        if (context.ScopeId != scope.ScopeId) return new(null, false, $"The context belongs to scope '{context.ScopeId}', not '{scope.ScopeId}'.");
        var approved = context.Snapshots.Where(a => a.Approval == CompositeApproval.Approved).ToList();
        if (approved.Count == 0) return new(null, false, "No source of the context is approved: a suite relies only on approved associations.");
        var coverage = context.SourceCoverage!;
        if (coverage.State != CompositeReadiness.Ready && !request.AllowPartial)
            return new(null, false, $"Source coverage is {coverage.State}: {string.Join("; ", coverage.Items.Take(6))}. Generate a partial draft explicitly to continue.");
        var inputs = approved.Select(a =>
        {
            var byModule = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (stage, modules) in a.ModulesByStage)
                foreach (var m in modules) { if (!byModule.TryGetValue(m, out var list)) byModule[m] = list = []; if (!list.Contains(stage)) list.Add(stage); }
            return new SourceInput(a.SnapshotId, null, [.. byModule.Keys.Order(StringComparer.OrdinalIgnoreCase)], byModule, a.Approval.ToString());
        }).ToList();
        // One suite per context identity (scope + owning project) and options: a new context revision becomes the next suite revision.
        var suiteKey = $"{scope.ScopeId}:composite:{context.OwningProject}:{IntegrationSuiteComposer.Fingerprint(new { request.EnvironmentId, request.IncludeNegatives, request.IncludeSecurity, request.GenerateTestData, request.Seed })[..12]}";
        var gate = Gates.GetOrAdd(suiteKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try { return await GenerateLockedAsync(request, initiator, inputs, scope with { Modules = [.. inputs.SelectMany(i => i.Modules).Distinct(StringComparer.OrdinalIgnoreCase)] }, suiteKey, context, ct); }
        finally { gate.Release(); }
    }

    private async Task<SuiteComposite> CompositeSectionAsync(CompositeSourceContext context, IntegrationSuiteScope scope, List<SuiteSourceBinding> bindings, List<SuiteContractRef> contracts,
        List<SuiteTestRef> tests, List<SuiteStage> stages, List<SuiteSecurityScenario> security, List<SuiteReviewItem> review, IntegrationSuiteGenerateRequest request, string suiteKey,
        List<string> limitations, CancellationToken ct)
    {
        // 1. Transaction plan from the stages' contracts (documented identifier flow only).
        var stageInputs = new List<CompositePlanner.StageInput>();
        foreach (var template in scope.Stages.Where(s => s.Source is not null))
        {
            var stageContracts = new List<CompositePlanner.StageContract>();
            foreach (var c in contracts.Where(c => c.Stages.Contains(template.StageId) && c.Type == nameof(SourceContractType.OpenApi) && c.ContractKey is not null && c.ContractVersion is not null))
            {
                if (await registry.DocumentAsync(c.ContractKey!, c.ContractVersion!.Value, ct) is not { } doc) continue;
                var cref = ContractIdentity.ContractRef(c.ContractKey!, c.ContractVersion!.Value);
                // The newest positive test of this contract at or below the stage's version. Contract evolution never rebinds a test silently: a test
                // still bound to an earlier version is used, and its own readiness names what blocks it (the update proposal to review, authentication).
                var positives = tests.Where(t => t.Kind == nameof(ContractTestKind.Positive) && VersionOf(t.ContractRef, c.ContractKey!) is { } v && v <= c.ContractVersion!.Value)
                    .GroupBy(t => t.Operation).ToDictionary(g => g.Key, g => g.OrderByDescending(t => VersionOf(t.ContractRef, c.ContractKey!)).First().TestId, StringComparer.Ordinal);
                stageContracts.Add(new(c.ContractKey!, c.ContractVersion!.Value, doc, positives));
            }
            stageInputs.Add(new(template.StageId, template.Label, template.Source!, stageContracts));
        }
        var plan = CompositePlanner.Plan(stageInputs);
        if (plan.Steps.Count > 0 && plan.Steps.All(s => s.TestId is not null))
        {
            var suggestionId = "composite-" + IntegrationSuiteComposer.Fingerprint(new { suiteKey, Steps = plan.Steps.Select(s => (s.StepId, s.TestId, s.FromStep, s.FromField, s.Parameter, s.EchoField)) })[..24];
            var existing = (await contractTests.ChainsAsync(ct)).FirstOrDefault(c => c.SuggestionId == suggestionId);
            if (existing is null)
            {
                var saved = await contractTests.SaveChainAsync(null, new ContractChainSave
                {
                    Name = $"Composite E2E · {scope.DisplayName} · {context.OwningProject}", State = ContractChainState.Draft, SuggestionId = suggestionId,
                    Description = $"Generated from composite context {context.ContextKey} r{context.Revision}. Draft until a person approves the identifier mappings and data-changing steps.",
                    Steps = CompositePlanner.ChainSteps(plan),
                }, ct);
                existing = saved.Chain;
                if (saved.Problems.Count > 0) plan = plan with { Missing = [.. plan.Missing, .. saved.Problems.Select(p => $"Plan chain: {p}")] };
            }
            if (existing is not null) plan = plan with { ChainId = existing.Id, ChainRevision = existing.Revision, State = existing.State.ToString() };
        }
        else if (plan.Steps.Count > 0) plan = plan with { State = "Incomplete" };

        // 2. Stage views.
        var views = StageViews(scope, context, contracts, tests, stages, plan);
        var section = new SuiteComposite
        {
            ContextId = context.Id, ContextKey = context.ContextKey, ContextRevision = context.Revision, PartialDraft = context.SourceCoverage?.State != CompositeReadiness.Ready,
            Sources = bindings, Stages = views, Transitions = context.Compatibility, Plan = plan,
            PeriodLock = PeriodLock(plan),
            BufdataDelivery = plan.Steps.Any(s => s.StageId == "bufdata")
                ? "Observed through a contract read in composite runs (synthetic or approved environments only); the Parquet content is verified separately in Parquet Quality Review."
                : "SPECIFIED_BUT_NOT_RUNTIME_VERIFIED: the producer's dataset contract is specified in source; no delivery executor or observer exists, and the recipient's source is not associated.",
        };
        if (section.PartialDraft) limitations.Add("Partial draft: mandatory stages lack an approved source; the suite can never be VERIFIED as a full E2E in this state.");
        _ = request;
        return Readiness(section, context, security, review, tests, runs: []);
    }

    private static List<string> PeriodLock(CompositePlan plan)
    {
        var action = plan.Steps.FirstOrDefault(s => s.StageId == "period-lock" && s.Role == "Action");
        return
        [
            "Prerequisite: the reports of the period are received and the period is open.",
            action is null ? "Operation: operator-only in the real system (no lock operation in the associated contracts); BirkNext never locks a real period." : $"Operation: {action.Operation} ({action.ContractRef}) — data-changing; runs only after explicit confirmation, only against an approved non-production target.",
            "Authorized owner: Operator (test lead approval for a synthetic local target).",
            "Environment scope: the run's approved Target Environment; never production.",
            "Expected result: the period is locked and the dataset for the period is prepared.",
            "Downstream trigger: the period-closed event / dataset delivery to Bufdata.",
            "Evidence: the action call and the downstream observation in the same composite run.",
        ];
    }

    private static List<CompositeStageView> StageViews(IntegrationSuiteScope scope, CompositeSourceContext context, List<SuiteContractRef> contracts, List<SuiteTestRef> tests,
        List<SuiteStage> stages, CompositePlan plan)
    {
        var list = new List<CompositeStageView>();
        var order = 0;
        foreach (var template in scope.Stages.Where(s => s.Source is not null))
        {
            var mapping = context.StageMappings.FirstOrDefault(m => m.StageId == template.StageId);
            var approved = context.Snapshots.Where(a => a.Approval == CompositeApproval.Approved && a.Stages.Contains(template.StageId)).ToList();
            var step = plan.Steps.FirstOrDefault(s => s.StageId == template.StageId);
            var runner = stages.FirstOrDefault(s => s.StageId == template.StageId);
            var test = step?.TestId is { } id ? tests.FirstOrDefault(t => t.TestId == id) : null;
            var gaps = test is null ? [] : BlockingGaps(step!, test);
            var readiness = step is not null
                ? test is null ? CompositeReadiness.Blocked : test.Readiness == "Ready" || gaps.Count == 0 ? CompositeReadiness.Ready : CompositeReadiness.Blocked
                : runner?.Coverage is SuiteStageCoverage.Executable or SuiteStageCoverage.Observable or SuiteStageCoverage.ExecutableAndObservable ? CompositeReadiness.Partial
                : CompositeReadiness.NotSupported;
            var executor = step?.Role is "Executor" or "Action" ? $"Contract test {step.Operation} ({step.ContractRef}){(step.Mutating ? " — data-changing" : "")}"
                : runner?.Coverage is SuiteStageCoverage.Executable or SuiteStageCoverage.ExecutableAndObservable ? "Registered journey executor (Altinn TT02; needs an approved TT02 target)"
                : step is not null ? "Not caused by BirkNext (asynchronous downstream stage)" : "MISSING_EXECUTOR";
            var observer = step?.Role == "Observer" ? $"Contract read {step.Operation} — waits up to {step.ObserveAttempts} × {step.ObserveDelayMs} ms{(step.EchoField is null ? "" : $", checks {step.EchoField}")}"
                : step?.Role is "Executor" or "Action" ? "Its own synchronous response (status and contract assertions)"
                : runner?.Coverage is SuiteStageCoverage.Observable or SuiteStageCoverage.ExecutableAndObservable ? "Registered journey observer (aggregate unless correlated)"
                : "NOT_OBSERVABLE";
            var stageContracts = contracts.Where(c => c.Stages.Contains(template.StageId) && c.ContractKey is not null).Select(c => ContractIdentity.ContractRef(c.ContractKey!, c.ContractVersion ?? 0)).Distinct().ToList();
            var coverage = step is not null && readiness == CompositeReadiness.Ready && plan.State == nameof(ContractChainState.Approved) ? CompositeCoverageLevel.Executable
                : step?.TestId is not null ? CompositeCoverageLevel.Configured
                : stageContracts.Count > 0 || runner?.JourneySteps.Count > 0 ? CompositeCoverageLevel.Generated : CompositeCoverageLevel.None;
            list.Add(new CompositeStageView
            {
                Order = ++order, StageId = template.StageId, Label = template.Label, Mandatory = template.Source!.Mandatory, SourceClass = mapping?.Class ?? CompositeMatchClass.Missing,
                Sources = approved.Count > 0 ? [.. approved.Select(a => $"{a.ProjectKey} · {a.ArchiveFileName}")] : mapping?.ReferenceSource is { } r ? [r] : [],
                Contracts = stageContracts, Executor = executor, Observer = observer, Readiness = readiness, Coverage = coverage,
                WhyNotVerified = WhyNot(mapping, step, readiness, gaps, plan),
            });
        }
        return list;
    }

    /// <summary>
    /// The readiness gaps of a plan step's test that stop a composite run — the same rule the chain runner applies: a chained input's own Test Data
    /// gap does not block (the value comes from an earlier step), a test's review state is a limitation (the plan approval is the run gate), and the
    /// Target Environment is chosen per run, and a data-changing call is confirmed per run in the preflight (an unbound authentication still blocks).
    /// </summary>
    internal static List<string> BlockingGaps(CompositePlanStep step, SuiteTestRef test) =>
        [.. test.ReadinessGaps.Where(g => !(step.FromStep is not null && g.StartsWith("Test Data", StringComparison.Ordinal))
            && !g.StartsWith("Review", StringComparison.Ordinal) && !g.StartsWith("Target Environment", StringComparison.Ordinal)
            && !g.StartsWith("Changes data", StringComparison.Ordinal))];

    private static string? WhyNot(CompositeStageMapping? mapping, CompositePlanStep? step, CompositeReadiness readiness, List<string> gaps, CompositePlan plan)
    {
        if (mapping is null || mapping.Class == CompositeMatchClass.Missing) return "No approved source for this stage.";
        if (mapping.Class is CompositeMatchClass.Candidate or CompositeMatchClass.Ambiguous) return "The stage's source is not approved.";
        if (step is null) return "No executor/observer: the stage's contracts offer no operation that continues the transaction (NOT_OBSERVABLE).";
        if (step.TestId is null) return $"{step.Operation} has no positive contract test.";
        if (readiness != CompositeReadiness.Ready) return string.Join(" ", gaps.Take(2));
        if (plan.State != nameof(ContractChainState.Approved)) return "The transaction plan is a draft: approve it before running.";
        return "Not executed yet in a composite run.";
    }

    /// <summary>Levels B and C, blockers and coverage from the stage views and (on read) the composite runs; level A comes from the context.</summary>
    private static SuiteComposite Readiness(SuiteComposite section, CompositeSourceContext? context, List<SuiteSecurityScenario> security, List<SuiteReviewItem> review,
        List<SuiteTestRef> tests, List<CompositeRunView> runs)
    {
        var levelA = context?.SourceCoverage ?? section.Readiness.FirstOrDefault(r => r.Level == "Source coverage") ?? new("Source coverage", CompositeReadiness.NotAssessed, "Context not found.", []);
        var mandatory = section.Stages.Where(s => s.Mandatory).ToList();
        var ready = mandatory.Count(s => s.Readiness == CompositeReadiness.Ready);
        var levelB = new CompositeReadinessLevel("Stage execution",
            mandatory.Count == 0 ? CompositeReadiness.NotAssessed : ready == mandatory.Count ? CompositeReadiness.Ready : ready > 0 || mandatory.Any(s => s.Readiness == CompositeReadiness.Partial) ? CompositeReadiness.Partial
            : mandatory.All(s => s.Readiness == CompositeReadiness.NotSupported) ? CompositeReadiness.NotSupported : CompositeReadiness.Blocked,
            $"{ready} of {mandatory.Count} mandatory stage(s) can be executed or observed by the plan.",
            [.. mandatory.Where(s => s.Readiness != CompositeReadiness.Ready).Select(s => $"{s.Label}: {s.Readiness} — {s.WhyNotVerified}")]);
        var cItems = new List<string>();
        if (levelA.State != CompositeReadiness.Ready) cItems.Add($"Source coverage is {levelA.State}.");
        if (levelB.State != CompositeReadiness.Ready) cItems.Add($"Stage execution is {levelB.State}.");
        if (section.Plan.ChainId is null) cItems.Add("No complete transaction plan (a stage has no executor/observer or test).");
        else if (section.Plan.State != nameof(ContractChainState.Approved)) cItems.Add("The transaction plan is a draft: a person must approve it.");
        var missingFromPlan = mandatory.Where(s => section.Plan.Steps.All(p => p.StageId != s.StageId)).Select(s => s.Label).ToList();
        if (missingFromPlan.Count > 0) cItems.Add($"Stages outside the plan (never verified end to end): {string.Join(", ", missingFromPlan)}.");
        if (section.Transitions.Any(t => t.Overall == TransitionCompatibility.Incompatible && !t.Accepted)) cItems.Add("An incompatible transition is not accepted.");
        cItems.Add("Run-time prerequisites: an approved non-production Target Environment, authentication bound per contract, explicit authorisation and confirmation of data-changing steps.");
        var levelC = new CompositeReadinessLevel("Complete E2E",
            levelA.State == CompositeReadiness.Blocked || levelB.State is CompositeReadiness.NotSupported or CompositeReadiness.Blocked || missingFromPlan.Count > 0 && section.Plan.ChainId is null ? CompositeReadiness.Blocked
            : cItems.Count == 1 ? CompositeReadiness.Ready
            : section.Plan.State != nameof(ContractChainState.Approved) || levelA.State == CompositeReadiness.NeedsReview ? CompositeReadiness.NeedsReview : CompositeReadiness.Partial,
            cItems.Count == 1 ? "Every mandatory stage is in an approved plan with an executor or observer; a run can verify the continuous transaction." : "A full E2E run cannot be VERIFIED yet.", cItems);

        // Coverage: stages (with run overlay), scenarios, contract operations, and the complete E2E.
        var verifiedRun = runs.FirstOrDefault(r => r.Verdict == CompositeRunVerdict.FullE2EVerified);
        var stageRows = section.Stages.Select(s =>
        {
            var executed = runs.Any(r => r.Stages.Any(x => x.StageId == s.StageId && x.State is not (CompositeStageState.NotRun or CompositeStageState.Blocked or CompositeStageState.NotObservable)));
            var verified = runs.Any(r => r.Verdict is CompositeRunVerdict.FullE2EVerified or CompositeRunVerdict.SubflowVerified && r.Stages.Any(x => x.StageId == s.StageId && x.State == CompositeStageState.Succeeded));
            var level = verified ? CompositeCoverageLevel.Verified : executed ? CompositeCoverageLevel.Executed : s.Coverage;
            return new CompositeCoverageRow(s.Label, "Stage", level, level == CompositeCoverageLevel.Verified ? null : executed ? "Executed, but no run verified it (failed, timed out, unknown outcome or wrong correlation)." : s.WhyNotVerified);
        }).ToList();
        var stagesWithCoverage = section.Stages.Zip(stageRows, (s, r) => s with { Coverage = r.Level }).ToList();
        var opRows = tests.Where(t => t.Kind == nameof(ContractTestKind.Positive)).GroupBy(t => (t.ContractRef, t.Operation)).Select(g =>
        {
            var inPlan = section.Plan.Steps.Any(p => p.TestId is { } id && g.Any(t => t.TestId == id));
            var level = inPlan && stageRows.Any(r => r.Level == CompositeCoverageLevel.Verified) && runs.Any(r => r.Verdict is CompositeRunVerdict.FullE2EVerified or CompositeRunVerdict.SubflowVerified) ? CompositeCoverageLevel.Verified
                : g.Any(t => t.Readiness == "Ready") ? CompositeCoverageLevel.Executable : g.Any(t => t.TestDataBinding is not null) ? CompositeCoverageLevel.Configured : CompositeCoverageLevel.Generated;
            return new CompositeCoverageRow($"{g.Key.ContractRef} · {g.Key.Operation}", "Contract operation", level, level == CompositeCoverageLevel.Verified ? null
                : inPlan ? "In the transaction plan; verified only through a composite run." : "Not part of the E2E transaction plan (contract test only).");
        }).ToList();
        var securityRows = security.Select(s => new CompositeCoverageRow($"{s.ScenarioId} · {s.Title}", "Kode 6/7 scenario",
            s.Triage == SuiteTriage.CanRunNow ? CompositeCoverageLevel.Executable : s.Triage == SuiteTriage.WaitingForPrerequisite ? CompositeCoverageLevel.Configured : CompositeCoverageLevel.Generated,
            s.OpenQuestion is not null ? $"NEEDS_DOMAIN_DECISION: {s.OpenQuestion}" : s.Prerequisites.Count > 0 ? string.Join("; ", s.Prerequisites.Take(2)) : "Runs in its own owner (M01), not in the composite plan.")).ToList();
        var e2e = new CompositeCoverageRow("Complete E2E transaction", "Complete E2E",
            verifiedRun is not null ? CompositeCoverageLevel.Verified : runs.Any(r => r.Kind == CompositeRunKind.FullE2E) ? CompositeCoverageLevel.Executed
            : levelC.State == CompositeReadiness.Ready ? CompositeCoverageLevel.Executable : section.Plan.ChainId is not null ? CompositeCoverageLevel.Configured : CompositeCoverageLevel.Generated,
            verifiedRun is not null ? null : levelC.Detail + " " + string.Join(" ", levelC.Items.Take(3)));

        return section with
        {
            Readiness = [levelA, levelB, levelC], Stages = stagesWithCoverage,
            Blockers = Blockers(section, context, security, review, tests),
            Coverage = [e2e, .. stageRows, .. opRows, .. securityRows],
        };
    }

    private static List<CompositeBlocker> Blockers(SuiteComposite section, CompositeSourceContext? context, List<SuiteSecurityScenario> security, List<SuiteReviewItem> review, List<SuiteTestRef> tests)
    {
        var list = new List<CompositeBlocker>();
        void Add(string id, string stage, string reason, string missing, string evidence, string action, string owner, bool blocking, string status = "Open") =>
            list.Add(new CompositeBlocker { Id = id, Stage = stage, Reason = reason, Missing = missing, Evidence = evidence, Action = action, Owner = owner, Severity = blocking ? "Blocking" : "Limiting", ReviewStatus = status });
        foreach (var m in context?.StageMappings ?? [])
        {
            if (m.Class == CompositeMatchClass.Missing)
                Add($"source:{m.StageId}", m.Label, "No source is associated with the stage.", "An imported source snapshot of the stage's application", string.Join("; ", m.Missing),
                    "Import the source, then associate and approve it in Integration & Sources.", "Tester", m.Mandatory);
            else if (m.Class is CompositeMatchClass.Candidate or CompositeMatchClass.Ambiguous)
                Add($"source:{m.StageId}", m.Label, m.Class == CompositeMatchClass.Ambiguous ? "Several projects claim the stage." : "The stage's source is proposed, not approved.", "A human decision",
                    string.Join("; ", m.Evidence.Take(3)), "Review mappings: approve one source (or reject the others).", "Test lead", m.Mandatory, "Needs review");
        }
        foreach (var t in section.Transitions.Where(t => t.Overall == TransitionCompatibility.Incompatible))
            Add($"compat:{t.Key}", $"{t.FromStage} → {t.ToStage}", string.Join(" ", t.Dimensions.Where(d => d.State == TransitionCompatibility.Incompatible).Select(d => d.Detail)),
                "Compatible source versions or a reviewed acceptance", string.Join("; ", t.Dimensions.SelectMany(d => d.Evidence).Take(3)),
                t.Accepted ? $"Accepted by {t.AcceptedBy}: {t.AcceptedReason}" : "Associate compatible versions, or accept the transition with a reason after review (the state stays Incompatible).",
                "Test lead", !t.Accepted, t.Accepted ? "Accepted" : "Open");
        foreach (var t in section.Transitions.Where(t => t.Overall == TransitionCompatibility.MissingEvidence))
            Add($"evidence:{t.Key}", $"{t.FromStage} → {t.ToStage}", string.Join(" ", t.Dimensions.Where(d => d.State == TransitionCompatibility.MissingEvidence).Select(d => $"{d.Name}: {d.Detail}")),
                "Source evidence of the boundary", "", "Associate the source that declares the boundary (contract, schema or channel).", "Tester", false);
        foreach (var s in section.Stages.Where(s => s.Observer == "NOT_OBSERVABLE" && s.Executor is "MISSING_EXECUTOR"))
            Add($"observer:{s.StageId}", s.Label, "No executor and no observer can see this stage.", "A registered observer or a read contract of the stage", s.WhyNotVerified ?? "",
                "Configure an existing supported observer, or provide a read contract in the stage's source. No external adapter is invented.", "Platform", s.Mandatory);
        foreach (var step in section.Plan.Steps)
            if (step.TestId is { } id && tests.FirstOrDefault(t => t.TestId == id) is { } test)
            {
                var blocking = BlockingGaps(step, test);
                foreach (var gap in test.ReadinessGaps.Where(g => !(step.FromStep is not null && g.StartsWith("Test Data", StringComparison.Ordinal)) && !g.StartsWith("Target Environment", StringComparison.Ordinal)).Take(3))
                    Add($"test:{step.StepId}:{gap.Split(':')[0]}", step.StageId, gap, gap.Split(':')[0],
                        $"{step.Operation} ({step.ContractRef})",
                        gap.StartsWith("Test Data", StringComparison.Ordinal) ? "Bind approved Test Data (approve the generated synthetic draft in Test Data)."
                        : gap.StartsWith("Authentication", StringComparison.Ordinal) ? "Bind the contract's authentication for the Target Environment (credential reference, never a value)."
                        : gap.StartsWith("Review", StringComparison.Ordinal) ? "Review the generated test in Contract Testing (the plan approval gates the run; an unreviewed test is a limitation)."
                        : "Resolve the prerequisite in Contract Testing.", "Tester", blocking.Contains(gap));
            }
        if (section.Plan.ChainId is not null && section.Plan.State != nameof(ContractChainState.Approved))
            Add("plan:approve", "All", "The transaction plan is a draft.", "A test lead's approval of the identifier mappings and data-changing steps",
                string.Join("; ", section.Plan.Steps.Where(s => s.FromStep is not null).Select(s => $"{s.Parameter} ← {s.FromStep}.{s.FromField}").Take(4)),
                "Review the plan in Execute & Evidence and approve it.", "Test lead", true, "Needs review");
        foreach (var missing in section.Plan.Missing)
            Add($"plan:{IntegrationSuiteComposer.Fingerprint(missing)[..8]}", missing.Split(':')[0], missing, "Executor/observer", "", "Provide the missing operation or test; nothing is executed for this stage.", "Platform", true);
        foreach (var s in security.Where(s => s.OpenQuestion is not null).GroupBy(s => s.ReviewKey))
        {
            var decision = review.FirstOrDefault(r => r.Key == s.Key)?.Decision;
            Add($"kode67:{s.Key}", "Kode 6/7", s.First().OpenQuestion!, "An approved business rule", string.Join(", ", s.Select(x => x.ScenarioId)),
                "Needs domain decision: record it in Generate & Review.", "Domain owner", false, decision ?? "NEEDS_DOMAIN_DECISION");
        }
        if (!section.Plan.Steps.Any(s => s.StageId == "period-lock"))
            Add("period-lock", "Period lock", "Period lock is operator-only in the real system.", "An authorised operator lock or an approved synthetic lock operation", "Skolenærvær journey pack: period-locked / close-valid-period Not assessed",
                "Operator performs the lock in an approved test environment; BirkNext never locks a real period.", "Operator", false);
        if (!section.Plan.Steps.Any(s => s.StageId == "bufdata"))
            Add("bufdata", "Bufdata delivery", section.BufdataDelivery, "A delivery observer (receipt, manifest or dataset read)", "", "Verify delivered files in Parquet Quality Review when a file exists; no Azure Blob is read live.", "Domain owner", false);
        return list;
    }

    private static (SuiteStatus Status, string Reason) CompositeStatus(SuiteComposite composite, SuiteStatus status, string reason)
    {
        var c = composite.Readiness.FirstOrDefault(r => r.Level == "Complete E2E");
        if (composite.PartialDraft) return (SuiteStatus.Draft, "Partial draft: mandatory stages lack an approved source.");
        return c?.State switch
        {
            CompositeReadiness.Ready => (SuiteStatus.ReadyToExecute, c.Detail),
            CompositeReadiness.NeedsReview => (SuiteStatus.NeedsReview, string.Join(" ", c.Items.Take(2))),
            CompositeReadiness.Blocked => (SuiteStatus.Blocked, string.Join(" ", c.Items.Take(2))),
            CompositeReadiness.Partial => (SuiteStatus.PartiallyExecutable, string.Join(" ", c.Items.Take(2))),
            _ => (status, reason),
        };
    }

    // ── Read overlay ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The composite section with live overlays: the plan chain's state (approval happens after generation) and the composite runs.</summary>
    private async Task<(SuiteComposite Composite, List<CompositeRunView> Runs)> CompositeOverlayAsync(IntegrationSuite suite, CancellationToken ct)
    {
        var composite = suite.Composite!;
        if (composite.Plan.ChainId is { } chainId && await contractTests.ChainAsync(chainId, null, ct) is { } chain)
            composite = composite with { Plan = composite.Plan with { State = chain.State.ToString(), ChainRevision = chain.Revision } };
        var context = composites is null ? null : await composites.GetAsync(composite.ContextId, ct);
        var runs = await CompositeRunsAsync(suite.SuiteKey, ct);
        var tests = suite.Tests;
        // Stage coverage "Executable" depends on the plan's approval: recompute it from the generation-time level.
        composite = composite with
        {
            Stages = [.. composite.Stages.Select(s => s with { Coverage = s.Coverage is CompositeCoverageLevel.Configured or CompositeCoverageLevel.Executable
                ? s.Readiness == CompositeReadiness.Ready && composite.Plan.State == nameof(ContractChainState.Approved) ? CompositeCoverageLevel.Executable : CompositeCoverageLevel.Configured : s.Coverage,
                WhyNotVerified = s.WhyNotVerified == "The transaction plan is a draft: approve it before running." && composite.Plan.State == nameof(ContractChainState.Approved) ? "Not executed yet in a composite run." : s.WhyNotVerified })],
        };
        return (Readiness(composite, context, suite.Security, suite.Review, tests, runs), runs);
    }

    // ── Plan approval ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Approves the suite's draft transaction plan: the chain's next revision with state Approved (an explicit human decision).</summary>
    public async Task<(CompositePlan? Plan, string? Error)> ApprovePlanAsync(Guid suiteId, string reviewer, CancellationToken ct)
    {
        var suite = await GetAsync(suiteId, ct);
        if (suite?.Composite is not { } composite) return (null, "Composite suite not found.");
        if (composite.Plan.ChainId is not { } chainId || await contractTests.ChainAsync(chainId, null, ct) is not { } chain) return (null, "The suite has no transaction plan to approve.");
        if (chain.State == ContractChainState.Approved) return (composite.Plan with { State = chain.State.ToString(), ChainRevision = chain.Revision }, null);
        var saved = await contractTests.SaveChainAsync(chainId, new ContractChainSave
        {
            Name = chain.Name, Description = $"{chain.Description} Approved by {reviewer}.".Trim(), Steps = chain.Steps, ExpectedRevision = chain.Revision,
            State = ContractChainState.Approved, SuggestionId = chain.SuggestionId,
        }, ct);
        if (saved.Chain is null) return (null, string.Join(" ", saved.Problems));
        logger.LogInformation("Composite plan approved. Suite {Suite}; chain revision {Revision}", suite.SuiteKey, saved.Chain.Revision);
        return (composite.Plan with { State = saved.Chain.State.ToString(), ChainRevision = saved.Chain.Revision }, null);
    }

    // ── Execution ───────────────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<CompositePreflight?> PreflightAsync(Guid suiteId, CompositeRunRequest request, CancellationToken ct) =>
        await GetAsync(suiteId, ct) is { } suite ? Preflight(suite, request).Preflight : null;

    private static (CompositePreflight Preflight, CompositeRunPlan? Plan) Preflight(IntegrationSuite suite, CompositeRunRequest request)
    {
        var problems = new List<string>(); var warnings = new List<string>();
        if (suite.Composite is not { } composite) return (new(false, CompositeRunKind.SubflowTest, [], [], ["Only a composite suite has a composite run."], []), null);
        var plan = composite.Plan;
        var planStages = plan.Steps.Select(s => s.StageId).Distinct(StringComparer.Ordinal).ToList();
        var mandatory = composite.Stages.Where(s => s.Mandatory).Select(s => s.StageId).ToList();
        var chosen = request.Stages.Count == 0 ? planStages : [.. request.Stages];
        if (plan.ChainId is null) problems.Add("There is no complete transaction plan: a stage lacks an executor/observer or a contract test.");
        else if (plan.State != nameof(ContractChainState.Approved)) problems.Add("The transaction plan is a draft: a test lead must approve it first.");
        if (chosen.Any(s => !planStages.Contains(s))) problems.Add($"Not in the plan: {string.Join(", ", chosen.Where(s => !planStages.Contains(s)))}.");
        else if (!planStages.Take(chosen.Count).SequenceEqual(chosen)) problems.Add("A subflow is a prefix of the plan's stages (later stages take identifiers from earlier ones).");
        var full = chosen.Count == planStages.Count && mandatory.All(planStages.Contains) && !composite.PartialDraft;
        if (chosen.Count == planStages.Count && !full)
            warnings.Add($"Even the whole plan is a SUBFLOW_TEST: {(composite.PartialDraft ? "the suite is a partial draft" : $"stages outside the plan: {string.Join(", ", mandatory.Where(m => !planStages.Contains(m)))}")}.");
        var steps = plan.Steps.Where(s => chosen.Contains(s.StageId)).ToList();
        var mutating = steps.Where(s => s.Mutating).Select(s => $"{s.StageId}: {s.Operation}").ToList();
        if (!request.Authorized) problems.Add("Execution needs the tester's explicit authorisation; nothing was sent.");
        if (string.IsNullOrWhiteSpace(request.EnvironmentId) || string.IsNullOrWhiteSpace(request.TargetUrl)) problems.Add("Select an approved Target Environment.");
        if (request.IsProduction) problems.Add("A composite run never targets production.");
        if (mutating.Count > 0 && !request.ConfirmMutatingCalls) problems.Add($"Confirm the data-changing operation(s): {string.Join("; ", mutating)}.");
        if (composite.Sources.Any(s => s.Approval != nameof(CompositeApproval.Approved))) problems.Add("Every source of a composite run must be approved.");
        if (composite.Transitions.Any(t => t.Overall == TransitionCompatibility.Incompatible && !t.Accepted && chosen.Contains(t.ToStage)))
            problems.Add("An incompatible transition inside the run is not accepted.");
        if (composite.Readiness.FirstOrDefault(r => r.Level == "Stage execution") is { State: not CompositeReadiness.Ready } b)
            warnings.Add($"Stage execution readiness is {b.State}: {string.Join("; ", b.Items.Take(3))}");
        var kind = full ? CompositeRunKind.FullE2E : CompositeRunKind.SubflowTest;
        var preflight = new CompositePreflight(problems.Count == 0, kind, chosen, mutating, problems, warnings);
        return (preflight, problems.Count == 0 ? new CompositeRunPlan(kind, chosen, [.. steps.Select(s => s.StepId)], plan, composite.Sources, mandatory) : null);
    }

    public async Task<CompositeRunResult> ExecuteCompositeAsync(Guid suiteId, CompositeRunRequest request, string initiator, CancellationToken ct)
    {
        var suite = await GetAsync(suiteId, ct);
        if (suite is null) return new(null, "NotFound", "Suite not found.", new(false, CompositeRunKind.SubflowTest, [], [], ["Suite not found."], []));
        var (preflight, runPlan) = Preflight(suite, request);
        if (runPlan is null) return new(null, request.Authorized ? "Refused" : "NotAuthorized", string.Join(" ", preflight.Problems), preflight);
        var result = await contractTests.RunChainAsync(runPlan.Plan.ChainId!.Value, new ContractChainRunRequest
        {
            Environment = new ContractTestEnvironment
            {
                EnvironmentId = request.EnvironmentId, TargetUrl = request.TargetUrl, EnvironmentType = request.EnvironmentType, IsProduction = request.IsProduction,
                ProfileId = request.ProfileId, ContextFingerprint = request.ContextFingerprint,
            },
            ConfirmMutatingCalls = request.ConfirmMutatingCalls, StopOnFirstFailure = true, StepIds = runPlan.StepIds,
            Name = $"{(runPlan.Kind == CompositeRunKind.FullE2E ? "Composite E2E" : "Composite subflow")} · {suite.Scope.DisplayName} r{suite.Revision}",
        }, ct);
        if (result.Run?.RunId is not { } runId)
            return new(null, "Refused", string.Join(" ", result.NotRun.Concat(result.Run?.Blockers ?? []).DefaultIfEmpty(result.Run?.Message ?? "The run did not start.")), preflight);
        db.IntegrationSuiteRuns.Add(new IntegrationSuiteRunRecord
        {
            Id = Guid.NewGuid(), SuiteKey = suite.SuiteKey, SuiteId = suite.Id, RunId = runId, Kind = runPlan.Kind == CompositeRunKind.FullE2E ? CompositeE2EKind : CompositeSubflowKind,
            StartedAt = Now, Initiator = initiator, TestIdsJson = JsonSerializer.Serialize(runPlan.Plan.Steps.Where(s => runPlan.StepIds.Contains(s.StepId)).Select(s => s.TestId).OfType<Guid>(), Json),
            PlanJson = JsonSerializer.Serialize(runPlan, Json),
        });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Composite run started. Suite {Suite}; kind {Kind}; stages {Stages}", suite.SuiteKey, runPlan.Kind, runPlan.Stages.Count);
        return new(runId, result.Run.State.ToString(), result.Run.Message ?? "Started.", preflight);
    }

    // ── Run read model ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<List<CompositeRunView>> CompositeRunsAsync(string suiteKey, CancellationToken ct)
    {
        var links = await db.IntegrationSuiteRuns.AsNoTracking().Where(r => r.SuiteKey == suiteKey && r.PlanJson != null).OrderByDescending(r => r.StartedAt).Take(50).ToListAsync(ct);
        var list = new List<CompositeRunView>();
        foreach (var link in links) if (await CompositeRunAsync(link, ct) is { } view) list.Add(view);
        return list;
    }

    public async Task<CompositeRunView?> CompositeRunAsync(Guid suiteId, Guid runId, CancellationToken ct)
    {
        var suite = await db.IntegrationSuites.AsNoTracking().FirstOrDefaultAsync(s => s.Id == suiteId, ct);
        if (suite is null) return null;
        var link = await db.IntegrationSuiteRuns.AsNoTracking().FirstOrDefaultAsync(r => r.SuiteKey == suite.SuiteKey && r.RunId == runId && r.PlanJson != null, ct);
        return link is null ? null : await CompositeRunAsync(link, ct);
    }

    private Task<CompositeRunView?> CompositeRunAsync(IntegrationSuiteRunRecord link, CancellationToken ct) => ReadCompositeRunAsync(db, evidence, link, ct);

    /// <summary>The version of a "key@vN" contract reference when it belongs to <paramref name="key"/>; otherwise null.</summary>
    internal static int? VersionOf(string contractRef, string key) =>
        contractRef.StartsWith(key + "@v", StringComparison.Ordinal) && int.TryParse(contractRef.AsSpan(key.Length + 2), out var v) ? v : null;

    /// <summary>Reopens a composite run from its stored link (plan, kind, source versions) and its Execution Evidence run. Read-only; also used by
    /// Test Execution &amp; Evidence so the verdict has one owner.</summary>
    public static async Task<CompositeRunView?> ReadCompositeRunAsync(AppDbContext db, ExecutionEvidenceStore evidence, IntegrationSuiteRunRecord link, CancellationToken ct)
    {
        if (link.PlanJson is null) return null;
        var runPlan = JsonSerializer.Deserialize<CompositeRunPlan>(link.PlanJson, Json);
        if (runPlan is null) return null;
        var revision = await db.IntegrationSuites.AsNoTracking().Where(s => s.Id == link.SuiteId).Select(s => s.Revision).FirstOrDefaultAsync(ct);
        var detail = await evidence.GetAsync(link.RunId, new ExecutionCallQuery(0, 500), ct);
        return CompositeRunReader.Read(link.RunId, link.SuiteId, revision, link.StartedAt, link.Initiator, runPlan.Kind, runPlan.Stages, runPlan.StepIds, runPlan.Plan, runPlan.Sources,
            runPlan.MandatoryStages, detail);
    }
}
