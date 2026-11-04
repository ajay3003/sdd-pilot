namespace BirkNext.Web.Services;

/// <summary>
/// The status families of BirkNext. A status belongs to exactly one family; families are not interchangeable (a lifecycle state is never a readiness
/// state, a freshness state never a quality verdict).
/// </summary>
public enum StatusFamily { Evidence, Assessment, Configuration, Verification, Lifecycle, Coverage, Discovery, Freshness, Outcome }

/// <summary>Visual intent; mapped once to design tokens (see SemanticStatusBadge.razor.css). Strong colour only for decision-relevant states.</summary>
public enum StatusIntent { Positive, Info, Neutral, Warning, Danger, Active }

/// <summary>
/// The canonical semantic statuses. Machine keys are language-neutral; the visible label, meaning, usage rules, intent, icon and tooltip come from
/// <see cref="StatusVocabulary"/>. Same key → same fundamental meaning in every feature; features differ only in their prerequisites.
/// </summary>
public enum SemanticStatus
{
    // Evidence
    Detected, Confirmed, Inferred, Unknown, Unresolved,
    // Assessment / readiness
    NotAssessed, Assessing, Ready, ReadyWithLimitations, NeedsReview, Blocked, FailedToAssess, NotRun, Warning,
    // Configuration
    Missing, Configured, Invalid, Disabled, Available,
    // Runtime verification
    NotVerified, Verifying, Verified, VerificationFailed, Unavailable,
    // Lifecycle
    Draft, Validated, Approved, Archived,
    // Coverage / completeness
    InventoryComplete, SourceModelAvailable, ClassificationIncomplete, Partial, LimitedBySource, Heuristic, NotApplicable,
    // Discovery
    NotDetected,
    // Freshness / change
    Current, Stale, Historical, Live, Changed, New, Removed,
    // Outcome
    Completed, Failed, Conflict, Imported, Cancelled,
}

/// <summary>One canonical status definition: the single source the badge, tooltips, documentation and tests read.</summary>
public sealed record StatusDefinition(
    SemanticStatus Key, string Label, StatusFamily Family, StatusIntent Intent, string Icon, string Meaning, string UseWhen, string DoNotUseWhen);

/// <summary>What a pill is. Only <see cref="Status"/> carries a semantic status; the others are metadata and never styled as warnings.</summary>
public enum BadgeKind { Status, Type, Provenance, Scope, Environment, Confidence, Change, Count }

/// <summary>
/// The BirkNext semantic status dictionary. One definition per status; consumers pass a <see cref="SemanticStatus"/>, never a colour or a free label.
/// Unknown legacy labels map to <see cref="SemanticStatus.Unknown"/> (neutral) — never silently to Ready or a warning.
/// </summary>
public static class StatusVocabulary
{
    private static readonly StatusDefinition[] Definitions =
    [
        // ── Evidence ──
        new(SemanticStatus.Detected, "Detected", StatusFamily.Evidence, StatusIntent.Info, "◉",
            "Evidence was found.", "Something exists in the analysed source or capture.", "Implying correct, approved, complete or runtime verified."),
        new(SemanticStatus.Confirmed, "Confirmed", StatusFamily.Evidence, StatusIntent.Positive, "✓",
            "Explicitly established by authoritative evidence or an explicit reviewed association.", "An explicit declaration or a person's confirmation exists.",
            "Runtime health or verification (unless the evidence itself is runtime evidence)."),
        new(SemanticStatus.Inferred, "Inferred", StatusFamily.Evidence, StatusIntent.Info, "⇢",
            "Derived defensibly from available evidence, not declared.", "A deterministic but indirect relation.", "Ambiguity (use Needs review) or manual approval."),
        new(SemanticStatus.Unknown, "Unknown", StatusFamily.Evidence, StatusIntent.Neutral, "?",
            "No defensible state can be established.", "Nothing more precise applies (also the safe fallback for unrecognised values).", "A more precise state exists."),

        // ── Assessment / readiness ──
        new(SemanticStatus.NotAssessed, "Not assessed", StatusFamily.Assessment, StatusIntent.Neutral, "○",
            "The assessment has not been performed.", "Nothing has evaluated this yet.", "While loading (use Assessing) or after a failure (use Failed to assess)."),
        new(SemanticStatus.NotRun, "Not run", StatusFamily.Outcome, StatusIntent.Neutral, "○",
            "The operation has not been executed.", "A run, test or check that could be started has not been.", "Failed (nothing failed) or Not assessed (an assessment, not a run)."),
        new(SemanticStatus.Warning, "Warning", StatusFamily.Assessment, StatusIntent.Warning, "⚠",
            "A non-blocking concern was found.", "The result is usable but something deserves attention.", "Blocked (it stops work) or Needs review (a decision is required)."),
        new(SemanticStatus.Assessing, "Assessing", StatusFamily.Assessment, StatusIntent.Active, "⟳",
            "The assessment is loading or computing.", "Work is in progress.", "Showing a previous result that is being refreshed (keep it, add a refreshing hint)."),
        new(SemanticStatus.Ready, "Ready", StatusFamily.Assessment, StatusIntent.Positive, "✓",
            "All required prerequisites for this specific activity are satisfied.", "The activity can start; prerequisites differ per feature.",
            "Lifecycle, presence, configuration alone, or one sub-dimension while the whole is blocked (name the scope)."),
        new(SemanticStatus.ReadyWithLimitations, "Ready with limitations", StatusFamily.Assessment, StatusIntent.Positive, "✓",
            "The activity can proceed; known limitations remain that do not block its purpose.", "Non-blocking limitations are listed.", "A required prerequisite is missing."),
        new(SemanticStatus.NeedsReview, "Needs review", StatusFamily.Assessment, StatusIntent.Warning, "!",
            "A specific unresolved decision requires human assessment.", "A conflict, ambiguity or explicit confirmation exists.",
            "Raw analyzer limitations, unresolved-by-source counts or heuristic parsing that need no decision."),
        new(SemanticStatus.Blocked, "Blocked", StatusFamily.Assessment, StatusIntent.Danger, "⛔",
            "A required prerequisite prevents the activity from proceeding.", "Something required is missing or refused.", "Optional data is missing."),
        new(SemanticStatus.FailedToAssess, "Failed to assess", StatusFamily.Assessment, StatusIntent.Danger, "✕",
            "The assessment itself failed technically or could not complete.", "An analyzer or request failed.", "The assessed thing is merely blocked or needs review."),

        // ── Configuration ──
        new(SemanticStatus.Available, "Available", StatusFamily.Configuration, StatusIntent.Info, "◇",
            "It exists and can be used.", "A source, asset or capability is present and usable.", "Ready (all prerequisites met) or verified."),
        new(SemanticStatus.Missing, "Missing", StatusFamily.Configuration, StatusIntent.Neutral, "○",
            "No configuration exists at the owning scope.", "The value was never configured.", "It is required right now (the consumer says Blocked)."),
        new(SemanticStatus.Configured, "Configured", StatusFamily.Configuration, StatusIntent.Info, "⚙",
            "A usable configuration value exists at its owning scope.", "The setting is present.", "Implying approved, runtime verified, execution-compatible or healthy."),
        new(SemanticStatus.Invalid, "Invalid", StatusFamily.Configuration, StatusIntent.Danger, "✕",
            "Configuration or content exists but is invalid for its declared purpose.", "Validation found it unusable.", "It is merely unverified."),
        new(SemanticStatus.Disabled, "Disabled", StatusFamily.Configuration, StatusIntent.Neutral, "⊘",
            "Explicitly disabled by configuration or a person's choice.", "Turned off on purpose.", "It is unavailable for a technical reason."),

        // ── Runtime verification ──
        new(SemanticStatus.NotVerified, "Not verified", StatusFamily.Verification, StatusIntent.Neutral, "○",
            "No runtime or explicit verification has been performed.", "Only source or configuration evidence exists.", "Verification failed."),
        new(SemanticStatus.Verifying, "Verifying", StatusFamily.Verification, StatusIntent.Active, "⟳",
            "Verification is running.", "A verification is in progress.", "Analysis that is not verification."),
        new(SemanticStatus.Verified, "Verified", StatusFamily.Verification, StatusIntent.Positive, "✓",
            "Runtime or explicit verification succeeded.", "A verification was actually performed.", "Source declaration, inference, configuration presence or static parsing."),
        new(SemanticStatus.VerificationFailed, "Verification failed", StatusFamily.Verification, StatusIntent.Danger, "✕",
            "Verification ran and did not succeed.", "A verification result was negative.", "Verification could not run (use Unavailable)."),
        new(SemanticStatus.Unavailable, "Unavailable", StatusFamily.Verification, StatusIntent.Neutral, "⊘",
            "The capability or source needed is not available right now.", "A tool, backend or runtime is unavailable.", "A negative result."),

        // ── Lifecycle ──
        new(SemanticStatus.Draft, "Draft", StatusFamily.Lifecycle, StatusIntent.Neutral, "✎",
            "Editable, not yet validated or approved.", "Work in progress.", "Readiness."),
        new(SemanticStatus.Validated, "Validated", StatusFamily.Lifecycle, StatusIntent.Positive, "✓",
            "Passed its type-specific validation.", "A real validation ran.", "Mere existence."),
        new(SemanticStatus.Approved, "Approved", StatusFamily.Lifecycle, StatusIntent.Positive, "✓",
            "Explicit approval has been recorded.", "A person or policy approved it.", "Configuration presence or verification."),
        new(SemanticStatus.Archived, "Archived", StatusFamily.Lifecycle, StatusIntent.Neutral, "▤",
            "Kept for history, no longer active.", "Retired but retained.", "Deleted or failed."),

        // ── Coverage / completeness ──
        new(SemanticStatus.InventoryComplete, "Inventory complete", StatusFamily.Coverage, StatusIntent.Info, "✓",
            "All supported inventory items were discovered; this does not confirm runtime quality or successful execution.", "Discovery is complete (tests, dependencies…).", "Quality, execution or pass/fail completeness."),
        new(SemanticStatus.SourceModelAvailable, "Source model available", StatusFamily.Coverage, StatusIntent.Positive, "✓",
            "A model was built from source.", "Architecture/data model extracted.", "Runtime verification."),
        new(SemanticStatus.ClassificationIncomplete, "Classification incomplete", StatusFamily.Coverage, StatusIntent.Warning, "◐",
            "Everything was read; some items could not be categorised.", "A classification gap that may need action.", "Defects or human decisions."),
        new(SemanticStatus.Partial, "Partial", StatusFamily.Coverage, StatusIntent.Warning, "◑",
            "A defined coverage dimension is only partly satisfied.", "A genuine whole/part relationship.",
            "Source limitations, heuristic parsing, unknown runtime state, review needs or not-applicable evidence."),
        new(SemanticStatus.LimitedBySource, "Limited by source", StatusFamily.Coverage, StatusIntent.Neutral, "◌",
            "Constrained by what the source, tool or scope can establish.", "An analyzer limitation.", "A human decision is required."),
        new(SemanticStatus.Heuristic, "Heuristic", StatusFamily.Coverage, StatusIntent.Info, "≈",
            "Read with pattern-based parsing, not a structured parser.", "Describing the parse approach.", "Indicating a defect or a review need."),
        new(SemanticStatus.NotApplicable, "Not applicable", StatusFamily.Coverage, StatusIntent.Neutral, "–",
            "This dimension is not relevant to the selected source or operation.", "Out of scope by design.", "Not yet assessed, or missing (use Missing)."),

        // ── Discovery ──
        new(SemanticStatus.NotDetected, "Not detected", StatusFamily.Discovery, StatusIntent.Neutral, "○",
            "No evidence was detected within the stated scope or snapshot.", "Absence in a named scope (say which).", "It does not exist globally, is missing, or needs review."),

        // ── Freshness / change ──
        new(SemanticStatus.Current, "Current", StatusFamily.Freshness, StatusIntent.Info, "●",
            "The latest version.", "Marking the current snapshot/version.", "Quality."),
        new(SemanticStatus.Stale, "Stale", StatusFamily.Freshness, StatusIntent.Warning, "↻",
            "Previously valid evidence is outdated.", "Inputs changed since it was produced.", "Failure."),
        new(SemanticStatus.Unresolved, "Unresolved", StatusFamily.Evidence, StatusIntent.Warning, "?",
            "A reference exists but its target could not be resolved.", "A stated link points at something not found or ambiguous.", "Missing (nothing is referenced) or Conflict."),
        new(SemanticStatus.Historical, "Historical", StatusFamily.Freshness, StatusIntent.Neutral, "⟲",
            "A past version, kept for history.", "Temporal state.", "Low confidence."),
        new(SemanticStatus.Live, "Live", StatusFamily.Freshness, StatusIntent.Active, "●",
            "Observed now.", "Live runtime observation.", "Quality pass."),
        new(SemanticStatus.Changed, "Changed", StatusFamily.Freshness, StatusIntent.Info, "Δ",
            "Different from the compared version.", "Comparisons.", "Quality."),
        new(SemanticStatus.New, "New", StatusFamily.Freshness, StatusIntent.Info, "+",
            "Not present in the compared version.", "Comparisons.", "Quality."),
        new(SemanticStatus.Removed, "Removed", StatusFamily.Freshness, StatusIntent.Neutral, "−",
            "No longer present.", "Comparisons and removed references.", "Failure."),

        // ── Outcome ──
        new(SemanticStatus.Completed, "Completed", StatusFamily.Outcome, StatusIntent.Positive, "✓",
            "The operation ran to completion.", "An operation finished (its own result is stated separately).", "Quality passed or tests passed."),
        new(SemanticStatus.Failed, "Failed", StatusFamily.Outcome, StatusIntent.Danger, "✕",
            "The operation itself failed.", "Prefer a specific label (Import failed, Validation failed).", "A negative assessment result."),
        new(SemanticStatus.Conflict, "Conflict", StatusFamily.Outcome, StatusIntent.Danger, "⚡",
            "Two or more authoritative evidence sources disagree.", "Deterministic disagreement.", "Missing evidence, unknown or a parser limitation."),
        new(SemanticStatus.Imported, "Imported", StatusFamily.Outcome, StatusIntent.Info, "↓",
            "Imported into BirkNext.", "An import completed.", "Reviewed, approved or tested."),
        new(SemanticStatus.Cancelled, "Cancelled", StatusFamily.Outcome, StatusIntent.Neutral, "⊘",
            "The operation was stopped before it finished.", "A person or the system cancelled a run.", "Failed (nothing failed) or Blocked."),
    ];

    private static readonly Dictionary<SemanticStatus, StatusDefinition> ByKey = Definitions.ToDictionary(d => d.Key);

    public static IReadOnlyList<StatusDefinition> All => Definitions;

    public static StatusDefinition Of(SemanticStatus status) => ByKey.TryGetValue(status, out var d) ? d : ByKey[SemanticStatus.Unknown];

    public static string Label(SemanticStatus status) => Of(status).Label;
    public static StatusIntent Intent(SemanticStatus status) => Of(status).Intent;

    /// <summary>The CSS modifier for an intent (one mapping to design tokens).</summary>
    public static string IntentClass(StatusIntent intent) => intent switch
    {
        StatusIntent.Positive => "positive",
        StatusIntent.Info => "info",
        StatusIntent.Warning => "warning",
        StatusIntent.Danger => "danger",
        StatusIntent.Active => "active",
        _ => "neutral",
    };

    /// <summary>
    /// The human-readable dictionary (docs/status-vocabulary.md), generated from these definitions so the documentation cannot drift from the code
    /// (a test compares the saved file with this output).
    /// </summary>
    public static string ToMarkdown()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# BirkNext status vocabulary").AppendLine();
        sb.AppendLine("Generated from `frontend/BirkNext.Web/Services/StatusVocabulary.cs` — edit the code, then regenerate (a test fails when this file drifts).").AppendLine();
        sb.AppendLine("Same term → same fundamental meaning in every feature. Features differ only in their prerequisites (Ready for AQR and Ready for Performance");
        sb.AppendLine("need different inputs, but Ready always means *all required prerequisites for this activity are satisfied*).").AppendLine();
        sb.AppendLine("## Before adding a status").AppendLine();
        sb.AppendLine("1. Check this dictionary. 2. Reuse a term only if its meaning matches exactly. 3. Never reuse a term with a different meaning.");
        sb.AppendLine("4. Add a new term only for genuinely distinct semantics. 5. Keep status apart from type, provenance, environment, confidence and counts —");
        sb.AppendLine("those use `MetaBadge` (`BadgeKind`), never `SemanticStatusBadge`. Unknown or legacy values map to *Unknown* (neutral), never to Ready or a warning.").AppendLine();
        sb.AppendLine("Intents map once to design tokens: Positive → success, Info → info, Warning → warning, Danger → danger, Active → accent, Neutral → muted.");
        sb.AppendLine("Strong colour is reserved for decision-relevant states; red only for Blocked, Conflict, Failed, Invalid (and their failure variants).").AppendLine();
        foreach (var family in Enum.GetValues<StatusFamily>())
        {
            sb.AppendLine($"## {family}").AppendLine();
            sb.AppendLine("| Term | Meaning | Use when | Do not use when | Intent | Icon |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var d in Definitions.Where(d => d.Family == family))
                sb.AppendLine($"| {d.Label} | {d.Meaning} | {d.UseWhen} | {d.DoNotUseWhen} | {d.Intent} | {d.Icon} |");
            sb.AppendLine();
        }
        return sb.ToString().Replace("\r\n", "\n");
    }

    /// <summary>Tooltip text: the canonical meaning (contextual explanation is added by the consumer).</summary>
    public static string Tooltip(SemanticStatus status) => Of(status).Meaning;

    /// <summary>
    /// Legacy or stored labels mapped to the canonical status — for compatibility with persisted values and older APIs. Unrecognised values are
    /// <see cref="SemanticStatus.Unknown"/> (neutral), never Ready or a warning.
    /// </summary>
    public static SemanticStatus FromLegacy(string? label) => (label ?? "").Trim().ToLowerInvariant() switch
    {
        "detected" or "detected in source" or "found" => SemanticStatus.Detected,
        "confirmed" or "confirmed in source" => SemanticStatus.Confirmed,
        "inferred" or "strongly supported" or "suggested" => SemanticStatus.Inferred,
        "not assessed" or "not analyzed" or "not evaluated" or "not checked" => SemanticStatus.NotAssessed,
        "not run" or "not executed" or "notrun" => SemanticStatus.NotRun,
        "available" => SemanticStatus.Available,
        "unresolved" => SemanticStatus.Unresolved,
        "warning" => SemanticStatus.Warning,
        "cancelled" or "canceled" => SemanticStatus.Cancelled,
        "assessing" or "loading" or "running" => SemanticStatus.Assessing,
        "ready" or "ready to run" or "ready to review" => SemanticStatus.Ready,
        "ready with limitations" => SemanticStatus.ReadyWithLimitations,
        "needs review" or "review required" or "needs confirmation" => SemanticStatus.NeedsReview,
        "blocked" => SemanticStatus.Blocked,
        "failed to assess" or "could not be assessed" => SemanticStatus.FailedToAssess,
        "missing" or "not configured" => SemanticStatus.Missing,
        "configured" => SemanticStatus.Configured,
        "invalid" => SemanticStatus.Invalid,
        "disabled" => SemanticStatus.Disabled,
        "not verified" => SemanticStatus.NotVerified,
        "verified" => SemanticStatus.Verified,
        "unavailable" or "not available" => SemanticStatus.Unavailable,
        "draft" => SemanticStatus.Draft,
        "validated" or "valid" => SemanticStatus.Validated,
        "approved" => SemanticStatus.Approved,
        "archived" => SemanticStatus.Archived,
        "inventory complete" => SemanticStatus.InventoryComplete,
        "source model available" => SemanticStatus.SourceModelAvailable,
        "classification incomplete" => SemanticStatus.ClassificationIncomplete,
        "partial" or "partial evidence" => SemanticStatus.Partial,
        "limited by source" or "limited" => SemanticStatus.LimitedBySource,
        "heuristic" => SemanticStatus.Heuristic,
        "not applicable" => SemanticStatus.NotApplicable,
        "not detected" or "not detected in this snapshot" or "not found" or "not found in source" or "none found" => SemanticStatus.NotDetected,
        "current" => SemanticStatus.Current,
        "stale" or "outdated" => SemanticStatus.Stale,
        "historical" => SemanticStatus.Historical,
        "live" => SemanticStatus.Live,
        "changed" => SemanticStatus.Changed,
        "new" => SemanticStatus.New,
        "removed" => SemanticStatus.Removed,
        "completed" or "complete" or "done" => SemanticStatus.Completed,
        "failed" or "analysis failed" or "error" => SemanticStatus.Failed,
        "conflict" => SemanticStatus.Conflict,
        "imported" => SemanticStatus.Imported,
        _ => SemanticStatus.Unknown,
    };
}
