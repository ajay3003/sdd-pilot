# Data Model: Traceability-First Workflow

## CandidateReviewStatus (enum — backend + frontend mirror)

Extended with `AutoAccepted`. All existing values preserved.

```csharp
public enum CandidateReviewStatus
{
    New,            // Legacy: Unreviewed. Included in Traceability. No semantic change.
    AutoAccepted,   // NEW: Auto-persisted after analysis. Included in Traceability by default.
    Accepted,       // ManuallyAccepted. Included in Traceability.
    Rejected,       // Excluded from Traceability. Visible in Extraction Review diagnostics.
    NeedsReview,    // Included in Traceability with warning badge. Extraction quality uncertain.
}
```

**Backward compatibility**: `New` is retained unchanged. Existing persisted sessions, browser storage snapshots, and `reviewed_candidates` database rows using `New` continue to work as "included in Traceability" — identical behavior to before.

**Auto-persist assignment**: When analysis completes and candidates are auto-persisted, each new candidate receives `AutoAccepted`. Candidates restored from a prior session with `ManuallyAccepted`/`Rejected`/`NeedsReview` statuses are not overwritten.

---

## TracedRequirement (frontend model — additive change)

```csharp
public sealed class TracedRequirement
{
    // existing fields unchanged ...
    public Guid CandidateId { get; init; }
    public string Title { get; init; }
    public string? FrId { get; init; }
    public ScenarioKind Classification { get; init; }
    public bool IsEligible { get; init; }
    public TraceCoverageStatus Status { get; init; }
    public IReadOnlyList<TracedTest> LinkedTests { get; init; }
    public IReadOnlyList<string> LinkedScIds { get; init; }
    public string? UserStoryId { get; init; }
    public string? SourceDocument { get; init; }
    public string? CoverageReason { get; init; }

    // NEW field:
    public bool NeedsReviewWarning { get; init; } // true when source candidate has NeedsReview status
}
```

---

## ExtractionCandidate (frontend model — default value change)

```csharp
// Before:
public CandidateReviewStatus ReviewStatus { get; set; } = CandidateReviewStatus.New;

// After:
public CandidateReviewStatus ReviewStatus { get; set; } = CandidateReviewStatus.AutoAccepted;
```

All other fields unchanged.

---

## TraceabilityModelBuilder filter rule

Applied at the start of `Build()`, before any partitioning:

```csharp
// Exclude rejected artifacts from all Traceability calculations
var activeCandiates = candidates
    .Where(c => c.ReviewStatus != CandidateReviewStatus.Rejected)
    .ToList();
```

Then replace `candidates` with `activeCandidates` throughout the method.

When building `TracedRequirement` instances, set `NeedsReviewWarning`:

```csharp
NeedsReviewWarning = sourceCandidate.ReviewStatus == CandidateReviewStatus.NeedsReview
```

---

## Database (no migration required)

The `reviewed_candidates` table stores `review_status` as a string. Adding `AutoAccepted` is additive. Existing rows are unaffected. New auto-persist inserts use `AutoAccepted`.

| Existing value | Continues to work | Notes |
|----------------|-------------------|-------|
| `New`          | ✓                 | Unreviewed; included in Traceability |
| `Accepted`     | ✓                 | ManuallyAccepted; included |
| `Rejected`     | ✓                 | Excluded |
| `NeedsReview`  | ✓                 | Flagged; included with badge |
| `AutoAccepted` | ✓ (new)           | Auto-persisted; included |
