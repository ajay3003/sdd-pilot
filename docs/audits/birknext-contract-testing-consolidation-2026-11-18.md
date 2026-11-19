# Contract Testing — audit, consolidation and import redesign (2026-11-18)

Branch `008-traceability-first`, starting HEAD `230cecc0`. Local commits only; nothing pushed. Isolated Release instance (API :5097,
web :5197), disposable databases and synthetic contracts; no external system contacted, no imported code executed, no API called.
`appsettings.Local.json`, `AIAssisted/testdata/`, the Kode67 gap register and the unrelated audit drafts were not modified.

## 1. The problem (user screenshot)

`/api-quality-review/contracts` opened on a long import list: a project selector on *All projects*, a Target Environment panel with
attended-session controls above the work area, "35 contract files from 11 logical API groups", repeated GraphQL schemas with overlapping
operations, several GraphQL files with zero root operations, an *Import only this one* link on every row and a pre-ticked
**Generate tests and synthetic test data right away** next to *Import selected*.

## 2. Architecture as found

| Part | Implementation | Owner of |
|---|---|---|
| Page | `Pages/ContractTesting.razor` (≈1 800 lines): project selector, workspace modes Import / Configure environment / Project coverage, contract list (left), contract detail (versions, endpoint binding, Operations · Tests · Chains · Changes · Coverage · Lifecycle) | presentation; state in the URL |
| API | `ContractTestingController` (`api/contract-testing/…`) | — |
| Contract registry | `ContractRegistryService` (+ `.Source.cs`): immutable versions (`ApiContracts`, `ApiContractVersions`), project scoping by repository identity, identity classification of source files (same contract, new version, moved, possible match, ambiguous), identity decisions, retention | **the** contract inventory |
| Source discovery | `SourceCandidatesAsync`: OpenAPI and GraphQL schema contracts of each project's analysis-baseline snapshot (Source Analysis contract evidence), one row per source contract, registry state, retained document text (`SourceContractDocuments`, SHA-256) | reads Source Analysis; owns nothing new |
| Bulk import | `ContractBulkImportService`: per item the one registry import, then (when asked) the one test generator and the one test-data generator | orchestration only |
| Tests | `ContractTestService` (draft definitions, revisions, review states Draft → Reviewed → Approved → Retired, impact, migration proposals) | contract test definitions |
| Test Data | `ContractTestDataService` → the shared Test Data registry (datasets pinned by version) | uses Test Data; no second registry |
| Execution | runs through Execution Evidence (`ExecutionRunService`), safety policy and attended-session gates; results read back from Execution Evidence | execution history |
| Coverage | `ContractProjectCoverageService`: Definition (tests, negatives, data) kept apart from Execution and Verification; never from discovery or import | contract coverage |

Identity: a contract is `{key}@v{version}`; an operation `METHOD /path` (REST) or `query|mutation name` (GraphQL) inside one contract version;
a test revision `contract-test:{id}@r{n}`. Source-backed versions record the snapshot id and the source path.

## 3. Overlap analysis

| Concern | Contract Testing | API Quality Review | Manual Testing (Plan & prepare) | Integration | Test Data | Coverage |
|---|---|---|---|---|---|---|
| Contract inventory | **owner** (registry) | reads its endpoints from Endpoint Discovery, not from the registry | reads Source Analysis contract evidence as scenario candidates (no copy) | Integration Test Suites read the registry / source snapshot | — | — |
| Test generation | **owner** (contract tests) | own runtime checks (not contract tests) | manual test cases, never contract tests | suites compose contract tests | — | — |
| Synthetic data | generates into the shared Test Data registry | — | — | — | **owner** | — |
| Execution | through Execution Evidence | own runs, also Execution Evidence | manual runs | through Execution Evidence | — | reads |
| Coverage | per contract version (definition vs execution) | — | — | — | — | Test Coverage reads executions only |

No competing registry or generator was found: API Quality Review checks live endpoints, Manual Testing proposes human cases from the same source
evidence, Integration Test Suites reuse contract tests. The overlap was in **identity and navigation** (no link from a manual API candidate to
its contract file/operation) and in the **import UX** (generation implied by import).

## 4. Findings and changes

| # | Finding | Change |
|---|---|---|
| F1 | *Generate tests and synthetic test data right away* was pre-ticked, and `SourceContractBulkImportRequest.GenerateTests/GenerateTestData` defaulted to **true** (an omitted field generated) | Defaults are **false**; the page has no remembered checkbox. *Import selected* opens a preview with **Import N contracts** (import only, primary) and a separate **Import and generate tests and synthetic data**; after an import-only, **Generate tests and synthetic data for these N** generates for exactly those contracts (the generator never duplicates existing tests) |
| F2 | No import preview | Preview: new / changed (next version) / moved, logical API groups, operations, projects, GraphQL files without root operations, byte-identical copies, structural-validation-only files, and what generation means (count known only after generation) |
| F3 | Bulk import across projects was neither confirmed nor validated | `SourceContractBulkImportRequest.ProjectKey` / `ConfirmMultipleProjects`; `ContractBulkImportService.RefusalAsync` checks each item's **own snapshot project** on the server: with a project, a foreign file refuses the whole import; without one, several projects need the explicit confirmation (422 otherwise) |
| F4 | Contract files and operations were mixed in one sentence; copies of a schema inflated operation counts | Summary strip: contract files, already imported, new or changed, REST, GraphQL (with files without root operations), **distinct operations** vs operation entries, generated test definitions (drafts, not executed) |
| F5 | No REST/GraphQL filter; selection silently hidden by filters | Protocol segment (All / REST / GraphQL), state filter (new and changed, already imported, needs identity review, without root operations, archived), search over names, files, modules and operations; *N selected · M hidden by filters*; *Clear selection* |
| F6 | *Import only this one* on every row; no way to inspect a file before import | One row per file (name, protocol, state, role-by-location, path, operations, identical-copy count) with *Details*: project, source snapshot (+ *Open in Source Analysis*), file and validation depth, import state (*Import this contract only* or *Open the imported contract*), operations, identical copies, related files, fragment explanation |
| F7 | Copies and fragments | Byte-identical copies by retained document **SHA-256** (new `SourceContractCandidate.DocumentSha256`); related files by shared operations (GraphQL per project, REST per service module); a GraphQL file without root operations is explained as a fragment/extension with *possible parent schemas* in the same module (labelled as a location proposal — the analyzer records no "extends" link); no tests are generated from it on its own |
| F8 | An archive with one wrapper folder (`SchoolHub.zip` → `SchoolHub/…`) put every contract in one "module" | `ContractRegistryService.WithoutArchiveFolder`: the archive's own wrapper folder is removed before the service module is taken (exact archive name only) |
| F9 | No operation-level deep link | `api-quality-review/contracts?mode=import&snapshot={id}&file={path}&operation={op}` opens that exact file (snapshot + exact path, never a name match) with its details and the operation highlighted; an unknown file says so. Imported contracts highlight `?contract=…&operation=…` in Operations |
| F10 | Manual Testing API candidates linked to the generic page | Plan & prepare's *Open in Contract Testing* uses the deep link (snapshot, file, operation) |

## 5. Source identities and duplicate schemas (real M2LB, read-only)

Source Analysis found 388 operation entries in 36 contract files; 262 distinct operations (see the scenario-ownership audit). The repeated
GraphQL schemas are four documents per service: the specification contract, `autodoc/schema.graphql` (generated documentation), the frontend
client's copy and the contract-test `schema.snapshot.graphql`. Whether two are *byte-identical* is now decided by the retained document hash;
the rest are *related* (same operations, possibly different), never merged and never silently treated as versions of each other — the registry's
identity rules (same source location → next version; identical document elsewhere → *moved*; otherwise identity review) are unchanged.

## 6. Readiness and execution

Unchanged and verified: import and generation never send a request; generated tests are **Draft** until reviewed; readiness names environment,
authentication, Test Data and approval separately; runs need the explicit *Run selected* with Ready tests and the existing safety policy and
attended-session gates; coverage counts execution evidence of the exact version only.

## 7. Performance

`ContractImportPresentationTests.Large_inventories_filter_summarize_and_preview_quickly` (10, 100, 500, 1 000 files with 40 operations each):
summary, filter, preview and related-file lookup together stay under the 3 s ceiling (they are linear passes over the in-memory list; no backend
call per filter or row). The page renders groups inside `<details>` (closed above 40 files) and builds a file's details only when opened. The
operation names added to each candidate are capped at 1 000 per file.

## 8. Tests

On final code:

| Run | Passed | Failed | Skipped |
|---|---|---|---|
| Backend `ContractTesting` + `TestPlanning` + source-evidence scan (incl. new: import without generation request creates no tests and later generation is idempotent; server refuses mixed / foreign projects; candidates carry document hash and operations and group without the archive folder) | 244 | 0 | 0 |
| Frontend Contract Testing page, `ContractImportPresentationTests`, `ContractSourceVariants`, Plan & prepare, User Guide, Documentation | 344 | 0 | 0 |
| API + Web Release publish (tag 62) | exit 0 | — | — |

Changed existing tests (behaviour change, no assertion removed): the bulk-import test now opens the preview and chooses *Import and generate*
explicitly (the old assertion "generated in the same step by default" described the defect); the single-import test opens the file's details
(the per-row *Import only this one* link moved there); the backend helper asks for generation explicitly.

**Full frontend suite and backend partitions 1–6 on this code: not yet run** (they follow in the next regression round, sequentially).

## 9. Browser acceptance

Fresh Release publish (tag 62; web assets 86/86 match their hashes), disposable database `birknext_ct_20261119` (template copy that also holds
an earlier M2LB import), API :5097, web :5197, repository network guard. Synthetic **SchoolHub** archive (three byte-identical copies of one
GraphQL schema — service docs, contract-test snapshot, frontend client — a schema extension without root operations, SchoolService and
PersonService OpenAPI documents) and the unrelated REST-only `tps-api` archive. Scripts `acc/ct-accept.cjs` and `acc/ct-accept2.cjs`.

**Result: 33 of 33 checks passed** (first run 22/22 up to step 21, which stopped on a script issue — the workspace project was the last
imported archive, so Plan & prepare showed that project; the continuation selects SchoolHub with the shared selector and ran steps 21–24, 11/11).

| Step | Check | Result |
|---|---|---|
| 1–3 | Technical Testing → Contract Testing; choosing SchoolHub narrows 42 files to its 6 | pass |
| 4 | 6 files = 2 REST + 4 GraphQL; 12 distinct operations vs 24 operation entries; grouped by AccessService, Frontend, PersonService, SchoolService (not by the archive folder); no generate checkbox | pass |
| 5–7 | GraphQL filter; the main schema lists its 6 operations before import; its two byte-identical copies are named; the details link the exact Source Analysis snapshot | pass |
| 8–9 | The extension is explained as a fragment/extension (no endpoint tests) with its possible parent schema as a labelled proposal | pass |
| 10–11 | Selection survives a filter (*3 selected · 2 hidden by filters*); the preview names 3 new, 2 logical API groups, the project and the file without root operations | pass |
| 12–15 | *Import 3 contracts*: imported, **0 tests**; after reload still 3 contracts and 0 tests | pass |
| 16–17 | Generation as a separate step (*Generate tests and synthetic data for it*): 3 draft tests | pass |
| 18–19 | Re-importing the 4 imported files: all *Unchanged*; still 4 contracts and 3 tests | pass |
| S1 | The server refuses a bulk import naming SchoolHub with another project's file: 422 "belong to another project (M2LB); nothing was imported" | pass |
| 20 | Deep link `?mode=import&snapshot=&file=&operation=mutation lockPeriod` opens that file with the operation highlighted; an unknown file says so | pass |
| 21–22 | Plan & prepare's API candidate links to `…&file=SchoolHub/AccessService/autodoc/schema.graphql&operation=mutation activateEmergencyAccess`; Contract Testing opens it highlighted; Back returns to the technical view | pass |
| 23 | Generated tests are drafts, nothing run | pass |
| — | 1440 / 1280 / 1024 / 768 / 390 px without horizontal scroll; no console or page errors; no external request | pass |

Before / after (same SchoolHub data and API; "before" is the previous web build, tag 61): `contract-testing-2026-11-18/before-1440-import.png`,
`after-1440-import.png`, `before-390-import.png`, `after-390-import.png`, `ct-after-1440-preview.png`.

## 10. Remaining limitations

* A GraphQL extension's parent is a proposal from location and protocol; the analyzer records no `extend type` → schema link.
* Contracts imported earlier from a wrapper-folder archive keep their stored module (from the stored source path); only the import list groups
  without the wrapper.
* The operations listed before import are names only (the full operation detail — parameters, schemas, security — appears after import).
* API Quality Review still reads its own Endpoint Discovery targets; it does not consume the contract registry.

## 11. Local commits

One local commit for the implementation, tests, User Guide and this document (starting HEAD `230cecc0`); a follow-up commit records the browser acceptance and full regression. Nothing pushed.
