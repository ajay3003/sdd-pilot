# BirkNext — Documentation Quality & Drift: implementation (2026-11-10)

Branch `008-traceability-first`. Baseline `3ca01b8c`. Audit: `docs/audits/birknext-documentation-quality-drift-audit.md`.
Local commits: `39f9a30f` (quality layer, views, tests) · `b3f6f84f` (URL state + Changes lineage, found by acceptance) · reports commit.

## Architecture (no second engine, no second store)

`Services/DocumentationQuality.cs` is a thin, pure layer over the existing Documentation Drift engine:

| Function | Reuses | Adds |
|---|---|---|
| `Areas` | `DocumentationDriftComposite.CandidateGroups`, `SavedFor` | one state per module · area: **Compared**, **Not assessed** (comparable, not compared or inputs changed), **Missing generated**, **Missing authored**, **Ambiguous** (several generated candidates), **Not supported** (generated areas without a structured extractor, e.g. Architecture) |
| `RunBulk` | `BuildDataModel` → `CompareDataModel` → `Save` (the same path as a single comparison) | Compare all eligible / selected / changed; idempotent (identical inputs → "Unchanged", nothing saved); ambiguous, missing and unsupported areas never compared; missing captured text reported |
| `Findings` | saved structured comparison items | stable id `dq-` + hash(lineage · module · area · concept · category); expected (authored) vs actual (implementation-derived); authored references (path · section), generated reference, specification packs (provenance); rule (`DataModel structural v1 · category`); confidence; severity with rationale |
| `Review` | `SddLifecycleState` (workspace persistence) | append-only `SddDriftReview` (decision, note, reviewer, time, inputs fingerprint, comparison id); a newer comparison with other inputs shows the finding **Reopened** with its history |
| `Summary` / `Report` | — | KPIs; Markdown report with 13 sections through the existing `downloadBase64File`; "Not assessed" — never "0 drift" — before any comparison |
| `Moves` | catalogue + import lineage | exact-content moves between consecutive imports of one project lineage |

Analyzer verdict vs human review are separate. Mapping of engine categories: field type / constraint / relationship difference and
authored-only concept → **Potential drift** (Medium); generated-only → **Needs review** (Low, "additional implementation detail", never a
defect); authored conflict → **Conflict** (Needs review); not comparable → **Not comparable**. Missing evidence produces no finding. Confirmed
difference / accepted change / false positive / resolved close a finding for its inputs; clarification / defer keep it in "needs review".

Many-to-one applicability (e.g. AutorisasjonService 3 authored ↔ 1 generated) stays the engine's evidence rule: authored packs are additive;
a later pack's explicit change statement supersedes; any other contradiction is an authored conflict for review — never false drift.

Saved-comparison history raised from 50 to 200 (bulk runs never save duplicates).

## UI (`/documentation-drift`)

- **Overview**: KPIs (comparable, compared, confirmed differences, potential drift, needs review, missing evidence — "Not assessed" when
  nothing compared), "What evidence is available and why" (authored / generated / source, and why the project strip's "required evidence roles"
  does not decide area comparability), actions (Compare all eligible, Review findings, Quality report, Compare versions when a lineage has two
  imports), compact module rows with area chips. The old inventory dashboard stays under "Inventory and import details".
- **Findings** (new tab): search, status, module and severity filters; compact rows; detail with expected vs actual, evidence, rule,
  severity rationale, trace links (Data Model / Specification Explorer, Artifact Traceability, Impact Analysis), review history and the six
  review actions.
- **Compare**: automatic candidates (checkboxes), Preview, Compare all eligible / selected / recompare changed, per-area result table; every
  expert mode kept under "Advanced comparison".
- **Changes**: unchanged empty state for one import; moved documents listed.
- URL state: `?tab=` and `?import=` follow tab clicks and the import selector (reload, back and links keep the context).

## Tests

`DocumentationQualityTests` (7): area states (A–M fixtures incl. ambiguous and unsupported), bulk + idempotency + exact finding-level
expectations (C, D, unresolved H; A, B, G, superseded H consistent), reviews append-only/reopen with stable ids, moves within a lineage only,
UI (overview → compare all → findings review → report), URL import selection, Changes lineage. Existing Drift / Generated Documentation tests
green (one tab-list expectation updated for the new Findings tab).
