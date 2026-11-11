# BirkNext — User documentation & Help Center: acceptance (2026-11-11)

- **Branch and baseline:** `008-traceability-first`, baseline `4c424ca6`.
- **Systems used:** a disposable database (`birknext_hc_20261111`). No external system was contacted.

Statuses: PASS · FAIL · PARTIAL · BLOCKED · NOT RUN · NOT SUPPORTED.

## 1. Automated tests

| Suite | Result |
|---|---|
| `HelpCenterTests` (9) | PASS |
| Focused frontend: HelpCenter, UserGuide, DocumentationCatalog (structural guard: every feature documented, real routes, anchors, no broken links in guide sources), UxConsistency, NavMenu, ExecutionResults, ApplicationBuildCard, WorkspaceTransfer, ContractTesting, IntegrationSuites, CriticalE2E | 316/316 PASS |
| First focused run | 2 failures, both fixed properly: (1) the guard "no navigation group above 10 topics" failed because the new topics grew Testing and Reference, so two workflow groups were added; (2) a phrase query ranked a scattered-word match first, so an exact-phrase bonus was added to search |

## 2. Browser acceptance (Microsoft Edge, Release frontend, real local API, every non-localhost request aborted)

**Main phase: 26/26 PASS.**

| ID | Check | Result |
|---|---|---|
| HC-01 | Help Center opens: title, search, quick links, user guides by default, category cards, common tasks | PASS |
| HC-01b | Browsing the guide made no run or execution call | PASS |
| HC-02 | Search "historical" finds Status meanings and Versions, with highlighted excerpts | PASS |
| HC-03/03b | Selecting a result lands on the article; it shows its breadcrumb, verification and feature links | PASS |
| HC-04/05 | Related guide link and in-article sub-section link land correctly | PASS |
| HC-06/07 | Contextual help on Test Execution & Evidence opens the article; Back returns to the feature | PASS |
| HC-08 | Contextual help on Contract Testing, Integration Test Suites, Critical E2E Regression and Backup & Transfer points to the right article | PASS |
| HC-09 | Troubleshooting opens and lists the new entries | PASS |
| HC-10 | Searching a status ("Not assessed") finds Status meanings | PASS |
| HC-11/12/12b | Keyboard: type + Enter opens the best match; the audience buttons work with Space; focus is visible | PASS |
| HC-13 | All 56 guide anchor links resolve | PASS |
| HC-14 | All 70 distinct feature routes linked from the Help Center open a real page (none shows "nothing at this address") | PASS |
| HC-15/16/17 | Hostile and invalid deep links, a script/img injection query and a 5,000-character query are inert or bounded; no dialog | PASS |
| HC-L1 | No page-level horizontal scroll at 1440 / 1280 / 1024 / 768 / 390 px (landing and a long article) | PASS |
| HC-L2 | At 390 px the contents collapse behind a toggle and search stays usable | PASS |
| HC-Z | No request left localhost; no page error, console error or dialog | PASS |

**After API restart: 3/3 PASS.** HC-20: a deep link opens. HC-21: search works (local index). HC-Z: clean.

**Total: 29/29 PASS.**

## 3. Content acceptance

| Check | Result |
|---|---|
| Every navigation feature has a guide anchor and a real route (structural guard) | PASS |
| Coverage judged against article content: 33 DOCUMENTED, 19 PARTIALLY_DOCUMENTED, 0 MISSING (audit §4) | PARTIAL (UD-G2) |
| No planned-only capability is described as available. What's new says it is not available. The MU → MM risk is not stated as a confirmed incompatibility. Suggested-test counts are kept qualitative. | PASS |
| No secrets or real protected data in examples. The examples are synthetic names and versions (`children-api@v3`, `1.0.0-ci.152`). | PASS |
| Only recorded verification is claimed (9 articles); the other articles say it is not recorded | PASS |
| Workflow acceptance A–H through the documentation | PARTIAL (UD-G7) |

## 4. Full regression

Run sequentially with the existing memory guards; no timeout changed, no test disabled.

| Step | Result |
|---|---|
| Backend partitions 1–6 | PENDING: recorded in the follow-up update |
| Frontend full suite | PENDING |
| Release frontend publish | PASS (used for the browser acceptance) |
