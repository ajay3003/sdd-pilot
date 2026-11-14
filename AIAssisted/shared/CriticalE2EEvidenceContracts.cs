using System.Text.Json.Serialization;
using BirkNext.TestData;

namespace BirkNext.CriticalE2E;

// ── Critical E2E as release evidence ──────────────────────────────────────────────────────────────────────────────────────
// Discovery proposes, the user selects, Browser Companion executes, assertions prove the outcome. Test Data owns reusable
// data (referenced by alias), topology defines automation boundaries, delivery evidence defines the build. A flow never
// counts as release evidence because clicks succeeded, a free-text module never creates release authority, and a browser
// pass never claims downstream success.

/// <summary>How a selector is expected to survive change. Never only a colour: the label is always shown.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CompanionSelectorStability { Preferred, Acceptable, Fragile }

/// <summary>Where a flow came from. Provenance only — never execution truth.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CriticalE2EFlowSource { Manual, BrowserDiscovery, ObservedJourney, RequirementDriven, Imported, SuggestedFromSource }

/// <summary>The concrete outcome a flow proves. Each kind is executable (browser) or explicitly provider-verified.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CriticalE2EOutcomeKind
{
    UrlEquals, UrlContains, ElementVisible, ElementHidden, TextEquals, TextContains, ValueEquals, SuccessMessageVisible, ListContains,
    /// <summary>A domain/business state verified by an approved evidence provider (outside the browser).</summary>
    ProviderVerified,
}

/// <summary>
/// The final business outcome: what must be true for the flow to pass. "The last click succeeded" is not one. A
/// <see cref="ProviderVerified"/> outcome names the provider; without a configured provider the run is inconclusive, never passed.
/// </summary>
public sealed record CriticalE2EFinalOutcome
{
    public CriticalE2EOutcomeKind Kind { get; init; } = CriticalE2EOutcomeKind.UrlEquals;
    /// <summary>The element the outcome is about (visible/hidden/text/value/list/success message).</summary>
    public CompanionSelector? Selector { get; init; }
    /// <summary>Expected path, text or value. May reference central Test Data as ${data:alias} (expected/reference asset).</summary>
    public string? Expected { get; init; }
    /// <summary>For <see cref="CriticalE2EOutcomeKind.ProviderVerified"/>: the verification provider id.</summary>
    public string? ProviderId { get; init; }
    public string Description { get; init; } = "";
}

/// <summary>Where automation starts and ends, and who verifies what lies beyond it. Structured, with provenance.</summary>
public sealed record CriticalE2EAutomationBoundary
{
    public string Start { get; init; } = "";
    public string End { get; init; } = "";
    /// <summary>Downstream systems this flow does NOT verify, and who does (e.g. "Service Bus / Bufdata verified separately").</summary>
    public string Downstream { get; init; } = "";
    /// <summary>True when the flow's own steps verify the downstream part too (full end-to-end).</summary>
    public bool CoversDownstream { get; init; }
    /// <summary>"Manual", "Integration Topology" or "Journey definition".</summary>
    public string Source { get; init; } = "Manual";
    public string? Note { get; init; }
    public bool Explicit => Start.Length > 0 && End.Length > 0;
}

/// <summary>A step placeholder or form value mapped to central Test Data assets (aliases). Values stay in Test Data; the backend
/// resolves them only while the run executes.</summary>
public sealed record CriticalE2EDataMapping(string Variable, List<string> AssetAliases, TestDataDataType DataType, string Target)
{
    /// <summary>Stable TestDataAssetIds, position-aligned with <see cref="AssetAliases"/> (null entry = legacy alias-only reference).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public List<string?>? AssetIds { get; init; }
}

/// <summary>A release-readiness blocker with the action that resolves it.</summary>
public sealed record CriticalE2EReleaseBlocker(string Key, string Text);

/// <summary>Whether a flow definition can be release evidence (independent of any run). Build identity is checked separately.</summary>
public sealed record CriticalE2EFlowReadiness
{
    public bool ReleaseReady => Blockers.Count == 0;
    public List<CriticalE2EReleaseBlocker> Blockers { get; init; } = [];
    public bool HasConcreteOutcome { get; init; }
    public bool LegacyAssertionMarker { get; init; }
    public bool ModuleResolved { get; init; }
    /// <summary>Ready · Missing · Needs review · Not required.</summary>
    public string TestDataStatus { get; init; } = "Not required";
    /// <summary>"Release requirement configured · not release-ready" etc.</summary>
    public string Summary { get; init; } = "";
}

public static class CriticalE2ERoutes
{
    /// <summary>A route as identity: path only (no query or fragment), trailing slash removed, identifier-like segments replaced by {id}
    /// (the shared path-template rule) so an observed identifier is never stored in a flow. Existing {param} segments are kept.</summary>
    public static string Normalize(string? route)
    {
        if (string.IsNullOrWhiteSpace(route)) return "";
        var path = route.Trim();
        if (Uri.TryCreate(path, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https") path = absolute.AbsolutePath;
        return PerformanceTests.PerformanceScenarioEngine.Template(path);
    }

    /// <summary>Does a live route match a stored route template? {param} segments match any segment; the query is ignored.</summary>
    public static bool Matches(string? template, string? route)
    {
        if (string.IsNullOrWhiteSpace(template)) return true;
        if (route is null) return false;
        var t = Normalize(template).Split('/', StringSplitOptions.RemoveEmptyEntries);
        var r = route.Split('?', '#')[0].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (t.Length != r.Length) return false;
        for (var i = 0; i < t.Length; i++)
            if (!(t[i].StartsWith('{') && t[i].EndsWith('}')) && !string.Equals(t[i], r[i], StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
}

public static class CompanionSelectorQuality
{
    /// <summary>Role + accessible name, then a stable test id, then label, then text, then structural CSS (fallback only).</summary>
    public static int Rank(CompanionSelector s) => s.Kind switch
    {
        CompanionSelectorKind.Role when !string.IsNullOrWhiteSpace(s.Name) => 0,
        CompanionSelectorKind.TestId => 1,
        CompanionSelectorKind.Label => 2,
        CompanionSelectorKind.Role => 3,
        CompanionSelectorKind.Text => 3,
        _ => 4,
    };

    public static CompanionSelectorStability Stability(CompanionSelector? s) => s is null ? CompanionSelectorStability.Fragile : Rank(s) switch
    {
        0 or 1 => CompanionSelectorStability.Preferred,
        2 or 3 => CompanionSelectorStability.Acceptable,
        _ => CompanionSelectorStability.Fragile,
    };

    public static string Label(CompanionSelectorStability stability) => stability switch
    {
        CompanionSelectorStability.Preferred => "Preferred",
        CompanionSelectorStability.Acceptable => "Acceptable",
        _ => "Fragile",
    };

    public static string? Warning(CompanionSelector? s) =>
        Stability(s) == CompanionSelectorStability.Fragile ? "Fragile selector · layout changes may break this step" : null;

    /// <summary>The best unique candidate by this ranking (never a non-unique one).</summary>
    public static CompanionSelector? Best(IEnumerable<CompanionSelectorCandidate> candidates) =>
        candidates.Where(c => c.Unique).OrderBy(c => Rank(c.Selector)).Select(c => c.Selector).FirstOrDefault();
}

/// <summary>Element-aware authoring proposals. The user edits or rejects them; nothing is saved or run by suggesting.</summary>
public static class CriticalE2EAuthoring
{
    public static CompanionActionKind SuggestAction(CompanionElementDescriptor e)
    {
        var tag = e.TagName.ToLowerInvariant();
        var role = (e.Role ?? "").ToLowerInvariant();
        var type = (e.InputType ?? "").ToLowerInvariant();
        if (tag == "select" || role is "combobox" or "listbox") return CompanionActionKind.Select;
        if (tag == "textarea" || tag == "input" && type is "" or "text" or "email" or "search" or "tel" or "number" or "date" or "url" or "password" || role is "textbox" or "searchbox")
            return CompanionActionKind.Fill;
        return CompanionActionKind.Click;
    }

    /// <summary>"Open Generelle roller", "Fill Child identifier", "Select Kommune", "Check Samtykke", "Submit Lagre", "Click Lagre".</summary>
    public static string SuggestName(CompanionActionKind action, CompanionElementDescriptor e)
    {
        var subject = (e.AccessibleName ?? e.Label ?? e.TestId ?? e.TagName).Trim();
        if (subject.Length > 50) subject = subject[..50].TrimEnd() + "…";
        var role = (e.Role ?? "").ToLowerInvariant();
        var type = (e.InputType ?? "").ToLowerInvariant();
        return action switch
        {
            CompanionActionKind.Fill => $"Fill {subject}",
            CompanionActionKind.Select => $"Select {subject}",
            CompanionActionKind.Click when role == "link" || e.Href is not null => $"Open {subject}",
            CompanionActionKind.Click when role is "checkbox" or "radio" || type is "checkbox" or "radio" => $"Check {subject}",
            CompanionActionKind.Click when type == "submit" => $"Submit {subject}",
            _ => $"Click {subject}",
        };
    }

    /// <summary>A link's own target path is a proposed expected destination (the user approves it). Otherwise nothing is inferred.</summary>
    public static string? SuggestDestination(CompanionElementDescriptor e) =>
        e.Href is { Length: > 0 } href && Uri.TryCreate(href, UriKind.RelativeOrAbsolute, out var uri)
            ? CriticalE2ERoutes.Normalize(uri.IsAbsoluteUri ? uri.AbsolutePath : href) : null;

    public static bool IsAssertion(CompanionActionKind? action) => action is CompanionActionKind.AssertRoute or CompanionActionKind.AssertText
        or CompanionActionKind.AssertValue or CompanionActionKind.AssertVisible or CompanionActionKind.AssertHidden;
}

public static class CriticalE2EReleaseRules
{
    /// <summary>Concrete outcome: a flow-level final outcome, or a final step that is itself an assertion (browser Assert*, or an
    /// integration step with an expectation). A final-marked click is a legacy marker, never an outcome.</summary>
    public static bool HasConcreteOutcome(CriticalE2EFlowDefinition flow) =>
        flow.FinalOutcome is not null
        || flow.Steps.Any(s => s.IsFinalAssertion && (CriticalE2EAuthoring.IsAssertion(s.BrowserAction) || s.IsIntegrationStep && s.Expect is not null));

    public static bool LegacyAssertionMarker(CriticalE2EFlowDefinition flow) => !HasConcreteOutcome(flow) && flow.Steps.Any(s => s.IsFinalAssertion);

    public static int Assertions(CriticalE2EFlowDefinition flow) =>
        flow.Steps.Count(s => CriticalE2EAuthoring.IsAssertion(s.BrowserAction) || s.IsIntegrationStep && s.Expect is not null) + (flow.FinalOutcome is null ? 0 : 1)
        + flow.Steps.Count(s => !string.IsNullOrWhiteSpace(s.ExpectedDestination));

    public static string OutcomeText(CriticalE2EFlowDefinition flow)
    {
        if (flow.FinalOutcome is { } o) return Describe(o);
        var final = flow.Steps.LastOrDefault(s => s.IsFinalAssertion);
        return final is null ? "No final outcome" : HasConcreteOutcome(flow) ? final.Description : "Legacy assertion marker · concrete outcome required";
    }

    public static string Describe(CriticalE2EFinalOutcome o) => o.Description.Length > 0 ? o.Description : o.Kind switch
    {
        CriticalE2EOutcomeKind.UrlEquals => $"Page {o.Expected} is loaded",
        CriticalE2EOutcomeKind.UrlContains => $"Page path contains {o.Expected}",
        CriticalE2EOutcomeKind.ElementVisible => $"{o.Selector?.Describe()} is visible",
        CriticalE2EOutcomeKind.ElementHidden => $"{o.Selector?.Describe()} is hidden",
        CriticalE2EOutcomeKind.TextEquals => $"{o.Selector?.Describe()} reads \"{o.Expected}\"",
        CriticalE2EOutcomeKind.TextContains => $"{o.Selector?.Describe()} contains \"{o.Expected}\"",
        CriticalE2EOutcomeKind.ValueEquals => $"{o.Selector?.Describe()} has value \"{o.Expected}\"",
        CriticalE2EOutcomeKind.SuccessMessageVisible => $"Success message \"{o.Expected}\" is visible",
        CriticalE2EOutcomeKind.ListContains => $"{o.Selector?.Describe()} contains \"{o.Expected}\"",
        _ => $"Verification provider {o.ProviderId ?? "(none)"} confirms the expected state",
    };

    /// <param name="registeredModules">Authoritative delivery modules (registered for the environment). A label outside them is a manual label.</param>
    /// <param name="testDataIssues">Problems of the mapped central Test Data (unknown, unapproved, wrong type); empty = ready.</param>
    public static CriticalE2EFlowReadiness Evaluate(CriticalE2EFlowDefinition flow, IReadOnlyCollection<string> registeredModules, IReadOnlyList<string> testDataIssues)
    {
        var blockers = new List<CriticalE2EReleaseBlocker>();
        var moduleResolved = registeredModules.Contains(flow.Module.Trim(), StringComparer.OrdinalIgnoreCase);
        var concrete = HasConcreteOutcome(flow);
        var legacy = LegacyAssertionMarker(flow);
        if (string.IsNullOrWhiteSpace(flow.Module)) blockers.Add(new("module", "No delivery module is selected."));
        else if (!moduleResolved) blockers.Add(new("module", $"Unresolved module \"{flow.Module}\" · manual label · does not count toward release coverage until it is a registered delivery module."));
        if (!flow.Enabled) blockers.Add(new("enabled", "The flow is disabled."));
        if (flow.Kind != CriticalE2EFlowKind.Critical) blockers.Add(new("kind", "Diagnostic flows are never release evidence."));
        if (!concrete) blockers.Add(new("outcome", legacy ? "Legacy assertion marker · concrete outcome required." : "No final business outcome: add a concrete assertion."));
        if (flow.ConfigurationProblem() is { } problem && concrete) blockers.Add(new("runnable", problem));
        if (flow.Steps.Where(s => s.IsBrowserStep && s.Selector is not null && !s.SelectorReviewed).Any(s => CompanionSelectorQuality.Stability(s.Selector) == CompanionSelectorStability.Fragile))
            blockers.Add(new("selector", "A step uses a fragile selector: review it (or choose a role/test-id selector) before release use."));
        if (flow.Boundary is not { Explicit: true }) blockers.Add(new("boundary", "The automation boundary (start and end) is not explicit."));
        if (testDataIssues.Count > 0) blockers.Add(new("testdata", testDataIssues[0]));
        var testDataStatus = testDataIssues.Count > 0
            ? testDataIssues.Any(i => i.StartsWith("Test Data missing", StringComparison.Ordinal)) ? "Missing" : "Needs review"
            : flow.DataMappings is not { Count: > 0 } && flow.TestDataAliases is not { Count: > 0 } ? "Not required" : "Ready";
        return new CriticalE2EFlowReadiness
        {
            Blockers = blockers, HasConcreteOutcome = concrete, LegacyAssertionMarker = legacy, ModuleResolved = moduleResolved, TestDataStatus = testDataStatus,
            Summary = !flow.RequiredForRelease ? blockers.Count == 0 ? "Release-ready definition (not required for release)" : "Not required for release"
                : blockers.Count == 0 ? "Required for release · definition release-ready" : "Release requirement configured · not release-ready",
        };
    }

    /// <summary>The run's result label: never "release pass" for a diagnostic run, never "E2E passed" when downstream is outside the boundary.</summary>
    public static string ResultLabel(CriticalE2ERunResult run) => run.Status switch
    {
        CriticalE2EStatus.Passed when !run.ReleaseEvidence => run.DownstreamVerified ? "Diagnostic pass" : "Diagnostic pass · browser journey only",
        CriticalE2EStatus.Passed when !run.DownstreamVerified => "Browser journey passed · downstream not verified by this flow",
        CriticalE2EStatus.Passed => "Passed",
        CriticalE2EStatus.Failed => "Failed",
        CriticalE2EStatus.Blocked => "Blocked",
        CriticalE2EStatus.Inconclusive => "Inconclusive",
        CriticalE2EStatus.Cancelled => "Cancelled",
        CriticalE2EStatus.Running => "Running",
        _ => "Not run",
    };
}

/// <summary>
/// Versioned release gate policy. <see cref="Legacy"/> ("critical-e2e-gate/1") is the gate as it was before test types: every critical,
/// release-ready required flow that passed on the build counts. <see cref="Current"/> ("critical-e2e-gate/2") adds the test type:
/// <list type="bullet">
/// <item>UI smoke never satisfies Business E2E; UI functional is functional coverage only; Integration/API is integration coverage only.</item>
/// <item>Business E2E counts only when the flow is Critical AND release-ready AND has passed execution evidence on the named build.</item>
/// <item>Criticality alone never overrides the test type. No passed execution evidence, no passed-flow credit.</item>
/// <item>Unknown (stored before test types) is never silently Business E2E: "Legacy classification requires review" — never PASS.</item>
/// </list>
/// Historical results stay as they were: the policy is chosen per build from the evidence recorded on it. Runs record the policy in force
/// (<see cref="CriticalE2ERunResult.GatePolicyVersion"/>); a build whose evidence is only runs without it is evaluated by the legacy policy.
/// Pure: nothing here starts a run, stores anything or contacts anything.
/// </summary>
public static class CriticalE2EGatePolicy
{
    public const string Legacy = "critical-e2e-gate/1";
    public const string Current = "critical-e2e-gate/2";

    public static bool IsLegacy(string? version) => version == Legacy;

    /// <summary>"critical-e2e-gate/2 · test type aware" / "critical-e2e-gate/1 · legacy (criticality only)".</summary>
    public static string Label(string? version) => IsLegacy(version)
        ? $"{Legacy} · legacy (criticality only, evidence recorded before test types)"
        : $"{Current} · test type aware";

    /// <summary>
    /// The policy that evaluates a build. No build, or no run recorded on it: <see cref="Current"/>. Only runs recorded before versioned
    /// policies (no <see cref="CriticalE2ERunResult.GatePolicyVersion"/>): <see cref="Legacy"/>, so that build's verdict is what it was.
    /// Any run recorded under a versioned policy makes it a new evaluation under <see cref="Current"/>.
    /// </summary>
    public static string ForBuild(IEnumerable<CriticalE2ERunResult> history, string? buildId)
    {
        if (string.IsNullOrWhiteSpace(buildId)) return Current;
        var onBuild = history.Where(r => string.Equals(r.BuildId?.Trim(), buildId.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        return onBuild.Count > 0 && onBuild.All(r => string.IsNullOrWhiteSpace(r.GatePolicyVersion)) ? Legacy : Current;
    }

    /// <summary>"counts", "functional-only", "integration-only", "never", "legacy-review", "not-executed", "not-ready".</summary>
    public static string Token(CriticalE2EGateEligibilityKind kind) => kind switch
    {
        CriticalE2EGateEligibilityKind.Counts => "counts",
        CriticalE2EGateEligibilityKind.FunctionalOnly => "functional-only",
        CriticalE2EGateEligibilityKind.IntegrationOnly => "integration-only",
        CriticalE2EGateEligibilityKind.Never => "never",
        CriticalE2EGateEligibilityKind.LegacyReview => "legacy-review",
        CriticalE2EGateEligibilityKind.NotReady => "not-ready",
        _ => "not-executed",
    };

    /// <summary>The short badge text; the reason carries the why.</summary>
    public static string ShortLabel(CriticalE2EGateEligibilityKind kind) => kind switch
    {
        CriticalE2EGateEligibilityKind.Counts => "Counts toward Business E2E",
        CriticalE2EGateEligibilityKind.FunctionalOnly => "Functional only",
        CriticalE2EGateEligibilityKind.IntegrationOnly => "Integration only",
        CriticalE2EGateEligibilityKind.Never => "Never counts",
        CriticalE2EGateEligibilityKind.LegacyReview => "Legacy review",
        CriticalE2EGateEligibilityKind.NotReady => "Not release-ready",
        _ => "No passed run",
    };

    public const string LegacyReviewReason = "Legacy classification requires review — classify the flow's test type in the editor before it can count toward Business E2E";

    /// <param name="policyVersion">The policy evaluating the build (<see cref="ForBuild"/>).</param>
    /// <param name="evidenceStatus">The status of the run that is release evidence for the named build (latest on it, current definition,
    /// release evidence when recorded); null when there is none.</param>
    public static CriticalE2EGateEligibility Evaluate(string policyVersion, CriticalE2EFlowKind kind, CriticalE2ETestType testType,
        CriticalE2EFlowReadiness readiness, bool buildNamed, CriticalE2EStatus? evidenceStatus)
    {
        var legacy = IsLegacy(policyVersion);
        CriticalE2EGateEligibility Result(CriticalE2EGateEligibilityKind eligibility, string reason, bool counts) =>
            new() { PolicyVersion = legacy ? Legacy : Current, Eligibility = eligibility, Reason = reason, CountsTowardBusinessE2E = counts };

        if (kind != CriticalE2EFlowKind.Critical)
            return Result(CriticalE2EGateEligibilityKind.Never, legacy ? "Diagnostic flow — never release evidence"
                : "Diagnostic flow — never counts toward Business E2E, whatever its test type", false);

        if (!legacy)
        {
            // The test type decides what a critical flow can prove; criticality never overrides it.
            switch (testType)
            {
                case CriticalE2ETestType.UiSmoke: return Result(CriticalE2EGateEligibilityKind.Never, "UI smoke — never counts toward Business E2E", false);
                case CriticalE2ETestType.UiFunctional: return Result(CriticalE2EGateEligibilityKind.FunctionalOnly, "UI functional — counts as functional coverage only, not Business E2E", false);
                case CriticalE2ETestType.IntegrationApi: return Result(CriticalE2EGateEligibilityKind.IntegrationOnly, "Integration/API — counts toward integration coverage only, not Business E2E", false);
                case CriticalE2ETestType.Other: return Result(CriticalE2EGateEligibilityKind.Never, "Other test type — never counts toward Business E2E", false);
                case CriticalE2ETestType.Unknown: return Result(CriticalE2EGateEligibilityKind.LegacyReview, LegacyReviewReason, false);
            }
        }

        var subject = legacy ? "Critical flow (legacy policy)" : "Business E2E";
        if (!readiness.ReleaseReady)
            return Result(CriticalE2EGateEligibilityKind.NotReady, $"{subject} — definition not release-ready: {readiness.Blockers.FirstOrDefault()?.Text ?? "see blockers"}", true);
        if (!buildNamed) return Result(CriticalE2EGateEligibilityKind.NotExecuted, $"{subject} — no build selected, so no passed-run credit", true);
        return evidenceStatus switch
        {
            CriticalE2EStatus.Passed => Result(CriticalE2EGateEligibilityKind.Counts, legacy
                ? "Counts — critical, release-ready and passed on this build (legacy policy: evidence recorded before test types)"
                : "Counts — Business E2E, critical, release-ready and passed on this build", true),
            CriticalE2EStatus.Failed => Result(CriticalE2EGateEligibilityKind.NotExecuted, $"{subject} — failed on this build, no credit", true),
            null or CriticalE2EStatus.NotRun => Result(CriticalE2EGateEligibilityKind.NotExecuted, $"{subject} — no passed run on this build, no credit", true),
            { } status => Result(CriticalE2EGateEligibilityKind.NotExecuted, $"{subject} — {status.ToString().ToLowerInvariant()} on this build, no credit", true),
        };
    }
}

/// <summary>
/// Boundary proposals from Integration Topology. A browser flow normally ends at browser-visible acceptance; what happens downstream
/// (messaging, resources, external systems) belongs to the providers that verify it. A proposal only — the author applies or edits it.
/// </summary>
public static class CriticalE2EBoundarySuggestion
{
    public static CriticalE2EAutomationBoundary For(CriticalE2EExecutionMode mode, BirkNext.IntegrationTopology.IntegrationTopologyView? topology)
    {
        // A topology with no components says nothing about this environment; it is not a source.
        if (topology is { Components.Count: 0 }) topology = null;
        var downstream = topology is null ? [] : topology.Components
            .Where(c => c.Kind is BirkNext.IntegrationTopology.TopologyComponentKind.Resource or BirkNext.IntegrationTopology.TopologyComponentKind.External
                || topology.Connections.Any(x => x.Type == BirkNext.IntegrationTopology.TopologyInterfaceType.Messaging && x.ConsumerComponentId == c.Id))
            .Select(c => c.Name).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).Take(4).ToList();
        var browser = mode == CriticalE2EExecutionMode.CompanionBrowser;
        return new CriticalE2EAutomationBoundary
        {
            Start = browser ? "Approved application page (Browser Companion)" : "Configured integration (authenticated API context)",
            End = browser ? "Browser-visible outcome" : "Integration response / downstream query",
            Downstream = downstream.Count == 0 ? "" : $"{string.Join(", ", downstream)} — verified separately (Integration Quality Review)",
            Source = topology is null ? "Manual" : "Integration Topology",
            Note = topology is null ? "No Integration Topology is available for this environment; boundary proposed from the execution mode only."
                : downstream.Count == 0 ? "Integration Topology lists no downstream systems for this environment." : null,
        };
    }
}
