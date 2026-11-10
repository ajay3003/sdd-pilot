# BirkNext — Documentation Quality & Drift: acceptance (2026-11-10)

Branch `008-traceability-first`. Baseline `3ca01b8c`; verified source = `b3f6f84f` (+ reports).

**Result: 23/23 PASS** in round 3 (main 19 · after restart 4). Round 1: 18 PASS, synthetic findings mixed with another import after a reload
(product defect DQ-D1) + harness timeout; round 2: 22/24 — the Changes view ignored the selected import (product defect DQ-D2) and the harness
reloaded during an in-flight auto-save (console error; harness now waits). No expectation was weakened to pass.

## Setup (scratch only)

Real BirkNext API (scratch tree) on :5098, fresh DB `birknext_dqa_20261110`, no `appsettings.Local.json`, outbound proxies to a dead port;
published **Release** frontend :5198; Edge headless, every non-localhost request blocked. Archives imported through the real Project Import UI:
`M2LB-language.zip` (read-only) and synthetic `dq-v1.zip` / `dq-v2.zip` (same project lineage). Oracles: `dqa/oracle.py` (stdlib, reads the ZIP:
Data Model documents per module, comparable / missing modules, field-name sets) and `dqa/zips/dq-oracle.json` (written with the fixtures).

## Real M2LB-language (oracle derived from the archive, not from the screenshot)

Oracle: 7 comparable modules (AutorisasjonService, HendelseAdapter, HendelseService, Person, PersonAdapter, SkoleService, TjenesteService), 5
with authored but no generated Data Model (Frontend, MeldingsutvekslerReceiver, Proxy, Revisjon, SkoleAdapter); field-name sets differ in all 7.

| Check | Result |
|---|---|
| DQ-01 7 comparable, 5 missing, compared "0 / 7", potential drift "Not assessed" | PASS |
| DQ-02 module area states = oracle | PASS |
| DQ-03 evidence explanation separates project readiness from area comparability | PASS |
| DQ-04 preview lists exactly the 7 eligible areas | PASS |
| DQ-05 Compare all eligible: 7 compared in one action, missing areas skipped | PASS |
| DQ-06 no false CONSISTENT: every module whose field sets differ has ≥ 1 difference finding | PASS |
| DQ-07 missing evidence produced no finding | PASS |
| DQ-08 finding detail: expected vs actual, authored + generated evidence, rule, severity rationale | PASS |
| DQ-09 review recorded (ConfirmDifference), analyzer verdict unchanged | PASS |
| DQ-10 report: 13 sections, the confirmed finding, no "0 drift" | PASS |
| DQ-L1…L3 overview, compare, findings: no page-level horizontal scroll at 1440 / 1280 / 1024 / 768 / 390 px | PASS |
| DR-01 after restart the comparisons persist (compared count unchanged) | PASS |
| DR-02 unchanged inputs are not offered again (no duplicate comparisons) | PASS |
| DR-03 after restart the review decision reopens exactly as saved | PASS |
| DQ-Z1 / DR-Z1 no external request, no page error, no console error | PASS |

Findings per module after the bulk run (analyzer verdicts): AutorisasjonService 8 potential drift + 12 needs review; HendelseAdapter 7;
HendelseService 9 + 6; Person 74 + 3; PersonAdapter 6; SkoleService 1; TjenesteService 9. These are documentation consistency findings for a
person to review — not proven implementation defects.

## Synthetic control project (exact oracle)

| Check | Result |
|---|---|
| DS-00 choosing an import keeps it in the URL | PASS |
| DS-01 compared areas = fixture oracle (7) | PASS |
| DS-02 findings exactly = oracle: MissingField Person.Email (potential drift), TypeChange Person.Age (potential drift), Conflict Person.Kode (conflict); exact, reworded, additive and superseded modules consistent | PASS |
| DS-03 v2 of the same project: the type finding keeps its id and is Reopened (earlier decision kept) | PASS |
| DS-04 exact-content move detected between the two imports | PASS |

Unit-level oracle (`DocumentationQualityTests`) covers the same scenarios plus ambiguous pairing, unsupported area, unrelated project (no move)
and idempotent re-runs.

## Defects found and fixed

| Id | Round | Defect | Fix |
|---|---|---|---|
| DQ-D1 | 1 | after a reload the page fell back to the workspace's current import (the chosen import was not kept) | `?import=` honoured by the panel; import selection and tab clicks write the URL (`b3f6f84f`) |
| DQ-D2 | 2 | the Changes view's lineage ignored the selected import | lineage follows the selected import (`b3f6f84f`), bUnit regression |

## Not covered here

Semantic equivalence of reworded prose, API/Architecture/Configuration structural comparison (no extractor on this page), requirement and test
links by evidence, security-requirement mismatch (N): NOT SUPPORTED — see the gap register.
