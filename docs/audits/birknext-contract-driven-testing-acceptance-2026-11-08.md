# BirkNext — Contract-Driven API & GraphQL Testing: acceptance (2026-11-08)

Implementation: `docs/hardening/birknext-contract-driven-testing-implementation-2026-11-08.md`. Baseline: `docs/audits/birknext-contract-driven-testing-audit-2026-11-08.md`.

## 1. Environment (isolated, local, synthetic)

| Item | Value |
|---|---|
| API | Release publish of the committed code, `http://localhost:5099`, its own content root (no `appsettings.Local.json`, own `App_Data`), its own PostgreSQL database `birknext_cdt_20261108` (created for this task, migrated on start), outbound HTTP(S) proxy pointed at a dead local port |
| Web | Release publish, `http://localhost:5199`; the browser routes the default backend port to 5099 |
| Target Environment | `Local synthetic` (type Local, `http://localhost:5288/`) set in the browser's own storage |
| API under test | Synthetic Node server `localhost:5288`: REST under `/api` (children GET/POST, `/admin/reports` 401), GraphQL at `/api/graphql` (validates required variables, `forbiddenReport` answers HTTP 200 with a FORBIDDEN error), `/openapi.json` (v1), `/schema-v2.graphql`. It records every request (method, path+query, body, correlation id, whether an Authorization header was present): the **independent oracle**. |
| Data | Synthetic only (`Testbarn`, id 7, age 7). Datasets created and approved through the existing Test Data API; everything after that through the UI. |
| Not touched | The user's stack on 5000/5173, `appsettings.Local.json`, `testdata/`, existing databases. No external network calls (0 external requests in every browser phase). |

## 2. REST workflow (`cdtval/accept.cjs`, Edge, Playwright) — 34 / 34, then 4 / 4 after an API restart

| ID | Check | Verdict |
|---|---|---|
| CT-00 | Synthetic datasets created (3), two approved, through the Test Data API | PASS |
| CT-01 | API Quality Review links to Contract tests | PASS |
| CT-02 | Empty registry says so; the active Target Environment is shown | PASS |
| CT-03 | Import by URL creates version 1 with 4 operations | PASS (failed first: CDT-DEF-1) |
| CT-04 | Explorer: `METHOD /path` identity, inputs, `$ref` response schema (200 Child), security, operationId | PASS |
| CT-05 | Version facts: from URL, JSON, fully read, fingerprint | PASS |
| CT-06 | Re-importing the identical document stores nothing | PASS |
| CT-07 | A cloud-metadata URL is refused before any request | PASS |
| CT-08 | 6 drafts: positive per operation, negative where a required query/body value can be omitted | PASS |
| CT-09 | Before Test Data: POST Not ready with "Missing: name, age"; secured `/admin` Blocked | PASS |
| CT-10 | Import, generation and opening pages sent nothing to the API | PASS |
| CT-11 | Picker: compatible approved dataset first; one shows "Missing required: age"; the unapproved one cannot be used | PASS |
| CT-12 | Binding pins the approved version; readiness turns Ready | PASS |
| CT-13 | Five tests Ready, the secured test Blocked | PASS |
| CT-14 | Without confirmation the POST calls are Blocked and never reach the API | PASS |
| CT-15 | Confirmed run: 5 calls, all Passed | PASS |
| CT-16 | Evidence carries `synthetic-children-api@v1#POST /children`, `children-complete@v1`, `contract-test:…@r2`, run contract ref | PASS |
| CT-17 | Oracle: received requests = recorded wire transmissions; `/api` base path; `"age":7` as a number; negative request without `status`; no credentials | PASS |
| CT-18 | The negative test passes on the documented HTTP 400 | PASS |
| CT-19 | Tests show the latest result (Passed HTTP 201) linked to the call | PASS |
| CT-20 | The linked call in Execution Results shows the POST that was sent | PASS |
| CT-21 | YAML upload adds version 2 (3 operations); version 1 unchanged (4) | PASS |
| CT-22 | Changes v1 → v2: removed operation, required parameter, required field, removed field | PASS |
| CT-23 | Impact: `/admin` Broken, GET `/children` Needs update (school), POST Needs update (birthYear), GET `/children/{id}` Review (Child.age) | PASS |
| CT-24 | Tests follow the change (Not ready / Blocked, impact badges) | PASS |
| CT-25 | Regenerate from v2 keeps the binding; birthYear listed missing in readiness and picker | PASS |
| CT-26 | Test Data v2 approved: picker offers `children-complete@v2` as compatible | PASS |
| CT-27 | Pinned to v2 → Ready | PASS |
| CT-28 | A test still pinned to v1 keeps v1 and says v2 is now approved | PASS |
| CT-29 | Keyboard: only the selected view tab is a Tab stop; Enter opens a focused tab | PASS |
| CT-30 | Browsing pages and evidence after the run sent nothing | PASS |
| CT-31 | 390 px: no horizontal page scroll (tests, test detail) | PASS |
| CT-Z1/Z2 | No page or console errors; no external requests | PASS |
| CT-R1 | After an API restart: contract v2, tests and their latest results still there; the run opens with 5 calls | PASS |
| CT-R2 | Opening everything after the restart sent nothing (oracle count unchanged) | PASS |

The REST workflow is verified from imported contract through persisted execution evidence and contract-change impact.

## 3. GraphQL workflow (`cdtval/gql-accept.cjs`) — 23 / 23

| ID | Check | Verdict |
|---|---|---|
| G-00 | Synthetic GraphQL dataset (id, name, age Int, status enum) created and approved | PASS |
| G-01 | SDL upload (named) creates a GraphQL contract, version 1, 4 operations | PASS |
| G-02 | Explorer: `query child` / `mutation createChild`, typed arguments (`ID!`), result types (`Child`, `[Child!]!`) | PASS |
| G-03 | Version facts show the GraphQL format, fully read | PASS |
| G-04 | 6 tests; negatives only for non-null inputs; mutations marked as changing data | PASS |
| G-05 | Document `query Child($id: ID!) { child(id: $id) { id name age status } }`; variables with placeholders | PASS |
| G-06 | Rationale: an HTTP 200 with GraphQL errors fails | PASS |
| G-07 | Picker compatible for every GraphQL test with inputs; pinned `gql-children@v1` | PASS |
| G-08 | All 6 Ready (schemas declare no authentication; anonymous runs) | PASS |
| G-09 | Opening, generating and binding sent nothing | PASS |
| G-10 | Run: 5 Passed (both negatives on GraphQL errors), `forbiddenReport` Failed | PASS |
| G-11 | HTTP 200 with a FORBIDDEN error is recorded and fails the test | PASS |
| G-12 | Negative passes because the server reported the missing `$id` | PASS |
| G-13 | Evidence: `…@v1#mutation createChild`, `gql-children@v1`, operation type mutation | PASS |
| G-14 | Oracle: typed variables (`"age":7`, optional nickname left out), enum `"ACTIVE"`, `/api/graphql`, no credentials, count = wire transmissions | PASS |
| G-15 | The tests list shows `forbiddenReport` Failed HTTP 200, linked to the evidence | PASS |
| G-16 | Execution Results shows the GraphQL error | PASS |
| G-17 | An OpenAPI document cannot become a version of a GraphQL contract | PASS |
| G-18 | Changes (v2 by URL): root field removed, required input field added, enum value removed, result field removed | PASS |
| G-19 | Impact: `forbiddenReport` Broken, `createChild` Needs update, `children` Needs update (enum), `child` Review | PASS |
| G-20 | 390 px: no horizontal scroll on a GraphQL test detail | PASS |
| G-Z1/Z2 | No page or console errors; no external requests | PASS |

Screenshots (synthetic data only, kept in the session scratchpad `cdtval/shots/`): `ct-01-operations`, `ct-02-picker`, `ct-03-tests-after-run`,
`ct-04-evidence`, `ct-05-changes`, `ct-06-tests-390`, `ct-07-detail-390`, `g-01-tests-after-run`, `g-02-forbidden-evidence`, `g-03-changes`,
`g-04-detail-390`.

## 4. Regression on the final code (`10ab13c5`)

Memory-guarded and sequential (1.5 GB floor).

| Suite | Passed | Failed | Skipped | Total |
|---|---|---|---|---|
| Backend partition 1 | 984 | 0 | 2 | 986 |
| Backend partition 2 | 1,096 | 0 | 5 | 1,101 |
| Backend partition 3 | 826 | 0 | 0 | 826 |
| Backend partition 4 | 652 | 0 | 1 | 653 |
| Backend partition 5 | 520 | 0 | 3 | 523 |
| Backend partition 6 | 2,019 | 0 | 8 | 2,027 |
| **Backend total** | **6,097** | **0** | **19** | **6,116** |
| **Frontend (full)** | **8,479** | **0** | **0** | **8,479** |

Focused runs after each stage (also green): contract/AQR/topology/Altinn 959; partition 1 four times; Execution Evidence + Test Data + contract
suites 568 and 548 (+1 skipped); frontend AQR/navigation/Execution Results 499.

## 5. Stated as it happened

- **CDT-DEF-1** was found by the browser run (CT-03 failed: Import stayed disabled after typing a URL). Fixed with a regression test that failed
  first; the acceptance environment was then reset (database recreated, the API's `App_Data/test-data` cleared, mock restarted) and the whole REST
  run repeated: 34 / 34.
- A publish script edit failed silently and the first Release publish went to two earlier scratch folders of this session (`api-gaps`, `webpub`)
  instead of new ones. Only their binaries changed (`App_Data` and settings files kept their timestamps). Not user data; noted for transparency.
- Test Data datasets were created and approved through the existing Test Data API, not through the Test Data page (that page is covered by its own
  earlier acceptance). The picker, binding and everything after them were driven through the new UI.

## 6. Not verified / limits

- Authenticated contract runs (GAP-CDT-1 / GAP-EE-2): only the Blocked case is verified.
- TLS and HTTP/2 targets were not exercised.
- Real OpenAPI documents or GraphQL schemas of real systems were not used (synthetic only; no external calls).
- The remaining unsupported functionality is listed in the implementation report (§ 11, GAP-CDT-1 … 12).
