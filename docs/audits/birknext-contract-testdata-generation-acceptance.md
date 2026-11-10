# BirkNext — Automatic contract test data generation: acceptance (2026-11-10)

Branch `008-traceability-first`. Baseline `d2746018`; verified source = `0ed74d1f` + the acceptance fixes (committed with these reports).

**Result: 27/27 PASS** on a clean isolated stack (API 17/17 · after restart 2/2 · browser 8/8), after two rounds that found and
fixed TDG-7…TDG-10 (round 1: 24 PASS / 3 FAIL; round 2: 26 PASS / 1 FAIL).

## Setup (scratch only)

Real BirkNext API (scratch tree = HEAD + this change) on :5098 with a fresh database `birknext_tda_20261109`, no `appsettings.Local.json`,
outbound proxies pointed at a dead port; published Release frontend on :5198; Edge headless with every non-localhost request blocked; loopback
target :5099 recording every request (independent ledger). The running stack on 5000/5173 was not used. Archive `AIAssisted/testdata/M2LB.zip`
(SHA-256 `22339207…f0`, read-only) through the real Project Import. Independent oracle `tda/oracle.py` (stdlib, ZIP in memory): OpenAPI
operations of every module.

## Real M2LB archive

| Check | Result |
|---|---|
| TD-01 archive identity = oracle | PASS |
| TD-02 the snapshot offers all 12 OpenAPI documents of the oracle | PASS |
| TD-03 all 35 source contracts (12 OpenAPI, 23 GraphQL files) imported **with their retained document text** (full schema validation) | PASS |
| TD-04 every oracle OpenAPI operation has a positive test (record level, per file) | PASS |
| TD-05 test-data generation answered for every contract | PASS |
| TD-06 data generated **and bound automatically** for operations whose values the contract defines | PASS — 68 operations |
| TD-07 what cannot be generated is named with reasons (needs review / unresolved) | PASS — 186 + 7 |
| TD-08 no identity-shaped generated value | PASS — 0 of 741 values |
| TD-09 regeneration idempotent (bound stays bound, nothing re-created) | PASS |
| TD-10 Autorisasjon inventory = oracle (11 operations; positive tests = oracle operations) | PASS |

Totals over 322 operations: **68 Bound** (validated Valid, approved, pinned — no manual entry), **186 Needs review**, **7 Unresolved**,
**61 No inputs**; 100 requests validated Valid, 7 not assessable. Per OpenAPI document (oracle operations = positive tests):

| Document | Ops | Bound | Needs review | Unresolved | No inputs |
|---|---|---|---|---|---|
| Autorisasjon/autodoc/openapi.yaml | 11 | 2 | 6 | 0 | 3 |
| Hendelse/autodoc/openapi.yaml | 7 | 1 | 2 | 0 | 4 |
| HendelseAdapter/autodoc/openapi.yaml | 2 | 0 | 0 | 0 | 2 |
| MeldingsutvekslerReceiver/autodoc/openapi.yaml | 4 | 0 | 0 | 0 | 4 |
| Person/autodoc/openapi.yaml | 10 | 2 | 2 | 3 | 3 |
| PersonAdapter/autodoc/openapi.yaml | 3 | 0 | 0 | 0 | 3 |
| Proxy/autodoc/openapi.yaml | 11 | 4 | 3 | 0 | 4 |
| Revisjon/autodoc/openapi.yaml | 3 | 0 | 0 | 0 | 3 |
| SkoleService/autodoc/openapi.yaml | 17 | 4 | 8 | 0 | 5 |
| SkoleService/…/rapportinntak.openapi.yaml | 1 | 0 | 1 | 0 | 0 |
| SkoleService/…/utdanning-innmating.openapi.yaml | 8 | 3 | 4 | 0 | 1 |
| Tjeneste/autodoc/openapi.yaml | 4 | 3 | 1 | 0 | 0 |

"Needs review" is mostly record identifiers (path parameters, `…Id` fields, GraphQL `ID`) — values that must exist in the target, which the
generator never invents. "Unresolved" are identity-shaped fields (Person). Generation never claims runtime availability of these services.

## Synthetic contract against the loopback ledger

| Check | Result |
|---|---|
| TS-01 contract uploaded | PASS |
| TS-02 `POST /orders` (allOf, $ref, oneOf, const, enum, bounds, pattern, arrays) generated, Valid, approved, bound | PASS |
| TS-03 the positive test's Test Data readiness is Ok without manual entry | PASS |
| TS-04 generation sent nothing | PASS |
| TS-05 authorised run started | PASS |
| TS-06 **ledger: exactly one `POST /orders` whose body equals the dataset value** | PASS |
| TS-07 JSON types preserved on the wire (`quantity` is a number) | PASS |

## After restart

TR-01 regeneration creates nothing and keeps bindings · TR-02 the synthetic test keeps its pinned set — PASS.

## Browser (Contract Testing, M2LB Person API)

TB-01 every operation with inputs listed with its state · TB-02 pinned values visible with provenance without regenerating · TB-03 only what
needs a human is expanded, with the reason · TB-04 counts in the message · TB-05/06/07 no page-level horizontal scroll at 1440 / 820 / 390 px ·
TB-Z1 no external request, no page error, no console error — PASS.

## Defects found by acceptance (fixed)

| Id | Round | Defect | Fix |
|---|---|---|---|
| TDG-7 | 1 (TB-01: 2 operations Failed) | a field with pattern `^\d{11}$` received an identity-shaped value; Test Data refused the set | 11-digit values never generated (Unresolved), red-first unit test |
| TDG-8 | 1 | generator credential names narrower than Test Data's | aligned rule |
| TDG-9 | 1 | the refusal did not name the failing check | the failing Test Data checks are reported |
| UI-TD-1 | 1 (TB-07) | 390 px: import form controls widened the page | form controls/headings contained |
| TDG-10 | 2 (TB-Z1) | `GET credentials/{env}` 404 for "none configured" → console error on every visit | 204 No Content (client already treated both as "none") |

## Regression

Backend partitions 1–5: 1,031 + 1,097 (+1 pre-existing Altinn flake, 5/5 on re-run) + 830 + 652 + 520 passed. Partition 6 (re-run
2026-11-10): 2,208 passed, 8 skipped, 0 failed. Frontend full: 8,531/8,531. Details in the gap register.
