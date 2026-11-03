using BirkNext.Web.Models;

namespace BirkNext.Web.Services.ProjectEvidence;

/// <summary>A summary card at the top of Target Environment → Project.</summary>
public sealed record ProjectEvidenceCard(string Key, string Label, string Value, string? Detail, string Tone);

/// <summary>The overall decision line: one word-level decision and the sentence that explains it.</summary>
public sealed record ProjectEvidenceDecision(string Label, string Tone, string Explanation);

/// <summary>A source's provenance as an explicit badge (Confirmed / Auto-associated / Needs confirmation) with its full origin as the explanation.</summary>
public sealed record ProvenanceBadge(string Label, string Tone, string Origin);

/// <summary>
/// Presentation of Target Environment → Project, from the same <see cref="ProjectEvidenceView"/> counts as before (no new state): an explicit
/// overall decision, summary cards, short role reasons with the full diagnostic behind a disclosure, provenance badges and context-specific
/// actions. Bound ≠ ready is preserved: a Partial required role is "Ready with limitations", never "Ready".
/// </summary>
public static class ProjectEvidencePresentation
{
    private static string N(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    public static ProjectEvidenceDecision Decision(ProjectEvidenceView v)
    {
        var decisions = v.NeedsReviewCount + v.NeedsDecision + v.Conflicts;
        var noDecisions = decisions == 0 ? " No manual decisions are required." : "";
        if (v.TargetApplication is null)
            return new("Waiting for Target Application", "partial", "Choose the Target Application; its evidence roles are evaluated once it is known.");
        if (v.Conflicts > 0 || v.NeedsDecision > 0 || v.NeedsReviewCount > 0 && !v.RequiredReadyAll)
            return new("Needs review", "partial", $"{N(decisions, "item needs", "items need")} your decision before the evidence can be relied on.");
        if (!v.IsConfigured) return new("Not configured", "neutral", v.ReadinessExplanation);
        if (v.RequiredMissing > 0)
            return new("Incomplete", "rejected", $"{N(v.RequiredMissing, "required evidence role has", "required evidence roles have")} no source.{noDecisions}");
        if (v.RequiredReadyAll) return new("Ready", "available", $"All required evidence roles are bound and ready.{noDecisions}");
        return new("Ready with limitations", "partial",
            $"All required evidence roles are bound. {N(v.RequiredPartial, "required role has", "required roles have")} partial source coverage.{noDecisions}");
    }

    /// <summary>"Partial — all required roles are bound, but one required role has incomplete evidence coverage": why Partial can coexist with
    /// Needs review 0 (coverage is not a decision).</summary>
    public static string ProjectState(ProjectEvidenceView v) => v.EvidenceState switch
    {
        "Partial" when v.RequiredMissing == 0 && v.Conflicts == 0 =>
            $"Partial — all required roles are bound, but {N(v.RequiredPartial, "required role has", "required roles have")} incomplete evidence coverage",
        "Partial" => $"Partial — {N(v.RequiredMissing, "required role is", "required roles are")} not bound",
        "Ready" => "Ready — every required role is bound and ready",
        "Needs review" => $"{N(v.NeedsReviewCount + v.NeedsDecision + v.Conflicts, "decision is", "decisions are")} pending",
        var other => other,
    };

    public static IReadOnlyList<ProjectEvidenceCard> Cards(ProjectEvidenceView v, TargetApplicationIdentity? application, string? provenance)
    {
        var cards = new List<ProjectEvidenceCard>
        {
            new("application", "Target application", application?.Name ?? "Not chosen", provenance, application is null ? "partial" : "neutral"),
            new("project", "Project evidence", v.EvidenceState, v.IsConfigured ? ProjectState(v) : null,
                v.EvidenceState switch { "Ready" => "available", "Partial" or "Needs review" or "Waiting for Target Application" => "partial", _ => "neutral" }),
        };
        if (v.IsConfigured)
        {
            cards.Add(new("required", "Required evidence", $"{v.RequiredBound} / {v.RequiredTotal} bound",
                $"{v.RequiredReady} ready{(v.RequiredPartial > 0 ? $" · {v.RequiredPartial} partial" : "")}{(v.RequiredMissing > 0 ? $" · {v.RequiredMissing} missing" : "")}",
                v.RequiredReadyAll ? "available" : v.RequiredMissing > 0 ? "rejected" : "partial"));
            cards.Add(new("optional", "Optional evidence", $"{v.OptionalAvailable} available", $"of {v.OptionalTotal} optional roles · never blocks", "neutral"));
        }
        var review = v.NeedsReviewCount + v.NeedsDecision + v.Conflicts;
        cards.Add(new("review", "Needs review", review.ToString(System.Globalization.CultureInfo.InvariantCulture),
            review == 0 ? "No decisions pending" : "Decisions pending", review == 0 ? "neutral" : "partial"));
        return cards;
    }

    /// <summary>The role's reason in a few words; the full diagnostic stays behind "View reason".</summary>
    public static string ShortReason(EvidenceRoleRow row) => row.State switch
    {
        EvidenceRoleState.Ready => "Ready",
        EvidenceRoleState.Partial => $"Partial — {row.Role.Label.ToLowerInvariant()} coverage is incomplete",
        EvidenceRoleState.Conflict => $"Conflict — {row.Bound.Count} sources are associated where one is expected",
        EvidenceRoleState.Suggested => "Suggested — one compatible source, not yet confirmed",
        EvidenceRoleState.NeedsDecision => $"Needs decision — {row.Candidates.Count(c => !c.Bound)} compatible sources",
        EvidenceRoleState.Unavailable => "Unavailable — the bound source is not imported in this workspace",
        EvidenceRoleState.Missing => "Missing — no source selected",
        _ => row.IsRequired ? "Missing — no source selected" : "Optional · not provided",
    };

    /// <summary>The full reason of a role that is not simply ready (null when there is nothing more to say).</summary>
    public static string? FullReason(EvidenceRoleRow row) => row.State switch
    {
        EvidenceRoleState.Partial => row.PartialDetail,
        EvidenceRoleState.Conflict => $"This role uses one source, but {row.Bound.Count} are associated. Remove the one that is not authoritative.",
        _ => null,
    };

    /// <summary>Explicit provenance: Confirmed (you, Project setup, IQR) · Auto-associated (deterministic detection) · Needs confirmation (a
    /// suggestion). The full origin stays available as the badge's explanation.</summary>
    public static ProvenanceBadge Provenance(ProjectEvidenceSourceReference source) => source.BoundBy switch
    {
        "User" or "ProjectSelection" or "Iqr" => new("Confirmed", "available", EvidenceRoleRow.OriginLabel(source)),
        "Detected" => new("Auto-associated", "neutral", EvidenceRoleRow.OriginLabel(source)),
        "TestPack" => new("Needs confirmation", "partial", EvidenceRoleRow.OriginLabel(source)),
        _ => new("Detected", "neutral", EvidenceRoleRow.OriginLabel(source)),
    };

    /// <summary>A context-specific action instead of a generic "Change / add".</summary>
    public static string Action(EvidenceRoleRow row) => row.State switch
    {
        EvidenceRoleState.Partial => "Resolve partial coverage",
        EvidenceRoleState.Conflict => "Review association",
        EvidenceRoleState.Suggested => "Review suggestion",
        EvidenceRoleState.NeedsDecision => "Choose source",
        EvidenceRoleState.Ready when row.Role.Cardinality == ProjectEvidenceCardinality.Multiple => "Add supporting source",
        EvidenceRoleState.Ready => "Change source",
        _ => "Select source",
    };
}
