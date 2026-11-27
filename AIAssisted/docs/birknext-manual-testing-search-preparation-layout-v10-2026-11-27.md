# BirkNext — Manual Testing V10: server search with filters, smaller first load, preparation without a source, three columns (2026-11-27)

Commits (not pushed):
- `b44c98d5`: smaller first load, filtered server search, fetching scenarios by id
- `83619ae6`: preparation without a source
- `7a65dd5b`: three columns
- `0f9ec37a`: stories that are not loaded
- `add3ea3e`: save no longer waits for the translation

Backend stability is in its own report (`birknext-backend-regression-stability-2026-11-26.md`, section «2026-11-27»).

## 1. Server search and filtering; smaller first load

| | Before | Now |
|---|---|---|
| Scenarios sent at once | 10,000 (synthetic 12,000: **15.7 MB**) | **2,000** (same project: **3.2 MB**, −80 %) |
| Server search | words only; filters ignored | words **and** the page's own scenario filters: area, type, user story, requirement, priority, test level. **A filter alone is a search** (choosing an area or a user story finds its scenarios that are not loaded). |
| Filter choices | only values of the loaded scenarios | also the values of the scenarios not loaded (`NotKeptFacets`) |
| User stories with no loaded scenario | not reachable from the story list | listed under «Ikke lastet inn (N)»; choosing one searches its scenarios on the server |
| Scenario not loaded, opened from a link, plan or Testoversikt | Planlegg: «finnes ikke»; Finn og forstå: reported missing | fetched by id (`GET discover/candidates`, at most 200 ids) |
| Testoversikt counts | number of loaded scenarios | whole population (`TotalCandidates`, `TotalNeedsClarification`) |

**Honest limit:** some filters need state only the page holds: work queue, preparation status, scenario type (reviewer decisions), implementation status, test approach, automated tests, test plan status, and hiding technical suggestions. Those are NOT applied to matches among the scenarios that are not loaded, and the page names them («Ikke brukt på disse treffene: …»).

**Tests:**
- Backend `DiscoveryCapTests`: 8, covering the window of 2,000, every filter, filter-only search, facets, the clarification count and `Pick` by id or source identity.
- Frontend: 4 new tests in `DiscoverScopeAndActionTests`:
  - a filter alone sends the filter;
  - no words and no filter gives no request;
  - an open scenario that is not loaded is fetched by id;
  - an unloaded user story is offered and searched.

## 2. «Scenarioet har ingen kilde å lagre klargjøringen i»: investigated and fixed

**Cause.** `ScenarioPreparationEngine.DocumentKey` decides which grouping document holds a scenario's readiness decisions. It found one only through:
- a user story's specification path;
- a `testspec:` key;
- an evidence path containing «/specs/».

Every other scenario got **no document**, so its readiness could not be prepared at all. Measured in the browser on M2LB (AutorisasjonService), **458 of the 566 scenarios** had no document:
- API contract: 363
- UI: 58
- integration journeys: 19
- system guide: 18

The same applied to an acceptance scenario without a user story when its specification was outside a «/specs/» folder.

**Fix.** The existing rules come first and are unchanged, so no stored decision moves. Then:
1. the scenario's own specification evidence;
2. a per-source document of the project (`scenario-preparation/<source>`). The server already accepted any document path.

**Verified:**
- Backend test (root-level `specs/`, a pack's `docs/spec.md`, an API scenario, an unknown file, legacy keys unchanged).
- **Browser:** an API-contract scenario (DELETE /scim/v2/Users/{id}) shows its preparation with no error. A decision was saved and read back after a reload.

## 3. Three columns in Finn og forstå

- From a page width of **84 rem (1,344 px)**:
  - user stories (15 rem), scenarios, and the open scenario (22–30 rem) stand side by side;
  - the detail is a sticky column that follows a row click;
  - there is no overlay and nothing to close.
- Narrower: the existing drawer, opened with «Vis detaljer».
- The page measures its own width with a `ResizeObserver` (`js/pageFocus.js birknextWatchWidth`), so the sidebar is taken into account.
- With the sidebar open, three columns therefore appear on a 1920 px screen (page 1,536 px), but not at 1600 or 1440 (page 1,280 and 1,120 px).
- Nothing inside the detail changed.

## 4. Defect found by the regression and fixed: a save could be lost

- **What happened:** in Planlegg, «Lagre» waited for the automatic translation still running. That took about **11 s** on M2LB, with only «Oversetter …» shown. Leaving the page in that time cancelled both requests, and the change was lost without a message. It was found when journey A's step was not saved.
- **Now:**
  - the save is sent at once;
  - the translation is then refused on the old revision and retries on the new one;
  - if the translation was saved first, the page waits for it and sends the same change once more on its revision, keeping the translations.
- **Tests:**
  - A06 was rewritten: the save is sent at once, and the translation retries.
  - A06b is new: the translation wins, and the change is resent with the translations kept.
  - Both were checked against the old code by reasoning only; they were not run against it.
- **Browser:** the step is read back (A3, below).

## 5. Browser regression (isolated instance, disposable database, no Azure DevOps)

The final run on v1env32 included every fix up to `add3ea3e`. It used the scripts `scratchpad/v1/v1_v10_regression.py` and `v1_over10k_v2.py`.

### M2LB: 62/62
| Journey | Result |
|---|---|
| A — search → detail → «Forbered testtilfelle» → plan → case → step → Passed → Testoversikt | PASS; A3: the saved step read back in «Original» view |
| B — scenario only, no case, Blocked | PASS (first browser run after the per-selection currency fix) |
| C — copy starts NotRun; the original keeps Passed | PASS |
| D — B never shows A; module change clears the specification; the explicit choice is restored | PASS |
| E — «Viser 40 av 566», «Vis flere» keeps the selection, search beyond the batch, filter chip, 4 viewports without overflow | PASS |
| F — three columns at 1920; layout matches the page width at 1600/1440/1024/390; drawer opens and Escape closes it at 1024; no overflow | PASS (16 checks) |
| G — readiness of a technical scenario saved and read back | PASS |
| H — Testoversikt counts not capped at the window | PASS |
| Console | no errors |

### Synthetic 12,000 scenarios: 8/8
| Check | Result |
|---|---|
| Window | «10001 scenarioer utover de første 2000 vises ikke i listen …» |
| First load | 3,169,260 bytes (was 15.7 MB). 29.5 s until the list appears, which includes computing the discovery for the newly chosen specification (not measured separately). |
| Search «post 11999» | found among the scenarios not loaded |
| User story US120 (no loaded scenario) | offered; choosing it alone finds its **100** scenarios |
| Planlegg with a scenario that is not loaded | resolved («Valgt i Finn og forstå»), not «finnes ikke» |
| Testoversikt | Scenarioforslag = 12 001 |

The earlier run on v1env31 found defect 4 (a step check that failed: 60/61). The follow-up proved preparation for a scenario from a non-specification source (P checks 6/6).

Screenshots are in `scratchpad/v1/v1env32/shots/`: `v9-finn-three-1920.png`, `v10-finn-*.png`, `v10-over10k-*.png` (and `v1env31/shots/v10-preparation-no-spec.png`).

## 6. Automated tests
- **Frontend:**
  - `DiscoverScopeAndActionTests`, the Discover classes and the Planlegg classes pass (Discover 442, Planlegg 124).
  - The full suite was run at the end of this pass; see below.
- **Backend:**
  - discovery and planning classes: 291;
  - preparation, overview and execution: 289;
  - complete partitioned run: see the stability report.

## 7. Still open
- Matches among the scenarios that are not loaded are not filtered by work queue, status, plan status, automated tests or reviewer type decisions (said on the page).
- Group and story counts in the rail cover the loaded scenarios; unloaded stories are listed without counts.
- Three columns need a page of at least 1,344 px: a 1920 px screen with the sidebar open. At 1600 px they need a collapsed sidebar.
- Discovering a new specification of 12,000 scenarios takes about 30 s (computation, not transfer).
