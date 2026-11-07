# BirkNext Real Project Data Acceptance — 2026-11-07

Coverage matrix: `birknext-real-data-coverage-matrix-2026-11-07.md` · Gap register: `birknext-real-data-gap-register-2026-11-07.md`.

## Source and environment

| Item | Value |
|---|---|
| Tested source | HEAD `50c6c0ba` (docs only after `60671884`); API binaries = the `60671884` Release publish (`AIAssisted/` tree `81b5fdb7`, identical at HEAD); web = the same publish |
| Isolated stack | API `127.0.0.1:5099`, web `:5199`, fresh content root (`api-real`, new App_Data and launch code), **new database `birknext_realdata_acceptance`** (empty, migrated at start; the API's only database connection was verified to be this one before any write) |
| Outbound blocking | Browser: every non-localhost request aborted (none attempted). API process: `HTTP_PROXY`/`HTTPS_PROXY`/`ALL_PROXY` → dead local proxy, loopback exempt |
| Untouched | User stack on :5000/:5173 (restarted by the user during the session — PIDs 27508/31956 — never touched), existing databases (incl. `birknext_g67`), `appsettings.Local.json`, `testdata/` |
| Inputs | `AIAssisted/testdata` read only (`fs.readFileSync`); SHA-256 of all 11 files identical before and after (RD-SAFE-1). Negative inputs are DERIVED copies in the scratch folder, labelled as such |
| Window | 2026-11-07 ~15:40–16:20 local |

## Dataset inventory (summary)

11 files, 19 MB. M2LB.zip and M2LB-language.zip are two authentic exports of the same Spec-Kit/.NET solution (68/69 projects, 28/33 feature folders,
6 XSD, 12 OpenAPI, 27 GraphQL); M2LB.Common.zip (.NET library); five Terraform archives (infrastructure; one with 32 XSD); "Tiltak 2 – Sikkert
digitalt økosystem" (.NET, 14 projects, 24 XSD, 34 XML); `skolenaervaer.json` (a standalone OpenAPI document); `desktop.ini` (Windows metadata).
Several archives contain **credential-like values and 11-digit numbers** (counted only: e.g. 128–130 and 106–128 in the M2LB archives, 36 and 49
in Tiltak 2); no value was printed, logged by the test or copied into a fixture. Full table with hashes in the coverage matrix.

## Results

| Area | Result |
|---|---|
| Import Project (9 archives) | **PASS** — real preview → commit for every archive; archive identity = file SHA-256 (9/9); snapshot persisted and re-read by id (9/9) |
| Source-derived counts | **PASS (exact)** — projects = `.csproj` files (68, 69, 2, 14, 1, 0…); XSD schemas = XSD entries (6, 6, 24, 32, 0…) |
| Duplicate / revision | **PASS** — re-importing the current archive under another name: Reused, same snapshot; M2LB → M2LB-language: new snapshot, old kept |
| Negative inputs | **PASS 5/5** — truncated ZIP `ARCHIVE_INVALID_ZIP`; zip-slip `ARCHIVE_PATH_TRAVERSAL`; `desktop.ini` `ARCHIVE_UNSUPPORTED_FORMAT`; empty `ARCHIVE_EMPTY_UPLOAD`; unknown staging 404 `IMPORT_STAGING_EXPIRED` |
| Snapshot registry | **PASS** — both M2LB snapshots resolved by type and id |
| Delivery (Spec-Kit) | **PASS** — both exports stored, distinct fingerprints; features 28 / 33 = feature folders (independent count; two folders per export have a plan/tasks but no `spec.md`) ; tasks 1,415 / 1,742 |
| Canonical interpretation | **PASS** — 6,773 entities, 10,162 relations, references to both the source and the delivery snapshot; re-interpretation of unchanged input reused (no duplicates); 200 entities sampled, 0 duplicate identities |
| Relationship Review | **PASS** — 13 decisions (1 identity conflict "M2LB.SkoleAdapter.Worker … alias of more than one", 1 test link, 11 story references) and 2,430 suggestions, all left to people; the identity conflict was not resolved by the test |
| Release Readiness | **PASS** — Blocked by governance (the open identity conflict); scope and requirements Ready from real evidence; test design Ready with limitations; functional execution Not assessed; frozen report re-read unchanged |
| Connected Systems / Catalog | **PASS** — discovery proposed 54 unconfigured candidates from 8 contributing sources; 0 integrations registered by discovery |
| Integration Topology | **PARTIAL** — static topology read (components with provenance); contract discovery (`POST …/discover`) returns **403** to a local session (capability-gated) — RD-GAP-1 |
| M01 (static) | **PASS** — without a source snapshot correctly refused (400, reason given); with the M2LB-language snapshot: stored (0.4 s), retrievable as latest; no runtime control passed |
| Test Data | **PASS** (catalog read; nothing approved by imports) |
| Browser (21 pages) | **PASS 21/21** — real content, survives reload, no page/console errors, no 5xx, no external request; real imports visible on Source Analysis (M2LB-language, Tiltak 2), Snapshots, Delivery Reports, Import Project history (by project name "M2LB") |

## Timings (elapsed, isolated stack)

| Dataset | Preview | Commit (analysis + save) |
|---|---|---|
| M2LB.zip (8.2 MB, 2,361 entries) | 5.8 s | 63.9 s |
| M2LB-language.zip (9.2 MB, 2,612 entries) | 4.0 s | 68.5 s |
| Re-import (Reused) | 3.9 s | 0.1 s |
| Tiltak 2 (0.7 MB) | 1.5 s | 1.6 s |
| Terraform / Common | 1.4–1.9 s | 0.03–0.4 s |
| Interpretation (source + delivery) | — | 3.6–5.1 s |
| Delivery import (each export) | — | 0.85–0.90 s |
| Pages | 5.1–5.6 s to first content (incl. WASM start) | — |

API memory per step: **not measured** (the probe returned no value); system free memory stayed above the 1.5 GB floor throughout. Acceptance
database after the run: 63 MB. No configured performance threshold applies to these operations; none was invented.

## Not run / unsupported (by rule or scope)

| Item | Status | Reason |
|---|---|---|
| API Quality Review, Frontend Quality Review, Performance execution, IQR runtime, Skolenærvær journeys, M01 runtime | NOT RUN | Need live endpoints (real hosts in the sources and in `skolenaervaer.json`); no external calls allowed |
| `skolenaervaer.json` as standalone input | UNSUPPORTED | Import accepts ZIP archives only; the 12 OpenAPI files inside the M2LB archives were analysed |
| Real XML validated against the real XSDs (Tiltak 2) | NOT RUN | Validation runs in the XML fixture/Schema Studio UI on generated synthetic XML (covered in G6); real XML could carry personal data |
| XML fixture generation from a real XSD | NOT RUN | Not exercised in this pass (synthetic coverage: G6 22/22) |
| Analysis cancellation / restart with real data | NOT RUN | Covered by the BQA-007 failure-injection tests on synthetic data |
| Git Sources real sign-in | NOT RUN | No Microsoft contact allowed; local page renders (RD-UI-20) |

## Verdict

| | Assessment |
|---|---|
| A. Real-data functional acceptance | **PASS within the executed scope** — import, analysis, persistence, delivery, interpretation, review, readiness and static integration/M01 flows work on the authentic project archives |
| B. Source-analysis reliability | **PASS** — identities and countable facts match the archives exactly; negative inputs rejected precisely |
| C. Cross-module consistency | **PASS** — the same snapshot ids flow through registry, delivery, interpretation and readiness; topology contract discovery capability-gated (RD-GAP-1) |
| D. Persistence integrity | **PASS** — re-read by id, reuse without duplicates, history kept, frozen report unchanged |
| E. Browser workflow reliability | **PASS** — 21/21 pages; note that most pages are read here, the writes were made through the same API the UI uses |
| F. Readiness accuracy | **PASS** — never Ready without executed tests; governance conflict blocks; runtime Not assessed |
| G. Remaining critical defects | **None found** |
| H. Next actions | Decide RD-GAP-1; run real-XML validation and XSD-derived fixture generation with a sanitized copy; consider dropping `birknext_realdata_acceptance` after review (contains real source content; not deleted without authorization) |

This acceptance does **not** change the release-assurance decision: G9 and G10 remain PARTIAL; real-data validation is not a substitute for
clean-PC, cross-account security or authorized live integration testing.
