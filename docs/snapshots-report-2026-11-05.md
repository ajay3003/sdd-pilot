# Snapshots: registry status integrity, cross-module consistency and UI (2026-11-05)

Branch `008-traceability-first`, not pushed.

## A. Foundations: audit and implementation matrix

The repository code and the database were the truth, not prior reports.

| Capability | Existing | Verified gap | Action |
|---|---|---|---|
| Snapshot Registry (`snapshot_registry.snapshot_entry`, `SnapshotRegistryService`) | Metadata of every owner store; payloads stay with owners | Source Analysis status copied the application-code analyzer's outcome | Status rule fixed at the shared layer; one-time metadata re-read (§B) |
| References (`SnapshotReferenceIndex`, `/source-analysis/{id}/references`) | Checked stores, plus an explicit "not checked" list | The UI showed only a bare count ("Referenced by 0") | References tab lists referrers and the stores not checked; the list says "No saved references" |
| Lineage edges | `ImportedFrom`/`DerivedFrom`/…, recorded only from stored ids | — | Shown in the Lineage tab and as Origin in Overview |
| Pointers (Latest created / Latest completed / Current / Approved) | Failed or partial snapshots are never Latest completed or Approved | — | Kept; viewing never writes (verified) |
| Retention and immutability | Retention classes plus a `Frozen` flag | The UI mixed them ("Working · Frozen") | Labelled separately: "Immutable" / "Retention: …" |
| Listing | Type, status, project, date and sort on the server | No text search; no stable secondary order | Server-side `search` (label); secondary order by type and id |
| StatusVocabulary, SemanticStatusBadge, MetaBadge | Shared | Undimensioned "Failed" / "Partial" | Dimension-qualified labels ("Analysis: Partial", "Storage: available", …) |
| Shared loading (NEXT34) | `OperationProgress` | — | Reused for listing; `Finish()` after load |

No new registry, metadata store, status engine, badge set or loader was added.

## B. Root causes

**1. Terraform "Failed" in Snapshots vs "Infrastructure: Analyzed" in Project Evidence: one fact, two definitions.**

- The registry's Source Analysis adapter copied `IqrSourceSnapshot.Status`. `SourceSetupAreas` documents that status as the application-code analyzer's outcome only. An infrastructure-only package has no production project, so that analyzer reports Failed.
- Project Evidence (and, since NEXT35, Source Management) read the per-area outcomes.
- The M2LB copy shows the exact operations: in all four "Failed" Terraform snapshots, Application code is NotDetected, Architecture Unsupported, Infrastructure as Code Complete, Configuration Complete, CI/CD Partial, and Contracts Partial or NotDetected.

**2. Project "—" for every snapshot.** The registry records no project for Source Analysis snapshots, which are workspace-global. The UI showed "—" instead of resolving ownership from shared records.

**3. "Referenced by 0" read as "unreferenced".** The count covers checked stores only; references kept in payloads or the browser are listed by the backend as not checked, but the UI never showed that.

## C. Data corrections (metadata only)

- **One shared rule:** `SourceSetupAreas.Overall` gives a snapshot's analysis status over the areas it contains. Not present (NotDetected) or not run (NotAnalyzed) does not count:

  | Areas present | Status |
  |---|---|
  | All complete | Completed |
  | All failed | Failed |
  | Otherwise (an Unsupported area counts here) | Partial |
  | None present | Unknown |

  An Unsupported area is a BirkNext limitation and keeps the status Partial; it is never rounded up. The registry adapter uses this rule.
- **Migration:** `MetadataVersion` 1 → 2. Source Analysis entries below 2 are re-read once; only status, summary and metadata version are written. Identity, timestamps, retention, immutability and payloads never change. This is idempotent: the second sync updates 0.
- **Result on the M2LB copy:** sync `updated: 4`. The four Terraform snapshots changed Failed → **Partial** (not Completed: CI/CD and contracts are partial). The registry now reads 26 Completed, 10 Partial, 0 Failed. No genuine failure exists in this data; a genuine one (unreadable production projects) stays Failed (tested).
- **Pointers:** no snapshot became Completed through this change, so no Latest completed pointer moved.
- **Frozen reports** keep their own copies and are unchanged.

## D. UI (`/snapshots`)

- **Header** with the brief's lead and the preservation note.
- **KPIs** (registry counts, clickable as filters): snapshots, analysis completed, partial, failed, nothing analyzable. They are hidden when the registry does not answer.
- **Filters:** search (server-side, true counts), type, analysis status, project, created from/to, and sort (newest, oldest, effective, source name, status, project). Active filters show as chips with Clear all.
- **Compact list:** Snapshot/source (with pointers) · Type · Project · Created · Analysis · References · Details.
  - **Project** is the recorded project, else the applications whose Project Evidence references the snapshot or its import, else "Unassigned" or "Not recorded". Never guessed from names.
  - **References** reads "No saved references" or "N saved references".
- **Detail panel tabs:**
  - **Overview:** type, created, effective, revision, origin, pointer and retention actions, deletion check.
  - **Processing:** the actual area outcomes in words (e.g. "Application code: not present in this package", "Architecture: not supported by the analyzer (a BirkNext limitation)"), plus what completed, what did not, and whether the evidence is usable.
  - **Evidence:** "Open in Source Analysis" for the same snapshot id.
  - **References:** referrers, plus the stores not checked.
  - **Lineage.**
  - **Diagnostics:** id, module, fingerprint, storage, payload availability, metadata version, and the retry path (a new import; never re-analysis in place).
- **Dimension badges:** Analysis, Storage, Immutable/Mutable, Retention, Project.
- **URL state:** filters, the open snapshot and its tab live in the URL, so back from Source Analysis and reload restore them.
- **390 px fix (found in the browser):** visually-hidden cell text was absolutely positioned with the page as its containing block, so text in table cells widened the page. The table frame is now the containing block. The same latent defect was fixed in the Source Management and Project Evidence table frames.

## E. Cross-module

- Project Evidence, Source Management and Snapshots now share one definition of a Source Analysis outcome: per area, and only the areas present.
- **Remaining difference by design:** Snapshots shows one overall status, the others show per-area badges.
- Snapshots' Project column reads the same Project Evidence records as Source Management.

## F. Tests

**Backend** (`SnapshotRegistryTests`):

| Test | What it covers |
|---|---|
| Infrastructure-only → Partial | Areas Complete/Complete/Partial give Partial; all-complete infrastructure gives Completed; unreadable production projects stay Failed. |
| Outcome rule (5 cases) | The area combinations above. |
| One-time re-read | v1 Failed becomes v2 Partial, with identity, timestamps and Frozen unchanged; the second sync updates 0. |
| Fixture fix | Fixtures gained the production project every real analyzed package has. A bad enum value in my first fixture was caught by the test. |

**Frontend** (`SnapshotBrowserTests`, rewritten, 5 tests):

- dimension labels, Unassigned/M2LB project, reference wording, columns;
- KPIs as registry counts and filters, chips in the URL, search and status sent to the registry, clear;
- detail dimensions, Processing stages and summary, references and not-checked, lineage, evidence link, diagnostics, no pointer change on viewing, explicit Make current;
- URL restore of filters, open snapshot and tab;
- backend error with Retry, with no invented counts.

**Flaky test fixed:** `ActivityCenterTests.Cancel_…` asserted synchronously after Dismiss, which re-renders via `InvokeAsync`. It flaked twice under full parallel runs and now waits like the test's other assertions (3/3 re-runs green).

| Run | Result |
|---|---|
| Full frontend suite | **8139 / 8140** passed; the one failure was that flaky test, since fixed |
| Full backend suite | **5901 passed, 13 skipped, 0 failed** |

**Real browser** (`scratchpad/snap-e2e.cjs`):

- **Setup:** Edge on the M2LB copy (DB `birknext_gaps`, API rebuilt with the new rule).
- **Result: 18/18 checks passed.** An earlier run failed because it ran against a stale API build; that run is not counted.

| Area | Checks |
|---|---|
| List | KPIs (36 total, 26 completed, 10 partial, 0 failed); 10 M2LB Source Analysis snapshots with "Analysis: …"; all Terraform snapshots Partial, not Failed; Project from Project Evidence ("M2LB" / "Not recorded"), never "—"; reference wording. |
| Detail (Terraform-iac-meldingsutveksler) | Dimensions; Processing shows Infrastructure as Code completed and Application code not present; References. |
| Navigation | Open in Source Analysis shows the same snapshot id (`30a4f584-…`); Back restores filters, the open snapshot and the Evidence tab; lineage. |
| Pointers | No pointer or retention write request during viewing, tabs and navigation (counted in the route). |
| Search, filters, reload | Server-side search "terraform" (5, true count); the Partial KPI filters; reload restores filters. |
| General | No horizontal scroll at 390 px (after the fix); no external hosts; no page errors. |

**Regression:** after the shared table-frame fix, `src-e2e.cjs` (NEXT35) still passes 23/23.

## G. Gaps

1. **KPIs not shown:** "available payloads", "associated projects" and "referenced artifacts". The registry listing cannot filter or count those server-side, and counting one page would be wrong.
2. **Filters not offered:** retention, import method (ZIP/Git), association status and storage status are not registry query filters yet.
3. **No retry of a failed analysis.** No idempotent re-analysis endpoint exists; Diagnostics states the real path (a new import).
4. **Registry coverage** for AQR, IQR, Performance, Critical E2E, Test Data and Schema Studio is as before. Review runs and executions are registered; Test Data and Schema Studio are not, and nothing was faked.
5. **Real user data.** The real `birknext` DB is empty, so verification used the M2LB copy. The four Terraform statuses there are the observed case.

## Matrix

| Requirement | Implemented | Automated tests | Browser verified | Evidence |
|---|---|---|---|---|
| Separate status dimensions | Yes (labels; one analysis status from areas) | Frontend 1 and 3 | 2, 6 | — |
| Terraform not failed for missing app code | Yes (shared rule and re-read) | Backend 3 tests | 3, 7 | sync updated 4 |
| Project ownership, no name guessing | Yes (records, Unassigned/Not recorded) | Frontend 1 | 4 | — |
| References with "not checked" | Yes | Frontend 3 | 5, 8 | — |
| Lineage, origin | Yes (recorded edges) | Frontend 3 | 11 | — |
| Pointers unchanged by viewing | Kept | Frontend 3 | 12 | 0 writes |
| KPIs, filters, search, sort, chips | Yes (registry-side) | Frontend 2 | 1, 13–15 | — |
| URL state: back and reload | Yes | Frontend 4 | 10, 15 | — |
| Responsive | Yes (containing-block fix) | — | 16 | — |
| Retry of failed analysis | No (no safe endpoint) | — | — | Gap 3 |
