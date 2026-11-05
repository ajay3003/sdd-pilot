using System.Text.Json.Serialization;

namespace BirkNext.Governance;

// ── Governance & quality gates (policy set governance/1) ─────────────────────────────────────────────────────────────────
// A POLICY is an expectation with a stable id and version. A FINDING is evidence that a policy needs attention, from canonical evidence only. A GATE
// decides one ACTION (review, approval, release …) from the findings: the same finding can block a release and only warn on a review. No score: the
// decision is Ready / Ready with limitations / Passed with reviewed exception / Blocked / Not assessed, always with its reasons. Analyzer limitations
// nobody can resolve are Informational and never enter the review queue.

/// <summary>The action a gate decides.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GateAction { Review, Execution, Approval, DeliveryReport, ReleaseReadiness }

/// <summary>How a finding weighs on one action. Never "Partial".</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FindingLevel { Blocking, Warning, Informational, NotApplicable, NotAssessed }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GateDecision { Ready, ReadyWithLimitations, PassedWithException, Blocked, NotAssessed }

/// <summary>A versioned expectation. <see cref="Levels"/> is the default level per action (absent = Informational).</summary>
public sealed record GovernancePolicy(
    string Id, int Version, string Title, string Purpose, string Owner, string Resolution, bool Actionable, IReadOnlyDictionary<GateAction, FindingLevel> Levels)
{
    public FindingLevel LevelFor(GateAction action) => Levels.TryGetValue(action, out var level) ? level : FindingLevel.Informational;
}

/// <summary>Evidence that a policy needs attention: aggregated per policy (and module, when the evidence has one), with a few concrete examples.</summary>
public sealed record GovernanceFinding(
    string PolicyId, int PolicyVersion, string Title, string WhyItMatters, int Count, IReadOnlyList<string> Evidence, string Owner, string Resolution,
    IReadOnlyList<string> AffectedConsumers, string? Module = null, IReadOnlyList<string>? RuleIds = null);

/// <summary>A reviewed exception: changes one gate decision for one action (and module, when given). The finding and its evidence stay.</summary>
public sealed record GovernanceWaiver(string PolicyId, GateAction Action, string? Module, string Reason, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt = null)
{
    public bool Applies(GovernanceFinding f, GateAction action, DateTimeOffset now) =>
        f.PolicyId == PolicyId && action == Action && (Module is null || string.Equals(Module, f.Module, StringComparison.OrdinalIgnoreCase)) && (ExpiresAt is null || ExpiresAt > now);
}

/// <summary>A level set outside the policy default (project or environment), shown with where it comes from.</summary>
public sealed record GovernanceOverride(string PolicyId, GateAction Action, FindingLevel Level, string Source);

/// <summary>One finding as the gate weighed it: its effective level and where that level comes from, and the waiver that let it pass, if any.</summary>
public sealed record GateFinding(GovernanceFinding Finding, FindingLevel Level, string LevelSource, GovernanceWaiver? Waiver);

public sealed record GateResult(
    GateAction Action, GateDecision Decision, string Reason, IReadOnlyList<GateFinding> Findings, string PolicySetVersion, Guid? InterpretationId, DateTimeOffset EvaluatedAt,
    IReadOnlyList<string> ModuleScope)
{
    public int Blocking => Findings.Count(f => f.Level == FindingLevel.Blocking && f.Waiver is null);
    public int Warnings => Findings.Count(f => f.Level == FindingLevel.Warning);
    public int Waived => Findings.Count(f => f.Waiver is not null);
    /// <summary>For a CI step: non-zero only for an unwaived blocking finding. Warnings never fail CI.</summary>
    public int ExitCode => Decision == GateDecision.Blocked ? 1 : 0;
}

public static class GovernancePolicies
{
    public const string PolicySetVersion = "governance/1";

    private static IReadOnlyDictionary<GateAction, FindingLevel> L(params (GateAction, FindingLevel)[] levels) => levels.ToDictionary(l => l.Item1, l => l.Item2);
    private const FindingLevel B = FindingLevel.Blocking, W = FindingLevel.Warning;

    // New rules start as warnings where behaviour used to be permissive; only release / approval decisions that would rest on contradicted identity block.
    public static readonly GovernancePolicy IdentityConflict = new("gov.identity.conflict", 1, "Identity conflict",
        "Two declarations, or a reviewed decision and new source evidence, disagree about what something is. Anything built on it may point at the wrong system, contract or story.",
        "Human review", "Confirm the correct identity in Relationship Review.", true,
        L((GateAction.Review, W), (GateAction.Execution, W), (GateAction.DeliveryReport, W), (GateAction.Approval, B), (GateAction.ReleaseReadiness, B)));

    public static readonly GovernancePolicy StaleAssociation = new("gov.review.stale-association", 1, "Reviewed association to recheck",
        "A reviewed link was decided on evidence that has since changed. It is not silently re-applied or dropped.",
        "Human review", "Recheck the decision in Relationship Review: confirm it again or reject it.", true,
        L((GateAction.Review, W), (GateAction.Approval, W), (GateAction.DeliveryReport, W), (GateAction.ReleaseReadiness, W)));

    public static readonly GovernancePolicy Ambiguity = new("gov.traceability.ambiguous-reference", 1, "Ambiguous reference",
        "A reference matches several candidates (for example FR-001 defined by two feature packs). It is not merged by id text; until someone chooses, it counts for neither.",
        "Human review", "Choose the intended target in Relationship Review.", true,
        L((GateAction.Review, W), (GateAction.DeliveryReport, W), (GateAction.ReleaseReadiness, W)));

    public static readonly GovernancePolicy RemovedStory = new("gov.traceability.removed-story-reference", 1, "Task references a removed story",
        "A task still names a story the specification removed. Its work no longer traces to a current story.",
        "Delivery", CanonicalStoryResolution, true,
        L((GateAction.DeliveryReport, W), (GateAction.ReleaseReadiness, W)));

    public static readonly GovernancePolicy UnresolvedStory = new("gov.traceability.unresolved-story-reference", 1, "Task references an undefined story",
        "A task names a story the feature's specification does not define. Shared, foundational, functional-group and scenario-group work is never counted here.",
        "Delivery", CanonicalStoryResolution, true,
        L((GateAction.Review, W), (GateAction.DeliveryReport, W), (GateAction.ReleaseReadiness, W)));

    public static readonly GovernancePolicy NoTest = new("gov.coverage.requirement-without-test", 1, "Requirement without any test",
        "No test verifies the requirement, not even as a candidate. Its behaviour is unverified.",
        "Test coverage", "Link an existing test (explicit id in the test) or write one; scenario candidates in Functional Quality Scope are a starting point.", true,
        L((GateAction.ReleaseReadiness, W)));

    public static readonly GovernancePolicy CandidateOnly = new("gov.coverage.candidate-only", 1, "Requirement with only a suggested test",
        "The only test link is a naming-based suggestion. A suggested link never satisfies a gate that needs confirmed coverage.",
        "Test coverage", "Confirm or reject the suggested test link in Relationship Review, or reference the requirement id in the test.", true,
        L((GateAction.ReleaseReadiness, W)));

    public static readonly GovernancePolicy UnknownImpact = new("gov.impact.unknown", 1, "Unresolved relations",
        "References that resolve to nothing: whatever depends on them has Unknown impact, which never passes as \"unaffected\".",
        "Source Analysis", "Usually fixed in the source or tasks file; re-import and re-interpret afterwards.", true,
        L((GateAction.ReleaseReadiness, W)));

    public static readonly GovernancePolicy NotInterpreted = new("gov.analysis.not-interpreted", 1, "No canonical interpretation",
        "Nothing has been interpreted for this project yet, so no policy can be assessed.",
        "Relationship Review", "Interpret a Delivery snapshot with its source snapshots in Relationship Review.", true,
        L((GateAction.Review, FindingLevel.NotAssessed), (GateAction.Execution, FindingLevel.NotAssessed), (GateAction.Approval, FindingLevel.NotAssessed),
          (GateAction.DeliveryReport, FindingLevel.NotAssessed), (GateAction.ReleaseReadiness, FindingLevel.NotAssessed)));

    public static readonly GovernancePolicy Limitation = new("gov.analysis.limitation", 1, "Analysis limitation",
        "Something the analysis cannot see (unsupported language, runtime outside source-only analysis). It limits conclusions but is nobody's review task.",
        "Source Analysis", "None needed; the limitation is stated so conclusions are read with it.", false, L());

    public const string CanonicalStoryResolution = "Fix the reference in the tasks file (update the task, restore the story, or mark the task obsolete), then re-import. BirkNext never reassigns it.";

    public static readonly IReadOnlyList<GovernancePolicy> All =
        [IdentityConflict, StaleAssociation, Ambiguity, RemovedStory, UnresolvedStory, NoTest, CandidateOnly, UnknownImpact, NotInterpreted, Limitation];

    public static GovernancePolicy? Find(string id) => All.FirstOrDefault(p => p.Id == id);

    public static string ActionLabel(GateAction action) => action switch
    {
        GateAction.DeliveryReport => "Delivery report",
        GateAction.ReleaseReadiness => "Release readiness",
        _ => action.ToString(),
    };

    public static string DecisionLabel(GateDecision decision) => decision switch
    {
        GateDecision.ReadyWithLimitations => "Ready with limitations",
        GateDecision.PassedWithException => "Passed with reviewed exception",
        GateDecision.NotAssessed => "Not assessed",
        _ => decision.ToString(),
    };

    public static string LevelLabel(FindingLevel level) => level switch
    {
        FindingLevel.Warning => "Non-blocking warning",
        FindingLevel.NotApplicable => "Not applicable",
        FindingLevel.NotAssessed => "Not assessed",
        _ => level.ToString(),
    };
}

public static class GovernanceGate
{
    /// <summary>
    /// The gate decision for one action. Effective level = override (with its source) ?? policy default. Modules, when given, keep findings of those
    /// modules plus global findings (no module): an unrelated module's finding never blocks this scope. An unwaived Blocking finding blocks; a waived one
    /// passes WITH the exception shown; warnings give Ready with limitations; a Not assessed finding (nothing interpreted) gives Not assessed.
    /// </summary>
    public static GateResult Evaluate(GateAction action, IReadOnlyList<GovernanceFinding> findings, Guid? interpretationId, DateTimeOffset now,
        IReadOnlyCollection<string>? modules = null, IReadOnlyList<GovernanceWaiver>? waivers = null, IReadOnlyList<GovernanceOverride>? overrides = null)
    {
        var moduleSet = modules is { Count: > 0 } ? new HashSet<string>(modules, StringComparer.OrdinalIgnoreCase) : null;
        var weighed = new List<GateFinding>();
        foreach (var f in findings)
        {
            if (moduleSet is not null && f.Module is not null && !moduleSet.Contains(f.Module)) continue;
            var policy = GovernancePolicies.Find(f.PolicyId);
            var over = overrides?.FirstOrDefault(o => o.PolicyId == f.PolicyId && o.Action == action);
            var level = over?.Level ?? policy?.LevelFor(action) ?? FindingLevel.Informational;
            if (level == FindingLevel.NotApplicable) continue;
            var waiver = level == FindingLevel.Blocking ? waivers?.FirstOrDefault(w => w.Applies(f, action, now)) : null;
            weighed.Add(new(f, level, over?.Source ?? $"Policy default ({f.PolicyId} v{f.PolicyVersion})", waiver));
        }
        weighed = [.. weighed.OrderBy(w => w.Waiver is null ? (int)w.Level : 1).ThenBy(w => w.Finding.Title, StringComparer.Ordinal).ThenBy(w => w.Finding.Module, StringComparer.Ordinal)];

        var blocking = weighed.Count(w => w.Level == FindingLevel.Blocking && w.Waiver is null);
        var waived = weighed.Count(w => w.Waiver is not null);
        var warnings = weighed.Count(w => w.Level == FindingLevel.Warning);
        var (decision, reason) =
            weighed.Any(w => w.Level == FindingLevel.NotAssessed) ? (GateDecision.NotAssessed, weighed.First(w => w.Level == FindingLevel.NotAssessed).Finding.WhyItMatters)
            : blocking > 0 ? (GateDecision.Blocked, $"{blocking} blocking finding{(blocking == 1 ? "" : "s")} for {GovernancePolicies.ActionLabel(action).ToLowerInvariant()}.")
            : waived > 0 ? (GateDecision.PassedWithException, $"{waived} blocking finding{(waived == 1 ? " is" : "s are")} accepted by a reviewed exception; the evidence is unchanged.")
            : warnings > 0 ? (GateDecision.ReadyWithLimitations, $"{warnings} non-blocking warning{(warnings == 1 ? "" : "s")}.")
            : (GateDecision.Ready, "No policy needs attention for this action.");
        return new(action, decision, reason, weighed, GovernancePolicies.PolicySetVersion, interpretationId, now, moduleSet is null ? [] : [.. moduleSet.Order(StringComparer.OrdinalIgnoreCase)]);
    }
}
