# Relationship Review (formerly Correlation Review): report (2026-11-05)

Branch `008-traceability-first`, not pushed. NEXT39.

## A. Architecture (audit)

The flow is unchanged and still has one engine and one store:

1. **Sources:** Project Evidence associations select stored Source Analysis snapshots.
2. **Analysis:** the canonical interpretation (`POST api/canonical/{scope}/interpret`) turns them into entities, relations and candidates.
3. **Review:** the shared review queue and decisions (`POST api/canonical/{scope}/decisions`).
4. **Governance:** the governance gate.
5. **Consumers:** Release Readiness, Delivery, Traceability, Topology, IQR, AQR and Project Evidence.

No engine, store, registry or vocabulary was added. The canonical scope stays the Target Environment id.

**Rename:**

- Every user-visible "Correlation Review" now reads "Relationship Review": page title and heading, navigation, documentation catalog, Release Readiness, Delivery Overview, Functional Quality scope, the canonical correlation panel, and the backend/shared guidance texts (governance resolutions, Release Readiness actions, interpretation limitations).
- Unchanged:
  - the route `/correlation-review` (old links work);
  - module ids, test ids and API contracts;
  - persisted data.
- Navigation search keeps "Correlation Review" as an alias.

## B. Project identity: "Project: Development" vs M2LB

**Root cause:** the page showed `profile.Name`, which is the Target Environment's name, as "Project".

A second defect sat behind the first fix. The analysis context's profile is a data-minimized copy (`CreateSafeProfileSnapshot`) that leaves out the Target Application. The page now reads the Target Application from the saved profile by id, as `ProjectContextStrip` does.

It shows: **"Target Application: M2LB · Environment: Development"**.

## C. Source status: "analyzed" vs Partial/Failed

**Root cause:** the list printed "analyzed <time>" for every stored snapshot, whatever its outcome.

`SourceSetupAreas.Overall`, the rule the Snapshot Registry uses, moved to shared code as `SourceSetupOutcome.Overall`. The backend now delegates to it.

The frontend's `RelationshipReviewSources` applies the same rule to the snapshot's per-area results (`SourceSetupAsync`). Older backends without per-area results fall back to the code analyzer's status.

Each row shows:

- **Status:** Analyzed, Partially analyzed or Analysis failed.
- **Areas not complete:** e.g. "Architecture: Unsupported · CI/CD: Partial".
- **Association:** whether Project Evidence associates the source.

On the M2LB copy, all 10 snapshots are Partially analyzed. The Snapshot Registry gives the same outcome for each one (10/10 compared).

## D. UI

- **Header:** the intro text from the brief (verbatim) and the context line.
- **Tabs:** Overview | Needs Review (N) | Relationships | History & Governance. `?tab=review|relationships|history` opens a tab directly.
- **Overview:**
  - **Before any analysis:** "Relationships — Not analyzed" cards, never 0.
  - **After an analysis:** the counts, with a "Review N decisions" action.
  - **Source evidence:** a one-line summary ("7 of 10 sources selected · default: the 7 sources associated with M2LB in Project Evidence") and the outcome counts.
  - **Actions:** **Analyze relationships** (was "Interpret selected") and **Customize sources** (the list with status; "Reset to the default selection").
- **Default selection:** sources associated with the Target Application in Project Evidence. Needs-review and unrelated packages are excluded, and a failed analysis is never selected. Without any associations, every usable source is selected.
- **Needs Review:** the shared queue (Confirm / Reject / **Defer**, previously "Keep open"; alternatives; affected modules). Before an analysis: "Not analyzed — …".
- **Relationships:** the relationship explorer.
  - Quick filters: Requirements, User stories, Source code, APIs, Contracts, Tests.
  - Plus an entity-type filter and search by name or identity (Enter submits).
  - Before an analysis: "Not analyzed".
- **History & Governance:**
  - **Without an analysis:** "Governance not assessed — Analyze project relationships before evaluating governance rules." The gate is not called.
  - **With an analysis:** the existing Governance Gate, then the analysis and decision history.
- **Trigger name:** new analyses record the trigger "Relationship Review".

## E. Analysis

- **Unchanged backend:** frozen, fingerprinted interpretations. Identical inputs are reused ("Nothing changed: the existing analysis was reused.").
- **Applied decisions count:** a changed decision sequence (for example after a Defer) correctly produces a new version.
- **Progress:** uses the shared `OperationProgress` with the real stages only (analyze, refresh queue).

## F. Decisions

Unchanged shared registry: auditable, never auto-confirmed. On the copy, a Defer increased the decision count by exactly one.

## G. Tests

| Suite | Test | What it covers |
|---|---|---|
| Frontend `RelationshipReviewTests` | Outcome from areas | Terraform without code = Completed; architecture partial = Partially analyzed (with detail); failed = Analysis failed; fallback without areas; failed never selected by default |
| Frontend `RelationshipReviewTests` | Default selection | Only Associated packages (not NeedsReview / NotAssociated) |
| Frontend `RelationshipReviewTests` | Before analysis | Not analyzed (no zeros); Target Application shown; per-source status; queue/explorer Not analyzed; governance not assessed and never called |
| Frontend `RelationshipReviewTests` | Analyze | Sends the selected ids with trigger "Relationship Review" |
| Frontend `RelationshipReviewTests` | `?tab=history` | Opens that tab |
| Frontend `CanonicalCorrelationUiTests` | Page tests | Updated for the tabs and the customize step |
| Frontend nav, feature visibility, Release Readiness, Functional Quality scope, canonical summary | Assertions | Updated to the new label |

**Flaky test hardened:** `ActivityCenterTests.Cancel_…` read the live region once; under a full parallel run the earlier "Cancelled" announcement could still be there. It now waits, like the surrounding assertions (3/3 green alone).

| Run | Result |
|---|---|
| Full frontend suite | 8159 passed, 1 failed (the flaky ActivityCenter test, fixed afterwards; 3/3 alone) |
| Full backend suite | **5906 passed, 13 skipped, 0 failed** |

**Real browser** (`scratchpad/rr-e2e.cjs`):

- **Setup:** Edge on the M2LB copy (DB `birknext_gaps`, environment `ci-live` "Development", Target Application M2LB). Localhost only.
- **Writes:** only to the throwaway DB — interpret, plus one Defer per run.

| # | Scenario | Result |
|---|---|---|
| A1–A4 | Initial state | Title and navigation say Relationship Review; intro; "Target Application: M2LB · Environment: Development"; "7 of 10 sources selected · default: the 7 sources associated with M2LB in Project Evidence" |
| A5 | Per-source status | Equal to the Snapshot Registry for 10/10. The first run's check also required the statuses to differ; the data is legitimately all Partial, so that criterion was wrong. It was corrected and the rows re-read with a read-only script (each names its incomplete areas) |
| A6–A7 | Selection and existing analysis | Failed sources never selected by default; the existing analysis is summarised |
| B1–B3 | Analyze | "New analysis recorded. 2735 entities · 3802 relationships · rules correlation/4"; counts shown (2439 established, 1362 candidates, 1 decision); the second identical run reuses the analysis |
| C1–C3 | Review | Needs Review (1); Defer records exactly one decision (3 → 4); the Contracts filter lists contract entities with their relations |
| D1–D2 | Governance | Governance is evaluated with an analysis (Blocked: 1 blocking finding); Release Readiness shows no "Correlation Review" |
| E1–E2 | History | `?tab=history` lists the analysis with trigger "Relationship Review"; reload restores the identical frozen history |
| F1 | Phone width | 390 px: no horizontal overflow |
| Z | Safety | No external host; no page errors |

Runs: first 19/20 (A5 not compared because of a script parsing error), second 19/20 (A5 with the wrong criterion), then the read-only re-check of A5.

## H. Gaps

| Item | Status |
|---|---|
| Rename, project context, source status, default selection, tabs, Not analyzed, governance not assessed | Implemented · Unit · Browser |
| Explorer master-detail with filters by relationship type, source module, confidence, review status, evidence status | **Not implemented**: the explorer reuses `CanonicalCorrelationPanel` (search plus entity type); the extra filters need backend query support |
| "How Relationship Review works" panel, project selector for several applications | Not implemented (one Target Application per environment today) |
| Fine-grained progress stages (resolving snapshots, extracting entities …) | Not implemented: the backend interpret call is one request; only real stages are shown |
| Interpretation history labels (Latest created / Current selected / Approved) | Not implemented (the existing history table and compare are kept) |
| Concurrent reviewers / stale state | Existing registry controls only; not browser-tested |
| Downstream consumer reflecting a decision (C) | Partially verified: the decision count and governance; no Topology/IQR browser check |
