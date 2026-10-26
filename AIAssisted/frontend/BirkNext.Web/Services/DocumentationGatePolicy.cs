using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Whether a project policy gives Document Quality Review release significance. Without one (<see cref="None"/>, the only policy BirkNext
/// knows today) Delivery Readiness is a documentation-readiness observation: advisory, never a product release decision. A policy that does
/// promote it names its source and threshold, so "release gate" wording always has provenance.
/// </summary>
public sealed record DocumentationGatePolicy(bool ReleaseSignificant, string? Source, double? MinimumReleaseScore)
{
    public static DocumentationGatePolicy None { get; } = new(false, null, null);

    /// <summary>"Policy: Release gate per <source> (minimum score 80%)", or the advisory statement.</summary>
    public string Description => ReleaseSignificant
        ? $"Release gate per project policy {Source ?? "(unnamed)"}{(MinimumReleaseScore is { } min ? $" — minimum documentation readiness score {min:0.#}%" : "")}."
        : DocumentationGate.AdvisoryNote;
}

/// <summary>Delivery Readiness wording under a <see cref="DocumentationGatePolicy"/>: blocking terms only when the policy makes it a release gate.</summary>
public static class DocumentationGate
{
    public const string AdvisoryNote =
        "Advisory: no project policy assigns release significance to Document Quality Review, so this is documentation readiness — not a product release decision.";

    /// <summary>The decision summary as shown: the gate's own summary under a release-gate policy, else an advisory documentation statement.</summary>
    public static string? Summary(ReadinessDecision decision, bool releaseSignificant) => releaseSignificant
        ? decision.Summary
        : $"{StateLabel(decision.State, false)} — documentation readiness {decision.Score:0.#}/100 (advisory).";

    /// <summary>The label of the overall heading: "Release" under a release-gate policy, else "Documentation gate".</summary>
    public static string Heading(bool releaseSignificant) => releaseSignificant ? "Release" : "Documentation gate";

    /// <summary>
    /// The readiness state as shown. Under a release-gate policy the gate states are kept ("Blocked"); without one they read as documentation
    /// observations ("Documentation gate not met") — never "Release blocked".
    /// </summary>
    public static string StateLabel(ReadinessState state, bool releaseSignificant) => releaseSignificant
        ? state switch
        {
            ReadinessState.Ready => "Ready",
            ReadinessState.MostlyReady => "Mostly ready",
            ReadinessState.NotReady => "Not ready",
            ReadinessState.Blocked => "Blocked",
            _ => state.ToString(),
        }
        : state switch
        {
            ReadinessState.Ready => "Documentation gate met",
            ReadinessState.MostlyReady => "Documentation gaps",
            _ => "Documentation gate not met",
        };
}
