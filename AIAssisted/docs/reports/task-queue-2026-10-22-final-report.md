# Final report: tasks 14–21 (branch `008-traceability-first`)

All eight tasks are implemented and committed locally; nothing was pushed. Every task passed its full regression, with two one-off flaky tests that passed on rerun and are described under Regression. Where the work only partly covers the spec, the YES/NO section marks it **Partial**.

## A. Starting state and commits

- **Branch:** `008-traceability-first`. The queue started at `e0ba8562`, the Task 13 commit. Each task's starting HEAD is the previous task's commit.
- **Pre-existing dirty files:** `appsettings.Local.json` (your change) and untracked `AIAssisted/testdata/`. Both were left untouched and excluded from every commit.

| Task | Commit | Subject |
|---|---|---|
| 14 | `74fd1a93` | refactor(pipelines): add shared delivery evidence provider model and consumers |
| 15 | `72addc72` | refactor(tests): add shared test evidence model and canonical test identity |
| 16 | `4553cc1a` | refactor(quality): add shared quality evidence provider model and consumers |
| 17 | `dc59c0bc` | refactor(data-model): add shared data model evidence provider model and consumers |
| 18 | `65f2b84b` | feat(test-data): add shared reusable test data lifecycle, candidates and consumers |
| 19 | `c43392d8` | refactor(skolenaervaer): orchestrate journeys over shared evidence with canonical readiness dimensions |
| 20 | `11bc6610` | refactor(security-classification): share graded-access evidence with M01 and Skolenærvær consumers |
| 21 | `248ced34` | refactor(security-classification): rename review to M01 graded access |

## B. Regression

Each run covered backend and frontend in Debug and Release, with EnvironmentDependent run separately.

| Task | Backend Debug | Backend Release | EnvDependent | Frontend Debug / Release |
|---|---|---|---|---|
| 14 | 4902 ✓ (13 skipped) | 4901 ✓, 1 ✗ (flake, see below) | 3 ✓ | 6540 / 6540 ✓ |
| 15 | 4908 ✓ | 4908 ✓ | 3 ✓ | 6541 / 6541 ✓ |
| 16 | 4914 ✓ | 4914 ✓ | 3 ✓ | 6543 / 6543 ✓ |
| 17 | 4918 ✓ | 4917 ✓, 1 ✗ (flake, see below) | 3 ✓ | 6544 / 6544 ✓ |
| 18 | 4925 ✓ | 4925 ✓ | 3 ✓ | 6547 / 6547 ✓ |
| 19 | 4937 ✓ | 4937 ✓ | 3 ✓ | 6549 / 6549 ✓ |
| 20 | 4944 ✓ | 4944 ✓ | 3 ✓ | 6552 / 6552 ✓ |
| 21 | 4944 ✓ | 4944 ✓ | 3 ✓ | 6552 / 6552 ✓ |

The two failures are both chance-based and unrelated to the tasks. Neither test was changed.
- **Task 14:** `LocalDataResetTests.Reset_ClearsBackendStateOutsideTheDatabase…` passed 3/3 when rerun on its own and on a full rerun.
- **Task 17:** `LocalHttpsProxyServiceTests.BearerOnSuccessful…` checks that a log line does not contain "abc", and a random GUID in the log contained it. It passed 3/3 when rerun on its own and in every later run.

Before each commit I confirmed the committed files were identical in the main repo, a saved copy, and the worktree the regression ran in.

## C. Acceptance, responsive and accessibility

All checks below ran against the isolated stack (local API on :5098, published frontend on :5198) with the real M2LB snapshot. Each run also checked for no page errors, no external requests and no writes to the API.

| Task | Result | What was checked |
|---|---|---|
| 14 | 55/55 | Delivery panels in API/Frontend/Integration Quality Review, Target Environment and Environment Analysis; deployments shown as "defined", never "deployed" |
| 15 | 20/20 | Source Analysis Tests domain and Test Coverage before a run |
| 16 | 19/19 | Shared quality evidence on Environment Analysis, API Quality Review and Performance |
| 17 | 17/17 | M2LB: 100 tables, 34 explicit mappings, 13 by convention, 28 migrations, 69 nullability differences |
| 18 | 36/36 | 200 candidates, all in Candidate state (121 negative, 33 identity placeholders); unknown snapshot → 404; catalog unchanged |
| 19 | 39/39 | Ten dimensions per journey, matrix, attention by owner, history unchanged |
| 20 | 25/25 | Consumers M01 and Skolenærvær share one run; Skolenærvær Security tab has no form; no review run |
| 21 | 21/21 | Exact heading and title, old route works, sidebar not clipped, Skolenærvær link opens the renamed page |

- **Responsive:** no page overflow at 1440, 1100, 768 and 390 px. Tasks 18–21 also checked 2560 px.
- **Accessibility:** axe found 0 violations at 1440 and 390 on every changed page.
- **Write check exceptions:** a few existing POST calls that only read data were excluded from the no-writes check. These were the authenticated-review capabilities and companion/engine status calls, plus the topology `…/paths` calculation.

## D. Diff audit

Every task held to the shared constraints:
- No changes to Critical E2E, `appsettings.Local.json` or `testdata/skolenaervaer.json`.
- No calls to TT02, Altinn or Azure.
- No mutation of source snapshots or IQR bindings.
- No auto-confirmed mappings and no newest-snapshot selection.
- No secrets, tokens or cookies stored, and no Kode 6/7 identities generated.
- No change to runtime security behavior.

Task 21 is naming and copy only, plus one opt-in label-wrap CSS rule.

## E. What each task delivered, and known gaps

- **14 Pipeline & Delivery Evidence.**
  - One delivery evidence model with providers, consumed by Pipeline Review, Source Analysis CI/CD, API/Frontend/Integration Quality Review, Performance, Target Environment, Environment Analysis, Contracts, Impact and Test Coverage.
  - Configured, executed and passed are kept apart, and so are defined and deployed. Pipeline environment labels stay mapping candidates until someone maps them.
- **15 Test Evidence.**
  - One canonical test identity.
  - Defects found and fixed in Test Coverage:
    - TRX execution results never matched discovered tests.
    - The first result won instead of the latest.
    - Tests that only appear in execution results were dropped.
    - Inferred references were treated as links (M2LB: 743 candidates against 26 explicit links).
  - **Gap:** IQR still uses its own scan of developer tests.
- **16 Quality Evidence.**
  - Shared quality evidence items, with currency and de-duplication, in API Quality Review, Frontend Quality Review and Environment Analysis.
  - **Gaps:** Frontend Quality Review still fetches the document about 7 times per pass. API Quality Review still parses the runtime OpenAPI itself. Impact Analysis has no quality-evidence UI.
- **17 Data Model Evidence.**
  - Entities are identified per database (`db/table`). Explicit table mappings are kept apart from naming conventions, and nullability is shown per layer.
  - Migrations are declarations; a model change without a migration is shown as a potential migration gap.
  - Consumers: Source Analysis Data, Data Model Explorer, Impact, Technology Coverage.
  - **Gaps:** IQR still uses its own database-model scan. The Data Model Explorer panel appears only when the workspace has a data model document.
- **18 Shared Test Data.**
  - Candidates come from contract and data-model evidence of one snapshot the tester chooses. Identity fields get a controlled placeholder and are never generated.
  - Readiness never reuses another environment's asset. Impact marks affected assets for review without changing them.
  - Runs record alias and version, and documentation shows metadata only.
  - Consumers: the Test Data page, API/Integration Quality Review, Test Coverage, Impact and Technology Coverage.
- **19 Skolenærvær orchestration.**
  - A generic composer gives each journey ten separate readiness dimensions, each with an owner link.
  - The Overview has a matrix and attention grouped by owner.
  - Runs record the source snapshots and Test Data versions they started with.
  - An instance referenced by several runs stays unresolved. MM and Skoletjenesten disagreeing gives "Review required".
  - Impact Analysis names affected journeys.
  - **Rejected:** a "Partial" verification verdict. Existing tests deliberately use "Not evaluated" for partly proven evidence.
- **20 Security Classification as a shared capability.**
  - One stored review is turned into a generic assessment with seven requirements. M01 and Skolenærvær each get their own assessment, and Skolenærvær adds its own rules for protected downstream handling and Bufdata exclusion.
  - Runtime results apply only to the surfaces tested. Results recorded under another test context show as "Not current". Source and runtime disagreeing gives "Review required".
  - New: endpoint `GET api/security-classification/consumers` and a Consumers section on the review page.
  - Skolenærvær gets a Security tab with no source selection or test context of its own.
  - Impact names consumers when a classification rule changes, and Technology Coverage has a provider row.
  - **Gap:** Skolenærvær journey readiness does not yet include the security assessment.
- **21 Rename.**
  - The sidebar shows the full label, which now wraps instead of being cut off with "…". The tooltip keeps "formerly Security Classification" and the Kode 6/7 terms, because there is no search-alias feature.
  - Also renamed: heading, browser title, description, links, export, User Guide, documentation catalog, Technology Coverage label and two docs.
  - Kept: route `/security-classification-review`, API ids, enums, evidence models and stored data.
  - Skolenærvær keeps its own "Security / Gradert tilgang" heading. Within Skolenærvær, M01 appears only in the link to the full review. M2LB is kept where it names the actual application.

## F. Required YES/NO answers

Each line lists numbers by answer. The NO answers the specs require are all NO.

**Task 14 (41 questions)**
- YES: 1–30, 36–40.
- NO: 31, 32, 33, 34, 35, 41.
- Note on 29: there is no Azure DevOps runtime enrichment yet, so it can't leak into source parsing.

**Task 15 (43)**
- YES: 1–8, 10–21, 24–29, 32–42.
- NO: 9, 22, 23, 43.
- Partial: 30 (IQR uses its own developer-test scan) and 31 (linked only where it makes sense, through quality evidence).

**Task 16 (40)**
- YES: 1–9, 12–27, 29–39.
- NO: 40.
- Partial: 10 (Frontend Quality Review repeats fetches), 11 (API Quality Review parses the runtime OpenAPI), 28 (no Impact UI) and 35 (same causes as 10 and 11).

**Task 17 (40)**
- YES: 1–7, 9–12, 14–18, 20, 21, 25, 26, 28–34, 36–39.
- NO: 8, 13, 19, 40.
- Partial:
  - 22: tables only; no field or migration links.
  - 23: IQR has its own database-model scan.
  - 24: not wired into API Quality Review.
  - 27: not wired into traceability.
  - 35: IQR.

**Task 18 (44)**
- YES: 1–4, 6, 8–15, 17–23, 27, 28, 30, 31, 33–43.
- NO: 24, 25, 26, 44.
- Partial:
  - 5: Schema Studio registers through the catalog already; no new flow was added.
  - 7: linked through aliases and requirements, not a dedicated link field.
  - 16: only API schemas that appear in contract evidence.
  - 29: structural candidates only; no OpenAPI example candidates.
  - 32: candidates appear on the Test Data page, not in Source Analysis.

**Task 19 (43)**
- YES: 1–5, 7–17, 19–27, 29–42.
- NO: 43.
- Partial: 6 (delivery evidence is an optional dimension, from bound sources only), 18 (three providers; no new Service Bus or Event Hub verify providers) and 28 (Test Coverage reads requirement links to the journey or scenario only).

**Task 20 (42)**
- YES: 1–14, 19, 23–25, 27, 29–32, 34–41.
- NO: 20, 21, 42.
- Partial:
  - 15–18: the shared assessment reads the stored review rather than calling the data-model, contract, infrastructure and quality evidence models directly.
  - 22: identities are environment-scoped only when the Test Data asset declares an environment.
  - 26: the assessment reads the review's GraphQL observations only; the API and browser authorization checks are not wired into it.
  - 28: authorization tests are not recorded as Test Evidence.
  - 33: the security assessment is a separate area, not a journey dimension.

**Task 21:** this spec has no YES/NO list, and all 14 commit-gate items are met. Sidebar ✓ · heading ✓ · browser title ✓ · links ✓ · docs ✓ · generic internals unchanged ✓ · persisted ids unchanged ✓ · old route works ✓ · Skolenærvær link works ✓ · no security behavior change ✓ · responsive ✓ · axe ✓ · regression ✓ · not pushed ✓.
