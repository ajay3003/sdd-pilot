using System.Text.Json.Serialization;

namespace BirkNext.Web.Models;

/// <summary>
/// Generic capabilities an engine can need, independent of the implementation that provides them. An engine names capabilities;
/// providers (Playwright, the authenticated review browser, the Browser Companion, a bundled or local Chromium, the Local HTTPS proxy)
/// are resolved at review time. "Playwright DOM" or "Node + Chrome" are never a prerequisite by themselves.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FrontendQualityCapabilityKind
{
    /// <summary>Anonymous HTTP to the frontend host (document and static assets).</summary>
    PublicHttp,
    /// <summary>Approved authenticated HTTP through the backend gateway (Local HTTPS proxy API context).</summary>
    AuthenticatedHttp,
    /// <summary>A rendered DOM of the public application that a backend browser can open.</summary>
    BrowserDom,
    /// <summary>A rendered DOM of the signed-in application (authenticated review browser session).</summary>
    AuthenticatedBrowserDom,
    /// <summary>A live DOM in the user's own browser, inspected by the paired Browser Companion. Its sign-in state is not recorded.</summary>
    CompanionLiveDom,
    /// <summary>A Node.js execution runtime (the Lighthouse package runs in it).</summary>
    NodeRuntime,
    /// <summary>A Chromium-family browser reachable over the DevTools protocol. Compatibility is the protocol, never the product name.</summary>
    CdpTarget,
    /// <summary>A container runtime (OWASP ZAP for Passive Web Security).</summary>
    ContainerRuntime,
    /// <summary>Evidence recorded by the Browser Companion or the Local HTTPS proxy; the consuming engine decides whether it is enough.</summary>
    RecordedBrowserEvidence,
}

/// <summary>How a capability stands for this review. Unsupported = the selected method can never provide it; Unavailable = it could, but not now.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FrontendQualityCapabilityAvailability { Available, Unavailable, Unsupported, CheckedAtRun }

/// <summary>One resolved capability: whether it is there, which provider supplies it, and why not when it is not.</summary>
public sealed record FrontendQualityCapability(
    [property: JsonPropertyName("kind")] FrontendQualityCapabilityKind Kind,
    [property: JsonPropertyName("state")] FrontendQualityCapabilityAvailability State,
    [property: JsonPropertyName("provider")] string? Provider,
    [property: JsonPropertyName("reason")] string Reason)
{
    /// <summary>Usable for planning: available now, or verified only when the engine runs (readiness revalidation still applies).</summary>
    [JsonIgnore] public bool Usable => State is FrontendQualityCapabilityAvailability.Available or FrontendQualityCapabilityAvailability.CheckedAtRun;
}

/// <summary>The application surface an engine assesses. A Browser Companion page is its own surface: its sign-in state is not recorded, so it never counts as public or authenticated coverage.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FrontendQualityReviewSurface { Public, Authenticated, CompanionPage }

/// <summary>What the planner decided for an engine (or one surface of it). Never "No evidence": that is a result after a run.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FrontendQualityPlanDecision
{
    /// <summary>The engine will execute.</summary>
    Run,
    /// <summary>The engine does not apply to the selected review scope or surface.</summary>
    NotApplicable,
    /// <summary>The engine could apply, but a capability it needs is not available now.</summary>
    NotAssessed,
    /// <summary>The current access method can never provide what the engine needs.</summary>
    Unsupported,
    /// <summary>Disabled in the Target Environment, or deselected for this review. Not part of the review.</summary>
    Excluded,
}

/// <summary>Effect of an engine on the release decision.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FrontendQualityReleaseImpact { Satisfied, Blocking, NonBlocking, NotApplicable }

/// <summary>The plan for one surface of one engine.</summary>
public sealed record FrontendQualitySurfacePlan(
    [property: JsonPropertyName("surface")] FrontendQualityReviewSurface Surface,
    [property: JsonPropertyName("decision")] FrontendQualityPlanDecision Decision,
    [property: JsonPropertyName("prerequisite")] string Prerequisite,
    [property: JsonPropertyName("provider")] string? Provider,
    [property: JsonPropertyName("reason")] string Reason,
    /// <summary>Counts toward the engine's completion (its configured prerequisite's surface). A secondary surface only adds coverage.</summary>
    [property: JsonPropertyName("primary")] bool Primary);

/// <summary>
/// The execution plan of one engine: saved state and policy, the review scope, every surface considered, and the decision with its reason
/// and release impact. Testable without UI; the post-run Coverage view renders it.
/// </summary>
public sealed record FrontendQualityEnginePlan
{
    [JsonPropertyName("engineId")] public FrontendQualityEngineId EngineId { get; init; }
    [JsonPropertyName("displayName")] public string DisplayName { get; init; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    [JsonPropertyName("selected")] public bool Selected { get; init; } = true;
    [JsonPropertyName("policy")] public FrontendQualityEngineRequirement Policy { get; init; }
    [JsonPropertyName("scope")] public FrontendReviewAccessScope Scope { get; init; }
    /// <summary>The engine's configured prerequisite in generic capability terms ("Public HTTP", "Browser DOM", "Node runtime + Chromium/CDP target").</summary>
    [JsonPropertyName("prerequisite")] public string Prerequisite { get; init; } = "";
    [JsonPropertyName("surfaces")] public IReadOnlyList<FrontendQualitySurfacePlan> Surfaces { get; init; } = [];
    [JsonPropertyName("decision")] public FrontendQualityPlanDecision Decision { get; init; }
    [JsonPropertyName("reason")] public string Reason { get; init; } = "";
    [JsonPropertyName("releaseImpact")] public FrontendQualityReleaseImpact ReleaseImpact { get; init; }
    /// <summary>Part of the coverage denominators: enabled, selected and applicable to at least one surface in scope.</summary>
    [JsonPropertyName("applicable")] public bool Applicable { get; init; }

    /// <summary>The surfaces the engine will run on, for display ("Public", "Authenticated", "Public, Browser Companion page").</summary>
    [JsonIgnore] public IReadOnlyList<FrontendQualityReviewSurface> RunSurfaces =>
        Surfaces.Where(s => s.Decision == FrontendQualityPlanDecision.Run).Select(s => s.Surface).Distinct().ToList();

    public FrontendQualitySurfacePlan? For(FrontendQualityReviewSurface surface) => Surfaces.FirstOrDefault(s => s.Surface == surface);
}

/// <summary>The execution plan of one review: the capabilities resolved for it and one plan per engine.</summary>
public sealed record FrontendQualityExecutionPlan
{
    [JsonPropertyName("scope")] public FrontendReviewAccessScope Scope { get; init; }
    [JsonPropertyName("capabilities")] public IReadOnlyList<FrontendQualityCapability> Capabilities { get; init; } = [];
    [JsonPropertyName("engines")] public IReadOnlyList<FrontendQualityEnginePlan> Engines { get; init; } = [];

    public FrontendQualityEnginePlan? For(FrontendQualityEngineId engineId) => Engines.FirstOrDefault(e => e.EngineId == engineId);
    public FrontendQualityCapability? Capability(FrontendQualityCapabilityKind kind) => Capabilities.FirstOrDefault(c => c.Kind == kind);
}

/// <summary>Why a review is blocked: one entry per exact cause, never a generic sentence.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FrontendQualityBlockerType
{
    /// <summary>A required, applicable engine did not complete.</summary>
    Coverage,
    /// <summary>A logical issue matched a rule of the Target Environment release policy.</summary>
    IssuePolicy,
    /// <summary>The saved configuration itself is inconsistent (for example a Required engine that is disabled).</summary>
    Configuration,
}

public sealed record FrontendQualityReleaseBlocker(
    [property: JsonPropertyName("type")] FrontendQualityBlockerType Type,
    [property: JsonPropertyName("entity")] string Entity,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("rule")] string? Rule = null,
    [property: JsonPropertyName("releaseImpact")] FrontendQualityReleaseImpact ReleaseImpact = FrontendQualityReleaseImpact.Blocking);

/// <summary>Result of one engine on one surface after the run (from the coverage passes and the plan).</summary>
public sealed record FrontendQualitySurfaceResult(
    [property: JsonPropertyName("surface")] FrontendQualityReviewSurface Surface,
    [property: JsonPropertyName("result")] string Result,
    [property: JsonPropertyName("completed")] bool Completed,
    [property: JsonPropertyName("provider")] string? Provider,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("primary")] bool Primary);
