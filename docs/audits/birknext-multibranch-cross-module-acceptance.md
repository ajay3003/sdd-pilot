# Multi-branch source context — acceptance (tasks 14 + 15, continuation of 2026-11-12)

**Status:** real-archive browser acceptance COMPLETE for every check below, except TE-0 (NOT RUN, reason given). Full regression: see the last
section. Nothing is claimed beyond what ran.

- **Code under test:** `22710749` on `008-traceability-first`. The isolated install was published from the scratch build tree, which was verified
  identical to that commit (107 files changed since task 12 compared byte for byte, shared CSS included).
- **Isolated install:**
  - Release API on :5496, Development environment, database `birknext_mb` in the disposable PostgreSQL 16 cluster (port 55990, application role
    NOCREATEDB);
  - Release web on :5497;
  - Microsoft Edge driven by Playwright; every non-localhost request aborted (none occurred).
- **Archives:** real archives read-only from `C:\Users\ajaan\Downloads` (`C:\Downloads` does not exist). Expected values come from the independent
  oracle `birknext-m2lb-source-inventory.md` (`scratchpad/mb/out/inventory.json`, `oracle.md`).
- **Harness:** `scratchpad/mb/accept2.cjs` (phases import, dup, verify, baseline, restart, hist, visual). Results are in `scratchpad/mb/out/accept2-*.json`.

## Restart (requirement 1)

- **Before restarting:**
  - Available memory was 4.2 GB, with commit at 43.8 of 52.1 GB.
  - Ports 5496, 5497 and 55990 were free; 5000 and 5173 belonged to the user's services, which were not touched.
  - The cluster data was intact (`PG_VERSION` 16, no stale `postmaster.pid`). `birknext_mb` held 5 snapshots and no decisions.
- **Order:** code fixes and builds were done first. The services were then started one at a time, and the cluster recovered normally.
- **Memory:** available memory dropped to about 1.3 GB during the first imports. Nothing was terminated except my own acceptance API, stopped
  deliberately before each republish.
- **Afterwards:** API, web server and cluster were stopped (`pg_ctl -m fast stop`); the data is kept.

## Import facts (real archives)

| Archive | Archive SHA-256 (oracle) | Content fingerprint (oracle) | Snapshot | Branch | Result |
|---|---|---|---|---|---|
| M2LB (3).zip | BC780B6B… | B3D2547B… | 151ff305 | UNKNOWN | PASS (FP) |
| M2LB (4).zip | 04903137… | E2A0D4C0… | — | UNKNOWN | PASS (FP) |
| M2LB (5).zip | 0AF7DCF7… | 660DD268… | 3cd7572a | UNKNOWN | PASS (FP) |
| M2LB (6).zip | A306EE47… | 737A4F22… | — | UNKNOWN | PASS (FP) |
| M2LB (7).zip | C7BB9F12… | CA5992E5… | 46d8661d | UNKNOWN | PASS (FP) |

- All five were imported through the UI; (7) had also committed before the earlier interruption. The previous report's "4 of 5" under-counted.
- No archive contains Git metadata, so no branch is shown or derived from file names.
- After the duplicate tests the source holds **7 snapshots with 5 distinct contents**: the re-zip `M2LB.zip` and a re-import of (3) share
  (3)'s content.

## Results by requirement

| # | Requirement | Checks | Result |
|---|---|---|---|
| 1 | Fifth import | FP-* | PASS |
| 2 | Five snapshots and fingerprints | FP-*, RST-3 | PASS (also after the API restart) |
| 3 | Re-zipped duplicate detection | DUP-1 **FAIL** → fixed → DUP-1b/DUP-3b PASS | The file was chosen before the page's source-setup load finished, so the content check had no snapshots and `M2LB.zip` was imported as new. Fix: the session reads the setup itself. Re-run with the never-imported `M2LB Skole.zip` (same content as (4)): detected, import disabled, no snapshot created. |
| 4 | Duplicates keep history | DUP-2, DUP-4, DUP-5, DB check | PASS. In the saved workspace, the `M2LB (3).zip` import record has `History` [151ff305 at 23:26 (legacy, seeded), a0faecfa at 07:33]; the earlier snapshot is unchanged. |
| 5 | Logical grouping | ID-1, ID-2 | PASS: 1 logical source, 7 snapshots, 5 contents. Basis "archive name, not verified identity" is shown. |
| 6 | Baseline selection | BL-1 | PASS (UI). First attempt: the decision was recorded but the panel collapsed the decided source (defect, fixed). |
| 7 | Switch versions | BL-4, BL-5, CR-2, SU-2 | PASS |
| 8 | Compare any two | CMP-0 (4)→(6) without a baseline, CMP-1/2 (5)→(7), CMP-3 | PASS: 4 228 changes for (5)→(7); 268 for (5)→(6) |
| 9 | XML/XSD differences | XSD-1 | PASS: skolenaervaer v2.0.0.flat → v2.0.1.flat (matches the oracle) |
| 10 | MM and SkoleService | MM-1, SKO-1 | PASS: Meldingsutvekslermottak → MeldingsutvekslerReceiver; SkoleService added in (7) |
| 11 | Source Analysis scope | SA-1 | PASS: the exact linked snapshot, with role badge `BASELINE_NOT_SELECTED`, later `ALTERNATIVE_VERSION` |
| 12 | System Understanding | SU-0, SU-1, SU-2, H-1…H-4 | PASS: without a baseline, "BASELINE_NOT_SELECTED", never "not imported" |
| 13 | Integration Topology | TOP-0, BL-2, BL-4, RST-TOP | PASS: no source-derived M2LB component without a baseline; (5) shows only Norwegian components, (7) only English |
| 14 | Contract Registry | CR-0, CR-1, CR-2 | PASS: none without a baseline; (5) has `Skole/…`, (7) has `SkoleService/…` |
| 15 | Project Quality / Workflow | PQ-0, BL-3, BL-5, UI-N-*, UI-B-* | PASS: coverage withheld with a `BASELINE_NOT_SELECTED` notice, then follows the baseline |
| 16 | Target Environment associations | TE-0 | **NOT RUN**: the isolated install has no Target Environment with Target Application "M2LB", and creating one was outside the approved scope. Covered by `ProjectEvidenceVersionsTests` only. |
| 17 | Historical reviews and provenance | H-0…H-5 | PASS: a guide revision written under (7) and submitted for review still cites (7) after a switch to (5); the guide reports the difference |
| 18 | Refresh and API restart | RST-1…RST-UI | PASS: baseline, history (2 decisions) and fingerprints persist; the notice survives a browser reload |
| 19 | Responsive layout | VIS-* (31 checks) + inspected screenshots | PASS: no overflow at 1440/1280/1024/768/390; text ≥ 13 px. Defect found by inspection: text buttons kept browser button chrome at narrow widths; fixed and re-shot. |

**Totals** (final run of each phase): import 8/11, then dup 3/3, verify 17/18 + 1 NOT RUN, baseline 21/24, then CMP-4 fixed (re-zip comparison
now reports no change), restart 11/11, hist 7/7, visual 31/31.
- Import failures: DUP-1/DUP-3, fixed and re-verified as DUP-1b/DUP-3b; Z (see below).
- Baseline failures:
  - HIST-1 (harness: the draft content was unchanged, so nothing was saved; replaced by phase H);
  - CMP-4 (product defect, fixed, verified);
  - Z (see below).

**Z (console errors) — not fixed in this task.** Workspace auto-save and load failed with "Request body too large (30 000 000 bytes)". The saved
workspace lifecycle JSON is 23 MB with 7 large imports. This is gap MB-20.

## Defects found by real-archive acceptance and fixed (each with a regression test)

| Defect | Fix | Test |
|---|---|---|
| Content-duplicate check raced the async source-setup load | `ProjectImportSession` reads the setup when it is missing | `ProjectImportSourceSetupTests.A_content_duplicate_is_detected_even_when_the_file_is_chosen_before_the_source_setup_loaded` |
| Source versions collapsed the source just decided | The source being acted on stays open | `SourceContextBaselineNotSelectedTests.The_source_stays_open_after_the_person_chooses_its_baseline` |
| Re-zip comparison reported "unclassified change, review required" | Equal content fingerprints mean no source change | `SourceChangeImpactServiceTests.The_same_content_in_a_different_archive_is_no_source_change` |
| Topology said "no snapshot exists" when M2LB was awaiting a baseline | `TopologyContext.AwaitingBaseline` names the left-out sources | `TopologyIntegrationTests.SourceAnalysis_source_names_the_sources_left_out_for_want_of_a_baseline` |
| Guide said "No longer imported" for a version that is only no longer the baseline | `SourceChanges(…, stillImported)` | `SystemGuideTests` (extended) |
| Guard trigger referenced `OLD."Id"` (columns are snake_case), so updates were refused with the wrong error | `OLD.id`. The acceptance DB got the corrected function body (same migration id; a fresh DB gets it from the migration). | `WorkspacePackageRoundTripTests` asserts the guard's own message on real PostgreSQL |
| Text buttons rendered with browser button chrome | Scoped `button.btn-link` styles | Visual re-shot (390 px) |

## Migration validation (requirement 4)

- `20261112070530_GuardSourceDecisions` was applied as an **upgrade** of the populated `birknext_mb` at API start. The log shows it applied, with no
  errors.
- Trigger `source_decisions_no_update` is present. An UPDATE inside a rolled-back transaction is refused: "source decisions are append-only
  (decision df7578cb…)".
- On fresh databases, the round-trip tests (real PostgreSQL) apply it, and an update through EF is refused with that message.

## Decision history recorded in the acceptance install

There are 6 baseline decisions, all by `local-session:loopback`:
- 5 → 7 (the baseline phase; the first selection of (5) was made in the attempt whose panel collapsed);
- 5 → 7 with reasons (the historical-provenance phase);
- 5 → 7 with reasons (the guide wording check after its fix).

The final baseline is M2LB (7). It is an analysis baseline only; no deployment claim is made.

## Full regression

See `birknext-multibranch-implementation.md` → "Final regression".
