# BirkNext — Skole Parquet / Bufdata implementation-gap acceptance (2026-11-09)

Branch `008-traceability-first`. Source: `a541e20e` + this change (committed with these reports).

**Result: 57/57 PASS** on a clean isolated stack — API 32/32, after API restart 3/3, browser 22/22.
Archive: **`M2LB Skole.zip`**, SHA-256 `7516b48d6bf77426feef67bca89d68070486896a0430b30c41085f103149dcd8`, 1,839 files (the brief's
`M2LB Skole(1).zip` does not exist here; this archive was explicitly chosen by the user). The archive was read only; nothing was extracted to disk.

## Setup (scratch, not committed)

- Real BirkNext API (clean tree = `a541e20e` + this change), port 5098, dedicated database `birknext_gap_20261109` (created for this run),
  content root without `appsettings.Local.json`, outbound proxies pointed at a dead port. Published frontend on 5198; Edge headless with every
  non-localhost request blocked.
- **Independent oracle** `gap/oracle.py` — standard-library Python reading the ZIP in memory; shares no code with BirkNext.
- **Synthetic controls** `gap/make_controls.py` — five archives (positive, partial, ambiguous, missing, unapproved) with their expected verdicts
  declared in the generator, not derived from BirkNext.
- Archives were imported through the **real Project Import endpoints/UI**; the evaluation is created by the import itself.

## Record-level reconciliation with the oracle

| Task | Line | Task state | Implementation | Verification | Readiness | Gate | Expected artifact(s) | Match |
|---|---|---|---|---|---|---|---|---|
| T014 | 66 = 66 | Not started | oracle *not evidenced* (ADR-028 not in register) = BirkNext *No matching source* (register entry Missing) | Not applicable = NotApplicable | NOT IMPLEMENTED | — | ADR-028 entry | ✔ |
| T105 | 527 = 527 | Not started | No matching source, scope absent | Test not found | BLOCKED | ADR-028 | `Skole/tests/M2LB.Skole.Unit/Leveranse/ParquetDeterminismeTests.cs` | ✔ |
| T119 | 600 = 600 | Not started | No matching source, scope absent; `Parquet.Net` not declared anywhere | Test not found | BLOCKED | ADR-028 | `Skole/src/M2LB.Skole.Infrastructure/M2LB.Skole.Infrastructure.csproj` | ✔ |
| T120 | 601 = 601 | Not started | No matching source; `DatasettParquetSkriver`, `DekningParquetSkriver` not declared | Test not found | BLOCKED | ADR-028 | `…/Leveranse/ParquetSkrivere.cs` | ✔ |
| T121 | 606 = 606 | Not started | No matching source; `ManifestSkriver` not declared | Test not found | BLOCKED | ADR-028 | `…/Leveranse/ManifestSkriver.cs` | ✔ |
| T122 | 607 = 607 | Not started | No matching source; `LeveransefilUtboksJobb` not declared | Test not found | BLOCKED | ADR-028 | `…/Leveranse/LeveransefilUtboksJobb.cs` | ✔ |

Gate: `tasks.md:38` → ADR-028 **PENDING**, blocks exactly T105, T119–T122; register last entry ADR-027 (oracle = BirkNext).
Feature: **"Parquet.Net for the Bufdata delivery" — Blocked**; contract `contracts/bufdata-leveranse.md`; source implementation 0 of 4;
tests 0 of 1 test task present; runtime execution **Not assessed**; affected requirements from the contract and tasks (incl. FR-019, FR-036).
Finding: *"Parquet.Net for the Bufdata delivery — implementation not evidenced in supplied archive"* (never "does not exist").
The 15 unrelated Blob-using files (Event Hub checkpoints, Revisjon audit log) are never attributed to the exporter.
Contract clauses: the oracle's 8 clauses have no code evidence; BirkNext does not judge clause behaviour (determinism, manifest-last, …) without
implementation — the contract-linked feature is reported as not evidenced (clause-level checks: NOT ASSESSED).

## Results

| Id | Result | Check |
|---|---|---|
| GA-01–04 | PASS | archive identity (preview SHA-256 = oracle), snapshot + evaluation made at import, identity tied to evaluation, 159 tasks / 0 checked |
| GA-R-T014…T122 | PASS | six records reconciled field by field (table above) |
| GA-10–16 | PASS | no Parquet package; types searched archive-wide; ADR-028 PENDING with register checked; exact blocked set; Blocked feature with contract; dimensions with numerators/denominators; affected requirements |
| GA-17–22 | PASS | narrower "not evidenced" finding; one scope finding (no per-task missing claims); unrelated Blob code not attributed; explainable findings, no fabricated lines; no runtime PASS; contract clauses not evidenced |
| GA-23 | PASS | review stored and shown on reload |
| GA-C-* | PASS | positive → Implemented, not verified → **Verified only after recorded passing executions** (labelled synthetic); partial → Partially implemented + "task complete, code missing"; ambiguous → Candidate + "several candidates" (human review); missing → Not implemented + "not in supplied archive"; unapproved → Blocked + "decision not approved"; five distinct verdicts |
| GA-24 | PASS | reviews never leak across imports |
| GA-30–32 | PASS | after restart: evaluation, findings, review and the Verified control are intact; evaluations listed per import |
| GB-01 | PASS | Import Project in the browser with the oracle SHA-256 |
| GB-02–04 | PASS | Specification / Plan / Task Explorer deep links focus module Skole and open the exact document; T119–T122 details (T119 shows the ADR-028 gate) |
| GB-05–12 | PASS | Implementation Evidence Review: archive/SHA/snapshot context; Blocked feature with ADR-028 · Pending; contract, tasks, "not evidenced in supplied archive"; runtime Not assessed; task / implementation / verification columns; the finding with actions; no runtime PASS; "View dependency" → gate |
| GB-13 | PASS | Quality Review → feature delivery readiness: same Blocked conclusion as the backend |
| GB-14–16 | PASS | "View source inventory" → exact snapshot; review saved and shown after reload; keyboard order |
| GB-17 | PASS | 390 px without page scroll (after fixing two layout defects, below) |
| GB-18 | PASS | positive control through the UI: confirmed implementation recognised, not verified without executions |
| GB-Z1 | PASS | no external request, no page error, no console error |

## Defects found during acceptance (fixed)

| Id | Where | Fix |
|---|---|---|
| GAP-DEF-10 | positive control Partially implemented via the API | package paths mapped repository → archive (regression test, red first) |
| GAP-DEF-11 | T014 verification ≠ oracle | decision-drafting tasks: verification Not applicable |
| UI-1 | Explorer `?document=` ignored on multi-module imports ("Choose a module") | focus the document's module first (or the module its path names), then the document |
| UI-2 | 390 px page scroll from the dimensions table | host page's unscoped `table{min-width:820px}` overridden in the component; table in a scroll region |
| UI-3 | 390 px page scroll from long gate chips (gates without a decision record carry their full label) | chips wrap |

Harness corrections (scratch only): spec/plan assertions read the selected document instead of page text; an already-imported archive needs an
explicit "Import again"; the review check picks any not-yet-reviewed finding.
