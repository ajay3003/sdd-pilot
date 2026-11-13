# Code Risk Review — baseline import, part 1 (2026-11-13)

## Verified implementation

- **Page:** `Pages/AiGeneratedCodeReview.razor` (route `code-risk-review`). It has current and baseline selectors.
- **Baseline rules:** `BaselineEligibility` (shared) decides eligibility. `BaselineEligibilityNote` asks for confirmation when the
  repository identity differs. The repository association can be remembered or revoked through the shared association service.
- **Sources:** `IAiCodeReviewApiService.SourcesAsync` returns the shared Source Analysis snapshot list. Snapshots are immutable and owned
  by Source Analysis.
- **Import:** the only source import path is the shared Source Analysis upload,
  `IIntegrationCatalogApiService.AnalyzeSourceSnapshotDetailedAsync`. A ZIP is uploaded, analyzed and registered as one immutable
  snapshot.

## Gap closed: direct baseline import

The page now has **Import baseline ZIP**, which opens a compact panel next to the baseline selector:

- It calls the **same** shared Source Analysis upload. There is no second import path, registry or snapshot store.
- It then reloads the shared snapshot list.
- If the new snapshot is a baseline candidate for the current repository, it is selected as baseline, and the confirmation step is reset.
  A different repository identity goes through the existing confirmation note. The current snapshot itself, or a non-comparable source, is
  stored but not selected, and the page says why.
- A failure (validation, analysis, persistence, network), an unreadable file or a cancellation leaves the current and baseline selections
  unchanged and states why. A cancellation says the server may still have finished, in which case a reload lists the snapshot.
- Other modules see the snapshot through the shared registry. Their own selections and histories are not changed.

The User Guide was updated.

**Tests** (`AiGeneratedCodeReviewPageTests`):

- A compatible import is uploaded once through the shared service and selected as baseline.
- A failed import shows the reason, and the earlier baseline and current selections are unchanged.

The focused Code Risk Review suite passed 22/22.

## Not done (rest of the brief)

- Entering an effective source date or commit at import. The shared upload API has no such field; the page states that the import time
  proves nothing.
- The five-view redesign: Overview, Findings explorer, Change Impact, Coverage & Evidence, History.
- Human finding decisions.
- Test Plan Suggestions drafts from findings.
- Browser and real-M2LB acceptance.
