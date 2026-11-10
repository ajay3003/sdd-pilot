# BirkNext — Parquet Quality Review: acceptance (2026-11-10)

Branch `008-traceability-first`. Baseline `f55c7c49`; verified source = `13f1dc5b` + the parser fix found by acceptance (reports commit).

**Result: 48/48 PASS** in the final round (main 42 · after API restart 6). Earlier rounds and what they found:

| Round | Result | Cause | Kind |
|---|---|---|---|
| 1 | harness stopped | the launch code exists only after the first local-session request (harness start helper) | harness |
| 2 | 3 failures | the parser applied renames from `domain-term-inventory.md` (database tables `dekningsoppsummering` → `dekning_summary`) to the Parquet contract, and treated a data-model entity table as a Parquet dataset | **product defect PQ-D1** |
| 3 | 1 failure | only 2 of 5 business rules: the real contract wraps rule sentences across lines; prose was read line by line | **product defect PQ-D2** |
| 4 | harness stopped | Validation reopened on the Requirements view; the harness looked for the approve button on Rules | harness |
| 5 | 1 failure | the synthetic file matches both the synthetic contract and the identical Bufdata coverage schema → AMBIGUOUS (correct product behaviour; the expectation was wrong) | harness expectation |
| 6/7 | 1 failure | text assertion without whitespace between cells (the page showed Pass) | harness |
| 8 | **48/48** | — | — |

No product expectation was weakened: PQ-D1 and PQ-D2 were fixed in the parser with unit regressions (shapes taken from the real documents);
the synthetic contract fixture now wraps its sentences like the real one.

## Setup (scratch only)

Real BirkNext API (scratch tree) on :5098, fresh DB `birknext_pqa_20261110`, no `appsettings.Local.json`, outbound proxies to a dead port;
published **Release** frontend :5198; Edge headless, every non-localhost request blocked. Imported through the real Project Import UI:
`M2LB.zip` and `M2LB-language.zip` (read-only; both hold the same `bufdata-leveranse.md`) and a synthetic `SyntheticDelivery.zip` (the
synthetic contract + rename document). Two synthetic Execution Evidence runs were inserted into the scratch DB (one whose captured call
names the synthetic file's SHA-256, one unrelated). Oracles: `pq_oracle.py` (stdlib Parquet reader) + `real_oracle.py` (contract text read from
the archive, expected rule outcomes), and the fixtures' `quality-oracle.json` (independent writer).

## Real Skolenærvær sample (`skolenaervaer-dekning-example.parquet`, read-only)

Independent facts: 15 rows, 1 row group, UNCOMPRESSED, columns `periode`, `kategori`, `maal`, `aarsakskode` (optional, 13 null), `antall`
(INT32, 0…10); legacy (pre-`1.0.0-draft`) names; documented renames: `periode`→`period` and every value used; undocumented for the file:
`kategori`, `maal`, `aarsakskode`, `antall`.

| Check | Result |
|---|---|
| PQ-E1 guided empty state (no tabs, limits and expiry explained) · PQ-E2 corrupt file refused (no path) · PQ-E3 > 64 MB refused before upload | PASS |
| RQ-01 file opens into Quality Overview, compact source bar, no baseline input | PASS |
| RQ-02 metadata = independent reader (15 rows, 5 columns) | PASS |
| RQ-04a the same contract in two imported projects → AMBIGUOUS | PASS |
| RQ-04b within project m2lb → CANDIDATE: file name a hint, documented `periode→period`, 4 columns unexplained | PASS |
| RQ-05 exact identity: `skolenaervaer-dekning` `1.0.0-draft`, governance **Draft**, project m2lb, documented mapping, proposed | PASS |
| RQ-06a as delivered: schema **Fail**, data Fail, business / E2E not assessed (separate results) | PASS |
| RQ-06b contract rules generated; 5 business rules NEEDS REVIEW, not run | PASS |
| RQ-07a missing contract columns fail; `period` present via documented rename | PASS |
| RQ-08 aggregate requirement Not assessed, "Never a single percentage" Not supported, Kode 6/7 NEEDS DOMAIN DECISION | PASS |
| RQ-07b with the tester's declared mappings the schema is only **Partial** | PASS |
| RQ-07c after approval every rule outcome = oracle: allowed values, format, sum identity, roll-up, combinations pass; **sort order fails** (translated values sort differently), **Snappy fails** (file is uncompressed); manifest not assessed | PASS |
| RQ-07d approved rules are APPROVED_BUSINESS_RULE | PASS |
| RQ-10a failed rule: expected/actual, 15 rows examined (exact), first rows, contract line | PASS |
| RQ-03 schema checks through the mappings, nothing missing | PASS |
| RQ-09 exact profile = oracle (13 null `aarsakskode`, `antall` 0…10, every contract aggregate cell) | PASS |
| RQ-10b typed filter chip, whole-file scope, 2 matches = oracle | PASS |
| RQ-11a an unrelated run is refused as proof · RQ-11b saved as MANUAL_UNVERIFIED · RQ-11c stored evidence identity/association/rule-set ref, no reason-code values, no keys | PASS |
| RQ-13 export redacted · RQ-12a report: 14 sections, draft, "never a verified production delivery", NEEDS DOMAIN DECISION | PASS |

**The synthetic sample is not, and is never presented as, a production Bufdata delivery**; against `1.0.0-draft` it is schema-incompatible
as delivered.

## Synthetic control (independent oracle)

| Check | Result |
|---|---|
| SQ-01a full schema match to two contracts (synthetic + identical Bufdata schema) → AMBIGUOUS · SQ-01b after choosing the project → CANDIDATE | PASS |
| SQ-02 `q_bad_aggregate`: sum identity and roll-up fail (1 group each = oracle), allowed values pass | PASS |
| SQ-03 group detail names `category=FOSTERHJEM` (live only) · SQ-04 drill-down → whole-file filter, 4 matches = oracle | PASS |
| SQ-05 baseline `q_valid` → REGRESSION from rule outcomes | PASS |
| SQ-06 the run whose evidence names the file SHA-256 → CONFIRMED; E2E evidence **Pass** only then | PASS |
| PQ-X1 expired session → actionable message, no stale success · PQ-X2 save without a permitted session → 401/403 · PQ-K1 keyboard | PASS |

## Layout, restart, hygiene

| Check | Result |
|---|---|
| PQ-L1…L8 overview, requirements, rules, profile, data, evidence, compare, history: no page-level horizontal scroll at 1440 / 1280 / 1024 / 768 / 390 px | PASS |
| PR-01 recent session files after restart · PR-02 both evidence records persist · RQ-12b history reopens without the file ("cannot be re-run") · PR-03 report after restart | PASS |
| PQ-Z no external request, no page error, no unexpected console error (the 3 deliberate 422 refusals are counted separately) | PASS |

Unit-level independent oracle (`ParquetQualityTests`, 12 theory cases + 10 facts) covers every synthetic fixture: valid, missing column,
wrong type, null in required field, invalid enum, duplicate composite key, wrong period, wrong aggregate, added optional column, breaking
change, other dataset, protected canary + identifier, changed baseline, legacy layout.
