using System.Text.Json.Serialization;

namespace BirkNext.ManualReview;

// ── Manual review decisions: a person's recorded judgement on one obligation or control, append-only ────────────────────────
// A decision is evidence of a human assessment, never an automated result: it never turns a check, control or review "Passed",
// and it never replaces runtime verification. Every save is a new row; the latest row per subject is current, earlier rows are its
// audit history. No secret, token or personal test identity belongs here (validated).

public static class ManualReviewModules
{
    public const string ApiQuality = "api-quality-review";
    public const string SecurityClassification = "security-classification";
    /// <summary>Implementation-gap findings (subject = stable finding id; scope = the evaluation scope, e.g. "import:{id}").</summary>
    public const string ImplementationGaps = "implementation-gaps";
    /// <summary>Integration suite review items (subject = review item key; scope = the suite key).</summary>
    public const string IntegrationSuites = "integration-suites";
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { ApiQuality, SecurityClassification, ImplementationGaps, IntegrationSuites };
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ManualReviewDecisionValue
{
    /// <summary>Reviewed; the obligation is met for the stated scope.</summary>
    Acceptable,
    /// <summary>Reviewed; a problem was found (record it as a defect elsewhere).</summary>
    IssueFound,
    /// <summary>Not decidable yet: more evidence is needed.</summary>
    NeedsEvidence,
    /// <summary>The obligation does not apply to the stated scope.</summary>
    NotApplicable,
    /// <summary>A known gap accepted by its owner for the stated scope.</summary>
    RiskAccepted,
}

public sealed record ManualReviewDecisionRequest
{
    public string Module { get; init; } = "";
    /// <summary>What the decision is about at the top level: an environment id, a source snapshot scope ("source:{id}") or an API target set.</summary>
    public string ScopeKey { get; init; } = "";
    /// <summary>The obligation or control (e.g. "SEC-03", "Authorization between roles and tenants").</summary>
    public string SubjectKey { get; init; } = "";
    public string Reviewer { get; init; } = "";
    public ManualReviewDecisionValue Decision { get; init; }
    public string? Note { get; init; }
    /// <summary>References to evidence (test case ids, document links, run ids) — references only, never the evidence content.</summary>
    public List<string> EvidenceReferences { get; init; } = [];
    /// <summary>The narrower thing the decision applies to (an API target, a journey), when not the whole scope.</summary>
    public string? AppliesTo { get; init; }
}

public sealed record ManualReviewDecisionView
{
    public Guid Id { get; init; }
    public string Module { get; init; } = "";
    public string ScopeKey { get; init; } = "";
    public string SubjectKey { get; init; } = "";
    public string Reviewer { get; init; } = "";
    public ManualReviewDecisionValue Decision { get; init; }
    public string? Note { get; init; }
    public List<string> EvidenceReferences { get; init; } = [];
    public string? AppliesTo { get; init; }
    public DateTimeOffset DecidedAt { get; init; }
    /// <summary>True for the latest decision of its subject; false for an earlier (superseded) one kept as audit history.</summary>
    public bool Current { get; init; }
}

public static class ManualReviewRules
{
    public const int MaxScope = 300, MaxSubject = 300, MaxReviewer = 120, MaxNote = 2000, MaxEvidence = 20, MaxEvidenceLength = 500;

    private static readonly string[] SecretMarkers = ["bearer ", "access_token", "id_token", "authorization:", "password=", "client_secret", "eyj"];

    /// <summary>Null when valid; otherwise the reason. Secrets and tokens are refused (a reference is a pointer, never a credential).</summary>
    public static string? Validate(ManualReviewDecisionRequest r)
    {
        if (!ManualReviewModules.All.Contains(r.Module)) return "Unknown review module.";
        if (string.IsNullOrWhiteSpace(r.ScopeKey) || r.ScopeKey.Length > MaxScope) return "A scope is required (at most 300 characters).";
        if (string.IsNullOrWhiteSpace(r.SubjectKey) || r.SubjectKey.Length > MaxSubject) return "A subject (obligation or control) is required (at most 300 characters).";
        if (string.IsNullOrWhiteSpace(r.Reviewer) || r.Reviewer.Length > MaxReviewer || r.Reviewer.Contains('\n')) return "A reviewer name is required (one line, at most 120 characters).";
        if (!Enum.IsDefined(r.Decision)) return "Unknown decision.";
        if (r.Note is { Length: > MaxNote }) return "The note is too long (at most 2000 characters).";
        if (r.AppliesTo is { Length: > MaxScope }) return "'Applies to' is too long (at most 300 characters).";
        if (r.EvidenceReferences.Count > MaxEvidence || r.EvidenceReferences.Any(e => string.IsNullOrWhiteSpace(e) || e.Length > MaxEvidenceLength))
            return "Evidence references: at most 20, each 1–500 characters.";
        var text = string.Join("\n", new[] { r.Note ?? "" }.Concat(r.EvidenceReferences)).ToLowerInvariant();
        if (SecretMarkers.Any(text.Contains)) return "Remove credentials or tokens: record a reference to the evidence, never the secret itself.";
        return r.Decision is ManualReviewDecisionValue.RiskAccepted or ManualReviewDecisionValue.IssueFound && string.IsNullOrWhiteSpace(r.Note)
            ? "Explain the decision in the note (required for 'issue found' and 'risk accepted')." : null;
    }

    public static string Label(ManualReviewDecisionValue d) => d switch
    {
        ManualReviewDecisionValue.Acceptable => "Reviewed — acceptable",
        ManualReviewDecisionValue.IssueFound => "Reviewed — issue found",
        ManualReviewDecisionValue.NeedsEvidence => "Needs more evidence",
        ManualReviewDecisionValue.NotApplicable => "Not applicable",
        ManualReviewDecisionValue.RiskAccepted => "Risk accepted",
        _ => d.ToString(),
    };
}
