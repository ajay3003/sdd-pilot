# BirkNext — Contract Testing final integration & identity hardening: acceptance (2026-11-10)

Branch `008-traceability-first`. Baseline `ae1db8e5`; verified source = `770c8c99` (+ reports).

**Result: 67/67 PASS** (API 28 · browser 27 · after restart 8 · browser after restart 4) in round 4, plus **migration acceptance
MG-01…MG-07 PASS** on a disposable synthetic legacy database. Earlier rounds: round 1 harness errors (fixture layout, SQL escaping); round 2 found
one product defect (CTH-D1) and three harness expectation errors; round 3 one harness session error. No round changed a product expectation to
make it pass.

## Setup (scratch only)

Real BirkNext API (scratch tree) on :5098, fresh DB `birknext_cth2_20261110`, no `appsettings.Local.json`, outbound proxies to a dead port; published
**Release** frontend on :5198; Edge headless, every non-localhost request blocked; a disposable local Target Environment profile (`Local synthetic`,
`http://127.0.0.1:5099/`) stored in the test browser's own storage; loopback target :5099 recording every request. The running stack on 5000/5173
was not touched. Archives read-only: `M2LB-language.zip` (`a5c083c8…`), `M2LB.zip` (`22339207…`); oracle `cth2/oracle.json` (stdlib reader).
Synthetic projects `proj-a-v1.zip`, `proj-b.zip`, `proj-a-v2.zip` with their own expectation file (`synthetic-oracle.json`, written with the fixtures).

## The four key criteria

| Criterion | Evidence | Result |
|---|---|---|
| 1. Project isolation: two projects import `Person/autodoc/openapi.json` without mixing contracts or tests | FS-02, FS-03, FA-05, FA-06 | PASS |
| 2. Configure many contracts for one approved environment at once | FA-16, FA-17, FS-09, BE-13…BE-15 | PASS (24 applied in one action on M2LB; secured contracts blocked, never anonymous) |
| 3. Run a test from Contract Testing; evidence and coverage update | BE-01…BE-11, BR-01, BR-02, FR-08 | PASS |
| 4. Project coverage: tests / executed / verified across the project | FA-18, BE-11, BE-12, FR-08 | PASS |

## Real M2LB

| Check | Result |
|---|---|
| FA-01 M2LB-language.zip is its own project; identity = oracle | PASS |
| FA-02 discovery lists every oracle OpenAPI document once (12), all in the project | PASS |
| FA-03/04 bulk import of 34 contracts with tests (540) and data (69 operations bound); all owned by the project | PASS |
| FA-05 M2LB.zip **without** a declaration is another project: every contract New, none continues an M2LB-language contract | PASS |
| FA-06 listing imports nothing | PASS |
| FA-07 same-repository association declared (explicit) | PASS |
| FA-08 joined project: the 9 byte-identical OpenAPI documents are Up to date | PASS |
| FA-09 `Autorisasjon/` → `AutorisasjonService/`: MovedChanged, candidate with 10 of 11 operations shared (oracle 10/13) | PASS |
| FA-10 bulk import never imports a review case | PASS |
| FA-11/12 confirmed decision → v2 of the AutorisasjonService contract; both source locations and the decision kept | PASS |
| FA-13 its tests and data bindings kept (pinned to v1) | PASS |
| FA-14 no source location belongs to two contracts | PASS |
| FA-15 Integration Suite generated with the project's existing contracts, no new contract | PASS |
| FA-16/17 bulk binding preview for 34 contracts (24 applicable, 2 review, 8 blocked by declared security vs None); 24 applied; second preview Unchanged | PASS |
| FA-18 project coverage: 304/324 operations with tests; executed 0/324 → NOT ASSESSED | PASS |
| FA-19 chain suggestions over all real contracts (1 suggestion), nothing sent | PASS |

Joined-project classification of the M2LB.zip snapshot: 16 SameContract, 3 NewVersion, 5 MovedUnchanged (recorded locations), 2 MovedChanged,
1 PossibleMatch, 7 Ambiguous. The Ambiguous ones are GraphQL schemas whose identical text exists in several files of M2LB-language (`autodoc/` and
`tests/.../schema.snapshot.graphql`): correctly left for a person. Not claimed: runtime verification of any M2LB service (no endpoint was called).

## Synthetic projects

FS-01 identities by root solution (`proja`, `projb`) and contracts = fixture oracle · FS-02 same path, identical content → two contracts ·
FS-03 explicit import into another project's contract refused/never mixed · FS-04 v2 classification = fixture oracle (Changed / Moved /
NeedsReview / UpToDate) · FS-05 Orders → v2 of the same contract; Pets moved → recorded, no new version · FS-06 Store/ offers exactly Shop ·
FS-07 chain suggestions = fixture oracle (no DELETE) · FS-08 nothing sent · FS-09 project bound to the local environment — **all PASS**.

## Browser (Release build; 1440 / 1280 / 1024 / 768 / 390 px)

| Check | Result |
|---|---|
| BE-01/02 active environment shown; read-only test Ready | PASS |
| BE-03 Run selected → exactly one `GET /pets` in the independent ledger | PASS |
| BE-04 Execution Results shows request `/pets`, response 200, assertion passed | PASS |
| BE-05…BE-09 POST test Ready; "select every ready" leaves it out; warning; **without confirmation nothing sent** (the page says why); with confirmation exactly one `POST /orders`, never resent | PASS |
| BE-10 the POST ran a v1 test: coverage counts it for v1 (1/3) and **NOT ASSESSED** for v2 | PASS |
| BE-11/12 project coverage dashboard; module drill-down | PASS |
| BE-13…15 bulk configuration: preview, Apply disabled until the environment is confirmed, per-row results | PASS |
| BE-16/17 identity review row (evidence, not bulk-selectable) and recorded confirmation | PASS |
| BE-18…20 chain suggestions → Draft (Run disabled, nothing sent) → Approve | PASS |
| BE-21/22 no Target Environment: explained, Run disabled; Configure explains instead of a form | PASS |
| BE-L1…L4 no page-level horizontal scroll (project coverage, configure, import, chains) at all five widths | PASS |
| BE-Z1 / BR-Z1 no external request, no page error, no console error | PASS |
| BR-01…03 after restart: historical run reopens (`/orders`, 201); coverage persists; assigned contract has no identity banner | PASS |

Independent ledger over the whole browser run: exactly `GET /pets` and `POST /orders`.

## Legacy identity and restart (real Postgres)

FR-01 every legacy contract of one project (group) regains its project · FR-02 a contract with versions of unrelated projects → NeedsReview ·
FR-03 joined M2LB project resolved through the association · FR-04 tests/bindings checksum unchanged · FR-05 imports touching the unresolved
contract never create a second one · FR-06 project assignment recorded · FR-07 the Integration Suite resolves its contracts after restart ·
FR-08 coverage counts only exact-version evidence (Pets executed 1; Orders' v1 run is history) — **all PASS**.

## Migration acceptance (`birknext_cthm_20261110`, synthetic legacy rows)

MG-01 new columns (3) and decision table · MG-02 legacy rows byte-unchanged by the migration (checksum) · MG-03 no orphan versions/tests/bindings ·
MG-04 identity empty before backfill · MG-05 after backfill: resolvable → `legacyproj`, missing snapshot → `NeedsReview`, upload → `Unscoped` ·
MG-06 version project keys only where the snapshot exists · MG-07 rows unchanged after backfill — **all PASS**. The migration was applied only to
this disposable database (explicit `--connection`).

## Negative cases

| Case | Result |
|---|---|
| Missing environment · mutating without confirmation · missing Test Data (not ready, named) · missing credential (bulk preview blocks, unit-tested) | PASS |
| Unauthorized project | NOT SUPPORTED — BirkNext has no project membership model; isolation is by identity/query scope |
| Wrong permission, expired attended session, target timeout, unknown outcome, partial and cancelled runs | NOT RUN in this acceptance (existing Execution Evidence tests cover the run-service behaviour; not re-verified in the browser here) |

## Defect found and fixed

| Id | Round | Defect | Fix |
|---|---|---|---|
| CTH-D1 | 2 (BR-03) | the contract header sliced the fingerprint to 12 characters: a version with a shorter (legacy) fingerprint crashed rendering | guarded; bUnit regression test (`770c8c99`) |

## Regression

See the gap register.
